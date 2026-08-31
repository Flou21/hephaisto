#!/usr/bin/env bash
# Phase 4: the observability stack the agent needs in order to see anything.
#
# Every chart version and every values file here is the one the Tiltfile uses on the dev
# cluster, deliberately and without a kind-flavoured variant. The values files are the thing
# under test as much as the chart is: a `release:` selector or an OTLP endpoint that is wrong
# in them fails silently and identically in both places, and a copy edited to suit kind would
# test the copy.
#
# The only overrides are passed as --set, and there are two. Both are genuinely
# cluster-specific rather than convenience.

deps_add_repos() {
    say "adding helm repos"
    helm repo add prometheus-community https://prometheus-community.github.io/helm-charts >/dev/null 2>&1 || true
    helm repo add grafana-charts       https://grafana.github.io/helm-charts             >/dev/null 2>&1 || true
    # A distinct repo, not a mirror: tempo and grafana-mcp moved here, and the versions the
    # Tiltfile pins do not exist under grafana/.
    helm repo add grafana-community    https://grafana-community.github.io/helm-charts   >/dev/null 2>&1 || true
    helm repo add open-telemetry       https://open-telemetry.github.io/opentelemetry-helm-charts >/dev/null 2>&1 || true
    helm repo update >/dev/null 2>&1
}

deps_install() {
    local obs="$REPO/infra/observability"

    deps_add_repos

    # --- kube-prometheus-stack -----------------------------------------------------------
    # Release name `hephaisto`, matching the Tiltfile - and therefore matching
    # prometheusOperator.selectorLabels.release in values-e2e.yaml. Get those two out of step
    # and every PodMonitor and PrometheusRule is created successfully and selected by
    # nothing: no metrics, no alerts, no incidents, and an agent reporting itself healthy.
    #
    # crds.enabled=true is the first override. The values file sets it false because the dev
    # cluster's CRDs are managed separately; a fresh kind cluster has none at all.
    #
    # externalLabels.cluster is the second. The values file stamps every sample with
    # `cluster: studio-rancher-desktop`, which would be a lie on this one and would make any
    # dashboard opened against a real Prometheus mix the two.
    say "installing kube-prometheus-stack 81.1.0 (this is the slow one)"
    helm_e2e upgrade --install hephaisto prometheus-community/kube-prometheus-stack \
        --version 81.1.0 \
        --namespace "$OBS_NS" --create-namespace \
        --values "$obs/kube-prometheus-stack.values.yaml" \
        --set crds.enabled=true \
        --set prometheus.prometheusSpec.externalLabels.cluster=kind-e2e \
        --wait --timeout 10m \
        || { fail "kube-prometheus-stack installed" "helm install failed"; return 1; }
    pass "kube-prometheus-stack 81.1.0"

    # --- Loki ----------------------------------------------------------------------------
    # The chaos fixtures' LogQL selectors depend on this file's otlp_config.resource_attributes
    # block, which is what turns k8s.namespace.name and friends into queryable labels.
    say "installing loki 6.40.0"
    helm_e2e upgrade --install loki grafana-charts/loki \
        --version 6.40.0 \
        --namespace "$OBS_NS" \
        --values "$obs/loki.values.yaml" \
        --wait --timeout 10m \
        || { fail "loki installed" "helm install failed"; return 1; }
    pass "loki 6.40.0"

    # --- Tempo ---------------------------------------------------------------------------
    say "installing tempo 2.3.0"
    helm_e2e upgrade --install tempo grafana-community/tempo \
        --version 2.3.0 \
        --namespace "$OBS_NS" \
        --values "$obs/tempo.values.yaml" \
        --set "metricsGenerator.remoteWriteUrl=http://hephaisto-kube-prometheus-prometheus.$OBS_NS:9090/api/v1/write" \
        --wait --timeout 10m \
        || warn "tempo install failed; traces and span metrics will be missing"
    pass "tempo 2.3.0"

    # --- Grafana datasources -------------------------------------------------------------
    # NOT optional, and the failure mode is the quiet one. The stack values set
    # sidecar.datasources.defaultDatasourceEnabled: false so the chart does not provision its
    # own Prometheus datasource and fight this ConfigMap over the `prometheus` uid. Leave it
    # unapplied and Grafana does not fall back to a default - it has ZERO datasources, an
    # empty Explore, and every panel reads "Datasource not found". Nothing logs an error,
    # because from Grafana's point of view it was never told about any.
    say "applying the Grafana datasource ConfigMap"
    kc apply -f "$obs/grafana-datasources.yaml" >/dev/null
    pass "grafana datasources applied"

    # --- OTel collector ------------------------------------------------------------------
    # After its exporters, so its first minutes are not spent logging connection refused.
    # The k8s_events receiver in here is what makes C3 and C7 diagnosable at all: both carry
    # their cause in an Event and in no metric anywhere.
    say "installing opentelemetry-collector 0.171.0"
    helm_e2e upgrade --install otel-collector open-telemetry/opentelemetry-collector \
        --version 0.171.0 \
        --namespace "$OBS_NS" \
        --values "$obs/otel-collector.values.yaml" \
        --wait --timeout 10m \
        || { fail "otel-collector installed" "helm install failed"; return 1; }
    pass "opentelemetry-collector 0.171.0"

    # --- grafana-mcp ---------------------------------------------------------------------
    # Without it the agent degrades to Kubernetes reads only: it still detects and still
    # investigates, but it cannot run a PromQL or LogQL query, which is most of what a
    # grounded diagnosis is made of. Installed before its token exists; bootstrap-secrets.sh
    # mints that and restarts it.
    say "installing grafana-mcp 0.19.0"
    helm_e2e upgrade --install grafana-mcp grafana-community/grafana-mcp \
        --version 0.19.0 \
        --namespace "$OBS_NS" \
        --values "$obs/grafana-mcp.values.yaml" \
        --timeout 5m \
        || warn "grafana-mcp install failed; the agent will run without query tools"
    pass "grafana-mcp 0.19.0"
}

