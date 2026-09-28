# pager: P11 | - | shared | a person closing a still-firing incident is not paged again by its repeats
#
# A person decided this one is handled while the alert still fires - a known fault, a fix
# waiting on a deploy. Alertmanager keeps repeating it. Each repeat must not open a new incident
# and page again: that is a page every repeat_interval until somebody silences the rule.

scenario() {
    local n id told code
    n=$(pager_name P11)
    local labels="namespace=pager-e2e deployment=reports"

    pager_fire "$n" $labels
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true

    code=$(pager_close "$id")
    want "a person closed it" "$code" -lt 300
    # Whatever the close itself sends has been sent before the count is taken.
    sleep 15
    told=$(pager_alerts_sent "$id")

    pager_fire "$n" $labels
    sleep 15
    want "the repeat opened nothing" "$(pager_count "$n")" -eq 1
    want "it stays closed" "$(pager_state "$id")" = Closed
    want "nobody was paged by the repeat" "$(pager_alerts_sent "$id")" -eq "$told"
}
