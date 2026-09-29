# pager: P46 | mcp kubectl spare-agent | exclusive | the agent refuses to start with an MCP token that is too short
#
# Backlog #157, F1. A token is the whole of the endpoint's authentication, and a short one can be
# guessed. A copy of the installed Deployment is started with its first MCP token replaced by a
# short value, under labels no Service selects. It must never become Ready, and its log must say
# why. The copy is deleted whatever happens. Same shape as P02.

scenario() {
    local name=hephaisto-p46-${PAGER_RUN} ready log
    pager_kc -n "$PAGER_NS" get deploy "$PAGER_DEPLOY" -o json \
        | jq --arg n "$name" '
            .metadata = {name: $n, namespace: .metadata.namespace, labels: {"app.kubernetes.io/name": $n}}
            | .spec.replicas = 1
            | .spec.selector = {matchLabels: {"app.kubernetes.io/name": $n}}
            | .spec.template.metadata.labels = {"app.kubernetes.io/name": $n}
            | .spec.template.spec.containers[0].env |=
                (map(select(.name != "Mcp__Tokens__0__Value")) + [{name: "Mcp__Tokens__0__Value", value: "too-short"}])
            | del(.status)' \
        | pager_kc apply -f - >/dev/null

    sleep 60
    ready=$(pager_kc -n "$PAGER_NS" get deploy "$name" -o jsonpath='{.status.readyReplicas}' 2>/dev/null)
    log=$(pager_kc -n "$PAGER_NS" logs "deploy/$name" --tail=200 2>/dev/null; \
          pager_kc -n "$PAGER_NS" logs "deploy/$name" --previous --tail=200 2>/dev/null)
    pager_kc -n "$PAGER_NS" delete deploy "$name" --wait=false >/dev/null 2>&1

    want "the agent with a short token never became ready" "${ready:-0}" -eq 0
    if grep -qi "mcp" <<<"$log" && grep -q "32" <<<"$log"; then
        pass "its log says the token is shorter than 32 characters"
    else
        fail "its log says the token is shorter than 32 characters" "$(tail -3 <<<"$log" | tr '\n' ' ')"
    fi
}
