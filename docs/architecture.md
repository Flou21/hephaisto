# Watchtower architecture

## The pipeline

```
   SOURCES                    CORE                                 OUTCOMES
┌──────────────┐
│ K8s informers│──┐      ┌──────────┐   fingerprint   ┌──────────┐
│(pods/events/ │  ├─────▶│  Signal  │────dedup───────▶│ Incident │
│ nodes/jobs)  │  │      └──────────┘                 └────┬─────┘
└──────────────┘  │                                        ▼
┌──────────────┐  │                              ┌──────────────────┐
│ Alertmanager │──┤                              │ 1. INVESTIGATE   │ read-only tools,
│   webhook    │  │                              │    (LLM + tools) │ step-budgeted
└──────────────┘  │                              └────────┬─────────┘
┌──────────────┐  │                                       ▼
│ Periodic     │──┘                              ┌──────────────────┐
│ PromQL sweep │                                 │ Finding+Evidence │ ── Grafana annotation
└──────────────┘                                 └────────┬─────────┘ ── Blazor timeline
                                                          ▼
                                                 ┌──────────────────┐
                                                 │ 2. PLAN          │ NO tools,
                                                 │    (LLM, schema) │ JSON schema only
                                                 └────────┬─────────┘
                                                          ▼
                                                 ┌──────────────────┐
                                                 │ 3. EXECUTE       │ pure C#,
                                                 │   PolicyEngine   │ closed vocabulary
                                                 └────────┬─────────┘
                                        ┌─────────────────┴──────────────────┐
                                   Allow (low risk)                   RequireApproval
                                        ▼                                    ▼
                                 dry-run → execute                    await human
                                        └───────────────┬────────────────────┘
                                                        ▼
                                          ┌───────────────────────────┐
                                          │ Verify T+1m / T+5m / T+15m│
                                          └──────────┬────────────────┘
                                    ┌────────────────┼────────────────┐
                                    ▼                ▼                ▼
                                Resolved       auto-rollback      Escalated
```

## The single most important design decision

**The LLM never holds a mutating tool handle.**

Phase 1 gives it read-only tools. Phase 2 is a separate model call with *zero* tools and a
JSON response schema. Phase 3 is pure C# over the typed result, against a closed `ActionType`
enum.

A prompt injection in a log line can therefore at most produce a *plan* that the
deterministic policy engine then rejects. It can never reach the Kubernetes API. The split
also sidesteps Gemini's historical "tools XOR responseSchema" restriction, which is a
convenient second reason for a decision that was already correct on security grounds.

## Incident state machine

```
Detected ─► Triaging ─┬─► Suppressed          (dedup / flap / maintenance / self-signal)
                      └─► Investigating ─┬─► Escalated   (budget exhausted, low confidence,
                                         │                policy deny, no plan, quarantine)
                                         ├─► AwaitingApproval ──(timeout)──► Escalated
                                         │        │ approve
                                         └─► Acting ─► Verifying ─┬─► Resolved
                                                                  └─(rollback)─► Escalated
Any ─► Expired (24 h no signal) │ Resolved (human, or signal quiet 10 min)
```

Transitions go only through `Core/IncidentStateMachine.cs`, one method per edge, each
emitting an `IncidentEvent` row. The column answers "what state is it in"; the event log
answers "how long was it awaiting approval" — both are needed and neither substitutes.

**The LLM may propose `Resolved`; only the verifier grants it**, after checking Kubernetes
and PromQL. A model marking its own work complete is not evidence.

## Fingerprinting, dedup and correlation are deterministic C#

Never the LLM at ingest — it would be both expensive and nondeterministic on the hot path.

- **Fingerprint** = `sha256(source | kind | cluster | namespace | ownerKind/ownerName | reason)`,
  keyed on the **owner**, never the pod name. A Deployment whose pods churn produces one
  fingerprint, not fifty.
- **Burst collapse** on the fingerprint over 5 minutes.
- **Flap detection**: more than 3 incidents in an hour ⇒ `Suppressed{Flapping}` with a 4-hour
  cooldown, plus one meta-incident routed straight to `Escalated`.
- **Correlation** by `CorrelationKey`, or same namespace with an overlapping ownerRef chain
  within 10 minutes, or a node-level signal absorbing pod signals on that node.

## Context management — why logs are never passed through raw

Pod logs are the whole cost problem. `Core/LogDigester.cs`:

1. strip ANSI and timestamps,
2. normalise UUIDs, hex, integers, IPs and durations to placeholders,
3. group identical normalised lines and emit the top-K clusters as
   `{count, firstSeen, lastSeen, exemplar}`,
4. always keep the last 40 lines verbatim,
5. always keep every line matching `panic|fatal|exception|OOM|refused|timeout|unauthorized|denied`
   verbatim with ±3 lines of context,
6. hard cap at 8 KB with the omission marked.

The full raw log goes to `evidence_blobs`; the model gets an `evidence://step/{id}` URI it can
cite and a human can click. **Digest for the model, raw for the audit.**

## Grounding is a runtime invariant, not a prompt instruction

