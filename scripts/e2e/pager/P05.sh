# pager: P05 | - | shared | fire, repeat twice, resolve: one incident, one signal row of count 3, closed
#
# Backlog #129 and #130, the milestone's first sentence. Alertmanager repeats a firing alert and
# then sends one resolve. That is one fault: one incident, whose single alert instance is one
# signal row counted three times, and which is no longer open once the alert cleared.

scenario() {
    local n id
    n=$(pager_name P05)
    local labels="namespace=pager-e2e deployment=orders"

    pager_fire "$n" $labels; sleep 3
    pager_fire "$n" $labels; sleep 3
    pager_fire "$n" $labels

    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    sleep 5
    want "three firings are one incident" "$(pager_count "$n")" -eq 1
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true

    want "one signal row for one alert instance" "$(pager_incident "$id" | jq '.signals | length')" -eq 1
    want "the row counts the three firings" "$(pager_incident "$id" | jq '[.signals[].count] | max')" -eq 3

    pager_resolve "$n" $labels
    pager_wait_state "$id" 60 Closed && pass "the resolve closed it" \
        || fail "the resolve closed it" "state $(pager_state "$id")"
    want "the resolve opened nothing new" "$(pager_count "$n")" -eq 1
    want "nothing about it is open" "$(pager_open "$n")" -eq 0
}
