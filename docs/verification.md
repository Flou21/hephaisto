# Verifying Watchtower end to end

Use the Tailscale hostname throughout. Tilt binds every port-forward to that interface, so
`localhost` does not work — not even from a shell on this machine. The upside is that one
address works identically from the Mac Studio and from the laptop.

```fish
set -x H macstudio-von-florian.tail3043f4.ts.net
set -x GPW (kubectl -n watchtower-obs get secret watchtower-grafana -o jsonpath='{.data.admin-password}' | base64 -d)
set -x MCP (kubectl -n watchtower-obs get secret grafana-mcp-caller-token -o jsonpath='{.data.token}' | base64 -d)
```

## 1. Prometheus is up with the receivers enabled

```sh
curl -s "http://$H:9090/api/v1/query?query=up" | jq '.data.result | length'
curl -s "http://$H:9090/api/v1/status/flags" | jq '.data["web.enable-remote-write-receiver"]'   # "true"
```

If that flag is `false`, the values file used `enableFeatures: [remote-write-receiver]` —
the pre-operator-0.60 spelling, which does nothing. The correct key is the first-class CRD
field `enableRemoteWriteReceiver`.

## 2. Grafana has all four datasources

```sh
curl -s -u "admin:$GPW" "http://$H:3030/api/datasources" | jq -r '.[] | "\(.uid)\t\(.type)"'
# expect prometheus, loki, tempo, alertmanager
```

## 3. A span survives the round trip

```sh
curl -s -o /dev/null -w '%{http_code}\n' -X POST "http://$H:4318/v1/traces" \
  -H 'Content-Type: application/json' -d @testdata/probe-span.json      # 200
sleep 15
curl -s -G "http://$H:3200/api/search" \
  --data-urlencode 'q={resource.service.name="verify-probe"}' | jq '.traces | length'
```

## 4. Span metrics reached Prometheus

This proves three things at once: Tempo's metrics-generator is running, remote-write works,
and `outOfOrderTimeWindow` is set.

```sh
curl -s "http://$H:9090/api/v1/query?query=traces_spanmetrics_calls_total" | jq '.data.result | length'   # > 0
```

If it returns 0, look for rejected samples — this is the failure mode that is silent by
design and costs an afternoon:

```sh
kubectl -n watchtower-obs logs sts/tempo | grep -i "out of order\|429"
```

The generator writes samples seconds to minutes late. Without
`tsdb.outOfOrderTimeWindow: 30m` Prometheus rejects them as out-of-order and span metrics
simply never appear, with no error anywhere obvious.

## 5. Logs carry `trace_id`, and Kubernetes Events landed

```sh
curl -s -G "http://$H:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name="watchtower"}' | jq '.data.result | length'
curl -s -G "http://$H:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name="k8s-events"}' | jq '.data.result | length'
```

The second one matters more than it looks. Kubernetes Events are the *narrative* layer:
`FailedScheduling: insufficient memory`, `Failed to pull image`, `BackOff restarting failed
container`. Without them the agent sees a metric go to 1 and has no reason.

## 6. The alert path works — the Watchdog is the proof

```sh
curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[] | "\(.labels.alertname)\t\(.status.state)"'
kubectl -n watchtower logs deploy/watchtower --tail=50 | grep -i watchdog
```

`AgentWatchdog` fires permanently by design (`expr: vector(1)`). If the agent stops seeing
it, the whole alert path is broken and the agent can say so itself.

## 7. grafana-mcp answers, with auth actually enforced

```sh
curl -s -X POST "http://$H:8200/mcp" -H "Authorization: Bearer $MCP" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq -r '.result.tools[].name'
kubectl -n watchtower-obs logs -l app.kubernetes.io/name=grafana-mcp | grep -i "caller authentication"
```

## 8. Chaos produces the signals it claims to

Repeat per scenario against the table in `infra/chaos/README.md`. C1 shown:

```sh
tilt trigger chaos-oomkill
kubectl -n watchtower-chaos get events --sort-by=.lastTimestamp | tail
curl -s "http://$H:9090/api/v1/query?query=kube_pod_container_status_last_terminated_reason%7Breason%3D%22OOMKilled%22%7D" | jq '.data.result|length'
sleep 90 && curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[].labels.alertname'
```

