# issues: G01 | kubectl | exclusive | an assigned issue is planned by one Job, and the plan is a comment on the issue
#
# The milestone's first sentence (#243). Somebody assigns an issue to Hephaisto's account and
# does nothing else: the agent finds it by polling, one Job plans it, and the plan is where the
# person who asked will look - on the issue. Nothing is implemented, because nobody has said
# yes. (That the comment says how to say yes is G03's: it is true one stage later.)

scenario() {
    issues_ready || return
    local n attempt_id files named

    n=$(gh_issue_create "$ISSUES_REPO" "$(issues_title G01 "the order total is null for an empty cart")" \
        "Open the cart with nothing in it. The total reads null where it should read 0.") \
        || { fail "the stand-in opened an issue"; return; }
    gh_assign "$ISSUES_REPO" "$n"

    issues_plan_ready "$ISSUES_REPO" "$n" || { issues_done "$ISSUES_REPO" "$n"; return; }
    attempt_id=$(jq -r .id <<<"$ATTEMPT")

    want "one work item for the issue" "$(wi_count "$ISSUES_REPO" "$n")" -eq 1
    want "one attempt for the work item" "$(wi_attempt_count "$WI")" -eq 1
    want "one Job planned it" "$(issues_job_count "$attempt_id" plan)" -eq 1
    want "nothing was implemented before anybody approved" "$(issues_job_count "$attempt_id" implement)" -eq 0

    # What an approver approves is the plan in Postgres, so the comment has to be that plan. Held
    # to the files it names rather than to its prose: a path survives any rendering.
    files=$(jq -c '.files // []' <<<"$ATTEMPT")
    want "the plan names the files it would touch" "$(jq 'length' <<<"$files")" -ge 1
    named=$(gh_bot_comments "$ISSUES_REPO" "$n" \
        | jq --argjson f "$files" '[.[] | select(.body as $b | ($f | length) > 0 and all($f[]; . as $p | $b | contains($p)))] | length')
    want "the plan is one comment on the issue, written by the bot" "$named" -eq 1

    issues_done "$ISSUES_REPO" "$n"
}
