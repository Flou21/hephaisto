# pager: P53 | - | exclusive | a team member who is not an approver cannot close by click: the answer says why, and nothing changes
#
# Backlog #124. A card is the same for everybody who reads it, so the Close button is in front
# of people who may not press it, and the check is made at the click: the token says Microsoft
# sent it, the roster says who it is, and notifications.teamsBot.actions.approvers says whether
# that person may close. dev@example.com is in the stand-in's team and not on that list.
#
# The refusal is an answer the person reads in Teams - a click that failed silently is a person
# who thinks they closed something - and it is what makes this scenario red until the verb
# exists: before, the same click was refused as a verb nobody knew.
#
# Exclusive for P50's reason: one alert fewer in the shared scenarios' burst.

scenario() {
    local n id answer

    n=$(pager_name P53)
    pager_fire "$n" namespace=pager-p53 deployment=clicks
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || { fail "the investigation ended" "state $(pager_state "$id")"; return; }

    answer=$(pager_click "$(jq -cn --arg id "$id" '{incidentId:$id, verb:"close", user:"dev@example.com", reason:"pager suite: this must not close"}')")
    want "the click is answered, not dropped" "$(jq -r '.status // 0' <<<"$answer")" = 200
    grep -qi 'approver' <<<"$(pager_click_said "$answer")" && pass "the answer says closing needs the approver role" \
        || fail "the answer says closing needs the approver role" "answered: ${answer:0:300}"

    # Somebody outside the team is not answered at all, as for every other verb.
    answer=$(pager_click "$(jq -cn --arg id "$id" '{incidentId:$id, verb:"close", user:"outsider@elsewhere.example", reason:"pager suite: nor this"}')")
    want "somebody outside the team is refused outright" "$(jq -r '.status // 0' <<<"$answer")" = 403

    sleep 3
    want "the incident is still open" "$(pager_open "$n")" -eq 1
    want "nobody closed it" "$(pager_incident "$id" | jq -r '.closedBy // "none"')" = none
    want "and it never moved to Closed" \
        "$(pager_incident "$id" | jq '[.transitions[] | select(.to == "Closed")] | length')" -eq 0

    pager_resolve "$n" namespace=pager-p53 deployment=clicks
}
