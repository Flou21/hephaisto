# pager: P40 | mcp | shared | a token switched to read-only cannot write; the same call with a writing token can
#
# Backlog #157, F4. The control for P39: a gateway's token can be made read-only, and then the
# writes are neither listed nor callable. The same acknowledgement through a token that may
# write goes through, so the refusal is the switch and not a broken tool.

scenario() {
    mcp_ready || return
    local n id r args

    n=$(pager_name P40)
    pager_fire "$n" namespace=pager-p40 deployment=billing
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    args=$(jq -cn --arg id "$id" '{id:$id, onBehalfOf:"pager-oncall"}')

    want "a read-only token does not list acknowledge_incident" \
        "$(mcp_tools "$PAGER_MCP_TOKEN_READONLY" | jq 'index("acknowledge_incident") == null')" = true
    r=$(mcp_call "$PAGER_MCP_TOKEN_READONLY" acknowledge_incident "$args")
    want "and cannot call it" "$(jq -r '(.error != null) or .isError' <<<"$r")" = true
    want "nothing was acknowledged" "$(pager_incident "$id" | jq -r '.acknowledgedBy // "none"')" = none

    r=$(mcp_call "$PAGER_MCP_TOKEN_SHARED" acknowledge_incident "$args")
    want "the same call with a writing token is accepted" "$(jq -r '.isError' <<<"$r")" = false
    want "and acknowledged it" "$(pager_incident "$id" | jq -r '.acknowledgedBy')" = mcp/pager-shared
}
