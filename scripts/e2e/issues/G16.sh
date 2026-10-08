# issues: G16 | kubectl | exclusive | after an attempt that did not work, the issue shows what the planner asked, and /replan plans again
#
# A planner that cannot plan says what it is missing - and that, too, was shown to nobody: the
# issue read "it did not work" and "unassign Hephaisto and assign it again", which planned the
# same text a second time. Now the status comment carries the planner's questions, says that an
# approver's /replan has it tried again, and the answer reaches the Job that tries.
#
# The first attempt is made to fail cheaply: a line in the issue tells the SCRIPTED planner to
# answer insufficient_context with a question (coder/fake-scripts/default.plan.unclear.json).
# No model is asked, and nothing in the agent knows the line.

scenario() {
    issues_ready || return
    local n first first_id second job request status
    local answer="G16-ANSWER-${ISSUES_RUN}"

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G16 "the total is wrong")" \
        "The total is wrong sometimes.

FAKE-SDK-PLAN: unclear") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"

    wi_wait "$ISSUES_REPO" "$n" || { fail "the assigned issue became a work item" "none within ${ISSUES_SEEN_WAIT}s"; issues_done "$ISSUES_REPO" "$n"; return; }
    WI=$(wi_id "$ISSUES_REPO" "$n")
    _g16_has_attempt() { [ "$(wi_attempt_count "$WI")" -ge 1 ]; }
    wait_for "the work item to get an attempt" "$ISSUES_SEEN_WAIT" _g16_has_attempt || true
    wi_wait_attempt "$WI" "$ISSUES_PLAN_WAIT" Failed PlanReady Cancelled Denied Expired || true
    first=$(wi_attempt "$WI"); first_id=$(jq -r '.id // empty' <<<"$first")
    [ "$(jq -r '.state // empty' <<<"$first")" = Failed ] && pass "the first attempt did not work" "$(jq -r '.failureReason // ""' <<<"$first")" \
        || { fail "the first attempt did not work" "it is $(jq -r '(.state // "no attempt")' <<<"$first") - the scripted planner did not answer insufficient_context"; issues_done "$ISSUES_REPO" "$n"; return; }
    want "the planner said what it is missing, as a question" "$(jq '.questions // [] | length' <<<"$first")" -ge 1

    _g16_told() { [ "$(issues_status_comment "$ISSUES_REPO" "$n" "$WI" | grep -c '`/replan`')" -ge 1 ]; }
    wait_for "the issue to say how it is tried again" "$ISSUES_SEEN_WAIT" _g16_told \
        && pass "the issue says that /replan has it tried again" \
        || fail "the issue says that /replan has it tried again" "the status comment does not name /replan"
    status=$(issues_status_comment "$ISSUES_REPO" "$n" "$WI")
    want "and shows every question the planner asked" \
        "$(jq -r --arg s "$status" '[.questions[]? | select(. as $q | $s | contains($q))] | length' <<<"$first")" -eq "$(jq '.questions // [] | length' <<<"$first")"
    want "the bot has written one comment so far" "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq length)" -eq 1

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_AUTHOR" "$ISSUES_AUTHOR_ID" "It is the cart's total when the cart is empty. ${answer}" >/dev/null
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/replan" >/dev/null

    issues_next_plan_ready "$WI" 2 || { issues_done "$ISSUES_REPO" "$n"; return; }
    second="$ATTEMPT"

    want "it is the same work item" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "the failed attempt is still what it was" \
        "$(wi_attempts "$WI" | jq -c '.[0] | [.id, .state, .failureReason]')" = "$(jq -c '[.id, .state, .failureReason]' <<<"$first")"

    job=$(jq -r '.planJobName // empty' <<<"$second")
    request=$(issues_request "$job")
    want "the new Job is told what the planner had asked" \
        "$(jq --argjson f "$first" '.previous.questions == $f.questions' <<<"${request:-null}")" = true
    want "and what the issue's author answered" \
        "$(jq --arg a "$ISSUES_AUTHOR" --arg t "$answer" '[.work_item.comments[]? | select(.author == $a and (.body | contains($t)))] | length' <<<"${request:-null}")" -eq 1
    want "the second attempt has a plan on the issue" "$(issues_plan_comment "$ISSUES_REPO" "$n" "$(jq -r .id <<<"$second")" | grep -c 'plan for this issue')" -ge 1

    issues_done "$ISSUES_REPO" "$n"
}
