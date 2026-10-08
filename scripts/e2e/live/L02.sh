# live: L02 | /reject <reason> by the approver ends the attempt as denied, with the reason, and nothing is pushed
#
# The other answer, on github.com: the plan is refused in the place it was offered, by a comment
# of a person the install lists by number. The reason is kept with the attempt and said on the
# issue. No Job implements anything, and GitHub has no branch.

scenario() {
    issues_ready || return
    local n attempt attempt_id branch
    local reason="not this way: the null is the caller's ${ISSUES_RUN}"

    n=$(gh_issue_create "$LIVE_REPO" "$(live_title L02 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "gh opened an issue in the sandbox"; return; }
    gh_assign "$LIVE_REPO" "$n" || { fail "GitHub accepted the bot as the assignee"; issues_done "$LIVE_REPO" "$n"; return; }
    issues_plan_ready "$LIVE_REPO" "$n" || { issues_done "$LIVE_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")
    branch=$(jq -r '.branch // empty' <<<"$ATTEMPT")

    want "the plan says how to refuse it" "$(gh_bot_said "$LIVE_REPO" "$n" '/reject')" -ge 1

    gh_comment_as "$LIVE_REPO" "$n" "$ISSUES_APPROVER" "$ISSUES_APPROVER_ID" "/reject $reason" >/dev/null \
        || { fail "gh commented /reject"; issues_done "$LIVE_REPO" "$n"; return; }

    wi_wait_attempt "$WI" "$ISSUES_SEEN_WAIT" Denied Implementing PrOpened Failed Cancelled || true
    attempt=$(wi_attempt "$WI")
    want "the attempt is denied" "$(jq -r '.state // empty' <<<"$attempt")" = Denied
    want "by the GitHub account that commented" "$(jq -r '.approvedBy // empty' <<<"$attempt")" = "github:$ISSUES_APPROVER"
    want "the reason is kept with it, as it was written" \
        "$(jq --arg r "$reason" '(.failureReason // "") | contains($r)' <<<"$attempt")" = true
    want "no Job implemented anything" "$(issues_job_count "$attempt_id" implement)" -eq 0

    # In the status comment, with who and why. Waited for: the comment follows the database by
    # up to a poll.
    _l02_told() { [ "$(gh_bot_comments "$LIVE_REPO" "$n" | jq --arg r "$ISSUES_RUN" '[.[] | select((.body | contains("plan was rejected")) and (.body | contains($r)))] | length')" -ge 1 ]; }
    wait_for "the issue to say the plan was rejected" "$ISSUES_SEEN_WAIT" _l02_told \
        && pass "the status comment says the plan was rejected, and why" \
        || fail "the status comment says the plan was rejected, and why" "no comment of the bot's says so with the reason"
    want "it is still two comments by the bot" "$(gh_bot_comments "$LIVE_REPO" "$n" | jq 'length')" -eq 2

    want "GitHub has no branch for it" "$(issues_git_rev "${branch:-none}")" = ""
    want "and no pull request" "$(live_prs_for "$n" | jq 'length')" -eq 0

    # A refused plan does not end the work item; closing the issue does - and "closed" is
    # something the agent learns by asking GitHub for an issue its list no longer holds.
    want "the work item is still taken while the issue is open" "$(wi_state "$LIVE_REPO" "$n")" = Taken
    issues_done "$LIVE_REPO" "$n"
    wi_wait_state "$LIVE_REPO" "$n" "$ISSUES_SEEN_WAIT" "$WI_CANCELLED" "$WI_DONE" || true
    want "closing the issue ends the work item" \
        "$(_issues_curl "$ISSUES_API/api/workitems/$WI" | jq -r '"\(.state): \(.stateReason)"')" = "$WI_CANCELLED: the issue was closed"
}
