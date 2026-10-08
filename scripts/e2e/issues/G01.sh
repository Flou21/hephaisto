# issues: G01 | kubectl | exclusive | an assigned issue is planned by one Job, and the plan is a comment on the issue
#
# The milestone's first sentence (#243). Somebody assigns an issue to Hephaisto's account and
# does nothing else: the agent finds it by polling, one Job plans it, and the plan is where the
# person who asked will look - on the issue. Nothing is implemented, because nobody has said
# yes. (That the comment says how to say yes is G03's: it is true one stage later.)

scenario() {
    issues_ready || return
    local n attempt_id files named told

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

    # And the people a route names are told (#248): the same three moments an incident's code
    # fix announces, through the outbox. The dev values route CodeFixPlanReady to the Teams bot,
    # so each of its recipients gets one message in their chat - which names the issue, says the
    # plan is answered there or in the console, and links the attempt's own page. The board
    # stays a board of incidents.
    if issues_route_takes CodeFixPlanReady; then
        _g01_told() { [ "$(issues_teams_alerts "$attempt_id" | jq 'length')" -ge 1 ]; }
        wait_for "the plan to be announced in Teams" 90 _g01_told || true
        told=$(issues_teams_alerts "$attempt_id")
        want "a plan-ready message for the work item reached a person's chat" "$(jq 'length' <<<"$told")" -ge 1
        want "it names the issue by its reference" "$(jq --arg i "$ISSUES_REPO#$n" '[.[] | select(.text | contains($i))] | length' <<<"$told")" -ge 1
        want "its lock-screen line is the event and the reference" \
            "$(jq -r '.[0].summary // ""' <<<"$told")" = "Code fix planned for $ISSUES_REPO#$n"
        want "it says where the plan is answered" "$(jq '[.[] | select(.text | test("/approve")) | select(.text | test("in the console"))] | length' <<<"$told")" -ge 1
        want "nobody was told twice" "$(jq '[group_by(.conversation)[] | length] | max // 0' <<<"$told")" -eq 1
        want "the board has no card for a work item" "$(issues_teams_board_mentions "/codefixes/$attempt_id")" -eq 0
    else
        skip "a plan-ready message for the work item reached a person's chat" "no route of this install sends CodeFixPlanReady to the Teams bot"
    fi

    issues_done "$ISSUES_REPO" "$n"
}
