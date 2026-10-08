#!/usr/bin/env bash
# The live tier's driver: what a person does at github.com - the REAL one - and what the agent
# made of it.
#
# Sourced by scripts/e2e/github-live.sh after lib/common.sh; not executable on its own.
#
# It is lib/issues.sh with its first half replaced. That file has two halves: gh_* is the
# person and the witness, played there through the stand-in's controls; wi_* and issues_* read
# the agent. The agent's half is the same agent here, so it is sourced and used as it is. The
# person's half is redefined below with the `gh` CLI of whoever runs the suite, against ONE
# repository:
#
#   LIVE_REPO   TrueRelevance/hephaisto-sandbox. A constant, not a setting: every write this
#               file can make goes through _live_api or _live_gh, which put that name into the
#               request themselves. Nothing here takes a repository from its caller - the
#               <owner/repo> arguments the issues suite's functions carry are compared with the
#               constant and refused when they differ.
#   LIVE_BOT    the account issues are assigned to (the agent's token is its token)
#
# What the stand-in could do and GitHub cannot is not faked: failing the API on demand, the
# request log, merging without changing a branch, editing an issue as somebody else. Those
# functions refuse loudly here (_live_no), so a scenario copied from the issues suite fails at
# the line that assumed a stand-in.
#
# Politeness. Every wait in a scenario is on the AGENT's API (wi_*), and GitHub is read once
# the agent says something happened; the few waits that have to watch GitHub itself probe every
# five seconds for at most a few minutes, with REST reads that cost one request each.
#
# shellcheck disable=SC2034

# shellcheck source=issues.sh
source "$(dirname "${BASH_SOURCE[0]}")/issues.sh"

readonly LIVE_REPO="TrueRelevance/hephaisto-sandbox"
LIVE_BOT="${LIVE_BOT:-tr-agent-dev}"

# The one branch pattern the runner may push (coder/src/pr.ts BRANCH_RE), and the only kind of
# branch this suite ever deletes.
readonly LIVE_BRANCH_RE='^hephaisto/codefix-[0-9a-f]{12}$'

# Every issue this run opens says so in its title, so that a person looking at the sandbox - and
# --sweep - can tell what is the suite's.
readonly LIVE_TITLE_PREFIX='[hephaisto-live'

# The issues suite's names, for the functions sourced from it.
ISSUES_REPO="$LIVE_REPO"
ISSUES_BOT="$LIVE_BOT"

# The agent polls every twenty seconds here, not every five, and GitHub is further away than a
# pod: "seen" gets five minutes. The ceilings of a plan and an implementation stay the issues
# suite's.
ISSUES_SEEN_WAIT="${LIVE_SEEN_WAIT:-300}"

live_title() { printf '%s %s %s] %s' "$LIVE_TITLE_PREFIX" "$1" "$ISSUES_RUN" "$2"; }

# ---------------------------------------------------------------------------------------
# The two doors to GitHub
# ---------------------------------------------------------------------------------------

_live_repo_is() {
    [ "$1" = "$LIVE_REPO" ] && return 0
    printf 'refusing: %s is not %s, the only repository the live tier touches\n' "$1" "$LIVE_REPO" >&2
    return 1
}

# REST, under the sandbox and nowhere else.
#   _live_api <METHOD> <path under repos/$LIVE_REPO/, or "" for the repository> [gh api arguments ...]
_live_api() {
    local method="$1" path="$2"; shift 2
    case "$path" in
        /*|*..*) printf 'refusing: %s is not a path under the sandbox\n' "$path" >&2; return 1 ;;
    esac
    gh api --method "$method" -H 'X-GitHub-Api-Version: 2022-11-28' "repos/$LIVE_REPO${path:+/$path}" "$@"
}

# The CLI's own commands (issue, pr), with the repository named here.
#   _live_gh issue create --title ... ; _live_gh pr view 12 --json ...
_live_gh() {
    local noun="$1" verb="$2"; shift 2
    gh "$noun" "$verb" --repo "$LIVE_REPO" "$@"
}

_live_no() {
    printf 'not on real GitHub: %s is a control of the stand-in (scripts/e2e/lib/issues.sh)\n' "$1" >&2
    return 1
}

# ---------------------------------------------------------------------------------------
# The person
# ---------------------------------------------------------------------------------------

# Opens an issue as whoever `gh` is logged in as, and prints its number. Nobody is assigned.
# The number is also written down ($LIVE_CREATED), which is what the cleanup closes.
#   gh_issue_create <owner/repo> <title> [body] [label]
gh_issue_create() {
    _live_repo_is "$1" || return 1
    local url n
    if [ -n "${4:-}" ]; then
        url=$(_live_gh issue create --title "$2" --body "${3:-}" --label "$4") || return 1
    else
        url=$(_live_gh issue create --title "$2" --body "${3:-}") || return 1
    fi
    n="${url##*/}"
    case "$n" in ''|*[!0-9]*) printf 'gh issue create printed no issue address: %s\n' "$url" >&2; return 1 ;; esac
    [ -n "${LIVE_CREATED:-}" ] && echo "$n" >> "$LIVE_CREATED"
    echo "$n"
}

