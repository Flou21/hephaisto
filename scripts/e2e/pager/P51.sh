# pager: P51 | - | exclusive | a click re-investigates an incident nobody diagnosed, as the person the roster names, and the model is asked again
#
# Backlog #124. Asking for another attempt is read-level in the console - no approver role - so
# any member of the team may click it, and the member here is deliberately one who is NOT an
# approver (values-pager.yaml names only oncall@). The button is drawn where the console offers
# the retry: an escalated incident whose investigations found nothing. The model stand-in's one
# finding cites nothing, so every incident of this suite is one of those.
#
# Exclusive for P50's reason, not because it disturbs anything: one alert fewer in the burst the
# shared scenarios fire into the investigation queue in the same second.

scenario() {
    local n id before after=0 i answer

    n=$(pager_name P51)
    pager_fire "$n" namespace=pager-p51 deployment=clicks
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || { fail "the first investigation ended" "state $(pager_state "$id")"; return; }
    want "it escalated with nothing found" "$(pager_state "$id")" = Escalated

    pager_wait_verb "$id" reinvestigate 40 && pass "the alert carries a Reinvestigate button" \
        || fail "the alert carries a Reinvestigate button" "verbs drawn: $(pager_card_verbs "$id")"

    before=$(pager_llm "$n" | jq 'length')

    answer=$(pager_click "$(jq -cn --arg id "$id" '{incidentId:$id, verb:"reinvestigate", user:"dev@example.com"}')")
    want "a member's click is answered 200" "$(jq -r '.status // 0' <<<"$answer")" = 200
    want "with the refreshed card" "$(pager_click_said "$answer")" = card

    for i in $(seq 1 45); do
        after=$(pager_llm "$n" | jq 'length')
        [ "${after:-0}" -gt "${before:-0}" ] && break
        sleep 2
    done
    want "and the model is asked again" "${after:-0}" -gt "${before:-0}"

    want "the request is recorded as the roster names the person, not as the click does" \
        "$(pager_incident "$id" | jq '[.transitions[] | select(.to == "Investigating") | select(.reason | contains("requested by dev@example.com"))] | length')" -eq 1

    pager_wait_settled "$id" 90 || true
    pager_resolve "$n" namespace=pager-p51 deployment=clicks
}
