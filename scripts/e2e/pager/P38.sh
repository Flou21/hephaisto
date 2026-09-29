# pager: P38 | mcp | shared | no token 401, a wrong token 401, /mcp on the console port 404, /api on the MCP port 404
#
# Backlog #157, F1. The endpoint is its own port with its own credentials. A missing and a wrong
# token look the same from outside; the console port has no /mcp and the MCP port has nothing
# else. The positive control is a right token answering 200.

scenario() {
    mcp_ready || return
    local base="${PAGER_MCP%/mcp}" headers

    want "a right token is answered" "$(mcp_raw "$PAGER_MCP" "Bearer $PAGER_MCP_TOKEN_READER")" = 200
    want "no token is 401" "$(mcp_raw "$PAGER_MCP")" = 401
    want "a wrong token is 401" "$(mcp_raw "$PAGER_MCP" "Bearer not-a-token-not-a-token-not-a-token-xx")" = 401
    headers=$(_pager_curl -s -D - -o /dev/null -X POST "$PAGER_MCP" -H 'Content-Type: application/json' --data '{}')
    want "a 401 says Bearer" "$(grep -ci '^www-authenticate: bearer' <<<"$headers")" -ge 1
    want "/mcp on the console port is 404" "$(mcp_raw "$PAGER_API/mcp" "Bearer $PAGER_MCP_TOKEN_READER")" = 404
    want "/api on the MCP port is 404" "$(mcp_raw "$base/api/incidents" "Bearer $PAGER_MCP_TOKEN_READER" GET)" = 404
}