# ---------------------------------------------------------------------------------------
# Secrets
# ---------------------------------------------------------------------------------------
deps_secrets() {
    # bootstrap-secrets.sh does all four, idempotently, and mints the Grafana service-account
    # token from inside the Grafana pod so the admin password never crosses the network. Its
    # context guard is honoured rather than bypassed: CONTEXT_REQUIRED is overridden to this
    # cluster, so the guard still refuses everything else.
    say "bootstrapping secrets"

    if [ -z "${HEPHAISTO_GEMINI_API_KEY:-}" ]; then
        # The repo keeps one locally for exactly this purpose, gitignored.
        local secret_file="$REPO/secrets/hephaisto-llm.secret.yaml"
        if [ -f "$secret_file" ]; then
            local from_file
            from_file=$(grep -oE 'GEMINI_API_KEY:[[:space:]]*.*' "$secret_file" 2>/dev/null \
                        | head -1 | sed 's/GEMINI_API_KEY:[[:space:]]*//' | tr -d '"'"'" )
            # A base64 `data:` value rather than a plaintext `stringData:` one.
            if grep -q '^data:' "$secret_file" 2>/dev/null && [ -n "$from_file" ]; then
                from_file=$(printf '%s' "$from_file" | base64 -d 2>/dev/null || true)
            fi
            # An exact `!= "REPLACE_ME"` test is not enough: the committed placeholder is
            # `REPLACE_ME_WITH_A_REAL_KEY`-shaped, 35 characters long, and would sail past it
            # to become an API key that fails every single call with a 400. Prefix match.
            case "$from_file" in
                REPLACE*|CHANGE*|YOUR_*|"") from_file="" ;;
            esac
            if [ -n "$from_file" ]; then
                export HEPHAISTO_GEMINI_API_KEY="$from_file"
                say "using the Gemini key from secrets/hephaisto-llm.secret.yaml"
            fi
        fi
    fi

    # ---------------------------------------------------------------------------------
    # An OpenAI-compatible provider instead of Gemini, selected by HEPHAISTO_LLM_PROVIDER.
    # ---------------------------------------------------------------------------------
    # Same contract as the Gemini arm below: resolve a key, prove it works with one cheap
    # call, and degrade to a detection-only run rather than failing, because a run that
    # exercises detection is worth more than no run at all.
    if [ "${HEPHAISTO_LLM_PROVIDER:-gemini}" = "openai" ]; then
        if [ -z "${HEPHAISTO_LLM_API_KEY:-}" ]; then
            local llm_secret="$REPO/secrets/hephaisto-llm.secret.yaml"
            if [ -f "$llm_secret" ]; then
                local from_file
                from_file=$(grep -oE 'LLM_API_KEY:[[:space:]]*.*' "$llm_secret" 2>/dev/null \
                            | head -1 | sed 's/LLM_API_KEY:[[:space:]]*//' | tr -d '"'"'" )
                if grep -q '^data:' "$llm_secret" 2>/dev/null && [ -n "$from_file" ]; then
                    from_file=$(printf '%s' "$from_file" | base64 -d 2>/dev/null || true)
                fi
                case "$from_file" in
                    REPLACE*|CHANGE*|YOUR_*|"") from_file="" ;;
                esac
                if [ -n "$from_file" ]; then
                    export HEPHAISTO_LLM_API_KEY="$from_file"
                    say "using the LLM key from secrets/hephaisto-llm.secret.yaml"
                fi
            fi
        fi

        local endpoint="${HEPHAISTO_LLM_ENDPOINT:?HEPHAISTO_LLM_ENDPOINT is required when HEPHAISTO_LLM_PROVIDER=openai}"

        if [ -n "${HEPHAISTO_LLM_API_KEY:-}" ]; then
            # /v1/models rather than a completion: every OpenAI-compatible server implements
            # it, it costs nothing, and a 401 here is the same 401 that would otherwise
            # appear on the fourth investigation of a twelve-minute run.
            local probe
            probe=$(curl -sS --max-time 30 "${endpoint%/}/models" \
                -H "Authorization: Bearer ${HEPHAISTO_LLM_API_KEY}" \
                -o /dev/null -w '%{http_code}' 2>/dev/null || echo "000")

            if [ "$probe" = "200" ]; then
                say "LLM key accepted by $endpoint"
                LLM_AVAILABLE=1
            else
                warn "the LLM key was rejected by $endpoint (HTTP $probe)"
                warn "continuing without investigations; detection is still exercised"
                unset HEPHAISTO_LLM_API_KEY
                LLM_AVAILABLE=0
            fi
        else
            warn "no HEPHAISTO_LLM_API_KEY - investigations will be skipped, detection still tested"
            LLM_AVAILABLE=0
        fi

        KUBECONFIG="$E2E_KUBECONFIG" CONTEXT_REQUIRED="$E2E_CONTEXT" \
            "$REPO/scripts/bootstrap-secrets.sh" 2>&1 | sed 's/^/    /' \
            || { fail "secrets bootstrapped" "bootstrap-secrets.sh failed"; return 1; }

        for s in hephaisto-postgres grafana-mcp-caller-token; do
            kc -n "$APP_NS" get secret "$s" >/dev/null 2>&1 \
                && pass "secret $s exists" \
                || fail "secret $s missing" "the agent pod will not start"
        done

        if [ "$LLM_AVAILABLE" = "1" ]; then
            kc -n "$APP_NS" get secret hephaisto-llm >/dev/null 2>&1 \
                && pass "secret hephaisto-llm exists" \
                || fail "secret hephaisto-llm missing" "investigations cannot run"
        else
            # Both chart keys are optional now, so an empty Secret is enough to start the pod.
            kc -n "$APP_NS" create secret generic hephaisto-llm >/dev/null 2>&1 || true
            skip "secret hephaisto-llm" "placeholder only, no key available"
        fi

        return 0
    fi

    # Validate the key NOW rather than discovering it is wrong fifteen minutes in, when four
    # investigations have failed and the failure looks like a bug in the agent. One cheap call.
    if [ -n "${HEPHAISTO_GEMINI_API_KEY:-}" ]; then
        local probe
        probe=$(curl -sS --max-time 30 \
            "https://generativelanguage.googleapis.com/v1beta/models/${JUDGE_MODEL:-gemini-3.7-flash}:generateContent" \
            -H "x-goog-api-key: ${HEPHAISTO_GEMINI_API_KEY}" \
            -H 'Content-Type: application/json' \
            -d '{"contents":[{"role":"user","parts":[{"text":"ok"}]}],"generationConfig":{"maxOutputTokens":1}}' \
            2>/dev/null | jq -r '.error.message // "ok"' 2>/dev/null || echo "unreachable")

        if [ "$probe" != "ok" ]; then
            warn "the Gemini key was rejected: $probe"
            warn "continuing without investigations; detection is still exercised"
            unset HEPHAISTO_GEMINI_API_KEY
        else
            say "Gemini key accepted"
        fi
    fi

    if [ -z "${HEPHAISTO_GEMINI_API_KEY:-}" ]; then
        # Not fatal. The agent still detects, dedups, correlates and serves the UI without a
        # model; it just cannot investigate. That is a legible degraded run, and saying so
        # here is better than failing twelve assertions later for an unobvious reason.
        warn "no HEPHAISTO_GEMINI_API_KEY - investigations will be skipped, detection still tested"
        LLM_AVAILABLE=0
    else
        LLM_AVAILABLE=1
    fi

    KUBECONFIG="$E2E_KUBECONFIG" CONTEXT_REQUIRED="$E2E_CONTEXT" \
        "$REPO/scripts/bootstrap-secrets.sh" 2>&1 | sed 's/^/    /' \
        || { fail "secrets bootstrapped" "bootstrap-secrets.sh failed"; return 1; }

    for s in hephaisto-postgres grafana-mcp-caller-token; do
        kc -n "$APP_NS" get secret "$s" >/dev/null 2>&1 \
            && pass "secret $s exists" \
            || fail "secret $s missing" "the agent pod will not start"
    done

    if [ "$LLM_AVAILABLE" = "1" ]; then
        kc -n "$APP_NS" get secret hephaisto-llm >/dev/null 2>&1 \
            && pass "secret hephaisto-llm exists" \
            || fail "secret hephaisto-llm missing" "investigations cannot run"
    else
        # The chart's secretKeyRef is not optional, so the pod would sit in
        # CreateContainerConfigError without something here.
        kc -n "$APP_NS" create secret generic hephaisto-llm \
            --from-literal=GEMINI_API_KEY=not-a-real-key >/dev/null 2>&1 || true
        skip "secret hephaisto-llm" "placeholder only, no key available"
    fi
}

