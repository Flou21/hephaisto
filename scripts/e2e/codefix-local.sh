#!/usr/bin/env bash
#
# The code-fix tier against the LOCAL Tilt stack (port 10351) on studio-rancher-desktop.
#
#   scripts/e2e/codefix-local.sh                  # c15 happy path + negatives
#   scripts/e2e/codefix-local.sh --only c15       # just the happy path
#
# Prerequisites (tilt_config.json): "coder": true, "local-llm": true, "chaos": true, and
# "coder-mode": "pr". With "coder-sdk": "fake" the coder is scripted - a $0 plumbing run that
# still goes through the real guard, the real driver verification, a real push to the in-cluster
# git server and a real Draft-PR shape; with "real" it spends subscription quota.
#
# RED UNTIL THE JOB IS SPLIT (#116). c15 asserts that the coder container is handed no git or
# NuGet key, that no process inside it holds one, and that the implement result is printed by
# the publish container. This suite has no known-red list, so it says so here: those assertions
# fail against an agent that still starts one container, and this paragraph goes in the commit
# that starts three.
#
# SAFETY. This machine's default kube context is a production cluster. The runner never reads
# ~/.kube/config after its first line: it extracts ONE context into a private kubeconfig, checks
# that context's API server is this machine, and exports only that file. A mistyped namespace
# therefore cannot reach anything that matters.

set -Eeuo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

source "$E2E_DIR/lib/common.sh"
source "$E2E_DIR/lib/codefix.sh"

ONLY=""
while [ $# -gt 0 ]; do
    case "$1" in
        --only) ONLY="$2"; shift 2 ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *) die "unknown argument $1" ;;
    esac
done

CF_CONTEXT="${CF_CONTEXT:-studio-rancher-desktop}"
case "$CF_CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) die "refusing: $CF_CONTEXT is not a local dev context" ;;
esac

RUN_DIR="${CF_RUN_DIR:-$REPO/results/codefix-local-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$RUN_DIR"
RESULTS="$RUN_DIR/results.jsonl"
: > "$RESULTS"
FAILED=0
CURRENT_PHASE=preflight

E2E_KUBECONFIG="$RUN_DIR/kubeconfig"
kubectl config view --minify --flatten --context "$CF_CONTEXT" > "$E2E_KUBECONFIG"
chmod 600 "$E2E_KUBECONFIG"
E2E_CONTEXT="$CF_CONTEXT"

server=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config view -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
    *macstudio-von-florian*|*127.0.0.1*|*localhost*) ;;
    *) die "refusing: $CF_CONTEXT points at $server, which is not this machine" ;;
esac
export KUBECONFIG="$E2E_KUBECONFIG"

H="${CF_HOST:-$(jq -r '.host // "localhost"' "$REPO/tilt_config.json" 2>/dev/null || echo localhost)}"
CF_API="${CF_API:-http://$H:8100}"
CF_TILT=(tilt --host "$H" --port "${CF_TILT_PORT:-10351}")

say "cluster $CF_CONTEXT ($server), agent $CF_API, results in $RUN_DIR"

phase_start() { CURRENT_PHASE="$1"; phase "$1"; }

# --- preflight ------------------------------------------------------------------------------
phase_start preflight

curl -sf --max-time 10 "$CF_API/healthz" >/dev/null && pass "agent is healthy" || die "agent at $CF_API is not healthy"

mode=$(cf_get /api/codefixes/mode | jq -r '.effective // empty')
[ -n "$mode" ] || die "GET /api/codefixes/mode answered nothing - is this a v0.9.0 agent?"
[ "$mode" = "Pr" ] && pass "code-fix mode is Pr" || fail "code-fix mode is Pr" "it is $mode; set coder-mode=pr in tilt_config.json"

cf_assert_rbac

kc -n "$CF_CODER_NS" get deploy coder-git >/dev/null 2>&1 \
    && pass "the in-cluster git server exists" || fail "the in-cluster git server exists"

case "$ONLY" in
    ""|all) run_c15; run_forged; run_c13; run_c19 ;;
    c15) run_c15 ;;
    c13) run_c13 ;;
    c19) run_c19 ;;
    forged) run_forged ;;
    negatives) run_forged; run_c13; run_c19 ;;
    *) die "--only takes c15, c13, c19, forged or negatives" ;;
esac

phase "summary"
passed=$(jq -s '[.[] | select(.status == "pass")] | length' "$RESULTS")
failed=$(jq -s '[.[] | select(.status == "fail")] | length' "$RESULTS")
skipped=$(jq -s '[.[] | select(.status == "skip")] | length' "$RESULTS")
say "$passed passed, $failed failed, $skipped skipped - $RESULTS"
jq -r 'select(.status != "pass") | "  \(.status)  [\(.phase)] \(.name)\(if .detail != "" then " -- " + .detail else "" end)"' "$RESULTS"
[ "$failed" = 0 ]
