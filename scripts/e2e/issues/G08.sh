# issues: G08 | kubectl | exclusive | an agent restart while an issue is being planned leaves exactly one attempt
#
# The poller keeps nothing in memory that it could not ask GitHub and the database again: no
# queue, no "seen" list. So a pod that is replaced between finding the issue and collecting its
# plan comes back, looks, and must recognise its own work - one work item, one attempt, one
# plan on the issue - instead of starting over beside it.

scenario() {
    issues_ready || return
    local n attempt_id files

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G08 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it.") || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"

    wi_wait "$ISSUES_REPO" "$n" && pass "the assigned issue became a work item" \
        || { fail "the assigned issue became a work item" "none within ${ISSUES_SEEN_WAIT}s"; issues_done "$ISSUES_REPO" "$n"; return; }

    kc -n "$ISSUES_NS" delete pod -l "app.kubernetes.io/name=${ISSUES_DEPLOY}" --wait=false >/dev/null 2>&1
    sleep 5
    kc -n "$ISSUES_NS" rollout status "deploy/$ISSUES_DEPLOY" --timeout=300s >/dev/null 2>&1 || true
    wait_for "the agent to answer again" 300 issues_agent_up && pass "the agent came back" \
        || { fail "the agent came back" "no answer on /healthz within 300s"; issues_done "$ISSUES_REPO" "$n"; return; }

    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    # And two polls later, so that the restarted agent has had the chance to start over.
    gh_wait_polls "$(gh_mark)" 2 || true

    want "still one work item for the issue" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "still one attempt" "$(wi_attempt_count "$WI")" -eq 1
    want "one Job planned it" "$(issues_job_count "$attempt_id" plan)" -eq 1

    files=$(jq -c '.files // []' <<<"$ATTEMPT")
    want "the plan was posted once" \
        "$(gh_bot_comments "$ISSUES_REPO" "$n" | jq --argjson f "$files" '[.[] | select(.body as $b | ($f | length) > 0 and all($f[]; . as $p | $b | contains($p)))] | length')" -eq 1

    issues_done "$ISSUES_REPO" "$n"
}
