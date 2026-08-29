# Verifying Hephaisto end to end

`$H` below is whatever `host` is set to in `tilt_config.json` — Tilt binds every
port-forward to that interface. If you have set it to something other than `localhost`, then
`localhost` does **not** work, not even from a shell on the machine running Tilt; the upside
is that the one address then works identically from every machine on your network.

```fish
set -x H (jq -r '.host // "localhost"' ~/hephaisto/tilt_config.json)
set -x GPW (kubectl -n hephaisto-obs get secret hephaisto-grafana -o jsonpath='{.data.admin-password}' | base64 -d)
set -x MCP (kubectl -n hephaisto-obs get secret grafana-mcp-caller-token -o jsonpath='{.data.token}' | base64 -d)
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
kubectl -n hephaisto-obs logs sts/tempo | grep -i "out of order\|429"
```

The generator writes samples seconds to minutes late. Without
`tsdb.outOfOrderTimeWindow: 30m` Prometheus rejects them as out-of-order and span metrics
simply never appear, with no error anywhere obvious.

## 5. Logs carry `trace_id`, and Kubernetes Events landed

```sh
curl -s -G "http://$H:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name="hephaisto"}' | jq '.data.result | length'
curl -s -G "http://$H:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={service_name="k8s-events"}' | jq '.data.result | length'
```

The second one matters more than it looks. Kubernetes Events are the *narrative* layer:
`FailedScheduling: insufficient memory`, `Failed to pull image`, `BackOff restarting failed
container`. Without them the agent sees a metric go to 1 and has no reason.

## 6. The alert path works — the Watchdog is the proof

```sh
curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[] | "\(.labels.alertname)\t\(.status.state)"'
kubectl -n hephaisto logs deploy/hephaisto --tail=50 | grep -i watchdog
```

`AgentWatchdog` fires permanently by design (`expr: vector(1)`). If the agent stops seeing
it, the whole alert path is broken and the agent can say so itself.

## 7. grafana-mcp answers, with auth actually enforced

```sh
curl -s -X POST "http://$H:8200/mcp" -H "Authorization: Bearer $MCP" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq -r '.result.tools[].name'
kubectl -n hephaisto-obs logs -l app.kubernetes.io/name=grafana-mcp | grep -i "caller authentication"
```

## 8. Chaos produces the signals it claims to

Repeat per scenario against the table in `infra/chaos/README.md`. C1 shown:

```sh
tilt trigger c1-oomkill
kubectl -n hephaisto-chaos get events --sort-by=.lastTimestamp | tail
curl -s "http://$H:9090/api/v1/query?query=kube_pod_container_status_last_terminated_reason%7Breason%3D%22OOMKilled%22%7D" | jq '.data.result|length'
sleep 90 && curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[].labels.alertname'
```

## 9. RBAC is genuinely bounded

The first three must answer `no`, the last `yes`. This is also asserted at startup by a
`SelfSubjectAccessReview`, which refuses to boot if any forbidden verb is allowed — a
fat-fingered RoleBinding is caught in seconds rather than during an incident.

```sh
kubectl auth can-i delete secrets             --as=system:serviceaccount:hephaisto:hephaisto -A
kubectl auth can-i delete pods -n kube-system --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i create clusterrolebindings --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i delete pods -n hephaisto-chaos --as=system:serviceaccount:hephaisto:hephaisto
```

## 10. Observe mode: a grounded diagnosis and zero mutations

```sh
open http://$H:8100
kubectl -n hephaisto logs deploy/hephaisto | grep -i "would have"
```

## 11. pgvector is live and incidents are indexed

```sh
kubectl -n hephaisto exec deploy/postgres -- psql -U hephaisto -c \
  "select extname from pg_extension where extname='vector';"
kubectl -n hephaisto exec deploy/postgres -- psql -U hephaisto -c \
  "select count(*) from incident_embeddings where embedding is not null;"

# the real test: a semantic query sharing no keywords with the incident's title
curl -s "http://$H:8100/api/incidents/search?q=database+connection+problem" | jq '.[].title'
```

## 12. Budget accounting is real, and exhaustion degrades rather than dies

```sh
curl -s "http://$H:9090/api/v1/query?query=hephaisto_llm_budget_utilization" | jq '.data.result'

# force it: set MaxCostUsdPerHour to 0.01 in the config ConfigMap, then trigger a fixture
kubectl -n hephaisto logs deploy/hephaisto | grep -i "BudgetExhausted"
curl -s "http://$H:9093/api/v2/alerts" | jq -r '.[] | select(.labels.alertname|startswith("HephaistoLlmBudget")) | .labels.alertname'

# detection must keep running - the incident escalates, the agent does not stop watching
curl -s "http://$H:8100/api/incidents?state=Escalated" | jq '.[].escalationReason'
```

