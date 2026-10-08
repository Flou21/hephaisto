# issues: G09 | kubectl | exclusive | GitHub answering 500 or a rate limit is a degraded dependency that recovers: no crash, no duplicate work
#
# GitHub is somebody else's computer. While it answers 500, and while it answers the 403 that
# is its rate limit, the agent says so where it says how every other dependency is doing - and
# does nothing else about it: the next poll is the retry. When GitHub is back the row is
# healthy again, the work that was there is there once, and an issue assigned in the meantime
# is found.

scenario() {
    issues_ready || return
    local n later before mode

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G09 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"
    wi_wait "$ISSUES_REPO" "$n" && pass "the assigned issue became a work item" \
        || { fail "the assigned issue became a work item" "none within ${ISSUES_SEEN_WAIT}s"; issues_done "$ISSUES_REPO" "$n"; return; }

    wait_for "GitHub to be reported healthy" "$ISSUES_SEEN_WAIT" issues_github_is Healthy \
        && pass "GitHub is a dependency the agent reports on, and it is healthy" \
        || fail "GitHub is a dependency the agent reports on, and it is healthy" "it is '$(issues_github_health)'"
    before=$(issues_agent_restarts)

    for mode in 500 rate-limit; do
        gh_fail "$mode" 100000
        wait_for "GitHub to be reported as failing ($mode)" "$ISSUES_SEEN_WAIT" issues_github_is Degraded Unreachable \
            && pass "while GitHub answers $mode it is reported as degraded" \
            || fail "while GitHub answers $mode it is reported as degraded" "it is '$(issues_github_health)'"
        issues_agent_up && pass "the agent answers while GitHub answers $mode" || fail "the agent answers while GitHub answers $mode"

        gh_fail off
        wait_for "GitHub to be reported healthy again" "$ISSUES_SEEN_WAIT" issues_github_is Healthy \
            && pass "after $mode it recovers by itself" \
            || fail "after $mode it recovers by itself" "it is '$(issues_github_health)'"
    done

    want "the agent's pod was neither restarted nor replaced" "$(issues_agent_restarts)" = "$before"
    want "the issue is still one work item" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "with one attempt at most" "$(wi_attempt_count "$(wi_id "$ISSUES_REPO" "$n")")" -le 1

    later=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G09 "assigned after GitHub came back")" "Found by the next poll.")
    gh_assign "$ISSUES_REPO" "$later"
    wi_wait "$ISSUES_REPO" "$later" && pass "an issue assigned afterwards is found" \
        || fail "an issue assigned afterwards is found" "none within ${ISSUES_SEEN_WAIT}s"

    issues_done "$ISSUES_REPO" "$n" "$later"
}
