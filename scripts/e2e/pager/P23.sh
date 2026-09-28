# pager: P23 | - | shared | unacknowledged past the step, the next person is told once; acknowledged in time, not at all
#
# Backlog #142. The suite's values give team=pager-a's route a step: unacknowledged after 30
# seconds, tell lead@. An acknowledgement stops the steps. A step fires once, however long the
# incident then stays unacknowledged.

told_lead() { pager_teams "$1" | jq '[.[] | select(.kind == "chat") | select(.conversation | contains("lead"))] | length'; }

scenario() {
    local slow quick a b
    slow=$(pager_name P23)
    quick=$(pager_name P23Ack)

    pager_fire "$slow" team=pager-a provider=zeta
    pager_fire "$quick" team=pager-a provider=eta
    pager_wait_count "$slow" 1 60 && pager_wait_count "$quick" 1 60 || { fail "both incidents opened"; return; }
    a=$(pager_first "$slow"); b=$(pager_first "$quick")

    want "the acknowledgement was accepted" "$(pager_ack "$b")" -lt 300

    sleep "${PAGER_STEP_WAIT:-75}"
    want "unacknowledged, the step told lead@" "$(told_lead "$a")" -ge 1
    want "acknowledged in time, lead@ was not told" "$(told_lead "$b")" -eq 0

    sleep 45
    want "the step fired once" "$(told_lead "$a")" -eq 1
}
