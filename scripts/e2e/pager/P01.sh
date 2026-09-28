# pager: P01 | - | shared | with a token configured, a request without it is 401 and opens nothing
#
# Backlog #138. The webhook is the one route an Alertmanager - or anything else that can reach
# the port - uses to make the agent page a person. With secrets.webhookToken set, the suite's
# caller exports PAGER_TOKEN; without it this scenario has nothing to test and fails, because an
# install that CAN be configured with a token and is not is the thing F1 exists to end.

scenario() {
    local n
    n=$(pager_name P01)

    if [ -z "$PAGER_TOKEN" ]; then
        fail "a webhook token is configured" "PAGER_TOKEN is empty: the suite's values do not set secrets.webhookToken"
        return
    fi

    pager_post_as "" "$(pager_alert firing "$n" namespace=pager-e2e deployment=token)"
    want "no Authorization header is refused with 401" "$PAGER_CODE" = 401

    pager_post_as "Bearer not-the-token-not-the-token" "$(pager_alert firing "$n" namespace=pager-e2e deployment=token)"
    want "a wrong token is refused with 401" "$PAGER_CODE" = 401

    sleep 5
    want "a refused alert opened nothing" "$(pager_count "$n")" -eq 0

    pager_fire "$n" namespace=pager-e2e deployment=token
    want "the right token is accepted" "$PAGER_CODE" -lt 300
    pager_wait_count "$n" 1 60 && pass "the accepted alert opened an incident" \
        || fail "the accepted alert opened an incident" "none within 60s"
}
