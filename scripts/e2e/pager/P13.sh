# pager: P13 | am kubectl | exclusive | with the database stopped the webhook is not answered 2xx, and the alert arrives after
#
# Backlog #136. Until v0.10.0 the webhook put the alert on an in-memory queue, answered 200, and
# dropped it when the write failed. Alertmanager was told it arrived and never retried. With the
# database stopped the agent must refuse, so that Alertmanager's own retry is the queue - and
# the alert must then arrive exactly once.

scenario() {
    local n pg
    n=$(pager_name P13)
    pg="${PAGER_POSTGRES:-statefulset/hephaisto-postgres}"

    pager_kc -n "$PAGER_NS" scale "$pg" --replicas=0 >/dev/null
    pager_kc -n "$PAGER_NS" wait --for=delete "pod/${PAGER_POSTGRES_POD:-hephaisto-postgres-0}" --timeout=120s >/dev/null 2>&1 || sleep 20

    pager_fire "${n}Direct" namespace=pager-e2e deployment=vault
    { [ "${PAGER_CODE:-000}" -lt 200 ] || [ "${PAGER_CODE:-000}" -ge 300 ]; } \
        && pass "without a database the webhook is not answered 2xx" \
        || fail "without a database the webhook is not answered 2xx" "it answered $PAGER_CODE"

    pager_am_fire "$n" namespace=pager-e2e deployment=vault
    want "Alertmanager took the alert" "$PAGER_CODE" -lt 300
    sleep "${PAGER_OUTAGE:-40}"

    pager_kc -n "$PAGER_NS" scale "$pg" --replicas=1 >/dev/null
    pager_kc -n "$PAGER_NS" rollout status "$pg" --timeout=180s >/dev/null 2>&1 || true

    pager_wait_count "$n" 1 "${PAGER_RETRY_WAIT:-240}" && pass "the alert arrived after the database was back" \
        || fail "the alert arrived after the database was back" "no incident within ${PAGER_RETRY_WAIT:-240}s"
    sleep 20
    want "exactly once" "$(pager_count "$n")" -eq 1
}