## 13. The Aspire dashboard renders `gen_ai` spans

```sh
open http://$H:18888   # Traces -> hephaisto.investigation -> child chat span
```

The child span must show `gen_ai.request.model` and token counts with no extra
configuration. That is the whole reason it is deployed next to Tempo: Tempo is the durable
system of record, this is the live-tail view that understands the semantic conventions
natively.

## 14. Every action has an actor, including automatic ones

```sh
kubectl -n hephaisto exec deploy/postgres -- psql -U hephaisto -c \
  "select approval_source, approved_by, count(*) from actions group by 1,2;"
```

No row may have a null or empty `approved_by`. Automatic actions record
`hephaisto/auto` with `approval_source = Auto`.

## 15. `Off` actually stops the agent, and lets go again

The enum says `Off` means "ingest nothing, investigate nothing. Full stop." This step exists
because for a while it did neither: on a clean cluster reporting `effectiveMode: Off`, an
injected fault was still ingested, opened as an incident and escalated.

Note the deliberate contrast with step 12. Budget exhaustion must **degrade** - detection keeps
running, because a cluster you cannot afford to investigate is still a cluster you have to
watch. `Off` must **stop**. Those are opposite behaviours and neither should drift into the
other.

```sh
kubectl -n hephaisto patch cm hephaisto-switches --type merge -p '{"data":{"mode":"Off"}}'
# The kubelet takes up to ~60s to project a changed ConfigMap into the pod.
curl -s http://$H:8100/api/status | jq '{effectiveMode, modeDecidedBy, openIncidents}'
#   -> "Off", "configmap:mode".  Note openIncidents as N.

kubectl -n hephaisto-chaos create deployment offtest --image=ghcr.io/flou21/nope:v9
sleep 120
curl -s http://$H:8100/api/status    | jq .openIncidents   # MUST still be N
curl -s http://$H:8100/api/incidents | grep -c offtest     # MUST be 0
```

**`watchdogStale` must stay `false` throughout.** The heartbeat is deliberately not gated: it
arrives at `/webhooks/watchdog`, which never touches the signal sink. An `Off` that silenced it
would make the agent believe it had gone blind the moment it was switched back on.

Then prove the gate lifts - a switch that stops things and cannot be released is a different
bug:

```sh
kubectl -n hephaisto patch cm hephaisto-switches --type merge -p '{"data":{"mode":"Observe"}}'
sleep 120
curl -s http://$H:8100/api/incidents | grep -c offtest     # MUST now be 1
kubectl -n hephaisto-chaos delete deployment offtest
```

`killSwitch: "true"` is a *different* control and does not do this: it clamps to `Observe`, not
`Off`. It stops the agent acting, not the agent watching.

---

## Running all of this automatically

Everything above is the manual form, and it is still the right thing when you are chasing one
specific behaviour. For a release, `scripts/e2e/run.sh` does the equivalent against a throwaway
kind cluster and prints a verdict:

```sh
scripts/e2e/run.sh                    # dispatch a nightly build and test it
scripts/e2e/run.sh --rc               # cut a real release candidate and test it
scripts/e2e/run.sh --tag 0.0.1-rc2    # test something already published
```

It covers steps 1, 2, 5, 6, 9, 11, 12 and 14 above, plus the parts CI cannot reach: that the
`release:` selector actually selects (CI installs no Prometheus), and that a real investigation
runs end to end (CI has no key). See `scripts/e2e/README.md`.

Two things it deliberately does **not** cover, and neither does anything else:

- **NetworkPolicy enforcement (step 9's sibling).** kind's default CNI accepts the objects and
  ignores them, and that policy is the webhook's entire authentication. Verify it by hand, on a
  cluster whose CNI enforces.
- **Root cause quality.** The harness grades each diagnosis against the answer key in
  `infra/chaos/README.md` and reports a score, but never fails on it. The MVP bar — ≥ 7/10 over
  ≥ 10 scenarios — is still a judgement someone makes by reading.

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

Apply the chaos fixtures. For each one Hephaisto must open exactly one incident, write a
diagnosis citing a real PromQL or LogQL query whose result is stored as evidence, annotate
Grafana, emit its own investigation trace to Tempo — and **change nothing in the cluster.**

Measured over at least 10 seeded scenarios, the target is **≥ 7/10 correct root cause.**
