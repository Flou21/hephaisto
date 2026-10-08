# issues: G10 | kubectl | exclusive | comments are capped: one status comment edited in place, and never more than a fixed number on an issue
#
# Every comment is a notification to everybody who watches the repository, and an issue is a
# place people read. So where the work stands is ONE comment that is edited as it moves, and
# however many times the agent is provoked - here by eight strangers answering the plan - what
# it has written for one attempt stays under a number an install can read
# (ISSUES_ATTEMPT_COMMENT_CAP, beside the one status comment). Since a work item can be planned
# again (#252) the ceiling is per attempt, with a ceiling on attempts above it
# (ISSUES_COMMENT_CAP for the whole work item); this issue has one attempt.

scenario() {
    issues_ready || return
    local n i mark comments

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G10 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }

    comments=$(gh_bot_comments "$ISSUES_REPO" "$n")
    want "where the work stands is one comment, edited in place as it moved" \
        "$(jq '[.[] | select(.edits > 0)] | length' <<<"$comments")" -eq 1
    want "a planned issue carries two comments of the bot's: the status and the plan" "$(jq length <<<"$comments")" -eq 2

    mark=$(gh_mark)
    for i in 1 2 3 4 5 6 7 8; do
        gh_comment_as "$ISSUES_REPO" "$n" "stranger$i" "$(( 5000 + i ))" "/approve" >/dev/null
    done
    gh_wait_comment_reads "$mark" 3 "$ISSUES_REPO" "$n" || fail "the agent kept reading the comments" "fewer than three reads in ${ISSUES_SEEN_WAIT}s"

    comments=$(gh_bot_comments "$ISSUES_REPO" "$n")
    want "eight strangers later the bot has written no more than the status comment and $ISSUES_ATTEMPT_COMMENT_CAP for the attempt" \
        "$(jq length <<<"$comments")" -le "$(( 1 + ISSUES_ATTEMPT_COMMENT_CAP ))"
    want "and still edits only the one" "$(jq '[.[] | select(.edits > 0)] | length' <<<"$comments")" -eq 1
    want "the plan is still waiting for somebody who may answer it" "$(wi_attempt_state "$WI")" = PlanReady

    issues_done "$ISSUES_REPO" "$n"
}
