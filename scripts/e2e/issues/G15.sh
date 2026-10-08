# issues: G15 | kubectl | exclusive | /replan from anybody else changes nothing, and while a Job runs or after a pull request it is refused once
#
# The same rule as for /approve: a command is heard from an account whose NUMBER the install
# lists, and from nobody else - a stranger is told so once, however often they try. And an
# approver's /replan is not always possible: while a Job is running for the issue there is
# nothing to plan again yet, and once a pull request is open the work is reviewed there. Each of
# those is said in one sentence, once, and changes nothing.

scenario() {
    issues_ready || return
    local n attempt_id mark before after

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G15 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    # Somebody who is not on the list, and the approver's login on another account.
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER" "$ISSUES_OUTSIDER_ID" "/replan
do it my way" >/dev/null
    _g15_answered() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER")" -ge 1 ]; }
    wait_for "the answer to the stranger" "$ISSUES_SEEN_WAIT" _g15_answered \
        && pass "the stranger is told that the command did not count" \
        || fail "the stranger is told that the command did not count" "no comment of the bot's names $ISSUES_OUTSIDER"

    before=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')
    mark=$(gh_mark)
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_OUTSIDER" "$ISSUES_OUTSIDER_ID" "/replan" >/dev/null
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$(( ISSUES_APPROVER_ID + 7 ))" "/replan" >/dev/null
    gh_wait_comment_reads "$mark" 3 "$ISSUES_REPO" "$n" || fail "the agent kept reading the comments" "fewer than three reads in ${ISSUES_SEEN_WAIT}s"
    after=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq -c '[length, (map(.edits) | add // 0)]')
    want "told once, not at every command" "$after" = "$before"
    want "the plan is still waiting" "$(wi_attempt_state "$WI")" = PlanReady
    want "and it is still the one attempt" "$(wi_attempt_count "$WI")" -eq 1

    # While a Job runs: the plan is approved, and the implementing Job takes minutes.
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Implementing PrOpened Failed Cancelled || true
    if [ "$(wi_attempt_state "$WI")" = Implementing ]; then
        gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/replan" >/dev/null
        _g15_running() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" 'a Job is running')" -ge 1 ]; }
        wait_for "the refusal while a Job runs" "$ISSUES_SEEN_WAIT" _g15_running \
            && pass "while a Job runs, /replan is refused and says so" \
            || fail "while a Job runs, /replan is refused and says so" "no comment of the bot's says that a Job is running"
        want "the running attempt was not ended by it" "$(wi_attempt_count "$WI")" -eq 1
    else
        fail "the approval started the implementing Job" "the attempt is $(wi_attempt_state "$WI")"
    fi

    wi_wait_attempt "$WI" "$ISSUES_IMPLEMENT_WAIT" PrOpened Failed Cancelled Denied Expired || true
    [ "$(wi_attempt_state "$WI")" = PrOpened ] && pass "the approved plan ended in a pull request" \
        || { fail "the approved plan ended in a pull request" "the attempt is $(wi_attempt_state "$WI")"; issues_done "$ISSUES_REPO" "$n"; return; }

    # After the pull request: refused, with another sentence - and that one, too, once.
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/replan" >/dev/null
    _g15_opened() { [ "$(gh_bot_said "$ISSUES_REPO" "$n" 'pull request is already open')" -ge 1 ]; }
    wait_for "the refusal after the pull request" "$ISSUES_SEEN_WAIT" _g15_opened \
        && pass "after a pull request, /replan is refused and says so" \
        || fail "after a pull request, /replan is refused and says so" "no comment of the bot's says that a pull request is open"

    before=$(gh_bot_comments "$ISSUES_REPO" "$n" | jq 'length')
    mark=$(gh_mark)
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/replan" >/dev/null
    gh_wait_comment_reads "$mark" 3 "$ISSUES_REPO" "$n" || fail "the agent kept reading the comments" "fewer than three reads in ${ISSUES_SEEN_WAIT}s"
    want "a second /replan is not answered again" "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq 'length')" -eq "$before"
    want "the pull request stands" "$(wi_attempt_state "$WI")" = PrOpened
    want "there is still one attempt" "$(wi_attempt_count "$WI")" -eq 1
    want "and one planning Job was all there ever was" "$(issues_job_count "$attempt_id" plan)" -eq 1
    want "what the bot wrote for this attempt stays under its ceiling" \
        "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq 'length')" -le "$(( 1 + ISSUES_ATTEMPT_COMMENT_CAP ))"

    issues_done "$ISSUES_REPO" "$n"
}
