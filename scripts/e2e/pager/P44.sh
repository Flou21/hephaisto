# pager: P44 | mcp | shared | an incident with no code fix says why none was started; the lists of code fixes and of work items answer
#
# Backlog #157, F2: read access to code fixes. Most incidents have none, and "none" is only useful
# with its reason - the stage is off, the workload maps to no repository, the finding was not
# grounded. The list across incidents answers whatever the install has.
#
# And the same for work items (#248): the two tools answer on every install - with rows where
# GitHub issues are taken as work, and with an empty list that says so where they are not. What
# a row holds is the issues suite's and the integration tests'; here it is that a running
# install serves the tools at all, to a reader's token.

scenario() {
    mcp_ready || return
    local n id r

    n=$(pager_name P44)
    pager_fire "$n" namespace=pager-p44 deployment=search
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || true

    r=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_code_fix "$(jq -cn --arg id "$id" '{incidentId:$id}')")")
    want "the incident has no code fix" "$(jq '.attempts | length' <<<"$r")" -eq 0
    want "and says why" "$(jq -r '.why // ""' <<<"$r")" != ""

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" list_code_fixes '{"limit":5}')
    want "the list of code fixes answers" "$(jq -r '.isError' <<<"$r")" = false
    want "with a list" "$(mcp_json "$r" | jq -r '.codeFixes | type')" = array

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" list_work_items '{"limit":5}')
    want "the list of work items answers" "$(jq -r '.isError' <<<"$r")" = false
    want "with a list" "$(mcp_json "$r" | jq -r '.workItems | type')" = array
    want "and says whether issues are taken at all" "$(mcp_json "$r" | jq -r '.enabled | type')" = boolean

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" get_work_item '{"repository":"nobody/nothing","number":1}')
    want "a work item nobody has is an error" "$(jq -r '.isError' <<<"$r")" = true
    want "that says where to look" "$(jq -r '.text' <<<"$r" | grep -c 'list_work_items')" -ge 1
}
