#!/usr/bin/env bash
#
# Investigation in a Job (v0.12.0 F5) against the LOCAL Tilt stack (port 10351) on
# studio-rancher-desktop.
#
#   scripts/e2e/investigate-local.sh                 # every scenario
#   scripts/e2e/investigate-local.sh --only I2,I4    # some
#   scripts/e2e/investigate-local.sh --list          # what exists, and what is known red
#   scripts/e2e/investigate-local.sh --strict        # the release gate: a known-red entry fails
#
# Prerequisites (tilt_config.json): "coder": true, "local-llm": true, "chaos": true,
# "investigator": true and "investigator-sdk": "fake". With the fake SDK every Job investigation
# is scripted and costs $0; the in-process scenarios use the host's Ollama. Nothing here spends
# subscription quota: codeFixMode is switched off for the run and restored afterwards.
#
# scripts/e2e/investigate/KNOWN_RED lists scenarios that land before the part that makes them
# pass. A red scenario listed there is reported and does not fail the run; a GREEN one listed
# there fails it, so the fix and the removal land in one commit.
#
# SAFETY. As codefix-local.sh: one context extracted into a private kubeconfig, checked to be
# this machine, and nothing else is ever read.

set -Eeuo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

source "$E2E_DIR/lib/common.sh"
source "$E2E_DIR/lib/codefix.sh"
source "$E2E_DIR/lib/investigate.sh"

KNOWN_RED_FILE="$E2E_DIR/investigate/KNOWN_RED"

ONLY=""
STRICT=0
LIST=0
while [ $# -gt 0 ]; do
    case "$1" in
        --only) ONLY="$2"; shift 2 ;;
        --strict) STRICT=1; shift ;;
        --list) LIST=1; shift ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *) die "unknown argument $1" ;;
    esac
done

known_red() { grep -vE '^\s*(#|$)' "$KNOWN_RED_FILE" | awk '{print $1}'; }
is_known_red() { known_red | grep -qx "$1"; }

if [ "$LIST" = 1 ]; then
    for s in $IV_SCENARIOS; do
        if is_known_red "$s"; then
            printf '%-4s known red  %s\n' "$s" "$(grep -E "^$s " "$KNOWN_RED_FILE" | cut -d' ' -f2-)"
        else
            printf '%-4s\n' "$s"
        fi
    done
    exit 0
fi

CF_CONTEXT="${CF_CONTEXT:-studio-rancher-desktop}"
case "$CF_CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) die "refusing: $CF_CONTEXT is not a local dev context" ;;
esac

RUN_DIR="${IV_RUN_DIR:-$REPO/results/investigate-local-$(date -u +%Y%m%dT%H%M%SZ)}"
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

# --- preflight: nothing in this run may spend money -------------------------------------------
phase preflight

sdk=$(jq -r '."investigator-sdk" // "fake"' "$REPO/tilt_config.json" 2>/dev/null || echo fake)
[ "$sdk" = fake ] || die "tilt_config.json investigator-sdk is '$sdk'; this runner is \$0 only with 'fake'"

SAVED_CODEFIX=$(iv_switch_get codeFixMode)
SAVED_EXECUTOR=$(iv_switch_get "$IV_SWITCH_KEY")
restore() {
    iv_switch_set codeFixMode "$SAVED_CODEFIX" || true
    iv_switch_set "$IV_SWITCH_KEY" "$SAVED_EXECUTOR" || true
}
trap restore EXIT
iv_switch_set codeFixMode off
say "codeFixMode off for this run (was '${SAVED_CODEFIX:-unset}'), executor switch was '${SAVED_EXECUTOR:-unset}'"

# --- scenarios --------------------------------------------------------------------------------
selected="$IV_SCENARIOS"
[ -n "$ONLY" ] && selected=$(tr ',' ' ' <<<"$ONLY")

declare_red=()
declare_green=()
for s in $selected; do
    type "scenario_$s" >/dev/null 2>&1 || die "no scenario $s (see --list)"
    CURRENT_PHASE="$s"
    phase "$s"
    before=$FAILED
    "scenario_$s" || fail "$s ran to completion" "the scenario itself failed"
    if [ "$FAILED" -gt "$before" ]; then declare_red+=("$s"); else declare_green+=("$s"); fi
done

# --- summary: known red is reported, green-but-listed fails -----------------------------------
phase summary
verdict=0
for s in "${declare_red[@]:-}"; do
    [ -n "$s" ] || continue
    if is_known_red "$s" && [ "$STRICT" = 0 ]; then
        say "$s red, known: $(grep -E "^$s " "$KNOWN_RED_FILE" | cut -d' ' -f2-)"
    else
        say "$s RED"; verdict=1
    fi
done
for s in "${declare_green[@]:-}"; do
    [ -n "$s" ] || continue
    if is_known_red "$s"; then
        say "$s is GREEN but still listed in KNOWN_RED - remove it in the commit that fixed it"; verdict=1
    fi
done

passed=$(jq -s '[.[] | select(.status == "pass")] | length' "$RESULTS")
failed=$(jq -s '[.[] | select(.status == "fail")] | length' "$RESULTS")
skipped=$(jq -s '[.[] | select(.status == "skip")] | length' "$RESULTS")
say "green: ${declare_green[*]:-none}   red: ${declare_red[*]:-none}"
say "$passed assertions passed, $failed failed, $skipped skipped - $RESULTS"
jq -r 'select(.status != "pass") | "  \(.status)  [\(.phase)] \(.name)\(if .detail != "" then " -- " + .detail else "" end)"' "$RESULTS"
exit "$verdict"
