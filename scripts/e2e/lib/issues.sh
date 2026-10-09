#!/usr/bin/env bash
# The issues suite's driver: what a person does at github.com, and what the agent made of it.
#
# Sourced by scripts/e2e/issues-local.sh; not executable on its own. Every scenario under
# scripts/e2e/issues/ is written against these functions and nothing else.
#
#   ISSUES_API      the agent's HTTP base, for /api (e.g. http://127.0.0.1:18080)
#   ISSUES_STANDIN  the stand-in's base; GitHub is under /github
#                   (infra/e2e/notification-receiver/GitHubStandIn.cs)
#
# TWO HALVES, AND THE SECOND IS BEING BUILT STAGE BY STAGE (v0.14.0, #243).
#
# gh_*  is the person and the witness: it opens, assigns and comments through the stand-in's
#       controls, and reads back what the agent wrote and asked. Built and exercised in #244.
#
# wi_*  reads the AGENT. When this was written the agent had no GitHub client, no work item and
#       no endpoint for one, so everything below it was the best reading of docs/roadmap.md and
#       the plan, kept in this one file so that the stage which builds a thing adjusts it HERE
#       and no scenario has to change. Six things, and where each stands:
#
#   1. BUILT (stage 2.2, #245). GET /api/workitems?state=any&limit=N answers a JSON array of
#      work items, newest first: {id, source, repository: "owner/repo", number, url, title,
#      type, authorLogin, state, stateReason, takenAt, closedAt, body}. `body` is the issue's
#      text when the work item was taken, never updated. Without `state` it lists what is
#      Taken; `state=any` as on /api/incidents; limit is capped at 500. GET /api/workitems/{id}
#      is one of them.
#   2. BUILT. A work item whose issue was unassigned or closed is Cancelled, with why in
#      stateReason, and the same issue assigned again is a SECOND work item (stage 2.2). A work
#      item whose pull request was merged is Done, with "merged"; one whose pull request was
#      closed without merging is Cancelled, with "pull request closed without merging" (stage
#      2.4, #247). The pull request is read before the list of assigned issues, so a merge that
#      closes the issue is Done and never "the issue was closed". An issue that is still open
#      and assigned when its work item ends that way is NOT taken again (`stillAssigned` on the
#      work item): only after one poll in which it was not assigned - or was closed - is the
#      next assignment new work.
#   3. BUILT (stage 2.3, #246). A row of GET /api/codefixes carries `workItemId` where it has no
#      incident (`incidentId` is then null), and keeps the fields it has today: state, summary,
#      files, branch, planJobName, implementJobName, prUrl, prNumber, approvedBy, and
#      failureReason - which is where a rejection's reason is. It also carries `issue`
#      ("owner/repo#12"), `issueUrl` and `planCommentId`: the id of the comment on the issue that
#      holds the plan, null until the poller's next pass has written it. PlanReady is the
#      database's word and the comment follows it by one poll, so issues_plan_ready waits for
#      both. GET /api/workitems/{id} is the work item with `attempts`, newest first.
#      A work item has at most ONE OPEN attempt, and may have several in a row (#252, #285,
#      #286): an approver's `/replan` on the issue, or a fresh assignment once the latest attempt
#      has ended, starts the next one for the SAME work item - never more than
#      ISSUES_ATTEMPT_CAP of them. A row also carries `questions`: what the planner asks of a
#      person, beside `notes`. The request of an attempt after the first carries `previous`
#      (the earlier plan's summary, questions and steps) and, under `work_item.comments`, what
#      the issue's author and the approvers wrote since the issue was handed over.
#   4. BUILT (stage 2.3). A Job and its request ConfigMap keep the labels hephaisto.dev/attempt
#      and hephaisto.dev/phase (and carry hephaisto.dev/work-item in place of
#      hephaisto.dev/incident), and the request is still <job>-req, key request.json - contract
#      version 2, with the issue under `work_item` and nothing of an incident beside it.
#   5. BUILT (stage 2.2). GitHub is a row named "github" in GET /api/status `.connections`:
#      Healthy when the last poll of every listed repository was answered (a 304 is an
#      answer), Degraded with one line of why otherwise, NotConfigured with GitHub off or the
#      agent Off. The row is what the poller last saw, served from the status page's cache -
#      so it follows GitHub by up to a minute, which is why a scenario waits for it.
#   6. BUILT, and not as assumed (stage 2.4, #247). The pull request's body is `prBody` on the
#      attempt - a row of GET /api/codefixes, and `attempts` of GET /api/workitems/{id}. The
#      publish role prints the description it sent in a block of its own before the result
#      (---HEPHAISTO-PR-BODY-BEGIN ...), the agent keeps it with the attempt, and
#      issues_pr_body reads it there: the Job's pod and the `gh` shim's copy are gone when a
#      scenario comes to look. Null for an attempt without a pull request.
#
# What the bot writes on an issue: ONE status comment per work item, edited in place (it ends
# with <!-- hephaisto:status:<work item id> -->), and ONE comment per attempt with its plan,
# never edited (<!-- hephaisto:plan:<attempt id> -->), which says how to answer. The plan
# comment shows the planner's questions as a numbered list under **Questions** and its notes in
# a <details> block; the status comment of an attempt that did not work shows them too.
#
# How a plan is answered, since stage 2.4 (#247): a comment whose FIRST NON-BLANK LINE is exactly
# `/approve`, or `/reject` alone or followed by a reason, written by an account whose NUMBER is
# in the install's github.approvers (ISSUES_APPROVER_ID here). The first such comment after the
# plan decides; a comment is looked at once and never again, whatever it is edited into.
#
# And `/replan` (#252): the same grammar as `/approve` - alone on the first non-blank line - and
# the same people. It is ACCEPTED while the latest attempt's plan is waiting (that attempt is
# then Denied, with the reason "replanned by github:<login>") or once the latest attempt has
# ended without a pull request (Failed, Denied, Expired, Cancelled), and it starts a new attempt
# for the same work item. It is REFUSED while a Job is running for the issue and after a pull
# request was opened. Whatever else the command's comment says is an answer like any other
# comment.
#
# Besides the two comments above the agent writes, each ending in
# <!-- hephaisto:answer:<attempt id>:<key> -->:
#   - at most ONE per attempt to people who are not approvers (key not-approver). It names the
#     first of them by login, in a code span, and nobody else;
#   - at most ONE per attempt and cause when an approver's command is refused: mode-plan,
#     mode-off, emergency-stop, kill-switch, second-repository, not-waiting, taken-back, and for
#     /replan job-running, pull-request, attempts.
# And never more than ISSUES_ATTEMPT_COMMENT_CAP comments for one attempt, whatever anybody
# does - so, with the one status comment and at most ISSUES_ATTEMPT_CAP attempts, never more than
# ISSUES_COMMENT_CAP on one work item. A refusal does not use the plan up: when its cause is
# gone, a NEW /approve is acted on. The API door
# (POST /api/workitems/{id}/codefix/{attemptId}/approve|deny) is as it was.
#
# A fresh assignment (#285): for a work item whose latest attempt has ended without a pull
# request the agent reads GET /repos/{o}/{r}/issues/{n}/timeline, and an `assigned` event for
# the bot that is newer than that attempt's end is a new hand-over - a new attempt, as /replan
# without answers. A waiting plan is left alone, and for one the timeline is not read at all.
#
# shellcheck disable=SC2034

