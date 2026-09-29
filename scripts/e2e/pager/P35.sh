# pager: P35 | mcp | shared | my incidents: a person token gets its own open and closed; a shared token is told why not
#
# Backlog #157, F2: "what are my open incidents and my latest closed ones". Two incidents are
# assigned to pager-reader, and one of them is closed. The reader's token finds the open one as
# open and the closed one as closed; another person's token finds neither; a shared token - a
# gateway's, which stands for nobody in particular - is told that `me` means nobody, rather than
# being answered with an empty list it would report as "you have no incidents".

scenario() {
    mcp_ready || return
    local prefix="E2ePagerP35${PAGER_RUN}" a b open closed r

    pager_fire "${prefix}Open" namespace=pager-p35-a deployment=one
    pager_fire "${prefix}Done" namespace=pager-p35-b deployment=two
    pager_wait_count "${prefix}Open" 1 60 && pager_wait_count "${prefix}Done" 1 60 \
        || { fail "both alerts opened an incident" "not within 60s"; return; }
    a=$(pager_first "${prefix}Open"); b=$(pager_first "${prefix}Done")

    for id in "$a" "$b"; do
        _pager_curl -o /dev/null -X POST "$PAGER_API/api/incidents/$id/assign" -H 'Content-Type: application/json' \
            --data '{"assignee":"pager-reader","assignedBy":"pager-suite"}'
    done
    want "the second is closed" "$(pager_close "$b")" -lt 300

    q() { jq -cn --arg p "${prefix}*" --arg s "$1" '{alertName:$p, state:$s, assignedTo:"me"}'; }
    open=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" search_incidents "$(q open)")")
    closed=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" search_incidents "$(q closed)")")
    want "my open incidents are the open one" "$(jq -c '[.incidents[].id]' <<<"$open")" = "[\"$a\"]"
    want "my closed incidents are the closed one" "$(jq -c '[.incidents[].id]' <<<"$closed")" = "[\"$b\"]"

    want "somebody else's are not mine" \
        "$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_ONCALL" search_incidents "$(q any)")" | jq '.incidents | length')" -eq 0

    r=$(mcp_call "$PAGER_MCP_TOKEN_SHARED" search_incidents "$(q open)")
    want "a shared token asking for me is an error" "$(jq -r '.isError' <<<"$r")" = true
    want "and it says why" "$(jq -r ".text" <<<"$r" | grep -ci 'shared')" -ge 1
}
