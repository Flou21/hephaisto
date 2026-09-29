# pager: P31 | mcp | shared | incidents a scenario opened are found by search and counted by severity
#
# Backlog #157, F2: "what are the latest open incidents grouped by severity". Three alerts of
# this run - one critical, two warnings - each in its own namespace so none correlates with
# another. search_incidents finds all three by the run's alert-name prefix, count_incidents
# groups them 1 critical, 2 warning.

scenario() {
    mcp_ready || return
    local prefix="E2ePagerP31${PAGER_RUN}" rows groups

    pager_fire "${prefix}Q1" severity=critical namespace=pager-p31-a deployment=one
    pager_fire "${prefix}Q2" severity=warning namespace=pager-p31-b deployment=two
    pager_fire "${prefix}Q3" severity=warning namespace=pager-p31-c deployment=three
    for q in Q1 Q2 Q3; do
        pager_wait_count "${prefix}$q" 1 60 || fail "the alert $q opened an incident" "none within 60s"
    done

    rows=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" search_incidents \
        "$(jq -cn --arg p "${prefix}*" '{alertName:$p, state:"open", limit:50}')")")
    want "search finds the three open incidents" "$(jq '.incidents | length' <<<"$rows")" -eq 3
    want "newest first" "$(jq -r '[.incidents[].openedAt] == ([.incidents[].openedAt] | sort | reverse)' <<<"$rows")" = true
    want "a row names its alert" "$(jq -r '[.incidents[].alertName] | sort | .[0]' <<<"$rows")" = "${prefix}Q1"

    groups=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_READER" count_incidents \
        "$(jq -cn --arg p "${prefix}*" '{alertName:$p, state:"open", groupBy:"severity"}')")")
    want "the count's total is three" "$(jq '.total' <<<"$groups")" -eq 3
    want "one critical" "$(jq '[.groups[] | select(.key == "Critical") | .count] | add // 0' <<<"$groups")" -eq 1
    want "two warnings" "$(jq '[.groups[] | select(.key == "Warning") | .count] | add // 0' <<<"$groups")" -eq 2
    want "each group carries its newest incidents as examples" \
        "$(jq '[.groups[] | .examples | length] | min' <<<"$groups")" -ge 1
}