# The repository the agent is told to work in, and one it is not. The first has to be one the
# in-cluster git server holds (infra/coder/git-server), because a plan clones it.
ISSUES_REPO="${ISSUES_REPO:-Flou21/hephaisto-fixture-dotnet}"
ISSUES_FOREIGN_REPO="${ISSUES_FOREIGN_REPO:-Flou21/not-listed}"

# Three people. An account is a login AND a number, and only the number is for ever: the agent's
# approver list names numbers, so the install under test has to name ISSUES_APPROVER_ID.
ISSUES_AUTHOR="${ISSUES_AUTHOR:-reporter}";     ISSUES_AUTHOR_ID="${ISSUES_AUTHOR_ID:-3003}"
ISSUES_APPROVER="${ISSUES_APPROVER:-maintainer}"; ISSUES_APPROVER_ID="${ISSUES_APPROVER_ID:-1001}"
ISSUES_OUTSIDER="${ISSUES_OUTSIDER:-passerby}";  ISSUES_OUTSIDER_ID="${ISSUES_OUTSIDER_ID:-2002}"

# The account issues are assigned to. The runner reads it from the stand-in.
ISSUES_BOT="${ISSUES_BOT:-hephaisto-bot}"

# How many comments the agent may ever write: for one attempt (its plan and its one-time
# answers), how many attempts one work item may have, and so - with the one status comment - for
# one work item. Not settings: they are IssueComments.MaxPerAttempt, MaxAttemptsPerWorkItem and
# MaxPerWorkItem (src/Hephaisto.Agent/WorkItems/IssueComments.cs), and a unit test
# (IssuesSuiteTests) fails when a number here and its number there differ.
ISSUES_ATTEMPT_COMMENT_CAP="${ISSUES_ATTEMPT_COMMENT_CAP:-5}"
ISSUES_ATTEMPT_CAP="${ISSUES_ATTEMPT_CAP:-5}"
ISSUES_COMMENT_CAP="${ISSUES_COMMENT_CAP:-26}"

