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

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-29.** `playwright.config.ts` now emits the JSON reporter, and `ui/run.sh` reads
`.stats` out of it and exits non-zero when `expected == 0` or `skipped != 0` — so the exact report
quoted above (`expected: 0, skipped: 5`) is now a failure rather than a green phase. `run.sh` also
invokes the suite as `bash ui/run.sh` instead of executing it, so a lost exec bit is no longer a
silent skip either.

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

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-29.** `IncidentQueries.SubmitFeedbackAsync` records it after the row commits.
The verdict vocabulary changed on the way in: the instrument emitted `helpful`/`unhelpful`, which
the "Feedback precision" panel — `verdict=~"correct|incorrect|partial"` — matches nothing of, so
simply adding the missing call would have produced a recorded metric that still drew a division by
zero. It now emits `correct`/`incorrect`/`partial`/`unclear` plus `kind` and `false_positive`.

### 4. `hephaisto.incidents.closed` and `hephaisto.incident.duration` are never recorded

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** MTTR is undrawn. Three dashboard panels are permanently empty, plus the "closed"
series on a fourth.

**Evidence.** Instruments at `Telemetry/HephaistoMetrics.cs:45,50`, recorders at `:72,78`, zero
call sites for either. Their twin `DetectionLatency` (MTTD) *is* called, at
`Pipeline/IncidentTriage.cs:109`, one line after `IncidentOpened`.

