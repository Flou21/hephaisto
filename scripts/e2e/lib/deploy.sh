#!/usr/bin/env bash
# Phase 5: install the published chart and assert what the published artifact is.

# The number of entries already in values-e2e.yaml's extraEnv list, so provider settings can
# be appended rather than written at a hardcoded index. Helm's --set replaces by position, so
# a fixed index silently overwrites a budget cap the first time somebody adds an entry above
# it - and a silently-removed cost cap is the failure this harness least wants.
deploy_extra_env_count() {
    awk '
        /^extraEnv:/            { inblock = 1; next }
        inblock && /^[^[:space:]#]/ { inblock = 0 }
        inblock && /^[[:space:]]+- name:/ { n++ }
        END                     { print n + 0 }
    ' "$E2E_DIR/values-e2e.yaml"
}

deploy_install() {
    say "installing hephaisto $VERSION from $CHART_REPO"

    local extra=()

    # One counter for every appended entry, advanced by env_add and never recomputed. Helm's
    # --set replaces by position, so an index derived twice - or inferred from array length -
    # is how a budget cap gets silently overwritten by the next setting somebody adds.
    local env_i
    env_i=$(deploy_extra_env_count)

    env_add() {
        extra+=(--set "extraEnv[$env_i].name=$1" --set "extraEnv[$env_i].value=$2")
        env_i=$((env_i + 1))
    }

    # An OpenAI-compatible provider is three settings, none of which the chart exposes as a
    # first-class value - which is what extraEnv is for. Gemini needs none of them, so the
    # default path is unchanged.
    if [ "${HEPHAISTO_LLM_PROVIDER:-gemini}" = "openai" ]; then
        env_add Llm__Provider openai
        env_add Llm__Endpoint "${HEPHAISTO_LLM_ENDPOINT}"
        env_add Llm__Model "${HEPHAISTO_LLM_MODEL:?HEPHAISTO_LLM_MODEL is required when HEPHAISTO_LLM_PROVIDER=openai}"

        # Only when asked for. It is the weaker mode - the schema becomes a request rather
        # than a constraint - so it should never be switched on by inferring a provider's
        # capability from its name.
        if [ -n "${HEPHAISTO_LLM_PLANNING_FORMAT:-}" ]; then
            env_add Llm__PlanningStructuredOutput "${HEPHAISTO_LLM_PLANNING_FORMAT}"
        fi

        say "provider: openai, model ${HEPHAISTO_LLM_MODEL} at ${HEPHAISTO_LLM_ENDPOINT}"
    fi

    # The step ceiling, which is a per-model number wearing a global default's clothes.
    #
    # backlog #59 measured it: gpt-oss-120b scores 17/30 at MaxSteps=12 and 18/20 at 20, and
    # ten of those thirty runs terminated StepBudgetExhausted having produced NO finding. A
    # ceiling tuned for one model does not read as a ceiling when another model hits it - it
    # reads as that model being worse. So an openai-compatible provider gets 20 by default
    # and the hosted Gemini path keeps the shipped 12, with an explicit override either way.
    local steps="${HEPHAISTO_LLM_MAX_STEPS:-}"
    if [ -z "$steps" ] && [ "${HEPHAISTO_LLM_PROVIDER:-gemini}" = "openai" ]; then
        steps=20
    fi
    if [ -n "$steps" ]; then
        env_add Llm__Investigation__MaxSteps "$steps"
        say "investigation step ceiling: $steps"
    fi

    helm_e2e upgrade --install hephaisto "$CHART_REPO/hephaisto" \
        --version "$VERSION" \
        --namespace "$APP_NS" --create-namespace \
        --values "$E2E_DIR/values-e2e.yaml" \
        --set "mode=${E2E_MODE:-Observe}" \
        --set "policy.autoEnabledActionTypes={RestartPod}" \
        "${extra[@]+"${extra[@]}"}" \
        --wait --timeout 8m \
        || { fail "hephaisto installed" "helm install failed; see kubectl describe"; return 1; }

    pass "chart $VERSION installed from the registry"
}

deploy_assert() {
    local pod
    pod=$(kc -n "$APP_NS" get pod -l app.kubernetes.io/name=hephaisto -o name | head -1)
    [ -n "$pod" ] || { fail "agent pod exists" "no pod matched the name label"; return 1; }

    # Snapshot the log while the pod is FRESH and the log is short.
    #
    # Anything that only appears at startup - what this process can and cannot send outward,
    # which arms the kill switch resolved to - cannot be reliably grepped out of `kubectl logs`
    # later. The kubelet rotates a container log at 10Mi by default and `kubectl logs` reads the
    # current file, and a pod replaced for any reason takes its predecessor's log with it. An
    # agent that has run five investigations emits lines of up to ~11 KB, so the startup lines
    # are the first thing to go.
    #
    # That is not hypothetical: the notify phase asserted on those lines and failed across a
    # 1674-line log while the agent had emitted them perfectly - the product was right and the
    # assertion was reading a window they were no longer in.
    kc -n "$APP_NS" logs "$pod" --tail=-1 > "$WORKDIR/agent-startup.log" 2>/dev/null || true

    # --- RESTARTS 0 -----------------------------------------------------------------------
    # Every fresh install used to show RESTARTS: 1 - the agent starts, awaits the EF migration,
    # Postgres is not accepting connections yet, the process exits, Kubernetes restarts it and
    # the second attempt works. A wait-for-postgres initContainer was added to stop that, and
    # this is the assertion that keeps it fixed. A tolerated restart on every install is how a
    # real crash loop goes unnoticed.
    local restarts
    restarts=$(kc -n "$APP_NS" get "$pod" -o jsonpath='{.status.containerStatuses[0].restartCount}')
    [ "${restarts:-1}" -eq 0 ] \
        && pass "the agent started without a restart" \
        || fail "agent restarted $restarts time(s) on first install" \
                "the wait-for-postgres initContainer is not doing its job"

    local init_state
    init_state=$(kc -n "$APP_NS" get "$pod" \
        -o jsonpath='{.status.initContainerStatuses[0].state.terminated.reason}' 2>/dev/null || true)
    [ "$init_state" = "Completed" ] \
        && pass "wait-for-postgres initContainer completed" \
        || skip "wait-for-postgres initContainer" "state '${init_state:-<none>}'"

    # --- Security posture -----------------------------------------------------------------
    # readOnlyRootFilesystem is exercised almost nowhere else: the dev cluster runs the dev
    # image, which is `dotnet watch` and needs to write. This and CI are the only places the
    # production image ever runs under it.
    local ro uid
    ro=$(kc -n "$APP_NS" get "$pod" -o jsonpath='{.spec.containers[0].securityContext.readOnlyRootFilesystem}')
    uid=$(kc -n "$APP_NS" get "$pod" -o jsonpath='{.spec.securityContext.runAsUser}')
    [ "$ro" = "true" ]  && pass "runs with a read-only root filesystem" || fail "readOnlyRootFilesystem is '$ro'"
    [ "$uid" = "64198" ] && pass "runs as uid 64198, not root"          || fail "runAsUser is '$uid'"

    # --- The version chain ------------------------------------------------------------------
    # git tag -> MinVer -> MSBuild -> image -> chart appVersion -> /api/version. Every link has
    # a way of quietly reporting something else, and an image tagged with a version the
    # assembly inside it does not report is the failure that wastes a whole afternoon.
    local reported chart_app commit
    reported=$(api /api/version | jq -r '.version // "unknown"')
    chart_app=$(helm_e2e -n "$APP_NS" get metadata hephaisto -o json | jq -r .appVersion)
    commit=$(api /api/version | jq -r '.commit // ""')

    [ "$reported" = "$VERSION" ] \
        && pass "/api/version reports $VERSION" \
        || fail "/api/version reports '$reported'" "expected $VERSION"

    [ "$chart_app" = "$VERSION" ] \
        && pass "chart appVersion is $VERSION" \
        || fail "chart appVersion is '$chart_app'" "expected $VERSION"

    # The commit is what makes the chain falsifiable rather than self-consistent: three
    # matching version strings prove nothing if they all came from the same wrong build.
    if [ -n "$commit" ]; then
        say "built from commit ${commit:0:12}"
        record pass "$CURRENT_PHASE" "the running build names its commit" "${commit:0:12}"
    fi

    # --- The image really is the published one ---------------------------------------------
    local running
    running=$(kc -n "$APP_NS" get "$pod" -o jsonpath='{.spec.containers[0].image}')
    [ "$running" = "$IMAGE_REPO:$VERSION" ] \
        && pass "running the published image" \
        || fail "running '$running'" "expected $IMAGE_REPO:$VERSION"
}

# ---------------------------------------------------------------------------------------
# RBAC
# ---------------------------------------------------------------------------------------
# The most important prose in the repo turned into assertions. Only a live API server can
# answer these, which is why they cannot live in the unit suite.
deploy_assert_rbac() {
    local sa="system:serviceaccount:$APP_NS:hephaisto"

    must_not() {
        local what="$1"; shift
        if kc auth can-i "$@" --as="$sa" >/dev/null 2>&1; then
            fail "the agent cannot $what" "it CAN, and must not"
        else
            pass "the agent cannot $what"
        fi
    }
    must() {
        local what="$1"; shift
        if kc auth can-i "$@" --as="$sa" >/dev/null 2>&1; then
            pass "the agent can $what"
        else
            fail "the agent cannot $what" "but it needs to"
        fi
    }

    must_not "read secrets anywhere"        get secrets -A
    must_not "delete secrets anywhere"      delete secrets -A
    must_not "delete pods in kube-system"   delete pods -n kube-system
    must_not "create clusterrolebindings"   create clusterrolebindings
    must_not "delete pods in its own namespace" delete pods -n "$APP_NS"
    must_not "delete pods in the observability namespace" delete pods -n "$OBS_NS"
    must_not "exec into pods"               create pods/exec -A
    must_not "edit its own RBAC"            update clusterroles

    must "delete pods in the chaos namespace" delete pods -n "$CHAOS_NS"
    must "read pods cluster-wide"             get pods -A
    must "read events cluster-wide"           get events -A
    must "read nodes"                         get nodes
}

# ---------------------------------------------------------------------------------------
# The operator actually selected the chart's objects
# ---------------------------------------------------------------------------------------
# CI cannot check this: e2e-kind installs no Prometheus, so the `release:` selector is only
# covered there by a render-time grep. A rule that is created and never selected is the
# chart's single most dangerous failure, because everything looks right.
deploy_assert_selected() {
    # Waited for, not sampled once. The operator has to notice the PrometheusRule objects,
    # write a new config and signal Prometheus to reload, and none of that is synchronous with
    # `helm install` returning. A single query right after the install is a race: it won on
    # rc3 and rc4 and lost on rc5, reporting "selected none of the chart's rule groups" about
    # a Prometheus that had 12 groups and 34 rules loaded a minute later.
    #
    # A flaky assertion on the failure this one exists to catch is worse than no assertion,
    # because it teaches you to disbelieve it.
    wait_for "prometheus to select the chart's rule groups" "${RULE_TIMEOUT:-180}" \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_PROM/api/v1/rules' | jq -e '[.data.groups[] | select(.name | test(\"hephaisto|kubernetes|slo|watchdog\"))] | length > 0' >/dev/null" \
        || true

    local groups
    groups=$(curl -sS --max-time 15 "http://127.0.0.1:$PF_PORT_PROM/api/v1/rules" \
             | jq -r '[.data.groups[] | select(.name | test("hephaisto|kubernetes|slo|watchdog"))] | length')

    [ "${groups:-0}" -gt 0 ] \
        && pass "prometheus selected $groups of the chart's rule groups" \
        || fail "prometheus selected none of the chart's rule groups" \
                "prometheusOperator.selectorLabels.release does not match the kps release name"

    local rules
    rules=$(curl -sS --max-time 15 "http://127.0.0.1:$PF_PORT_PROM/api/v1/rules" \
            | jq -r '[.data.groups[].rules[]] | length')
    say "prometheus has $rules rules loaded in total"

    # The PodMonitor, proven by the agent's own metrics arriving rather than by the object
    # existing.
    wait_for "the agent's metrics to be scraped" 180 \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=hephaisto_build_info' | jq -e '.data.result | length > 0' >/dev/null" \
        && pass "the PodMonitor is scraping the agent" \
        || fail "hephaisto_build_info never appeared in Prometheus" "the PodMonitor was not selected"

    # The gauge this work added. Before it existed the two budget alert rules could never
    # fire and both dashboard panels were permanently empty.
    if curl -sS --max-time 10 \
        "http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=hephaisto_llm_budget_utilization" \
        | jq -e '.data.result | length > 0' >/dev/null 2>&1; then
        local scopes
        scopes=$(curl -sS --max-time 10 \
                 "http://127.0.0.1:$PF_PORT_PROM/api/v1/query?query=hephaisto_llm_budget_utilization" \
                 | jq -r '[.data.result[].metric.scope] | sort | join(",")')
        pass "hephaisto_llm_budget_utilization is emitted (scopes: $scopes)"
    else
        fail "hephaisto_llm_budget_utilization is not in Prometheus" \
             "the budget alert rules cannot fire and the panels stay empty"
    fi
}

# ---------------------------------------------------------------------------------------
# The alert path
# ---------------------------------------------------------------------------------------
deploy_assert_watchdog() {
    # AgentWatchdog fires permanently by design (expr: vector(1)). It is the agent's proof
    # that Prometheus -> Alertmanager -> its own webhook is alive, so it is the first thing
    # that must work and the last thing that may be gated.
    wait_for "the watchdog to reach the agent" 300 \
        bash -c "curl -sS --max-time 10 'http://127.0.0.1:$PF_PORT_APP/api/status' | jq -e '.watchdogReceipts > 0' >/dev/null" \
        && pass "the alert path is alive (watchdog received)" \
        || fail "no watchdog receipt within 5 minutes" \
                "Prometheus -> Alertmanager -> webhook is broken; nothing else here will work"

    local stale receipts
    stale=$(api /api/status | jq -r '.watchdogStale')
    receipts=$(api /api/status | jq -r '.watchdogReceipts')
    [ "$stale" = "false" ] \
        && pass "watchdogStale is false ($receipts receipts)" \
        || fail "watchdogStale is $stale" "the agent believes it has gone blind"
}