ISSUES_NS="${ISSUES_NS:-hephaisto}"
ISSUES_DEPLOY="${ISSUES_DEPLOY:-hephaisto}"
ISSUES_CODER_NS="${ISSUES_CODER_NS:-hephaisto-coder}"
ISSUES_SWITCHES_CM="${ISSUES_SWITCHES_CM:-hephaisto-switches}"

# Waits are ceilings, never sizes: a scenario asserts an order of events and gives each one this
# long to happen. A plan is a Job that clones and restores; an implementation builds and tests.
ISSUES_SEEN_WAIT="${ISSUES_SEEN_WAIT:-180}"
ISSUES_PLAN_WAIT="${ISSUES_PLAN_WAIT:-900}"
ISSUES_IMPLEMENT_WAIT="${ISSUES_IMPLEMENT_WAIT:-1800}"

WI_DONE="${WI_DONE:-Done}"
WI_CANCELLED="${WI_CANCELLED:-Cancelled}"

# Short and unique per run, and in every issue's title: the stand-in forgets nothing between
# runs on purpose, and a person reading it can tell whose an issue is.
ISSUES_RUN="${ISSUES_RUN:-$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')}"

issues_title() { printf '[%s %s] %s' "$1" "$ISSUES_RUN" "$2"; }

_issues_curl() { curl -sS --max-time "${ISSUES_TIMEOUT:-15}" "$@"; }

# ---------------------------------------------------------------------------------------
# The person: the stand-in's controls
# ---------------------------------------------------------------------------------------

#   _gh_control <METHOD> <path under /github/control> [json]
_gh_control() {
    local method="$1" path="$2" body="${3:-}"
    if [ -n "$body" ]; then
        _issues_curl -X "$method" "$ISSUES_STANDIN/github/control$path" -H 'Content-Type: application/json' --data "$body"
    else
        _issues_curl -X "$method" "$ISSUES_STANDIN/github/control$path"
    fi
}

# Opens an issue and prints its number. Nobody is assigned yet.
#   gh_issue_create <owner/repo> <title> [body] [login] [id]
gh_issue_create() {
    local repo="$1" title="$2" body="${3:-}" login="${4:-$ISSUES_AUTHOR}" id="${5:-$ISSUES_AUTHOR_ID}"
    _gh_control POST "/repos/$repo/issues" \
        "$(jq -cn --arg t "$title" --arg b "$body" --arg l "$login" --argjson i "$id" \
            '{title:$t, body:(if $b == "" then null else $b end), login:$l, id:$i}')" \
        | jq -er '.number'
}

gh_assign()   { _gh_control POST "/repos/$1/issues/$2/assign" >/dev/null; }
gh_unassign() { _gh_control POST "/repos/$1/issues/$2/unassign" >/dev/null; }

# Off and on again in ONE request, so that no poll can fall between the two: what a person does
# in a few seconds, and what a poll a minute apart never sees (#285). The issue's timeline gets
# an `unassigned` and an `assigned` event, as on GitHub. Fails when the stand-in does not know
# the control - a pod that predates it.
gh_reassign() { _gh_control POST "/repos/$1/issues/$2/reassign" | jq -e '.number' >/dev/null; }

#   gh_issue_edit <owner/repo> <number> <new body>
gh_issue_edit()   { _gh_control PATCH "/repos/$1/issues/$2" "$(jq -cn --arg b "$3" '{body:$b}')" >/dev/null; }
gh_issue_close()  { _gh_control PATCH "/repos/$1/issues/$2" '{"state":"closed"}' >/dev/null; }
gh_issue_reopen() { _gh_control PATCH "/repos/$1/issues/$2" '{"state":"open"}' >/dev/null; }

# A comment as a named account. Prints the comment's id.
#   gh_comment_as <owner/repo> <number> <login> <id> <body>
gh_comment_as() {
    _gh_control POST "/repos/$1/issues/$2/comments" \
        "$(jq -cn --arg l "$3" --argjson i "$4" --arg b "$5" '{login:$l, id:$i, body:$b}')" \
        | jq -er '.id'
}

# A reaction on a comment as a named account (#298): an approver's rocket on a plan, a
# stranger's thumbs-up. Prints the reaction's id; one that is there already prints its own.
#   gh_react_as <owner/repo> <comment-id> <login> <id> <content>      content: GitHub's word,
#                                                                     rocket, -1, +1, heart, ...
gh_react_as() {
    _gh_control POST "/repos/$1/issues/comments/$2/reactions" \
        "$(jq -cn --arg l "$3" --argjson i "$4" --arg c "$5" '{login:$l, id:$i, content:$c}')" \
        | jq -er '.id'
}

