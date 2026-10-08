# issues: G17 | kubectl | exclusive | unassigning and assigning again faster than a poll is noticed once the attempt has ended, and not while a plan waits
#
# An unassignment was noticed only when a poll found the issue without the bot on it. Unassign
# and assign again between two polls - seven seconds apart on the first real issue, against a
# poll a minute apart - and every poll saw an assigned issue: nothing followed, and the issue
# still said "unassign Hephaisto and assign it again" (#285). So a fresh assignment is now
# recognised by its TIME on GitHub: for a work item whose latest attempt has ended, the issue's
# timeline is read, and an `assigned` event for the bot that is newer than that end is a new
# hand-over - a new attempt for the same work item.
#
# The rule for a plan that is waiting, written down: it is left alone. Assigning again does not
# answer a plan, and a second Job for an issue whose plan nobody has read would be a way to
# spend money by clicking. Only an attempt that has ENDED is followed by another one.
#
# The stand-in's `reassign` does both in one request, so that no poll can fall between them.

scenario() {
    issues_ready || return
    local n first_id mark second job request

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G17 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    first_id=$(jq -r .id <<<"$ATTEMPT")

    # While the plan waits: nothing, and nothing is asked of GitHub for it either.
    mark=$(gh_mark)
    gh_reassign "$ISSUES_REPO" "$n" || { fail "the stand-in reassigned the issue"; issues_done "$ISSUES_REPO" "$n"; return; }
    gh_wait_polls "$mark" 3 || fail "the agent kept polling" "fewer than three polls in ${ISSUES_SEEN_WAIT}s"
    want "no poll saw the issue unassigned" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "a waiting plan is left alone" "$(wi_attempt_state "$WI")" = PlanReady
    want "and no second attempt was started" "$(wi_attempt_count "$WI")" -eq 1
    want "the timeline is not read for a plan that waits" "$(gh_timeline_reads_since "$mark" "$ISSUES_REPO" "$n")" -eq 0

    # The plan is refused: the attempt has ended, and the issue is still assigned.
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject not this way" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied || { fail "the plan was rejected" "the attempt is $(wi_attempt_state "$WI")"; issues_done "$ISSUES_REPO" "$n"; return; }

    # The assignment from before the end is not a new one: three polls, and nothing.
    mark=$(gh_mark)
    gh_wait_polls "$mark" 3 || true
    want "the assignment from before the attempt ended starts nothing" "$(wi_attempt_count "$WI")" -eq 1
    want "the timeline is read for an attempt that has ended" "$(gh_timeline_reads_since "$mark" "$ISSUES_REPO" "$n")" -ge 1
    want "and an unchanged one costs nothing" \
        "$(gh_requests_since "$mark" | jq --arg p "/repos/$ISSUES_REPO/issues/$n/timeline" '[.[] | select(.path == $p and .status == 304)] | length')" -ge 1

    # Now what the person on the first real issue did: off and on again, faster than a poll.
    gh_reassign "$ISSUES_REPO" "$n"
    issues_next_plan_ready "$WI" 2 || { issues_done "$ISSUES_REPO" "$n"; return; }
    second="$ATTEMPT"

    want "it is still one work item: no poll saw the gap" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "the rejected attempt is still rejected" "$(wi_attempts "$WI" | jq -r '.[0].state')" = Denied
    want "and the new one is another attempt" "$(jq -r .id <<<"$second")" != "$first_id"

    job=$(jq -r '.planJobName // empty' <<<"$second")
    request=$(issues_request "$job")
    want "the new Job knows what was planned before" "$(jq '(.previous.summary // "") | length > 0' <<<"${request:-null}")" = true

    # And once: the same assignment does not start a third when the second has ended.
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject nor this" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied || true
    mark=$(gh_mark)
    gh_wait_polls "$mark" 3 || true
    want "one assignment is one hand-over" "$(wi_attempt_count "$WI")" -eq 2

    issues_done "$ISSUES_REPO" "$n"
}
