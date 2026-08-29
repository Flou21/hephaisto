# Hephaisto backlog

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

Everything known to be broken, half-built, or lying — found during development and not fixed at
the time. Each entry carries the **symptom**, the **evidence** that it is real, **why it is still
open**, and a rough **size**.

Grouped by area rather than by priority. Priority lives in [`roadmap.md`](roadmap.md), and keeping
a second ordering here would guarantee the two drift apart. Milestones link in by anchor.

Sizes are honest guesses: **S** ≈ under an hour, **M** ≈ a day, **L** ≈ several days.

Item numbers are **stable ids, not an ordering**. New items take the next free number and are
filed under the area they belong to, so a number can appear out of sequence. Renumbering would
break every anchor `roadmap.md` links by, which is a worse problem than a non-monotonic list.

An item leaves this file by being fixed, or by being reclassified as a deliberate limitation and
written down somewhere permanent. It does not leave by being ignored.

---

## Measurement integrity

The instruments this project judges itself with. These come first because every other number in
the repo is only as good as they are.

### 1. The e2e Playwright phase reports `pass` on a zero-assertion run

**Symptom.** The console phase of `scripts/e2e/run.sh` ticks green without running a single
assertion.

**Evidence.** Decoding the last committed report:

```json
{ "total": 5, "expected": 0, "unexpected": 0, "flaky": 0, "skipped": 5, "ok": true }
```

All five specs carry `"outcome": "skipped"` with `"results": []` — they were never started, not
skipped inside their bodies. The gate is exit status alone:

```sh
"$E2E_DIR/ui/run.sh" \
    && pass "playwright suite" \
    || fail "playwright suite" "see $E2E_DIR/ui/playwright-report"
```

Playwright exits `0` when everything skips (`"ok": true`), so five missing assertions record as one
passing phase.

