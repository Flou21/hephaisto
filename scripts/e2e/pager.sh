#!/usr/bin/env bash
# The pager suite: is Hephaisto a pager a person can rely on?
#
# Every scenario under scripts/e2e/pager/ posts alerts into an INSTALLED agent - through a real
# Alertmanager or straight to the webhook - and asserts on what a person would have seen: which
# incidents exist, what state they are in, which Teams messages were sent and edited, what the
# model was asked. The model and Teams are stand-ins (infra/e2e/notification-receiver), so a run
# is deterministic, free, and fast enough to gate every change.
#
# It is the acceptance test of the v0.10.0 milestone (docs/roadmap.md, F0): each sentence of the
# milestone's "Done when" is a scenario here. A scenario may land before the code that turns it
# green; its id is then listed in pager/KNOWN_RED, where a red run is reported and does not fail,
# and a GREEN run fails - so the fix and its removal from the list land in the same commit.
#
# This script only drives. It never installs anything and never picks a cluster: the three
# callers do that and export the addresses (see lib/pager.sh):
#
#   scripts/e2e/pager-local.sh      the dev cluster, through Tilt's port-forwards
#   .github/workflows/ci.yml        e2e-pager, on kind, on every change
#   scripts/e2e/run.sh              the `pager` phase of the release harness
#
# Usage:
#   scripts/e2e/pager.sh [--only P05,P09] [--serial] [--list] [--results DIR]
#
# Capabilities a caller grants through PAGER_CAPS (space-separated). A scenario that needs one
# that is missing is skipped, and says so:
#   am           PAGER_AM is an Alertmanager routing to the agent
#   kubectl      pager_kc runs kubectl against the agent's cluster, and only that cluster
#   prometheus   a Prometheus evaluates the chart's rules and routes to PAGER_AM
#   spare-agent  a second copy of the agent may be started briefly (production image only)
#
# Exit status: the number of scenarios that failed, plus any green scenario still on KNOWN_RED.

set -uo pipefail

# The whole script is one function, called on the last line. bash reads a script as it runs it,
# so a run in progress used to execute whatever an editor had since put at its old byte offset;
# a function is parsed in full before its first line runs.
main() {

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# common.sh names the release harness's kind context unconditionally; a caller that pinned a
# different one (pager-local.sh, the CI job) keeps it.
_pinned_context="${E2E_CONTEXT:-}"
# shellcheck source=lib/common.sh
source "$HERE/lib/common.sh"
[ -n "$_pinned_context" ] && E2E_CONTEXT="$_pinned_context"
# shellcheck source=lib/pager.sh
source "$HERE/lib/pager.sh"

ONLY=""
SERIAL=false
LIST=false
OUT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --only)    ONLY="$2"; shift 2 ;;
        --serial)  SERIAL=true; shift ;;
        --list)    LIST=true; shift ;;
        --results) OUT="$2"; shift 2 ;;
        -h|--help) sed -n '2,38p' "$0"; exit 0 ;;
        *)         die "unknown argument: $1" ;;
    esac
done

SCENARIOS="$HERE/pager"
KNOWN_RED_FILE="$SCENARIOS/KNOWN_RED"

# One header line per scenario file carries its metadata, so --list and the scheduler read the
# same thing the reader of the file does:
#   # pager: <id> | <needs, space-separated or -> | <exclusive|shared> | <what it asserts>
meta() { sed -n 's/^# pager: //p' "$1" | head -1; }
field() { meta "$1" | awk -F' [|] ' -v n="$2" '{print $n}'; }

known_red() { grep -qE "^$1([[:space:]]|$)" "$KNOWN_RED_FILE" 2>/dev/null; }

if $LIST; then
    for f in "$SCENARIOS"/P*.sh; do
        id=$(field "$f" 1)
        printf '%-4s %-9s %-26s %s%s\n' "$id" "$(field "$f" 3)" "$(field "$f" 2)" "$(field "$f" 4)" \
            "$(known_red "$id" && echo '   [known red]')"
    done
    exit 0
fi

for v in PAGER_API PAGER_HOOK PAGER_STANDIN; do
    [ -n "${!v:-}" ] || die "$v is not set - run this through pager-local.sh, run.sh or the CI job"
done

PAGER_CAPS=" ${PAGER_CAPS:-} "
PAGER_NS="${PAGER_NS:-hephaisto}"
PAGER_DEPLOY="${PAGER_DEPLOY:-hephaisto}"
export PAGER_NS PAGER_DEPLOY