# GitHub ignores an assignee it will not accept and answers 201 all the same, so the answer is
# read: assigned means the account is in it.
gh_assign() {
    _live_repo_is "$1" || return 1
    [ "$(_live_api POST "issues/$2/assignees" -f "assignees[]=$LIVE_BOT" \
        | jq --arg b "$LIVE_BOT" '[.assignees[]?.login] | index($b) != null')" = true ]
}

gh_unassign() {
    _live_repo_is "$1" || return 1
    _live_api DELETE "issues/$2/assignees" -f "assignees[]=$LIVE_BOT" >/dev/null
}

# Off and on again, as fast as two requests go - about a second, where the agent polls every
# twenty. On the stand-in this is one control and nothing can fall between; here a poll can, and
# a scenario has to say which road the run took (L05).
gh_reassign() {
    gh_unassign "$1" "$2" && gh_assign "$1" "$2"
}

gh_issue_close() {
    _live_repo_is "$1" || return 1
    _live_api PATCH "issues/$2" -f state=closed >/dev/null
}

# A comment as the person running the suite - there is one human account, so the login and the
# number the issues suite passes are checked against it and otherwise not used. Prints the id.
#   gh_comment_as <owner/repo> <number> <login> <id> <body>
gh_comment_as() {
    _live_repo_is "$1" || return 1
    [ "$3" = "$ISSUES_APPROVER" ] || { printf 'the live tier can only comment as %s, not as %s\n' "$ISSUES_APPROVER" "$3" >&2; return 1; }
    local url
    url=$(_live_gh issue comment "$2" --body "$5") || return 1
    echo "${url##*issuecomment-}"
}

# Closed, and never merged: a merge would move main, and the scripted fix applies to main as
# it is. "Merged is Done" stays the stand-in's to show (issues G11).
#
# Asked a second time when GitHub does not take it, once and after a pause: on the suite's
# second run ever GitHub answered this request with an error and no body ("unexpected end of
# JSON input" is all gh makes of that), in the minute in which it was also late to link the
# pull request to its issue - and took the same request a quarter of an hour later.
gh_pr_close() {
    _live_repo_is "$1" || return 1
    _live_api PATCH "pulls/$2" -f state=closed >/dev/null && return 0
    printf 'GitHub did not close pull request %s; asking once more in 20s\n' "$2" >&2
    sleep 20
    [ "$(_live_api GET "pulls/$2" --jq '.state' 2>/dev/null)" = closed ] || _live_api PATCH "pulls/$2" -f state=closed >/dev/null
}

# The issues GitHub itself says a pull request will close when it is merged, as a sorted JSON
# list of numbers. GitHub works this out AFTER the pull request is saved, by a job of its own:
# seconds as a rule, and minutes on a bad day - so a caller waits for it.
#   live_closing <pr>
live_closing() {
    _live_gh pr view "$1" --json closingIssuesReferences | jq -c '[.closingIssuesReferences[].number] | sort'
}

gh_issue_edit()   { _live_no gh_issue_edit; }
gh_issue_reopen() { _live_no gh_issue_reopen; }
gh_pr_merge()     { _live_no gh_pr_merge; }
gh_pr_forget()    { _live_no gh_pr_forget; }
gh_fail()         { _live_no gh_fail; }
gh_requests()     { _live_no gh_requests; }

# ---------------------------------------------------------------------------------------
# The witness
# ---------------------------------------------------------------------------------------

