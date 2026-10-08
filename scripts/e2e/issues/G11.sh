# issues: G11 | kubectl | exclusive | a merged pull request ends the work item
#
# Opening the pull request is not the end of the work; somebody reviewing and merging it is. The
# agent reads the pull request it opened until that happens, and then the work item is done -
# and stays done: the issue GitHub closed is not found again as something new.

scenario() {
    issues_ready || return
    local n attempt pr mark

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G11 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }

    # The shim numbers pull requests from 1 in every Job: whatever an earlier scenario, or an
    # earlier run, said became of "pull request 1" is not about this one.
    gh_pr_forget "$ISSUES_REPO" 1
    mark=$(gh_mark)

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_IMPLEMENT_WAIT" PrOpened Failed Cancelled Denied Expired || true
    attempt=$(wi_attempt "$WI")
    pr=$(jq -r '.prNumber // empty' <<<"$attempt")
    [ "$(jq -r .state <<<"$attempt")" = PrOpened ] && [ -n "$pr" ] && pass "a pull request is open" "#$pr" \
        || { fail "a pull request is open" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$attempt")"; issues_done "$ISSUES_REPO" "$n"; return; }

    # Seen open at least twice before anything is merged: "not done yet" read in the second the
    # pull request appeared would be true of an agent that never asks about one.
    _g11_asked() { [ "$(gh_requests_since "$mark" | jq --arg p "/repos/$ISSUES_REPO/pulls/$pr" '[.[] | select(.path == $p and (.status == 200 or .status == 304))] | length')" -ge 2 ]; }
    wait_for "the agent to ask about the pull request" "$ISSUES_SEEN_WAIT" _g11_asked \
        && pass "the agent asks what became of its pull request" \
        || fail "the agent asks what became of its pull request" "fewer than two reads of pulls/$pr in ${ISSUES_SEEN_WAIT}s"
    want "an open pull request does not end the work" "$(wi_state "$ISSUES_REPO" "$n")" != "$WI_DONE"

    # Merged - and, as GitHub does for a body that says Closes, the issue closes with it.
    gh_pr_merge "$ISSUES_REPO" "$pr"
    gh_issue_close "$ISSUES_REPO" "$n"

    wi_wait_state "$ISSUES_REPO" "$n" "$ISSUES_SEEN_WAIT" "$WI_DONE" "$WI_CANCELLED" || true
    want "the merge ended the work item as done" "$(wi_state "$ISSUES_REPO" "$n")" = "$WI_DONE"

    _g11_told() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" 'Done.*merged')" -ge 1 ]; }
    wait_for "the issue to say that it is done" "$ISSUES_SEEN_WAIT" _g11_told \
        && pass "and the issue says so" || fail "and the issue says so" "no comment of the bot's says Done and merged"

    gh_wait_polls "$(gh_mark)" 2 || true
    want "it is still one work item" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "with the one attempt" "$(wi_attempt_count "$WI")" -eq 1

    # The next Job's pull request is number 1 again.
    gh_pr_forget "$ISSUES_REPO" "$pr"
}
