# pager: P12 | - | shared | an incident about another cluster says so, and the model is offered no Kubernetes tool
#
# Backlog #131. The agent's Kubernetes tools read the cluster it runs in. For an incident about
# another cluster they would return a same-named workload's pods, events and rollouts from the
# wrong place, and nothing would mark them as such. Metrics and logs are central and stay.

K8S_TOOLS='["list_pods","get_pod","describe_pod","who_owns","get_events","get_pod_logs","list_deployments","list_statefulsets","list_daemonsets","get_workload","get_rollout_history","list_nodes","get_node","get_resource_usage","list_hpa","list_pvcs","get_service_endpoints"]'

scenario() {
    local n id
    n=$(pager_name P12)

    pager_fire "$n" cluster=pager-elsewhere namespace=pager-e2e deployment=ledger
    pager_wait_count "$n" 1 60 || { fail "an incident opened" "none within 60s"; return; }
    id=$(pager_first "$n")
    pager_wait_settled "$id" 120 || true

    want "the incident names its cluster" "$(pager_incident "$id" | jq -r '.target.cluster // empty')" = pager-elsewhere
    pager_incident "$id" | jq -r '.title' | grep -c pager-elsewhere >/dev/null && pass "so does its title" \
        || fail "so does its title" "$(pager_incident "$id" | jq -r '.title')"

    local asked offered
    asked=$(pager_llm "$n" | jq length)
    want "the model was asked" "$asked" -ge 1
    offered=$(pager_llm "$n" | jq --argjson k "$K8S_TOOLS" '[.[].tools[] | select(. as $t | $k | index($t))] | unique | length')
    want "no Kubernetes tool was offered" "$offered" -eq 0
    pager_llm "$n" | jq -r '.[0].text' | grep -c pager-elsewhere >/dev/null && pass "the prompt names the other cluster" \
        || fail "the prompt names the other cluster"
}