# Every comment on an issue, oldest first, in GitHub's own shape - which is the shape the
# stand-in copied: [{id, body, user:{login,id}, created_at, updated_at, html_url}]. With "html"
# as a third argument each also carries body_html, as GitHub rendered it.
#   gh_comments <owner/repo> <number> [html]
gh_comments() {
    _live_repo_is "$1" || return 1
    if [ "${3:-}" = html ]; then
        _live_api GET "issues/$2/comments?per_page=100" -H 'Accept: application/vnd.github.full+json'
    else
        _live_api GET "issues/$2/comments?per_page=100"
    fi
}

# One comment, with body_html.
live_comment() { _live_api GET "issues/comments/$1" -H 'Accept: application/vnd.github.full+json'; }

# The commit a branch of the sandbox points at, or nothing.
#   issues_git_rev <branch> [owner/repo]
issues_git_rev() {
    [ -z "${2:-}" ] || _live_repo_is "$2" || return 1
    # gh prints GitHub's "Not Found" document on stdout and fails; only a success is an answer.
    local sha
    sha=$(_live_api GET "git/ref/heads/$1" --jq '.object.sha' 2>/dev/null) && echo "$sha"
    return 0
}

live_main_sha() { issues_git_rev main; }

# The events of an issue's timeline that say "another issue or pull request mentioned this
# one", as [{source: <number>, actor: <login>}].
#   live_cross_references <number>
live_cross_references() {
    _live_api GET "issues/$1/timeline?per_page=100" \
        | jq -c '[.[] | select(.event == "cross-referenced") | {source: .source.issue.number, actor: .actor.login}]'
}

# How often GitHub's rendering of a text made something of it that a reader did not write: a
# mention (which notifies), or a link to an issue or a pull request (which is how a
# cross-reference looks from the page that makes it). Reads HTML on stdin.
live_html_mentions()   { grep -o 'class="user-mention[^"]*"' | wc -l | tr -d ' '; }
#   live_html_links_to <number>     links to that issue or pull request of the sandbox
live_html_links_to()   { grep -oE "href=\"https://github.com/$LIVE_REPO/(issues|pull)/$1\"" | wc -l | tr -d ' '; }

# What GitHub answers to one question asked twice, the second time with the entity tag of the
# first answer: "200 304" when it honours the condition. With the headers the agent's client
# sends (GitHubClient.SendAsync) and the tag handed back as it was given - GitHub's tags for
# most answers are weak (W/"..."), and a rewritten one never matches.
#   live_asked_twice <path under the sandbox>
live_asked_twice() {
    local path="$1" headers etag
    headers=$(_live_api GET "$path" -i -H 'Accept: application/vnd.github+json' 2>/dev/null | tr -d '\r' | sed '/^$/q')
    etag=$(sed -n 's/^[Ee][Tt]ag: //p' <<<"$headers" | head -1)
    [ -n "$etag" ] || { echo "$(head -1 <<<"$headers" | awk '{print $2}') no-etag"; return 0; }
    echo "$(head -1 <<<"$headers" | awk '{print $2}') $(_live_api GET "$path" -i -H 'Accept: application/vnd.github+json' -H "If-None-Match: $etag" 2>/dev/null | head -1 | tr -d '\r' | awk '{print $2}')"
}

# ---------------------------------------------------------------------------------------
# The agent's side of GitHub: its own counters
# ---------------------------------------------------------------------------------------

# hephaisto_github_polls_total by outcome, off the agent's /metrics. 0 when it has none yet.
#   live_polls <outcome>            ok | not_modified | rate_limited | unauthorized | ...
live_polls() {
    _issues_curl "$ISSUES_API/metrics" 2>/dev/null \
        | awk -v want="outcome=\"$1\"" '/^hephaisto_github_polls_total\{/ && index($0, want) { n += $NF } END { printf "%d\n", n + 0 }'
}

# The stand-in keeps a request log, and the issues suite waits on it ("the agent has looked
# twice more"). Here the agent's own counter of answered polls is that log: a pass asks for the
# list once, and reads the comments of every waiting plan in the same pass.
gh_mark() { echo $(( $(live_polls ok) + $(live_polls not_modified) )); }

gh_polls_since() {
    local now
    now=$(gh_mark)
    # A restarted agent counts from zero again.
    [ "$now" -ge "$1" ] && echo $(( now - $1 )) || echo "$now"
}

gh_comment_reads_since() { gh_polls_since "$1"; }

