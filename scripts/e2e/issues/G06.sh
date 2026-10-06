# issues: G06 | kubectl | exclusive | unassigning the bot cancels the running Job
#
# Taking the issue away is how a person stops Hephaisto, and it has to stop: the Job that is
# implementing is ended, nothing is pushed, no pull request appears. Unassigned while it
# implements, because that is the one phase long enough to be caught running - the scripted
# coder holds an implementation for two minutes, and a plan is over in seconds.

scenario() {
    issues_ready || return
    local n attempt impl branch

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G06 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null
    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Implementing PrOpened Failed Cancelled || true
    attempt=$(wi_attempt "$WI")
    impl=$(jq -r '.implementJobName // empty' <<<"$attempt")
    [ "$(jq -r .state <<<"$attempt")" = Implementing ] && [ -n "$impl" ] && issues_job_exists "$impl" \
        && pass "a Job is implementing" "$impl" \
        || { fail "a Job is implementing" "the attempt is $(jq -r '.state // "missing"' <<<"$attempt")"; issues_done "$ISSUES_REPO" "$n"; return; }

    gh_unassign "$ISSUES_REPO" "$n"

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Cancelled PrOpened Failed || true
    attempt=$(wi_attempt "$WI")
    want "the attempt is cancelled" "$(jq -r '.state // empty' <<<"$attempt")" = Cancelled

    _g06_stopped() { ! issues_job_running "$impl"; }
    wait_for "the Job to stop" 120 _g06_stopped && pass "the Job no longer runs" || fail "the Job no longer runs" "$impl still has a pod"

    want "no pull request was opened" "$(jq -r '.prUrl // ""' <<<"$attempt")" = ""
    branch=$(jq -r '.branch // empty' <<<"$attempt")
    want "the branch was not pushed" "$(issues_git_rev "${branch:-none}")" = ""
    want "the work item ended with the assignment" "$(wi_state "$ISSUES_REPO" "$n")" = "$WI_CANCELLED"

    issues_done "$ISSUES_REPO" "$n"
}
