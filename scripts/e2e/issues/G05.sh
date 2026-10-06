# issues: G05 | kubectl | exclusive | /reject <reason> ends the attempt as denied, with the reason
#
# The other answer. The plan is refused in the place it was offered, the reason is kept with the
# attempt - it is the only thing the next reader of that attempt will want to know - and a
# later /approve does not bring the refused plan back.

scenario() {
    issues_ready || return
    local n attempt attempt_id mark
    local reason="not this way: the null is the caller's ${ISSUES_RUN}"

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G05 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject $reason" >/dev/null

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied Implementing PrOpened Failed Cancelled || true
    attempt=$(wi_attempt "$WI")
    want "the attempt is denied" "$(jq -r '.state // empty' <<<"$attempt")" = Denied
    want "the reason is kept with it, as it was written" \
        "$(jq --arg r "$reason" 'tostring | contains($r)' <<<"$attempt")" = true
    want "no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0
    want "the issue says the plan was refused" "$(gh_bot_said "$ISSUES_REPO" "$n" 'reject|denied|refused')" -ge 1

    mark=$(gh_mark)
    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null
    gh_wait_polls "$mark" 2 || true
    want "an approval afterwards does not revive it" "$(wi_attempt_state "$WI")" = Denied
    want "and still starts no Job" "$(issues_job_count "$attempt_id" implement)" -eq 0

    issues_done "$ISSUES_REPO" "$n"
}