# ---------------------------------------------------------------------------------------
# Somebody else on the sandbox
# ---------------------------------------------------------------------------------------

# The suite's newest issues that ANOTHER Hephaisto took as well: one line per work item,
# "<issue> <work item id>", for every status marker of the bot's on them that the agent under
# test does not know as a work item of its own.
#
# The bot's token is an account's, and any install that holds it and lists the sandbox polls
# the sandbox. On 2026-10-08 production did - with a real model: every issue this suite opened
# was planned there too, for money, and carried a second status comment. Nothing in a preflight
# that looks at the dev agent can see that; what an earlier run left on GitHub can.
#   live_foreign_takers [how many of the newest closed issues to look at]
live_foreign_takers() {
    local n id uuid
    for n in $(_live_api GET "issues?state=closed&sort=created&direction=desc&per_page=${1:-6}" \
            | jq -r --arg p "$LIVE_TITLE_PREFIX" '.[] | select(.pull_request == null) | select(.title | startswith($p)) | .number'); do
        for id in $(_live_api GET "issues/$n/comments?per_page=100" \
                | jq -r --arg b "$LIVE_BOT" '.[] | select(.user.login == $b) | .body' \
                | grep -o 'hephaisto:status:[0-9a-f]\{32\}' | cut -d: -f3 | sort -u); do
            uuid="${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"
            [ "$(_issues_curl -o /dev/null -w '%{http_code}' "$ISSUES_API/api/workitems/$uuid")" = 200 ] || echo "$n $uuid"
        done
    done
}

# ---------------------------------------------------------------------------------------
# One issue nobody is working on
# ---------------------------------------------------------------------------------------

# The bystander: an issue of the sandbox that is never assigned and that nothing should ever
# point at. What a model repeats in a plan or a pull request names it - "fixes #<bystander>" -
# and its timeline is where GitHub would show that the text did something. One per run, opened
# by whichever scenario asks first.
live_bystander() {
    local file="${LIVE_OUT:?}/bystander" n
    if [ ! -s "$file" ]; then
        n=$(gh_issue_create "$LIVE_REPO" "$(live_title bystander "an issue nothing should point at")" \
            "Opened by scripts/e2e/github-live.sh and never assigned. Its timeline is read for references that text Hephaisto wrote must not have made.") \
            || return 1
        echo "$n" > "$file"
    fi
    cat "$file"
}

# What a model repeats from an issue when it is asked to (coder/src/phases.ts, fakeRepeated):
# a mention, three ways of naming another issue, and two closing keywords. The line goes into
# the issue inside a code block, where GitHub itself makes nothing of it - so whatever the
# bystander's timeline shows afterwards was made by what Hephaisto wrote.
#   live_hostile_body <bystander>
live_hostile_body() {
    printf 'The order total is null for an empty cart: open the cart with nothing in it.\n\n```\nFAKE-SDK-REPEAT: cc @%s - fixes #%s, closes GH-%s and resolves https://github.com/%s/issues/%s\n```\n' \
        "$ISSUES_APPROVER" "$1" "$1" "$LIVE_REPO" "$1"
}

# ---------------------------------------------------------------------------------------
# Leaving the sandbox as it was found
# ---------------------------------------------------------------------------------------

