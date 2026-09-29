# pager: P43 | mcp | shared | a 60,000-character annotation: the answer stays under the budget, parses, and says what it cut
#
# Backlog #157, F3. A gateway cuts a tool result at a fixed length, and a result cut mid-JSON is
# worse than none. The agent keeps every answer under its own budget (32,000 characters) by
# shortening the longest untrusted fields, and says so in the text.

scenario() {
    mcp_ready || return
    local n id big r

    n=$(pager_name P43)
    big=$(head -c 60000 /dev/zero | tr '\0' 'x')
    pager_fire "$n" namespace=pager-p43 deployment=huge "@description=$big"
    pager_wait_count "$n" 1 60 || { fail "the alert opened an incident" "none within 60s"; return; }
    id=$(pager_first "$n")

    for tool in get_incident get_incident_signals; do
        case "$tool" in
            get_incident) r=$(mcp_call "$PAGER_MCP_TOKEN_READER" "$tool" "$(jq -cn --arg id "$id" '{id:$id}')") ;;
            *)            r=$(mcp_call "$PAGER_MCP_TOKEN_READER" "$tool" "$(jq -cn --arg id "$id" '{incidentId:$id}')") ;;
        esac
        want "$tool stays under 32,000 characters" "$(jq '.chars' <<<"$r")" -le 32000
        want "$tool still parses" "$(mcp_json "$r" | jq -r 'type')" = object
        want "$tool says what it cut" "$(jq -r '.text' <<<"$r" | grep -c 'characters cut')" -ge 1
    done
}
