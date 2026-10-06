#!/usr/bin/env bash
#
# The issues suite against the LOCAL Tilt stack (port 10351) on studio-rancher-desktop: is a
# GitHub issue something Hephaisto can be handed?
#
#   scripts/e2e/issues-local.sh                 # every scenario
#   scripts/e2e/issues-local.sh --only G01,G03  # some
#   scripts/e2e/issues-local.sh --list          # what exists, and what is known red
#   scripts/e2e/issues-local.sh --strict        # the release gate: every known-red entry fails
#
# It is the acceptance test of v0.14.0 (#243): an issue assigned to Hephaisto's account is
# planned by a Job, the plan is a comment on the issue, an approver answers /approve or
# /reject <reason> in a comment, and the implementing Job opens a draft pull request that closes
# the issue. One scenario per file under scripts/e2e/issues/, each with a header line
#
#   # issues: <id> | <needs, space-separated or -> | <exclusive|shared> | <what it asserts>
#
# and a scenario() function, in the pager suite's shape (scripts/e2e/pager.sh). A scenario may
# land before the code that turns it green: its id is then in issues/KNOWN_RED, where a red run
# is reported and does not fail, and a GREEN run fails - so the fix and its removal from the
# list land in one commit. A scenario that asserted nothing has not passed.
#
# GitHub is a stand-in (infra/e2e/notification-receiver/GitHubStandIn.cs, in the teams-stand-in
# pod): no account, nothing leaves the cluster. git is the in-cluster coder-git, `gh` the shim,
# the coder a script. Prerequisites (tilt_config.json): "github": "stand-in", "coder": true,
# "coder-mode": "pr", "coder-sdk": "fake".
#
# Capabilities this runner grants (ISSUES_CAPS); a scenario that needs one that is missing is
# skipped, and says so:
#   kubectl   kc runs kubectl against the agent's cluster, and only that cluster
#
# SAFETY. This machine's default kube context is a production cluster. The runner never reads
# ~/.kube/config after its first line: it extracts ONE context into a private kubeconfig, checks
# that context's API server is this machine, and hands only that file to the scenarios. The same
# pinning as pager-local.sh and codefix-local.sh. And it refuses an agent that talks to any
# GitHub but the stand-in, or whose coder is the real model: every scenario opens an issue, and
# with either of those a run would write on a real repository or spend real quota.
#
# Exit status: the number of scenarios that failed, plus any green scenario still on KNOWN_RED.

set -uo pipefail

# The whole script is one function, called on the last line: bash reads a script as it runs it,
# and a run in progress would execute whatever an editor had since put at its old byte offset.
main() {

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

# shellcheck source=lib/common.sh
source "$E2E_DIR/lib/common.sh"
# shellcheck source=lib/issues.sh
source "$E2E_DIR/lib/issues.sh"

ONLY=""
LIST=false
STRICT=false
OUT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --only)    ONLY="$2"; shift 2 ;;
        --list)    LIST=true; shift ;;
        --strict)  STRICT=true; shift ;;
        --results) OUT="$2"; shift 2 ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *)         die "unknown argument: $1" ;;
    esac
done

SCENARIOS="$E2E_DIR/issues"
KNOWN_RED_FILE="$SCENARIOS/KNOWN_RED"

meta() { sed -n 's/^# issues: //p' "$1" | head -1; }
field() { meta "$1" | awk -F' [|] ' -v n="$2" '{print $n}'; }

known_red() { grep -qE "^$1([[:space:]]|$)" "$KNOWN_RED_FILE" 2>/dev/null; }

if $LIST; then
    for f in "$SCENARIOS"/G*.sh; do
        id=$(field "$f" 1)
        printf '%-4s %-9s %-10s %s%s\n' "$id" "$(field "$f" 3)" "$(field "$f" 2)" "$(field "$f" 4)" \
            "$(known_red "$id" && echo '   [known red]')"
    done
    exit 0
fi

# --- the one cluster this run may touch -------------------------------------------------------

ISSUES_CONTEXT="${ISSUES_CONTEXT:-studio-rancher-desktop}"
case "$ISSUES_CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) die "refusing: $ISSUES_CONTEXT is not a local dev context" ;;
esac

OUT="${OUT:-$REPO/results/issues-local-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT/scenarios" || die "cannot write to $OUT"

E2E_KUBECONFIG="$OUT/kubeconfig"
kubectl config view --minify --flatten --context "$ISSUES_CONTEXT" > "$E2E_KUBECONFIG" \
    || die "kubectl knows no context $ISSUES_CONTEXT"
chmod 600 "$E2E_KUBECONFIG"
E2E_CONTEXT="$ISSUES_CONTEXT"

server=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config view -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
    *macstudio-von-florian*|*127.0.0.1*|*localhost*) ;;
    *) die "refusing: $ISSUES_CONTEXT points at $server, which is not this machine" ;;
esac
export E2E_KUBECONFIG E2E_CONTEXT

