# live: L03 | unassigning the bot while the plan waits takes the issue back, and a later /approve changes nothing
#
# Taking the issue away is how a person stops Hephaisto. On github.com that is removing the bot
# from the assignees - the issue stays open, so it is the one case where the agent has to tell
# "no longer in the list of assigned issues" apart from "closed" by asking for the issue itself.
# The plan that was waiting is no longer a plan anybody can approve.

scenario() {
    issues_ready || return
    local n attempt_id branch mark

    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L03 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "gh opened an issue in the sandbox"; return; }
    gh_assign "$LIVE_REPO" "$n" || { fail "GitHub accepted the bot as the assignee"; issues_done "$LIVE_REPO" "$n"; return; }
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    branch=$(jq -r '.branch // empty' <<<"$ATTEMPT")

    gh_unassign "$LIVE_REPO" "$n" || fail "gh unassigned the bot"
    want "GitHub has the issue open, with nobody assigned" \
        "$(_live_api GET "issues/$n" --jq '"\(.state) \(.assignees | length)"')" = "open 0"

    wi_wait_state "$LIVE_REPO" "$n" "$ISSUES_SEEN_WAIT" "$WI_CANCELLED" "$WI_DONE" || true
    want "the work item ended with the assignment" "$(wi_state "$LIVE_REPO" "$n")" = "$WI_CANCELLED"
    want "and says why" "$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '.stateReason // empty')" = "$LIVE_BOT is no longer an assignee"

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Cancelled Implementing PrOpened Failed Denied || true
    want "the waiting plan is cancelled" "$(wi_attempt_state "$WI")" = Cancelled

    _l03_told() { [ "$(gh_bot_said "$LIVE_REPO" "$n" 'Hephaisto has let go of this issue')" -ge 1 ]; }
    wait_for "the issue to say that Hephaisto let go" "$ISSUES_SEEN_WAIT" _l03_told \
        && pass "the status comment says that Hephaisto has let go of the issue" \
        || fail "the status comment says that Hephaisto has let go of the issue" "no comment of the bot's says so"

    # An approval that arrives after the issue was taken back starts nothing - and is not
    # answered either: a plan that is not waiting is not read for.
    mark=$(gh_mark)
    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null || fail "gh commented /approve"
    gh_wait_polls "$mark" 3 || true
    want "an approval after that changes nothing" "$(wi_attempt_state "$WI")" = Cancelled
    want "starts no Job" "$(issues_job_count "$attempt_id" implement)" -eq 0
    want "makes no second work item" "$(wi_count "$LIVE_REPO" "$n")" -eq 1
    want "pushes no branch" "$(issues_git_rev "${branch:-none}")" = ""
    want "and the bot wrote nothing more: two comments" "$(gh_bot_comments "$LIVE_REPO" "$n" | jq 'length')" -eq 2

    issues_done "$LIVE_REPO" "$n"
}
