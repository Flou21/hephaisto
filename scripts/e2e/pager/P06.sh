# pager: P06 | - | shared | a repeat long after the first firing is absorbed, and nobody is told twice
#
# Backlog #130. Alertmanager repeats on repeat_interval - hours in production, forty seconds
# here, which is past both the burst and the correlation windows the suite's values set. The
# open incident absorbs it however old its last signal is.

scenario() {
    local n id before
    n=$(pager_name P06)
    local labels="namespace=pager-e2e deployment=search"

    pager_fire "$n" $labels
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true
    sleep "${PAGER_PAST_WINDOWS:-45}"
    before=$(pager_alerts_sent "$id")

    pager_fire "$n" $labels
    sleep 15
    want "the repeat opened no second incident" "$(pager_count "$n")" -eq 1
    want "the repeat sent nobody a second message" "$(pager_alerts_sent "$id")" -eq "$before"
}
