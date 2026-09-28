# pager: P17 | - | exclusive | a person is told while the model is still thinking
#
# Backlog #133, the milestone's second clause. Until v0.10.0 the first message about an
# incident was sent when its investigation ended - up to ten minutes of wall clock, set by a
# model. The stand-in holds its answer here; the person must be told anyway. Exclusive because
# the hold is global and would stall every other scenario's investigation.

scenario() {
    local n id deadline told=0 s
    n=$(pager_name P17)

    pager_llm_hold
    pager_fire "$n" namespace=pager-e2e deployment=cart
    pager_wait_count "$n" 1 60 || { pager_llm_release; fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")

    deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        told=$(pager_alerts_sent "$id")
        [ "$told" -ge 1 ] && break
        sleep 3
    done
    s=$(pager_state "$id")
    pager_llm_release

    want "a person was told within a minute" "$told" -ge 1
    want "while the investigation was still running" "$s" = Investigating
}
