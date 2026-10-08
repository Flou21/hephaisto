#!/usr/bin/env bash
#
# The live tier: the dev agent on studio-rancher-desktop against the REAL github.com - a real
# account, its real tokens, the real `gh` in the coder Job. The first, and the only, automated
# test of this project that leaves the cluster for GitHub (#249).
#
#   scripts/e2e/github-live.sh                 # every scenario, about a quarter of an hour
#   scripts/e2e/github-live.sh --only L01,L04  # some
#   scripts/e2e/github-live.sh --list          # what exists
#   scripts/e2e/github-live.sh --sweep         # close whatever an aborted run left, and stop
#
# Everything else that tests issues as work runs against a stand-in for GitHub
# (scripts/e2e/issues-local.sh), which answers what this project's authors believed GitHub
# answers. This suite asks GitHub: the same road - an issue assigned to the bot, a plan Job, the
# plan as a comment, /approve by an approver, an implementing Job, a draft pull request that
# closes the issue - with nothing between the agent and api.github.com but the egress proxy, and
# nothing between the Job's `gh` and github.com but the same proxy. One scenario per file under
# scripts/e2e/live/, each with a header line
#
#   # live: <id> | <what it asserts>
#
# and a scenario() function, written against lib/live.sh. They run one at a time, in order:
# there is one Job slot, and one person to play.
#
# WHAT IT NEEDS (tilt_config.json: "github": "live", "coder": true, "coder-mode": "pr",
# "coder-sdk": "fake"; charts/hephaisto/values-dev-github-live.yaml says the rest):
#   - the bot account, a member of the sandbox's organisation with write on the sandbox, and its
#     two tokens in the two Secrets - the agent's (Issues read and write, Pull requests read)
#     and the coder's (Contents and Pull requests read and write);
#   - the sandbox, TrueRelevance/hephaisto-sandbox, with GitHub Actions DISABLED: a pushed
#     branch must not run anything. Its main is the fixture's c15 branch, which is what the
#     scripted fix applies to;
#   - the sandbox enabled in dev-context's repos.yaml, on the ref the values file names;
#   - `gh` on this machine, logged in as an account whose NUMBER is in the agent's approvers.
#     The suite plays the person with it: it opens the issues, assigns the bot, answers plans.
#
# IT REFUSES TO RUN unless all of this is so, read off the agent's Deployment and off GitHub:
# the kube context is this machine's; the agent talks to https://api.github.com; the ONE
# repository it lists is the sandbox; its coder is the script (CodeFix__Sdk=fake) and the Job's
# gh is the real one; `gh` is an approver; Actions are off on the sandbox; and neither the
# sandbox nor the agent holds anything an earlier run left. (--sweep asks only GitHub, and needs
# only the kube context check and a `gh` that can read the sandbox: it is for the minute after a
# run was killed, and works when the agent has been pointed elsewhere again.)
#
# WHAT IT WRITES, and where. Only in the sandbox - lib/live.sh puts the repository's name into
# every request itself and takes it from nobody. Per run: five issues, half a dozen comments by
# the person, one branch and one draft pull request by the bot. A trap cleans up on EVERY exit:
# the issues it opened are closed, the pull requests they led to are closed - never merged -
# and those pull requests' hephaisto/codefix-* branches are deleted. It never pushes, never
# merges, and never touches main, and it asserts at the end that main did not move.
#
# WHAT IT DOES NOT TEST, and where that is tested instead:
#   - "merged is Done". Merging would move main, and the scripted fix would no longer apply to
#     it. The stand-in's G11 holds that the agent reads a merge as Done; that GitHub closes the
#     issue of a merged pull request whose description says Closes is GitHub's own promise,
#     and this suite holds the half of it that can be seen without merging:
#     closingIssuesReferences names the issue.
#   - an answer by somebody who is not an approver (issues G04). There is one human account.
#   - GitHub failing, or limiting (issues G09): GitHub cannot be told to.
#   - a model. The coder is the script; what a model writes is tested by the eval harness.
#   - the cap on comments (issues G10), a restart in the middle (G08), the mode (G12).
#
# SAFETY. This machine's default kube context is a production cluster. The runner never reads
# ~/.kube/config after its first line: it extracts ONE context into a private kubeconfig, checks
# that context's API server is this machine, and hands only that file to the scenarios - the
# same pinning as issues-local.sh.
#
# Exit status: the number of scenarios that failed, plus one if the sandbox was not left clean.