# Pull requests of the bot's from a hephaisto/codefix-* branch whose description closes one of
# the given issues: [{number, state, head}].
#   live_prs_for <issue> [issue ...]
live_prs_for() {
    local wanted
    wanted=$(printf '%s\n' "$@" | jq -Rn --arg r "$LIVE_REPO" '[inputs | select(length > 0) | "Closes \($r)#\(.)"]')
    _live_api GET "pulls?state=all&per_page=100" \
        | jq -c --arg b "$LIVE_BOT" --arg re "$LIVE_BRANCH_RE" --argjson w "$wanted" \
            '[.[] | select(.user.login == $b and (.head.ref | test($re)))
                  | select((.body // "") as $body | any($w[]; . as $line | ($body | split("\n") | index($line)) != null))
                  | {number, state, head: .head.ref}]'
}

# Branches of the sandbox that match the runner's pattern and whose head commit carries the
# trailer of one of the given issues: one name per line.
#   live_branches_for <issue> [issue ...]
live_branches_for() {
    local ref sha message n
    _live_api GET "git/matching-refs/heads/hephaisto/codefix-" --jq '.[] | "\(.ref) \(.object.sha)"' 2>/dev/null \
        | while read -r ref sha; do
            ref="${ref#refs/heads/}"
            [[ "$ref" =~ $LIVE_BRANCH_RE ]] || continue
            message=$(_live_api GET "git/commits/$sha" --jq '.message' 2>/dev/null) || continue
            for n in "$@"; do
                if grep -qxF "Hephaisto-Issue: $LIVE_REPO#$n" <<<"$message"; then echo "$ref"; break; fi
            done
        done
}

#   live_delete_branch <branch>     only ever a hephaisto/codefix-<id> branch
live_delete_branch() {
    [[ "$1" =~ $LIVE_BRANCH_RE ]] || { printf 'refusing to delete %s: not a hephaisto/codefix-<id> branch\n' "$1" >&2; return 1; }
    _live_api DELETE "git/refs/heads/$1" >/dev/null
}

# Closes the issues this run opened, then - once the agent has let go of them, so that no Job
# pushes afterwards - closes without merging the pull requests they led to and deletes those
# pull requests' branches. In the sandbox, by construction. Safe to call twice; prints what it
# did.
live_cleanup() {
    local created="${LIVE_CREATED:-}" issues n pr branch
    [ -n "$created" ] && [ -s "$created" ] || return 0
    issues=$(sort -un "$created" | tr '\n' ' ')

    for n in $issues; do
        if [ "$(_live_api GET "issues/$n" --jq '.state' 2>/dev/null)" = open ]; then
            gh_issue_close "$LIVE_REPO" "$n" && echo "cleanup: closed issue #$n"
        fi
    done

    # A closed issue is a cancelled work item on the agent's next pass, and a cancelled work
    # item's Job is deleted. Until then a Job may still be about to push.
    _live_let_go() {
        local open
        open=$(_issues_curl "$ISSUES_API/api/workitems?limit=200" 2>/dev/null \
            | jq --arg r "$LIVE_REPO" --arg ns " $issues" '[.[]? | select(.repository == $r) | select($ns | contains(" \(.number) "))] | length' 2>/dev/null)
        [ "${open:-1}" -eq 0 ] && [ "$(kc -n "$ISSUES_CODER_NS" get jobs -l hephaisto.dev/work-item -o json 2>/dev/null | jq '[.items[] | select((.status.active // 0) > 0)] | length' 2>/dev/null)" = 0 ]
    }
    local waited=0
    until _live_let_go || [ "$waited" -ge 180 ]; do sleep 5; waited=$(( waited + 5 )); done
    [ "$waited" -lt 180 ] || echo "cleanup: the agent still holds a work item of this run after 180s; cleaning what GitHub shows now"

    # shellcheck disable=SC2086
    for pr in $(live_prs_for $issues | jq -r '.[] | select(.state == "open") | .number'); do
        gh_pr_close "$LIVE_REPO" "$pr" && echo "cleanup: closed pull request #$pr without merging"
    done

    # shellcheck disable=SC2086
    for branch in $(live_branches_for $issues); do
        live_delete_branch "$branch" && echo "cleanup: deleted branch $branch"
    done
}

# Everything in the sandbox that carries the suite's marks, whichever run made it: open issues
# whose title starts with the prefix, the bot's open pull requests from hephaisto/codefix-*
# branches, and those branches. For after a run that was killed before its trap ran.
live_sweep() {
    local n pr ref
    for n in $(_live_api GET "issues?state=open&per_page=100" \
        | jq -r --arg p "$LIVE_TITLE_PREFIX" '.[] | select(.pull_request == null) | select(.title | startswith($p)) | .number'); do
        gh_issue_close "$LIVE_REPO" "$n" && echo "sweep: closed issue #$n"
    done
    for pr in $(_live_api GET "pulls?state=open&per_page=100" \
        | jq -r --arg b "$LIVE_BOT" --arg re "$LIVE_BRANCH_RE" '.[] | select(.user.login == $b and (.head.ref | test($re))) | .number'); do
        gh_pr_close "$LIVE_REPO" "$pr" && echo "sweep: closed pull request #$pr without merging"
    done
    for ref in $(_live_api GET "git/matching-refs/heads/hephaisto/codefix-" --jq '.[].ref' 2>/dev/null); do
        live_delete_branch "${ref#refs/heads/}" && echo "sweep: deleted branch ${ref#refs/heads/}"
    done
}
