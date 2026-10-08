# issues: G03 | kubectl | exclusive | /approve from an approver starts exactly one implementing Job: the branch is pushed, and the pull request says Closes
#
# The whole road (#243): assigned, planned, approved in a comment, implemented, and a draft pull
# request whose body closes the issue - so that merging it is what finishes the work, in GitHub
# as in Hephaisto. The approval is a comment by an account the install names as an approver.

scenario() {
    issues_ready || return
    local n attempt attempt_id branch impl url

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G03 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it. The total reads null where it should read 0.") \
        || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    want "the plan says how to approve it" "$(gh_bot_said "$ISSUES_REPO" "$n" '/approve')" -ge 1
    want "and how to refuse it" "$(gh_bot_said "$ISSUES_REPO" "$n" '/reject')" -ge 1

    gh_comment_as "$ISSUES_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/approve" >/dev/null

    wi_wait_attempt "$WI" "$ISSUES_IMPLEMENT_WAIT" PrOpened Failed Cancelled Denied Expired || true
    attempt=$(wi_attempt "$WI")
    url=$(jq -r '.prUrl // empty' <<<"$attempt")
    [ "$(jq -r .state <<<"$attempt")" = PrOpened ] && pass "the approval ended in a pull request" "$url" \
        || { fail "the approval ended in a pull request" "$(jq -r '(.state // "no attempt") + ": " + (.failureReason // "")' <<<"$attempt")"; issues_done "$ISSUES_REPO" "$n"; return; }

    want "the approver is on record, by the account that commented" \
        "$(jq --arg a "$ISSUES_APPROVER" '(.approvedBy // "") | contains($a)' <<<"$attempt")" = true
    want "exactly one Job implemented it" "$(issues_job_count "$attempt_id" implement)" -eq 1
    want "and it is still the one attempt" "$(wi_attempt_count "$WI")" -eq 1

    branch=$(jq -r '.branch // empty' <<<"$attempt")
    [ -n "$branch" ] && [ -n "$(issues_git_rev "$branch")" ] && pass "the branch was pushed" "$branch" \
        || fail "the branch was pushed" "${branch:-no branch on the attempt}"

    impl=$(jq -r '.implementJobName // empty' <<<"$attempt")
    issues_pr_body "$impl" | grep -qF "Closes $ISSUES_REPO#$n" \
        && pass "the pull request's body says Closes $ISSUES_REPO#$n" \
        || fail "the pull request's body says Closes $ISSUES_REPO#$n" "not found in what $impl published"

    want "the issue is told where the pull request is" \
        "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq --arg u "$url" '[.[] | select($u != "" and (.body | contains($u)))] | length')" -ge 1

    issues_done "$ISSUES_REPO" "$n"
}
