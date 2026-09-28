#!/usr/bin/env bash
# The release harness's `pager` phase: the pager suite against the published build.
#
# Sourced by run.sh. It runs LAST before the report because it reconfigures the installed agent:
# a helm upgrade layers values-pager.yaml over what deploy_install set, which swaps the real
# model for the stand-in and every window for seconds. Everything the phases before it proved
# about the real model has been proved by then; everything the pager suite proves is about who
# is told, which does not need one.
#
# The one scenario only this harness and the dev cluster can run is P16: a Prometheus evaluates
# the chart's agent-presence rules, and the observability stack's Alertmanager routes
# hephaisto_route=external away from the agent.

PF_PORT_STANDIN=18110

pagerphase_run() {
    if ! docker image inspect hephaisto/notification-receiver:dev >/dev/null 2>&1; then
        docker build -q -f "$REPO/infra/e2e/notification-receiver/Dockerfile" \
            -t hephaisto/notification-receiver:dev "$REPO" >/dev/null \
            || { fail "pager suite" "the stand-in image did not build"; return; }
    fi
    kind load docker-image hephaisto/notification-receiver:dev --name "$E2E_CLUSTER" >/dev/null 2>&1 \
        || { fail "pager suite" "the stand-in image could not be loaded into kind"; return; }

    kc apply -f "$REPO/infra/e2e/teams-stand-in.yaml" >/dev/null
    kc -n "$OBS_NS" rollout restart deploy/teams-stand-in >/dev/null
    kc -n "$OBS_NS" rollout status deploy/teams-stand-in --timeout=120s >/dev/null \
        || { fail "pager suite" "the stand-in did not start"; return; }

    say "reconfiguring the agent for the pager suite (values-pager.yaml over the install's values)"
    helm_e2e upgrade hephaisto "$CHART_REPO/hephaisto" \
        --version "$VERSION" \
        --namespace "$APP_NS" \
        --reuse-values \
        --values "$E2E_DIR/values-pager.yaml" \
        --wait --timeout 8m >/dev/null \
        || { fail "pager suite" "helm upgrade with values-pager.yaml failed"; return; }

    port_forward standin "$OBS_NS" svc/teams-stand-in "$PF_PORT_STANDIN" 8080 || true
    port_forward hephaisto "$APP_NS" svc/hephaisto "$PF_PORT_APP" 8080 || true

    local status=0
    PAGER_API="http://127.0.0.1:$PF_PORT_APP" \
    PAGER_HOOK="http://127.0.0.1:$PF_PORT_APP" \
    PAGER_STANDIN="http://127.0.0.1:$PF_PORT_STANDIN" \
    PAGER_AM="http://127.0.0.1:$PF_PORT_ALERT" \
    PAGER_CAPS="am kubectl prometheus spare-agent" \
    E2E_KUBECONFIG="$E2E_KUBECONFIG" E2E_CONTEXT="$E2E_CONTEXT" \
    RESULTS="$RESULTS" \
        "$E2E_DIR/pager.sh" --results "$WORKDIR/pager" || status=$?

    # pager.sh wrote its verdicts into the same results file; this line is the phase's own.
    if [ "$status" -eq 0 ]; then
        pass "pager suite"
    else
        fail "pager suite" "$status scenario(s) failed - $WORKDIR/pager/scenarios/"
    fi
}
