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

**Status: fixed 2026-09-01** — see the end of this entry.

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

**Mechanism landed 2026-08-31; the entry closes on the first green run.** `--full` runs
`c1,c2,c3,c4,c5,c7,c8,c10,c11,c12` — ten, which is the bar's own denominator — and the report now
states whether the bar was met instead of only printing the ratio, because `7/9` fails on the
count while reading as a pass on the proportion. The incident deadline was raised to clear c8's
thirty-minute window, since timing out a fixture that is exactly on schedule would have replaced
under-coverage with a false failure.

Note what this does **not** claim: c6 and c9 still cannot run, so the coverage is ten of twelve
and not twelve of twelve. The bar was always written as ten. This reaches it with the fixtures
that work rather than by replacing either of the two that do not, which is the cheaper half of
the fix above and leaves the replacement-fixture half open if the count ever needs to be twelve.

**Fixed 2026-09-01.** `scripts/e2e/run.sh --tag 0.5.0-rc5 --full --mode Auto` ran
`c1,c2,c3,c4,c5,c7,c8,c10,c11,c12` against a published artifact and exited **0**: 77 assertions,
0 failed, 8 skipped, 98 minutes, $0.115 on a local `gpt-oss-120b`.

The bar was reached rather than approximated. `root cause 8/10 correct — MVP bar met (>= 7/10
over >= 10 scenarios)`: ten scenarios scored, which is the denominator this entry was opened
about, and the first time in the project's history that the bar has been **evaluable at all**.