# Somebody takes a reaction off again.
#   gh_unreact <owner/repo> <comment-id> <reaction-id>
gh_unreact() { _gh_control DELETE "/repos/$1/issues/comments/$2/reactions/$3" >/dev/null; }

# What became of a pull request. The `gh` that opens one in a coder Job is a script with no
# network, so the stand-in has never heard of it and answers "an open draft" until told.
#   gh_pr_merge <owner/repo> <number>       gh_pr_close <owner/repo> <number>
gh_pr_merge() { _gh_control PUT "/repos/$1/pulls/$2" '{"merged":true}' >/dev/null; }
gh_pr_close() { _gh_control PUT "/repos/$1/pulls/$2" '{"state":"closed"}' >/dev/null; }

# Back to "an open draft". The `gh` shim numbers pull requests from 1 in every Job, so pull
# request 1 of the repository is a different one in every scenario: one that was merged here has
# to be forgotten, or the next scenario's pull request is found merged the moment it is opened.
#   gh_pr_forget <owner/repo> <number>
gh_pr_forget() { _gh_control DELETE "/repos/$1/pulls/$2" >/dev/null; }

# The next <count> API calls answer 500, or a rate-limit 403; `off` ends it early.
#   gh_fail <500|rate-limit|off> [count]
gh_fail() { _gh_control POST "/fail/$1?count=${2:-1}" >/dev/null; }

# ---------------------------------------------------------------------------------------
# The witness: what the agent wrote and asked
# ---------------------------------------------------------------------------------------

# Every comment on an issue, oldest first: [{id, body, user:{login,id}, edits, created_at, ...}]
#   gh_comments <owner/repo> <number>
gh_comments() {
    _gh_control GET /comments \
        | jq -c --arg r "$1" --argjson n "$2" '[.[] | select(.repository == $r and .number == $n)]'
}

# The agent's own. By the account's NUMBER when the runner knows it, as everything else here is.
gh_bot_comments() { gh_comments "$1" "$2" | jq -c --arg b "$ISSUES_BOT" '[.[] | select(.user.login == $b)]'; }

# How many of the agent's comments on an issue match a regex (jq's, case-insensitive).
#   gh_bot_said <owner/repo> <number> <regex>
gh_bot_said() { gh_bot_comments "$1" "$2" | jq --arg re "$3" '[.[] | select(.body | test($re; "i"))] | length'; }

# The reactions on one comment of an issue, oldest first: [{id, content, user:{login,id}}]
#   gh_reactions <owner/repo> <number> <comment-id>
gh_reactions() {
    gh_comments "$1" "$2" | jq -c --argjson c "$3" '[.[] | select(.id == $c)][0].reactions // []'
}

# Every API request the stand-in has kept, oldest first:
# [{seq, method, path, query, conditional, status, at}] - path without the /github/api prefix.
gh_requests() { _gh_control GET /requests; }

# "From here on". A number of the stand-in's own, so neither a clock nor the 2000 requests it
# keeps can make "since" mean something else.
gh_mark() { gh_requests | jq '(last // {seq: 0}).seq'; }

#   gh_requests_since <mark>
gh_requests_since() { gh_requests | jq -c --argjson m "$1" '[.[] | select(.seq > $m)]'; }

# How often the agent has looked at a repository's issues since a mark, and been answered.
#   gh_polls_since <mark> [owner/repo]
gh_polls_since() {
    gh_requests_since "$1" | jq --arg p "/repos/${2:-$ISSUES_REPO}/issues" \
        '[.[] | select(.method == "GET" and .path == $p and (.status == 200 or .status == 304))] | length'
}

# How often it has read an issue's comments since a mark.
#   gh_comment_reads_since <mark> <owner/repo> <number>
gh_comment_reads_since() {
    gh_requests_since "$1" | jq --arg p "/repos/$2/issues/$3/comments" \
        '[.[] | select(.method == "GET" and .path == $p and (.status == 200 or .status == 304))] | length'
}

# How often it has read the reactions on a comment since a mark - which it does only for the
# comment of a plan that is waiting.
#   gh_reaction_reads_since <mark> <owner/repo> <comment-id>
gh_reaction_reads_since() {
    gh_requests_since "$1" | jq --arg p "/repos/$2/issues/comments/$3/reactions" \
        '[.[] | select(.method == "GET" and .path == $p and (.status == 200 or .status == 304))] | length'
}

# How often it has read an issue's timeline since a mark - which it does only for a work item
# whose latest attempt has ended.
#   gh_timeline_reads_since <mark> <owner/repo> <number>
gh_timeline_reads_since() {
    gh_requests_since "$1" | jq --arg p "/repos/$2/issues/$3/timeline" \
        '[.[] | select(.method == "GET" and .path == $p and (.status == 200 or .status == 304))] | length'
}

