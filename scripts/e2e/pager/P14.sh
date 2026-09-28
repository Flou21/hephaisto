# pager: P14 | - | shared | one alert that cannot be stored does not stop the ones beside it
#
# An alert carrying a NUL in a label cannot be written to Postgres, and retrying it cannot help.
# It must not take the rest of its group down with it, and it must not turn the group into a
# retry loop: the webhook answers 2xx for what it could not store for good.

scenario() {
    local good bad
    good=$(pager_name P14)
    bad=$(pager_name P14Bad)

    # bash cannot hold a NUL, so the escape is written into the JSON text afterwards.
    pager_post "$(pager_alert firing "$bad" namespace=pager-e2e deployment=nul note=a__NUL__b | sed 's/__NUL__/\\u0000/')" \
               "$(pager_alert firing "$good" namespace=pager-e2e deployment=beside)"
    want "the group is answered 2xx" "$PAGER_CODE" -lt 300

    pager_wait_count "$good" 1 60 && pass "the alert beside it opened its incident" \
        || fail "the alert beside it opened its incident" "none within 60s"

    curl -sS --max-time 10 "$PAGER_API/api/status" >/dev/null && pass "the agent is still answering" \
        || fail "the agent is still answering"
}
