# Hephaisto architecture

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

**No model in this process ever holds a mutating handle to the cluster.**

Until v0.9.0 this read "the LLM never holds a mutating tool handle", and for the investigator
it still means exactly that. It was re-scoped, not weakened, when the code-fix stage arrived:
that stage's coder is by construction a model holding a shell and a filesystem, so "never a
mutating handle" could no longer be true of every model Hephaisto starts. What stays true is the
part the safety argument rests on — *nothing a model does can reach the Kubernetes API*. The
investigator and planner run in this process with no write tool at all; the coder runs in a
separate pod whose identity is bound to nothing, and its only write target is a git branch and a
Draft PR. See [Code fixes](#code-fixes-a-second-separately-gated-stage).

Phase 1 gives it read-only tools. Phase 2 is a separate model call with *zero* tools and a
JSON response schema. Phase 3 is pure C# over the typed result, against a closed `ActionType`
enum — `ActionExecutor`, a `switch` whose every arm is a typed API call written in this repo.

Two places where that could have leaked, and does not. The executor covers only the verbs the
write `Role` grants and refuses everything else **before** a call is made, so a plan naming
`CordonNode` produces `outcome=unsupported` rather than a 403. And the rollback spec is
free-form JSON the model wrote: it is read for typed values and never executed as written,
because doing so would hand back the mutating handle on the one path nobody watches — minutes
after the incident, with budgets deliberately bypassed.

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

1. **RBAC** — read cluster-wide, write only into the actionable namespaces
   (`hephaisto-chaos` here), no Secrets access at all. A `SelfSubjectAccessReview` at startup asserts the agent does *not* hold verbs it
   should never have, and refuses to boot if it does.
2. **Policy engine** — pure, deterministic, default-deny.
3. **Self-protection** — `hephaisto`, `hephaisto-obs` and `kube-system` are permanently
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
   reopening ⇒ 24-hour quarantine, recorded against the **workload** on the row admission
   already locks. This is the concrete answer to "it restarts a pod that crashes again
   forever", and it is the only control that notices the agent is not helping: every other one
   caps a rate, and a workload failing every fifteen minutes sits comfortably inside all of
   them. Not on the incident, because a recurrence arrives as a *new* incident — so a
   quarantine held there would lapse exactly when the loop would otherwise continue.
10. **Verification and auto-rollback** at T+60 s, T+5 m, T+15 m — deterministic predicates,
    never a model, and only the last attempt may conclude a failure. The three answer different
    questions rather than retrying one: at 60 s "did anything obviously break", at 5 m "has it
    converged", at 15 m "did it come back". A check that cannot run is Inconclusive, never
    Failed, so an API timeout does not revert a healthy cluster.
11. **Immutable audit trail**, append-only in Postgres and mirrored to Kubernetes Events on
    the target object — so `kubectl describe pod` shows *"hephaisto restarted this pod
    because …"*, which is where an on-call engineer actually looks.

12. **`create jobs` in the coder namespace only.** The one `create` verb on a code-executing
    resource the agent holds, granted by a namespaced `Role` in `codeFix.namespace` and nowhere
    else. The chart refuses to render if that namespace is `default`, `kube-*`, the release or
    observability namespace, or actionable; `RbacSelfCheck` refuses to boot if `create jobs` is
    held cluster-wide, in `kube-system` or in any actionable namespace, and refuses `create pods`
    anywhere. Rendered only when `codeFix.enabled`.
13. **Coder isolation.** A coder pod has no ServiceAccount token (its identity is bound to
    nothing), no Hephaisto credential, no inbound surface, a read-only root, no capabilities, and
    egress only to DNS and an allowlist proxy that logs every request. Its credentials arrive by
    `secretKeyRef` from a Secret Hephaisto can name and cannot read. The investigator's read
    tools are denied the coder namespace, so a coder's output never becomes the next
    investigation's evidence.

### Why L3 is safe enough to enable, in four sentences

RBAC bounds the worst case to *delete pods and patch workloads in one namespace*. No model
holds a mutating handle to the cluster, so prompt injection from a log line can at most produce
a plan the policy engine rejects — or, with code fixes on, a Draft PR a human must still merge. Every auto action is individually reversible and is actually
reverted on failed verification. Budget, cooldown and oscillation caps mean the worst
*sustained* case is about ten pod restarts an hour — indistinguishable from a badly tuned HPA.

## Code fixes: a second, separately gated stage

Most production incidents on the cluster this was built for are bugs in application code, and an
agent whose actions are *restart, roll back, scale* can only escalate them. v0.9.0 adds a stage
after the outcome rather than a new action: when an investigation escalates because the planner
said a human must fix the code (`NoPlanProduced`) and its grounded primary finding says
`application`, a **coder** — Claude Code via the Agent SDK, in a Kubernetes Job — clones the
repository the workload is mapped to and writes a fix plan. After a human approves, a second Job
implements it and opens a Draft PR. It is not an `ActionType`, on purpose: every action is gated
by the agent mode and denied in `Observe` before approval routing, would take the workload's lock
and cooldown for something that never touches the workload, and would change the planner's prompt
— and therefore every recorded cassette.

**Two axes.** `AgentMode` means *may mutate the cluster*. `CodeFixMode` (`Off | Plan | Pr`) means
*may start a coder*, and is independent: an `Observe` agent may still plan code fixes, because
planning writes nothing anywhere, and tying the two together would either lock production out or
force widening cluster autonomy for a git feature. The kill switch still wins across both — agent
`Off`, the emergency stop, the runaway latch or any unreadable arm all resolve the code-fix mode to
`Off`. Silence is `Off`: an install that never configured the stage spends nothing on it, though
every escalation is still *evaluated* and the verdict recorded, which is the evidence an operator
turns it on from.

**The double opt-in.** A workload reaches a coder only if the operator mapped it
(`codeFix.repositories`, evaluated in-process by the pure `CodeFixEligibility` predicate — the
same posture as `actionableNamespaces`) *and* the repository's own entry in dev-context's
`repos.yaml` says `coderEnabled`, which the coder enforces. Eligibility is default-deny and
accumulates reason codes in gate order like the policy engine; infrastructure-only signal kinds
never qualify whatever the category says.

**The pull model and result framing.** The coder has no callback. It prints a framed block as the
last thing on stdout — `---HEPHAISTO-RESULT-BEGIN sha256=… bytes=…---`, JSON,
`---HEPHAISTO-RESULT-END---` — and Hephaisto reads its pod log with the `get pods/log` it already
held, takes the *last* pair, verifies length and hash, and deserialises with unknown members
disallowed. Anything else is `Failed(ContractViolation)`. A callback endpoint would be a second
write surface into Hephaisto, reachable by a shell that has just read attacker-influenceable logs,
on installs where the NetworkPolicy is off. The cost is no live streaming.

**What the coder can reach.** Its ServiceAccount is bound to nothing and its token is never
mounted, so it cannot read a Secret, a log or a pod. Its egress is DNS and a squid proxy whose
domain allowlist is the model API, GitHub and the package registries; `CONNECT` only to 443, and
every request is a line in the proxy's log. Its GitHub token is a fine-grained PAT scoped to the
mapped repositories, and branch protection on their default branches is an operator prerequisite
the chart names and cannot check. Hephaisto post-validates everything it is told — the branch is
the one it assigned, the PR is in the mapped repository on an allowed host, the build was green —
before recording a PR.

**What it cannot.** Merge, deploy, touch the workload, reach the cluster API, read the incident
database, or see a credential Hephaisto holds. The only model-influenced input to the Job is a
ConfigMap of JSON the coder treats as data; the image and the spec are chart values and golden-
tested C#.

**The same-uid caveat, stated rather than hidden.** Inside the coder pod the driver (which holds
the GitHub token to push) and the agent's Bash tool run as the same uid, so a determined agent
could read the driver's environment through `/proc`. The controls that actually bound that are
outside the pod: the token's repository scope, branch protection, the proxy allowlist and its log,
and the guard hook's denial record. The v2 hardening is a two-container split — a driver container
holding the tokens and an agent container without them, sharing `/work` — deferred because the
first version has to prove the flow before it is worth splitting.

## Self-observability

```
hephaisto.incident      {incident.id, correlation_key, signal.kind, k8s.namespace, workload}
└ hephaisto.investigation {investigation.id, model, budget.steps, budget.usd}
   ├ chat                gen_ai.operation.name=chat, gen_ai.system=gemini,
   │                     gen_ai.request.model, gen_ai.usage.input_tokens/output_tokens
   │                     ← emitted free by .UseOpenTelemetry() (semconv v1.37)
   ├ hephaisto.tool.*   {tool.name, tool.result_bytes, tool.truncated, k8s.*}
   └ hephaisto.plan     {plan.action_count, plan.max_risk}
hephaisto.policy.evaluate {decision, reasons}
hephaisto.action.*        {action.id, action.risk, action.mode, action.dry_run}
hephaisto.verification    {result, attempt}
```

Order matters: **`.UseOpenTelemetry()` before `.UseFunctionInvocation()`** in the
`ChatClientBuilder` chain, or the tool calls are not captured inside the chat span.

The `observability-selfcheck` rules webhook back into the agent's own ingest, with
**self-signals hard-coded to `Escalated` and never auto-actionable** — otherwise the agent
can act on itself in a feedback loop.

## Reaching people: the outbox

Everything above happens inside one process. This is the part that does not.

**The problem it solves is not "send a message".** `IIncidentNotifier` could already send one:
an in-process `Channel<T>` fan-out to Blazor circuits, bounded at 64 with `DropOldest`. It is a
fine hook point and a catastrophic delivery mechanism, because it is *designed* to drop — right
for nudging a browser that will re-read from Postgres anyway, and wrong for the one message
whose whole purpose is to reach somebody who is not looking. "Escalated, and nobody was told" is
the worst failure this system has, and a pod restart must not be able to cause it.

So delivery is a table.

```
IncidentStateMachine.Transition        appends an IncidentEvent on EVERY edge
        |
        v
HephaistoDbContext.SaveChangesAsync    interceptor scans new IncidentEvent rows,
        |                              evaluates routing, adds notification_deliveries
        v                              -- ALL IN THE SAME TRANSACTION
  one commit
        |
        v
NotificationDispatcher (10s poll)      due rows off (status, next_attempt_at)
        |
        +-- rate limit --> Suppressed (recorded, counted, never discarded)
        |
        +-- INotificationChannel --> Delivered
                                 --> Retryable  -> backoff, stay Pending
                                 --> Permanent  -> Failed + audit row + ERROR
```

### Why the interceptor rather than ten call sites

The obvious design is an enqueue call at each place an incident commits a transition. The
obvious failure of that design was already in this codebase: `IncidentTriage` reaches
`Escalated` twice — the self-signal arm and the storm circuit breaker — and published no live
event at all. Nobody noticed, because nothing asserted it.

`Transition` appends an `IncidentEvent` on every edge without exception; that is the log the
audit trail is built from. Watching those rows gives the property directly: **an incident cannot
reach a notifiable state without a delivery row being written by the same
`SaveChangesAsync`.** There is no ordering, no second commit, and nothing for an eleventh call
site to forget.

The two events that are not transitions — the mode changing and the policy reloading — are
enlisted explicitly, following the same stage-don't-save discipline as an audit row.

### Why the payload is frozen

An outbox row can sit for twenty minutes behind a failing endpoint. Re-reading the incident at
send time — which is exactly what `IIncidentNotifier` correctly does for a UI nudge — would make
a retry describe a *later* state than the event it reports, so an escalation card would quietly
become a resolution card. The snapshot is taken at enqueue and never re-read.

Deep links are the exception and are built at render, because a wrong base URL should be fixable
by editing a value rather than by re-queuing every pending row.

### The Teams bot is a projection, and the one place the payload is not frozen

Everything above describes a delivery: something happened, and a message reports it. The Teams bot
(`notifications.teamsBot`) keeps two kinds of message that are not deliveries in that sense,
because they go on being edited after they are sent.

```
TeamsBoardReconciler (15s)   render the board from the open incidents  --> hash differs? PUT
                             render every live alert from its incident --> hash differs? PUT
                             incident over? the alert says so and becomes Final

NotificationDispatcher       an event routed to teamsBot --> POST a new alert to each
                             recipient's personal chat; an older live alert for the same
                             incident becomes Superseded and is shrunk to one line
```

**A comparison on a timer, not a reaction to events.** There is no queue of pending edits, so
nothing has to be replayed in order: an edit that failed is still different on the next tick, and
one that a newer state overtook is never sent. It also covers what announces nothing - closing,
assigning and acknowledging are not notifications, and all three change the board.

**Both cards show the incident as it is now.** That is the opposite of the frozen snapshot, on
purpose, and it is confined to this channel. What an event contributes is the decision to post
and the line a lock screen shows. That line is kept out of the content hash: it names the event
while the card shows the state, and hashed together the first comparison after every alert would
edit a card that had not changed.

**It never deletes.** Measured against a real tenant: a deleted channel post and a deleted reply
both leave "This message has been deleted." behind, and nothing switches that off. So
`ITeamsBotClient` has no delete, a closed incident leaves the board by being edited out, and an
alert that is over is edited into its final state and left.

**Two buttons act, and only when asked to.** With `Notifications:TeamsBot:Actions:Enabled` an open
alert carries Acknowledge and Assign to me as `Action.Execute`, and Teams delivers the click as an
`adaptiveCard/action` invoke to `POST /api/teams/messages`:

```
Teams --invoke + Bot Framework JWT--> :8082 /api/teams/messages   (nothing else answers on 8082)
  scheme "BotFramework": issuer, audience = app id, signature, key ENDORSED for msteams
  handler: serviceurl claim = activity.serviceUrl, tenant = ours, from.aadObjectId in the roster
  --> IncidentQueries.Acknowledge/Assign as the roster names the person --> refreshed card
```

Every check fails closed before an incident is touched, and a roster nobody could read is a 503,
not a yes. The actor is the member list's name for the object id, never the display name the
activity carries. Close, approve and deny stay links (backlog #124).

**An edit notifies nobody**, which is why an alert is a new message and why it goes to a personal
chat: the channel holds one message, and a person's own chat with the bot is where a message per
alert is a history rather than a flood.

**The board is sized in bytes, not rows.** Forty rows with long titles, a PR and a resolution
note come to about 160 KB; the largest board Teams was seen to accept was 114 KB. It trims itself
to 100 KB and says how many incidents it left out.

### One retry authority

`ServiceDefaults` applies `AddStandardResilienceHandler` to every client the HTTP factory
builds. The notification channels opt out of it. The outbox owns retry because the outbox is the
only layer that survives a restart, and two schedules stacked would multiply every attempt
against an endpoint that is already struggling.

### The notifier must not amplify a storm

Ingest has dedup, flap suppression and a storm circuit breaker; the outbound side inherits none
of them, and a storm that opens forty incidents would otherwise produce forty pages. Two
controls, both pure functions in `Hephaisto.Core` for the reason `ActionBudget` is: a status
page has to be able to answer "why did that not go out" with the identical arithmetic.

The **first** message for a workload always goes out. A cooldown that could swallow the opening
message would be a worse failure than the storm it prevents. The repeats are suppressed, counted
on the row, and stated on the next message that does go out.

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
