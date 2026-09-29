# pager: P29 | - | shared | the suite's MCP driver lists and calls a tool on a server known to work
#
# Backlog #157, F0. The control for every MCP scenario after it: the decoy server in the stand-in
# (infra/e2e/notification-receiver/DecoyMcp.cs) is not the agent, and it works. If the driver in
# lib/mcp.sh cannot list and call a tool there, a red P30-P48 says nothing about the agent.

scenario() {
    local decoy="$PAGER_STANDIN/decoy/mcp" tools call

    tools=$(mcp_tools "" "$decoy")
    want "the decoy answers tools/list over HTTP" "$(mcp_rpc "" tools/list "{}" "$decoy" | jq "._http")" = 200
    want "it lists the neighbour tools" "$(jq 'length' <<<"$tools")" -eq \
        "$(jq '.tools | length' "$HERE/mcp/neighbour-tools.json")"

    call=$(mcp_call "" search_incidents '{"query":"pager-suite-echo"}' "$decoy")
    want "a tool call is not an error" "$(jq -r '.isError' <<<"$call")" = false
    want "the call's text is what the tool returned" "$(mcp_json "$call" | jq -r '.arguments.query')" = pager-suite-echo
}
