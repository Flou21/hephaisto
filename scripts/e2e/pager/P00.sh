# pager: P00 | - | shared | an alert opens an incident, the stand-in model concludes, and a person is told
#
# The suite's own smoke test, green before any v0.10.0 change: if this fails, every other
# scenario's verdict is about the harness and not about the agent. One alert about a workload,
# the stand-in answers at once, the incident escalates, the Teams stand-in holds a personal chat
# message that links to it - and the bot deleted nothing.

scenario() {
    local n id
    n=$(pager_name P00)

    pager_fire "$n" namespace=pager-e2e deployment=smoke
    want "the webhook accepts the alert" "$PAGER_CODE" -ge 200
    want "the webhook answers 2xx" "$PAGER_CODE" -lt 300

    pager_wait_count "$n" 1 60 && pass "an incident opened" || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")

    pager_wait_settled "$id" 120 && pass "its investigation ended" \
        || fail "its investigation ended" "state $(pager_state "$id") after 120s"

    want "the stand-in model was asked about it" "$(pager_llm "$n" | jq length)" -ge 1

    local deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_alerts_sent "$id")" -lt 1 ]; do sleep 3; done
    want "a person was sent a Teams message about it" "$(pager_alerts_sent "$id")" -ge 1
    want "the bot deleted nothing" "$(pager_teams_deletes)" -eq 0
}