set -uo pipefail

# The whole script is one function, called on the last line: bash reads a script as it runs it,
# and a run in progress would execute whatever an editor had since put at its old byte offset.
main() {

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$E2E_DIR/../.." && pwd)"

# shellcheck source=lib/common.sh
source "$E2E_DIR/lib/common.sh"
# shellcheck source=lib/live.sh
source "$E2E_DIR/lib/live.sh"

ONLY=""
LIST=false
SWEEP=false
OUT=""

while [ $# -gt 0 ]; do
    case "$1" in
        --only)    ONLY="$2"; shift 2 ;;
        --list)    LIST=true; shift ;;
        --sweep)   SWEEP=true; shift ;;
        --results) OUT="$2"; shift 2 ;;
        -h|--help) awk 'NR>1 { if ($0 !~ /^#/) exit; sub(/^# ?/, ""); print }' "$0"; exit 0 ;;
        *)         die "unknown argument: $1" ;;
    esac
done

SCENARIOS="$E2E_DIR/live"

meta() { sed -n 's/^# live: //p' "$1" | head -1; }
field() { meta "$1" | awk -F' [|] ' -v n="$2" '{print $n}'; }

if $LIST; then
    for f in "$SCENARIOS"/L*.sh; do
        printf '%-4s %s\n' "$(field "$f" 1)" "$(field "$f" 2)"
    done
    exit 0
fi

# --- the one cluster this run may touch -------------------------------------------------------

LIVE_CONTEXT="${LIVE_CONTEXT:-studio-rancher-desktop}"
case "$LIVE_CONTEXT" in
    studio-rancher-desktop|rancher-desktop) ;;
    *) die "refusing: $LIVE_CONTEXT is not a local dev context" ;;
esac

OUT="${OUT:-$REPO/results/github-live-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT/scenarios" || die "cannot write to $OUT"

E2E_KUBECONFIG="$OUT/kubeconfig"
kubectl config view --minify --flatten --context "$LIVE_CONTEXT" > "$E2E_KUBECONFIG" \
    || die "kubectl knows no context $LIVE_CONTEXT"
chmod 600 "$E2E_KUBECONFIG"
E2E_CONTEXT="$LIVE_CONTEXT"

server=$(KUBECONFIG="$E2E_KUBECONFIG" kubectl config view -o jsonpath='{.clusters[0].cluster.server}')
case "$server" in
    *macstudio-von-florian*|*127.0.0.1*|*localhost*) ;;
    *) die "refusing: $LIVE_CONTEXT points at $server, which is not this machine" ;;
esac
export E2E_KUBECONFIG E2E_CONTEXT

H="${LIVE_HOST:-$(jq -r '.host // "localhost"' "$REPO/tilt_config.json" 2>/dev/null || echo localhost)}"

export ISSUES_API="${LIVE_API:-http://$H:8100}"
export LIVE_OUT="$OUT"
export LIVE_CREATED="$OUT/created-issues"
: > "$LIVE_CREATED"

RESULTS="${RESULTS:-$OUT/results.jsonl}"
touch "$RESULTS"
FAILED=0
CURRENT_PHASE=preflight

for t in gh jq curl kubectl; do
    command -v "$t" >/dev/null 2>&1 || die "missing required tool: $t"
done

# --- --sweep: only GitHub is asked, so it also works when the agent is somewhere else again -------

if $SWEEP; then
    gh api user >/dev/null 2>&1 || die "gh is not logged in to github.com (gh auth status)"
    [ "$(_live_api GET "" --jq '.full_name' 2>/dev/null)" = "$LIVE_REPO" ] || die "gh cannot read $LIVE_REPO"
    say "sweeping $LIVE_REPO of what earlier runs left: open issues titled '$LIVE_TITLE_PREFIX ...', the bot's open pull requests from hephaisto/codefix-* branches, and those branches"
    live_sweep
    say "swept"
    exit 0
fi

# --- what the agent is, read off its Deployment ---------------------------------------------------
#
# Every refusal below is about the same thing: this run writes on github.com with a person's
# account, and makes a bot with tokens that see a whole organisation open a pull request. It
# goes ahead only where all of that can land in one place, the sandbox.

