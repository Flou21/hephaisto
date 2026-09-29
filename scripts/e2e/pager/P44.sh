# pager: P44 | mcp | shared | an incident with no code fix says why none was started; the list of code fixes answers
#
# Backlog #157, F2: read access to code fixes. Most incidents have none, and "none" is only useful
# with its reason - the stage is off, the workload maps to no repository, the finding was not
# grounded. The list across incidents answers whatever the install has.

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
}
