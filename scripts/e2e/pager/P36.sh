# pager: P36 | mcp | shared | acknowledge, assign, feedback and a note entry, as the person the token is
#
# Backlog #157, F4. Each write lands where the console's would, under the token's person: the
# acknowledgement and the assignment on the incident, the feedback in its list, the entry under
# the alert's note.

scenario() {
    mcp_ready || return
    local n id detail note

    n=$(pager_name P36)
    pager_fire "$n" namespace=pager-p36 deployment=cart
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")

    want "acknowledge is accepted" \
        "$(mcp_call "$PAGER_MCP_TOKEN_READER" acknowledge_incident "$(jq -cn --arg id "$id" '{id:$id}')" | jq -r '.isError')" = false
    want "assign to me is accepted" \
        "$(mcp_call "$PAGER_MCP_TOKEN_READER" assign_incident "$(jq -cn --arg id "$id" '{id:$id, assignee:"me"}')" | jq -r '.isError')" = false
    want "feedback is accepted" \
        "$(mcp_call "$PAGER_MCP_TOKEN_READER" submit_incident_feedback "$(jq -cn --arg id "$id" '{id:$id, helpful:true, comment:"pager suite"}')" | jq -r '.isError')" = false
    want "a note entry is accepted" \
        "$(mcp_call "$PAGER_MCP_TOKEN_READER" add_alert_note_entry "$(jq -cn --arg n "$n" --arg id "$id" '{alertName:$n, text:"restarted it", incidentId:$id}')" | jq -r '.isError')" = false

    detail=$(pager_incident "$id")
    want "acknowledged by the token's person" "$(jq -r '.acknowledgedBy' <<<"$detail")" = pager-reader
    want "assigned to the token's person" "$(jq -r '.assignedTo' <<<"$detail")" = pager-reader
    want "the feedback is the person's" "$(jq -r '[.feedback[].submittedBy] | last' <<<"$detail")" = pager-reader

    note=$(_pager_curl "$PAGER_API/api/alerts/$n/note")
    want "the note entry is the person's" "$(jq -r '[.entries[].author] | last' <<<"$note")" = pager-reader
}