deps_verify() {
    # A handful of checks that the stack is not merely installed but working. Each one has
    # burned somebody: a Prometheus without the remote-write receiver silently drops Tempo's
    # span metrics, and Grafana with zero datasources renders every panel as an error.
    say "verifying the stack"

    check "prometheus is answering" \
        curl -sSf --max-time 10 "http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=up"

    local receiver
    receiver=$(curl -sS --max-time 10 "http://127.0.0.1:$PF_PORT_PROM/api/v1/status/flags" \
               | jq -r '.data["web.enable-remote-write-receiver"] // "unknown"')
    [ "$receiver" = "true" ] \
        && pass "prometheus has the remote-write receiver enabled" \
        || fail "prometheus remote-write receiver is '$receiver'" \
                "the values file used the pre-0.60 enableFeatures spelling, which does nothing"

    local targets
    targets=$(curl -sS --max-time 15 "http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=up" \
              | jq -r '[.data.result[] | select(.value[1] == "1")] | length')
    [ "${targets:-0}" -gt 3 ] \
        && pass "prometheus has $targets healthy targets" \
        || fail "prometheus has only ${targets:-0} healthy targets"

    local ksm
    ksm=$(curl -sS --max-time 15 \
          "http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=kube_pod_container_status_waiting_reason" \
          | jq -r '.data.result | length')
    [ "${ksm:-0}" -gt 0 ] \
        && pass "kube-state-metrics is producing workload state" \
        || fail "no kube_pod_container_status_waiting_reason series" \
                "almost every shipped alert rule is built on these"
}
