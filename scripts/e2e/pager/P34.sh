# pager: P34 | mcp | shared | an alert that fired three times: the history says three, and how each ended
#
# Backlog #157, F2: "has this happened before". One alert name, three workloads in three
# namespaces, so three incidents: one closed by a person, one ended by its alert resolving (which
# closes it, as hephaisto/alertmanager), one still open. get_incident_history for the alert name
# counts three and says how each ended.

scenario() {
    mcp_ready || return
    local n h a b

    n=$(pager_name P34)
    pager_fire "$n" namespace=pager-p34-a deployment=one
    pager_fire "$n" namespace=pager-p34-b deployment=two
    pager_fire "$n" namespace=pager-p34-c deployment=three
    pager_wait_count "$n" 3 90 || { fail "three incidents opened" "$(pager_count "$n") within 90s"; return; }

    a=$(pager_incidents "$n" | jq -r '[.[] | select(.namespace == "pager-p34-a")][0].id')
    b=$(pager_incidents "$n" | jq -r '[.[] | select(.namespace == "pager-p34-b")][0].id')
    want "a person closes the first" "$(pager_close "$a")" -lt 300
    pager_resolve "$n" namespace=pager-p34-b deployment=two
    pager_wait_state "$b" 60 Closed Resolved || fail "the second ended with its alert" "state $(pager_state "$b")"

    h=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_incident_history "$(jq -cn --arg n "$n" '{alertName:$n}')")")
    want "the history counts three" "$(jq '.total' <<<"$h")" -eq 3
    want "one was closed by a person" "$(jq '.endings.closedByPerson' <<<"$h")" -eq 1
    want "one ended with its alert" "$(jq '.endings.endedByAlert + .endings.resolved' <<<"$h")" -eq 1
    want "one is still open" "$(jq '.endings.open' <<<"$h")" -eq 1
    want "it lists the three" "$(jq '.incidents | length' <<<"$h")" -eq 3
    want "the buckets add up" "$(jq '[.buckets[].count] | add' <<<"$h")" -eq 3
}
