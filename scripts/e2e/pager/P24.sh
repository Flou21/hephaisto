# pager: P24 | - | shared | a reopen starts the step clock again, and the old acknowledgement does not count
#
# Backlog #149. An acknowledgement belongs to an outage. Once the alert cleared and fired again,
# the person who acknowledged the first one has not seen this one.

told_lead() { pager_teams "$1" | jq '[.[] | select(.kind == "chat") | select(.conversation | contains("lead"))] | length'; }

scenario() {
    local n id
    n=$(pager_name P24)
    local labels="team=pager-a provider=theta"

    pager_fire "$n" $labels
    pager_wait_count "$n" 1 60 || { fail "an incident opened"; return; }
    id=$(pager_first "$n")
    want "acknowledged" "$(pager_ack "$id")" -lt 300
    pager_wait_settled "$id" 120 || true

    pager_resolve "$n" $labels
    pager_wait_state "$id" 60 Closed || { fail "the resolve closed it" "state $(pager_state "$id")"; return; }
    pager_fire "$n" $labels
    sleep 10
    want "the re-fire reopened the same incident" "$(pager_count "$n")" -eq 1
    want "the reopen cleared the acknowledgement" "$(pager_incident "$id" | jq -r '.acknowledgedBy // "none"')" = none

    sleep "${PAGER_STEP_WAIT:-75}"
    want "unacknowledged since the reopen, the step told lead@" "$(told_lead "$id")" -ge 1
}
