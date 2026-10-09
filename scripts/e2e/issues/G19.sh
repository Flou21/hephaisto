# issues: G19 | kubectl | exclusive | nobody else's reaction and no other reaction answers a plan; an approver's thumbs-down rejects it
#
# A reaction is something anybody with a GitHub account can click on a public repository, and a
# thumbs-up is what people set on a comment they have merely read. So (#298): a stranger's
# rocket changes nothing and is answered once - by the sentence a stranger's /approve gets, and
# never twice for one plan; the approver's LOGIN on another account is a stranger; and an
# approver's own thumbs-up, heart or eyes says nothing at all.
#
# The control is last: the approver's thumbs-down is acted on. Without it "nothing changed" is
# true of an agent that reads no reactions.

scenario() {
    issues_ready || return
    local n attempt_id plan mark before after

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G19 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    plan=$(issues_plan_comment_id "$ISSUES_REPO" "$n" "$attempt_id")
    [ -n "$plan" ] || { fail "the plan is a comment on the issue" "none carries the attempt's marker"; issues_done "$ISSUES_REPO" "$n"; return; }

    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_OUTSIDER" "$ISSUES_OUTSIDER_ID" rocket >/dev/null

    _g19_answered() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER")" -ge 1 ]; }
    wait_for "the refusal" "$ISSUES_SEEN_WAIT" _g19_answered \
        && pass "the stranger is told, by name, that the click did not count" \
        || fail "the stranger is told, by name, that the click did not count" "no comment of the bot's names $ISSUES_OUTSIDER"

    # More of them: the stranger's thumbs-down, the approver's login on another account, and
    # the approver's own thumbs-up, heart and eyes. Then three more reads of the reactions.
    before=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')
    mark=$(gh_mark)
    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_OUTSIDER" "$ISSUES_OUTSIDER_ID" -1 >/dev/null
    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$(( ISSUES_APPROVER_ID + 7 ))" rocket >/dev/null
    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" +1 >/dev/null
    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" heart >/dev/null
    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" eyes >/dev/null
    gh_wait_reaction_reads "$mark" 3 "$ISSUES_REPO" "$plan" || fail "the agent kept reading the reactions" "fewer than three reads in ${ISSUES_SEEN_WAIT}s"
    after=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')

    want "it was answered once, not for every click" "$after" = "$before"
    want "the plan is still waiting" "$(wi_attempt_state "$WI")" = PlanReady
    want "no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0
    want "nobody is on record as having approved" "$(wi_attempt "$WI" | jq -r '.approvedBy // ""')" = ""

    gh_react_as "$ISSUES_REPO" "$plan" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" -1 >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied && pass "the approver's thumbs-down rejected the plan" \
        || fail "the approver's thumbs-down rejected the plan" "the attempt is $(wi_attempt_state "$WI")"
    want "and it is on record how it was given" \
        "$(wi_attempt "$WI" | jq -r '.failureReason // ""')" = "no reason given: rejected with a thumbs-down on the plan"
    want "no Job implemented anything, still" "$(issues_job_count "$attempt_id" implement)" -eq 0

    issues_done "$ISSUES_REPO" "$n"
}
