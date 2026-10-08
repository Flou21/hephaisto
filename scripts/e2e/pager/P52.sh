# pager: P52 | mcp | exclusive | an approver's click closes an incident with the reason typed into the card: closed by the roster's name, one audit row
#
# Backlog #124. Closing sits with approval in the console, so in Teams it takes somebody the
# install maps to the approver role: notifications.teamsBot.actions.approvers lists Microsoft
# Entra object ids, and values-pager.yaml lists the one the stand-in gives oncall@example.com.
# The Close button unfolds a card with a required reason; Teams merges what was typed into the
# button's data, which is what the stand-in's "reason" field stands for.
#
# The audit row is read through the MCP timeline, the one place the suite can see audit rows.
# Exclusive for P50's reason: one alert fewer in the shared scenarios' burst.

scenario() {
    mcp_ready || return
    local n id reason answer rows i verbs=x

    n=$(pager_name P52)
    reason="pager suite: closed from the card, run $PAGER_RUN"
    pager_fire "$n" namespace=pager-p52 deployment=clicks
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || { fail "the investigation ended" "state $(pager_state "$id")"; return; }

    pager_wait_verb "$id" close 40 && pass "the alert carries a Close button" \
        || fail "the alert carries a Close button" "verbs drawn: $(pager_card_verbs "$id")"

    answer=$(pager_click "$(jq -cn --arg id "$id" '{incidentId:$id, verb:"close", user:"oncall@example.com", reason:"   "}')")
    grep -qi 'reason' <<<"$(pager_click_said "$answer")" && pass "a close without a reason is told to give one" \
        || fail "a close without a reason is told to give one" "answered: ${answer:0:300}"
    want "and closes nothing" "$(pager_open "$n")" -eq 1

    answer=$(pager_click "$(jq -cn --arg id "$id" --arg r "$reason" '{incidentId:$id, verb:"close", user:"oncall@example.com", reason:$r}')")
    want "an approver's close is answered 200" "$(jq -r '.status // 0' <<<"$answer")" = 200
    want "with the refreshed card" "$(pager_click_said "$answer")" = card
    want "the incident is closed" "$(pager_state "$id")" = Closed
    want "closed by the person the roster names, not the name the click carries" \
        "$(pager_incident "$id" | jq -r '.closedBy // "none"')" = oncall@example.com
    want "with the reason that was typed into the card" \
        "$(pager_incident "$id" | jq --arg r "$reason" '[.transitions[] | select(.to == "Closed") | select(.reason | contains($r))] | length')" -eq 1

    # A second click on a card that has not been refreshed yet: the incident is already closed.
    pager_click "$(jq -cn --arg id "$id" --arg r "$reason" '{incidentId:$id, verb:"close", user:"oncall@example.com", reason:$r}')" >/dev/null

    rows=$(mcp_audit_rows "$PAGER_MCP_TOKEN_READER" "$id")
    want "one audit row of the close, however often the button was pressed" \
        "$(jq '[.[] | select(.action == "incident.closed")] | length' <<<"$rows")" -eq 1
    want "in that person's name" \
        "$(jq -r '[.[] | select(.action == "incident.closed") | .actor] | join(",")' <<<"$rows")" = oncall@example.com

    # The card says how it ended and offers nothing more to press.
    for i in $(seq 1 15); do
        verbs=$(pager_card_verbs "$id")
        [ -z "$verbs" ] && break
        sleep 2
    done
    want "the closed alert carries no button that acts" "${verbs:-none}" = none
    want "nothing was deleted" "$(pager_teams_deletes)" -eq 0
}