agent_env_json() {
    kc -n "$ISSUES_NS" get deploy "$ISSUES_DEPLOY" -o json 2>/dev/null | jq -c '.spec.template.spec.containers[0].env // []'
}

ENV_JSON=$(agent_env_json)
[ -n "$ENV_JSON" ] && [ "$ENV_JSON" != "[]" ] || die "cannot read the agent's Deployment $ISSUES_NS/$ISSUES_DEPLOY in $LIVE_CONTEXT"

env_value()  { jq -r --arg n "$1" '[.[] | select(.name == $n)][0].value // empty' <<<"$ENV_JSON"; }
env_values() { jq -r --arg p "$1" '.[] | select(.name | startswith($p)) | .value // empty' <<<"$ENV_JSON"; }

github_api=$(env_value GitHub__ApiBaseUrl)
case "${github_api%/}" in
    https://api.github.com) ;;
    "") die "refusing: the agent is not pointed at any GitHub - set \"github\": \"live\" in tilt_config.json" ;;
    *)  die "refusing: the agent talks to $github_api, not https://api.github.com - set \"github\": \"live\" in tilt_config.json" ;;
esac

repositories=$(env_values GitHub__Repositories__ | tr '\n' ' ' | sed 's/ $//')
[ "$repositories" = "$LIVE_REPO" ] \
    || die "refusing: the agent lists '${repositories}', and this suite runs only where the one repository is $LIVE_REPO"

sdk=$(env_value CodeFix__Sdk)
[ "$sdk" = fake ] \
    || die "refusing: the agent's coder is '${sdk:-unset}', and every scenario here starts one - set \"coder-sdk\": \"fake\" in tilt_config.json"

[ "$(env_value CodeFix__Gh)" != shim ] \
    || die "refusing: the coder Job's gh is the shim (CodeFix__Gh=shim), which opens no pull request on github.com"

configured_bot=$(env_value GitHub__BotLogin)
[ -z "$configured_bot" ] || [ "$configured_bot" = "$LIVE_BOT" ] \
    || die "refusing: the agent takes issues for '$configured_bot', and this suite assigns them to $LIVE_BOT"

# The person. `gh` decides who that is; the agent's list decides whether they may answer.
me=$(gh api user 2>/dev/null) || die "gh is not logged in to github.com (gh auth status)"
ISSUES_APPROVER=$(jq -r '.login' <<<"$me")
ISSUES_APPROVER_ID=$(jq -r '.id' <<<"$me")
env_values GitHub__Approvers__ | grep -qx "$ISSUES_APPROVER_ID" \
    || die "refusing: gh is logged in as $ISSUES_APPROVER ($ISSUES_APPROVER_ID), who is not one of the agent's approvers ($(env_values GitHub__Approvers__ | tr '\n' ' '))"
[ "$ISSUES_APPROVER" != "$LIVE_BOT" ] || die "refusing: gh is logged in as the bot itself; the suite plays the person"
export ISSUES_APPROVER ISSUES_APPROVER_ID

curl -sf --max-time 10 "$ISSUES_API/healthz" >/dev/null || die "the agent at $ISSUES_API is not healthy"

# --- what the sandbox is, read off GitHub -----------------------------------------------------------

sandbox=$(_live_api GET "" 2>/dev/null) || die "gh cannot read $LIVE_REPO as $ISSUES_APPROVER"
[ "$(jq -r '.full_name' <<<"$sandbox")" = "$LIVE_REPO" ] || die "GitHub answered for another repository than $LIVE_REPO"
[ "$(jq -r '.default_branch' <<<"$sandbox")" = main ] || die "refusing: the default branch of $LIVE_REPO is not main"

# A pushed branch must not run anything: a workflow there would run code this suite pushed,
# with whatever that repository's Actions may reach.
[ "$(_live_api GET actions/permissions --jq '.enabled' 2>/dev/null)" = false ] \
    || die "refusing: GitHub Actions are not disabled on $LIVE_REPO (or $ISSUES_APPROVER cannot see whether they are)"

MAIN_BEFORE=$(live_main_sha)
[ -n "$MAIN_BEFORE" ] || die "cannot read main of $LIVE_REPO"
export LIVE_MAIN="$MAIN_BEFORE"

