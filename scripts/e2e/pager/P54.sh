# pager: P54 | - | exclusive | approve and deny by click answer only an approver, and an action that is not waiting says so; nothing changes
#
# Backlog #124. Approving from the card is behind a flag of its own
# (notifications.teamsBot.actions.approvals.enabled, on in values-pager.yaml) and takes a
# mapped approver. Two verbs, never one with a boolean, each carrying the id of the action.
#
# WHAT THIS DOES NOT PROVE: an approval that runs. The suite cannot make an action wait for
# approval - the model stand-in answers the planning call with {} (LlmStandIn.Answer, "plans
# nothing"), so no incident here ever reaches AwaitingApproval. The approval itself is unit
# tested (TeamsBotActionsTests); what a running agent proves here is the wire around it: the
# flag reaches the pod, both verbs are answered, the approver map is asked first, and a click
# for an action that is not waiting - a stale card - gets a sentence and changes nothing.
#
# Exclusive for P50's reason: one alert fewer in the shared scenarios' burst.

scenario() {
    local n id verb answer said before

    n=$(pager_name P54)
    pager_fire "$n" namespace=pager-p54 deployment=clicks
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || { fail "the investigation ended" "state $(pager_state "$id")"; return; }
    before=$(pager_state "$id")

    for verb in approve deny; do
        # A member who is not an approver. The incident's own id stands in for an action's: a
        # well-formed id that names no action.
        answer=$(pager_click "$(jq -cn --arg id "$id" --arg v "$verb" '{incidentId:$id, verb:$v, user:"dev@example.com", actionId:$id}')")
        grep -qi 'approver' <<<"$(pager_click_said "$answer")" && pass "$verb by a member who is not an approver says it needs the role" \
            || fail "$verb by a member who is not an approver says it needs the role" "answered: ${answer:0:300}"

        answer=$(pager_click "$(jq -cn --arg id "$id" --arg v "$verb" '{incidentId:$id, verb:$v, user:"oncall@example.com", actionId:$id}')")
        said=$(pager_click_said "$answer")
        want "$verb by an approver is answered 200" "$(jq -r '.status // 0' <<<"$answer")" = 200
        grep -qiE 'no longer exists|already decided' <<<"$said" && pass "$verb of an action that is not waiting says so" \
            || fail "$verb of an action that is not waiting says so" "answered: ${answer:0:300}"

        answer=$(pager_click "$(jq -cn --arg id "$id" --arg v "$verb" '{incidentId:$id, verb:$v, user:"oncall@example.com"}')")
        grep -qi 'named no action' <<<"$(pager_click_said "$answer")" && pass "$verb that names no action is refused" \
            || fail "$verb that names no action is refused" "answered: ${answer:0:300}"
    done

    sleep 3
    want "the incident is where it was" "$(pager_state "$id")" = "$before"
    want "and has no action anybody decided" \
        "$(pager_incident "$id" | jq '[.actions[]? | select(.approvedBy != null)] | length')" -eq 0

    pager_resolve "$n" namespace=pager-p54 deployment=clicks
}