# The text of the comment that carries an attempt's plan, and of the one status comment of a
# work item: found by their markers, among the bot's own comments. Empty when there is none.
#   issues_plan_comment <owner/repo> <number> <attempt-id>
#   issues_status_comment <owner/repo> <number> <work-item-id>
issues_plan_comment() {
    gh_bot_comments "$1" "$2" | jq -r --arg m "hephaisto:plan:$(printf '%s' "$3" | tr -d '-') " '[.[] | select(.body | contains($m))][0].body // empty'
}
# The id of the comment that carries an attempt's plan: where a reaction is an answer.
#   issues_plan_comment_id <owner/repo> <number> <attempt-id>
issues_plan_comment_id() {
    gh_bot_comments "$1" "$2" | jq -r --arg m "hephaisto:plan:$(printf '%s' "$3" | tr -d '-') " '[.[] | select(.body | contains($m))][0].id // empty'
}
issues_status_comment() {
    gh_bot_comments "$1" "$2" | jq -r --arg m "hephaisto:status:$(printf '%s' "$3" | tr -d '-') " '[.[] | select(.body | contains($m))][0].body // empty'
}

# "Nothing happened" is only worth asserting once the agent has had the chance: these wait
# for it to have looked <count> more times, however long its interval is.
#   gh_wait_polls <mark> <count> [timeout] [owner/repo]
gh_wait_polls() {
    local mark="$1" count="$2" timeout="${3:-$ISSUES_SEEN_WAIT}" repo="${4:-$ISSUES_REPO}"
    _gh_polled() { [ "$(gh_polls_since "$mark" "$repo")" -ge "$count" ]; }
    wait_for "$count more poll(s) of $repo" "$timeout" _gh_polled
}

#   gh_wait_comment_reads <mark> <count> <owner/repo> <number> [timeout]
gh_wait_comment_reads() {
    local mark="$1" count="$2" repo="$3" number="$4" timeout="${5:-$ISSUES_SEEN_WAIT}"
    _gh_read() { [ "$(gh_comment_reads_since "$mark" "$repo" "$number")" -ge "$count" ]; }
    wait_for "$count more read(s) of the comments on $repo#$number" "$timeout" _gh_read
}

#   gh_wait_reaction_reads <mark> <count> <owner/repo> <comment-id> [timeout]
gh_wait_reaction_reads() {
    local mark="$1" count="$2" repo="$3" comment="$4" timeout="${5:-$ISSUES_SEEN_WAIT}"
    _gh_reacted() { [ "$(gh_reaction_reads_since "$mark" "$repo" "$comment")" -ge "$count" ]; }
    wait_for "$count more read(s) of the reactions on comment $comment of $repo" "$timeout" _gh_reacted
}

# ---------------------------------------------------------------------------------------
# The agent: work items and their attempts - see the header.
# ---------------------------------------------------------------------------------------

# The first line of every scenario: an agent that serves no work items cannot pass one, and
# says so in a second rather than after every wait below has run out.
issues_ready() {
    local out code
    out=$(_issues_curl -w '\n%{http_code}' "$ISSUES_API/api/workitems?state=any&limit=1" 2>/dev/null) || out=$'\n000'
    code=$(tail -1 <<<"$out")
    if [ "$code" = 200 ] && [ "$(sed '$d' <<<"$out" | jq -r type 2>/dev/null)" = "array" ]; then
        return 0
    fi
    fail "the agent serves work items" "GET /api/workitems answered $code - it does not know a GitHub issue as work"
    return 1
}

# Every work item, in any state. Prints [] and fails on a body that is not an array - an error
# document read as data counts its keys (lib/common.sh, api_array).
wi_all() {
    local body
    body=$(_issues_curl "$ISSUES_API/api/workitems?state=any&limit=200") || body=""
    if [ "$(jq -r type <<<"$body" 2>/dev/null)" != "array" ]; then
        printf 'GET /api/workitems returned: %s\n' "${body:0:300}" >&2
        echo '[]'
        return 1
    fi
    printf '%s' "$body"
}

# The work items of one issue. More than one is a finding, which is why this is a list.
#   wi_for <owner/repo> <number>
wi_for()   { wi_all | jq -c --arg r "$1" --argjson n "$2" '[.[] | select(.repository == $r and .number == $n)]'; }
wi_count() { wi_for "$1" "$2" | jq 'length'; }
wi_id()    { wi_for "$1" "$2" | jq -r '.[0].id // empty'; }
wi_state() { wi_for "$1" "$2" | jq -r '.[0].state // empty'; }

