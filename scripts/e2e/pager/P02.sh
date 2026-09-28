# pager: P02 | kubectl spare-agent | exclusive | the agent refuses to start with a cost cap and an unpriced model
#
# Backlog #140. A model id with no price bills as zero, so every cost cap is decorative - and
# the caps are the only thing between the number of alerts and the bill. The agent must refuse
# to start rather than run uncapped.
#
# A copy of the installed Deployment is started with an unpriced model and the default cost
# caps, under labels no Service selects. It must never become Ready, and its log must say which
# model has no price. The copy is deleted whatever happens. "spare-agent" is only granted where
# the image is the production one: the dev image compiles for minutes before it could refuse.

scenario() {
    local name=hephaisto-p02-${PAGER_RUN} ready log
    pager_kc -n "$PAGER_NS" get deploy "$PAGER_DEPLOY" -o json \
        | jq --arg n "$name" '
            .metadata = {name: $n, namespace: .metadata.namespace, labels: {"app.kubernetes.io/name": $n}}
            | .spec.replicas = 1
            | .spec.selector = {matchLabels: {"app.kubernetes.io/name": $n}}
            | .spec.template.metadata.labels = {"app.kubernetes.io/name": $n}
            | .spec.template.spec.containers[0].env |=
                (map(select(.name != "Llm__Model")) + [{name: "Llm__Model", value: "pager-suite-unpriced-model"}])
            | del(.status)' \
        | pager_kc apply -f - >/dev/null

    sleep 60
    ready=$(pager_kc -n "$PAGER_NS" get deploy "$name" -o jsonpath='{.status.readyReplicas}' 2>/dev/null)
    log=$(pager_kc -n "$PAGER_NS" logs "deploy/$name" --tail=200 2>/dev/null; \
          pager_kc -n "$PAGER_NS" logs "deploy/$name" --previous --tail=200 2>/dev/null)
    pager_kc -n "$PAGER_NS" delete deploy "$name" --wait=false >/dev/null 2>&1

    want "the unpriced agent never became ready" "${ready:-0}" -eq 0
    if grep -q "pager-suite-unpriced-model" <<<"$log" && grep -qi "price" <<<"$log"; then
        pass "its log names the model that has no price"
    else
        fail "its log names the model that has no price" "$(tail -3 <<<"$log" | tr '\n' ' ')"
    fi
}
