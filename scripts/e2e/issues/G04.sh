# issues: G04 | kubectl | exclusive | /approve from anybody who is not an approver changes nothing, and is answered once
#
# A comment is something anybody with a GitHub account can write on a public repository. Two
# people try: one who is simply not on the list, and one whose LOGIN is an approver's and whose
# account is not - a login can be given up and taken by somebody else, which is why the install
# lists numbers. Neither may start anything. The first is told so, once: an agent that answers
# the same comment at every poll has turned one stranger into a flood.
#
# The control is last: the real approver's /reject is acted on. Without it "nothing changed"
# is true of an agent that reads no comments at all.

scenario() {
    issues_ready || return
    local n attempt_id mark before after

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G04 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER" "$ISSUES_OUTSIDER_ID" "/approve" >/dev/null

    _g04_answered() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER")" -ge 1 ]; }
    wait_for "the refusal" "$ISSUES_SEEN_WAIT" _g04_answered \
        && pass "the stranger is told, by name, that the approval did not count" \
        || fail "the stranger is told, by name, that the approval did not count" "no comment of the bot's names $ISSUES_OUTSIDER"

    # What the bot has written so far, and how often it has edited it; then three more reads
    # of the comments, with the stranger's still there to be answered again.
    before=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')
    mark=$(gh_mark)
    gh_wait_comment_reads "$mark" 3 "$ISSUES_REPO" "$n" || fail "the agent kept reading the comments" "fewer than three reads in ${ISSUES_SEEN_WAIT}s"
    after=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')
    want "it was answered once, not at every poll" "$after" = "$before"

    # The approver's login, on another account.
    mark=$(gh_mark)
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$(( ISSUES_APPROVER_ID + 7 ))" "/approve" >/dev/null
    gh_wait_comment_reads "$mark" 2 "$ISSUES_REPO" "$n" || true

    want "the plan is still waiting" "$(wi_attempt_state "$WI")" = PlanReady
    want "no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0
    want "nobody is on record as having approved" "$(wi_attempt "$WI" | jq -r '.approvedBy // ""')" = ""

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject the control: an approver is heard" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied && pass "the approver's own answer was acted on" \
        || fail "the approver's own answer was acted on" "the attempt is $(wi_attempt_state "$WI")"

    issues_done "$ISSUES_REPO" "$n"
}
