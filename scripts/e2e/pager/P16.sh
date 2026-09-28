# pager: P16 | am kubectl prometheus | exclusive | with the agent scaled to zero, a person is told by a path the agent is not on
#
# Backlog #137. Once Hephaisto is the only receiver, every alert about Hephaisto goes to
# Hephaisto. The chart's HephaistoAbsent rule is labelled hephaisto_route=external, and the
# install routes that label to a receiver that is not the agent - here, the stand-in's
# /hooks/external.

scenario() {
    local found=0 deadline

    _pager_curl -X DELETE "$PAGER_STANDIN/received" >/dev/null 2>&1 || true
    pager_kc -n "$PAGER_NS" scale "deploy/$PAGER_DEPLOY" --replicas=0 >/dev/null

    # Fifteen minutes. The series lingers for Prometheus's five-minute staleness when the agent's
    # metrics also travel through an OTel collector, as they do on the dev stack, and the rule then
    # waits its own two minutes: measured at a little over seven. Seven minutes was the old bound,
    # and the first run failed on it with the route demonstrably working.
    deadline=$(( SECONDS + ${PAGER_ABSENT_WAIT:-900} ))
    while [ "$SECONDS" -lt "$deadline" ]; do
        found=$(_pager_curl "$PAGER_STANDIN/received" \
            | jq '[.[] | select(.hook == "external") | select((.body | tostring) | contains("HephaistoAbsent"))] | length' 2>/dev/null || echo 0)
        [ "${found:-0}" -ge 1 ] && break
        sleep 15
    done

    pager_kc -n "$PAGER_NS" scale "deploy/$PAGER_DEPLOY" --replicas=1 >/dev/null
    pager_kc -n "$PAGER_NS" rollout status "deploy/$PAGER_DEPLOY" --timeout=300s >/dev/null 2>&1 || true

    want "HephaistoAbsent reached the external receiver" "${found:-0}" -ge 1
}