left=$(_live_api GET "issues?state=open&assignee=$LIVE_BOT&per_page=100" --jq '[.[] | select(.pull_request == null) | .number] | join(" ")' 2>/dev/null)
[ -z "$left" ] || die "refusing: $LIVE_REPO has open issues assigned to $LIVE_BOT (#${left// / #}) - an earlier run did not clean up; scripts/e2e/github-live.sh --sweep closes them"
left=$(_live_api GET "git/matching-refs/heads/hephaisto/codefix-" --jq '[.[].ref] | join(" ")' 2>/dev/null)
[ -z "$left" ] || die "refusing: $LIVE_REPO has branches an earlier run left ($left); scripts/e2e/github-live.sh --sweep deletes them"
held=$(_issues_curl "$ISSUES_API/api/workitems?limit=200" | jq --arg r "$LIVE_REPO" '[.[]? | select(.repository == $r)] | length' 2>/dev/null)
[ "${held:-1}" -eq 0 ] || die "refusing: the agent still holds $held work item(s) of $LIVE_REPO from an earlier run; wait for its next poll, or see GET /api/workitems"

# --- from here on something is written, so from here on it is cleaned up ----------------------------

CLEANED=false
cleanup() {
    $CLEANED && return 0
    CLEANED=true
    phase "cleanup"
    live_cleanup 2>&1 | sed 's/^/  /'
}
trap 'cleanup' EXIT
trap 'warn "interrupted"; exit 130' INT TERM

say "live tier, run ${ISSUES_RUN}: cluster $LIVE_CONTEXT ($server), agent $ISSUES_API -> $github_api"
say "the sandbox is $LIVE_REPO (main ${MAIN_BEFORE:0:12}), the bot $LIVE_BOT, the person $ISSUES_APPROVER ($ISSUES_APPROVER_ID); results in $OUT"

# --- the schedule ---------------------------------------------------------------------------------

run_one() {
    local file="$1" id
    id=$(field "$file" 1)
    (
        # The parent's trap is the parent's: a scenario that ends does not clean up for the run.
        trap - EXIT INT TERM
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

SELECTED=""
for f in "$SCENARIOS"/L*.sh; do
    id=$(field "$f" 1)
    if [ -n "$ONLY" ]; then
        case ",$ONLY," in *",$id,"*) ;; *) continue ;; esac
    fi
    SELECTED="$SELECTED $f"
done
[ -n "$SELECTED" ] || die "no scenario matches --only $ONLY"

phase "scenarios, one at a time"
for f in $SELECTED; do
    wait_for "the agent to answer" 300 issues_agent_up || warn "the agent did not answer within 300s before $(field "$f" 1)"
    say "run $(field "$f" 1): $(field "$f" 2)"
    run_one "$f" || true
done

# --- the sandbox is left as it was found -----------------------------------------------------------

cleanup
CURRENT_PHASE=sandbox

# shellcheck disable=SC2046
created=$(sort -un "$LIVE_CREATED" | tr '\n' ' ')
open_left=0
for n in $created; do
    [ "$(_live_api GET "issues/$n" --jq '.state' 2>/dev/null)" = closed ] || open_left=$(( open_left + 1 ))
done
want "every issue this run opened is closed" "$open_left" -eq 0
# shellcheck disable=SC2086
want "no pull request of this run is open" "$(live_prs_for $created | jq '[.[] | select(.state == "open")] | length')" -eq 0
# shellcheck disable=SC2086
want "no branch of this run is left" "$(live_branches_for $created | wc -l | tr -d ' ')" -eq 0
want "main of the sandbox did not move" "$(live_main_sha)" = "$MAIN_BEFORE"

# --- the verdict per scenario -----------------------------------------------------------------------

phase "verdict"
CURRENT_PHASE=live

for f in $SELECTED; do
    id=$(field "$f" 1)
    what=$(field "$f" 2)
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
        pass "$id $what" "$passes assertions"
    else
        fail "$id $what" "${detail:0:900}"
    fi
done

phase "summary"
jq -rs '
  (map(select(.phase == "live"))) as $v
  | (map(select(.phase == "sandbox"))) as $s
  | "pass \($v | map(select(.status == "pass")) | length)   "
  + "fail \($v | map(select(.status == "fail")) | length)   "
  + "sandbox " + (if ($s | map(select(.status == "fail")) | length) == 0 then "clean" else "NOT CLEAN" end)
' "$RESULTS"
say "results: $RESULTS (per scenario: $OUT/scenarios/)"

exit "$FAILED"
}

main "$@"