wi_exists() { [ -n "$(wi_id "$1" "$2" 2>/dev/null)" ]; }

#   wi_wait <owner/repo> <number> [timeout]
wi_wait() { wait_for "$1#$2 to become a work item" "${3:-$ISSUES_SEEN_WAIT}" wi_exists "$1" "$2"; }

#   wi_wait_state <owner/repo> <number> <timeout> <state> [state ...]
wi_wait_state() {
    local repo="$1" number="$2" timeout="$3"; shift 3
    _wi_in() { local s w; s=$(wi_state "$repo" "$number"); for w in "$@"; do [ "$s" = "$w" ] && return 0; done; return 1; }
    wait_for "the work item of $repo#$number to be $*" "$timeout" _wi_in "$@"
}

# The attempts of a work item, oldest first; and its newest.
#   wi_attempts <work-item-id>
wi_attempts() {
    _issues_curl "$ISSUES_API/api/codefixes?limit=200" \
        | jq -c --arg w "$1" '[.[] | select((.workItemId // "") == $w)] | sort_by(.createdAt)'
}
wi_attempt()       { wi_attempts "$1" | jq -c 'last // {}'; }
wi_attempt_count() { wi_attempts "$1" | jq 'length'; }
wi_attempt_state() { wi_attempt "$1" | jq -r '.state // empty'; }

wi_attempt_in() {
    local id="$1" s w; shift
    s=$(wi_attempt_state "$id")
    for w in "$@"; do [ "$s" = "$w" ] && return 0; done
    return 1
}

#   wi_wait_attempt <work-item-id> <timeout> <state> [state ...]
#
# A plan that is waiting moves when somebody answers it on the issue, and the agent learns of an
# answer only by reading the issue's comments. An agent that reads none cannot make a wait on a
# waiting plan come true - which is what one is between stage 2.3 and stage 2.4 - and the ceiling
# of an implementation is half an hour. So a wait that long, on a plan that is waiting - and
# for something other than that - is first a short one: for the plan to have moved, or for the
# agent to have read the comments at all.
wi_wait_attempt() {
    local id="$1" timeout="$2"; shift 2
    if [ "$timeout" -gt "$ISSUES_SEEN_WAIT" ] && ! wi_attempt_in "$id" "$@" && wi_attempt_in "$id" PlanReady; then
        local item repo number mark
        item=$(_issues_curl "$ISSUES_API/api/workitems/$id")
        repo=$(jq -r '.repository // empty' <<<"$item"); number=$(jq -r '.number // empty' <<<"$item")
        mark=$(gh_mark)
        _wi_answerable() {
            ! wi_attempt_in "$id" PlanReady || [ "$(gh_comment_reads_since "$mark" "$repo" "${number:-0}")" -ge 1 ]
        }
        wait_for "the agent to read what was answered on $repo#$number" "$ISSUES_SEEN_WAIT" _wi_answerable || return 1
    fi
    wait_for "the attempt of work item $id to be $*" "$timeout" wi_attempt_in "$id" "$@"
}

# The road every scenario but two starts on: the issue is a work item, and its plan is ready.
# Sets WI (the work item's id) and ATTEMPT (the attempt, as /api/codefixes shows it); records
# both steps, and returns non-zero when either did not happen.
#   issues_plan_ready <owner/repo> <number>
issues_plan_ready() {
    local repo="$1" number="$2"
    WI=""; ATTEMPT="{}"

    wi_wait "$repo" "$number" || { fail "the assigned issue became a work item" "none for $repo#$number within ${ISSUES_SEEN_WAIT}s"; return 1; }
    pass "the assigned issue became a work item"
    WI=$(wi_id "$repo" "$number")

    # An attempt is a row before it is a Job, so whether there will be one is known in seconds.
    # Without this, an agent that takes an issue and plans nothing for it - which is what one
    # is between stage 2.2 and stage 2.3 - is waited on for the whole of a plan's ceiling, in
    # every scenario that starts on this road.
    _wi_has_attempt() { [ "$(wi_attempt_count "$WI")" -ge 1 ]; }
    wait_for "the work item to get an attempt" "$ISSUES_SEEN_WAIT" _wi_has_attempt \
        || { fail "its plan is ready" "no attempt for the work item within ${ISSUES_SEEN_WAIT}s - nothing started a plan for it"; return 1; }

    wi_wait_attempt "$WI" "$ISSUES_PLAN_WAIT" PlanReady Failed Cancelled Denied Expired || true
    ATTEMPT=$(wi_attempt "$WI")
    if [ "$(jq -r '.state // empty' <<<"$ATTEMPT")" = PlanReady ]; then
        pass "its plan is ready"
    else
        fail "its plan is ready" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$ATTEMPT")"
        return 1
    fi

    # PlanReady is the database's word, written by the loop that collects a Job; the comment on
    # the issue is written by the poller's next pass, and the attempt names it (planCommentId)
    # once it is there and the status comment says so too. Every scenario that starts on this
    # road reads the issue next, so the road ends where the issue has been told.
    _wi_plan_posted() { [ -n "$(wi_attempt "$WI" | jq -r '.planCommentId // empty')" ]; }
    if wait_for "the plan to be written on the issue" "$ISSUES_SEEN_WAIT" _wi_plan_posted; then
        pass "the plan is on the issue"
        ATTEMPT=$(wi_attempt "$WI")
    else
        fail "the plan is on the issue" "the attempt names no plan comment within ${ISSUES_SEEN_WAIT}s"
        return 1
    fi
}

