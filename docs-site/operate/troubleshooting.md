# Troubleshooting

Organised by what you observe, not by which component is at fault.

The recurring theme: **most failures here are silent.** The agent is designed to fail closed, which
means a broken install usually looks like a healthy one doing nothing. Nearly every entry below is
a case of that.

## Nothing happens at all — no incidents are ever created

The single most common cause, and it produces no error anywhere.

**`prometheusOperator.selectorLabels` does not match your stack.** Every object is created,
`kubectl get prometheusrule` shows them all present, Prometheus selects none, and the agent
reports itself perfectly healthy.

```sh
kubectl exec -n <observability-ns> sts/prometheus-<release> -c prometheus -- \
  wget -qO- localhost:9090/api/v1/rules | grep -c hephaisto
```

Zero means the labels do not match. See [Verify your install](/operate/verify#_1-prometheus-actually-selects-the-rules).

**Or: the alert path is broken.** Check `watchdogStale`:

```sh
curl -s localhost:8080/api/status | jq '.watchdogStale'
```

`true` means Prometheus, Alertmanager, the NetworkPolicy or the receiver is broken. The
`AgentWatchdog` rule fires constantly by design, so its absence is the signal.

**Or: `mode` is `Off`.** `Off` ingests nothing and investigates nothing. Remember the most
restrictive of the three arms wins, so a values file saying `Observe` loses to an env var saying
`Off`.

## Incidents are created but always get the default runbook

The alert is not declaring a valid `hephaisto_kind`. See
[Alerting and `hephaisto_kind`](/operate/alerting) — `Enum.TryParse` fails **silently** and the
classifier falls back to guessing from the alert name.

## The pod restarts once or twice on a cold start, then settles

**Expected.** The agent applies its EF migrations before serving and exits if the database is not
reachable. With an external database it may restart while that database is still accepting its
first connections. Kubernetes retries and it settles.

Only a pod **still** restarting after a few minutes means the connection details are actually
wrong.

## The pod will not start at all

| Symptom | Cause |
|---|---|
| `CreateContainerConfigError` | A referenced Secret or key does not exist. The pod never starts, so **there are no logs** — read the events. |
| Exits immediately, logs mention the database | Postgres unreachable, or missing `pgvector`. The agent fails fast on purpose: no audit, no action. |
| Refuses to start, mentions `baseUrl` | `notifications.routes` is set but `notifications.baseUrl` is empty. Every message exists to make someone open a link, and the pod cannot work out the address a person reaches it on — it only knows the one it binds. |

## It starts, passes probes, and does nothing — with egress on

Two distinct cases, both silent:

**`networkPolicy.egress.apiServerCIDRs` is empty.** The agent cannot reach the Kubernetes API
server, so it cannot watch pods, read events or act.

```sh
kubectl get endpoints kubernetes -o jsonpath='{.subsets[*].addresses[*].ip}'
```

**`networkPolicy.egress.extraEgressCIDRs` is empty.** Nothing outside the cluster is reachable: no
model calls, and no notification is ever delivered. Investigations fail and escalations reach
nobody.

## Investigations run but always end without a finding

Check the termination reason on the investigation.

| `terminationReason` | What it means |
|---|---|
| `Concluded` | Normal. If there is still no finding, the model genuinely declined. |
| `StepBudgetExhausted` | Raise `Llm:Investigation:MaxSteps`. A reserved concluding step usually rescues these. |
| `TokenBudgetExhausted` | Raise `MaxInputTokens`. **The concluding-step rescue cannot land here**, because the concluding call resends the conversation — so these produce no finding and are indistinguishable from a decline in a summary line. |
| `Faulted` | Read the error on the step. |

## It diagnoses correctly but never proposes an action

Two very different causes, and they are easy to confuse.

**Your provider cannot constrain output to a JSON schema.** DeepSeek answers
`400 "This response_format type is unavailable now"`. Phase 1 is unaffected, so the agent
diagnoses correctly and proposes nothing. Set `Llm:PlanningStructuredOutput=JsonObject`.

**Or the model simply does not propose actions.** This is a real, measured property that varies
enormously between models — `gpt-oss:120b` proposed an action in 0 of 18 runs on a fixture where
`deepseek-v4-flash` proposed one in 4 of 8. See
[the table](/guide/what-it-is#the-row-to-read-carefully). If you need remediation proposals, this
is a model-selection decision, not a configuration one.

**Or the policy engine is refusing.** In `Observe` the plan is still generated; check the
incident's escalation reason. `PolicyDenied` means the plan existed and was rejected.

## Cost is always reported as zero

The model has **no `Llm:Pricing` entry**. An unpriced model is charged at zero, which switches the
cost budget off rather than approximating it. Add a price.

## Search returns lexical matches only

No embedding endpoint is configured, or the key is missing. The generator **degrades rather than
throwing**: a null vector is written and lexical plus trigram search still works. The UI says so.

Configure `Llm:EmbeddingProvider` / `Llm:EmbeddingEndpoint` — any endpoint serving
`/v1/embeddings` works, including Ollama and vLLM.

## Notifications are never delivered

In order of likelihood:

1. **`notifications.routes` is empty.** A stock install delivers nowhere, deliberately. An
   escalation is a database row and a nudge to any open browser tab.
2. The route names a channel that is not configured — refused at startup rather than silently
   delivering nowhere.
3. Egress is blocked (see above).
4. The per-channel hourly cap or the per-workload cooldown suppressed it. The **first** message for
   a workload always goes out; repeats are suppressed and counted.

## An action was executed but the incident sits in `Verifying`

Verification runs at T+60s / T+5m / T+15m, and only the **last** may conclude a failure — a pod
still pulling its image at T+60s is not a fault, and reverting on it would make the agent the cause
of the next incident. So a wait of up to fifteen minutes is normal.

Beyond that, check that the action's target carries an owner. A `RestartPod` whose target had no
owner reference could not be verified at all in versions before v0.6.0.

## The agent acted on something it should not have

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Off
```

Then read the audit log — every action has one, and the audit row and the state transition that
caused it are written in a single transaction, so the record cannot be missing.

Check which of the four gates was open:
[the promotion path](/guide/promotion-path#the-four-gates).

## Getting help

Open an issue with the output of `GET /api/status`, the investigation's step trace, and the chart
values you installed with — redacted. `docs/backlog.md` in the repository lists everything already
known to be broken; it is worth a search before filing.
