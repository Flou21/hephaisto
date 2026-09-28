# pager: P15 | kubectl | exclusive | the agent restarted mid-flight: no alert lost, none duplicated
#
# Backlog #136 and #144. Alerts arrive while the agent's pod is replaced. What a sender was
# told arrived must exist afterwards, exactly once; what it was refused, it re-sends - as
# Alertmanager does - and that must not become a second incident either.

scenario() {
    local i n code deadline missing=0 dup=0

    (
        sleep 2
        pager_kc -n "$PAGER_NS" delete pod -l "app.kubernetes.io/name=${PAGER_DEPLOY}" --wait=false >/dev/null 2>&1
    ) &

    for i in 1 2 3 4 5 6 7 8; do
        n="$(pager_name P15)n$i"
        deadline=$(( SECONDS + ${PAGER_RESTART_WAIT:-300} ))
        while :; do
            pager_fire "$n" namespace=pager-e2e deployment="restart$i"
            [ "${PAGER_CODE:-000}" -ge 200 ] && [ "${PAGER_CODE:-000}" -lt 300 ] && break
            [ "$SECONDS" -ge "$deadline" ] && break
            sleep 3
        done
        sleep 1
    done
    wait

    pager_kc -n "$PAGER_NS" rollout status "deploy/$PAGER_DEPLOY" --timeout=300s >/dev/null 2>&1 || true
    sleep 30

    for i in 1 2 3 4 5 6 7 8; do
        n="$(pager_name P15)n$i"
        case "$(pager_count "$n")" in
            0) missing=$((missing + 1)) ;;
            1) ;;
            *) dup=$((dup + 1)) ;;
        esac
    done
    want "no accepted alert was lost" "$missing" -eq 0
    want "no alert became two incidents" "$dup" -eq 0
}
