# pager: P28 | - | shared | an alert whose name merely contains "watchdog" is an alert, not a heartbeat
#
# Backlog #151. The watchdog is the rule named Watchdog, or one labelled hephaisto_kind=Watchdog.
# A consumer's "watchdog stalled" alert is a fault, and swallowing it as a heartbeat means
# nobody hears of it at all.

scenario() {
    local n
    n="E2ePagerP28ConsumerWatchdogStalled${PAGER_RUN}"

    pager_fire "$n" namespace=pager-e2e deployment=consumer
    pager_wait_count "$n" 1 60 && pass "it opened an incident" || fail "it opened an incident" "none within 60s"
}
