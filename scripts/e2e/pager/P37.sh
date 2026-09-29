# pager: P37 | mcp | shared | a reader cannot close; an approver can, and the audit row names the token and says mcp
#
# Backlog #157, F4. Closing takes an incident out of the open set on a person's judgement, and
# sits behind the approver role on the API; it does here too. The audit row of the close says it
# came through MCP and which token made it.

scenario() {
    mcp_ready || return
    local n id r rows

    n=$(pager_name P37)
    pager_fire "$n" namespace=pager-p37 deployment=ledger
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" close_incident "$(jq -cn --arg id "$id" '{id:$id, reason:"pager suite"}')")
    want "a reader's close is refused" "$(jq -r '(.error != null) or .isError' <<<"$r")" = true
    want "and the incident is still open" "$(pager_open "$n")" -eq 1

    r=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" close_incident "$(jq -cn --arg id "$id" '{id:$id, reason:"pager suite"}')")
    want "an approver's close is accepted" "$(jq -r '.isError' <<<"$r")" = false
    want "the incident is closed" "$(pager_state "$id")" = Closed
    want "closed by the approver" "$(pager_incident "$id" | jq -r '.closedBy')" = pager-approver

    rows=$(mcp_audit_rows "$PAGER_MCP_TOKEN_APPROVER" "$id")
    want "the audit row of the close says mcp and names the token" \
        "$(jq '[.[] | select(.origin.source == "mcp" and .origin.token == "pager-approver" and .actor == "pager-approver")] | length' <<<"$rows")" -ge 1
}
