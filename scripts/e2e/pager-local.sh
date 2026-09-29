#!/usr/bin/env bash
#
# The pager suite against the LOCAL Tilt stack (port 10351) on studio-rancher-desktop.
#
#   scripts/e2e/pager-local.sh                 # every scenario
#   scripts/e2e/pager-local.sh --only P05,P09  # some
#   scripts/e2e/pager-local.sh --list          # what exists, and what is known red
#
# Prerequisite (tilt_config.json): "pager-e2e": true. That layers scripts/e2e/values-pager.yaml
# over the dev values - the model and Teams become the stand-in, and the windows the scenarios
# wait out become seconds instead of hours - and it overrides "local-llm" and "teams-bot".
# Switch it off again to get the dev agent back on the local model.
#
# This is the one place the suite has a Prometheus behind it, so it is the one place P16 (the
# agent's own absence, reported by a path the agent is not on) runs outside the release harness.
#
# SAFETY. This machine's default kube context is a production cluster. The runner never reads
# ~/.kube/config after its first line: it extracts ONE context into a private kubeconfig, checks
# that context's API server is this machine, and hands only that file to the scenarios that scale
# or delete something (P02, P13, P15, P16). The same pinning as codefix-local.sh.

set -Eeuo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

source "$E2E_DIR/lib/common.sh"

case "${1:-}" in
    -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
    --list) exec "$E2E_DIR/pager.sh" --list ;;
esac

PAGER_CONTEXT="${PAGER_CONTEXT:-studio-rancher-desktop}"
case "$PAGER_CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) die "refusing: $PAGER_CONTEXT is not a local dev context" ;;
esac

RUN_DIR="${PAGER_RUN_DIR:-$REPO/results/pager-local-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$RUN_DIR"

E2E_KUBECONFIG="$RUN_DIR/kubeconfig"
kubectl config view --minify --flatten --context "$PAGER_CONTEXT" > "$E2E_KUBECONFIG"
chmod 600 "$E2E_KUBECONFIG"
E2E_CONTEXT="$PAGER_CONTEXT"

server=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config view -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
    *macstudio-von-florian*|*127.0.0.1*|*localhost*) ;;
    *) die "refusing: $PAGER_CONTEXT points at $server, which is not this machine" ;;
esac
export E2E_KUBECONFIG E2E_CONTEXT


H="${PAGER_HOST:-$(jq -r '.host // "localhost"' "$REPO/tilt_config.json" 2>/dev/null || echo localhost)}"

export PAGER_API="${PAGER_API:-http://$H:8100}"
export PAGER_HOOK="${PAGER_HOOK:-$PAGER_API}"
export PAGER_STANDIN="${PAGER_STANDIN:-http://$H:8110}"
export PAGER_AM="${PAGER_AM:-http://$H:9093}"
export PAGER_NS="${PAGER_NS:-hephaisto}"
export PAGER_DEPLOY="${PAGER_DEPLOY:-hephaisto}"
export PAGER_CAPS="${PAGER_CAPS:-am kubectl prometheus mcp}"
# The MCP endpoint (#157) - Tilt forwards 8183 to the agent's 8083 - and the five tokens
# values-pager.yaml names, read from the Secret mcp-secrets.sh made (and makes, when missing).
export PAGER_MCP="${PAGER_MCP:-http://$H:8183/mcp}"

# The token the dev stack's Alertmanager sends, when the chart was given one.
secret=$(kc -n "$PAGER_NS" get deploy "$PAGER_DEPLOY" \
    -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="Web__WebhookToken")].valueFrom.secretKeyRef.name}' 2>/dev/null || true)
if [ -n "$secret" ]; then
    PAGER_TOKEN=$(kc -n "$PAGER_NS" get secret "$secret" -o jsonpath='{.data.token}' | base64 -d)
    export PAGER_TOKEN
fi

eval "$(KUBECONFIG="$E2E_KUBECONFIG" "$E2E_DIR/mcp-secrets.sh" --context "$PAGER_CONTEXT" --namespace "$PAGER_NS" --print)"
kc apply -f "$REPO/infra/e2e/pager-fixture.yaml" >/dev/null

# An agent that is not in pager mode investigates with a real model and waits out real windows:
# every scenario would fail slowly and for the wrong reason.
endpoint=$(kc -n "$PAGER_NS" get deploy "$PAGER_DEPLOY" \
    -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="Llm__Endpoint")].value}' 2>/dev/null || true)
case "$endpoint" in
    *model-stand-in*) ;;
    *) die "the dev agent is not in pager mode (Llm__Endpoint=${endpoint:-unset}): set \"pager-e2e\": true in tilt_config.json" ;;
esac

curl -sf --max-time 10 "$PAGER_API/healthz" >/dev/null || die "the agent at $PAGER_API is not healthy"
curl -sf --max-time 10 "$PAGER_STANDIN/healthz" >/dev/null || die "the stand-in at $PAGER_STANDIN is not answering"
# The stand-in's image is a fixed tag that Tilt rebuilds in place, and a rebuilt image does not
# restart the pod. One that predates the model stand-in answers 404 here.
curl -sf --max-time 10 "$PAGER_STANDIN/v1/models" >/dev/null \
    || die "the stand-in has no model: its pod predates LlmStandIn.cs - kubectl -n hephaisto-obs rollout restart deploy/teams-stand-in"

say "cluster $PAGER_CONTEXT ($server), results in $RUN_DIR"
exec "$E2E_DIR/pager.sh" --results "$RUN_DIR" "$@"
