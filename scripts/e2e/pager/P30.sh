# pager: P30 | mcp | shared | each role lists its own tools, equal to the reviewed list, in order
#
# Backlog #157, F5. scripts/e2e/mcp/tools.golden.json is the reviewed list: names, order and who
# may call each. A token that may not write sees the reads; one that may write sees the writes a
# reader may make; an approver sees everything. The order is part of it, because a gateway's
# tool search breaks ties by registry order.

scenario() {
    mcp_ready || return
    local golden="$HERE/mcp/tools.golden.json" want_list got

    want_list=$(jq -c '[.tools[] | select(.needs == "reader") | .name]' "$golden")
    got=$(mcp_tools "$PAGER_MCP_TOKEN_READONLY")
    want "a read-only token lists exactly the reads, in order" "$got" = "$want_list"

    want_list=$(jq -c '[.tools[] | select(.needs != "approver") | .name]' "$golden")
    got=$(mcp_tools "$PAGER_MCP_TOKEN_READER")
    want "a reader that may write lists the reads and a reader's writes, in order" "$got" = "$want_list"

    want_list=$(jq -c '[.tools[].name]' "$golden")
    got=$(mcp_tools "$PAGER_MCP_TOKEN_APPROVER")
    want "an approver lists every tool, in order" "$got" = "$want_list"

    got=$(mcp_tool_list "$PAGER_MCP_TOKEN_APPROVER" | jq -c '[.[] | {name, description}]')
    want "every description is the reviewed one" "$got" = "$(jq -c '[.tools[] | {name, description}]' "$golden")"
}