What made the difference was not coverage but [#78](#78). Every investigation truncated by the
step ceiling used to report no finding — its reserved concluding step could never complete — and a
scenario with no finding cannot be graded. Three earlier full runs scored 7/8, 7/7 and 7/7:
accuracy was never the problem, the denominator was. With the conclusion able to land, ceilings
fell to 1 of 20 investigations and the count reached ten.

**c6 and c9 are still excluded, and this entry closes anyway.** The bar was always written as
n/10; ten is what it now measures. Replacing the two fixtures that cannot run here would make it
n/12, which is a different and larger question than the one this entry asked.

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

**The prefix in the heading is stale, deliberately.** Renamed to
`hephaisto.dev/destructive-actions-allowed` on 2026-09-02, when the project bought the domain the
Kubernetes convention says a label prefix should be — the old one was a DNS name nobody here owned.
The heading keeps the name it was filed under; everything live uses the new prefix.

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

**Status: answered 2026-08-31** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** `TransientRetryChatClient` is unit-tested nine ways and the overload it exists for has
not recurred since it was written. That is the difference between "tested" and "proven".

**Fix.** A fault-injecting `IChatClient` behind a dev-only flag would settle it once.

**Size.** S.

**Answered 2026-08-31, by the overload finally recurring — as something else.** The retry path
fired in production for the first time: it backed off as designed, five attempts with jitter, and
did not discard the investigation. So the mechanism is proven rather than merely unit-tested,
which is what this entry asked for.

It fired on an error it should have refused. "Your prepayment credits are depleted" arrives with
no HTTP status, so `Classify` took the transport branch and retried a billing page five times per
step — see [#54](#54-a-depleted-api-budget-is-retried-five-times-as-a-transport-failure), fixed in
the same release. A first observation that finds a defect is worth more than one that confirms
what was assumed.

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

**Status: fixed 2026-08-31** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-31.** Resolution happens once, in `chaos_assert_detection`, and is written to
`$WORKDIR/fixture-incidents.tsv` for every later reader. It records *every* incident matched
rather than only the one whose kind is checked, so the judge takes the first carrying a primary
finding and says when it passed over an empty one — a fixture routinely opens two and only one
holds the diagnosis.

Reproduced on synthetic details before being fixed: the old resolution reports c10 as
*"no primary finding"* while its diagnosis exists, because it matched the raw fixture id against
`target.name` while detection matches `fixture_target`, and for c10 those are different strings
(#33). "Detection matched no incident" and "none of its incidents carried a finding" were the
same sentence and are now two.

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

**Status: answered 2026-08-31** — the question it asked is settled; the one that replaced it is
[#66](#66-the-planner-acts-on-half-of-a-fair-fixture). See the end of this entry.

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

**Made reproducible offline on 2026-08-30, and then measured four ways.** The first thing v0.5.0
did was stop paying a cluster to ask this question. `cassettes/c11.json` records one real
investigation's tool surface, and `AnswerKey` gained a c11 entry — the **first in the corpus with
a non-empty `AcceptableActions`**, which means `PlanGrader.MissedAnAction` had never once been
reachable by any scenario in three releases of eval. An eval where declining is always correct
measures one direction of a two-directional behaviour.

With that in place the question costs cents instead of twelve minutes:

| arm | what changed | plan verdict, 3 replays |
|---|---|---|
| before | the shipped vocabulary: bare enum names | `MissedAnAction` ×3 |
| described | every action described; `RestartPod` says it **deletes** the pod | `MissedAnAction` ×3 |
| positive case | `30-planning.md` gains "when an action *is* the answer" | `MissedAnAction` ×3 |
| claim rule | `00-role.md`: a workload's account of itself is a claim, not a mechanism | `MissedAnAction` ×3 |

**Twelve replays, twelve declines, on identical evidence.** The transcripts say why, and it is
not what this entry assumed. Every hypothesis in all twelve anchors on the same phrase —
*persistent volume*:

> `/data/generation` on the persistent volume (PVC c11-transient-state) holds generation 1,
> which fails the startup check

That reasoning is **correct, and the rule it applies is the right one**: state on a PVC survives
a pod replacement, so replacing the pod does not repair it. What makes c11 different is a
*second* volume — the `/pod/counted` marker on an `emptyDir`, which is what stops the counter
advancing while the kubelet restarts the container in place. The model never reconciles the two.

**And it is not an evidence gap, which is what this entry previously suspected.** `describe_pod`
was called during recording and its output is in the cassette: `emptyDir` and `/pod/counted` both
appear, available to every replay. By the fourth arm the hypotheses quote the entrypoint's own
`[ "$gen" -lt 2 ]` condition. It reads the script, reads the volumes, and still answers from the
one the FATAL line names.

**So the open question resolves toward the fixture rather than the planner.** c11's mechanism
requires holding two volumes and their interaction in mind at once, and the log line it prints —
*"this pod cannot recover in place"* — is precisely true and thoroughly leading. Three separate
prompt improvements do not overcome it, and the positive-case change makes the decline *more*
principled rather than less, because it hands the model the durable-state rule explicitly.

**Deliberately not fixed by editing the fixture.** Dropping the `; this pod cannot recover in
place` clause would very likely turn this green, and that is the reason not to reach for it
first: it is how a test gets quietly made easier. The ordering — vocabulary, then prompt, then
fixture, replaying between each — is what makes "the fixture is unfair" a measurement rather
than a rationalisation. **The remaining decision is whose problem this is**, and it wants a human:

- **The fixture is unfair.** Trim the editorial half of the FATAL line so it reports a symptom
  rather than a verdict about its own recoverability. Defensible — real workloads do not
  editorialise — but it is still making the test easier, and it should be recorded as that.
- **The corpus needs a fairer transient fixture.** One whose pod-scoped state is the *only*
  state, so a restart is the answer without a two-volume inference. c11 stays as the hard case
  and stops being the one thing v0.2.0's acceptance criterion rests on.
- **The agent should get there and does not.** Then the fix is in investigation rather than
  planning, and it is more than a prompt clause.

**What did ship**, and it is worth separating from what did not: the three changes are net
improvements measured against the whole corpus — **8/8 `CorrectlyDeclined`, zero harmful
proposals**. A change that talks about when to act is exactly the change that could make the
agent restart a missing Secret, and it did not.

**One live run, for the record, and read it with suspicion.** A fresh c11 on the development
cluster on 2026-08-30 did propose `RolloutRestart`, with the mechanism named exactly — *"a new
ReplicaSet is created with incremented generation, replacing the stuck pod ... and the
replacement pod reaches Ready status without crash-looping"*. Policy denied it on a real gate
(*"youngest pod is 41s old, below the 120s minimum"*). That is one sample against twelve, on
different evidence, and it is recorded here because it is the only observation pointing the
other way — not because it settles anything.

**A second model family declines it too, 2026-08-31.** `deepseek-v4-flash` replayed the corpus
three times: **c11 graded `MissedAnAction` 3 of 3**, with a correct root-cause diagnosis every
time. Gemini declined 12 of 12 across four prompt arms; that is now **15 of 15 across two
independent model families**, one of which had never seen a Hephaisto prompt before.

This is the strongest evidence yet for the first branch above — that the fixture, not the
planner, is what needs replacing — because it removes "this particular model under-reads" as an
explanation. Two models with different training, different tokenizers and different vendors reach
the same defensible conclusion from the same two-volume premise. It also independently justifies
building c12 rather than continuing to edit prompts against c11: a third prompt arm was never
going to move something that is not prompt-shaped.

Worth noting what did *not* happen. Across all 27 DeepSeek runs there were **zero wrong findings
and zero harmful proposals** — the `MustNotPropose` guard held on a model the prompts were never
tuned for, which is a stronger test of it than the one it was written against.

**c12 measured, 2026-08-31: the fixture was the problem, and it is not the whole problem.**
Eight replays of the new c12 cassette on `deepseek-v4-flash`, **all eight structurally sound**
(4% mean miss, because the cassette was recorded on the model replaying it):

| fixture | proposes the action | diagnosis |
|---|---|---|
| c11 | **0 of 15** (12 Gemini across four prompt arms, 3 DeepSeek) | correct throughout |
| c12 | **4 of 8 — `Reasonable`** | 8 of 8 correct |

The first half is settled. A fixture whose pod-scoped state is the only state moves the planner
from never to half the time, and the diagnosis was never the difficulty in either case. c12 was
not made easy to get there: it still asks the model to work out that the lease comparison is
against the pod's own hostname, and it declines that half the time.

**So this entry's own first branch is answered and a new question replaces it.** The corpus can
now grade an action at all — `Reasonable` had never once been produced before c12 existed, across
ten scenarios — which makes "does the agent act" measurable rather than theoretical. What it
measures is 50%, and a 50% action rate is not something to build the landing page's central claim
on. That is a planner question now, on a fair fixture, and it is worth reopening as one rather
than leaving inside an entry about c11.

**Answered 2026-08-31, and split.** This entry asked whether the fixture was unfair or the
planner under-reading. The answer is *both, separably*: c11 is unfair in a way that is now
documented rather than argued about — its recovery evidence is not pod-scoped, and 15 of 15
declines across two independent model families is not a planner that missed something — and c12,
built to be fair on exactly that axis, is acted on 4 times in 8 with 8 correct diagnoses.

What closes here is the v0.2.0 acceptance criterion's *fixture* problem, and with it this entry's
own framing. What does not close is the rate, which is why it leaves as #66 rather than as a
tick. Keeping the two inside one entry is how an answered question keeps a solved problem open,
and this milestone's rule is that an item leaves by being fixed or by being reclassified with the
reasoning recorded.

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

**Status: fixed 2026-08-31** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-31.** `chaos_assert_action_executed` records whether anything ran, and
`chaos_assert_verification` skips both assertions naming the one that actually failed rather than
asserting them into two 240s timeouts. Checked in both directions against a stubbed harness: the
skip fires only when nothing executed, and an action that ran and did not recover still fails
exactly as before — an assertion that holds in both directions is not an assertion.

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

**Status: fixed 2026-08-31** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-31.** `data-theme` on the root, three states, persisted through the interop that
already existed. Two palettes rather than three: `:root` is dark and stays the default, so only
light is written twice — once for an OS that asked and once for a reader who did — and the
`:not([data-theme="dark"])` guard is what makes those compose instead of fight. All six
combinations of choice and OS are worked through in `tokens.css`, because a theme system that is
right in four of them is the usual bug.

**Two mistakes worth keeping**, both found by driving the live console rather than by reading.
The label was written by `app.js` on load, and `<Routes>` renders interactively — so Blazor
replaced the body subtree and left the button reading "theme" with no state, which is
[#48](#48-the-console-suite-interacts-with-a-page-the-circuit-has-not-taken-over-yet) wearing
different clothes. The label is now a CSS `::after` keyed off the attribute on `<html>`, where
Blazor does not reach. And `app.js` is a plain script at the end of `<body>`, so on a warm cache
it can run *after* `DOMContentLoaded` and the listener would never fire at all.

Verified in a real browser against the live console, including the case the media query cannot
serve — a **light** operating system with dark chosen: stamped before paint, dark background,
`theme-color` following, surviving navigation. Six cascade combinations are asserted in
`design/visual/tests/theme.spec.ts` under both projects and verified falsifiable: removing the
explicit-light rule fails it under `[dark]` and still passes under `[light]`, which is precisely
the case that rule exists for.

### 51. `run.sh` has not been re-run on a kind cluster since the suite was fixed

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** v0.4.0 closed [#46](#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto)
and removed every `test.skip` from the console suite, and the milestone's exit criterion is that
`scripts/e2e/run.sh` exits 0 in its default mode. That has not been observed.

**Evidence.** Every spec was verified against a live console on the development cluster: 9 specs,
0 skipped. But `run.sh` in Observe boots its own kind cluster, applies chaos fixtures and runs
nine phases before the `ui` one, and that has not been run since the fixes landed.

**Why it is still open.** It is *blocked on the branch being pushed*, not merely un-run.
`scripts/e2e/lib/build.sh` dispatches `nightly.yml` with
`gh workflow run --ref $(git rev-parse --abbrev-ref HEAD)` and waits for the resulting image and
version artifact, so the harness cannot build anything from a branch GitHub has never seen. The
only other channels are `--rc`, which pushes a tag, and `--tag`, which tests something already
published. There is no local-build path.

So the sequence is: push the branch, then run it. Until then the claim is about the specs rather
than about the harness, and this repo has been caught by exactly that gap before — five of the six
v0.1.0 release candidates failed on the harness rather than on the thing being measured.

Two known failures are also waiting there and are *not* regressions from this milestone:
[#49](#49-the-console-spec-compares-a-capped-api-call-against-an-uncapped-page) trips on any
install with more than 100 incidents, and the budget-meter spec asserts non-zero spend, which is
only true once the agent has actually investigated something in the current hour.

**Fix.** Run it. `scripts/e2e/run.sh` with no arguments.

**Size.** S to run, unknown to fix whatever it finds.

**Fixed 2026-08-30, and "whatever it finds" was the point.** It was run, twice, and everything
outside the console phase passed on the first attempt: five fixtures detected and classified, five
diagnoses each citing evidence, cost reconciled against the ledger, no action executed in Observe,
and a queued notification surviving an agent restart.

The console phase failed all nine specs, and behind it were three separate things:

- My own regression. #48's fix waited for a `_blazor` **websocket**, which is a transport rather
  than a state; it passed against a development image and timed out against a published one. Now
  it waits for the negotiation, which happens under every transport.
- My own mistake. A `kubectl port-forward` left running against the *development* cluster owned
  port 18100, so one run's browser reached the wrong agent entirely — the giveaway was a console
  reporting `dryrun` and 106 incidents during an Observe run with five fixtures.
- And underneath both, [#53](#53-the-console-was-never-interactive-in-any-released-image): the
  console was never interactive in any released image, and this suite is the first thing in the
  repository capable of noticing.

Final state, against a live kind cluster in the default mode with the fixed image: **9 passed, 0
failed, 0 skipped.**

**Then the whole run, in one invocation.** The caveat above — that every phase had passed but not
together, because the published image predated #53's fix — was discharged by a nightly built from
the fixed Dockerfile:

```
hephaisto end-to-end: 0.4.0-main.0.24     channel nightly, mode Observe
  build 4  cluster 3  deps 13  deploy 26  chaos 2  validate 15  notify 7  ui 1
  0 failures in any phase
  PASSED -- 71 assertions, 5 skipped        11m 46s, $0.399 of Gemini
```

The five skips are all conditions the harness states outright: `acting` because Observe installs
nothing that can execute, unsigned deliveries because `values-e2e` leaves signing off, and three
diagnosis-shape notes. The console phase reports `expected=9 skipped=0 unexpected=0`.

### 52. Two components are implemented twice

**Status: fixed 2026-08-31** — see the end of this entry. The heading is left as it was, because these numbers and titles are the anchors `roadmap.md` links by.

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

**Fixed 2026-08-31, and with no visual change — which this entry did not expect.** It assumed
consolidating would change the rendering of both and deferred the work on that basis. What was
shared is now defined once; what differs stayed, because it is context rather than accident. The
budget meter is a page-level element carrying threshold marks, so it is taller and needs a
positioning context; the confidence bar is inline in a finding and has a fixed width. One height
for both would make one of them wrong for where it lives.

So the implementation is consolidated and the rendering is untouched, which is the half the
baselines can prove. A deliberate visual unification stays available as a design decision
somebody reviews rather than a refactor nobody can see.

Proven both ways: all 34 baselines pass unchanged at `maxDiffPixels: 0` and the gallery genuinely
renders all four components; then changing the **one** shared bar rule fails the finding shot and
the budget-meter shot in both themes — six failures from a single declaration, which is what "one
implementation" means when it is true.

### 53. The console was never interactive in any released image

**Status: fixed 2026-08-30** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** In every image this project has published, `_framework/blazor.web.js` returned
**404**. Blazor never started, no circuit was ever established, and the console was a static
page. Every interactive control was dead: approve, deny, re-arm, the retry button, the feedback
form, the incident filters.

**Evidence.** Against the e2e cluster, on the published `0.4.0-main.0.21`:

```
<script src="_framework/blazor.web.js">          <- not fingerprinted, which is the tell
_framework/blazor.web.js -> 404 (0 bytes)
grep -c blazor /app/Hephaisto.Agent.staticwebassets.endpoints.json   -> 0
```

Reproduced by building the production image locally, then bisected to one flag in `Dockerfile`:

| build | manifest | entries matching `blazor` | result |
|---|---|---|---|
| `dotnet publish --no-restore` | 42489 bytes | **0** | 404 |
| `dotnet publish` | 56532 bytes | `blazor.web.js` present | 200 |

**Cause.** The Dockerfile restores in a separate earlier layer, against the `.csproj` files alone,
so a source-only change reuses it. At that moment the project contains no Razor components, so
the Blazor framework's static web assets are not resolved — and `--no-restore` at publish reuses
that incomplete result. The endpoint is never registered, `@Assets["_framework/blazor.web.js"]`
finds no entry and returns its input unchanged, and the browser asks for a path nothing serves.

**Why nothing caught it for four releases.** Three failures stacked:

1. Nothing fails. The static server-side render is unaffected, so the console looks perfect and
   the pod logs nothing.
2. Development never sees it. `Dockerfile.dev` runs `dotnet watch`, which builds rather than
   publishes, and the build manifest has the assets. Every manual check was done there.
3. The console suite could not see it. Until v0.4.0 it asserted only read-only content, and
   reading a static render is indistinguishable from reading a live one. Its single interacting
   spec was the one [#46](#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto)
   caused to skip in the default mode — so the only assertion in the repository that could have
   caught this was also the only one that never ran.

**Fixed 2026-08-30.** `--no-restore` removed from the publish step, with the measurement in a
comment beside it. The layer split still earns its keep: the packages are already in the image's
NuGet cache, so the restore the publish now performs downloads nothing.

Verified end to end rather than by inspection: the fixed image was loaded into the live e2e kind
cluster, the deployment repointed at it, and the console suite run against it — **9 passed, 0
failed, 0 skipped**, where the same suite against the published image failed all nine.

---

## Opened by v0.5.0

### 54. A depleted API budget is retried five times as a transport failure

**Status: fixed 2026-08-31** — see the end of this entry. The heading is left as it was, because
these numbers and titles are the anchors `roadmap.md` links by.

**Symptom.** Every investigation stalls, slowly, and the log says the provider failed
*transiently*. It did not. The account is out of credit, which is as permanent as a failure gets
until a human visits a billing page.

**Evidence.** Observed on the development cluster on 2026-08-31, on every step of every
investigation:

```
Provider call failed transiently (transport); retrying in 00:00:01.10 (attempt 1 of 5).
Google.GenAI.ClientError: Your prepayment credits are depleted.
```

**Cause.** `TransientRetryChatClient.Classify` walks the exception chain for an
`HttpRequestException` and returns `"transport"` when `StatusCode is null`, on the stated
reasoning that *"no status at all is a transport failure - DNS, connection reset, TLS - and is
exactly what a retry is for."* That reasoning is right about the cases it names and wrong about
this one: `Google.GenAI.ClientError` arrives with no status, so a billing exhaustion takes the
transport branch.

**Why it matters.** Not the wasted calls — those fail immediately and cost nothing. It is that
the failure is *reported as the wrong kind*. A run that says "transient, retrying" five times per
step, on every step, produces a slow opaque stall where the true answer is one sentence a human
can act on in a minute. The same shape as [#47](#47-the-act-phase-reports-two-failures-that-are-consequences-of-the-first):
a report that is confidently wrong about why is worse than one that is merely incomplete.

**Fix.** Classify on the message as well as the status. `RetryableMarkers` already exists for
exactly this - matching message text when a status is absent - so the change is its mirror: a
small set of *non*-retryable markers checked before the transport fallback, and a distinct log
line saying the budget is gone rather than that the network hiccupped. Getting the provider's
own error type out of the `HttpRequestException` shape would be better still, and is more work.

**It also answers [#13](#13-the-retry-path-has-never-been-observed-firing-in-production).** That
entry says the retry path *"is unit-tested nine ways and the overload it exists for has not
recurred since it was written - the difference between tested and proven."* It is now proven: it
fires, it backs off as designed, and it does not lose the investigation. It fired on an error it
should have refused, which is a better first observation than never having seen it at all.

**Size.** S.

**Blocks:** recording a c12 cassette, replaying anything, and the `--mode Auto` cluster run —
every one of which needs the model. Nothing in v0.5.0 that touches the agent's reasoning can be
measured until the account has credit.

**Fixed 2026-08-31, in the release that opened it.** `PermanentMarkers` are checked first, before
the status, because these arrive without one. Kept deliberately narrow: each phrase is one that
cannot also describe a transient condition, since this list *overrules* a correct retry and a
bare "billing" or "disabled" would match a 503 from a billing service and turn a real hiccup into
a hard stop — the worse of the two mistakes. Daily-quota wording is left out for the same reason;
it is not reliably distinguishable from a per-minute rate limit, and five retries over ten seconds
costs nothing if it turns out to be one.

Both arms tested and verified falsifiable: removing the check fails the four new permanent-error
assertions, while `"Rate limit exceeded for this project's billing tier"` — which contains the
word this list is most tempted to match on — still retries.

### 55. The cassette corpus grades the model that recorded it

**Symptom.** Replaying the nine-scenario corpus against `deepseek-v4-flash` on 2026-08-31
reported **6 of 9 runs structurally unsound**, with replay miss rates from 14% to 65%. The same
cassette replayed three times gave 14%, 38% and 50%.

**Cause.** A cassette records the tool calls the *recording* model chose to make, and nothing
else. `c5.json` declares **31 tools** to the model and records **9 calls across 7 of them**. Of
the six tools DeepSeek missed on, five — `grafana_api_request`, `list_datasources`,
`list_deployments`, `list_statefulsets`, `search_dashboards` — have **zero** recorded calls,
because Gemini never asked those questions. The sixth, `get_pod_logs`, has three recorded calls,
which disables `ReplayToolset`'s fuzzy arm (it resolves only when exactly one call to that tool
was recorded), so any argument difference is a miss too.

**Why it matters.** A miss is not a neutral absence. `ReplayToolset` answers "nothing was
recorded", which to the model reads as a cluster with no deployments and no dashboards — so it
digs further, spends its `MaxToolCalls` budget, and can exhaust the run. Replay does not merely
fail to help an unfamiliar model, it actively misleads it.

So the corpus is a **within-model** instrument. For its designed job — measuring a prompt or
budget change on a fixed model — it is sound, and the recorded evidence matches because the model
asks roughly the same questions. As a **cross-model** benchmark it is biased toward whichever
model recorded it, and the `sound` count is the guard that says so rather than a nuisance.

This was not understood when the provider work started; the plan for it asserted the corpus was
provider-neutral because the *format* is. The format is. The coverage is not.

**Consequence for a provider switch.** Adopting a new investigating model invalidates the corpus
as a baseline for it, and re-recording is part of the switch rather than an optional follow-up.
Note the direction of the bias before discounting a result: DeepSeek scored 8/9 while being fed
"nothing was recorded" for a third of its calls, so that is a floor and not an inflated number.

**Options, none of them free.**
- Re-record the corpus per candidate model. Sound, and costs a cluster run per fixture per model.
- Record with a deliberately exhaustive tool sweep, so a cassette covers more of the surface than
  one model happened to want. Larger cassettes, one recording, and it never covers everything.
- Report `sound` as a first-class number beside accuracy, and refuse to rank models on a corpus
  they did not record. Cheapest, and honest about what the instrument can carry.

**Size.** M for the first, S for the third.

### 56. The planner assumed every provider can enforce a JSON schema

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** On `deepseek-v4-flash`, all nine cassettes produced a correct diagnosis and
`NoPlan`. The agent looked like it was declining to act.

**Cause.** Phase 2 sets `ResponseFormat = ChatResponseFormat.ForJsonSchema<ActionPlanDraft>`.
DeepSeek answers `HTTP 400 invalid_request_error: This response_format type is unavailable now`.
Confirmed against the API directly rather than inferred: `json_schema` 400s, `json_object`
answers.

**Why it matters** is the disguise, not the outage. Phase 1 is untouched, so the agent
investigates well and proposes nothing — and `NoPlan` is not distinguishable, in a run summary,
from a considered `CorrectlyDeclined`. "The agent can diagnose but never acts" is the exact claim
[v0.2.0's acceptance criterion](roadmap.md) exists to disprove, and it must not be able to hide
inside a verdict that reads as judgement.

It also cost an hour of misdiagnosis: the first inference was that the low-confidence escalation
gate was skipping phase 2, because the planning error was invisible — a log filter had been
swallowing the line, since the exception detail renders on the same line as the message.

**Fix.** `Llm:PlanningStructuredOutput=JsonObject` asks for plain JSON and moves the schema into
the prompt. Both branches derive it from `ActionPlanDraft` through the same
`ChatResponseFormat.ForJsonSchema` call, so what the model is shown and what the reply is parsed
against cannot drift.

Off by default, and deliberately the weaker mode: the shape becomes a request rather than a
constraint. That is safe only because nothing downstream takes the model's word for it — the
draft is parsed leniently, every cited finding id is checked by `GroundingVerifier`, and an
action missing a namespace, kind or name is dropped before an executor sees it. A model that
ignores the requested shape produces no plan, not a wrong one.

**Size.** S.

**Fixed 2026-08-31.** Verified on c3 and c4, the two cassettes that replay soundly for this
model: `CorrectlyDeclined` on both, at the default confidence gate.

### 57. Production needs a Google API key so the search box has a semantic arm

**Symptom.** Embeddings are the one part of the stack with no alternative provider.
`GeminiEmbeddingGeneratorFactory` is the only implementation, and `Llm:EmbeddingDimensions=768`
is pinned to the `vector(768)` column in `incident_digests`.

**Why it matters.** For a self-hosted Kubernetes agent, requiring an external API account so
that one arm of the console's hybrid search works is a deployment tax out of proportion to what
it buys. It is not a cost problem — `gemini-embedding-001` is $0.15/M on short, hash-deduped text
— and not a correctness problem, since `IncidentEmbedder.EmbedAsync` already returns null on any
failure and `IncidentSearch` falls back to its lexical arm. It is a dependency problem.

**Candidate.** `EmbeddingGemma-300m`: 622 MB, **768 dimensions natively so the column is
unchanged**, MTEB English v2 69.67, and it runs on CPU as an in-cluster Deployment.
`Qwen3-Embedding-0.6B` scores higher (70.70) but is 1024-dimensional, so it needs Matryoshka
truncation or a migration.

**Blocked on a measurement that does not exist.** Nothing in this repo scores search quality, so
a change here could only be justified by someone else's benchmark. Building that measurement is
the first half of the work, and without it this would be an unevidenced swap bundled next to
measured ones — which is the habit the eval harness exists to break. Existing digests also need
re-embedding: a data backfill, not a schema migration.

**Size.** M, of which the measurement is most of it.

**Split 2026-09-01, and half of it shipped.** The entry conflated two questions that turn out to
have different blockers:

1. *Can a self-hosted install reach an embedding provider at all?* Nothing about that needed a
   measurement, because it does not change anyone's defaults. `IEmbeddingGeneratorFactory` now
   mirrors `IChatClientFactory`, `OpenAiEmbeddingGeneratorFactory` joins the Gemini one behind
   it, and `Llm:EmbeddingProvider` selects between them - so any server offering
   `/v1/embeddings`, an in-cluster Ollama included, works with no external account. **The
   default is unchanged**, deliberately: `EmbeddingProvider` is *not* inherited from `Provider`,
   because speaking the OpenAI chat format does not imply serving embeddings, and where it is
   served the useful model is rarely the chat model. Six tests pin that, including the
   non-inheritance rule and the mirror of the existing "a chat key is not an embedding key"
   invariant.
2. *Which model should ship as the default?* Still blocked, and on exactly what this entry said:
   nothing here scores search quality.

**Three corrections to the candidate above, for whoever picks up (2).**

- **Licence is the deciding factor and was not weighed.** EmbeddingGemma is under the Gemma
  Terms of Use, not Apache or MIT - use restrictions, flowed down to anyone the chart
  distributes to. This entry exists to remove a Google dependency from a self-hosted AGPL agent;
  a Google-*licensed* model removes the API account and not the vendor relationship. Apache-2.0
  peers exist in the same size class: `Qwen3-Embedding-0.6B`, `gte-base-en-v1.5`,
  `nomic-embed-text-v1.5`, `granite-embedding-278m`, `Arctic-embed-m-v2.0`.
- **"768 natively so the column is unchanged" is weaker than it reads.** It is used above to
  rule out Qwen3 at 1024, but Qwen3 supports Matryoshka truncation to 768 - and this entry
  already concedes every digest needs re-embedding regardless. The backfill is the expensive
  half and happens either way; once every row is being rewritten, `ALTER TABLE ... vector(N)`
  plus an HNSW rebuild is close to free.
- **Task prefixes.** EmbeddingGemma expects `task: search result | query: ...`. Wrong prefixes
  degrade retrieval silently, which is a good way to benchmark a model into last place by
  accident.

So the shortlist is filtered **licence first**, then 768-native or cleanly truncatable, then
CPU-runnable - and the measurement picks the winner rather than this file doing it. MTEB
standings move; re-check them at the time rather than trusting the numbers written here.

### 58. The eval judge bypasses the provider seam

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `Scoring/RootCauseJudge.cs` posts directly to
`generativelanguage.googleapis.com/v1beta/models/{model}:generateContent` with an
`x-goog-api-key` header and its own `HEPHAISTO_GEMINI_API_KEY` lookup. It never touches
`IChatClientFactory`.

**Why it matters, and why it is small.** It fails soft — returns null, and the run scores
deterministically — so it blocks nothing, and every bake-off so far has simply run `--no-judge`.
But it means a provider switch is complete everywhere except the instrument that grades it, and a
judge on a different model from the agent is a defensible choice that should be *chosen* rather
than inherited from where the code happened to be written.

**Consequence today.** With no Gemini credit the judge cannot run at all, so comparisons are
deterministic-only and not directly comparable to the published `22/24`, which was judged.

**Size.** S.

**Fixed 2026-08-31.** Both judges are now provider-selectable and both read the same variables,
so the shell harness and `hephaisto-eval` stay configured identically: `JUDGE_PROVIDER`, then
`JUDGE_ENDPOINT` / `JUDGE_MODEL` / `JUDGE_API_KEY`, each falling back to the agent's own setting.
`OpenAiRootCauseJudge` joins `GeminiRootCauseJudge` behind `IRootCauseJudge`, and
`RootCauseJudgeFactory` picks between them. The prompt was extracted to one shared builder rather
than copied a third time — a second provider must not become a second question, which is the same
reason it was copied verbatim from `judge.sh` in the first place. Three tests pin that invariant.

Verified against a local `gpt-oss:120b`, and verified *falsifiable*: a real cause grades
`correct: true`, and a bare restatement of the symptom grades `correct: false` — which is the one
distinction the prompt exists to draw.

**What this does not fix, and now says out loud.** Pointing the judge at the model the agent ran
on is self-assessment. The harness warns when the two match and marks the score `SELF-GRADED` in
the recorded note; it is weaker than two independent models, though not worthless, since the grade
is against a fixed answer key rather than against the agent's own reasoning. Set `JUDGE_ENDPOINT`
and `JUDGE_MODEL` to a second model when one is available.

### 59. The step budget is tuned to one model and silently caps another's accuracy

**Symptom.** `gpt-oss-120b` scored **17/30** on the corpus. Raising `Llm:Investigation:MaxSteps`
from 12 to 20 — changing nothing else — scored **18/20**, and **18/18** excluding the one cassette
the harness already reports unsound. Measured locally on 2026-08-31.

**Cause.** `MaxSteps = 12` was chosen against a measured Gemini mean of 7.5 steps, so it sits at
roughly 1.6× the behaviour it was calibrated on. `gpt-oss-120b` averages 9.6 steps and
`deepseek-v4-flash` 6.9, so the same ceiling is generous for one model, comfortable for another
and binding for a third. **10 of 30 gpt-oss runs terminated `StepBudgetExhausted`, and every one
produced no finding** — several at a 0% replay miss rate, so the evidence was in hand and the run
simply ran out of turns.

**Why it matters.** It does not look like a budget. It looks like a weaker model: the verdict is
`NoFinding`, which is the same verdict a model that genuinely failed to diagnose would produce.
Comparing two models under one ceiling therefore measures *how closely each matches the model the
ceiling was calibrated against*, and reports the difference as accuracy. That is the same class of
error as [#55](#55-the-cassette-corpus-grades-the-model-that-recorded-it), one layer down: the
corpus biases toward the model that recorded it, and the budget biases toward the model it was
tuned on.

DeepSeek is the control that makes this a finding rather than a guess: **0 of 27 runs hit any
ceiling**, so raising it could not have helped, and the two models are fairly compared at their own
natural budgets rather than at a shared one.

**Why it is not just "raise the default".** The ceiling is a cost control, and step count is what
it controls. Twenty steps on a hosted provider is roughly 60% more spend per investigation; on a
local model the tokens are free and the only cost is wall-clock. So the right value is a property
of the model *and* of how it is paid for, which is an argument for making it a documented
per-model setting rather than one number that is wrong for everything except Gemini.

**Fix, in order of cost.** Report `terminationReason` beside the verdict in the eval summary, so a
budget-truncated run is never read as a wrong answer — that is small and worth doing regardless.
Then record a recommended `MaxSteps` alongside each model's price entry, since the two are already
a pair: what a model costs and how many turns it needs are the same decision.

**Size.** S for the reporting, M for per-model budgets.

**Half of this entry's evidence was a different bug, found 2026-09-01.** The claim that every
`StepBudgetExhausted` run "produced no finding" was read here as the ceiling truncating the model
mid-thought. It was not: the reserved concluding step could never complete, because a tool-based
conclusion needs two round trips and one was reserved — [#78](#78). The ceiling is still a
per-model number and this entry stands, but its most striking figure belonged to something else.

**A second ceiling, measured on a cluster 2026-08-31.** The first full-corpus run against a local
`gpt-oss-120b` reported `1 Cancelled, 2 WallClockExhausted (of 12 investigations)`. So it is not
only `MaxSteps`: `MaxWallClock` (10 minutes) is also a hosted-model number, and a local model at
`MaxSteps=20` runs close enough to it that a fifth of investigations end on a clock rather than on
a conclusion. Measured throughput was ~6.7 minutes per investigation, serialised.

That matters for the same reason the step ceiling does — a run truncated by a limit is not
distinguishable, in a summary, from a model that had nothing to say — and it is the concrete
argument for `terminationReason` being reported beside the verdict, which is the small half of the
fix below and still not done.

**Partly addressed 2026-08-31, in the harness only.** `scripts/e2e/lib/deploy.sh` now sets
`Llm__Investigation__MaxSteps=20` whenever the provider is openai-compatible, overridable with
`HEPHAISTO_LLM_MAX_STEPS`, so a full-coverage e2e run does not measure this ceiling and call the
result a model. The hosted Gemini path keeps the shipped 12. **The entry stays open**: this is one
number in one script, not the per-model setting beside the price entry that the fix above asks
for, and the eval summary still does not report `terminationReason`.

### 60. A provider's own options cannot be reached through the OpenAI-compatible seam

**Symptom.** `qwen3-next:80b` was benchmarked and abandoned: it emits **3,559 output tokens for a
single agentic turn** (15,721 characters of reasoning) against a few hundred for
`gpt-oss-120b`, which makes an investigation take 5-12 minutes instead of 82 seconds. The model
is otherwise healthy — 100% GPU-resident, 893 tok/s prefill and 61 tok/s generation, both *better*
than gpt-oss, with prompt-prefix caching working (0.1s on a repeated 26k-token prompt).

**It is not fixable from here, and that is the actual entry.** Ollama can turn the reasoning off;
the seam cannot ask it to. Three routes were tried on 2026-08-31:

| route | result |
|---|---|
| `/no_think` in the system prompt | ignored — 3,295 tokens, reasoning intact |
| `"think": false` on `/v1/chat/completions` | ignored, **and the tool call was lost** |
| `PARAMETER think false` in a Modelfile | `Error: unknown parameter 'think'` |
| `"think": false` on native `/api/chat` | **works** — reasoning length 0 |

So the switch exists and sits on the one endpoint the seam deliberately does not use.

**Why not just add it.** `OpenAiChatClientFactory` exists so that DeepSeek, OpenRouter, Ollama and
LM Studio are one implementation. Reaching Ollama's native API means an Ollama-specific client,
which is the thing the seam was built to avoid — and the same problem recurs per provider, since
every one has options outside the OpenAI schema (Gemini's thinking budget, OpenRouter's provider
routing, gpt-oss's `reasoning_effort`).

The proportionate shape is a passthrough: a dictionary of provider-specific request properties on
`LlmOptions`, injected via `ChatOptions.RawRepresentationFactory`, which `Microsoft.Extensions.AI`
already provides for exactly this. One seam, provider-shaped extras, no second client. That also
covers `reasoning_effort` for gpt-oss, which is a live tuning knob rather than a hypothetical one.

**Until then, a model whose defaults do not suit an agentic loop cannot be tuned to fit it**, and
verbosity — not accuracy and not throughput — is what disqualified the only open-weight
alternative tested.

**Size.** S.

### 61. A keyless endpoint read as an absent model, and the run still exited 0

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** With `HEPHAISTO_LLM_PROVIDER=openai` pointed at a local Ollama, `deps_secrets` set
`LLM_AVAILABLE=0` and the harness went on to `skip` every investigation, act, judge and budget
assertion. The run then exited 0.

**Cause.** `lib/deps.sh` only probed the endpoint when `HEPHAISTO_LLM_API_KEY` was set, and
treated its absence as "no model". That is true of a hosted provider and false of every local
one: Ollama and LM Studio serve `/v1/models` with no credential at all, and
`OpenAiChatClientFactory` already knows this — it sends a placeholder to a local endpoint
precisely so a keyless server works.

**Why it matters** is the exit code, not the skip. A run that tested detection only is a
defensible outcome; a run that says so in eight `skip` lines and then reports success is not,
because nobody reads the middle of a green log. This is the same failure `scripts/ci-test.sh`
exists to prevent on the unit side — *"a green build that tested nothing is worse than a red one,
because nobody looks at it again"* — arriving through a door that had no guard on it.

It also lands on the one path this milestone most needed to work: the whole point of running
`gpt-oss-120b` locally is that the tokens are free, and free tokens come with no API key.

**Fix.** Probe `${endpoint%/}/models` without an `Authorization` header when no key is present
and believe a 200. The keyed path is unchanged, including its rejection handling. Downstream, a
reachable keyless endpoint creates an empty `hephaisto-llm` Secret — both chart keys are optional
since v0.5.0, and `bootstrap-secrets.sh` writes nothing when neither is set, so without this the
next assertion failed on a Secret that was correctly absent.

**Size.** S.

**Fixed 2026-08-31.** Both arms exercised: a keyless local endpoint reports
`LLM_AVAILABLE=1`, and an unreachable one still degrades to detection-only rather than aborting.

### 62. The harness proved the model was reachable from the wrong machine

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** The LLM endpoint probe passes, the install succeeds, and then every investigation
faults — forty minutes into a run whose full-coverage form takes two hours.

**Cause.** `deps_secrets` probes the endpoint with `curl` **from the host**. The agent runs in a
**pod**. For a hosted provider those are the same network position and the probe is honest; for a
local model they are not related at all — the endpoint is on this laptop, the cluster is inside
Rancher Desktop's Lima VM behind kind's own bridge, and `127.0.0.1` means a different machine on
each side.

Measured while fixing #61: Ollama ships bound to `127.0.0.1` only, so the host probe passes and
**no pod can reach it**. That is the default state of a fresh install, which makes this the
expected mistake rather than an exotic one.

**Why it matters** is where the failure lands. An unreachable model surfaces as ten faulted
investigations, which reads as a broken agent and not as a wrong address — and it costs the whole
run to find out. The harness already holds the opinion that a check belongs where it is cheap:
*"Validate the key NOW rather than discovering it is wrong fifteen minutes in, when four
investigations have failed and the failure looks like a bug in the agent."* This is that same
sentence, one network hop further out.

**Fix.** `deps_verify_llm_reachable` runs one `curl` from a throwaway pod inside the cluster
against the configured endpoint, and `fail`s on anything but 200. Deliberately a fail and not a
warn: continuing would spend two hours proving something about an address.

**Size.** S.

**Fixed 2026-08-31.** The reachable addresses from inside the VM are recorded in
`scripts/e2e/README.md`; the tailnet address is the documented default because it does not move
with DHCP or with docker's bridge topology.

### 63. An acting run could be told to skip the fixture it asserts about

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `--mode Auto --fixtures <list>` fails the act phase with *"c12 was not acted on"*.

**Cause.** `run.sh` appended `ACT_FIXTURE` only when `FIXTURES` was empty, so naming fixtures
explicitly replaced the act fixture instead of being joined by it. The act phase then asserted
against a fixture that had never been applied.

**Why it matters** is which claim it fakes. The failure is indistinguishable in the report from
the agent declining to act — the exact thing [#41](#41-c11-has-never-been-run-against-a-cluster)
spent three attempts and twelve replays investigating. A harness that can manufacture that
symptom is a harness that can send somebody looking for a planner bug that does not exist.

It also lands precisely on the release gate: `--full` names its fixtures, so `--mode Auto --full`
— the run this milestone needs — was the shape that broke.

**Fix.** In `DryRun` and `Auto`, ensure `ACT_FIXTURE` is in the list however that list was
chosen, materialising the default first and skipping the append when it is already present.

**Size.** S.

**Fixed 2026-08-31.** Seven cases exercised: empty, explicit, `--full`, act-fixture-only,
act-fixture-mid-list, and both acting modes. No duplicate, no drop.

### 64. DryRun asserted a condition DryRun cannot produce

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `--mode DryRun` always failed the act phase.

**Cause.** `chaos_assert_action_executed` selected actions with `.dryRun == false`. In DryRun
every executed action is `dryRun: true` by definition, so the assertion could never be satisfied.
The mode was not merely untested; it was **unrunnable**, and had been since it was added.

**Why it matters.** DryRun is the middle rung of the safety ladder — the mode that says "plan it,
then change nothing" — and it is the one an operator is most likely to trial before enabling
`Auto`. Shipping a mode whose own harness cannot express a pass is a claim without an instrument.

**Fix.** `chaos_expected_dryrun` derives the expected shape from `E2E_MODE`, and DryRun gains the
half that is actually worth asserting: a plan was produced **and** nothing executed for real.
Without that second check the mode only proves a plan existed, which Observe already proves.

While here: `--mode` is now validated at parse time rather than passed through to `--set mode=`,
where an invalid value was rejected by the chart's enum only after a cluster had been built and
the observability stack installed; and `Off` no longer runs the act assertions, which it could
never satisfy either.

**Size.** S.

**Fixed 2026-08-31.**

### 65. A resumed run skipped every model assertion and still exited 0

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `--from deploy` or `--only validate` reports investigations, budget, judge and act
as `skip`, then exits 0 — with a working key and a reachable model.

**Cause.** `LLM_AVAILABLE` is set inside `deps_secrets` and nowhere else. Any `--from`/`--only`
that starts after the deps phase carries the initialising `0`.

**Why it matters** is the same shape as [#61](#61) and worth stating once more because the
harness keeps rediscovering it: the failure is a **pass**. A resumed run is also when somebody is
least likely to reread the middle of the log, because they resumed precisely to skip the part
they had already watched.

**Fix.** Refuse rather than guess. Resuming past `deps` into a phase that needs the model now
requires `HEPHAISTO_E2E_ASSUME_LLM=1` (or `0`) — a claim the caller is entitled to make from
having watched the earlier run, and the harness is not entitled to make on its own.

While here, `chaos_cleanup` no longer hangs off `should_run chaos`: an `--only ui` run never
enters that phase but still finds fixtures on the cluster, and leaving them there is how the next
run inherits someone else's incidents.

**Size.** S.

**Fixed 2026-08-31.**

### 66. The planner acts on half of a fair fixture

Split out of [#41](#41-c11-has-never-been-run-against-a-cluster), which asked whether that
fixture was unfair or the planner under-reading and answered *both, separably*. This is the half
that survived.

**What is measured.** On `c12-stale-lease` — a transient fault whose evidence is entirely
pod-scoped, built precisely so that declining to restart is not the defensible answer — the agent
produces a correct diagnosis **8 times in 8** and a `Reasonable` plan **4 times in 8**.

**Why it matters.** It is the only number under the sentence the project leads with. "The agent
can act" was, until c12 existed, a statement about code: `PlanVerdict.Reasonable` had never once
been produced by any scenario in three releases, so the claim had no instrument at all. It has
one now, and what the instrument says is 50%. That is enough to stop calling the acting path
theoretical and not enough to build a landing page on.

**What it is not.** Not a safety failure — the policy engine never denied anything, because it
was never offered anything; the four declines are plans that were not produced. Not a diagnosis
failure either, at 8 of 8. And not a structured-output failure: the local grammar-constrained
path produces well-formed plans, so [#56](#56)'s disguise is ruled out here rather than assumed.

**Where to look first**, in the order the evidence points:

1. **The confidence gate.** A decline and a low-confidence escalation are indistinguishable in
   the summary, and #41's four prompt arms moved the diagnosis without moving the plan — which is
   the shape of a threshold, not of a comprehension gap.
2. **`terminationReason` is not reported beside the verdict** ([#59](#59)), so a plan missing
   because the step budget ran out is not separable from one the model chose not to make. That
   reporting is small and blocks reading this number honestly.
3. **The 17+N tool surface at planning time.** Phase 2 is deliberately unable to call a tool, so
   everything it reasons from is what phase 1 chose to write down.

**Deliberately not fixed by lowering the bar.** Grading `MissedAnAction` as a pass, or widening
`AcceptableActions` until the observed behaviour scores well, would make the number go up without
moving the agent — and this corpus's whole value is that it was built before the result was
known.

**The cluster rate is lower than this entry's headline, and the headline came from replay.**
The 4-of-8 above was measured by **offline cassette replay**. On a cluster, since the wait bug
([#76](#76)) stopped releasing before c12 had finished investigating, c12 has been acted on **0 of
4** runs. Those are the only cluster measurements worth counting; everything earlier was taken
with an instrument that could report a decline the planner never made.

So there are two numbers and they disagree, which is itself the finding: replay and a live cluster
are not measuring the same thing, and the corpus is the one that has been quoted. Nothing should
claim an action rate until they are reconciled.

**Size.** M. **Blocks:** claiming an action rate, which the website and the README both want to
do.

---

**Reconciled 2026-09-02, and the paragraph above is wrong.** Replay and the cluster were measuring
the same thing all along. What differed was the **model**, and this entry compared a DeepSeek
replay number against a gpt-oss cluster number.

Every c12 measurement in `results/`, grouped by the model that produced it:

| model | n | acted | breakdown |
|---|---|---|---|
| `deepseek-v4-flash` | 8 | **4** | 4 `Reasonable`, 4 `MissedAnAction` |
| `gpt-oss:120b` | 18 | **0** | 10 `MissedAnAction`, 8 `NoPlan` |
| `gemini-3.7-flash` | 3 | **0** | 2 `MissedAnAction`, 1 `NoPlan` |

Fisher's exact, DeepSeek against gpt-oss: **p = 0.0047**.

The headline "4 times in 8" *is* the DeepSeek arm — all of it, exactly. The cluster's "0 of 4" was
gpt-oss, because the e2e gate runs the free local model. gpt-oss is **0 of 18** on replay, so the
two instruments agree precisely once the model is held fixed. There was never an
instrument disagreement to reconcile; there was a per-model property recorded as the agent's.

**That makes this the third instance of one failure mode**, and it is worth naming as a pattern
rather than a coincidence: [#55](#55) is the corpus grading the model that recorded it,
[#74](#74) is ceilings tuned for one model binding 45x earlier on another, and this is an action
rate. Each time, a number that belongs to a model was written down as a number that belongs to the
agent.

**What replaces the claim.** There is no such thing as "the action rate" for this agent. On c12,
`deepseek-v4-flash` proposes an acceptable action about half the time and `gpt-oss:120b`
essentially never does — and 0 of 18 is not a small sample. Any published figure must name the
model beside it, in the same sentence, the way every cost figure in this repository already does.

**A consequence worth stating separately, because it changes what can be tested.**
[#72](#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever) needs a cluster run
in which the agent actually acts. Driven by `gpt-oss:120b` that run cannot exist: the planner
proposes nothing, so nothing executes, so nothing verifies. Confirming #72 requires a model that
acts, which is why it stayed unconfirmed through a milestone whose gate had just been moved onto
the free local model to save money.

**Still open, and narrower than before:** *why* gpt-oss declines. The mechanism is visible in the
plan it does produce — `noActionRequired: true`, with a summary reasoning that the lease "is stored
on a PersistentVolumeClaim, which persists across pod restarts". That is our own planning prompt
talking back: it lists "the contents of a PersistentVolumeClaim" as state a replacement pod
reproduces exactly. For c12 that rule is wrong, because the hinge is the pod's *name* rather than
the stored bytes — the fixture's own comment says "different for a replacement pod. That is the
whole hinge." The model reads the rule correctly and applies it to a case the rule does not fit.

Not the confidence gate (0.95 on a declining run) and not the step budget
(`terminationReason: Concluded`), which are hypotheses 1 and 2 above; both are now ruled out with
data rather than argued.

**And the prompt is not the cause either — tested, and it is a null result.** The obvious fix was
to correct that rule: ask "would a replacement pod hit the same failure?" rather than "where does
the state live", remove PersistentVolumeClaim from the reproduce-exactly list, and say explicitly
that persisted state comparing a *previous pod's identity* against the current one does not
reproduce for a replacement. Twelve replays with that prompt against twelve without, same model,
same ceilings, same endpoint, one variable:

| arm | concluded | acted | rate |
|---|---|---|---|
| baseline | 9/12 | 0 | 0% |
| reworded planning prompt | 9/12 | 1 | 11% |

Fisher's exact **p = 1.0**. Root-cause accuracy moved 10/12 to 8/12, also noise, also not an
improvement. **The change was reverted** rather than kept: a longer prompt that moves nothing is a
cost with no benefit, and keeping it would be the unevidenced change this file exists to prevent.

What the arm does establish is where the association lives. The one run that acted is the shortest
hypothesis of the twelve and the only one that never mentions the volume at all; every declining
run reasons from "the PVC persists". Since removing that line from the prompt did not stop the
model reaching for it, **the association is in the model rather than only in our wording** — which
is the first explanation offered so far that accounts for four prompt arms failing in a row (#41's
three, and this one).

That reframes the remaining work. Prompt engineering has now been tried four times and measured
four times; the next thing worth trying is not a fifth wording.

---

**Corrected again 2026-09-02, and this time the instrument was the larger half.** Three defects
were found between the model and this number, each of which routes a run into `NoPlan` without
the planner ever being invoked: the `conclude` schema wrapper that gpt-oss fails on **every**
first attempt ([#86](#86)), a `confidence` field read from one of the two places it is offered
([#87](#87)), and an input-token ceiling never co-tuned with the step ceiling it sits beside
([#82](#82)). [#88](#88) is the pooling itself: `NoPlan` holds four different outcomes and this
entry read all of them as declines.

Nine of the 24 gpt-oss runs behind "0 of 18" terminated on a budget before phase 2 existed.
DeepSeek has never hit either ceiling here, because it concludes in 4-7 steps against
gpt-oss's 15-18 - and #86's forced retry is a large part of that gap. The two arms were also
run with different `PlanningStructuredOutput` settings, so the comparison moved a model and a
provider capability at once.

With all three fixed, twelve replays put the planner in front of the incident **11 times out of
12** rather than 5, and root-cause accuracy rose with it, 5/12 to 11/12 (p = 0.027). Acting,
now counted over runs where the planner ran, is 1 of 11 against DeepSeek's 4 of 8: **p = 0.11**.
The **p = 0.0047** this entry leads with does not survive counting the denominator honestly.

**So the sentence "gpt-oss essentially never acts" was over-stated, and the mechanism paragraph
above was right.** Both things are true. The residual difference is real in direction and no
longer significant in size, and the mechanism it points at is unchanged and now measured twice:
across every c12 hypothesis on record, **whether a run acts is predicted by whether the phase-1
hypothesis says a replacement pod would get a different name.** Within DeepSeek, holding the
model fixed, that is a perfect split - 4 of 4 acting runs contain the clause, 0 of 4 declining
runs do (p = 0.029). gpt-oss has now written it 0 times in 38 hypotheses, and its two acting
runs are the two that never mention the volume at all.

**Which points somewhere nobody has looked.** All four prompt arms - #41's three and this
entry's one - rewrote `30-planning.md`, the phase 2 prompt. The information is lost in phase 1.
`20-output-contract.md` asks for a hypothesis "in one or two plain sentences"; gpt-oss obeys it
at a median 205 characters, DeepSeek ignores it at 496, and the clause that decides the outcome
does not fit in one sentence. Phase 2 has no tools and cannot recover what phase 1 declined to
write down. The answer key for c12 already treats that clause as part of the correct root cause
- *"so any replacement pod has a different name and starts cleanly"* - and `MustMentionAnyOf` is
loose enough to grade a hypothesis `Correct` without it.

That is a fifth prompt arm, but it is the first one aimed at the phase that loses the
information rather than the phase that visibly declines.

**It was run, and the clause turned out to be a correlate rather than a cause** - see
[#89](#89). Asking phase 1 the replacement question induced the clause in 8 of 9 hypotheses,
up from 0 of 38, and the action rate went to **0 of 9**. What the arm bought instead is the
mechanism, because it turned a silent omission into a written wrong answer: of the nine, only
**two** answered correctly that a replacement would start cleanly, and both of those declined
anyway. The other seven reason that the lease file persists, therefore the failure persists,
without carrying through that the comparison is against the pod's own name and the name is the
half that changes.

So the remaining work is not a sixth wording either. It is a model that completes the
inference, or a corpus that does not rest its only acting measurement on a fixture this one
gets wrong 7 times in 9.

**Closed 2026-09-03, on the second of those two disjuncts.** [c13](#90) is a corpus that does not
rest its only acting measurement on c12. That was argued in `c13-wedged-lock.yaml`'s header and in
`AnswerKey.cs` *before* any result was known, which is the standard this corpus is held to - the
fixture was not chosen after seeing which way it went.

**What the entry was blocking, and what may now be said.** Three numbers, never averaged:

| fixture | model | runs where the planner ran | proposed an action |
|---|---|---|---|
| `c13-wedged-lock` | `gpt-oss:120b` | 6 | **6** |
| `c12-stale-lease` | `gpt-oss:120b` | 11 | **1** |
| `c12-stale-lease` | `deepseek-v4-flash` | 8 | **4** |

Plus, end to end and once: a `RestartPod` proposed, admitted, executed, the workload available in
16s and the incident `Resolved` 41 seconds later. c13 measures **willingness to act**; c12 measures
an **inference**. Averaging them recreates the conflation c13 was added to end.

**Three things had to land before this could close, and they are the reason it took a milestone
rather than a wording change:**

- **[#88](#88)'s reporting split.** The denominator now means what it says. `PlannerNeverRan` is
  separate from a decline, so a run cut off by a token budget is no longer counted as the agent
  choosing not to act - which is what produced the published `p = 0.0047` over 24 runs, nine of
  which never reached phase 2.
- **The `judge.sh` gap.** c13 had an answer key and no `fixture_truth` arm, and the guard above the
  loop dropped it with no line printed. The release gate was scoring a denominator that silently
  omitted the fixture this entry now closes on. Fixed, and a test now asserts the two graders agree.
- **[#97](#97).** `--full --mode Auto` cannot demonstrate acting at all, because the run's own
  breadth trips the cluster-health gate. Every measurement above comes from focused runs, and the
  procedure now says so.

**What is NOT claimed, and must not be sanded off downstream.** `6 of 6` is a statement about what
the planner **proposed**; runs 3 and 4 of that six were denied by [#92](#92) before executing. End
to end the number is **1 of 1**. And n = 6 supports "usually", not "always" - the exact one-sided
95% lower bound is 0.61.

**The residual limitation, stated rather than left to be found.** c13 has **one instrument and one
model**. c11 and c12 each have a cluster arm and a replay arm; c13 has only the cluster, because
`cassettes/c13.json` does not exist. This entry's own history is two consecutive corrections caused
by trusting a single instrument, so closing it while that is true is worth naming loudly rather
than burying. It is [#101](#101), and it is the first thing to do if any of these numbers is ever
disputed.

**Size.** M. **Status: closed 2026-09-03.** The `Blocks:` field is discharged: the site may state
an action rate, provided it names its fixture, its model and its denominator - which is the same
sentence this entry has been making since its first correction.

---

## The v0.5.0 carry, reviewed 2026-08-31

v0.5.0's rule is that *"an item leaves `backlog.md` by being fixed, or by being reclassified as a
deliberate limitation and written down somewhere permanent. It does not leave by being ignored."*
Its exit criterion asks that every remaining entry be looked at once and given a fresh sentence
saying why it is still there. This is that pass, in one place rather than seventeen, so the shape
of what is being carried is visible at a glance instead of only per-entry.

Seven open entries are not listed here because this milestone already re-argued them in their own
text: [#2](#2), [#55](#55), [#57](#57), [#58](#58), [#59](#59), [#60](#60) and the newly split
[#66](#66).

**Blocked on something outside the repository — carried, not deferred by choice:**

- **[#31](#31) Tempo tools.** Still blocked on grafana-mcp, which exposes no trace tools; nothing
  in this repo can make c10's five-hop trail reachable. The cheap half — making the absence loud
  rather than silent — was not done because c10 now runs in `--full` and reports the gap itself.
- **[#23](#23) NetworkPolicy enforcement.** Unprovable here: kind's CNI accepts the objects and
  ignores them, so the harness's own report names this as not covered on every run. It needs a
  cluster whose CNI enforces, which is a `--enforce-netpol` tier on the Later menu.
- **[#30](#30) The pre-purge `grafana.db` tarball.** A file on one machine, not a state of the
  repository; it closes by someone deleting it, and no code change can.

**Correct as designed; the entry exists so the tradeoff stays visible:**

- **[#24](#24) `values-dev.yaml` opens the webhook CIDR.** Still the price of kubelet probes on a
  single-tenant node, and still recorded rather than fixed, because fixing it properly is the
  same work as [#23](#23).
- **[#25](#25) The orphaned `data-postgres-0` PVC.** Deliberately left after the data was dumped
  and restored; it is one `kubectl delete` whenever the dev cluster is next rebuilt.
- **[#29](#29) The `CS0618` suppression.** KubernetesClient 19 still ships no replacement for the
  typed watch operations, so the suppression is still the only option and still deliberate.
- **[#34](#34) c1 produces `CrashLoopBackOff`, not `OomKilled`.** Unchanged and now *measured
  rather than argued*: c1 runs in `--full`, and its answer key still expects the kind the node
  cannot produce, so the disagreement shows up as a graded result instead of a footnote.

**Real, small, and simply not reached — the honest category:**

- **[#15](#15) Duplicate instrument registrations.** Two meters still disagree on type and unit.
  Untouched this milestone; it costs a dashboard reading wrongly, not an agent behaving wrongly.
- **[#21](#21) The stale "workflows have never run" line.** The correction is recorded; what
  remains is the wider docs restructure it was filed under, which is L and was never in scope.
- **[#22](#22) Budget values are write-only.** `extraEnv` still works and the harness still relies
  on it, so this is discoverability rather than capability — and this milestone leaned on it
  again, in `deploy.sh`, which is an argument for promoting them rather than against.
- **[#26](#26) The `k8s_events` receiver duplicates on a second node.** Still one `kubectl` away
  from being real, and still not real, because this cluster has one node.
- **[#27](#27) `AddHephaistoLlmWithoutPersistence` has no call sites.** Still dead, and now
  slightly more so: the provider seam grew a second implementation without anything needing it.
- **[#28](#28) `list_alert_rules` returns empty.** The prompt caveat still carries it. Worth
  noting the caveat is now read by two model families rather than one, and neither has tripped
  over it.
- **[#42](#42) Workload-shaped verification predicates.** Untouched, and still gated behind the
  Job action types being proposable at all, which is [#39](#39).
- **[#44](#44) Nothing sweeps `AwaitingApproval`.** Untouched. It is first in the Later menu's own
  ordering for interactive approvals, so it is queued rather than forgotten.
- **[#49](#49) The console spec's capped comparison.** Untouched; it cannot fire below 100 open
  incidents, and the e2e console suite runs against a cluster with ten.

**Excluded by this release's own rule:**

- **[#39](#39) Three refused action types.** `PatchResources` is capability, and this release
  said no new capability ships in it. It is named here because it is the reason c4 and c7 are
  permanently "diagnose correctly, propose nothing" — the corpus cannot grade the action that is
  actually right for them.

### 67. A missing line in a secrets file aborted the run before it probed anything

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** The first `--full` run ever attempted died 8 minutes in, immediately after
`bootstrapping secrets`, with **no output at all** and exit 1. The report read `ABORTED`, `13
assertions passed`, `fixtures none`, and — misleadingly — *"Investigations did not run at all: no
model was reachable"*, when the model was reachable and had never been asked.

**Cause.** `deps_secrets` resolves a key from `secrets/hephaisto-llm.secret.yaml` with

```sh
from_file=$(grep -oE 'LLM_API_KEY:...' "$llm_secret" | head -1 | sed ... | tr -d ...)
```

That file carries `GEMINI_API_KEY` and no `LLM_API_KEY`, so `grep` exits 1, `set -o pipefail`
propagates it to the assignment, and `set -Eeuo pipefail` kills the function. Silently: the abort
happens before the first `say` in that branch. The same shape sits in the Gemini arm and was only
ever dormant because that key was usually already exported.

**Why it matters** is that it is invisible and it is on the new path. Selecting a local model was
the one configuration that reached this branch with the key absent, so the feature added to make
the gate affordable could not run — and it failed as a *silence*, not as an error.

**Fix.** `|| true` on both pipelines: a file with no matching line means "no key here", not "stop".

A sweep for `[ cond ] && var=value` found three more instances, which were rewritten as `if`
blocks for clarity. **The claim first recorded here — that they would abort — was wrong, and is
corrected.** Tested directly afterwards: under `set -Eeuo pipefail`, a failing `[ ] && assign`
*mid-function* does not abort, because `set -e` ignores the non-final commands of an AND-OR list;
it aborts only as a function's **last** statement, where the failing status becomes the function's
return value. So those three were harmless and the rewrites are readability, not fixes.

The hazard in this entry is real and is narrower than first written: it is the **command
substitution assignment**, `var=$(pipeline)`, which does abort — demonstrated in isolation before
the fix and again after. The lesson worth keeping is the one about testing the claim rather than
the pattern: a sweep found by shape, and half of what it found was not the thing.

**Size.** S to fix, and the reason it is written down is the class rather than the instance: this
is the third time this repo has been bitten by `set -e` turning a false test into a dead run.

### 68. A stack check that passed only while the cluster was unhealthy

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `deps_verify` failed with *"no `kube_pod_container_status_waiting_reason` series"* on
a cluster where every single pod was `Running`.

**Cause.** That metric only has series while a container is **actually waiting**. The assertion
therefore passes when something is broken and fails when nothing is — the inverse of what it
reads as. It survived because a freshly created kind cluster always has something in
`ContainerCreating` when deps runs, so the check had never been asked the question on a warm
cluster. The first run to reuse one failed it, and the run before that had passed it only because
`grafana-mcp` happened to be stuck in `CreateContainerConfigError`.

**Why it matters** beyond the false failure: the check's stated purpose is *"kube-state-metrics is
producing workload state"*, and it was not measuring that. A green result meant "KSM is up **and**
something is currently broken", which is two claims welded together, one of them accidental.

**Fix.** Query `kube_pod_status_phase`, which carries one series per pod at all times and comes
from the same exporter. Same question, answerable without requiring a fault to exist.

**Size.** S.

**Fixed 2026-08-31.**

### 69. The in-cluster model probe read a healthy endpoint as unreachable

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** `the cluster cannot reach http://…:11434/v1 (HTTP 200200)`. The endpoint was up, the
pod could reach it, and the check said it could not.

**Cause.** `kubectl run --rm -i` can emit the container's output twice — once from the attach
stream, once when it collects the final logs — and `curl -w '%{http_code}'` writes no trailing
newline, so two successes concatenate into `200200`, which is not `200`.

**Why it matters** is the direction of the error. This check exists to fail a run early rather than
let it discover an unreachable model forty minutes in ([#62](#62)), so a **false negative** in it
is worse than not having it: it blocks a configuration that works, and it blames the thing that is
fine. A guard that cries wolf gets deleted, and then #62 comes back.

**Fix.** Keep the last three digits of whatever comes back, which is the status code however many
times kubectl chose to print it. Verified against `200200`, `200`, `404404`, `000` and empty.

**Size.** S.

**Fixed 2026-08-31.**

### 70. A generic rule with a shorter `for:` permanently mislabels two fixtures

**Symptom.** In the first full-corpus run, 7 of 10 fixtures classified correctly and two were
wrong in the same direction:

```
skip  c3 classified as ReadinessFlapping, expected Unschedulable
skip  c4 classified as ReadinessFlapping, expected ImagePullBackOff
```

(The third, c1 → `CrashLoopBackOff`, is [#34](#34) and expected on this node.)

**Cause.** `charts/hephaisto/files/alerts/kubernetes-rules.yaml` carries a "pod has been
non-ready for >2m" rule whose expression is

```promql
kube_pod_status_phase{phase=~"Pending|Unknown", namespace=~"hephaisto.*"}
```

labelled `hephaisto_kind: ReadinessFlapping`. Its own comment says *"Pending is precisely the
state the C3 unschedulable fixture sits in, and it must alert"* — so the rule was written
knowing it catches C3, and then labels it as flapping. An ImagePullBackOff pod is also `Pending`,
so it takes C4 too.

**The label contradicts the rule.** Flapping means intermittent; this fires on a pod that is
*persistently* stuck. The genuine flap detector is `TargetFlapping` further down the same file,
whose description says in as many words *"This is INTERMITTENT, not down."* Two rules share one
`hephaisto_kind` and only one of them means it.

**Why it did not show up before.** c3 and c4 are both in the default four-fixture set, so this
path has been exercised for months. What changed is timing: with ten fixtures and seventeen
incidents the generic rule's `for: 2m` beats the specific `ChaosPodUnschedulable` and
`ChaosImagePullFailure` rules, opens the incident first, and classification is first-rule-wins.
**So the real finding is not the label, it is that a race decides an incident's kind** — and load
changes who wins. That is why a full-corpus run finds things a four-fixture one cannot.

**Consequence.** `SignalKind` drives runbook selection, so c3 and c4 investigations are handed the
ReadinessFlapping runbook. That is wrong information, and only accidentally harmless: the flap
runbook says restarting will not help, which happens to be true for both.

**Deliberately not fixed in v0.5.0.** Changing what an alert rule means is a detection-semantics
change, it is not what this milestone is for, and the harness records these as `skip` rather than
`fail`, so the gate is not blocked on it. Fixing it wants a decision about whether the generic
rule should carry a distinct kind (`Unschedulable`, or a new `PodNotReady`) and whether
classification should prefer the most specific matching rule rather than the first.

**Size.** M — S for the label, M for the first-rule-wins question behind it.

### 71. An auto-executed action leaves `ApprovedBy` null, against its own documented invariant

**Symptom.** The first `--mode Auto` run ever observed on a cluster executed one `RestartPod`
against `c12-stale-lease` and the harness failed it:

```
1 action(s) executed in Auto mode; containment assertion does not apply
FAIL  1 approved action(s) have no approvedBy
```

The record is otherwise complete and correct:

```json
"type": "RestartPod", "risk": "Low", "decision": "Allow",
"decisionReasons": ["RestartPod is a low-risk, single-object action"],
"dryRun": false, "modeAtExecution": "Auto", "outcome": "applied",
"approvedBy": null, "approvalSource": "NotApplicable"
```

**Cause.** `ApprovedBy` is never populated for an auto-executed action. Three places in `Core`
say it should be: `ActionPlan.cs:76` — *"Always populated, including for automatic actions
(`hephaisto/auto`)"*; `Enums.cs:204` — *"Executed by policy under L3 autonomy. ApprovedBy is
`hephaisto/auto`"*; and `Audit.cs:27` names `hephaisto/auto` as one of the three actor forms.

**What is NOT wrong, and should be said first.** The audit trail is intact.
`ActionExecutor.cs:316` and `ActionRepository.cs:429` both write
`action.ApprovedBy ?? "hephaisto/auto"`, so the audit row names the actor and *"who did this"*
is answerable. This is not "no audit, no action" being violated — it is the **action projection**
contradicting its documented invariant while the audit record behind it is correct.

**Why it matters anyway.** Two write sites independently coalesce the same default, which is the
shape of a defaulted-at-the-edges invariant rather than an enforced one: a third consumer that
forgets the `??` reads null and reports an unattributed action. `Enums.cs:181` states the goal
plainly — *"so 'who did this' is answerable uniformly"* — and uniformly is exactly what it is not.

**Why it took until now.** Nothing had ever executed an action on a cluster. The assertion has
existed since v0.2.0 and had no subject to run against, so an invariant three doc comments assert
was never once checked against behaviour. This is the single clearest argument for the `--mode
Auto` run that has been outstanding across three releases.

**Fix.** Set `ApprovedBy` to `hephaisto/auto` where the policy engine admits an action under L3,
so the two `??` coalesces become redundant rather than load-bearing, and delete them or leave
them as belt-and-braces deliberately.

**Fixed, and the entry did not land with the fix.**
`InvestigationCoordinator.cs` sets `ApprovedBy = IncidentStateMachine.AutoActor`,
`ApprovalSource.Auto` and `ApprovedAt` on the `PolicyDecision.Allow` arm, where the claim
becomes true rather than at the edges that read it. Confirmed on a cluster by [#72](#72)'s run:
`ok every executed action names an approver`.

Worth recording that this is the repo's own convention broken - a fix and its backlog entry are
supposed to land in the same commit, and this one shipped in code and stayed open on paper until
the v0.6.0 sweep found it. An entry that is fixed but unmarked is worse than one that is open:
it makes the carry review count work that is already done.

**Size.** S. **Fixed 2026-09-02, marked 2026-09-03.**

### 72. An incident that was successfully acted on sits in `Verifying` forever

**Symptom.** The first `--mode Auto` cluster run executed a `RestartPod` against `c12`, the
restart worked, and the harness still failed:

```
FAIL  c12's incident did not reach Resolved
      the workload recovered but verification never closed the incident
```

Queried well after the run, the incident is still `state=Verifying`, `escalationReason=None` —
not escalated, not resolved, not stuck on a budget. Just open.

**Cause — corrected after looking rather than pattern-matching.** The first draft of this entry
said `Verifying` has no sweeper. **That was wrong.** `VerificationScheduler` exists, is registered
as a hosted service, polls every 10 seconds, and calls `Resolve` with `hephaisto/verifier`.

What actually happens is a timing mismatch. `VerificationSchedule.Delays` is **60s, 5m, 15m**.
`chaos_assert_verification` waits 240s, and says why in its own comment: *"4 minutes covers the
T+60s check plus scheduler poll … and stops short of the T+5m second attempt - if the first check
did not settle it, waiting for the second would be measuring something else."* So the harness
gives verification **one** attempt out of three.

c12's recovery is a pod deletion, a recreation, `minReadySeconds` (added for [#41](#41)) and the
app's own startup. That can exceed T+60s, and when it does the first check finds the workload not
yet settled — so the harness reports "verification never closed the incident" while the second
and third attempts have not happened yet. The observed run failed the assertion at 21:34:11 with
the final scheduled check due at 21:34:10: **one second.**

**The third version, and this one is an app bug.** With the wait corrected, a run on 2026-09-01
executed the restart, `c12 is available after the restart` **passed** — the workload recovered —
and the incident still did not resolve. All three verification attempts ran, and all three said
the same thing:

```
Verification 1 of RestartPod on .../c12-stale-lease-...: Inconclusive - no health predicate for a Pod
Verification 2 ... Inconclusive - no health predicate for a Pod
Verification 3 ... Inconclusive - no health predicate for a Pod
```

**Cause.** `WorkloadIsHealthyAsync` prefers the owner and falls back to the target's own kind:

```csharp
var kind = target.OwnerKind is { Length: > 0 } ok ? ok : target.Kind;
```

A `RestartPod` targets a **Pod**, and the switch has no `Pod` case — nor could it usefully have
one, since the action deletes the pod it names, so checking that name could never succeed. The
answer has to be the owning workload, which is why `TargetRef`'s own summary calls
`OwnerKind`/`OwnerName` *"the important fields"*.

They were null. The **incident** carried `ownerKind: Deployment, ownerName: c12-stale-lease`; the
**action**, built from the model's plan in `ActionPlanDraftMapper`, carried neither — the model
names a namespace, a kind and a name, and has no way to know what owns the object.

**So no `RestartPod` has ever been verifiable.** It is the only action type in
`autoEnabledActionTypes`, which means the sole action the agent can take unattended is the one
whose incident can never close. Every earlier "verification never closed the incident" was this,
wearing two different disguises.

**Fix.** The action inherits the incident's owner when it names the same object — and only then,
because an owner copied onto a different object would send verification to look at an unrelated
workload and call the result an answer. Three tests pin both directions.

**Why it matters** is what it does to the claim. v0.2.0's acceptance criterion is that the agent
can act; this run proved it can diagnose, plan, get admitted by policy, write to the cluster and
*fix the fault*. Then the loop does not close. An operator watching the console sees a fixed
workload and an open incident, which is precisely the state that makes people stop trusting an
automation and go look themselves — and it means "the agent resolved it" is still not a sentence
this project can say.

It also silently weakens the safety story in the other direction: `VerificationChecks` exists so
an action that did *not* work gets noticed. A verification path that never concludes cannot
distinguish "it worked" from "it did not", because it reports neither.

**Not the same as [#42](#42).** That entry is about the predicate being workload-shaped for two
action types. This is about how long the instrument waits before calling the predicate wrong.

**Why it took until now.** Nothing had ever executed an action on a cluster, so the
post-execution half of the state machine had never run outside unit tests.

**Size.** S for the harness wait, S for the app fix — the diagnosis was the work, and it took
three passes because the first two blamed the layer in front of the real one.

**Blocks:** claiming the agent resolves incidents, which is the natural next sentence after "the
agent can act". **Status: fixed 2026-09-01, pending a cluster run to confirm.**

**Why the confirming run still has not happened, stated precisely 2026-09-02.** It needs a
cluster run in which the agent actually executes something, and the only model this machine may
drive is the local one - DeepSeek and Gemini both cost real money. Three instrument defects that
were suppressing the acting path have since been fixed ([#82](#82), [#86](#86), [#87](#87)), and
they moved the odds materially in the right direction: on c12 replay the planner is now invoked
11 times in 12 rather than 5, so an e2e run at least reaches the decision it is meant to test.

What they did not move is the decision itself. `gpt-oss:120b` proposes an acceptable action on
c12 about 1 run in 11, and [#89](#89) established why: asked directly whether a replacement pod
would fail the same way, it answers wrongly 7 times in 9. At that rate a confirming e2e run is
roughly a one-in-eleven event costing ~25 minutes of the single shared cluster each attempt, so
it is not something to sit and retry - and retrying until it lands would be selecting the run
that agrees with us, which is the habit this file exists to prevent.

So the honest status is unchanged and now has a number attached: **the app fix is pinned by
three unit tests and has never been observed end to end**, and it stays that way until either a
model that completes the inference is available for a cluster run, or a fixture whose acting
case does not depend on it exists. Both are #66's remaining work, not this entry's.

---

**The fourth cause, found 2026-09-02, and this one is the actual one.** The fixture arrived
([#90](#90)): c13 puts the wedge on an emptyDir, `gpt-oss:120b` acted on it on the first
attempt and again on the second, and the confirming run could finally be made to happen on
demand instead of waited for. It failed, at exactly this assertion, with exactly this message.

Everything in front of it was working. The executed action carried
`ownerKind: Deployment, ownerName: c13-wedged-lock`, so the owner inheritance above is doing its
job; the workload recovered in 16s; and the agent's own log says the check **passed**:

```
Verification 1 of RestartPod on hephaisto-chaos/Deployment/c13-wedged-lock:
  Passed - Deployment/c13-wedged-lock is settled with 1/1 ready and no container waiting
```

And immediately after it, swallowed:

```
Npgsql.PostgresException 22P02: invalid input syntax for type json
DETAIL: Token "Deployment" is invalid.
```

**Cause.** `AuditEvent.Detail` is a `jsonb` column. `VerificationScheduler` assigned
`result.Detail` to it raw - a sentence beginning *"Deployment/c13-wedged-lock is settled"* - and
it is the **only one of the ten `new AuditEvent` sites in the agent that does not serialise**.
Every other one calls `JsonSerializer.Serialize`.

**Why that stops the incident closing rather than just losing a log line.** The audit row is
written in the same transaction as the transition it describes, deliberately, because that is
what makes "no audit, no action" true. So the rejected insert rolls back the transition to
`Resolved` - and it rolls back the verification row's own `Outcome` too, since that is the same
`SaveChanges`. The next poll therefore finds the same verification still `Pending` and still
due, runs it, passes again, and fails to save again. Every ten seconds, indefinitely.

That is the whole symptom: `state=Verifying`, `escalationReason=None`, a healthy workload, and
nothing in the incident to say why. `PollAsync`'s catch-all - *"The loop must outlive any single
bad tick"* - is what keeps the error out of sight, and it is right to exist; what was missing is
that a persistent failure there is indistinguishable from a scheduler that is simply waiting.

**It also explains [#11](#11).** "There is no production path to `Resolved`" was written before
`VerificationScheduler` existed and was believed fixed when it landed. The path was built and
has never once been able to complete, because `incident.resolved` is the only audit type this
bug can reach - the escalate path writes no audit event, so a *failing* verification would have
closed the incident fine.

**Why three passes missed it.** Each earlier diagnosis was looking at a real defect that was
genuinely in front of this one, and fixing each moved the failure one layer deeper while leaving
the symptom identical. The wait was too short; then the action had no owner; then the owner was
right and the write failed. None of them could be told apart from the outside, because all three
present as an incident that sits in `Verifying` for ever.

**What could have caught it.** Nothing in the unit suite - the in-memory provider has no column
types. Nothing in the e2e either, until a fixture existed that the agent would reliably act on.
`AuditDetailIsJsonTests` runs against the real schema and reproduces the `22P02` directly.

**Fix.** The call site serialises, and carries the structured `checks` alongside the sentence
because they were computed anyway and are the evidence for the closure. `AuditRepository.Enlist`
also wraps a non-JSON detail rather than letting Postgres reject the transaction - the same
argument as the timestamp it already normalises there, and load-bearing for a different reason:
an audit write that can fail is a state change that can fail.

**Size.** S, after an M of diagnosis and a fixture.

**Confirmed on a cluster 2026-09-02, which is what this entry has been waiting for since
v0.2.0.** A clean kind cluster, `0.5.1-main.0.34`, `--mode Auto`, driven by the free local
`gpt-oss:120b`:

```
ok  c13 was acted on (1 non-dry-run action(s) executed)
ok  every executed action names an approver
ok  c13 is available after the restart
ok  c13's incident reached Resolved            (41s)

PASSED -- 70 assertions, 4 skipped
```

and the incident itself:

```
state: Resolved | resolvedAt: 2026-09-02T16:02:24 | escalation: None
Detected -> Triaging -> Investigating -> Acting -> Verifying -> Resolved
  (granted by hephaisto/verifier)
resolution: "Deployment/c13-wedged-lock is settled with 1/1 ready and no container waiting"
```

**So "the agent resolved it" is a sentence this project can now say**, with the model named
beside it the way [#66](#66-the-planner-acts-on-half-of-a-fair-fixture) requires: on c13,
`gpt-oss:120b` proposed an acceptable action on six of six cluster runs. That is c13's number
and not c12's, and the two are different questions - see [#90](#90).

**It took five layers, and four of them were real.** The harness wait, the missing action owner,
the jsonb audit detail ([#72](#72) proper), the `instance`-as-node label ([#92](#92)), and
finally an assertion that could never pass ([#93](#93)). Every one presented identically: a
healthy workload, an incident in `Verifying` or refused, and no reason recorded anywhere. The
lesson worth keeping is the last one - the assertion agreed with four genuine bugs in a row, so
nothing ever questioned the assertion.

**Status: fixed and confirmed 2026-09-02. Closed.**

### 73. Every red run was reported as ABORTED, including the ones that finished

**Status: fixed 2026-08-31** — see the end of this entry.

**Symptom.** A run that completed all ten phases and recorded four failures printed:

```
ABORTED -- the run exited 1 before finishing.
77 assertions passed before it stopped; the rest never ran.
```

Nothing stopped, and everything ran.

**Cause.** `run.sh` ends with `[ "$FAILED" -eq 0 ] || exit 1`, so a completed-but-red run exits
non-zero — and `report_render` tested `aborted != 0` first. The `FAILED` branch below it was
therefore **unreachable on the normal path**, and the three outcomes collapsed to two.

**Why it matters.** This file's own comment explains the intent: *"Three outcomes, not two.
'Nothing recorded a failure' is not the same as 'everything was checked': a run that died before
its last phase has an empty failure list and a perfectly clean tally, and calling that PASSED is
how a release gate lies."* The implementation inverted it — it never lied about a pass, it lied
about a **failure**, telling the reader that assertions had not run when they all had. On a
release gate, "we never checked" and "we checked and it failed" are different decisions.

**Fix, first attempt.** `run.sh` sets `RUN_COMPLETED=1` on the only line that proves it reached
the end, and ABORTED required a non-zero exit **and** not having completed.

**That was still wrong, and a killed run proved it the next day.** Stopping a run during its act
phase produced `PASSED -- 61 assertions`: a signal delivered while the script sits in a wait can
leave `$?` at zero, so the exit-code half of the condition was false and it fell through to
`PASSED`. The same false green, one door over.

**Fix, corrected.** Completion is the fact; the exit code is secondary. ABORTED now depends on
`RUN_COMPLETED` alone — a run that did not reach its last line has not been fully checked,
whatever it exited with. Verified across four cases: killed at exit 0 → `ABORTED`, killed at 143
→ `ABORTED`, completed-with-failures → `FAILED`, completed-clean → `PASSED`.

**Size.** S, twice.

**Fixed 2026-08-31, corrected 2026-09-01.**

### 74. The token ceiling and the cost ceiling are calibrated for price points 45x apart

**Symptom.** The first full `--mode Auto` run refused **14 of 27** investigations outright —
`TerminationReason.Cancelled`, incidents escalated `BudgetExhausted` — having spent **$0.066**
against a **$3.00** hourly cost cap. Half the corpus was declined on a budget that was 2% used.

**Cause.** `LlmBudgetOptions` has two independent hourly ceilings:

```csharp
public long MaxTokensPerHour { get; set; } = 2_000_000;
public decimal MaxCostUsdPerHour { get; set; } = 3.00m;
```

At `gemini-3.7-flash`'s $0.75/1M those are roughly commensurate — 2M tokens is about $1.50, so
either could bind and the cost cap usually does. At `gpt-oss-120b`'s $0.03/1M, 2M tokens is about
**six cents**, and the cost cap of $3.00 corresponds to ~100M tokens. The token ceiling therefore
binds roughly **45x earlier**, and the observed $0.066 spend is exactly what 2M tokens costs at
that price — the run stopped on tokens and the money was never the point.

**Why it matters.** Switching to a cheaper provider silently changes *which* ceiling governs, and
the one that starts governing was never tuned for the new price. Worse, the failure is not legible
as a budget failure: the operator sees `BudgetExhausted` beside a spend two orders of magnitude
under the cap, so the obvious reading — "the cap is wrong" — is correct while the obvious *cap* is
the wrong one to look at.

It also cost the milestone its headline number. Fourteen refused investigations produce no
findings, so they cannot be graded, and the MVP bar reported `not applicable: 8 scenarios scored,
the bar needs 10`. **Accuracy was 7/8; it was the ceilings that dropped the denominator.**

**Fix, for the harness.** `values-e2e.yaml` raises `MaxTokensPerHour` to 20,000,000, which keeps a
real bound and restores cost as the ceiling that governs at this price point.

**Not fixed, and worth a decision.** The product default is still one number for every model. The
honest options are a token cap derived from the cost cap and the resolved price, or a documented
per-model pair recorded beside the price entry — which is what [#59](#59) already asks for on
`MaxSteps`, for the same underlying reason: **the limits are per-model and the defaults are not.**

**Size.** S for the harness, M for the product default.

### 75. The outbox test reported a durability failure when its own precondition had failed

**Status: fixed 2026-09-01** — see the end of this entry.

**Symptom.** `rc2` failed v0.3.0's acceptance criterion:

```
07:28:03  taking the receiver down (503) and restarting the agent mid-flight
07:30:05  WARN  no refused delivery observed; the restart test may be weaker than intended
          FAIL  a delivery survives an agent restart -- the outbox did not replay
```

The same assertion passed in `rc1` against the same code path.

**Cause.** The test needs a delivery to be *attempted* while the receiver is returning 503, so a
row is genuinely mid-flight. Whether that happens depends on incident activity the test does not
control. In `rc2` the investigations had all concluded by the time the window opened, so nothing
was sent, nothing was refused, and nothing was queued — and an empty outbox cannot replay.

**Why it matters** is the sentence it printed. "The outbox did not replay" is a claim that the
durability guarantee is broken; what actually happened is that the guarantee was never exercised.
On a release gate those are opposite conclusions — one blocks a release, the other means *"we did
not manage to test it"* — and the run had already **said so itself**, one line above, as a
warning it then ignored.

This is the identical mistake `chaos_assert_verification` was written to avoid, and its comment
says why in terms that apply verbatim here: *"They are the first failure restated as downstream
symptoms with confident and incorrect causes attached … A report that is wrong about why is worse
than one that is merely incomplete."*

**It also masked the more useful finding.** The interesting fact about `rc2`'s notify phase is not
the outbox; it is that the system was quiet enough at that moment to have nothing to send — which
is a property of when the window opens relative to the investigation queue.

**Fix.** Capture whether a refusal was observed and `skip` when it was not, naming the unmet
precondition. Also saves the 300s wait for an outcome that was already determined.

**Size.** S.

**Fixed 2026-09-01.**

### 76. The investigation wait counted incidents the fixtures did not cause

**Status: fixed 2026-09-01** — see the end of this entry.

**Symptom.** A two-fixture `--mode Auto` run reported `0 action(s) executed in Auto mode` and
failed `c12 was not acted on`. Queried at that moment, **c12's incident was still
`Investigating`, `hasDiagnosis=false`**. The planner had not declined; it had not yet been asked.

**Cause.** `chaos_await_investigations` waited on

```jq
([.[] | select(.hasDiagnosis)] | length) >= $want
```

— *any* incident carrying a diagnosis, counted against the number of fixtures applied. A cluster
opens incidents the fixtures did not cause: self-signals from the observability stack,
`kube-scheduler`, `coredns`, `local-path-provisioner`. On the observed run the two required
diagnoses were **c2 plus a `kube-scheduler` self-signal**, so the wait returned while the fixture
the act phase asserts about was still running.

**Why it matters** is which conclusion it manufactures. The symptom it produces —
*"0 actions executed"* — is indistinguishable from the planner choosing not to act, and that is
the exact claim this project has spent three releases investigating across [#41](#41) and
[#66](#66). An instrument that can fabricate the finding under study is worse than no instrument,
and this one did it silently, on the phase that carries v0.2.0's acceptance criterion.

**What this does and does not revise.** [#66](#66)'s 4-of-8 action rate was measured by **offline
cassette replay**, which does not use this code path, so that number stands. What is now in
question is every *cluster* observation of "the agent did not act" — rc2's included. Those were
measured with a wait that could return early, and they should not be treated as evidence about
the planner until they have been re-run against the corrected predicate.

**Fix.** Match on fixture target, the same way `chaos_await_incidents` already does, so what is
waited for and what is asserted cannot drift. Verified against the captured incident list from
the failing run: the new predicate reports not-satisfied while c12 lacks a diagnosis, where the
old one was satisfied by the scheduler self-signal.

**Size.** S to fix; the entry is long because of what it invalidates.

**Fixed 2026-09-01.**

### 77. Attributing an auto action woke a dormant cooldown, which refused the action that woke it

**Status: fixed 2026-09-01** — see the end of this entry.

**Symptom.** After [#71](#71) landed, a targeted Auto run planned a `RestartPod` for c12, policy
admitted it (`decision: Allow`, `approvedBy: hephaisto/auto`), and it never executed:

```
Refused RestartPod on hephaisto-chaos/Pod/c12-stale-lease-...:
WorkloadCooldown - workload ... acted on 0s ago; cooldown is 900s
```

**Nothing had executed.** The workload had never been acted on. The action was refused for a
cooldown it had started itself, 0 seconds earlier.

**Cause, and it is self-inflicted.** `ReadBudgetAsync` keys every figure on `ApprovedAt` —
including `LastActionOnWorkloadAt`, the cooldown's input — and `AdmittedStates` includes
`Approved`. #71 set `ApprovedAt` at the moment policy admits an action, so by the time execution
admission ran, the action was in its own budget snapshot.

**What that exposes is worse than the regression.** Auto-approved actions previously left
`ApprovedAt` null, so they contributed to *none* of these counts: `LastActionOnWorkloadAt`,
`ActionsOnWorkloadLastHour`, `ActionsClusterWideLastHour`, `ActionsClusterWideLastDay`. **The
workload cooldown, the per-workload hourly cap and both cluster-wide caps were dormant on the
entire Auto path** — the L3 path, the only one that writes to a cluster without a human. They
looked present in the code, in `values.yaml` and in the console, and never fired. A gate that
cannot fire is worse than an absent one, because nobody goes looking for it.

So #71 did not break the cooldown. It switched four safety gates on for the first time, and the
first thing the newly-live cooldown did was refuse the action that had just populated it.

**Fix.** The action being admitted is excluded from its own budget snapshot. The four gates stay
live — which is the behaviour their configuration has always promised — and no longer count the
candidate as its own precedent.

**Why the harness could not have caught this before.** It needs an action to be admitted *and*
executed on a cluster, which had happened exactly once in the project's history, three hours
before this entry was written.

**Outstanding: there is no regression guard.** The fix is verified end to end by the e2e acting
phase and by nothing else. A unit test cannot reach it — the figures come from an EF query inside
the admission transaction — and the honest home is
`tests/Hephaisto.IntegrationTests`, alongside `WorkloadQuarantineTests`, which already proves a
neighbouring admission property against real Postgres. Two cases are worth pinning: that an
auto-approved action **does** count toward the workload cooldown (the dormancy, which is the
finding), and that the action being admitted **does not** count toward its own (the regression).
Until that exists, four safety gates on the L3 path are covered only by a cluster run.

**Size.** S to fix, S for the guard that is not yet written. The entry is long because the
dormancy is the finding and the regression is only how it surfaced.

**Fixed 2026-09-01; regression guard outstanding.**

### 78. The reserved concluding step was half of what the protocol needs

**Status: fixed 2026-09-01** — see the end of this entry.

**Symptom.** Investigations that hit `MaxSteps` produced **no finding at all**, with the agent
logging its own rescue failing:

```
Investigation budget reached (StepBudgetExhausted); spending the reserved step on a conclusion
  rather than discarding the run.
The reserved concluding step failed. Reporting the original budget termination.
  BudgetExhaustedException: Investigation budget exhausted (StepBudgetExhausted): 21 of 20 steps used
```

**Cause.** The conclusion is taken through the `conclude` **tool** — deliberately, because a model
asked nicely to conclude may answer by calling one more diagnostic tool instead. But a tool call
is **two** model round trips: one where the model emits the call, one where it answers after the
framework has run it. `TryGrantConcludingStep` reserved **one**.

So the first trip was paid for, the second was refused, the tool never returned a value, and the
run reported nothing. The throw's own wording — *"21 of 20 steps used"* — reads as a run
overshooting its budget, which is why it looked like correct enforcement rather than a rescue
being cut in half.

**Why it matters.** This is the mechanism behind [#59](#59)'s central observation, recorded there
as a property of the model: *"10 of 30 gpt-oss runs terminated `StepBudgetExhausted`, and every one
produced no finding."* Every one produced no finding because **the rescue could not land**, not
because the model had nothing to say. A truncated investigation is meant to still report what it
learned; that path has never worked.

It also costs the corpus its denominator. A scenario with no finding cannot be graded, so every
truncated investigation subtracts from the MVP bar's count — which is why three consecutive full
runs scored 7/8, 7/7 and 7/7 against a bar that needs ten. The accuracy was never the problem.

**Fix.** `ConcludingCallAllowance = 2`, named and explained where it is defined, because the
number is the tool-calling protocol rather than a preference. The reservation stays finite: the
call after the allowance still throws, which is what stops "a final turn" becoming "unlimited
turns" when a model declines to conclude. Both existing tests were updated rather than removed —
their intent was right and only their arithmetic assumed a one-trip protocol.

**Size.** S.

**Fixed 2026-09-01.**

### 79. The acting assertion gated on a model's judgement, so it failed half the time

**Status: fixed 2026-09-01** — see the end of this entry.

**Symptom.** `c12 was not acted on` failed on roughly half of otherwise-clean runs. Across six
cluster runs the fixture was acted on twice. The same run could pass or fail with no change to the
code, which is the definition of an assertion that carries no information.

**Cause.** One assertion bundled two unrelated claims:

1. **Did the planner choose to act?** A model judgement. Measured at about 50% on this fixture
   ([#66](#66)), and 0 of 15 on its predecessor c11 ([#41](#41)).
2. **Does the acting machinery work, given a plan?** Deterministic. The executor, admission,
   attribution and verification.

Gating on both means the second — the part that is actually a test — is only exercised when the
first happens to land. And a red result never distinguished "the agent declined" from "the
executor is broken".

**Why it was not simply deleted.** That assertion earned its place three times in one night: the
null approver ([#71](#71)), the workload cooldown refusing an action as its own precedent
([#77](#77)), and verification that could never close ([#72](#72)). All three are machinery, all
three were found by exactly this check. The flakiness was in what it gated on, not in what it
looked at.

**Fix.** Split on a fact already in `details.jsonl` — whether any action was **proposed**:

| proposed | executed | verdict |
|---|---|---|
| 0 | 0 | **skip**, and report the action rate |
| ≥1 | 0 | **fail** — admission or the executor refused a plan the planner made |
| ≥1 | ≥1 | **pass**, then attribution, recovery and closure are gated hard |

The middle row is how #77 presented, so it stays a hard failure. The top row is a model judgement,
and this repo already has a settled convention for those: the root-cause judge *"never gates; the
number is in the summary for a human to read."* The action rate now joins it.

**What this deliberately gives up.** A run where the planner declines no longer proves anything
about the acting path — it reports that it could not test it. That is the honest outcome, and it
is better than a red mark that means "the model shrugged".

**The console suite had the same conflation, fixed the same way.** Two acting specs assert
`no incident in this run produced a plan` as a hard failure, with a comment choosing that
deliberately over a skip on the grounds that *"a phase that tested nothing must not be green"* —
[#1](#1)'s rule. On the first full run to meet the MVP bar, those two specs were the **only**
failure, for the same reason the shell assertion skipped: the planner declined.

[#1](#1)'s actual concern was **silence** — it was filed because the suite reported a PASS on a run
that asserted nothing. A skip that names its unmet precondition is not silent, and #1's own
wording allows it: *"must run in full **or say so**"*. So the specs now `test.skip` with a
`PRECONDITION:` marker, and `ui/run.sh` admits a skip only when every skipped spec carries one —
a bare skip still fails the phase. Verified both ways.

**Size.** S.

**Fixed 2026-09-01.** Verified across all four shapes, and again at the console layer.

---

## Opened by v0.6.0

### 80. Every cassette in the corpus is stale against the shipped prompts

**Symptom.** `hephaisto-eval run --cassettes cassettes` prints a `STALE` warning for **all ten**
cassettes, each with a different pair of hashes:

```
WARNING  c1: prompt sha256:b0fbe56d8f7dc168 STALE - prompts and runbooks now hash sha256:718988bb4e7837f3
WARNING  c4: prompt sha256:bc97facb144dbd6e STALE - prompts and runbooks now hash sha256:2911974534bbde1f
...
```

**Why it matters.** `PromptFingerprint` exists to say when a measurement is comparing against a
prompt that no longer ships, and it is currently saying so about the entire corpus. That does not
invalidate the v0.5.0 numbers — a replay measures the *current* prompt against a recorded tool
trace, which is the point — but it does mean every published accuracy figure was produced against
tool traces gathered under prompts that have since been rewritten, and nothing says how much of
the gap between replay and cluster results ([#66](#66-the-planner-acts-on-half-of-a-fair-fixture))
is that drift.

The differing "now" hashes are the sharper detail: the fingerprint is per fixture because it
includes the runbook a fixture loads, so the corpus has drifted unevenly. Three prompt
improvements shipped in v0.5.0 specifically to change planner behaviour, which is exactly the
behaviour #66 measures.

**Why it was not fixed here.** Re-recording is a cluster job — a kind cluster, ten seeded faults,
and a keyed run per fixture — and v0.6.0 buys no new evidence by doing it. It is also not free of
judgement: re-recording resets the baseline that every prior release's numbers were measured
against, so it should be done deliberately and once, with the before and after stated, rather than
as a side effect of a milestone about documentation.

**What it blocks.** Nothing that ships in v0.6.0. It is a precondition for taking either number in
#66 as final, and for [#55](#55-the-cassette-corpus-grades-the-model-that-recorded-it), since
re-recording is the moment to decide whether the corpus is recorded per model.

**Size.** M, almost entirely cluster time.

### 81. A demo transcript is published evidence, and only its addresses are redacted

**Status: mitigated on 2026-09-01, and recorded because the reasoning should not have to be
rediscovered.**

**Symptom.** `transcripts/*.json` are committed and published. They carry `EvidenceBlob`s, which
are raw untruncated tool output — the same content that keeps `cassettes/` untracked:

> SafeToolDecorator redacts tool *arguments*, not tool *results*, so a cassette's raw
> `describe_pod` and `get_pod_logs` output carries cluster env vars, hostnames and log contents
> verbatim.

**What was actually in them.** Scanned before the first commit. No credential values: the
`secret` and `token` matches are the standard `/var/run/secrets/kubernetes.io/serviceaccount`
mount path and a `serviceAccountToken` projected-volume declaration, neither of which is a
credential. What was present was network layout — pod addresses and the node address of the
machine that recorded them.

**Mitigation.** `TranscriptRedactor`, applied inside `Transcript.Save()` so a transcript cannot
reach disk unscrubbed by a second writer being added later. IPv4 only: no answer key in the corpus
turns on an address, so removing them costs no evidence, while editing the pod specs or the log
lines would turn a recording into a mock-up.

**What this does not cover, and is the reason for the entry.** The redaction is *sound for this
corpus*, not in general. Every fixture is a workload-level fault — a bad image tag, a memory
limit, a missing Secret reference. A future fixture that is about networking, or one whose
workload carries configuration in its environment, would put content in a blob that this does not
remove and that a diagnosis depends on. At that point the answer is to leave that scenario out of
the published set rather than to weaken the redactor, and whoever adds it should read this before
deciding otherwise.

**Size.** n/a — a standing constraint on the corpus rather than a defect.

### 82. The concluding-step rescue cannot land when the ceiling that broke was tokens

**Symptom.** Replaying `c12` twelve times against a local `gpt-oss-120b` with
`Llm:Investigation:MaxSteps=20`, several runs end:

```
TokenBudgetExhausted: 449,776 of 400,000 input tokens used
The reserved concluding step failed. Reporting the original budget termination.
```

The run produces **no finding**, so it cannot be graded, so it drops out of the denominator —
which is exactly the failure [#78](#78) fixed for the step ceiling and which
[#59](#59) recorded as "every one produced no finding".

**Cause.** `TryGrantConcludingStep` grants `ConcludingCallAllowance = 2` calls, and
`EnsureCanStartStep` lets those bypass the breach check. Two is the right number for the happy
path and the entry above says why: the conclusion goes through the `conclude` **tool**, and a tool
call is two round trips by protocol. What it does not cover is the model not calling the tool
cleanly on its first attempt — a text reply, a malformed call, a retry — after which the third
round trip finds the allowance spent and the breach still latched.

It is worse for tokens than for steps. A concluding call resends the whole conversation, so the
one dimension that is already over its ceiling is the one the rescue has to spend again to work.

**The rescue is not broken, it is a coin flip**, and that is the part worth stating precisely. In
the same twelve-run baseline, one token-exhausted investigation concluded and graded `correct`
while others did not: the difference is only whether the model spent its two granted turns on a
clean `conclude` call. So the symptom is intermittent, which is exactly the shape that gets
attributed to the model rather than to a ceiling.

**Why it was found now.** Raising `MaxSteps` to 20, which is [#59](#59)'s fix and what
`values-e2e.yaml` ships, buys more round trips — and every round trip resends the transcript, so
input tokens accumulate faster than linearly. **Fixing the step ceiling moved the binding
constraint to the token ceiling.** That is [#74](#74)'s thesis one level up: its lesson was that
the limits are per-model and the defaults are not, and this adds that the limits are not co-tuned
across *dimensions* either. `MaxInputTokens` has been 400,000 since it was written, against a
`MaxSteps` that has since changed.

**Why it matters beyond the number.** A budget-terminated run and a planner that chose not to act
are indistinguishable in the summary line, which is precisely what
[#66](#66-the-planner-acts-on-half-of-a-fair-fixture) flags as blocking an honest reading of the
action rate — that entry names the step budget and does not consider the token one.

**The design already agrees with the fix.** The `conclude` tool is wrapped
`budget: null` on stated reasoning: *"A run whose tool budget is exhausted is told to conclude
with what it has, and a `conclude` that the same budget then refuses would leave it no way to say
anything at all."* That is exactly this argument. It was applied to the **tool** and not to the
**round trips that carry it**, which instead get a fixed allowance of two - so the exemption holds
right up until the model needs a third turn to use it.

**Not an artefact of replay.** Replay tools go through the same `SafeToolDecorator.WrapAll` path
as live ones, so truncation and byte caps are identical and a cluster accumulates input tokens the
same way. The confound applies to both instruments, which is one fewer reason to believe
[#66](#66-the-planner-acts-on-half-of-a-fair-fixture)'s replay and cluster arms are measuring
different things.

**The coin flip was not a coin flip.** This entry attributes the intermittency to "the model not
calling the tool cleanly on its first attempt - a text reply, a malformed call, a retry". That
half is right and the cause is not stochastic: [#86](#86) found that `gpt-oss:120b` fails its
first `conclude` call **every single time**, on a schema wrapper this repo asked for, in ten of
ten published demo transcripts. The retry was structural. What was left to chance was only
whether the two granted turns sufficed to recover from it.

**Fixed 2026-09-02, and the ceilings are co-tuned rather than the rescue widened.** With #86's
retry gone, twelve c12 replays produced **zero** `TokenBudgetExhausted` terminations, against six
in the twenty-four runs before it: the reserved step now lands every time. That was not
sufficient. Five of those twelve still breached 400,000 mid-flight around step 15-17, spent the
reserved step on a hurried conclusion, and had **every** excerpt fail grounding - so they
produced no finding, could not be graded, and were counted as the planner proposing nothing.
The rescue landing is not the same as the run being worth grading.

So the fix is the second half of this entry's own diagnosis - *"`MaxInputTokens` has been 400,000
since it was written, against a `MaxSteps` that has since changed"* - rather than more headroom
for the rescue. `MaxInputTokens` defaults to 1,200,000, derived from `MaxSteps` rather than
picked: the transcript is resent every turn, so cumulative input grows with `n(n+1)/2`, and at
20 steps and the measured ~2,700 tokens each turn adds that is 567,000 before headroom.
`scripts/e2e/lib/deploy.sh` sets it per provider in the same block that already co-tunes the step
and wall-clock ceilings, which had this exact argument written beside them and simply missed the
third axis. `LlmBudgetRelationshipTests` fails the build if the two numbers drift apart again,
and also fails if the ceiling is raised until nothing could reach it - it is still a safety
control.

Raising it does not uncap spend: `MaxCostUsd` and `MaxWallClock` are unchanged, and they are
what bound the money and the clock.

While here: the XML comment on `TryGrantConcludingStep` still reads "This grants exactly one step
and never renews", which stopped being true when #78 changed the allowance to two.

**Size.** S. **Fixed 2026-09-02.**


### 83. The in-cluster reachability probe reads 401 as unroutable

**Symptom.** Pointing the harness at any authenticated provider fails at deps:

```
FAIL  the cluster cannot reach https://api.deepseek.com/v1 (HTTP 401) --
      the host probe passed, so the endpoint is up but not routable from a pod
```

The endpoint was reachable. It answered — that is what 401 *is*. The message asserts the
opposite, and it asserts it confidently.

**Cause.** `deps_verify_llm_reachable` curls `${endpoint}/models` from a pod with no
`Authorization` header and compares the status against `200`. A local Ollama serves that
unauthenticated, so the check was written, tested and shipped against the one provider for which
"reachable" and "200" are the same thing.

**Why it matters, and why it is the same bug twice.** This is [#61](#61) in a mirror. That entry
is titled "a keyless endpoint is a local model, not an absent one" and fixed a *host* probe that
assumed a key existed. This is the *in-cluster* probe assuming one never would. Both conflate
authentication with reachability, and both fail in the direction that blocks a working setup —
which the harness's own commentary calls out as worse than having no check at all.

It bit at the worst moment: [#66](#66-the-planner-acts-on-half-of-a-fair-fixture) resolved to "the
action rate is per-model, and `gpt-oss:120b` never acts on c12", which means confirming
[#72](#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever) requires a hosted
model — and this check refuses every hosted model before the first fixture is applied.

**Fix.** Read the status rather than compare it. `200` passes, `401` and `403` pass with the
reason stated in the assertion text, anything else fails as before. The probe deliberately still
sends no credential: it exists to answer a routing question, and answering that by putting an API
key into a pod spec would be a bad trade when the endpoint answering at all already settles it.
Whether the key works is `deps_secrets`' job on the host, and has been since #61.

**Size.** S.

**Fixed 2026-09-02.**

### 84. The redactor's word boundary missed an address, and mangled a version string

**Symptom.** `hephaisto-eval redact` reported all ten transcripts unchanged. The static demo site,
rendering those same transcripts to HTML, published `10.42.0.68` — a pod address from the
recording cluster — on the page for `c8`.

**Cause.** `TranscriptRedactor` anchors its dotted-quad pattern on `\b` at both ends. It runs over
the **serialized** document, where a newline inside an evidence blob is the two characters `\` and
`n`. A `kubectl`-style table in a tool result therefore serialized as:

```
...----------  -----\n10.42.0.68  ready...
```

There is no word boundary between `n` and `1`, so the address never matched. Every address that
happened to begin a line inside a blob was invisible to the redactor.

The same `\b` was wrong in the other direction, and that half is worse because it was not a miss
but a corruption: in `v1.2.3.4.5` the pattern matched `2.3.4.5` and rewrote a version string into
`0.0.0.0`. Redaction became editing — which is precisely what the file's own header argues against,
since "a transcript whose evidence had been edited would be a mock-up wearing the costume of a
recording".

**Why it is the same bug twice.** [#81](#81-a-demo-transcript-is-published-evidence-and-only-its-addresses-are-redacted)
records the first version, which walked a list of fields and missed `Incident.Target.NodeName`.
Scrubbing the whole serialized document fixed that — and *created this one*, because scrubbing a
serialized document means the JSON escapes are part of the text being matched. The lesson the first
fix drew was "a field list has to be re-derived every time the schema grows". The lesson this one
adds is that the serialized form is not the text a human sees, and a pattern written for prose does
not transfer to it unexamined.

**It was found by a second reader, not by review.** The redactor and the pre-commit scan agreed the
corpus was clean because they shared the pattern. Rendering the transcripts through unrelated code
is what disagreed. A check that inherits its subject's bug is not a second opinion.

**Fix.** The boundaries are `(?<![\d.])` and `(?![\d.])` — not part of a number — rather than `\b`.
An octet is bounded by something that is not a digit or a dot, which is what was meant both times.
`999.999.999.999`, `1.28.4` and `10.350197` are still left alone, and `v1.2.3.4.5` now is too.
`demo-site/build.mjs` carries the same pattern and refuses to render if any transcript still holds
an address, so the site cannot be built from an unredacted corpus.

`c8.json` was re-redacted. The other nine were already clean.

**Standing limit, unchanged.** Redaction is still addresses only. `nodeName: lima-rancher-desktop`
— the recording machine's Rancher Desktop VM — remains in the corpus, as
[#81](#81-a-demo-transcript-is-published-evidence-and-only-its-addresses-are-redacted) records. It
is a default name, not routable and not a credential, and removing it would mean editing evidence
rather than protecting it.

**Size.** S.

**Fixed 2026-09-02.**

### 85. `--nightly` never published the branch, and the README said it did

**Symptom.** `scripts/e2e/README.md:170` states, as a settled fact a reader relies on:

> A local model does not make the image local. The harness installs the *published* artifact
> from GHCR by design, so `--nightly` still pushes the branch and builds it in Actions.

`build_nightly` did no such thing. It called `gh workflow run nightly.yml --ref <branch>` and
nothing else.

**Cause.** `--ref` names a branch on GitHub. The runner checks out **that** branch, and every phase
after `build` installs the resulting published chart and image. So the commit under test was
whatever `origin/<branch>` already pointed at, which on an unpushed branch is an older commit and
on a never-pushed branch is a dispatch failure.

**Why it matters more than a stale test.** The failure is silent and it inverts the meaning of the
result. A dispatch against a stale ref succeeds, the workflow goes green, the harness installs it,
the fixtures pass, and the run reports a green gate — **for code that is not the code in the
working tree**. Every entry in this file about measurement integrity is a version of the same
problem: an instrument that reports success without having measured the thing. This one reports
success having measured a different thing.

It also had a second-order cost. Because publishing was a human step that nothing enforced, the
whole harness needed a person in the loop before it could test anything, which is why the label
rename in v0.6.0 could not be verified end to end from a working copy.

**Fix.** `build_nightly` publishes the current branch before dispatching, and then asserts
`ls-remote` agrees with local `HEAD` — so "the runner is building this commit" is checked rather
than assumed. Three guards make doing that automatically defensible:

- **A dirty tree is refused.** The artifact under test and the working copy would otherwise be
  different things, and every later failure would be attributed to the wrong code. `build_rc`
  refuses for the same reason.
- **`main` is never published from here.** A commit landing on `main` fires `ci.yml` *and*
  `deploy.yml`, and `deploy.yml` publishes the three public sites. Running a test harness must
  never be a way to deploy, so on `main` this asserts `HEAD == origin/main` and stops.
- **Nothing is ever forced**, under any flag. A rejected update means the branch moved, which is a
  thing to look at rather than something a test harness overwrites.

`HEPHAISTO_E2E_NO_PUSH=1` restores the old behaviour for dispatching a branch somebody else
published, and says so in the run output rather than doing it quietly.

`build_rc` is deliberately unchanged. It publishes a permanent public tag, image, chart and
prerelease, and it still asks for the tag to be typed back.

**Size.** S.

**Fixed 2026-09-02.**

### 86. Every gpt-oss run failed its first `conclude` call, on a wrapper we asked for

**Symptom.** Every step list from a `gpt-oss:120b` investigation holds two `conclude` calls,
the first of them failed:

```
ERROR: conclude failed: The arguments dictionary is missing a value for the
required parameter 'request'.
```

Not intermittently. **Ten of the ten published demo transcripts, and every c12 replay
examined.** The model reads the error, spends a turn, and calls `conclude` again with the
wrapper, so a correct conclusion always cost two extra round trips.

**Cause.** The tool was declared

```csharp
AIFunctionFactory.Create((ConcludeRequest request) => { ... }, "conclude", ...)
```

which generates a schema whose single property is `request`, wrapping the whole payload.
gpt-oss sends the payload flat - `{"findings":[...],"summary":"..."}` - and the binder refuses
it. DeepSeek and Gemini emit the wrapper, so neither has ever paid this, and it is invisible in
any comparison that reports only the score.

**What it cost, which is more than two round trips.** Every round trip resends the whole
transcript, so the two wasted turns are the two most expensive turns in the run. That is the
mechanism behind [#82](#82), which read "the model not calling the tool cleanly on its first
attempt" as a stochastic property of the model and called the rescue a coin flip. It is
neither: the first attempt is *never* clean, deterministically, because of this schema. The
only thing left to chance is whether the two granted concluding turns are enough to recover.
The same two turns are a large part of why gpt-oss takes 15-18 steps on c12 where
`deepseek-v4-flash` takes 4-7, and steps are what #82 and [#74](#74) are both about running
out of.

**Fix.** The schema is flat - `findings`, `summary`, `confidence` as siblings - which is the
payload the contract already describes and the shape gpt-oss was sending unprompted.

**The binder still accepts the wrapper**, deliberately rather than out of defensive habit. The
models that send it are DeepSeek and Gemini, both of which cost real money to run, and this
machine may only drive the local one - so "the wrapper still works" cannot be established by a
bakeoff and has to be established by a test. `ConcludeToolTests` pins both shapes, pins that
the schema names the fields rather than a wrapper, and pins that a missing field binds to its
default instead of failing the call.

**The general lesson.** A single-complex-parameter tool signature is idiomatic C# and a leaky
wire contract. Every other tool in the agent takes flat primitives and not one of them has
ever failed this way. Prefer flat parameters on anything a model calls.

**Size.** S. **Fixed 2026-09-02.**

### 87. Confidence is offered in two places and only one of them was read

**Symptom.** A c12 replay concluded cleanly, produced a grounded primary finding, graded
`Correct` - and scored `NoPlan`, with `terminationReason: Concluded` and
`investigation.confidence: 0`.

**Cause.** `conclude` accepts a `confidence` on the request *and* a `confidence` on each
finding. `ConcludeMapper.ToFindings` only ever read the finding's. Both were non-nullable
`double`, so a model that filled the top-level field and omitted the per-finding one bound the
primary finding to `0` - indistinguishable from a model asserting its finding is certainly
wrong. `0` is below `MinConfidenceForPlan` (0.5), which escalates `LowConfidence`, which
returns before phase 2.

So the run is recorded as one where the planner proposed nothing: the same cell as a planner
that considered the incident and declined. See [#88](#88), which is that observation
generalised.

**Why it surfaced now.** It was always latent, and the wrapper in [#86](#86) hid it - nested
under `request`, the two `confidence` fields sit at visibly different depths. Flattening the
schema puts `confidence` and `findings` side by side at the top level, and a model reading that
can reasonably take the top-level one as *the* confidence. The fix for #86 made an existing
ambiguity easier to fall into, which is a good argument that it was never safe.

**Fix.** Both fields are `double?`, so "did not say" is distinguishable from "said zero", and
the mapper reads the finding's number first and the request's as a fallback. Not the reverse:
the top-level field is a fallback rather than an override, so a model that gives per-finding
confidences keeps them.

**Not fixed by deleting one of the two fields**, which was the first instinct. A model that
reliably followed the schema would not need the fallback - and #86 is a measured, ten-of-ten
demonstration that this model does not reliably follow the schema. Deleting the field would
rest the fix on exactly the assumption already shown false one entry above.

**Size.** S. **Fixed 2026-09-02.**

### 88. `NoPlan` pools four outcomes, and the action rate counted all four as declines

**The finding that reframes [#66](#66-the-planner-acts-on-half-of-a-fair-fixture).**
`InvestigationRunner` returns before phase 2 on *any* escalation and on any termination that is
not `Concluded`:

```csharp
if (escalation is not null || termination != TerminationReason.Concluded)
{
    return new InvestigationOutcome { ... };   // phase 2 never runs
}
```

`PlanGrader` then scores the missing plan `NoPlan`, correctly and by design - "an investigation
can end before planning for a dozen legitimate reasons". What was wrong is the reading. #66
quotes `gpt-oss:120b` at **0 of 18** on c12 and calls that a per-model property of the planner.
It is not one number. It is at least four distinct outcomes in one cell:

| how the run reached `NoPlan` | did phase 2 run? |
|---|---|
| the planner produced a plan with `noActionRequired` | yes - the only real decline, and it grades `MissedAnAction`, not `NoPlan` |
| a budget ceiling terminated the run ([#82](#82), [#86](#86)) | **no** |
| every finding lost its evidence to grounding | **no** |
| the primary finding's confidence read `0` ([#87](#87)) | **no** |

Of the 24 historical gpt-oss c12 runs behind that figure, **6 ended `TokenBudgetExhausted` and
3 `StepBudgetExhausted`** - nine runs in which the planner was never invoked, scored as though
it had considered the incident and chosen not to act. `deepseek-v4-flash` has hit neither
ceiling on this fixture, ever, because it concludes in 4-7 steps where gpt-oss takes 15-18 -
and #86 is a large part of that gap.

**The published comparison also moved two variables at once.** Every DeepSeek c12 arm ran with
`Llm:PlanningStructuredOutput=JsonObject`, because DeepSeek answers `400` to a strict schema.
Every gpt-oss "baseline" arm ran with the default `JsonSchema`. So "4 of 8 against 0 of 18",
and the `p = 0.0047` computed from it, compare a model *and* a structured-output mode.

**Measured after the three fixes**, twelve c12 replays each, same model, same endpoint, same
`JsonObject` planning format DeepSeek was run with, `MaxSteps=20`:

| arm | planner ran | acted | root cause correct |
|---|---|---|---|
| before, `JsonObject` (`c12-jsonobject`) | 5/12 | 1 | 5/12 |
| [#86](#86) only (`c12-toolfix`) | 7/12 | 0 | 7/12 |
| #86 + [#87](#87) + [#82](#82) (`c12-ceilings`) | **11/12** | 1 | **11/12** |
| `deepseek-v4-flash`, for reference | 8/8 | 4 | 8/8 |

The instrument moved and the model did not. Getting the planner invoked at all went 5/12 to
11/12, Fisher's exact **p = 0.027**, and root-cause accuracy moved with it because both were
being lost to the same truncated runs. `TokenBudgetExhausted` went from 6 of 24 runs to **0 of
24** across both post-fix arms.

**And the headline significance does not survive the correction.** Acting, counted over the
runs where the planner actually ran, is 1 of 11 for gpt-oss against 4 of 8 for DeepSeek:
Fisher's exact **p = 0.11**. The published **p = 0.0047** was computed over all runs, nine of
which never reached the planner. So the number that made this look like a settled per-model
property was substantially the instrument.

**What this does not claim.** It does not claim gpt-oss acts as often as DeepSeek. The point
estimates are still 9% against 50% and the direction is unchanged; what changed is that at
these sample sizes the difference is no longer significant, and the denominator now means what
it says. Nothing here licenses a published action rate either - it says the previous one was
measured with a broken instrument, not that a working one has now been read.

**Fix.** The three instrument bugs are fixed (#82, #86, #87). What remains is reporting:
`terminationReason` beside the verdict, which is [#59](#59) and still open, and a
`PlanVerdict` that distinguishes "the planner declined" from "the planner never ran". The
second is the smaller change and the one that would have prevented this entry.

**The reporting split landed 2026-09-03.** `PlanVerdict.PlannerNeverRan` separates a run that hit
a ceiling or escalated before phase 2 from `NoPlan`, which now means only the residual - the loop
concluded cleanly and still emitted nothing, which is a defect and should be loud rather than
pooled. The per-scenario line names the termination reason whenever it is not `Concluded`, so a
truncated run can no longer render as a bare `no finding` and read as a wrong answer; the summary
prints the histogram with the sentence that matters beside it, and `RunReport.PlannerRan` is the
denominator nothing computed before.

**The escalation arm is passed in rather than derived**, and that is the part worth keeping.
`EscalationReason` lives on `InvestigationOutcome`, not on `Investigation`, so it cannot be seen
from what the grader receives - grounding loss reaches this path without touching anything on the
investigation. Deriving it from what *is* visible would have been the same guessing this entry is
about, so `ScenarioScorer.Combine` takes it from the call site that already holds the outcome.
`ScenarioScore.PlanVerdict` also defaults to `PlannerNeverRan` now: a score nobody set is one
where the planner demonstrably did not run, and the old default put it in the decline bucket.

**What it does not do is license a published action rate.** The instrument can now be read; the
number still has to be measured with it, and quoted with its fixture beside it ([#90](#90)).

**Size.** S for the reporting split. **Blocks:** the same sentence #66 blocks.
**Status: fixed 2026-09-03.**

### 89. gpt-oss declines c12 because it gets the decisive question wrong, not because nobody asked it

**The fifth prompt arm, and the first aimed at phase 1.** [#66](#66-the-planner-acts-on-half-of-a-fair-fixture)
observes that across every c12 hypothesis on record, whether a run acts is predicted by whether
the phase-1 hypothesis says a replacement pod would get a different name. Within
`deepseek-v4-flash`, holding the model fixed, the split is perfect: 4 of 4 acting runs carry the
clause, 0 of 4 declining runs do. `gpt-oss:120b` had written it 0 times in 38 hypotheses.

The four earlier arms - #41's three and #66's one - all rewrote `30-planning.md`, the phase 2
prompt. This one rewrote `20-output-contract.md`, which is where the clause would have to
originate: phase 2 has no tools, so it cannot recover anything phase 1 declined to write down,
and that file asks for a hypothesis "in one or two plain sentences". gpt-oss obeys at a median
205 characters; DeepSeek ignores it at 496.

The change asked the hypothesis to say whether a **freshly created replacement** of the failing
object would fail the same way, phrased symmetrically and naming the "would fail identically"
case first, so as not to lean toward acting.

**It did exactly what it was designed to do, and the action rate went to zero.**

| arm | n | wrote the clause | planner ran | acted |
|---|---|---|---|---|
| all fixes, shipped prompt (`c12-ceilings`) | 12 | 0/11 | 11/12 | 1 |
| all fixes, replacement question (`c12-handoff`) | 12 | **8/9** | 9/12 | **0** |

**So the clause is a correlate and not a cause**, which is worth more than the arm cost. Every
story in #66 up to here - four prompt arms, a threshold, a step budget - assumed the model
would act if the right thing were in front of it. Induce the clause and it still does not.

**And the reason is visible now, because the wrong answer is written down instead of merely
absent.** Of the nine hypotheses that reached the question, **two answered it correctly and
seven did not**:

- *"A fresh replacement pod would have a different name, so it would not see its own name in the
  lease and would start successfully"* — correct. Declined anyway.
- *"A new replica using the same PVC will encounter the same stale lease and fail identically."*
- *"A fresh replacement pod would inherit the same PVC and therefore fail identically."*
- *"A newly created replacement pod would also read the same stale lease (**different pod name**)
  and fail unless the file is cleared."* — which notices the name differs and concludes failure
  in the same sentence.
- three more that answer "would start successfully **if the lease file were cleared**" or
  "**using a new PVC**" — conditioning the recovery on clearing the state, which is the same
  wrong model wearing a hedge.

The failure is one inference, and it is the fixture's whole hinge: gpt-oss reasons *the state
persists, therefore the failure persists*, and does not carry through that the comparison is
`holder == my own name` and that the name is the half that changes. It is not missing the
mechanism - seven of these hypotheses quote the `FATAL` line and name the pod - it is not
completing it.

**Two independent failures, and neither is a prompt.** The model mostly cannot derive that a
replacement would succeed; and on the two runs where it did derive it and said so plainly,
phase 2 declined anyway. The second is the more interesting one and this arm is far too small to
say anything about it - n = 2 - but it is the first evidence that `30-planning.md`'s "where does
the bad state live?" routes to a decline even when the hypothesis in front of it says a
replacement would start cleanly. If that holds up, the phase 2 question is mis-shaped for
transient faults rather than merely under-informed, and asking it about *state* rather than
about *recurrence* is the thing to change.

**Reverted, on this file's own rule.** Root-cause accuracy went 11/12 to 9/12 and no-finding 1
to 3, both within noise, and the action rate did not move. A longer prompt that moves nothing is
a cost with no benefit - the same judgement #66 made about the fourth arm, on the same evidence
standard.

**What it changes about the remaining work.** #66 says "the next thing worth trying is not a
fifth wording". That was right, and this is the measurement that shows why rather than the
argument that it should be. Five arms have now moved the wording in front of the model and none
has moved the outcome. The remaining candidates are not prompts: a model that can complete the
inference, or a fixture set that does not rest the only acting measurement on one that
`gpt-oss:120b` gets wrong 7 times in 9.

**Size.** S, and spent. **Closed as a measured null 2026-09-02**, with the arm's results kept in
`results/c12-handoff-*.json`.

### 90. The acting path had no fixture that could measure it

**Why this exists.** [#72](#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever)
needed a cluster run in which the agent actually executes something, and could not get one. The
act phase rested on c12, which `gpt-oss:120b` acts on about 1 run in 11, so confirming a
post-execution bug meant waiting for a one-in-eleven event at ~25 minutes a try - or retrying
until it landed, which is selecting the run that agrees with you.

**The fixtures were measuring two things at once.** c11 and c12 both put the state on a PVC. So
acting on either means acting **against** the rule that PVC contents survive a pod replacement -
c11 by reconciling a second volume, c12 by noticing the comparison is against the pod's own
*name* rather than the stored bytes. That rule is correct and an SRE agent should hold it.
[#89](#89) measured gpt-oss failing c12's override 7 times in 9 when asked point blank, and
every gpt-oss run that *did* propose a restart was one that had stopped believing the state was
on a PVC - one hallucinated an emptyDir and acted on that.

So a decline on c11 or c12 is ambiguous between "will not act" and "did not make the
inference", and the act phase has been reading the first from evidence that only supports the
second. c12's own header names the trap and then walks into it: it identifies the PVC as the
reason c11 failed, removes c11's second volume, and keeps the PVC.

**c13.** The same fault with the wedge on an **emptyDir** - a startup lock left behind by an
abnormal exit, which is the case `30-planning.md` already names ("the process that wedged on a
stale lock") on a volume it already calls pod-scoped. Nothing to override, nothing to reconcile,
and the rule the prompt states is sufficient. It measures **willingness to act**; c12 keeps
measuring the inference.

**It cannot arm itself, and that is not an oversight.** The lock has to be left by an exit the
workload did not choose, so `chaos_apply` execs a `touch /scratch/crash` once the pod is Ready.
A fixture that wedged itself deterministically would wedge the replacement pod too - same
entrypoint, same empty volume - and then a restart would not repair it. That recursion is
exactly what forces c11 and c12 onto a PVC in the first place, and it is worth writing down
because it looks like laziness and is not.

**Measured.** Acted on the first cluster run and the second: diagnosed the stale lock, proposed
`RestartPod`, admitted by policy as low-risk, executed, and the replacement pod was available in
16-20s. 70 of 71 assertions passed. The one failure was #72, which is what the fixture was built
to reach - and it reached it on demand, twice, having previously been unreproducible.

**Not a replacement for c12, and this is the part that matters.** c11 and c12 are untouched and
keep reporting exactly what they reported. Grading the harder fixture as a pass, or widening its
`AcceptableActions`, would be the bar-lowering [#66](#66-the-planner-acts-on-half-of-a-fair-fixture)
refuses; adding an easier fixture alongside it is a different act. **The two numbers must be
quoted separately.** "The agent acts on c13" and "the agent acts on c12 about 1 time in 11" are
both true, they measure different capabilities, and averaging them would recreate precisely the
conflation this fixture was added to end.

`ACT_FIXTURE` defaults to c13; c12 and c11 stay selectable.

**Size.** M. **Fixed 2026-09-02.**

### 91. `docker manifest inspect` cannot reach ghcr from here, so the build phase aborts on a published artifact

**Symptom.** Two consecutive e2e runs aborted in phase 2:

```
waiting for the chart to appear in the registry  ok (1s)
ok    chart is pullable anonymously
waiting for the image manifest to appear .... timeout after 180s
FAIL  image is pullable -- ghcr.io/flou21/hephaisto:0.5.1-main.0.32 not resolvable
```

The nightly had gone green, and the image **was** published - the registry's own tag list
returns `0.5.1-main.0.32` to an anonymous `curl` in under a second, as does the token endpoint.
What fails is `docker manifest inspect`, with `net/http: TLS handshake timeout`, repeatably,
against a host that `curl` reaches fine from the same shell.

**So the check disagrees with the registry**, and it disagrees in the direction that stops a
release gate on a good build. The chart half of the same function uses `helm show chart` and
passes instantly, which is what makes it look like a publish race rather than a client problem.

**Not diagnosed further**, deliberately - it is a docker-CLI networking question on one machine
and it was in the way of #72 rather than being the work. Worked around with `--from cluster`,
which skips a phase whose assertion had already been satisfied by other means.

**Worth fixing as a check rather than as a network.** The harness wants to know "can a consumer
pull this", and it has two clients that answer that; using the one that also serves the chart,
or falling back to the registry API on a docker failure, would make the gate independent of a
tool that is not otherwise on the path. As it stands a transient docker fault reads as an
unpublished image.

**Size.** S. **Open.**

### 92. An `instance` label became a node name, and default-deny did the rest

**Symptom.** On the third and fourth cluster runs of c13, the planner proposed a correct
`RestartPod` and the harness still reported:

```
FAIL  c13 was not acted on -- 1 action(s) were proposed and none executed
      admission or the executor refused a plan the planner did make
```

The action:

```json
{ "type": "RestartPod", "state": "Denied", "decision": "Deny",
  "decisionReasons": ["cluster facts could not be read, so no action can be judged"] }
```

**Cause.** The incident's target:

```json
{ "kind": "Pod", "name": "c13-wedged-lock-6778bccbd9-6s4wg",
  "ownerKind": null, "ownerName": null,
  "nodeName": "10.244.0.6:8080" }
```

`nodeName` is an IP and a port. `AlertmanagerEndpoints.ResolveTarget` read
`Label(labels, "node") ?? Label(labels, "instance")`, and `instance` is a **scrape target
address** - that shape for anything scraped per-pod. `ClusterFactsGatherer.ReadNodeAsync` then
asks the API server for a Node called `10.244.0.6:8080`, gets a 404 and throws; the gatherer
converts any throw into `ClusterFactsUnavailable`; and that is default-denied, correctly.

**So every action proposed on an alert without a `node` label was refused, permanently**, and
the reason names neither the label nor the node. Default-deny is right and the message is
useless: "cluster facts could not be read" describes a cluster that cannot be reached, and what
actually happened is that we asked it a malformed question.

**The two ingestion paths disagreed about what a node name is.** `SignalMapper` - the Kubernetes
watch path - reads `node` and stops. Only the Alertmanager path had the fallback, so only alerts
could poison an incident, and the fixtures that had ever been acted on came in through the other
one. It is [#33](#33) one field over: a label mapped somewhere it does not belong, silently.

**Why it hid for three releases.** Nothing had ever executed an action on a cluster until
v0.6.0, and the two runs that did (#90) acted on the *Deployment*-scoped incident, whose target
carries no node at all. The pod-scoped duplicate is the one that gets the bogus node - so
whether the agent could act came down to which of two incidents for the same fault reached the
planner first. That duplication is itself worth an entry and does not have one yet.

**Fix.** `node` only. Null is a shape everything downstream already handles: `NodeFacts` is
nullable and `ReadNodeAsync` returns null for a missing name. A node the alert did not name is a
fact we do not have, which is a different thing from a fact we could not read.

`ReadNodeAsync` also now treats a **404 specifically** as "no such node" and returns null rather
than failing the whole gather. Narrow on purpose - every other failure still propagates and
still default-denies. It is there because this failure was silent, total, and presented as an
agent that had simply stopped acting.

**Size.** S. **Fixed 2026-09-02.**

### 93. The Resolved assertion read a field the list endpoint does not have

**Symptom.** With [#72](#72-an-incident-that-was-successfully-acted-on-sits-in-verifying-forever)
and [#92](#92) fixed, a clean cluster run acted on c13, recovered it, and the agent's own log
said:

```
Verification 1 of RestartPod on hephaisto-chaos/Deployment/c13-wedged-lock:
  Passed - Deployment/c13-wedged-lock is settled with 1/1 ready and no container waiting
Incident 01a062c6-ce97-7ce7-961c-28241050f6a5 resolved: ...
```

and the API agreed - `state: Resolved`, `resolvedAt: 2026-09-02T16:02:24`, the full
`Detected -> Triaging -> Investigating -> Acting -> Verifying -> Resolved` chain, granted by
`hephaisto/verifier`. The harness still reported *"the workload recovered but verification never
closed the incident"*.

**Cause.** `_act_resolved` filtered the list endpoint with `.target.name`. The list endpoint
projects `IncidentListItem`, which carries `targetKind` and `targetName` **flat**; only the
detail endpoint has a nested `target` object. So `.target.name` was null on every row, the
`select` dropped all of them, and the count was always zero.

**The assertion could never pass**, for any agent, in any release. Every other `.target.name` in
`chaos.sh` reads `details.jsonl`, which does have the nested object, which is why only this one
was wrong and why it looked idiomatic.

**Why it survived.** It is an assertion that can only fail, and for three releases it had four
genuine bugs to agree with - the harness wait, the missing owner, the jsonb audit detail, the
node label. Each time it fired it was right for a reason that had nothing to do with its own
query, so nothing ever questioned it. **A check that has never passed is not evidence that the
thing it checks is broken**, and this file has now produced two of those in one milestone -
[#1](#1) is the same shape with the opposite sign, an assertion that could only pass.

**Fix.** `.targetName`. Verified against the live API on the run that exposed it: the corrected
filter returns 1 for the same data the old one returned 0 for.

**Size.** S. **Fixed 2026-09-02.**

### 94. The demo site rendered another state's glyph for three of the five it knew

**Symptom.** None, which is the point. Every page on demo.hephaisto.dev rendered a state badge
that looked entirely correct and was wrong.

`demo-site/build.mjs` carried its own copy of the glyph table. `docs/design.md` names
`Components/Display.cs` as the owner of the glyph vocabulary and the enum-to-class mapping, and
the copy had drifted from it - not into arbitrary characters, which someone would have noticed,
but into **other states' glyphs**:

| state | the site rendered | `Display.cs` says | and that glyph belongs to |
|---|---|---|---|
| `Escalated` | `!` | `^` | `AwaitingApproval` |
| `Investigating` | `*` | `~` | `Detected` |
| `Detected` | `.` | `*` | `Expired` |

So the site announced "Escalated" beside the marker for an incident waiting on a human. It also
had no entry at all for `Acting`, `Verifying`, `Triaging`, `AwaitingApproval` or `Expired` - five
of the ten - and dropped the `st-*` class the console emits, leaving state carried by the word
alone where the console carries glyph, word and colour. `docs/design.md`'s fourth rule is that
state is never colour alone; this was the other half of that failure and nothing checked it.

**Why nothing caught it.** `demo-site/enums.mjs` exists precisely to stop this, and it works - it
parses `Enums.cs` rather than copying it, because a hand-written map "would create a second
definition that drifts the first time a member is inserted in the middle". The glyphs are the
same problem one layer up, and they were hand-written anyway. The visual baselines photograph
`design/gallery.html`, which renders from the console's stylesheet and never sees `build.mjs`.

**Fix.** `demo-site/display.mjs` parses `Display.cs` at build time on the same contract as
`enums.mjs`: refuse loudly rather than return something plausible, because what actually breaks
it is a `switch` rewritten as a dictionary and the symptom would be every state rendering
identically. `DisplayVocabularyTests` asserts from the C# side that the shape those regexes match
still exists, and that every arm agrees with what the method returns. Verified against a
`Display.cs` with an arm deleted and one with the switch replaced: both refused.

**Size.** S. **Fixed 2026-09-03.**

### 95. The seeded demo reported ten incidents as policy-denied by a policy engine that never ran

**Symptom.** `DemoSeeder` set `EscalationReason.PolicyDenied` on every seeded incident whose
investigation carried a plan:

```csharp
incident.EscalationReason = investigation.Plan is null
    ? EscalationReason.NoPlanProduced
    : EscalationReason.PolicyDenied;
```

The demo stack constructs no policy engine. `Kubernetes:Enabled` is false, the executor refuses
everything, and nothing on that path is ever asked for a decision - so this is a claim about a
component that was never built, asserted on ten incidents.

**Why it read as harmless.** It was uniform. Every row said the same thing, the wording on the
transition beside it ("Diagnosed, and a plan was proposed. Nothing executes in Observe mode.")
was accurate, and a reader had nothing to contrast it against. It stops being harmless the moment
a genuinely policy-denied incident is seeded into the same list, because then two rows carry the
same label and only one of them earned it - and the one that earned it is the interesting one.

**The general shape** is the one this project keeps finding: a value composed at render time and
presented as a value that was recorded. See [#94](#94) for the same failure in the glyph table
and the state badge, in the sibling renderer, found in the same sweep.

**Fix.** `NoPlanProduced` when nothing was proposed, `None` otherwise. More usefully, the whole
synthesis is now gated on whether the transcript carries incident transitions at all, so a
recorded incident keeps the state, the reason and the actions it actually had.

**Size.** S. **Fixed 2026-09-03.**

### 96. The console does not render an action's verifications

**Symptom.** `AgentAction.Verifications` is persisted, carries the T+60s / T+5m / T+15m outcomes,
and is read by nothing on the way out. `IncidentDetail.razor` renders the action, its decision,
its reasons and its execution line, and stops there.

**Why it matters more than it looks.** Verification is the load-bearing half of the safety
argument - "everything it does is verified, then reverted if it did not work" is on the landing
page - and the evidence that it happened is currently only in the database and the logs. The one
place a human looks at an action is the one place the check on that action is not shown.

**Not fixed here**, and noticed rather than hunted: `hephaisto-eval export` deliberately does not
carry the verification rows into a transcript, on the grounds that nothing renders them and an
artifact should not ship fields with no consumer. That reasoning is sound and the gap it points
at is real, so it is written down instead of being quietly worked around. Fixing the console is
the prerequisite; the exporter follows.

**Size.** S for the console, S to add them to the export afterwards. **Open.**

### 97. `--full` and `--mode Auto` defeat each other, and the release gate cannot confirm acting

**Symptom.** The v0.6.0 release gate, run exactly as the roadmap's procedure says to run it:

```
scripts/e2e/run.sh --tag 0.6.0-main.0.47 --full --mode Auto

  waiting for an action to be executed .......... timeout after 180s
  FAIL  c13 was not acted on -- 1 action(s) were proposed and none executed -
        admission or the executor refused a plan the planner did make
  skip  c13 is available after the restart
  skip  c13's incident reached Resolved
```

Eleven fixtures diagnosed, eight graded correct, and the one assertion the release needed
failed. The planner **did** propose a `RestartPod` on c13 - so this is not [#66](#66)'s
willingness question, and not [#89](#89)'s inference either. Something refused a correct plan.

**The reason, from the action row:**

```json
{"type": "RestartPod", "decision": "Deny", "state": "Denied", "dryRun": false,
 "decisionReasons": ["31 % of the cluster is unhealthy: cluster-wide event,
                     not a pod-level problem"]}
```

**Nothing is broken.** That is gate 7, `PolicyEngine.cs:203`, against
`PolicyOptions.ClusterUnhealthyCeiling = 0.3`, and it is arguably the most important gate in
the file: restarting one pod is the wrong response to a cluster that is coming apart, and an
agent that cheerfully did it anyway would be worse than one that does nothing. It fired
correctly, recorded its reason, and escalated to a human. The incident ended `Escalated` /
`PolicyDenied`, which is exactly what it should be.

**The conflict is between two flags, and it is structural.** `--full` applies eleven fixtures
*simultaneously* - that is the point of it, and `chaos_apply` says so: "applied 11 chaos
fixtures simultaneously". On a single-node kind cluster eleven deliberately broken workloads
are 31% of the pods, which is over the ceiling. So:

> **The breadth that makes `--full` the diagnosis gate is precisely what makes the act
> assertion impossible.** Every action in a `--full` run is denied, correctly, and the acting
> path cannot be confirmed by the command documented to confirm it.

**This is why the act assertion has only ever passed on narrow runs.** [#90](#90) records c13
acting "on the first cluster run and the second" - both with a small fixture set - and
[#72](#72)'s confirming run was the act phase, not the full corpus.

**And it is why v0.5.0's green run does not contradict any of this.** `roadmap.md` records
`run.sh --tag 0.5.0-rc5 --full --mode Auto` exiting **0** on 2026-09-01: 77 assertions, 0 failed,
**8 skipped**. One of those skips was the act assertion, on the "planner proposed nothing" branch
- `ACT_FIXTURE` was c12 then, and `gpt-oss:120b` proposes on c12 about one run in eleven. The
planner never got as far as proposing, so nothing was ever submitted to the policy engine, so
gate 7 was never reached.

**The conflict was therefore latent, and hidden by a second defect.** It could only surface once
the planner reliably proposed something, which is exactly what c13 was added to make true. Fixing
"the agent will not act" is what exposed "the gate the agent acts through refuses everything in
this configuration" - two bugs in a queue, where the first one masked the second and the run
looked green throughout.

**What must not be done about it.** Raising `ClusterUnhealthyCeiling` in `values-e2e.yaml`
would make the assertion pass by weakening a safety gate until the test agrees with us, which
is the bar-lowering [#66](#66) refuses in a different costume. The gate is right. The fixture
is right. The assertion is right. Only the *combination* is incoherent.

**Fix, in the shape this file already uses.** `chaos.sh:806-811` already skips rather than
fails when the planner proposed nothing, on the grounds that it is "a model judgement, not a
fault in the acting path", and the comment fifty lines below explains why a report that is
wrong about *why* is worse than one that is merely incomplete. A correct policy denial is the
same kind of thing. The act phase should read the denial reason and, when it is
`ClusterWideEvent`, **skip with that reason** - naming the fact that the run's own breadth
caused it - rather than failing as though the executor were broken.

Then the release procedure is two runs, and the docs should say so: `--full` for the diagnosis
corpus, and a focused `--fixtures c13 --mode Auto` for the acting path, which needs about
twenty minutes and does not trip the ceiling.

**Also worth keeping:** this produced the project's first genuine end-to-end policy denial on a
cluster - investigated fully, refused for a stated reason, escalated. It is a better answer to
"what stops it?" than any of the constructed examples, and it is a candidate for the demo site.

**The skip landed 2026-09-03.** `chaos_assert_acting` now has a fourth branch: an action denied
with a reason naming a cluster-wide event is a `skip` that says the run's own breadth caused it
and points at `--fixtures $ACT_FIXTURE`, rather than a `fail` that reads as a broken executor.
Verified against this run's own `details.jsonl`, where the new detection returns 1 - so the
branch would have fired on the run that produced this entry.

It matches on the reason **text**, because the incident API exposes `decisionReasons` as strings
and carries no reason code. That is worth fixing the next time that payload changes; a
`PolicyReasonCode` on the wire would make this robust rather than merely careful.

**Still open:** the release procedure itself. `docs/roadmap.md` and the README describe one gate
command, and it needs to be two - `--full` for the diagnosis corpus and a focused
`--fixtures c13 --mode Auto` for the acting path.

**Size.** S for the skip (done); S for the docs. **Open**, for the procedure.

### 98. The redactor's lookbehind lost to `\u0022`, which is [#84](#84) for the second time

**Symptom.** The first transcript ever produced by `hephaisto-eval export` carried a pod IP
**66 times**. The redactor ran - `Transcript.Save` always runs it - and replaced 98 other
addresses in the same file correctly.

```
\u0022endpoint\u0022: \u0022http\u0022, \u0022instance\u0022: \u002210.244.0.5:8080\u0022
                                                                  ^^^^^^^^^^ survived
```

**The cause is the one [#84](#84) already fixed, one level down.** That entry replaced `\b`
because this runs over a SERIALIZED document, where a newline is the two characters `\` and
`n`, so `...\n10.42.0.68` had no word boundary and the address survived. The replacement was
`(?<![\d.])` - "not preceded by a digit or a dot".

An escape sequence ends in whatever character the encoder chose, and `System.Text.Json` writes
a quote inside an evidence blob as `\u0022`. **That ends in the digit `2`.** So the lookbehind
refuses to begin a match at the `1` of `10.244.0.5`, for exactly the reason `\b` refused to
begin one after the `n` of `\n`. Same defect, same file, same class of blindness, fifteen
lines below the comment describing the first instance.

**The guard agreed with it, again, for the same reason.** `demo-site/build.mjs` re-scans every
transcript before rendering, and its comment says in as many words: *"This guard originally
used `\b` and agreed with the redactor that c8.json was clean; it was not. A check that shares
its predecessor's bug is not a second opinion."* It then copied the replacement lookbehind, and
so shared the replacement's bug: the site built the leaking file without complaint.

**Fix.** Two changes, deliberately different from each other:

- `TranscriptRedactor` matches after a complete `\uXXXX` escape as well as at the old
  boundary. Over-redacting a character would be cosmetic; leaving a pod IP on a published page
  is not, so the alternation is generous.
- `build.mjs` stops reasoning about boundaries in an escaped document at all. It **decodes**
  the escapes and scans what the text means, which is a different question from the one the
  redactor answers. Sharing the premise is how it agreed with the bug twice.

Verified both directions: the leaking file now fails the build with
`c13-denied.json contains 66 unredacted IPv4 address(es)`, and re-exporting after the fix
yields 164 addresses of which every one is the placeholder.

**Why the committed corpus was never affected.** All ten replayed transcripts are clean, checked
by decoding every escape and re-scanning. Replay serves recorded tool output; this leaked on the
export path, whose blobs come straight from the database and include Prometheus alert JSON with
an `instance` label. [#81](#81) said the corpus's redaction was "sound for THIS corpus only" and
that a new source of content would need re-examining. It was right, and the new source arrived
as a new command rather than as a new fixture.

**What this says about the rule.** Scrubbing a serialized document is still the correct design -
enumerating fields is the rule that goes stale, and that argument in `TranscriptRedactor`'s
remarks stands. But it means the scrubber reads text the encoder wrote, and every escape the
encoder emits is a boundary the pattern has to survive. That is now two bugs from one cause, so
it is written down rather than fixed a third time by accident.

**Size.** S. **Fixed 2026-09-03.**

### 99. `hourlyCostUtilization` disagreed with the ledger by 40x, on the wide run only

**Symptom.** In the v0.6.0 `--full` gate run:

```
FAIL  hourlyCostUtilization is 0.001359, ledger implies .054677
      -- budget reporting disagrees with what was actually spent
```

A factor of forty. The ledger is the sum of what the investigations actually cost, and the
gauge is what the agent reports about its own hourly spend - so one of the two is wrong about
money, and the gauge is the one a human would look at to decide whether the agent is running
away with itself.

**It did not reproduce on the focused run**, twenty minutes later, same build, same model:

```
ok    hourlyCostUtilization agrees with the ledger (0.006664 vs .006664)
```

So it is not simply broken arithmetic. The difference between the two runs is breadth and
time: eleven fixtures over 221 minutes with 36 investigations and an agent restarted mid-run
by the notify phase, against one fixture over 25 minutes with one investigation and no
restart. **The restart is the most interesting of those**, because an hourly figure is a
rolling window and a process that restarts has to reconstruct it from the database rather than
from memory. That is a hypothesis and nothing here tests it.

**Related, and worth checking together:** [#15](#15) records four instruments registered twice
under the same meter with conflicting types and units, two of which are double-counted. A gauge
that reads low by a large factor while a ledger reads correctly is the shape that would produce.

**Not diagnosed further** - it was found by a release gate that was looking for something else,
and chasing it would have meant another two-hour run. Recorded so the next `--full` run knows
to look, rather than rediscovering it.

**Size.** M, mostly to reproduce. **Open.**

### 100. One fixture in eleven opened an incident and never got investigated

**Symptom.** Same run:

```
FAIL  only 10 of 11 fixture incidents were investigated
skip  an investigation ended on a ceiling --    1 Faulted  (of 36 investigations)
```

Detection was complete - all eleven fixtures opened an incident, asserted and passed earlier in
the same phase. One of them then never produced a diagnosis, and separately one investigation
of thirty-six terminated `Faulted`.

**Those two lines are probably the same event**, and the report does not say so. `Faulted` is
the termination reason for an investigation that threw, and an incident whose only
investigation faulted has no diagnosis to collect. If that is right, the honest reading is "one
investigation crashed" rather than "a fixture was ignored", and the assertion is describing a
symptom two steps downstream of the cause - the same complaint [#93](#93) makes about an
assertion that pointed at the wrong thing for four bugs in a row.

**What is missing to close it** is the exception. `Faulted` is recorded on the investigation
and the message is in the agent's logs, and neither is surfaced in the run report - so a
`Faulted` count of 1 tells you something crashed and nothing about what. Reporting the
termination reason beside the verdict landed in [#88](#88); reporting the *fault* did not.

**Not reproduced.** The focused run had one fixture and one investigation, so it could not have
shown this either way.

**Size.** S to surface the exception; unknown for the fault itself. **Open.**

### 101. c13 is measured by one instrument and one model

**Why this exists.** [#66](#66) closed on c13, and c13 has a cluster arm and nothing else. There is
no `cassettes/c13.json`, so `hephaisto-eval run` cannot replay it, and every number the project now
publishes about willingness to act comes from one instrument observing one model.

c11 and c12 both have two arms. The value of the second is not redundancy - it is that #66's entire
history is **two consecutive corrections** caused by reading a single instrument as though it
described the agent. First replay-versus-cluster was called an instrument disagreement when it was
two models; then the per-model figure was called a property of the model when a third of its
denominator had never reached the planner. Both were found by comparing measurements, and c13 has
nothing to compare against.

**What it costs to fix:** tokens and a dev-cluster incident, no release time. Record once with
`hephaisto-eval record --fixture c13 --incident <guid>`, then a labelled replay arm. Recording is
the only half that needs a cluster.

**What it does not block.** The published claim already names its fixture, its model and its
denominator, and a replay arm would add a second denominator rather than change the first. This is
the guard against the *next* correction, not a defect in the current numbers.

**Size.** S. **Open.**
