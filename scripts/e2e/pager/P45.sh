# pager: P45 | mcp | shared | the status is the API's; the identity names the token
#
# Backlog #157, F1. get_status reads what /api/status reads; get_caller_identity says who the
# token is - a person for a person token, the token itself for a shared one, whose `me` resolves
# to nobody.

scenario() {
    mcp_ready || return
    local s api who

    s=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_status)")
    api=$(_pager_curl "$PAGER_API/api/status")
    want "the mode is the API's" "$(jq -r '.mode' <<<"$s")" = "$(jq -r '.mode' <<<"$api")"
    want "the effective mode is the API's" "$(jq -r '.effectiveMode' <<<"$s")" = "$(jq -r '.effectiveMode' <<<"$api")"
    want "it says the version" "$(jq -r '.version // ""' <<<"$s")" != ""

    who=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" get_caller_identity)")
    want "a person token is its person" "$(jq -r '.name' <<<"$who")" = pager-reader
    want "of kind person" "$(jq -r '.kind' <<<"$who")" = person
    want "with the reader role" "$(jq -r '.role' <<<"$who")" = reader
    want "whose me is itself" "$(jq -r '.me' <<<"$who")" = pager-reader

    who=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_SHARED" get_caller_identity)")
    want "a shared token is itself" "$(jq -r '.name' <<<"$who")" = mcp/pager-shared
    want "of kind shared" "$(jq -r '.kind' <<<"$who")" = shared
    want "whose me is nobody" "$(jq -r '.me' <<<"$who")" = null

    who=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" get_caller_identity)")
    want "an approver token says approver" "$(jq -r '.role' <<<"$who")" = approver
}