H="${ISSUES_HOST:-$(jq -r '.host // "localhost"' "$REPO/tilt_config.json" 2>/dev/null || echo localhost)}"

export ISSUES_API="${ISSUES_API:-http://$H:8100}"
export ISSUES_STANDIN="${ISSUES_STANDIN:-http://$H:8110}"
ISSUES_CAPS=" ${ISSUES_CAPS:-kubectl} "

RESULTS="${RESULTS:-$OUT/results.jsonl}"
touch "$RESULTS"
FAILED=0
CURRENT_PHASE=preflight

# --- preflight ----------------------------------------------------------------------------------

curl -sf --max-time 10 "$ISSUES_API/healthz" >/dev/null || die "the agent at $ISSUES_API is not healthy"
curl -sf --max-time 10 "$ISSUES_STANDIN/healthz" >/dev/null \
    || die "the stand-in at $ISSUES_STANDIN is not answering: set \"github\": \"stand-in\" in tilt_config.json"

# The stand-in's image is a fixed tag that Tilt rebuilds in place, and a rebuilt image does not
# restart the pod. One that predates GitHubStandIn.cs answers 404 here.
bot=$(_issues_curl "$ISSUES_STANDIN/github/control/state" 2>/dev/null | jq -r '.bot.login // empty' 2>/dev/null)
[ -n "$bot" ] \
    || die "the stand-in does not know GitHub: its pod predates GitHubStandIn.cs - kubectl -n hephaisto-obs rollout restart deploy/teams-stand-in"
export ISSUES_BOT="$bot"

# Which GitHub the agent talks to, read off its Deployment. THE VARIABLE'S NAME IS STAGE 2.2's:
# the agent had no GitHub client when this was written, so the name is what the Tiltfile's
# marked place says it will be. Three answers:
#
#   the stand-in   the run goes ahead - and only with the scripted coder
#   something else refused. A run opens a dozen issues and approves plans on them.
#   nothing        the agent cannot be handed an issue at all. Every scenario is red at its first
#                  line, which is what a run before stage 2.2 is for; the coder is not asked
#                  about, since nothing can start one.
agent_env() {
    kc -n "$ISSUES_NS" get deploy "$ISSUES_DEPLOY" \
        -o jsonpath="{.spec.template.spec.containers[0].env[?(@.name==\"$1\")].value}" 2>/dev/null || true
}

github_api=$(agent_env GitHub__ApiBaseUrl)
case "$github_api" in
    *github-stand-in*)
        sdk=$(agent_env CodeFix__Sdk)
        [ "$sdk" = fake ] \
            || die "refusing: the agent's coder is '${sdk:-unset}', and every scenario here starts one - set \"coder-sdk\": \"fake\" in tilt_config.json"
        ;;
    "")
        warn "the agent is not pointed at any GitHub (GitHub__ApiBaseUrl is unset): it cannot be handed an issue, and every scenario will be red"
        ;;
    *)
        die "refusing: the agent talks to $github_api, not the stand-in - set \"github\": \"stand-in\" in tilt_config.json"
        ;;
esac

say "issues suite, run ${ISSUES_RUN}: cluster $ISSUES_CONTEXT ($server), agent $ISSUES_API, stand-in $ISSUES_STANDIN"
say "the bot is $ISSUES_BOT, the repository $ISSUES_REPO; results in $OUT"

# Whatever an earlier run left failing must not be this run's first finding.
gh_fail off

# --- the schedule ---------------------------------------------------------------------------------

has_cap() { case "$ISSUES_CAPS" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

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
    for f in "$SCENARIOS"/G*.sh; do
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
        if [ "$(field "$f" 3)" = "exclusive" ]; then
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
    # An exclusive scenario restarts the agent, fails GitHub or lowers the code-fix mode; the
    # next one starts only once the agent answers again and GitHub does too.
    wait_for "the agent to answer" 300 issues_agent_up || warn "the agent did not answer within 300s before $(field "$f" 1)"
    gh_fail off
    say "run $(field "$f" 1): $(field "$f" 4)"
    run_one "$f" || true
done

# --- the verdict per scenario, with KNOWN_RED applied ---------------------------------------------

phase "verdict"
CURRENT_PHASE=issues

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
        else
            pass "$id $what"
        fi
    else
        if known_red "$id" && ! $STRICT; then
            record skip issues "$id $what" "known red: ${detail:0:400}"
        elif known_red "$id"; then
            fail "$id $what" "known red, and --strict: ${detail:0:500}"
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
  (map(select(.phase == "issues"))) as $v
  | "pass \($v | map(select(.status == "pass")) | length)   "
  + "fail \($v | map(select(.status == "fail")) | length)   "
  + "known red / skipped \($v | map(select(.status == "skip")) | length)"
' "$RESULTS"
say "results: $RESULTS (per scenario: $OUT/scenarios/)"

exit "$FAILED"
}

main "$@"
