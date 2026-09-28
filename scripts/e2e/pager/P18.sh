# pager: P18 | - | exclusive | the escalation that follows edits what was sent instead of sending it again
#
# Backlog #133. Once a person has been told an incident opened, the investigation's end is an
# update to that message, not a second alert with the same headline - an edit notifies nobody,
# which is right for somebody who already knows.

scenario() {
    local n id deadline at_open
    n=$(pager_name P18)

    pager_llm_hold
    pager_fire "$n" namespace=pager-e2e deployment=inventory
    pager_wait_count "$n" 1 60 || { pager_llm_release; fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_alerts_sent "$id")" -lt 1 ]; do sleep 3; done
    at_open=$(pager_alerts_sent "$id")
    pager_llm_release

    want "a person was told at open" "$at_open" -ge 1
    pager_wait_state "$id" 120 Escalated || { fail "the investigation escalated" "state $(pager_state "$id")"; return; }
    sleep 25

    want "the escalation sent no second alert to the same person" "$(pager_alerts_sent "$id")" -eq "$at_open"
    want "it edited the one that was sent" "$(pager_teams "$id" | jq '[.[] | select(.kind == "chat") | .edits] | max // 0')" -ge 1
}
