# pager: P50 | mcp | exclusive | a backlog closes in two calls: a dry run that counts, and a close that takes exactly that count
#
# Backlog #161. Clearing production on 2026-09-29 took 326 separate close_incident calls through
# a gateway, in a script. close_incidents takes the filters of search_incidents. Without
# `expect` it closes nothing and says how many would close; with it, it closes them only if
# exactly that many still match - so a filter that is slightly wrong, or a backlog that grew in
# between, is refused rather than closed. Approver only, a reason required, one audit entry each.
#
# Exclusive, though it disturbs nobody: it fires three alerts, and the shared scenarios all fire
# in the same second into an investigation queue of 32. On 2026-10-02 CI's P41 found its
# incident settled four seconds after firing with no investigation, which is what a full queue
# does to the alert that arrives last. Three alerts fewer in that burst is this line.

scenario() {
    mcp_ready || return
    local n other a b c r dry count rows args

    n=$(pager_name P50)
    other=$(pager_name P50x)
    pager_fire "$n" namespace=pager-p50 deployment=ledger
    pager_fire "$n" namespace=pager-p50 deployment=billing
    pager_fire "$other" namespace=pager-p50 deployment=mailer
    pager_wait_count "$n" 2 60 || { fail "two alerts opened two incidents" "$(pager_count "$n") within 60s"; return; }
    pager_wait_count "$other" 1 60 || { fail "the third alert opened its own" "none within 60s"; return; }
    a=$(pager_incidents "$n" | jq -r '.[0].id')
    b=$(pager_incidents "$n" | jq -r '.[1].id')
    c=$(pager_first "$other")
    pager_wait_settled "$a" 120; pager_wait_settled "$b" 120

    args=$(jq -cn --arg n "$n" '{alertName:$n}')

    r=$(mcp_call "$PAGER_MCP_TOKEN_READER" close_incidents "$args")
    want "a reader's bulk close is refused, the dry run too" "$(jq -r '(.error != null) or .isError' <<<"$r")" = true

    dry=$(mcp_json "$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" close_incidents "$args")")
    count=$(jq -r '.matched' <<<"$dry")
    want "the dry run says it is one" "$(jq -r '.dryRun' <<<"$dry")" = true
    want "it counts the two that match" "$count" = 2
    want "it names them" "$(jq -c '[.sample[].id] | sort' <<<"$dry")" = "$(jq -cn --arg a "$a" --arg b "$b" '[$a,$b] | sort')"
    want "and nothing closed" "$(pager_open "$n")" -eq 2

    r=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" close_incidents "$(jq -c '. + {expect: 5, reason: "pager suite"}' <<<"$args")")
    want "a count that does not hold closes nothing" "$(jq -r '.isError' <<<"$r")" = true
    want "still two open" "$(pager_open "$n")" -eq 2

    r=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" close_incidents "$(jq -c --argjson e "$count" '. + {expect: $e}' <<<"$args")")
    want "a close without a reason is refused" "$(jq -r '.isError' <<<"$r")" = true

    r=$(mcp_call "$PAGER_MCP_TOKEN_APPROVER" close_incidents \
        "$(jq -c --argjson e "$count" '. + {expect: $e, reason: "pager suite: the rollout finished"}' <<<"$args")")
    want "the close with the dry run's count is accepted" "$(jq -r '.isError' <<<"$r")" = false
    want "it closed both" "$(mcp_json "$r" | jq -r '.closed')" = 2
    want "the first is closed" "$(pager_state "$a")" = Closed
    want "the second is closed" "$(pager_state "$b")" = Closed
    want "the one the filter did not name is not" "$(pager_state "$c")" != Closed

    rows=$(mcp_audit_rows "$PAGER_MCP_TOKEN_READER" "$a")
    want "each has its own audit entry, saying it went with others" \
        "$(jq '[.[] | select(.action == "incident.closed") | select((.reason // "") | test("1 others"))] | length' <<<"$rows")" -eq 1

    pager_resolve "$other" namespace=pager-p50 deployment=mailer
}
