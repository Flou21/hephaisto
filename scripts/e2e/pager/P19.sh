# pager: P19 | - | shared | a rule labelled not to be investigated tells a person and costs no tokens
#
# Decided 2026-09-28 (#134): everything is investigated unless the rule opts out with
# hephaisto_investigate: "false". Such an incident still opens and a person is still told; the
# model is never asked.

scenario() {
    local n id deadline
    n=$(pager_name P19)

    pager_fire "$n" provider=gamma hephaisto_investigate=false
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")

    deadline=$(( SECONDS + 60 ))
    while [ "$SECONDS" -lt "$deadline" ] && [ "$(pager_alerts_sent "$id")" -lt 1 ]; do sleep 3; done
    want "a person was told" "$(pager_alerts_sent "$id")" -ge 1
    sleep 10
    want "the model was never asked" "$(pager_llm "$n" | jq length)" -eq 0
    want "it says it was not investigated" "$(pager_incident "$id" | jq -r '.escalationReason')" = NotInvestigated
}