**Why it is still open.** Both fire on incident *closure*, and until [#11](#11-there-is-no-production-path-to-resolved)
is fixed there is barely any closure to record. They are entangled.

**Size.** S, after #11.

**Fixed 2026-08-29.** Both fire from `RecordOutcome` in `IncidentTriage` and
`InvestigationCoordinator`, on every terminal transition **including `Escalated`** — which is what
makes them measurable without [#11](#11-there-is-no-production-path-to-resolved). Escalated is the
dominant outcome in Observe mode, so scoring MTTR only on `Resolved` would have left the histogram
as empty as it was. Both carry `kind` and `outcome`, which the MTTR panels filter on.

### 5. `hephaisto.incidents.open` and `hephaisto.budget.remaining` have no instrument at all

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-29.** `hephaisto.incidents.open` is an UpDownCounter incremented in
`IncidentOpened` and decremented in `IncidentClosed` — but only for states that actually leave
`HephaistoDbContext.OpenStates`, which does **not** include `Escalated`, so it agrees with
`/api/status.openIncidents`. The dashboard's spec table said otherwise and was corrected.
`hephaisto.budget.remaining` joins `BudgetGaugePublisher`, derived from the utilizations it already
polls, USD only.

The guard test is `tests/Hephaisto.Tests/Pipeline/IncidentMetricsTests.cs`, and it asserts *is
recorded*: it drives the real `IncidentTriage` and listens on the real meter through a
`MeterListener`. Verified by deleting a call site and watching it go red.

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

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-29.** `HighErrorRate`, which is what `slo-rules.yaml` actually attaches. Found
while widening the e2e fixture set to all eight — and the same pass turned up something worse in
the same file: nothing anywhere in `scripts/e2e/` built or `kind load`ed
`hephaisto/faulty-service:dev`, despite a comment saying c10 needs exactly that. c10 would have
come up `ImagePullBackOff` and opened a real incident of the wrong kind, grading the agent on the
test rig. `chaos_build_images` now does it.

### 6. Audit immutability is not enforced in the deployed database

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-29**, and the first attempt (`v0.1.0-rc2`) shipped a chart that could not install.
Repointing the registered `DbContext` at the serving role also repointed **migrations**, which run
through it — so a fresh database tried to authenticate as a role that nothing had created yet and
the agent never started. Every local database already had the role, so only the e2e's throwaway
cluster could surface it. The startup path is now a single ordered `PrepareDatabaseAsync`, and
`AuditRoleBootstrapTests` fails without it.

The agent now serves on a **separate, non-owner role**. Privileges cannot
restrain a table's owner — it can always grant itself back — so this was never fixable while the
agent connected as `hephaisto`. `ConnectionStrings:hephaisto` stays the owner and is what migrates;
`ConnectionStrings:hephaisto_app` is what serves. `EnsureAuditImmutabilityAsync` creates the role
and re-applies GRANT/REVOKE **on every boot**, rather than in a migration: the `InitialCreate` block
was wrapped in `IF EXISTS (SELECT 1 FROM pg_roles ...)`, so it no-opped on every database that ever
existed, and a migration also runs once, leaving any later table ungranted.

Absent `POSTGRES_APP_PASSWORD` the agent serves as the owner and logs a warning — an upgrade
degrades rather than wedging. `scripts/bootstrap-secrets.sh` adds the key to an existing Secret.

### 7. The planning prompt claims a verification-and-rollback mechanism that does not exist

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-30, in two halves and in that order.** The text was corrected first, in its
own commit, before any mechanism existed - the instruction this entry carried was "correct the
text now; build the mechanism in v0.2.0", and doing it the other way round would have meant a
window in which the fix justified the lie. `VerificationScheduler` then made it true: T+60s,
T+5m and T+15m, deterministic C# predicates, rollback where an inverse exists and escalation
where one does not.

Two things the original claim got wrong that the mechanism does not. Only the LAST check may
conclude a failure - the three answer different questions rather than retrying one, and a pod
still pulling its image at T+60s is not a fault. And "a failed check triggers a rollback" is
only true where a rollback exists: a pod delete has no inverse, so the recourse there is
escalation, which is also why gate 14 now exempts self-healing types rather than accepting a
fictional rollback spec.

### 8. Nothing writes the database mode arm

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Resolved 2026-08-30 by reclassification**, which this file permits as a way out - and the
reason is worth more than the fix would have been.

The entry asks for a writer for "the one a human flips from the UI". That was investigated and
rejected: the mode is a Helm value, it reaches the pod on the env var and the projected
ConfigMap, and an operator who could raise autonomy from a web form would be a second,
unreviewed source of truth for the most consequential setting in the system. `SetModeAsync`
and `GetModeAsync` are **deleted** rather than wired.

Investigating it turned up something worse than the missing writer. The arm DECLARED the
`agent_mode` row's mode column, `InitialCreate` seeds that column to `Observe`, and the
resolver takes the minimum over every arm that speaks - so `mode: Auto` in the chart resolved
to Observe on every database that had ever been migrated, and the only way to lift it was a
hand-written UPDATE. The arm is now Silent unless the runaway latch is set, and the column is
dropped.

The real complaint underneath this entry - a tripped latch could only be cleared with psql -
is fixed: `POST /api/mode/re-arm` and a button that appears only while latched. Re-arming is
different in kind from setting the mode. It cannot name one and cannot lift the agent above
the ceiling the deployment already grants, so the worst it can do is return the agent to the
state its own configuration says it should be in. It writes `mode.changed`, which is the first
thing ever to write that audit type.

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

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-30**, and the entry understates the problem it belongs to.

The label is read now, as `ClusterFacts.NamespaceLabels` - a separate field from
`TargetLabels`, because it is a NAMESPACE label and merging the two would let a workload label
opt its own namespace in, which is exactly the confusion a second independent confirmation
exists to avoid. Folded into gate 2 rather than renumbered, since the manifests already
describe it as part of that decision.

The wider problem this entry names in passing - `TargetLabels` is passed empty - was the
larger half. `ClusterFacts` was built with the clock, the mode and the quarantine stamp and
nothing else, so gates 3, 7, 8-fractional, 9, 10 and 13's budget downgrade were ALL inert
while passing their unit tests, because the tests supply the facts the caller did not.
`ClusterFactsGatherer` populates the record, and refuses to return a partial one: a null
Workload skips the stability and blast-radius gates rather than failing them, so a read
failure would quietly remove safety checks and the resulting verdict would be
indistinguishable from a considered one.

### 11. There is no production path to `Resolved`

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** An incident can only end `Suppressed` or `Escalated`. Nothing ever closes normally.

**Evidence.** `Core/IncidentStateMachine.cs` implements `AwaitApproval`, `BeginActing`,
`BeginVerifying`, `Resolve`, `Reopen` and `Expire`; grepping for callers finds them **only in
`tests/Hephaisto.Tests/IncidentStateMachineTests.cs`**. The production edges are `Triage`,
`Suppress`, `BeginInvestigation`, `Escalate` and `Reinvestigate`.

**Why it is still open.** Expected in Observe mode — nothing fixes anything, so nothing resolves.
But it also means [#4](#4-hephaistoincidentsclosed-and-hephaistoincidentduration-are-never-recorded)
has almost nothing to measure, and an operator has no way to close an incident a human dealt with.

**Size.** M. **Related:** v0.2.0.

**Fixed 2026-08-30.** A passing verification grants it, and only `hephaisto/verifier` may -
the state machine refuses model identities by construction, and the predicate that decides is
deterministic C# rather than a model marking its own work complete.

`Resolve` is granted once EVERY executed action on the incident has been verified, not on the
first to pass: a plan may carry several, and closing on the first would call an incident
resolved while another action on it was still being judged.

This is also what unblocks the second half of #4. `hephaisto.incident.duration` was recorded
only on escalations, so MTTR measured how long the agent took to give up. It now has closures
to measure.

### 12. Unbounded label cardinality on `hephaisto.grounding.rejected`

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-30** - one line, `rejection.Reason` instead of `rejection`, the correct form
having been three files away in `InvestigationRunner` the whole time.

Fixing it turned up a worse instance of the same class, live and unrecorded.
`hephaisto.policy.decisions` carried the verdict's first reason as a label value, and those
reasons are prose written for a human on the action row: "workload is quarantined until
2026-08-30T12:34:56.789Z", "pod is 45s old, younger than the 120s minimum". Timestamps and ages
in a label are unbounded series, and unlike grounding rejections - which are rare - that
counter fires for every proposed action. The label is gone; the prose lives on the action row,
the audit trail and the `policy.evaluate` span, and a bounded `downgraded` flag replaces it.

What is lost is the per-gate breakdown, and getting it back safely needs a closed reason code
on `PolicyResult` - filed as its own item rather than bodged with string matching.

### 13. The retry path has never been observed firing in production

**Symptom.** `TransientRetryChatClient` is unit-tested nine ways and the overload it exists for has
not recurred since it was written. That is the difference between "tested" and "proven".

**Fix.** A fault-injecting `IChatClient` behind a dev-only flag would settle it once.

**Size.** S.

### 14. `EscalateOnlyInvestigator` does not escalate

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** A class whose name and doc comment both promise an escalation, whose body performs
none.

**Evidence.** `Pipeline/IncidentInvestigator.cs:20-34`. The doc comment says *"Escalating is the
honest response: a human is told there is a problem and that nothing diagnosed it"*. The method
logs a warning and returns `Task.CompletedTask` — no state transition, so nothing escalates and
nobody is told. The incident is left exactly as the caller found it.

**Why it is still open.** Latent. It is registered with `TryAdd` and is only reachable if the LLM
stack was never registered, which does not happen in any shipped configuration.

**Size.** S.

**Fixed 2026-08-30.** It delegates to `IncidentTriage.EscalateAsync` with
`EscalationReason.InvestigationFailed` — chosen over `NoPlanProduced` because no plan was
produced *for want of an investigation*, and the distinction is what tells a reader whether to
look for a bad diagnosis or a missing model.

It stopped being harmless in v0.3.0, which is why it was picked up now rather than left as a
latent S. Escalation is the thing that reaches a person as of this release, so a fallback
investigator that silently does nothing is the exact failure the milestone exists to remove —
and the single configuration that reaches it, an install running with no model at all, is the
one where every incident depends on it.

**Not unit-tested, and that is worth stating rather than implying.** The class is `internal` and
its collaborator is a concrete `IncidentTriage` with six dependencies; the honest test is the
integration one asserting every path to `Escalated` leaves an outbox row, which now exists and
covers the transition this produces. A test that constructed six substitutes to assert one
delegation would be testing the mock.

---

### 33. Alertmanager signals lose their namespace when the alert labels it `k8s_namespace_name`

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** Every incident opened from a metric-derived alert has an empty namespace, so the
incident card tells the model to investigate `Target: `//faulty-service``.

**Reproduced on the e2e, 2026-08-29**, and it cost two release candidates. c10's incident opened
with an empty namespace and a target of `faulty-service`, so the harness - which matched fixtures
by a `c<N>-` name prefix inside the chaos namespace - reported c10 as having opened no incident
across rc3 and rc4 while the incident existed the whole time. The agent was right on both runs.

Worth separating the two halves, because only one is fixable here: the **namespace** is
recoverable, since the label set does carry `k8s_namespace_name`; the **target name** is not,
because the spanmetrics series identifies the workload only as `service: faulty-service`. So
fixing this entry would stop the incident card saying `//faulty-service`, and would still leave a
target that no fixture-name match can find. The harness now maps c10 to its real target instead.

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

**Fixed 2026-08-30, both halves.** `ResolveTarget` falls back through `namespace`,
`exported_namespace` and now `k8s_namespace_name`.

The half worth more is the test. `ShippedAlertRulesTests` now scans every shipped rule file for
any identifier that *looks like* it names a namespace and fails if it is not one of the three the
ingest reads — deliberately broad, because a pattern matching only the known spellings would
assert nothing about the fourth one somebody adds next year. Verified by appending
`sum by (pod_namespace_name)` to `slo-rules.yaml` and watching it go red.

It was promoted from "worth fixing" to blocking by v0.3.0: notification routes filter on
namespace, so an incident that arrives without one is now not merely awkward to investigate but
impossible to route — it matches no namespace-scoped rule and reaches nobody, while the routing
table looks entirely correct. `NotificationRouter` reports that case separately
(`SuppressedByUnknownNamespace`) and the interceptor logs it by incident id, so the two halves
fail loudly rather than quietly.

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

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `Spans.Incident`, `Spans.PolicyEvaluate`, `Spans.ActionExecute` and
`Spans.Verification` are declared in `Core/Telemetry/HephaistoTelemetry.cs` and drawn in the
self-observability tree in `docs/architecture.md`. Only three of the seven declared spans are ever
started.

**`PolicyEvaluate` is the notable one** — the policy engine *is* built and runs on every
investigation, so that span is a genuine gap rather than a Phase 2 placeholder. The other three
wait on the executor.

**Size.** S for `PolicyEvaluate`.

**Fixed 2026-08-30.** `PolicyEvaluate`, `ActionExecute` and `Verification` are all started.

The entry is right that `PolicyEvaluate` was the notable one: the other two were placeholders
for code that did not exist, while the policy engine has run on every investigation since the
MVP and its span was simply never opened. It now carries an event per action with the whole
verdict - which is also where the reasons went when they were taken out of the metric labels,
because a span is the right place for text a human reads once.

`Spans.Incident` is still unstarted and is left that way deliberately: an incident outlives any
one process, so a span covering it would either be a lie about duration or a trace held open
across restarts.

### 17. `hephaisto.kubernetes.watch_reconnects` bypasses the constants file

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** A metric emitted from a raw string literal rather than a shared constant.

**Evidence.** `Kubernetes/KubernetesWatcherService.cs:92` calls
`meter.CreateCounter<long>("hephaisto.kubernetes.watch_reconnects")`. The name is not in
`HephaistoTelemetry.Metrics`, not in the dashboard spec table, not charted and not alerted on —
which is exactly the drift the constants file exists to prevent: *"the names are shared so a
dashboard, an alert rule and the code that emits the metric cannot drift apart."*

**Size.** S.

**Fixed 2026-08-30.** The name is now `HephaistoTelemetry.Metrics.KubernetesWatchReconnects`
and the watcher emits it from there.

The entry calls this the drift the constants file exists to prevent, and understates it slightly:
a metric emitted from a literal is invisible to the dashboard and the alert rules **by
construction**, so it cannot drift back into agreement either. The metric is still uncharted and
unalerted — a reconnect counter is worth a panel, since a watch that reconnects constantly is an
agent that is intermittently blind and nothing else reports that — but it is at least now
nameable from the file the dashboard reads.

### 18. Two audit event types are named and never written

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `Core/Domain/Audit.cs` names `mode.changed` and `policy.decided` as examples of what
the audit trail records. Neither is ever written.

Written today: `action.admitted`, `action.refused`, `investigation.failed`,
`investigation.completed`, `incident.escalated`, and feedback.

`mode.changed` is entangled with [#8](#8-nothing-writes-the-database-mode-arm) — nothing changes the
mode in-product, so there is no event to record.

**Size.** S each.

---

## Config that behaves like a comment

**Half fixed 2026-08-30.** `mode.changed` is written, by the re-arm path - the moment
autonomy comes back is the single most important event in the system to be able to attribute,
and it was the one going unrecorded.

`policy.decided` is still not written, and now has a closer relative that is: `policy.changed`,
written whenever the hot-reloaded `PolicyOptions` moves. That one was the more urgent of the
two - a silent policy change is indistinguishable from an attack - and a per-decision audit row
remains open, since every policy verdict is already persisted on the action row it judged.

### 19. `MaxAutoScaleReplicas` and `MaxAutoScaleStep` have no readers

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Resolved 2026-08-30 by deletion**, which is the half of "either wire them or delete them"
that the code supports.

They were documented as *"Ceiling for an unattended scale-up, and the maximum step size"*, and
**there is no unattended scale-up**. `PolicyEngine` returns *"scaling changes capacity and cost,
so it requires approval"* for `ScaleWorkload`, so it is never allow-eligible and every scale that
happens has a person's name on it. Wiring a cap on unattended scaling would have built a control
for a path that cannot occur — which is worse than the dead config it replaced, because it would
*look* like a safety property while holding nothing up.

Capping a human-approved scale is a different control with a different name, and it needs a
`RequestedReplicas` on `ActionRequest` that does not exist. Filed here rather than built, so the
next person meets the decision instead of the absence.

Deleting them also removed a stray `<summary>` block that had been documenting
`MaintenanceWindows` with these two properties' description.

### 35. `AllowedTools` is documented "in order", and the order is the server's

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** The allowlist reads as though it sets the order the model sees tools in. It does not.

**Evidence.** `Llm/GrafanaMcpToolProvider.cs` documents the option as *"The tools actually exposed
to the model, **in order**. Everything grafana-mcp offers beyond this list is dropped."* The
implementation filters the server's list instead:

```csharp
var allowed = tools
    .Where(t => o.AllowedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
```

`tools` is what grafana-mcp returned, so the surviving order is grafana-mcp's, and rewriting the
allowlist changes membership only. Driving the projection from `AllowedTools` instead — looking
each name up in `tools` — would implement what the comment says, in about the same number of lines.

**Why it matters, and why it is not urgent.** Tool *order* influencing selection is an
**unvalidated hypothesis** — nothing in this repo measures it, and the eval harness now exists to
settle it. Until then the honest fix is the cheap one: make the code match the comment, or change
the comment. A doc-comment describing behaviour that was never written is how the next person
plans an experiment against a lever that does not exist.

**Size.** S.

---

**Fixed 2026-08-30** by making the code match the comment, which is what the entry asked for.
The projection is driven from `AllowedTools` and looks each name up in what the server returned,
rather than filtering the server's list — so the surviving order is the one written in the option.

The entry's reasoning is the reason it was fixed this way rather than by editing the comment:
whether tool order influences selection is an **unvalidated hypothesis**, the eval harness now
exists to settle it, and an experiment cannot be run against a lever that does not exist.

### 36. The environment card never names a datasource uid, because nothing sets them

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `EnvironmentCardOptions.DatasourceUids` is empty everywhere, so the prompt section
that would say *"Datasource uids (pass these, not the names)"* is never rendered — and the model
has to spend a tool call on `list_datasources` before it can query anything.

**Evidence.** The dictionary defaults to empty in `Investigation/EnvironmentCardOptions.cs`, and
`grep -rn DatasourceUids src/ charts/` finds only the declaration and the two lines in
`PromptComposer` that read it. No chart value, no ConfigMap key, no `extraEnv` entry. The deployed
Deployment carries no `Investigation__Environment__*` variable at all, so the whole environment
card runs on its compiled-in defaults.

**Why it is worth fixing.** It is the cheapest possible reduction in steps: a fact the agent cannot
look up is exactly what the environment card is *for*, and supplying it removes a discovery call
from every investigation that touches Grafana. Measured baseline for comparison: 7.5 steps and
$0.080 per investigation.

**Why it is still open.** Not noticed, because an empty dictionary renders as an absent section
rather than an empty one — the prompt looks well-formed either way. It surfaced while planning the
discovery-cap experiment, whose write-up asserted "the UIDs are already in the environment card".
They are not.

**Size.** S to populate from the chart; the uids are stable per cluster.

---

**Fixed 2026-08-30.** `grafanaMcp.datasourceUids` is a chart value rendering
`Investigation__Environment__DatasourceUids__<name>`, with the `curl` that finds the uids in the
comment beside it. It ships **empty**, which renders no section at all rather than an empty one —
so an operator who does not set it pays one discovery call and nothing else, which is exactly the
behaviour that let this go unnoticed.

Worth restating what it buys: at a measured baseline of 7.5 steps and $0.080 per investigation,
removing a `list_datasources` call from every investigation that touches Grafana is the cheapest
step reduction available, and a fact the agent cannot look up is precisely what the environment
card is for.

## Documentation asserting things that do not exist

### 37. The judge grades a different incident than the one the run asserted on

**Symptom.** On the eight-fixture run the harness asserted `8 incident(s) have a primary
finding`, and the judge then skipped c5, c8 and c10 with *"no primary finding"*. Both statements
cannot be true of the same incidents.

**Evidence.** `scripts/e2e/lib/judge.sh` resolves a fixture to an incident independently of
`chaos.sh`, and the run collected detail for **17** incidents against 8 fixtures - a freshly built
cluster opens its own (ReadinessFlapping on Grafana, Unschedulable on loki-0), and one fixture
routinely opens two. Picking a different row than the one that was graded is the obvious way to
get "no primary finding" for an incident that has one. c10 is the exception and is honest: it
opened no incident at all on that run.

**Why it is still open.** It costs grading coverage rather than correctness - 5 of 5 graded were
correct, but three gradeable fixtures went ungraded, so the denominator is quietly smaller than
the corpus. That is the same class of dishonesty the eval harness was built to remove.

**Fix.** Resolve fixture to incident once, in `chaos.sh`, and have the judge grade the incident
the detection assertion already matched.

**Size.** S.

### 38. `approval_source` reads `Ui` on actions nobody approved

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** Two `PatchResources` actions the policy engine **Denied** carry
`approval_source = Ui`, which reads as "a human typed a name into the console" for actions no
human ever saw. `approved_by` on the same rows is correctly null.

**Evidence.** `ApprovalSource` is a non-nullable enum whose zero value is `Ui`
(`Core/Domain/Enums.cs:186`), and `ActionPlan.ApprovalSource` is never set on the denial path -
so the default is written verbatim. Found on the first eight-fixture e2e run; the four-fixture
default set never produced an action proposal.

**Why it is still open.** Not a safety issue: nothing executed, and `approved_by` - the field the
audit trail actually rests on - is honest. It is misleading data on a screen, and the audit
trail is exactly the place where misleading beats absent by the smallest margin.

**Fix.** Make it nullable, or add a `NotApplicable = 0` member and shift `Ui`. Both are schema
changes, which is why this is written down rather than done inside the v0.1.0 fix pass.

**Size.** S for the enum, M with the migration and the UI that reads it.

**Fixed 2026-08-30.** `ApprovalSource.NotApplicable = 0` takes the zero value, `Ui` and the
rest shift up, and the `ActingSchema` migration moves the existing rows onto it - scoped by
`approved_by IS NULL` rather than by state, so an action a human really did approve keeps
saying `Ui` even if it has since failed.

Safe to renumber because the column is `text` and enums are stored by name, so no stored
value's meaning shifts underneath the rows. The entry sized this as "S for the enum, M with the
migration", which was right; it landed with the approval workflow because that is the commit
that made the field mean something.

### 20. The MVP acceptance test requires Grafana annotations, which are unbuilt

**Status: fixed 2026-08-29** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `docs/verification.md:262-266` states that for each fixture Hephaisto must open one
incident, write a diagnosis, **annotate Grafana**, emit its trace to Tempo, and change nothing.
Grafana annotations are unbuilt — recorded elsewhere as "MVP item 10, deferred".

**So the acceptance test cannot pass as written**, and has presumably been read past rather than
run to completion.

**Fix.** Build the annotations (v0.1.0) or restate the test. Do not silently drop the clause.

**Size.** S to reconcile.

**Fixed 2026-08-29.** Built, rather than restated. `GrafanaAnnotator` posts to Grafana's
`/api/annotations` on open and on outcome, the second as a region spanning the incident with the
primary hypothesis in the text, tagged `hephaisto` plus kind, severity and namespace. It is wired
only when `Grafana:Url` and `Grafana:AnnotationToken` are both set, and cannot fail an
investigation — every transport and status failure is logged and swallowed, with the deliberate
exception of the incident's own cancellation.

The token is a **separate Editor service account**, not grafana-mcp's Admin one: this is the only
Grafana credential in the system that may write. `chaos_assert_annotations` checks them in the e2e
using that same token, so `docs/verification.md`'s clause is now asserted rather than assumed.

### 43. `GrafanaAnnotator.Describe` is documented as a startup line and has no caller

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** An operator who has not configured Grafana annotations is told nothing about it,
by a mechanism whose own documentation says they are.

**Evidence.** `Observability/GrafanaAnnotator.cs:42-46`, on `NullGrafanaAnnotator`, verbatim:

> The absence is reported once at startup by `GrafanaAnnotator.Describe` rather than per
> incident: a warning on every transition would train people to ignore the log on exactly the
> installs that have chosen not to wire Grafana up.

The reasoning is right and the method exists — `:109-121`, returning one of three sentences
naming the missing key. `grep -rn "GrafanaAnnotator.Describe" src/ tests/` returns **nothing but
that doc comment**. Nothing has ever called it.

**Why it is still open.** It fails in the direction that looks fine, and it is invisible from
both sides: the method is written, documented and correct, and the `Null*` fallback works
exactly as intended. The only observable difference is a log line nobody knew to miss.

**Why it matters more than a missing log line.** Every outbound integration in this codebase
degrades silently when unconfigured, which is the right behaviour per call and a bad one
overall — the failure mode of the whole feature is that nothing happens, and "nothing happened"
looks identical whether it was never switched on or is broken. That is precisely the confusion
v0.3.0 exists to remove, so shipping notifications on top of it would have been building the
same trap one storey higher.

**Size.** S.

**Fixed 2026-08-30.** `OutboundStartupReport` is the caller, and it covers the notification
channels too: `INotificationChannel.Describe()` is part of the interface rather than a
convention, so a channel cannot be added without answering "what does this say at startup".
It also reports when no routes are configured — at `Information`, not `Warning`, because
shipping unable to notify is the deliberate default and warning about it every start would
train people to ignore the line on exactly the installs that chose it — and at `Error` when a
route names a channel that is not registered.

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

**Resolved 2026-08-29 by adoption.** `Hephaisto.Eval`'s `EvalHost.BuildForReplay` calls it — replay
needs the model and nothing else, so a cassette can be scored on a machine with no Postgres at all.
Left in the file rather than deleted because "it goes" was the other half of the choice and the
record of which way it went is the useful part.

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

---

## Opened by v0.2.0

Written down at the moment they were deferred, rather than discovered later by somebody
reading the code and wondering.

### 39. The executor covers five action types; three are refused

`ActionExecutor.CanPerform` implements `RestartPod`, `RolloutRestart`, `ScaleWorkload`,
`DeleteStuckJob` and `DeleteFailedJobPods` - the verbs the write `Role` actually grants.
Everything else fails closed with `outcome=unsupported` and nothing attempted.

For `CordonNode` and `DrainNode` that is the correct permanent answer until someone binds
their `ClusterRole`, which ships unbound on purpose. `SilenceAlert` needs an outbound client
bound to Alertmanager; it belongs with v0.3.0's notification stack.

**Corrected 2026-08-30.** This entry read *"and there is none anywhere in `src/`"*, which was
already wrong when it was written: `GrafanaAnnotator` has posted to Grafana through a client
registered with `AddHttpClient` since the annotations landed in `v0.1.0-rc2`. What `SilenceAlert`
is missing is the Alertmanager binding and the policy gate around it, not the ability to make a
request. The same false claim was in `roadmap.md` and `README.md` and is corrected in all three.

The two that matter are **`PatchResources` and `RollbackDeployment`**. `PatchResources` is the
actual remediation for c4 (a bad image tag) and c7 (a missing secret ref) - the two fixtures
where the agent diagnoses correctly and then has nothing useful to propose. Both always
require approval, so an operator can approve one today and watch it fail at execution, which
is a poor experience even if it is a safe one.

`PatchResources` needs care rather than effort: applying a model-authored JSON patch verbatim
would hand the model the mutating handle the three-phase split exists to deny it. It wants a
restricted, typed vocabulary - container image and resource limits - rather than an arbitrary
merge patch. **Size.** M each.

### 40. `PolicyResult` has no closed reason code, so the metric cannot say why

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

`hephaisto.policy.decisions` used to label on the verdict's first reason and now labels on
`decision`, `action_type` and `downgraded` - see [#12](#12-unbounded-label-cardinality-on-hephaistogroundingrejected)
for why the prose had to go.

What went with it is the per-gate breakdown: "how often does the cooldown bite versus the
namespace allowlist" is a genuinely useful question for tuning, and it is now unanswerable from
metrics. Getting it back means each of the ~20 `denials.Add` / `downgrades.Add` sites in
`PolicyEngine` carrying a closed `PolicyReasonCode` beside its human text. Deriving the code
from the string instead would be brittle in the one place brittleness is least acceptable.

A change to every gate in the safety argument, so it wants its own commit and its own pass over
`PolicyEngineTests`. **Size.** M.

**Fixed 2026-08-30**, in its own commit and with its own pass over `PolicyEngineTests`, as the
entry asked.

`PolicyReasonCode` has 23 members — eighteen denials in the order the gates run, five downgrades
— and `PolicyResult` carries a `Codes` list alongside `Reasons` plus a `PrimaryCode` for the
label. The gates run cheapest-and-most-certain first, so the first code is both the most specific
answer and the one a human would give.

**Carried beside the human text at each site, never derived from it**, exactly as this entry
insisted. The sites use a local `Deny(code, reason)` / `Downgrade(code, reason)` helper, so a
sentence and its code cannot be added apart.

The invariant that keeps it true is a test rather than a convention: a denial must have exactly
one code per reason, none of them `None`, and all distinct. A future bare `denials.Add(...)`
produces a count mismatch there instead of a metric that quietly attributes the denial to
whichever gate happened to fire beside it.

`ActionMetricsTests.A_policy_decision_carries_no_free_text` was the test asserting `reason` was
absent, and it is updated rather than deleted: the label is back, and the assertion is now that
its value is a `PolicyReasonCode` member. The dashboard's metric-contract panel is corrected too
— it had been claiming `decision`, `reason`, `action_type`, `risk`, and the code emits
`decision`, `action_type`, `downgraded`, `reason`, so the spec had drifted from the emitter in
both directions at once.

### 41. c11 has never been run against a cluster

`infra/chaos/c11-transient.yaml` is the fixture v0.2.0's acceptance test is written against,
and it has been verified only by simulating its container logic locally - the generation
counter on a PVC, the marker on an emptyDir, and the assertion that container restarts do not
advance the generation while pod replacement does.

What is unverified is everything Kubernetes contributes: that `local-path` binds the claim
under `WaitForFirstConsumer` in time, that `strategy: Recreate` releases the ReadWriteOnce
volume before the replacement pod wants it, and that the first pod reaches
`CrashLoopBackOff` rather than some other waiting reason. Any of those would make the fixture
wrong in a way the acceptance test would report as an agent failure.

**Run once on 2026-08-30, and most of this entry is answered.** c11 applied cleanly, the PVC
bound under `WaitForFirstConsumer`, `Recreate` did not deadlock on the ReadWriteOnce volume,
the first pod wedged as designed, and the shipped rule classified it `CrashLoopBackOff` - so
the fixture is sound and gets the right runbook.

**What it also showed is that the fixture was lying about recovery.** With no readiness probe
a container is Ready the instant it is Running, and this one runs for two seconds before
exiting - so the Deployment reported `availableReplicas: 1` for part of every crash cycle, and
both the harness and the agent's own verification predicate read that as recovered.
`minReadySeconds: 20` and a stability check in `VerificationChecks` close it.

**What remains open** is the second half: the acting path has never completed. The first run
was stopped by gate 9 refusing the restart, and while that is fixed, nothing has yet observed
an execution, a passing verification and a `Resolved` incident in sequence. Deferred to before
v0.4.0 by decision.

**Run against a cluster on 2026-08-30 in `--mode Auto`, and the acting path still did not
complete — for a third, different reason.** The record now reads: the first attempt was stopped
by gate 9 refusing to restart "the last Ready replica"; that was fixed; this attempt got past
policy entirely and stopped one step earlier.

```
state:            Escalated
escalationReason: NoPlanProduced
investigation:    terminationReason=Concluded, steps=22, findings=1, actions=0
```

**The agent diagnosed c11 correctly and then proposed nothing.** Its primary finding, verbatim:

> The pod entrypoint script detects a stale state lock in /data/generation (value 1 < 2) on its
> mounted persistent volume and immediately aborts with exit code 1, causing
> Deployment/c11-transient to enter CrashLoopBackOff.

That is exactly right. The policy engine never saw an action, so nothing was denied — this is a
planning gap, not a safety gate.

**And the reason is arguably good reasoning about a fixture designed to defeat it.** The
diagnosis correctly identifies the cause as *persistent state on a volume*, and the general rule
"restarting does not fix state that survives restarts" is one you would want an SRE agent to
hold. c11 is the specific case where it is wrong: pod *replacement* advances the generation
counter, so a restart is the fix. Nothing in the cluster says so — the agent would have to infer
it from the entrypoint's write of the next value.

So this is no longer "the acting path is unverified". It is: **the one fixture in the corpus that
a restart fixes is one the agent reasons its way out of restarting.** Which of the two is wrong -
the fixture, for being counter-intuitive, or the planner, for not reading the entrypoint's write
- is the open question, and it is worth answering before widening autonomy rather than after.

**Size.** S to run; M now that there is something to fix. **Blocks:** claiming the v0.2.0
acceptance criterion is met.

### 42. Verification predicates are workload-shaped, and two action types are not

`VerificationChecks` answers "is the owning workload settled and Ready" for everything except
the Job actions. That is right for `RestartPod`, `RolloutRestart` and `ScaleWorkload`, and it
is the honest general answer - the object an action named is often gone by the time the check
runs, which is the normal outcome of a pod delete.

It is thin for `ScaleWorkload`, where the interesting question is whether the replica count is
what was asked for rather than merely whether the workload is happy, and it has nothing
specific for a future `PatchResources`, where it should assert the patched field actually
changed. Neither is wrong today; both would be better. **Size.** S each.

---

## Opened by v0.3.0

Written down at the moment they were deferred, rather than discovered later by somebody
reading the code and wondering.

### 44. Nothing sweeps `AwaitingApproval`, so `ApprovalTimedOut` has no producer

**Symptom.** `EscalationReason.ApprovalTimedOut` is a defined member of the enum and nothing in
`src/` ever sets it. An incident that reaches `AwaitingApproval` and is never approved stays
there indefinitely.

**Evidence.** `grep -rn ApprovalTimedOut src/` finds the enum declaration and nothing else. There
is no timer, no sweeper and no `BackgroundService` that looks at `AwaitingApproval` — the only
transitions out of it are `BeginActing` (a human approved) and `Escalate` (a human denied).

**Why it matters more after v0.3.0 than before it.** Until this release, an incident sitting in
`AwaitingApproval` was visible in the console and nowhere else, which made it one of several
things a person had to remember to look at. Now a card goes out saying *"approval required"* with
a link — and if nobody clicks it, nothing happens and nothing says so again. That is *"escalated,
and nobody was told"* in slow motion, which is the exact failure the whole milestone was built to
remove, wearing a longer timescale.

**Why it is still open.** Building it means deciding what a timeout *does*, and every answer is a
policy question rather than an implementation. Re-notify — how often, and does that become the
storm the outbound rate limit exists to prevent? Escalate — that is a state transition, so it
needs a reason code, an audit row and a rule about whether a timed-out approval may still be
approved afterwards. Auto-deny — absolutely not, but somebody will propose it.

**Fix.** A sweeper with a configured window, most likely re-notifying once and then escalating
with `ApprovalTimedOut`, which is what the enum member was reserved for. It wants its own
decision rather than being appended to a release that was already about delivery.

**Size.** M.

### 45. Nothing has been delivered from a cluster

**Status: answered 2026-08-30** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** Every claim v0.3.0 makes is supported by unit and integration tests. None of it has
been observed leaving a running agent.

**Evidence.** 989 unit tests and 53 integration tests pass, including the transactional
guarantee — an incident cannot reach a notifiable state without an outbox row, asserted against a
real Postgres over all thirteen escalation reasons, and verified falsifiable by commenting out
the enqueue and watching 15 tests go red. The `notify` e2e phase is written, wired into `PHASES`,
and **has never been executed**: it needs a kind cluster and a Gemini key.

**What is unverified** is everything the environment contributes: that the chart's env-var shape
binds as `PolicyOptionsBindingTests`'s sibling says it does *in a pod*, that the dispatcher's
poll behaves under a real connection, that the agent can reach an endpoint outside its namespace,
and — the one that matters — that a queued delivery survives an actual process death rather than
a rolled-back transaction.

**Why it is still open.** Deliberate, and it is the same debt v0.2.0 ended on. The two now
compound and are **one run**: `scripts/e2e/run.sh --mode Auto` exercises the executor that
[#41](#41-c11-has-never-been-run-against-a-cluster) is waiting on, and every notification this
release built fires on the outcomes that run produces. Doing them separately would mean setting
up the same cluster twice.

**Size.** S to run; unknown to fix whatever it finds. The v0.2.0 precedent is that running it
once found three bugs.

**Blocks:** claiming the v0.3.0 acceptance criterion is met.

**Answered 2026-08-30, over three cluster runs.** The delivery path passed every assertion on two
independent runs against a real kind cluster:

```
pass  a notification reaches the receiver
pass  deliveries carry a stable delivery id
pass  deliveries carry a link back to the incident
pass  the delivered incident exists in the API
pass  a delivery survives an agent restart
```

The last is the one that could not have been a unit test: the receiver was taken to 503, an
escalation queued against it, the agent pod restarted **mid-flight**, the receiver brought back,
and the delivery arrived. An outbox that has never survived a restart is an outbox in name only,
and this one has.

**What it cost to find out** is the part worth keeping. The first run tested nothing — the
receiver image could not build, because `.dockerignore` excludes `infra/` wholesale and the
negation for the new directory was missing, in a file whose existing comment describes that exact
failure. The second and third runs then failed the two startup-line assertions while the agent
was emitting those lines perfectly, twice, for two different wrong reasons — see #46's sibling
note in `notify.sh`. Three runs to test one thing, and only one of the three failures was in the
product.

**What is still not delivered from a cluster:** the Teams channel, which needs a tenant the
harness does not have, and a signed delivery, which needs a Secret the chart deliberately will
not create. Both are covered by unit tests and neither is on the critical path.

### 46. The console suite cannot pass in Observe, so a green run needs `--mode Auto`

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `scripts/e2e/run.sh` in its default mode always fails the `ui` phase, on every run,
regardless of the code under test.

**Evidence.** `ui/run.sh` exits non-zero when `skipped != 0` — correctly, and deliberately, as the
fix for [#1](#1-the-e2e-playwright-phase-reports-pass-on-a-zero-assertion-run). But
`acting.spec.ts:79` (*"approve is disabled until someone says who they are"*) calls
`test.skip(target === null)` when no action is in `AwaitingApproval`, and in Observe mode **no
action ever can be**: the kill-switch gate denies every action before the risk routing that would
produce an approval. So the spec skips, and the phase fails.

Observed on 2026-08-30 in both modes. In Observe: 8 passed, 1 skipped, 0 unexpected — a failing
phase in which nothing was actually wrong.

**Why it is still open.** It was masked. Before v0.3.0 the last recorded green run was
`v0.1.0-rc6`, from before the acting specs existed, and every run since has had a louder failure
in front of it.

**Fix.** Either seed an `AwaitingApproval` action the suite can rely on regardless of mode, or let
that one spec be conditional in a way the phase does not count as a skip. **Do not** relax the
`skipped != 0` rule - that rule is #1's whole fix, and it is worth more than this spec.

**Size.** S. **Blocks:** `run.sh` ever exiting 0 in its default mode.

**Fixed 2026-08-30.** Neither of the two options in the paragraph above, in the end. Seeding an
`AwaitingApproval` action turned out to have nothing to build on - there is no seeding path in
either mode, because `--mode Auto` auto-enables only `RestartPod`, which the autonomy gate routes
straight to `Approved`; any approval at all depends on the model proposing some other action type,
which is not something a gate can rely on. And making the spec "conditional in a way the phase does
not count as a skip" is the same hole as #1 wearing different clothes.

So the spec asserts the contract in both directions instead, and the API decides which branch it
takes rather than the mode. Where an approval is offered, it must require a name before it will
act. Where none is offered, the console must be showing nobody a button to authorise something the
policy engine already refused - which in Observe, where every action is denied at the kill-switch
gate, is the more valuable of the two assertions. It is anchored on a rendered action row, so it
cannot pass by finding an empty page, and it was verified falsifiable: asserting one approve
control instead of zero turns it red.

Two further skips went with it, for consistency rather than because they were failing. Both
`acting.spec.ts`'s plan precondition and `console.spec.ts`'s diagnosis precondition now fail with
a sentence naming what was missing, instead of opting out and taking the phase down silently -
the same condition must not make one spec skip while another fails. **There is no `test.skip` left
in the suite.**

Fixing this exposed the failure behind it, which was not this one and was not a product bug
either: see [#48](#48-the-console-suite-interacts-with-a-page-the-circuit-has-not-taken-over-yet).

### 47. The act phase reports two failures that are consequences of the first

**Symptom.** When nothing is acted on, the run reports three failures, and two of them describe
something that did not happen:

```
FAIL  c11 was not acted on -- expected at least one executed, non-dry-run action
FAIL  c11 is still not available -- the action ran but the workload did not recover
FAIL  c11's incident did not reach Resolved -- the workload recovered but verification never closed it
```

The second says *"the action ran"* and the third says *"the workload recovered"*. Neither is true:
no action ran and nothing recovered. Both are the first failure, restated as its downstream
symptoms with confident and incorrect explanations attached.

**Why it matters more than tidiness.** It costs eight minutes of wall clock burning two 240s
timeouts, and it sends the reader looking for a broken restart and a broken verifier when the
actual finding is that the planner proposed nothing. A report that is wrong about *why* is worse
than one that is merely incomplete.

**Fix.** Short-circuit: if nothing executed, skip the recovery and Resolved assertions with a
reason naming the first failure rather than asserting and failing them.

**Size.** S.



---

## Opened by v0.4.0

### 48. The console suite interacts with a page the circuit has not taken over yet

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `acting.spec.ts:79` (*"approve is disabled until someone says who they are"*) failed
on every run that got far enough to execute it, with the approve button never enabling:

```
expect(locator).toBeEnabled() failed
Locator:  getByTestId('approve')
Expected: enabled   Received: disabled
44 × locator resolved to <button disabled ... data-testid="approve">approve</button>
```

Read at face value this says the approval control in the console is broken — which would have
been serious, because v0.3.0's entire approval story is a Teams card that deep-links to that
button, and [#45](#45-nothing-has-been-delivered-from-a-cluster)'s acceptance clause reads
*"an approval-required incident is approved from the browser via the link in that card"*.

**It is not a product bug.** The control works. The suite was interacting with a page that was
not interactive yet.

**Evidence.** Driven against a live console on the development cluster, with the circuit's
websocket frames captured:

```
h1 visible at            52 ms      <- what open() waited for
_blazor websocket open   55 ms
first RenderBatch       102 ms
a click first lands     629 ms      <- what open() needed to wait for
```

This is a Blazor **Web App** with `<Routes @rendermode="InteractiveServer" />`, so every
component renders twice: once as static server-rendered HTML delivered with the document, and
then again over the SignalR circuit, which replaces that DOM wholesale. Between those two moments
the page looks completely finished and is completely inert — the elements are present, visible
and correctly worded, and any event dispatched into them is dropped, because the handlers belong
to a render that has not happened yet.

Once past the takeover, the same interaction is reliable. Filling the name and watching the wire
shows the event arriving and the server re-rendering:

```
OUT  DispatchEventAsync [{"eventHandlerId":19,"eventName":"input","fieldValue":"x"}]
IN   JS.RenderBatch
```

**Why it stayed invisible.** Reading static HTML is indistinguishable from reading the
interactive DOM, so all 34 read-only assertions in this suite passed either way. Only a spec that
*interacts* could ever have noticed, and there is exactly one — which is also the spec
[#46](#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto) causes to be
skipped in the default mode. In Observe it skipped and was never reached; in Auto it ran and was
read as a product defect. The helper's own comment asserted the opposite of the truth — *"The
nav is server-rendered, so it is not proof of anything. The h1 is rendered by the component
itself"* — which was correct for Blazor Server as it behaved before .NET 8, and stopped being
correct without anybody editing the sentence.

**Fixed 2026-08-30.** `helpers.ts`'s `open()` now waits for the circuit's first `RenderBatch`
frame before asserting anything, so every element a spec goes on to find belongs to the
interactive tree. Waiting for the websocket to *open* is not sufficient — it opens at ~55ms,
still before the takeover.

Verified falsifiable rather than assumed: the same interaction, run five times with no sleeps
anywhere, fails 5/5 against the shipped helper and passes 5/5 against the fixed one.

### 49. The console spec compares a capped API call against an uncapped page

**Symptom.** `console.spec.ts:11` asserts `incident-row` count equals the length of
`/api/incidents?limit=100`. On any install with more than 100 open incidents that is a guaranteed
failure — observed on the development cluster as `Expected: 100, Received: 103`.

**Evidence.** The spec caps its own API call at 100 and compares the result to a page that
applies no such cap.

**Why it is still open.** An e2e run has a handful of incidents, so the gate never trips there.
It trips on any long-lived install, which is where a human is most likely to run the suite by
hand and least likely to trust the result afterwards.

**Fix.** Either cap both sides or compare the page against an uncapped count. Capping both is
the smaller change and keeps the assertion meaningful.

**Size.** S.

### 50. Both themes are first-class, and neither can be chosen

**Symptom.** Light mode stopped being "a courtesy, not the design target" in v0.4.0 — it is
contrast-asserted and photographed like the dark theme. A reader still cannot select it. Theme
follows the operating system through `prefers-color-scheme` and there is no control anywhere.

**Evidence.** `tokens.css` keys light mode entirely off `@media (prefers-color-scheme: light)`.
There is no `data-theme` attribute in the repository, no toggle, and no persisted preference.

**Why it matters more after v0.4.0 than before it.** While light was explicitly not the design
target, "your OS decides" was a coherent position. Now that both themes are held to the same bar,
an operator on a dark-mode laptop presenting the console on a projector in a bright room has no
way to ask for the theme the project says it supports.

**Why it is still open.** It is outside the milestone's stated "done when", and smuggling it in
would have been scope this release did not agree to.

**Fix.** A `data-theme` attribute on the root, three states (system / light / dark), persisted in
`localStorage`. The interop already exists and is proven — `wwwroot/app.js` uses it to remember
the feedback submitter's name — and `tokens.css` would need its light block duplicated under a
`[data-theme="light"]` selector.

**Size.** S.

### 51. `run.sh` has not been re-run on a kind cluster since the suite was fixed

**Symptom.** v0.4.0 closed [#46](#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto)
and removed every `test.skip` from the console suite, and the milestone's exit criterion is that
`scripts/e2e/run.sh` exits 0 in its default mode. That has not been observed.

**Evidence.** Every spec was verified against a live console on the development cluster: 9 specs,
0 skipped. But `run.sh` in Observe boots its own kind cluster, applies chaos fixtures and runs
nine phases before the `ui` one, and that has not been run since the fixes landed.

**Why it is still open.** The claim is currently about the specs rather than about the harness,
and this repo has been caught by exactly that gap before — three of the four v0.1.0 release
candidates failed on the harness rather than on the thing being measured.

Two known failures are also waiting there and are *not* regressions from this milestone:
[#49](#49-the-console-spec-compares-a-capped-api-call-against-an-uncapped-page) trips on any
install with more than 100 incidents, and the budget-meter spec asserts non-zero spend, which is
only true once the agent has actually investigated something in the current hour.

**Fix.** Run it. `scripts/e2e/run.sh` with no arguments.

**Size.** S to run, unknown to fix whatever it finds.

### 52. Two components are implemented twice

**Symptom.** The console has two unrelated implementations of a progress bar and two
near-duplicate treatments of a monospace block.

**Evidence.** `hp-meter` / `hp-meter-track` / `hp-meter-fill` (the budget meters on `/status`) and
`hp-conf` / `hp-conf-track` / `hp-conf-fill` (the confidence bar on a finding) share no tokens and
no rules. `hp-code` and `hp-excerpt` differ only in padding and border.

**Why it is still open.** Consolidating them changes the rendering of both, which is a visual
change rather than a refactor, and v0.4.0 had already made one deliberate visual change. Doing
both in the same release would have made the baseline diff unattributable — which is the property
the whole ordering of that milestone was built to preserve.

**Why it is worth doing.** Two implementations of one idea drift, and a design language exists to
stop exactly that. Both are now photographed by the visual baselines, so the consolidation is a
change somebody can actually verify.

**Size.** S.
