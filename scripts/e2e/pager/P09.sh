# pager: P09 | - | shared | a re-fire inside the reopen window reopens and tells again; after it, a new incident
#
# Decided 2026-09-28 (#129): a resolve closes, and the same alert firing again within the reopen
# window - 24 hours in production, two minutes here - reopens the same incident rather than
# opening a second one. It is still news: a person is told again. After the window it is a new
# incident.

scenario() {
    local n id told
    n=$(pager_name P09)
    local labels="namespace=pager-e2e deployment=payments"

    pager_fire "$n" $labels
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true
    pager_resolve "$n" $labels
    pager_wait_state "$id" 60 Closed || { fail "the resolve closed it" "state $(pager_state "$id")"; return; }
    told=$(pager_alerts_sent "$id")

    pager_fire "$n" $labels
    sleep 15
    want "a re-fire inside the window opened no second incident" "$(pager_count "$n")" -eq 1
    local s
    s=$(pager_state "$id")
    [ "$s" != Closed ] && pass "the incident was reopened" || fail "the incident was reopened" "state $s"
    local deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_alerts_sent "$id")" -le "$told" ]; do sleep 3; done
    want "a person was told again" "$(pager_alerts_sent "$id")" -gt "$told"

    pager_wait_settled "$id" 120 || true
    pager_resolve "$n" $labels
    pager_wait_state "$id" 60 Closed || true
    sleep "${PAGER_REOPEN_WINDOW:-135}"

    pager_fire "$n" $labels
    pager_wait_count "$n" 2 60 && pass "after the window it is a new incident" \
        || fail "after the window it is a new incident" "$(pager_count "$n") incidents"
}