## 9. RBAC is genuinely bounded

The first three must answer `no`, the last `yes`. This is also asserted at startup by a
`SelfSubjectAccessReview`, which refuses to boot if any forbidden verb is allowed — a
fat-fingered RoleBinding is caught in seconds rather than during an incident.

```sh
kubectl auth can-i delete secrets             --as=system:serviceaccount:watchtower:watchtower -A
kubectl auth can-i delete pods -n kube-system --as=system:serviceaccount:watchtower:watchtower
kubectl auth can-i create clusterrolebindings --as=system:serviceaccount:watchtower:watchtower
kubectl auth can-i delete pods -n watchtower-chaos --as=system:serviceaccount:watchtower:watchtower
```

## 10. Observe mode: a grounded diagnosis and zero mutations

```sh
open http://$H:8100
kubectl -n watchtower logs deploy/watchtower | grep -i "would have"
```

## 11. pgvector is live and incidents are indexed

```sh
kubectl -n watchtower exec deploy/postgres -- psql -U watchtower -c \
  "select extname from pg_extension where extname='vector';"
kubectl -n watchtower exec deploy/postgres -- psql -U watchtower -c \
  "select count(*) from incident_embeddings where embedding is not null;"

# the real test: a semantic query sharing no keywords with the incident's title
curl -s "http://$H:8100/api/incidents/search?q=database+connection+problem" | jq '.[].title'
```

## 12. Budget accounting is real, and exhaustion degrades rather than dies

```sh
curl -s "http://$H:9090/api/v1/query?query=watchtower_llm_budget_utilization" | jq '.data.result'

# force it: set MaxCostUsdPerHour to 0.01 in the config ConfigMap, then trigger a fixture
kubectl -n watchtower logs deploy/watchtower | grep -i "BudgetExhausted"
curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[] | select(.labels.alertname|startswith("WatchtowerLlmBudget")) | .labels.alertname'

# detection must keep running - the incident escalates, the agent does not stop watching
curl -s "http://$H:8100/api/incidents?state=Escalated" | jq '.[].escalationReason'
```

## 13. The Aspire dashboard renders `gen_ai` spans

```sh
open http://$H:18888   # Traces -> watchtower.investigation -> child chat span
```

The child span must show `gen_ai.request.model` and token counts with no extra
configuration. That is the whole reason it is deployed next to Tempo: Tempo is the durable
system of record, this is the live-tail view that understands the semantic conventions
natively.

## 14. Every action has an actor, including automatic ones

```sh
kubectl -n watchtower exec deploy/postgres -- psql -U watchtower -c \
  "select approval_source, approved_by, count(*) from actions group by 1,2;"
```

No row may have a null or empty `approved_by`. Automatic actions record
`watchtower/auto` with `approval_source = Auto`.

---

## The five-hop correlation test

**This is the acceptance test for the whole observability stack.** With chaos running, in
Grafana at `http://$H:3030`:

1. Explore → Prometheus →
   ```promql
   histogram_quantile(0.95, sum by (le) (rate(traces_spanmetrics_latency_bucket{service="chaos-faulty-service"}[5m])))
   ```
   An **exemplar dot** appears on the graph.
2. Click it → lands in Tempo on that exact slow trace.
3. On a span → **Logs for this span** → Loki returns the matching `trace_id` line.
4. On a span → **Related metrics** → back to the span-metrics query.
5. The service-graph panel shows `chaos-faulty-service` with a red error edge.

If all five hops work, the traces path, the exemplar path, the OTLP metrics path, the OTLP
logs path and both Grafana correlation configs are proven simultaneously. If hop 3 fails,
check that the logs were shipped **via OTLP** — scraped stdout has no `trace_id`, which is a
real limitation and not a misconfiguration.

## The MVP acceptance test

Apply the chaos fixtures. For each one Watchtower must open exactly one incident, write a
diagnosis citing a real PromQL or LogQL query whose result is stored as evidence, annotate
Grafana, emit its own investigation trace to Tempo — and **change nothing in the cluster.**

Measured over at least 10 seeded scenarios, the target is **≥ 7/10 correct root cause.**
