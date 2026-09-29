# pager: P42 | mcp | shared | no tool approves, denies, re-arms or sets a mode; calling one is calling a tool that never existed
#
# Backlog #157, F5: the doors that stay shut. Approving an action or a code-fix plan, re-arming
# the kill switch and choosing a mode are a person's, in the console, and a model that is told to
# do one finds no tool for it. Asked for one by name anyway, the server answers exactly as it
# does for a name that never existed - not "forbidden", which would say the door is there.

scenario() {
    mcp_ready || return
    local tools bad unknown asked

    tools=$(mcp_tools "$PAGER_MCP_TOKEN_APPROVER")
    want "an approver lists at least twenty tools" "$(jq 'length' <<<"$tools")" -ge 20
    want "close_incident is among them" "$(jq 'index("close_incident") != null' <<<"$tools")" = true
    bad=$(jq -r '[.[] | select(test("approve|deny|re_?arm|mode|kill|execute|delete|create_incident"))] | join(",")' <<<"$tools")
    want "none approves, denies, re-arms, sets a mode, executes or deletes" "${bad:-none}" = none

    unknown=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" pager_suite_no_such_tool '{}' | jq -c '.error.code')
    for name in approve_action deny_action approve_code_fix_plan re_arm set_mode; do
        asked=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" "$name" '{}' | jq -c '.error.code')
        want "$name is answered like a tool that never existed" "$asked" = "$unknown"
    done
}