**Why it is still open.** It was not visible: the phase *did* record a row, so the harness's own
defence against silent phases (`lib/report.sh` — "phases that recorded nothing at all") does not
catch it. Same class as the bug fixed one level up in `e6941db` ("stop calling an aborted run
PASSED").

**Fix.** Emit the JSON reporter from `ui/run.sh` and assert `expected > 0 && skipped == 0`. Also
note `[ -x "$E2E_DIR/ui/run.sh" ]` silently `skip`s if the file loses its exec bit.

**Size.** S. **Blocks:** the v0.4.0 CSS refactor, and generated screenshots.

### 2. Six of ten chaos fixtures never run in an automated gate

**Symptom.** The MVP bar — ≥ 7/10 correct root cause over ≥ 10 seeded scenarios — cannot be
reached by the harness as configured. It runs four.

**Evidence.** `scripts/e2e/lib/chaos.sh` defaults to `c2,c3,c4,c7`, "chosen to discriminate rather
than to cover". Each exclusion has a stated and real reason: **c9** is node-wide and evicts pods
across the cluster including Prometheus and the agent (the harness refuses it even if asked);
**c6** cannot fire on `local-path`, where every PVC reports the node filesystem and the ratio moves
by 0.0045; **c1** has no pod-scoped `OOMKilling` event on k3s+containerd (the kubelet raises
`SystemOOM` against the Node); **c8** needs a 30-minute window; **c10** needs a local image build
plus `kind load`.

**Why it is still open.** Every individual exclusion is correct. The problem is only visible when
the count is compared against the bar, which nothing did.

**Fix.** Two parts. Let the eval harness own the bar, over recorded scenarios including manually-run
fixtures, and say which instrument produced which number. Separately, raise automated coverage to
the tractable three (c1, c5, c8).

**Size.** M.

### 3. `hephaisto.human.feedback` is never recorded

**Symptom.** The project's only externally-sourced quality signal is not measured. Two dashboard
panels are permanently empty.

**Evidence.** The instrument exists (`Telemetry/HephaistoMetrics.cs:60`) and the recorder exists
(`:112`), and `grep -rn '\.HumanFeedback(' src/` returns **nothing**. The UI form works and
`Web/IncidentQueries.AddFeedbackAsync` writes a `HumanFeedback` row to Postgres — but
`IncidentQueries` does not take a `HephaistoMetrics` dependency at all.

The constant's own doc comment is the sharpest part: *"The only honest false-positive rate
available. Built in from day one on purpose."* It is not built in.

**Why it is still open.** The Postgres write works, so the feature looked done from the UI.

**Size.** S.

### 4. `hephaisto.incidents.closed` and `hephaisto.incident.duration` are never recorded

**Symptom.** MTTR is undrawn. Three dashboard panels are permanently empty, plus the "closed"
series on a fourth.

**Evidence.** Instruments at `Telemetry/HephaistoMetrics.cs:45,50`, recorders at `:72,78`, zero
call sites for either. Their twin `DetectionLatency` (MTTD) *is* called, at
`Pipeline/IncidentTriage.cs:109`, one line after `IncidentOpened`.

**Why it is still open.** Both fire on incident *closure*, and until [#11](#11-there-is-no-production-path-to-resolved)
is fixed there is barely any closure to record. They are entangled.

**Size.** S, after #11.

### 5. `hephaisto.incidents.open` and `hephaisto.budget.remaining` have no instrument at all

**Symptom.** Declared, charted, never emitted — one step worse than #3 and #4, which at least have
instruments.

**Evidence.** Both named in `Core/Telemetry/HephaistoTelemetry.cs` (`:34`, `:63`) and drawn on the
shipped dashboard; no `CreateUpDownCounter` or `CreateObservableGauge` for either anywhere in
`src/`. Same class as `hephaisto.llm.budget_utilization`, fixed 2026-08-29 — that one went first
because two alert rules rested on it.

**The general fix is worth more than either**: a test asserting that every `hephaisto_*` metric
named in the shipped alert rules or dashboard corresponds to a real instrument.

**Note the trap.** As originally written that test asserts *"an instrument is created"*, which
passes on #3 and #4. It must assert **"something records it"**.

**Size.** S each; M for the guard test.

---

### 31. grafana-mcp exposes no Tempo tools, so c10's whole reason for existing is untestable

**Symptom.** The fixture built to prove alert → exemplar → trace → log correlation cannot reach
traces at all. The agent's own log says so on every startup:

```
grafana-mcp offers 44 tools; 4 allowlisted tools are absent:
  query_tempo_traces, query_tempo_traceql, list_tempo_tag_names, list_tempo_tag_values
```

**Evidence.** `c10-faulty-service.yaml`'s header calls it "THE IMPORTANT ONE" and explains why:
C1–C9 are single- or two-signal scenarios, and c10 "IS THE ONLY FIXTURE THAT PRODUCES A FULLY
CORRELATED THREE-SIGNAL TRAIL". Hops 2, 3 and 4 of that trail — exemplar, trace, log-by-trace-id —
all require Tempo.

mcp-grafana reaches Tempo through its **proxied tools** mechanism, which discovers an external
Tempo MCP server; the deployed `grafana-mcp` is started with `--disable-oncall --disable-incident
--disable-asserts --disable-sift --disable-pyroscope` and no Tempo server configured, so those four
tools are never registered. The allowlist naming them is aspirational.

Measured on the dev cluster on 2026-08-29: recording c10 spent **13 steps and 16 tool calls and
produced no primary finding** — the only fixture of the eight to produce none.

**Why it is still open.** Not noticed, because nothing asserted it. The absence is logged at
`Information` on a line that reads like a routine startup message, and no test or e2e phase checks
that an allowlisted tool actually exists. The agent degrades silently to the two hops it can do.

**Size.** M to wire a Tempo MCP server into grafana-mcp; S to make the absence loud — an
allowlisted tool the server does not offer should be a startup warning that names the capability
being lost, not an info line.

---

### 34. `c1-oomkill` never produces an OOMKill on this node

**Symptom.** The fixture built to produce `OomKilled` produces `CrashLoopBackOff`, and for a while
produces a pod Kubernetes reports as `1/1 Running` while its cgroup sits at its memory limit.

**Evidence.** Observed on `studio-rancher-desktop` on 2026-08-29, from a freshly created pod:

- The balloon process writes 4Mi per second into a `medium: Memory` emptyDir against a 64Mi
  container limit, and **is not killed**. The pod stays `1/1 Running` well past the ~16s at which
  it crosses the limit.
- The cgroup *is* at its limit, though - `kubectl exec` into the healthy-looking pod fails with
  `error executing setns process: signal: killed (possibly OOM-killed)`. There is no headroom to
  fork a process, and no event says so.
- When the container does restart, init cannot start at all:
  `Error response from daemon: ... container init was OOM-killed (memory limit too low?)`. The
  tmpfs survives container restarts within the same pod, so the memory is already spent before
  the entrypoint runs.
- Every `c1` incident in the dev database is `CrashLoopBackOff`. Not one is `OomKilled` - including
  one pod that restarted **265 times over 23 hours**.

`infra/chaos/c1-oomkill.yaml` predicts "the kernel OOM killer terminates PID 1, the Deployment
restarts it, and the cycle repeats roughly every 20-30s forever". What happens is a container that
survives, then a create-time failure loop on a roughly five-minute period.

**Why it is not a blocker.** The scenario is still gradeable, and the agent gets it right: recorded
on 2026-08-29 it concluded *"the configured memory limit (64Mi) is too low, causing the OCI runtime
container init process to be OOM-killed during startup"* in 11 steps, which names the cause. The
cassette records `CrashLoopBackOff` as the kind while `AnswerKey` expects `OomKilled`, and that
disagreement is worth being able to see - which is why the cassette records the kind it actually
observed rather than the fixture's intended one.

**Why it is still open.** The fixture's README already hedged - it notes no pod-scoped `OOMKilling`
event on k3s+containerd - but the hedge understates it: the intended signal never appears at all.
Nothing asserted the kind, because c1 is not in `DEFAULT_FIXTURES`.

**Size.** M. Making it fire needs a heap allocation rather than tmpfs writes, or an emptyDir that
does not survive container restarts. Worth doing: a pod reported `Running` while pinned at its
memory limit is a genuine blind spot, and c1 is the only fixture that reveals it.

---

### 32. `chaos.sh` maps c10 to `SloBurn`, which is not a `SignalKind`

**Symptom.** The e2e harness's kind assertion for c10 can never match, whatever the agent does.

**Evidence.** `scripts/e2e/lib/chaos.sh`'s `fixture_kind()` returns `SloBurn` for c10.
`SignalKind` has eighteen members and none of them is `SloBurn`. The kind the shipped rule
actually attaches is `HighErrorRate` — confirmed against the dev cluster, where the incident
opened by `ServiceHighErrorRate` carries `hephaisto_kind: HighErrorRate`.

The eval harness cannot make the same mistake: `AnswerKey.ExpectedKind` is typed as the enum and
a test asserts every entry is a defined member. This item is about the shell harness.

**Why it is still open.** c10 is not in `DEFAULT_FIXTURES`, so the assertion has never run.

**Size.** S.

---

## Correctness and safety

### 6. Audit immutability is not enforced in the deployed database

**Symptom.** "No audit, no action" is a standing constraint, and in the deployed database nothing
enforces it.

**Evidence.** Measured on the dev cluster:

```
connected_as     | hephaisto
app_role_exists  | 0
is_superuser     | t
can_update_audit | t
can_delete_audit | t
```

The agent connects as a **superuser** and `hephaisto_app` does not exist, so the migration's
`GRANT`/`REVOKE` block — wrapped in `IF EXISTS (SELECT 1 FROM pg_roles ...)` — silently no-opped.
The integration test asserting a 42501 on `UPDATE audit_events` passes in CI (which creates the
role) and proves nothing about this cluster.

**Why it is still open.** It fails in the direction that looks fine: everything works, nothing is
denied.

**Fix.** Create `hephaisto_app`, connect as it rather than as the owner, re-run the migration so
the REVOKE applies. The chart should make the connecting role a value.

**Size.** M.

### 7. The planning prompt claims a verification-and-rollback mechanism that does not exist

**Symptom.** The model is told a safety net exists that does not.

**Evidence.** `src/Hephaisto.Agent/Prompts/30-planning.md:25-26`, verbatim: *"…automatically at 60
seconds, 5 minutes and 15 minutes, and a failed check triggers a rollback."* The same claim appears
in `Investigation/ActionPlanDraft.cs:65-72`. There is no `VerificationScheduler` and no rollback
path anywhere in `src/`; the `Verification` entity has a table, a DbSet and indices, and is never
constructed.

**Why it is still open.** Harmless while the executor does not exist — nothing acts, so nothing
needs verifying. It stops being harmless the moment anything executes, and it is shaping the
model's plans today.

**Fix.** Correct the text now; build the mechanism in v0.2.0.

**Size.** S to correct, L to build.

### 8. Nothing writes the database mode arm

**Symptom.** The kill-switch arm documented as "the one a human flips from the UI" has no UI and no
API, and a tripped runaway latch cannot be cleared from inside the product.

**Evidence.** `Persistence/Repositories/AgentModeStore.cs` declares and implements `SetModeAsync`
(`:21`, `:54`) and `ReArmAsync` (`:31`, `:81`). Neither is called from anywhere in `src/`. Only
`LatchAsync` is wired, from `Persistence/LlmBudgetService.cs`. `/api/status` reads the row; nothing
writes it.

**Why it is still open.** The other two arms (env var, ConfigMap) both work and are reachable with
`kubectl`, so the gap never blocked anyone.

**Consequence.** Clearing a runaway latch today means connecting to Postgres by hand.

**Size.** M. **Blocks:** operating v0.2.0.

### 9. Semantic search returns nothing, and the recorded cause was wrong

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `/api/incidents/search?q=out+of+memory` and `q=crash` return empty. `q=ImagePullBackOff`,
`q=image` and `q=pod` work.

**The previous diagnosis in this repo was:** *"Hybrid RRF over pgvector + pg_trgm, so the likely
suspects are the embedding of short conceptual queries, or an RRF weighting that lets exact-match
dominate."* **Neither is possible.** Recorded here because it is a good example of a plausible
hypothesis surviving a week unchecked.

**Evidence — the vector arm never runs.** The query embedding is hardcoded:

```csharp
// Web/IncidentQueries.cs:265
return await search.SearchAsync(query, queryEmbedding: null, filter, Math.Clamp(limit, 1, 100), ct);
```

`IncidentSearch.SearchAsync` then sets `semantic = false` and `BuildSql` emits the lexical-only
branch. RRF fusion never happens, so RRF weighting cannot be the cause.

**Evidence — `pg_trgm` is not used by the search at all.** The extension is created by the chart
and by `infra/app/postgres.yaml`, and there is no `similarity()`, no `%` operator and no
`gin_trgm_ops` index anywhere in `src/`. The lexical arm is Postgres full-text search over a
generated `tsvector` column built with `to_tsvector('english', digest)`.

**The actual cause is lexeme semantics, and it explains the exact data:**

| Query | Result | Why |
|---|---|---|
| `crash` | `[]` | `to_tsvector('english', 'CrashLoopBackOff')` yields the single lexeme `crashloopbackoff`. The English parser does not split camelCase, and `crash` is not a prefix match. |
| `out of memory` | `[]` | `out` and `of` are stop words and are dropped, leaving `memori`. Digests describing OOM say `OOMKilled` → `oomkil`. No overlap. |
| `ImagePullBackOff` | works | Exact single-lexeme match. |
| `image`, `pod` | work | Standalone English words, present in event text like *"Failed to pull image"*. |

So: **exact-lexeme-only matching over camelCase Kubernetes reason strings, with the paraphrase arm
switched off.** There is no fuzzy fallback, despite `pg_trgm` being installed for exactly that.

**Fixes, cheapest first.** (a) Add a `pg_trgm` similarity arm as a third CTE — the extension is
already there. (b) `to_tsquery` with `:*` prefix matching. (c) Index a space-split form of the
reason string alongside the raw digest. (d) Wire the embedding generator on the query side —
`IncidentEmbedder` already *writes* embeddings and the HNSW index is built, so the corpus side is
paid for and only the query side is missing.

**One UI knock-on.** `Components/Pages/Search.razor` infers "semantic is available" from whether any
hit came back with a `SemanticRank`, so once the vector arm lands, any query with no semantic hits
will still claim no embedding generator is wired up.

**Size.** M. **Blocks:** runbook memory in v0.1.0, which assumes this search works.

**Fixed 2026-08-29.** All four parts: `IncidentQueries.SearchAsync` generates the query embedding,
a `pg_trgm` word-similarity arm was added as a third CTE with a GIN index behind it, `SearchAsync`
now returns which arms actually ran instead of leaving the UI to infer it, and `Search.razor` reads
that. `IncidentSearchTests` is the reproduction, on a real Postgres.

**Two corrections to the analysis above, both found by measuring rather than reasoning:**

- **`q=out of memory` was not empty.** Against the dev cluster's 32 digests it returned **9** hits.
  The stop-word reasoning is right about `out` and `of`, but the digests say "memory" in prose often
  enough that `memori` matches anyway. `q=crash` was the real symptom, and it was exact: **0** hits
  before, **10** after.
- **The fusion needed restructuring, not just a third CTE.** The two-arm form was a `FULL OUTER
  JOIN` with a different `SELECT` per combination of arms; three arms would have needed seven of
  them, each repeating the scoring expression. It is now a `UNION ALL` folded with `GROUP BY`, which
  is one scoring expression and makes a fourth arm three lines.

Fixing this also surfaced that `scripts/dev-db.sh` and CI's service container never created
`pg_trgm` — only the chart did — so the first migration to use `gin_trgm_ops` failed locally with
`operator class "gin_trgm_ops" does not exist`. The `CREATE EXTENSION` now lives in the migration
beside the index, matching how `vector` is handled.

### 10. `hephaisto.io/destructive-actions-allowed` is read by no code

**Symptom.** A safety control that exists in manifests, in documentation and in test assertions,
and nowhere in the program.

**Evidence.** Applied to a namespace by `infra/namespaces.yaml:49`. Described in
`charts/hephaisto/templates/rbac-read.yaml:86-88` and `infra/app/rbac.yaml:174` as something *"the
agent's policy engine checks as a second, independent confirmation"*. Asserted by
`scripts/e2e/lib/cluster.sh:90-92`. **Zero occurrences in any `.cs` file.**

Worse, the seam it would travel through is empty: `Pipeline/InvestigationCoordinator.cs:256` passes
`TargetLabels = new Dictionary<string, string>()`, so **no label check of any kind is live**,
including `ProtectedLabels` and `AllowSingleReplicaRestartLabel`.

**Why it is still open.** Nothing executes yet, so the missing second confirmation has never been
load-bearing. The e2e assertion checks that the *label* is present, which it is.

**Size.** M. **Blocks:** v0.2.0.

### 11. There is no production path to `Resolved`

**Symptom.** An incident can only end `Suppressed` or `Escalated`. Nothing ever closes normally.

**Evidence.** `Core/IncidentStateMachine.cs` implements `AwaitApproval`, `BeginActing`,
`BeginVerifying`, `Resolve`, `Reopen` and `Expire`; grepping for callers finds them **only in
`tests/Hephaisto.Tests/IncidentStateMachineTests.cs`**. The production edges are `Triage`,
`Suppress`, `BeginInvestigation`, `Escalate` and `Reinvestigate`.

**Why it is still open.** Expected in Observe mode — nothing fixes anything, so nothing resolves.
But it also means [#4](#4-hephaistoincidentsclosed-and-hephaistoincidentduration-are-never-recorded)
has almost nothing to measure, and an operator has no way to close an incident a human dealt with.

**Size.** M. **Related:** v0.2.0.

### 12. Unbounded label cardinality on `hephaisto.grounding.rejected`

**Symptom.** GUIDs and free text are written into a Prometheus label value.

**Evidence.**

```csharp
// Pipeline/InvestigationCoordinator.cs:155
metrics.GroundingRejected(rejection.ToString() ?? "unknown");
```

`GroundingRejection` is a `record` of `(GroundingRejectionReason Reason, string Detail, Guid?
FindingId, Guid? StepId)`, so the compiler-generated `ToString()` emits all four — a label value
unique per rejection. The correct form is already in the codebase:
`Investigation/InvestigationRunner.cs:631` uses `rejection.Reason.ToString()`.

The shipped dashboard's own spec text warns against exactly this: *"Free-text label values,
per-incident ids, or pod names in labels will blow up cardinality — keep them on spans."*

**Why it is still open.** Never noticed, because groundings are rejected rarely and the dev
Prometheus has never been under pressure.

**Size.** S — a one-line fix.

### 13. The retry path has never been observed firing in production

**Symptom.** `TransientRetryChatClient` is unit-tested nine ways and the overload it exists for has
not recurred since it was written. That is the difference between "tested" and "proven".

**Fix.** A fault-injecting `IChatClient` behind a dev-only flag would settle it once.

**Size.** S.

### 14. `EscalateOnlyInvestigator` does not escalate

**Symptom.** A class whose name and doc comment both promise an escalation, whose body performs
none.

**Evidence.** `Pipeline/IncidentInvestigator.cs:20-34`. The doc comment says *"Escalating is the
honest response: a human is told there is a problem and that nothing diagnosed it"*. The method
logs a warning and returns `Task.CompletedTask` — no state transition, so nothing escalates and
nobody is told. The incident is left exactly as the caller found it.

**Why it is still open.** Latent. It is registered with `TryAdd` and is only reachable if the LLM
stack was never registered, which does not happen in any shipped configuration.

**Size.** S.

---

### 33. Alertmanager signals lose their namespace when the alert labels it `k8s_namespace_name`

**Symptom.** Every incident opened from a metric-derived alert has an empty namespace, so the
incident card tells the model to investigate `Target: `//faulty-service``.

**Evidence.** `src/Hephaisto.Agent/Web/AlertmanagerEndpoints.cs:276`:

```csharp
Namespace = Label(labels, "namespace") ?? Label(labels, "exported_namespace") ?? string.Empty,
```

The shipped OTel spanmetrics rules group by `k8s_namespace_name`, which is neither of those. From
the dev cluster's `signals` row for `ServiceHighErrorRate`:

```
target_namespace | (empty)
labels           | {"service": "faulty-service", "k8s_namespace_name": "hephaisto-chaos", ...}
```

The namespace is right there in the labels and is dropped on the way in. It also reaches the
message text — "in namespace hephaisto-chaos" — so the model can sometimes recover it by reading
prose, which is why this has looked like it works.

**Why it matters more than it looks.** The namespace is not just prose. It is part of the signal
fingerprint, it is what `Policy:AllowedNamespaces` is checked against, and it is what every tool
call needs as an argument. An incident with no namespace is one the policy engine cannot admit and
the model has to guess its way around.

**Why it is still open.** Found on 2026-08-29 while recording the c10 cassette, which is the first
time a metric-derived incident was investigated deliberately rather than incidentally.

**Size.** S for the label fallback; M to add a test over the real rule labels, which is the part
that stops it regressing.

---

## Telemetry drift

### 15. Duplicate instrument registrations with conflicting types and units

**Symptom.** Four metrics are registered twice under the same meter name `"Hephaisto"`, by
`Llm/LlmInstrumentation.cs` and `Telemetry/HephaistoMetrics.cs`, and the two disagree.

| Metric | `HephaistoMetrics` | `LlmInstrumentation` | Consequence |
|---|---|---|---|
| `investigation.steps` | `Histogram<int>` | `Counter<long>` | **Type conflict.** The dashboard queries `_bucket`/`_sum`/`_count`; the counter exports a separate `_total` series. |
| `investigation.duration` | `Histogram<double>` unit `s` | `Histogram<double>` unit `ms` | **Unit conflict.** With `add_metric_suffixes`, one becomes `_milliseconds`; three quantile panels query `_seconds_bucket`. |
| `investigation.terminations` | `Counter<long>` | `Counter<long>` | **Double counted** — recorded at both `InvestigationRunner.cs:188` and `InvestigationCoordinator.cs:149`, same `reason` tag. Two panels read 2×. |
| `grounding.rejected` | `Counter<long>` | `Counter<long>` | Double counted, and see [#12](#12-unbounded-label-cardinality-on-hephaistogroundingrejected). |

`signals.received` / `signals.dropped` are also duplicated, between `HephaistoMetrics` and
`Kubernetes/KubernetesWatcherService.cs`, but harmlessly — same type, same unit.

**Why it is still open.** Both registrations look correct in isolation; the conflict is only
visible in the exported series.

**Size.** M.

### 16. Four declared spans are never started

**Symptom.** `Spans.Incident`, `Spans.PolicyEvaluate`, `Spans.ActionExecute` and
`Spans.Verification` are declared in `Core/Telemetry/HephaistoTelemetry.cs` and drawn in the
self-observability tree in `docs/architecture.md`. Only three of the seven declared spans are ever
started.

**`PolicyEvaluate` is the notable one** — the policy engine *is* built and runs on every
investigation, so that span is a genuine gap rather than a Phase 2 placeholder. The other three
wait on the executor.

**Size.** S for `PolicyEvaluate`.

### 17. `hephaisto.kubernetes.watch_reconnects` bypasses the constants file

**Symptom.** A metric emitted from a raw string literal rather than a shared constant.

**Evidence.** `Kubernetes/KubernetesWatcherService.cs:92` calls
`meter.CreateCounter<long>("hephaisto.kubernetes.watch_reconnects")`. The name is not in
`HephaistoTelemetry.Metrics`, not in the dashboard spec table, not charted and not alerted on —
which is exactly the drift the constants file exists to prevent: *"the names are shared so a
dashboard, an alert rule and the code that emits the metric cannot drift apart."*

**Size.** S.

### 18. Two audit event types are named and never written

**Symptom.** `Core/Domain/Audit.cs` names `mode.changed` and `policy.decided` as examples of what
the audit trail records. Neither is ever written.

Written today: `action.admitted`, `action.refused`, `investigation.failed`,
`investigation.completed`, `incident.escalated`, and feedback.

`mode.changed` is entangled with [#8](#8-nothing-writes-the-database-mode-arm) — nothing changes the
mode in-product, so there is no event to record.

**Size.** S each.

---

## Config that behaves like a comment

### 19. `MaxAutoScaleReplicas` and `MaxAutoScaleStep` have no readers

**Symptom.** Two policy knobs that look like configuration and behave like documentation.

**Evidence.** Declared at `Core/Policy/PolicyOptions.cs:67,69`. `grep -rn` across `src/`, `tests/`,
`charts/` and `infra/` finds **only those two declarations**. `PolicyEngine` handles
`ActionType.ScaleWorkload` and never consults either cap. Every other `PolicyOptions` property has
a live reader.

**Why this one matters more than its size suggests.** It is the exact failure mode this project
already identified and cleaned up once, in Step 0, and wrote a rule against: *"Config that reads
like configuration and behaves like a comment is worse than no docs. Anything added there in future
needs a reader in `src/` in the same commit."* Two survived the sweep.

**Size.** S — either wire them or delete them.

---

## Documentation asserting things that do not exist

### 20. The MVP acceptance test requires Grafana annotations, which are unbuilt

**Symptom.** `docs/verification.md:262-266` states that for each fixture Hephaisto must open one
incident, write a diagnosis, **annotate Grafana**, emit its trace to Tempo, and change nothing.
Grafana annotations are unbuilt — recorded elsewhere as "MVP item 10, deferred".

**So the acceptance test cannot pass as written**, and has presumably been read past rather than
run to completion.

**Fix.** Build the annotations (v0.1.0) or restate the test. Do not silently drop the clause.

**Size.** S to reconcile.

### 21. "The workflows have never run — there is no remote yet" is stale

`5d4217b` is a merged Dependabot PR and `v0.0.1` was published by `release.yml`. The line was true
when written and is now false. Removed in this restructure; recorded here so the correction is
traceable.

---

## Chart and deployment

### 22. The chart's budget values are write-only

`extraEnv` (added 2026-08-29) makes `Llm__Budget__*` settable, which unblocked the e2e harness, but
they are still not first-class values. Someone reading `values.yaml` cannot tell a budget exists.
Worth promoting the four caps once their names have settled. **Size.** S.

### 23. NetworkPolicy enforcement is unproven

The Alertmanager webhook is deliberately unauthenticated and the NetworkPolicy is its entire
authentication. Neither CI nor `scripts/e2e/run.sh` proves it works: kind's default CNI accepts the
objects and does not enforce them. Testing it means `disableDefaultCNI: true` plus Calico — real
install time and real flake risk, so it is a documented `--enforce-netpol` tier rather than part of
the default run. Until then it is verified by reading, on a cluster whose CNI does enforce.
**Size.** L.

### 24. `values-dev.yaml` disables the webhook's only authentication

It sets `networkPolicy.extraIngressCIDRs: ["0.0.0.0/0"]` so kubelet probes work on this node.
Acceptable only because the cluster is single-tenant and reachable from one private network.
Must never be copied into a shared cluster. **Size.** n/a — a documented trade-off, listed so it
stays visible.

### 25. Orphaned `data-postgres-0` PVC on the dev cluster

The chart names its database `hephaisto-postgres`, leaving the old PVC behind. Deliberately left;
the data was dumped and restored first. **Size.** S.

### 26. The `k8s_events` receiver breaks the moment a second node is added

**Symptom.** Latent duplication, one `kubectl` away.

**Evidence.** `infra/observability/otel-collector.values.yaml:253` states it: the collector is a
DaemonSet and `k8s_events` watches cluster-wide, so N nodes means **N copies of every event**. It
works today only because the cluster has exactly one node. The file says the receiver must move to
a single-replica Deployment before a second node exists.

Two related limitations from the same file, both deliberate and worth keeping visible: the chart's
`presets.kubernetesEvents` is a **silent no-op** (the RBAC exists, so it looks configured, and no
events are collected), and scraped stdout logs carry no `trace_id` — only OTLP-shipped logs do.

**Size.** M, and it is a prerequisite for ever scaling this cluster.

---

## Dead or unreachable code

### 27. `AddHephaistoLlmWithoutPersistence` has no call sites

`Llm/LlmServiceCollectionExtensions.cs:78-92`. Its doc comment says it is *"for hosts with no
Postgres — the AppHost smoke run and the eval harness"*. Neither calls it, and the eval harness is
unbuilt. Either the v0.1.0 harness adopts it or it goes. **Size.** S.

### 28. `list_alert_rules` returns empty here, and is worked around in the prompt

`Llm/GrafanaMcpToolProvider.cs:91-102` carries an `AlertRulesCaveat`: the tool returns
Grafana-managed rules only, and this stack's rules are Prometheus-managed, so it comes back empty.
The caveat is injected into the prompt rather than the tool being dropped or fixed. Worth
revisiting — a tool that always returns nothing costs a step every time the model tries it, and
step budget is the binding constraint on accuracy. **Size.** S.

### 29. `CS0618` suppression on `WatcherExt.WatchAsync`

`Kubernetes/KubernetesWatcherService.cs:249-254`. KubernetesClient 19 marks it obsolete and ships
no replacement for the typed operations. Suppressed deliberately; revisit when the client offers a
successor. **Size.** n/a until upstream moves.

---

## Hygiene

### 30. A pre-purge `grafana.db` tarball is still on the development machine

`docs/old-grafana-teardown.md` records that the old Grafana database was copied off its PVC and
audited before teardown, and that the tarball exists only in `~` on that machine. It carries
credentials, and `backup/` was deliberately purged from git history. It should be deleted or moved
to real secret storage rather than left in a home directory indefinitely. **Size.** S.
