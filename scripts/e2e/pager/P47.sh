# pager: P47 | mcp | shared | a reader cannot re-investigate; an approver can, and it is queued
#
# Backlog #157, F4. Another investigation spends the model budget, so it needs the approver
# role here even where the API lets any signed-in reader ask. Accepted, the model is asked again.

scenario() {
    mcp_ready || return
    local n id r before i after=0

    n=$(pager_name P47)
    pager_fire "$n" namespace=pager-p47 deployment=profile
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 90 || { fail "the first investigation ended" "state $(pager_state "$id")"; return; }
    before=$(pager_llm "$n" | jq 'length')

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" reinvestigate_incident "$(jq -cn --arg id "$id" '{id:$id}')")
    want "a reader's re-investigation is refused" "$(jq -r '(.error != null) or .isError' <<<"$r")" = true

    r=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" reinvestigate_incident "$(jq -cn --arg id "$id" '{id:$id}')")
    want "an approver's is accepted" "$(jq -r '.isError' <<<"$r")" = false
    for i in $(seq 1 45); do
        after=$(pager_llm "$n" | jq 'length')
        [ "${after:-0}" -gt "${before:-0}" ] && break
        sleep 2
    done
    want "and the model is asked again" "${after:-0}" -gt "${before:-0}"
}