Every `Evidence` must reference a `StepId` from *this* investigation, and its `Excerpt` must
be a substring of that step's stored result after whitespace normalisation. Verified with
`Contains`.

Failing evidence is dropped; a finding with zero surviving evidence is dropped; a plan citing
a dropped finding is rejected and the incident escalates. Counted as
`grounding.rejected{reason}` — a rising rate is the earliest signal of prompt drift.

This is checked in code rather than asked for in the prompt because asking does not work: a
model that hallucinates a plausible log line will also sincerely believe it cited it.

## Safety architecture, outermost first

The outermost layer is the one that survives a compromised process.

1. **RBAC** — read cluster-wide, write only into `watchtower-chaos`, no Secrets access at
   all. A `SelfSubjectAccessReview` at startup asserts the agent does *not* hold verbs it
   should never have, and refuses to boot if it does.
2. **Policy engine** — pure, deterministic, default-deny.
3. **Self-protection** — `watchtower`, `watchtower-obs` and `kube-system` are permanently
   denied. The agent may never act on itself or on the stack it depends on to see.
4. **Budget and rate limits**, Postgres-backed so they survive a restart. Exceeding a budget
   *downgrades to RequireApproval* rather than hard-denying: a human must still be able to act.
5. **Cooldown** — 15 minutes per workload, checked inside the same transaction that inserts
   the action.
6. **Kill switch**, three independent forms: env var, live-watched ConfigMap, database row.
   Fail-safe direction: an unreadable ConfigMap reads as `observe`; unreachable Postgres means
   refuse to act.
7. **Modes** — `observe`, `dryrun` (really calls the API with `dryRun=All`), `auto`. Promote
   **per action type**, never globally.
8. **Stability gate**, evaluated immediately before execution: no acting during an in-flight
   rollout, on pods younger than 120 s, in a maintenance window, or when the cluster-wide
   unhealthy fraction is high.
9. **Oscillation detection** — the same action three times in two hours with the incident
   reopening ⇒ 24-hour quarantine. This is the concrete answer to "it restarts a pod that
   crashes again forever".
10. **Verification and auto-rollback** at T+60 s, T+5 m, T+15 m.
11. **Immutable audit trail**, append-only in Postgres and mirrored to Kubernetes Events on
    the target object — so `kubectl describe pod` shows *"watchtower restarted this pod
    because …"*, which is where an on-call engineer actually looks.

### Why L3 is safe enough to enable, in four sentences

RBAC bounds the worst case to *delete pods and patch workloads in one namespace*. The LLM
never holds a mutating handle, so prompt injection from a log line can at most produce a plan
the policy engine rejects. Every auto action is individually reversible and is actually
reverted on failed verification. Budget, cooldown and oscillation caps mean the worst
*sustained* case is about ten pod restarts an hour — indistinguishable from a badly tuned HPA.

## Self-observability

```
watchtower.incident      {incident.id, correlation_key, signal.kind, k8s.namespace, workload}
└ watchtower.investigation {investigation.id, model, budget.steps, budget.usd}
   ├ chat                gen_ai.operation.name=chat, gen_ai.system=gemini,
   │                     gen_ai.request.model, gen_ai.usage.input_tokens/output_tokens
   │                     ← emitted free by .UseOpenTelemetry() (semconv v1.37)
   ├ watchtower.tool.*   {tool.name, tool.result_bytes, tool.truncated, k8s.*}
   └ watchtower.plan     {plan.action_count, plan.max_risk}
watchtower.policy.evaluate {decision, reasons}
watchtower.action.*        {action.id, action.risk, action.mode, action.dry_run}
watchtower.verification    {result, attempt}
```

Order matters: **`.UseOpenTelemetry()` before `.UseFunctionInvocation()`** in the
`ChatClientBuilder` chain, or the tool calls are not captured inside the chat span.

The `observability-selfcheck` rules webhook back into the agent's own ingest, with
**self-signals hard-coded to `Escalated` and never auto-actionable** — otherwise the agent
can act on itself in a feedback loop.

## Persistence: Postgres 17 + pgvector

Four demands that rarely co-occur, all served by one process:

1. **ACID across a multi-row decision.** The budget check, cooldown check, kill-switch check
   and the action INSERT must be one transaction, or there is a TOCTOU race on the one code
   path where a race means an unintended `kubectl delete`. This requirement alone eliminates
   most alternatives, and it is why the agent is a single pod.
2. **Relational audit queries** — "every action on deployment X in 30 days".
3. **Heterogeneous payloads** — alert bodies, tool args, pre/post state → `jsonb` + GIN.
4. **Hybrid search over incident history** — pgvector HNSW for semantics, tsvector GIN for
   exact identifiers, fused with Reciprocal Rank Fusion (k=60).

Hybrid rather than pure vector because **vector search reliably misses exact identifiers** —
an image tag, an error code, a workload name — which is exactly what an SRE query is often
about.

**Retention is asymmetric on purpose**: evidence blobs (~1 MB) expire at 30 days; incident
digests and their embeddings (~2 KB) are kept indefinitely. History stays searchable long
after the logs behind it are gone, which is why a digest must stand on its own.