# The road of a SECOND plan: the work item has <count> attempts, the newest is PlanReady, and
# its plan is on the issue. Sets ATTEMPT to that newest attempt; records each step, and returns
# non-zero when one did not happen.
#   issues_next_plan_ready <work-item-id> <count>
issues_next_plan_ready() {
    local wi="$1" count="$2"
    ATTEMPT="{}"

    _wi_attempts_ge() { [ "$(wi_attempt_count "$wi")" -ge "$count" ]; }
    wait_for "attempt $count of the work item" "$ISSUES_SEEN_WAIT" _wi_attempts_ge \
        || { fail "a new attempt was started for the work item" "it has $(wi_attempt_count "$wi") attempt(s) after ${ISSUES_SEEN_WAIT}s"; return 1; }
    pass "a new attempt was started for the work item"

    wi_wait_attempt "$wi" "$ISSUES_PLAN_WAIT" PlanReady Failed Cancelled Denied Expired || true
    ATTEMPT=$(wi_attempt "$wi")
    if [ "$(jq -r '.state // empty' <<<"$ATTEMPT")" = PlanReady ]; then
        pass "its plan is ready"
    else
        fail "its plan is ready" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$ATTEMPT")"
        return 1
    fi

    _wi_next_posted() { [ -n "$(wi_attempt "$wi" | jq -r '.planCommentId // empty')" ]; }
    if wait_for "the new plan to be written on the issue" "$ISSUES_SEEN_WAIT" _wi_next_posted; then
        pass "the new plan is on the issue"
        ATTEMPT=$(wi_attempt "$wi")
    else
        fail "the new plan is on the issue" "the attempt names no plan comment within ${ISSUES_SEEN_WAIT}s"
        return 1
    fi
}

# An issue that stays open and assigned keeps its work item, and a work item with a plan or a
# Job keeps one of the few slots the dev values allow (codeFix.budgets.concurrentJobs is 1).
# Every scenario ends its own.
#   issues_done <owner/repo> <number> [number ...]
issues_done() {
    local repo="$1" n; shift
    for n in "$@"; do
        [ -n "$n" ] && gh_issue_close "$repo" "$n" 2>/dev/null
    done
    return 0
}

# ---------------------------------------------------------------------------------------
# Who else is told (stage 2.5, #248): the outbox, as the Teams stand-in received it
# ---------------------------------------------------------------------------------------

# Whether the install routes an event to the Teams bot, read off the agent's Deployment the way
# the runner reads which GitHub it talks to. The dev values' one route lists the three code-fix
# events and is not scoped, so with "teams-bot": "stand-in" a work item's plan is announced; an
# install without that route announces nothing, and a scenario says so instead of failing.
#   issues_route_takes <event>
issues_route_takes() {
    kc -n "$ISSUES_NS" get deploy "$ISSUES_DEPLOY" -o json 2>/dev/null \
        | jq -e --arg e "$1" '
            [.spec.template.spec.containers[0].env[]? | select(.name | test("^Notifications__Routes__[0-9]+__"))] as $r
            | [$r[] | select(.name | test("__Channel$")) | select(.value == "teamsBot") | (.name | capture("Routes__(?<i>[0-9]+)__").i)] as $bot
            | any($r[]; (.name | test("__Events__[0-9]+$")) and .value == $e
                and ((.name | capture("Routes__(?<i>[0-9]+)__").i) as $i | $bot | index($i)))' >/dev/null
}

# Personal-chat messages the Teams stand-in received that name an attempt: a work item's card
# links the attempt's own page, /codefixes/<attempt id>. The board is kind "channel".
#   issues_teams_alerts <attempt-id>   -> array of {id, kind, conversation, summary, text}
issues_teams_alerts() {
    _issues_curl "$ISSUES_STANDIN/teams/messages" \
        | jq -c --arg id "$1" '[.messages[] | select(.kind == "chat") | select((.activity | tostring) | contains("/codefixes/" + $id))
            | {id, kind, conversation, summary, text: (.activity | tostring)}]'
}

