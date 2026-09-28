# pager: P26 | - | shared | a signed click acknowledges; one signed for another app, tenant or outsider changes nothing
#
# Backlog #124. The one inbound route from Microsoft. The Teams stand-in signs an invoke the way
# the Bot Framework does, with its own published key, and posts it to the agent - once as it
# should be, and once each as a forgery that must be refused.

click() {
    _pager_curl -X POST "$PAGER_STANDIN/teams/click" -H 'Content-Type: application/json' --data "$1" \
        | jq -r '.status // 0'
}

scenario() {
    local n id forged
    n=$(pager_name P26)

    pager_fire "$n" namespace=pager-e2e deployment=clicks
    pager_wait_count "$n" 1 60 || { fail "an incident opened"; return; }
    id=$(pager_first "$n")

    for forged in '{"tenant":"00000000-0000-0000-0000-00000000bad1"}' \
                  '{"appId":"00000000-0000-0000-0000-00000000bad2"}' \
                  '{"user":"outsider@elsewhere.example"}' \
                  '{"endorse":false}'; do
        click "$(jq -cn --arg id "$id" --argjson f "$forged" '{incidentId:$id, verb:"acknowledge", user:"oncall@example.com"} + $f')" >/dev/null
    done
    sleep 3
    want "no forged click acknowledged it" "$(pager_incident "$id" | jq -r '.acknowledgedBy // "none"')" = none

    want "a signed click is answered 200" \
        "$(click "$(jq -cn --arg id "$id" '{incidentId:$id, verb:"acknowledge", user:"oncall@example.com"}')")" = 200
    sleep 3
    pager_incident "$id" | jq -r '.acknowledgedBy // ""' | grep -c oncall >/dev/null && pass "the signed click acknowledged it, as the person who clicked" \
        || fail "the signed click acknowledged it, as the person who clicked" "$(pager_incident "$id" | jq -r '.acknowledgedBy')"
}
