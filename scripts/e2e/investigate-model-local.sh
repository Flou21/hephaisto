#!/usr/bin/env bash
#
# Investigation in a Job with a REAL model (v0.12.0 F5), against the local Tilt stack. Not a gate.
#
#   scripts/e2e/investigate-model-local.sh          # three runs of I4
#   scripts/e2e/investigate-model-local.sh -n 5
#
# Prerequisites (tilt_config.json): "coder": true, "investigator": true and
# "investigator-sdk": "real". values-dev-investigator.yaml pins Haiku and $0.50 a run; the token
# is the one in the hephaisto-codefix Secret, so on a subscription this costs quota, not money.
#
# What it reports is how many of N runs a real model, driving Claude Code against Hephaisto's own
# tools on c15, concluded with a grounded application finding. A model is not deterministic, so
# this never fails a build: it prints n of N and exits 0. investigate-local.sh with the scripted
# investigator is the gate.

set -Eeuo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

N=3
while [ $# -gt 0 ]; do
    case "$1" in
        -n) N="$2"; shift 2 ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *) echo "unknown argument $1" >&2; exit 2 ;;
    esac
done

sdk=$(jq -r '."investigator-sdk" // "fake"' "$REPO/tilt_config.json" 2>/dev/null || echo fake)
[ "$sdk" = real ] || { echo "tilt_config.json investigator-sdk is '$sdk'; this tier needs 'real'" >&2; exit 2; }

RUN_DIR="$REPO/results/investigate-model-local-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$RUN_DIR"

concluded=0
for i in $(seq 1 "$N"); do
    echo "== run $i of $N =="
    if IV_ALLOW_REAL_SDK=1 IV_RUN_DIR="$RUN_DIR/run-$i" "$E2E_DIR/investigate-local.sh" --only I4; then
        concluded=$((concluded + 1))
    fi
    jq -r 'select(.phase == "I4") | "  \(.status)  \(.name)\(if .detail != "" then " -- " + .detail else "" end)"' \
        "$RUN_DIR/run-$i/results.jsonl" 2>/dev/null | grep -vE "^  pass" || true
    if [ -f "$RUN_DIR/run-$i/I4-investigation.json" ]; then
        jq -r '"  model \(.modelId), \(.stepsUsed) turns, \(.toolCallsUsed) tool calls, \(.terminationReason), executor \(.executor)"' \
            "$RUN_DIR/run-$i/I4-investigation.json"
    fi
done

echo
echo "$concluded of $N real-model Job investigations concluded with a grounded application finding - $RUN_DIR"
exit 0
