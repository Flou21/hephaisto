# pager: P39 | mcp | shared | a shared token acknowledges as mcp/pager-shared; the claimed name stays a claim
#
# Backlog #157, F4, the user's decision of 2026-09-29: a gateway's token may write, and is
# recorded as itself. Acknowledging stops the paging, so a shared token must name the person it
# acts for; that name is stored as claimed and unverified - also when it happens to be the name
# of a real user of the agent - and is never recorded as the actor.

scenario() {
    mcp_ready || return
    local n id r detail rows

    n=$(pager_name P39)
    pager_fire "$n" namespace=pager-p39 deployment=orders
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")

    r=$(mcp_call "$PAGER_MCP_TOKEN_SHARED" acknowledge_incident "$(jq -cn --arg id "$id" '{id:$id}')")
    want "a shared token acknowledging for nobody is refused" "$(jq -r '.isError' <<<"$r")" = true
    want "and nothing was acknowledged" "$(pager_incident "$id" | jq -r '.acknowledgedBy // "none"')" = none

    r=$(mcp_call "$PAGER_MCP_TOKEN_SHARED" acknowledge_incident "$(jq -cn --arg id "$id" '{id:$id, onBehalfOf:"pager-reader"}')")
    want "on behalf of a named person it is accepted" "$(jq -r '.isError' <<<"$r")" = false

    detail=$(pager_incident "$id")
    want "the actor is the token" "$(jq -r '.acknowledgedBy' <<<"$detail")" = mcp/pager-shared
    want "the person is stored as claimed" "$(jq -r '.acknowledgedClaimedBy' <<<"$detail")" = pager-reader

    rows=$(mcp_audit_rows "$PAGER_MCP_TOKEN_READER" "$id")
    want "the audit row keeps the claim unverified" \
        "$(jq '[.[] | select(.actor == "mcp/pager-shared" and .origin.claimedBy == "pager-reader" and .origin.claimVerified == false)] | length' <<<"$rows")" -ge 1
}