# The board's cards that mention an attempt or an issue: none, ever - the board is of incidents.
#   issues_teams_board_mentions <text>
issues_teams_board_mentions() {
    _issues_curl "$ISSUES_STANDIN/teams/messages" \
        | jq --arg t "$1" '[.messages[] | select(.kind == "channel") | select((.activity | tostring) | contains($t))] | length'
}

# ---------------------------------------------------------------------------------------
# The cluster, through common.sh's kc and nothing else
# ---------------------------------------------------------------------------------------

# Jobs the agent started for an attempt, by phase. The Job's name is derived from the attempt,
# so "two" cannot happen for one attempt - which is why a scenario counts work items and
# attempts as well, and this only says the one that should exist does.
#   issues_job_count <attempt-id> <plan|implement>
issues_job_count() {
    kc -n "$ISSUES_CODER_NS" get jobs -l "hephaisto.dev/attempt=$1,hephaisto.dev/phase=$2" -o json 2>/dev/null \
        | jq '.items | length'
}

issues_job_exists() { kc -n "$ISSUES_CODER_NS" get job "$1" >/dev/null 2>&1; }

# Whether a pod of the Job is still there to do anything: a cancelled Job is deleted, and a
# deleted Job's pod may take its grace period to go.
issues_job_running() {
    [ "$(kc -n "$ISSUES_CODER_NS" get pods -l "job-name=$1" -o json 2>/dev/null \
        | jq '[.items[] | select(.status.phase == "Running" or .status.phase == "Pending")] | length')" -gt 0 ]
}

# What a Job was told: its request, as the agent wrote it into the Job's ConfigMap.
#   issues_request <job-name>
issues_request() {
    kc -n "$ISSUES_CODER_NS" get configmap "$1-req" -o jsonpath='{.data.request\.json}' 2>/dev/null
}

# The body of the pull request an implementing Job opened, as the agent kept it with the
# attempt (header, 6). Empty when the attempt has none.
#   issues_pr_body <implement-job-name>
issues_pr_body() {
    _issues_curl "$ISSUES_API/api/codefixes?limit=200" \
        | jq -r --arg j "$1" '[.[] | select((.implementJobName // "") == $j)][0].prBody // empty'
}

# git over the in-cluster server, from inside it: no port-forward, no credential.
#   issues_git_rev <branch> [owner/repo]
issues_git_rev() {
    kc -n "$ISSUES_CODER_NS" exec deploy/coder-git -- \
        git --git-dir="/srv/git/${2:-$ISSUES_REPO}.git" rev-parse --verify --quiet "refs/heads/$1" 2>/dev/null || true
}

# GitHub, as the agent's own status page reports it: Healthy, Degraded, NotConfigured - or
# nothing, when it has no such row. Up to a minute behind the poller (header, 5).
issues_github_health() {
    _issues_curl "$ISSUES_API/api/status" | jq -r '[.connections[]? | select(.name == "github")][0].state // empty'
}

issues_github_is() {
    local s w
    s=$(issues_github_health 2>/dev/null)
    for w in "$@"; do [ "$s" = "$w" ] && return 0; done
    return 1
}

# How often the agent's container has been restarted, and which pod it is: a crash shows as
# the first going up, a replaced pod as the second changing.
issues_agent_restarts() {
    kc -n "$ISSUES_NS" get pods -l "app.kubernetes.io/name=$ISSUES_DEPLOY" -o json \
        | jq -r '[.items[] | select(.metadata.deletionTimestamp == null)]
                 | "\(.[0].metadata.name // "none") \([.[].status.containerStatuses[]?.restartCount] | add // 0)"'
}

issues_agent_up() { curl -sf --max-time 5 "$ISSUES_API/healthz" >/dev/null 2>&1; }

# The code-fix switch, as lib/codefix.sh turns it: the ConfigMap key is the one arm a test can
# lower without a rollout. The value a scenario found is the value it puts back.
issues_switch_get() {
    kc -n "$ISSUES_NS" get configmap "$ISSUES_SWITCHES_CM" -o jsonpath='{.data.codeFixMode}' 2>/dev/null
}

issues_switch_set() {
    kc -n "$ISSUES_NS" patch configmap "$ISSUES_SWITCHES_CM" --type merge \
        -p "$(jq -cn --arg m "$1" '{data:{codeFixMode:$m}}')" >/dev/null
}

issues_mode_is() { [ "$(_issues_curl "$ISSUES_API/api/codefixes/mode" | jq -r '.effective')" = "$1" ]; }
