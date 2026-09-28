# pager: P10 | - | shared | four clusters firing one workload alert: four incidents, none suppressed, all told
#
# Backlog #131 and #147. Five clusters share one Alertmanager in the install this release is
# for, told apart by a `cluster` label. The same deployment name in four of them is four faults.
# And a flap detector counting by workload name alone would suppress the fourth - a page nobody
# receives, for a reason that is not a flap.

scenario() {
    local n c
    n=$(pager_name P10)

    for c in pager-a pager-b pager-c pager-d; do
        pager_fire "$n" cluster=$c namespace=pager-e2e deployment=api
    done

    pager_wait_count "$n" 4 60 && pass "four incidents opened" \
        || fail "four incidents opened" "$(pager_count "$n") after 60s"
    want "none was suppressed" "$(pager_incidents "$n" | jq '[.[] | select(.state == "Suppressed")] | length')" -eq 0

    local deadline=$(( SECONDS + 120 )) told=0 id
    while [ "$SECONDS" -lt "$deadline" ]; do
        told=0
        for id in $(pager_incidents "$n" | jq -r '.[].id'); do
            [ "$(pager_alerts_sent "$id")" -ge 1 ] && told=$((told + 1))
        done
        [ "$told" -ge 4 ] && break
        sleep 5
    done
    want "a person was told about each of them" "$told" -ge 4
}
