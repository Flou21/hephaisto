# pager: P21 | - | shared | a cleared alert edits the card to say so, rings nobody, and never claims a fix
#
# An alert that clears on its own is good news nobody needs to be woken for, and it is not the
# agent's doing. The message that told a person must now say the alert cleared - an edit, which
# notifies nobody - and must not say the agent fixed anything.

scenario() {
    local n id told t text
    n=$(pager_name P21)
    local labels="namespace=pager-e2e deployment=mailer"

    pager_fire "$n" $labels
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true
    sleep 15
    told=$(pager_alerts_sent "$id")
    want "a person was told" "$told" -ge 1

    t=$(date -u +%Y-%m-%dT%H:%M:%S)
    pager_resolve "$n" $labels
    pager_wait_state "$id" 60 Closed || { fail "the resolve closed it" "state $(pager_state "$id")"; return; }
    sleep 25

    want "the clearing rang nobody" "$(pager_alerts_sent "$id")" -eq "$told"
    want "a message about it was edited after the clear" \
        "$(pager_teams "$id" | jq --arg t "$t" '[.[] | select(.kind == "chat") | select((.editedAt // "") > $t)] | length')" -ge 1
    text=$(pager_teams "$id" | jq -r '[.[] | select(.kind == "chat") | .text] | join(" ")')
    grep -qi "clear" <<<"$text" && pass "it says the alert cleared" || fail "it says the alert cleared" "${text:0:200}"
    grep -qiE "fixed|remediated|resolved by hephaisto" <<<"$text" && fail "it claims no fix" "${text:0:200}" \
        || pass "it claims no fix"
}
