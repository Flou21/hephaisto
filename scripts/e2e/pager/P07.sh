# pager: P07 | - | shared | one pod resolving does not close the incident while its sibling fires
#
# Two pods of one deployment fire the same alert: one incident, because the fingerprint excludes
# the pod on purpose. Alertmanager then sends the group with one alert resolved and one still
# firing. The incident is about the deployment and the deployment is still broken.

scenario() {
    local n id
    n=$(pager_name P07)
    local base="namespace=pager-e2e deployment=web"

    pager_post "$(pager_alert firing "$n" $base pod=web-5d7f-aaaaa)" "$(pager_alert firing "$n" $base pod=web-5d7f-bbbbb)"
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    sleep 5
    want "two pods of one deployment are one incident" "$(pager_count "$n")" -eq 1
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true

    pager_post "$(pager_alert resolved "$n" $base pod=web-5d7f-aaaaa)" "$(pager_alert firing "$n" $base pod=web-5d7f-bbbbb)"
    sleep 10
    local s
    s=$(pager_state "$id")
    [ "$s" != Closed ] && [ "$s" != Resolved ] && pass "still open while the sibling fires" \
        || fail "still open while the sibling fires" "state $s"

    pager_post "$(pager_alert resolved "$n" $base pod=web-5d7f-aaaaa)" "$(pager_alert resolved "$n" $base pod=web-5d7f-bbbbb)"
    pager_wait_state "$id" 60 Closed && pass "closed once both cleared" \
        || fail "closed once both cleared" "state $(pager_state "$id")"
}
