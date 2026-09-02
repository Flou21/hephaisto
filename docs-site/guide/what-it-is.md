# What it is

Hephaisto is an autonomous SRE agent that lives in your Kubernetes cluster. It receives
Alertmanager webhooks, investigates what is wrong using PromQL, LogQL, traces and the Kubernetes
API, and writes up a diagnosis with the evidence it used.

It is also a first-class *producer* of telemetry: every investigation is a trace you can open in
Grafana, step through, and then ask the agent about.

## The pipeline

```
Alertmanager ──▶ ingest ──▶ dedup + correlate ──▶ incident
                                                    │
                                                    ▼
                                          investigation loop
                                    (read-only tools, budget-capped)
                                                    │
                                                    ▼
                                  diagnosis + evidence + proposed plan
                                                    │
                                                    ▼
                                            policy engine
                                            (default-deny)
                                                    │
                              ┌─────────────────────┼─────────────────────┐
                              ▼                     ▼                     ▼
                          escalate            await a human            execute
                         to a human            (approve)         (closed action enum)
                                                    │                     │
                                                    └──────────┬──────────┘
                                                               ▼
                                              verify at T+60s / T+5m / T+15m
                                                               │
                                                    ┌──────────┴──────────┐
                                                    ▼                     ▼
                                                resolved          revert / escalate
```

An investigation is a bounded agentic loop. The model gets read-only tools — Kubernetes reads,
PromQL, LogQL, trace search — and a step, token, cost and wall-clock budget. It ends by calling
`conclude` with a root cause, the evidence it relied on, and a confidence. Everything it did is
persisted as a step trace you can replay in the UI.

The fastest way to understand the shape of that is to
[look at ten real ones](https://demo.hephaisto.dev) — no install, no account.

## What exists today

Being precise about this matters, because the difference is the whole safety argument.

| | |
|---|---|
| Alertmanager ingest, dedup, correlation, suppression | **works** |
| Investigation loop with read-only tools, budgets, step trace | **works** |
| Diagnosis, evidence, semantic incident search | **works** |
| Policy engine — default-deny, pure, exhaustively unit-tested | **works** |
| Kill switch — three independent arms, most restrictive wins | **works** |
| Audit log, budgets, cooldowns, oscillation detection | **works** |
| Plan generation (schema-constrained, no tools) | **works** |
| Executing a plan against the cluster | observed on a cluster, five action types |
| Verification at T+60s / T+5m / T+15m, and rollback | observed closing an incident once, on one fixture |
| Approval workflow — UI and API | **works** |
| Oscillation detection wired to a workload quarantine | **works** |
| `SilenceAlert` — always requiring approval | built, needs Alertmanager configured |
| Outbound notifications: webhook and Teams, over a Postgres outbox | **works** |
| `RollbackDeployment`, `PatchResources` | not built — refused, not attempted |
| Runbook memory, OIDC approval identity, in-card approval | not built |
| A written design language, one token set, visual regression baselines | **works** |

**The wording of each row is chosen, not casual.** Detection, investigation and diagnosis are
measured against a real cluster over ten seeded scenarios. The delivery path was measured
including the assertion it exists for: the receiver taken down, the agent restarted mid-flight,
the receiver brought back, and the delivery arriving anyway.

## The row to read carefully

The last step — an incident the agent acted on reaching `Resolved` — went unobserved for four
releases. It was confirmed on a cluster on 2026-09-02, on the `c13-wedged-lock` fixture:
`Detected → Triaging → Investigating → Acting → Verifying → Resolved`, 41 seconds after the
restart, granted by `hephaisto/verifier`. **That is one run on one fixture.** Read it as "this
path works end to end and has been seen to", not as a rate.

Whether the planner proposes an action at all is a property of the **fixture** as much as of the
model, and the two numbers below measure different questions. They must not be averaged.

| Fixture | Model | Runs where the planner ran | Proposed an action |
|---|---|---|---|
| `c13-wedged-lock` | `gpt-oss:120b` | 6 | 6 |
| `c12-stale-lease` | `gpt-oss:120b` | 11 | 1 |
| `c12-stale-lease` | `deepseek-v4-flash` | 8 | 4 |

c13 puts the wedge on an `emptyDir`, which the planning prompt already names as pod-scoped state
a replacement pod does not reproduce — so the rule it needs is one it already holds, and this
measures **willingness to act**. c12 puts the same fault on a PVC, so acting means overriding the
*correct* rule that PVC contents survive a replacement; that measures an **inference**, and
`gpt-oss:120b` gets it wrong 7 times in 9 when asked point blank. On c12 Fisher's exact gives
p = 0.11.

An earlier **p = 0.0047** was published here, over 24 runs. Nine of those had ended on a token
budget before the planner ever ran, and were counted as declines; it did not survive counting the
denominator honestly. Still worth knowing before you choose a model — a cheap local model can
diagnose well and still decline the harder shape — but the honest statement names its fixture.
[The evidence page](/internals/evaluation) has the detail.

## What it deliberately does not do

The executor covers exactly the verbs the write `Role` grants: `RestartPod`, `RolloutRestart`,
`ScaleWorkload`, `DeleteStuckJob` and `DeleteFailedJobPods`. Anything else is **refused before a
call is made**, with `outcome=unsupported` and nothing attempted — which for `CordonNode` and
`DrainNode` is the honest answer, because their `ClusterRole` ships deliberately unbound.

The chart installs the agent, its RBAC, both NetworkPolicies, the PodMonitor, the alert rules, a
Grafana dashboard, and — opt-in — an evaluation Postgres. It does **not** install Prometheus,
Alertmanager, Grafana, Loki, Tempo or a collector. You already run those.

## Next

- [See it without a cluster](/guide/without-a-cluster) — two containers, no API key
- [Requirements](/guide/requirements) — what you need before installing
- [Install](/guide/install) — the chart, and the secrets it deliberately will not create