# The scenarios' only door to the cluster is common.sh's kc, which refuses any context but the
# one the caller pinned in E2E_KUBECONFIG and E2E_CONTEXT.
pager_kc() { kc "$@"; }
has_cap() { case "$PAGER_CAPS" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

OUT="${OUT:-$HERE/../../results/pager-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT/scenarios"
RESULTS="${RESULTS:-$OUT/results.jsonl}"
touch "$RESULTS"
FAILED=0

say "pager suite, run ${PAGER_RUN}: agent $PAGER_API, stand-in $PAGER_STANDIN, alertmanager ${PAGER_AM:-none}"
say "capabilities:${PAGER_CAPS% }"

# The stand-in answers at once unless a scenario says otherwise, and forgets nothing between
# runs on purpose: every read-back filters by this run's alert names.
pager_llm_release
_pager_curl -X POST "$PAGER_STANDIN/llm/delay/0" >/dev/null

# The agent must be up and must know the two filters the suite reads through. An agent that
# predates `state=any` answers a validation problem, which would read as "no incidents" in every
# scenario and turn the whole suite into a list of confusing failures.
probe=$(_pager_curl "$PAGER_API/api/incidents?state=any&limit=1&alertname=$(pager_name probe)") \
    || die "the agent at $PAGER_API does not answer"
[ "$(jq -r type <<<"$probe" 2>/dev/null)" = "array" ] \
    || die "the agent does not understand ?state=any&alertname= - it is older than the suite: ${probe:0:200}"

run_one() {
    local file="$1" id
    id=$(field "$file" 1)
    (
        CURRENT_PHASE="$id"
        RESULTS="$OUT/scenarios/$id.jsonl"
        FAILED=0
        : > "$RESULTS"
        # shellcheck disable=SC1090
        source "$file"
        scenario
        exit "$FAILED"
    ) > "$OUT/scenarios/$id.log" 2>&1
}

selected() {
    local f id
    for f in "$SCENARIOS"/P*.sh; do
        id=$(field "$f" 1)
        if [ -n "$ONLY" ]; then
            case ",$ONLY," in *",$id,"*) ;; *) continue ;; esac
        fi
        echo "$f"
    done
}

runnable() {
    local f="$1" need
    for need in $(field "$f" 2); do
        [ "$need" = "-" ] && continue
        has_cap "$need" || { echo "needs $need"; return 1; }
    done
    return 0
}

SKIPPED=""
SHARED=""
EXCLUSIVE=""
for f in $(selected); do
    if reason=$(runnable "$f"); then
        if [ "$(field "$f" 3)" = "exclusive" ] || $SERIAL; then
            EXCLUSIVE="$EXCLUSIVE $f"
        else
            SHARED="$SHARED $f"
        fi
    else
        SKIPPED="$SKIPPED $(field "$f" 1):${reason// /_}"
    fi
done

phase "shared scenarios, concurrently"
pids=""
for f in $SHARED; do
    say "start $(field "$f" 1): $(field "$f" 4)"
    run_one "$f" &
    pids="$pids $!"
done
for p in $pids; do wait "$p" || true; done

phase "exclusive scenarios, one at a time"
for f in $EXCLUSIVE; do
    say "run $(field "$f" 1): $(field "$f" 4)"
    run_one "$f" || true
done

# ---------------------------------------------------------------------------------------
# The verdict per scenario, with KNOWN_RED applied
# ---------------------------------------------------------------------------------------
phase "verdict"
CURRENT_PHASE=pager
STALE=0
REDS=0

for f in $SHARED $EXCLUSIVE; do
    id=$(field "$f" 1)
    what=$(field "$f" 4)
    r="$OUT/scenarios/$id.jsonl"
    fails=$(jq -s '[.[] | select(.status == "fail")] | length' "$r" 2>/dev/null || echo 1)
    passes=$(jq -s '[.[] | select(.status == "pass")] | length' "$r" 2>/dev/null || echo 0)
    detail=$(jq -rs '[.[] | select(.status == "fail") | .name + (if .detail != "" then " (" + .detail + ")" else "" end)] | join("; ")' "$r" 2>/dev/null)

    # A scenario that asserted nothing has not passed. It has not run.
    if [ "$fails" -eq 0 ] && [ "$passes" -eq 0 ]; then
        fails=1
        detail="asserted nothing - see $OUT/scenarios/$id.log"
    fi

    if [ "$fails" -eq 0 ]; then
        if known_red "$id"; then
            fail "$id $what" "GREEN, and still on KNOWN_RED - remove it in the commit that fixed it"
            STALE=$((STALE + 1))
        else
            pass "$id $what"
        fi
    else
        if known_red "$id"; then
            record skip pager "$id $what" "known red: ${detail:0:400}"
            REDS=$((REDS + 1))
        else
            fail "$id $what" "${detail:0:600}"
        fi
    fi
done

for s in $SKIPPED; do
    id="${s%%:*}"; why="${s#*:}"
    skip "$id" "not run here: ${why//_/ }"
done

phase "summary"
jq -rs '
  (map(select(.phase == "pager"))) as $v
  | "pass \($v | map(select(.status == "pass")) | length)   "
  + "fail \($v | map(select(.status == "fail")) | length)   "
  + "known red / skipped \($v | map(select(.status == "skip")) | length)"
' "$RESULTS"
say "results: $RESULTS (per scenario: $OUT/scenarios/)"

exit "$FAILED"
}

main "$@"
