# Hephaisto engineering history

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

This is the record of work already done and — more usefully — of what was learned doing it. It was
split out of `docs/roadmap.md` on 2026-08-29, when that file had become roughly 60% history sitting
in front of the part anyone reads to decide what to do next.

It is **not** `CHANGELOG.md`. A changelog says what shipped, per release, for someone upgrading.
This says why the code is shaped the way it is, for someone about to change it. Several entries
below record a hypothesis that turned out to be wrong; those are kept deliberately, because the
wrong turn is usually the expensive part to rediscover.

For where the project is going, see [`roadmap.md`](roadmap.md). For what is known-broken and
unfixed, see [`backlog.md`](backlog.md).

---

## Step 0 — close the kill-switch gap — **done, 2026-08-28**

**The design specified three independent kill switches. Only one was wired.**

`AgentMode` was read solely from the `agent_mode` database row. `HEPHAISTO_MODE` was set by
`infra/app/hephaisto.yaml` and by the AppHost, the manifest carried a ConfigMap `mode` key
with a comment saying the two "must agree", and `HEPHAISTO_SWITCHES_PATH` pointed at a
`switches.yaml` — and **no code read any of them.**

That was harmless only because the missing controls failed safe. The dangerous direction was
the one that looked safe:

> An operator who sets `HEPHAISTO_MODE=observe` to **stop** an agent running in `auto` would
> find it keeps acting. The big red button is painted on.

### What was built

`ModeResolver` in `Hephaisto.Core/Safety` resolves the arms, and `KillSwitch` in
`Hephaisto.Agent/Safety` supplies them. **The most restrictive arm wins** — implemented as
`Min` over `AgentMode`, whose declaration order (`Off < Observe < DryRun < Auto`) is therefore
load-bearing and pinned by its own test. No arm can ever raise the mode, only lower it.

The distinction that carries the safety property is **silent versus failed**:

| Arm state | Meaning | Effect |
|---|---|---|
| Silent | not configured here (no env var, no mounted ConfigMap) | does not constrain |
| Declared | configured and understood | constrains to its value |
| Malformed | configured, not parseable (`HEPHAISTO_MODE=atuo`) | constrains to `Observe` |
| Unreadable | configured, not reachable (file gone, Postgres down) | constrains to `Observe` |

Collapsing malformed into silent is what would invert the whole thing: a typo would *remove*
the restriction the operator was applying. Every arm silent resolves to `Observe` — not
`Auto`, which would make "nobody configured it" the most dangerous state in the system, and
not `Off`, because an agent that reports nothing looks exactly like a healthy cluster.

Parsing is strict on purpose. `Enum.TryParse` alone would accept `HEPHAISTO_MODE=3` and
quietly mean `Auto`; a number in a kill switch is a misunderstanding, and a misunderstanding
reads as `Observe`. The `killSwitch` key parses the other way round: anything that is not an
unambiguous false engages it, because a garbled emergency stop is an engaged one.

### What changed around it

- The switch ConfigMap holds **discrete keys**, so each projects as its own file and needs no
  parser. It used to be a YAML document nested inside a YAML string — one bad indent away
  from breaking, in the file you least want to get wrong under pressure. `HEPHAISTO_SWITCHES_PATH`
  became `HEPHAISTO_SWITCHES_DIR`.
- The ConfigMap's `cooldown`, `budget`, `actionableNamespaces`, `investigation` and
  `grounding` blocks were **removed, not wired**. None was ever read; each duplicated a
  setting with a real home (`PolicyOptions`, `LlmBudgetOptions`, `GroundingVerifier`).
  Config that reads like configuration and behaves like a comment is worse than no docs.
  Anything added there in future needs a reader in `src/` in the same commit.
- `SwitchWatcher` polls every 10s, logs a mode change at Warning in both directions, and
  publishes `hephaisto_mode` — a gauge that was declared in Core's telemetry constants and
  had never been registered as an instrument. You can now alert on "the agent is in Auto".
- The admission transaction in `ActionRepository` folds the env and ConfigMap arms in beside
  the row it already reads, so the two arms an operator can actually reach at 3am bind the
  executor and not just the investigation loop. The row read stays inside the transaction,
  because it is the only arm a concurrent admission can race.
- `/status` shows configured and effective mode side by side, names the binding arm, and
  lists all of them. "Configured Auto, running Observe" is the state that most needs seeing.

### Verified

64 tests, including the exhaustive 4×4×4 precedence table. Two negative controls confirm the
tests detect the failure rather than passing vacuously: making a malformed arm read as silence
fails 8 tests, and flipping `Min` to `Max` fails 15. Live against a running process with
`HEPHAISTO_MODE=auto`, a ConfigMap file saying `Observe` and no Postgres:

```
Kill switch armed: effective mode Observe, bound by configmap:mode
  [env:HEPHAISTO_MODE: Auto; configmap:killSwitch: not set; configmap:mode: Observe;
   db:agent_mode: unreadable (...) - reads as Observe]
```

Editing the file to `Off` moved the gauge to 0 with no restart.

---

## Step 1 — first light against the cluster — **done, 2026-08-28**

The stack now runs in k3s. The old hand-installed observability stack was removed first
(plan §6): `grafana.db` was copied off the PVC and audited before teardown (`backup/`), and
Backstage and litellm were repointed at `hephaisto-obs`.

### What works, verified against the running cluster

| Area | Evidence |
|---|---|
| 14 pods | Prometheus, Grafana, Alertmanager, Loki, Tempo, otel-collector, grafana-mcp, Aspire, kube-state-metrics, node-exporter, operator, postgres, agent |
| Datasources | 4 provisioned: prometheus (default), loki, tempo, alertmanager |
| Alert path | 17 alerts firing; `AgentWatchdog` delivered to the webhook (`watchdogReceipts: 1`, not stale) |
| Kill switch | All three arms live. `kubectl edit cm` pulled the agent Observe → **Off**, bound by `configmap:mode`, **no restart** |
| RBAC | `secrets` denied everywhere incl. `get`; `kube-system`, `hephaisto`, `hephaisto-obs` writes denied; write **only** in `hephaisto-chaos` |
| Detection | Kubernetes watcher opens incidents for real cluster faults, resolved to the owning controller (`Deployment/c2-crashloop`, not the pod) |
| Dedup / correlation | 42+ signals collapsing into ~20 incidents, up to 6 signals on one |
| Self-protection | Signals about Hephaisto's own namespaces hard-escalate as `SelfSignal` — including the agent's own OOMKill |
| Chaos fixtures | C1–C5, C7 produce their documented states, incl. the C4/C7 discrimination pair |
| grafana-mcp | 44 tools; rejects unauthenticated calls with 401 |
| LLM | Gemini connected; the model calls Kubernetes read tools during investigations |
| Persistence | pgvector, pg_trgm, pgcrypto; HNSW index; append-only audit rows written |
| UI | Incident list with filters, detail with signal timeline and state transitions, status page with the kill-switch arms, feedback form |

### Resolved: investigations now persist

Fixed 2026-08-28. **The recorded hypothesis above was a dead end**, and it is worth saying so
rather than deleting it: `IncidentStateMachine.Transition` only constructs and appends, so no
state transition ever mutated a previous event. The bug was not *which* event was Modified, it
was *when the save happened*.

The coordinator, the incident repo and the audit repo share one scoped `HephaistoDbContext`.
`AuditRepository.AppendAsync` called `SaveChangesAsync` **one line after** the state machine
appended an `IncidentEvent` and **before** `TrackNewIncidentChildren` marked it Added. EF ran
`DetectChanges`, found the new event carrying a client-assigned `Guid.CreateVersion7()` key,
concluded the row existed, and emitted an `UPDATE ... WHERE id = ...` matching zero rows. The
staged graph then died with the scope.

The fix is the pattern already used by `IncidentQueries.AddFeedbackAsync`: `audit.Enlist(...)`
to stage, `TrackNewIncidentChildren` **before** any save, and exactly one `SaveChangesAsync`
per unit of work. Applied to all three investigation paths.

Three further bugs were only visible once this one was out of the way, each hidden by the one
in front of it:

| Bug | Why it was invisible |
|---|---|
| Gemini overloads were never retried | `HttpRetryOptions` is accepted by the SDK and ignored on the `AsIChatClient` path. Proven by timing: failed turns returned in 1.2–5.7s when four retries need ≥15s. Fixed with `TransientRetryChatClient`. |
| LLM turn boxes in the UI were empty | `RecordLlmTurn` recorded no text at all |
| Tools rejected their own documented usage | a nullable parameter with no default is still emitted as `required` in the JSON schema |
| **Investigations could never conclude** | concluding costs a step, and the step budget was spent before the model could call `conclude`. The codebase had already closed this exact hazard for the *tool* budget and never for steps. A step is now reserved. |

Measured across three eras, all completions, from Postgres:

| | Faulted | StepBudget | Concluded |
|---|---|---|---|
| Before any fix | 6 / 7 | 1 | 0 |
| After the retry fix | 0 / 4 | 3 | 1 |
| After the reserved step | **0 / 7** | 0 | **7 / 7** |

Provider faults went 6-of-7 to **0-of-11**, and every run now reaches a conclusion. What that
did *not* fix is accuracy — see Step 2.

**Two lessons worth keeping.** `dotnet watch` silently declined several edits with *"No managed
code changes to apply"*, so a run of iterations tested stale code — when a fix seems not to
take, `tilt trigger hephaisto` for a real image build before believing the result. And the
diagnostic that names the offending entity found in one iteration what inference had not found
in five; it is committed, and it should be reached for early.

---

## The first accuracy measurement — 2026-08-28

Kept here because the number is a historical fact and the *interpretation* of it is what drives
[`roadmap.md`](roadmap.md)'s v0.1.0 milestone. The bar itself, and what to do about the number,
live there.

### First real measurement — **3 findings from 11 runs. Well short.**

Taken 2026-08-28, after the four bugs in Step 1 were fixed. Every run now completes and every
run concludes, but only **3 of 11 produced a finding at all**; the rest conclude "insufficient
evidence", which is honest — the final-turn nudge asks for exactly that rather than a guess —
and is not a diagnosis.

The cause is visible in the step traces, and it is not the model being wrong. It is the model
**running out of budget in the wrong place**. On an `Unschedulable` incident it had the answer
at step 8 from `get_events`, then spent 6 of its 12 steps on Loki label discovery and only
reached `list_nodes` at step 24, long past the budget.

So the number to move is not "accuracy" directly. The candidates, cheapest first:

1. **Raise `MaxSteps`** from 12. The simplest experiment, and the one that says whether the
   ceiling is the binding constraint or an excuse. Costs money per run to evaluate.
2. **Order the tools by likely value per `SignalKind`** — for a scheduling incident, node and
   event tools before log search. The runbooks already carry the kind; nothing uses it to
   shape tool priority.
3. **Charge label/metadata discovery differently from evidence-gathering**, so exploring
   Loki's label space does not consume the budget meant for answering.

**Do not conclude anything about Phase 2 from 3/11.** The measurement is of an agent that ran
out of steps, not of an agent that reasoned badly, and those imply completely different work.

Build the **eval harness** here rather than in Phase 2 — replaying recorded incidents against
recorded tool output is the only way to tell whether a prompt change helped or just cost more.
Without it every subsequent change is a guess.

**The decision this produces:** if accuracy lands at 9/10, the gap to auto-restarting a pod is
small and Phase 2 is worth building. At 4/10 it is not, and you want to know that *before*
granting write RBAC — which is precisely why the executor was left unbuilt.

Cheap wins worth doing in the same pass: Grafana annotations on state transitions, and
runbook memory (retrieving the top-3 similar resolved incidents into the prompt). The storage
and the hybrid search already exist; only the retrieval call is missing, and it is the highest
-leverage quality change available after the runbooks themselves.
---

## Step 2b — packaged for release — **done, 2026-08-28**

Not on the original plan, and done because the repo was about to be made public. It changes
nothing about what the agent does.

| | |
|---|---|
| Licence | **AGPL-3.0**. The console is served over a network, so §13 applies: the footer links to the source of the exact running commit, overridable via `Hephaisto:SourceUrl` for forks |
| Versioning | MinVer — the git tag is the only source of truth. `/api/version`, the console footer, OTel `service.version` and `hephaisto_build_info{version,commit}` all read one assembly attribute |
| Chart | `charts/hephaisto`, with `ci/negative-tests.sh` — 24 assertions of things the chart must refuse or never emit, mutation-tested |
| CI | `ci.yml` and `release.yml`. **No deploy job, and there must not be one** |
| Tilt | now renders the chart, so dev and prod cannot drift into two sources of truth |
| Hygiene | `backup/` purged from history; this machine's tailnet address and the neighbouring project's name removed from every tracked file |

Four latent defects surfaced only by doing it, none of which any amount of reading would have
found:

- **The production Dockerfile had never been built.** Every image on the dev machine came from
  `Dockerfile.dev`. It failed at `RUN adduser` with exit 127: the `aspnet:10.0` base ships
  neither `adduser` nor `useradd`.
- **`--minimum-expected-tests` does not exist** in this runner. It prints `unknown option` and
  **exits 0** — the same "green build that tested nothing" trap it was meant to close.
  `scripts/ci-test.sh` parses the real count instead.
- **`dotnet build -warnaserror` did not pass**, so the planned CI flag would have failed on its
  first run. Also: an incremental build reported 0 warnings where a clean build reported 13.
- **`readOnlyRootFilesystem` cannot be a constant** — `true` is right for the published image
  and fatal for a dev image whose entrypoint is a compiler.

---

## Step 2c — three channels and one command — **done, 2026-08-29**

`v0.0.1-rc2` was verified by hand: twenty-odd commands and a person reading output. That does
not survive being needed twice.

**A third release channel.** `release.yml` publishes two kinds of thing and both are
statements — `v0.0.1` says "ship this", `v0.0.1-rc1` says "I intend to ship this". Neither is
right for "give me something installable to point a test at", and cutting a release candidate
per test run would turn a meaningful list of shipping candidates into a log of CI invocations.
`nightly.yml` publishes the same image and chart with every "this is the current version"
signal off: no GitHub Release, no moving `latest`/`0`/`0.0`, a prerelease version that ranges
skip. Manual dispatch only.

Worth recording, because the obvious guess is wrong: MinVer does **not** produce
`0.0.2-main.0.N` here. It appends the commit height to the newest tag, which is currently the
`v0.0.1-rc2` prerelease, so `main` resolves to `0.0.1-rc2.5`. The `main.0` identifiers only
appear once the newest tag is a release. Both shapes are prereleases, which is what matters,
but nothing downstream may assume the spelling — the nightly prune job asks git which versions
correspond to tags instead of pattern-matching one.

**`scripts/e2e/run.sh`.** Dispatch a nightly, wait until the artifacts are genuinely pullable,
create a single-node kind cluster, install the real observability stack from
`infra/observability/*.values.yaml` unmodified, `helm install` the published chart from GHCR,
break four things at once, and assert on what comes back. About 25 minutes and under a dollar.

It closes two of the three limits `ci.yml`'s `e2e-kind` job admits to in its own comment — no
Prometheus, and no real LLM key. The third, NetworkPolicy enforcement, is item 7 below and the
harness says so at the end of every run rather than letting a green tick imply otherwise.

Three defects were found and fixed on the way, all by checking rather than reading:
`integration-postgres` had been red on every run since it was written (two password literals
in different files that had to agree and did not); the chart validated against Kubernetes
1.31, which kind no longer ships; and `hephaisto_llm_budget_utilization` was declared,
documented, alerted on twice and charted twice with no instrument behind it.

---

## v0.2.0 — it acts — **done, 2026-08-30**

The executor, verification, rollback, approval, oscillation quarantine and a path to
`Resolved`. What is worth recording is not the feature list — `roadmap.md` has that — but what
building it revealed about the machinery that had been sitting there, tested, for a release.

### Four controls that were inert, and all of them looked fine

The roadmap's reading was that "almost everything except the executor already exists, built and
tested, waiting for a caller". True of the inventory. What it could not see was that four of
those waiting pieces did not work, and every one failed in the direction that produces no
symptom:

- **`PolicyOptions` was bound to configuration nowhere.** Every other options class in the repo
  has `services.Configure<T>(GetSection(T.SectionName))`; this one had none, and
  `IOptionsMonitor<T>` resolves perfectly happily to a default-constructed instance. So the
  allowlist was empty and gate 2 denied everything — which is the *correct-looking* answer. A
  smoke test would not have caught it either: "denies everything" is exactly what a properly
  configured agent does before anyone opts a namespace in.

- **`ClusterFacts` was three fields.** The clock, the mode, the quarantine stamp. Gates 3, 7,
  8-fractional, 9, 10 and 13's budget downgrade were all dead — and all of them have unit
  tests, all of which passed, because the tests supply the facts the caller did not. The policy
  engine ran in full and could not contradict itself.

- **The database mode arm pinned every cluster to Observe.** It declared the `agent_mode.mode`
  column, `InitialCreate` seeds that column to `Observe`, and the resolver takes the minimum
  over every arm that speaks. `mode: Auto` in the chart had never worked on any database that
  had ever been migrated, and the only way to lift it was a hand-written UPDATE.

- **`appsettings.json` — which ships inside the image — named `hephaisto-chaos`.** Binding
  `PolicyOptions` without noticing would have made the published default permit acting
  somewhere while the chart's `actionableNamespaces` was empty.

The common thread is worth naming, because it is the third time this project has hit it.
**Absence is not neutral.** An unbound options class, an unread fact and an unwritten metric
all present as "working, quietly". The v0.1.0 lesson was that a guard asserting *an instrument
exists* passes on the bug it exists to catch; this is the same shape one layer out.

### The gate that would have decided autonomy by wording

Gate 14 downgrades any action with no rollback spec, and a pod delete has no inverse — the
controller recreates the pod, which *is* the restart. So `RestartPod`, the one action this
milestone exists to automate, could reach `Allow` only if the model invented a fictional
rollback spec, and would sit at `RequireApproval` forever if it followed the planning prompt's
own instruction to say plainly when something cannot be undone.

Both outcomes are bad and neither is a bug in any single place. Whether autonomy worked at all
would have depended on how a model happened to word a JSON field. The fix is a named
`SelfHealing` exemption, two types wide, pinned by a test, with the reasoning in the code: for
these types the recourse on a failed verification is **escalation, not rollback**, because the
state after a failure is the state the controller chose anyway.

### A duplicate-key bug in the seam nobody had called

`TryAdmitActionAsync` ended with an unconditional `db.AgentActions.Add(action)`, and
`DecideOutcome` already writes every proposed action to `agent_actions` during the
investigation. The first real execution would have thrown a duplicate key inside the one
`Serializable` transaction the whole safety model rests on.

The intent was legible once looked for: `AdmittedStates` counts `Proposed` and
`AwaitingApproval`, so a proposal is meant to reserve its own budget slot and later *become*
the executed row. One row, created by the proposal, transitioned by admission. `Add` is now
reached only for a genuinely new action, which is the rollback case.

### Two decisions that shaped the rest

**Mode is GitOps.** There is no endpoint and no UI control that sets it; `SetModeAsync` is
deleted rather than wired, and backlog #8 is resolved by reclassification instead of by
building the writer it asked for. An operator who could raise autonomy from a web form would be
a second, unreviewed source of truth for the most consequential setting in the system. The
database arm restrains only — it carries the runaway latch and is otherwise silent — and the
one write exposed is re-arm, which cannot name a mode or exceed the deployment's ceiling.

**The model never gets the handle back.** The rollback spec is free-form JSON the planning
phase produced, and executing it as written would hand over exactly what the three-phase split
denies — on the one code path nobody watches, minutes after the incident, with budgets bypassed
by design. It is read for typed values, and the revert is built as an ordinary action over the
same closed enum. Only `ScaleWorkload` has an expressible inverse today, and the rest say so.

### Running it once found three bugs, and every one of them needed a cluster

The acceptance test was written, and then run. It got as far as the agent proposing exactly the
right action for c11 and no further, which is the most useful place it could have stopped.

**Gate 9 refused every restart the feature exists for.** It fired at `ReadyReplicas <= 1`, and
that includes zero - so it protected a last Ready replica that was not there, on a workload
that was already entirely down. Its denial said so in as many words: "this would restart the
last Ready replica (0 ready of 1 desired)". Since a crash-looping pod is by definition not
Ready, `RestartPod` - the single action type v0.2.0 promotes to auto - could never have fired
on the fault it was promoted for. The feature was dead on arrival, all 853 unit tests passed,
and the suite contained `[InlineData(3, 0)]` asserting the denial explicitly. The tests encoded
the same wrong belief as the code, which is the failure mode unit tests cannot catch by
construction.

**Verification passed on a workload that was still crash-looping**, which is the worse one
because it fails toward yes. The fixture's container has no readiness probe, so it is Ready the
instant it is Running, and while wedged it runs for two seconds before exiting: the Deployment
reports `availableReplicas: 1` for part of every crash cycle with nothing Waiting. Sample there
and a broken workload verifies clean, the incident reaches `Resolved`, and the agent reports
success for a fault it did not fix.

**The harness fell for the identical trick**, logging "c11 is available after the restart" while
the pod sat in CrashLoopBackOff with six restarts - which is the sixth time this harness has
measured its own instrumentation rather than the agent, across two releases.

The common property is worth keeping: none of the three was reachable without executing against
a real cluster. The first needed the policy engine to see true replica counts, the other two
needed a workload that is Ready and broken at the same moment. No amount of unit testing
produces either condition, because both are facts about Kubernetes rather than about this code.

### What is still not claimed

All three are fixed and **none of the fixes has been re-run**. Nothing has yet observed an
execution, a passing verification and a `Resolved` incident in sequence. That re-verification is
deferred to before v0.4.0 by decision rather than oversight, and until it happens the honest
statement is that everything up to "the agent decides to restart the pod" is measured and
everything after it is built.

---

## v0.3.0 — it reaches people — **done, 2026-08-30**

Before this release an escalation was a database row, an audit row, and a nudge to any browser
tab that happened to be open. If nobody was looking, nobody was told. For a system whose pitch is
autonomous remediation with a human backstop, that is the worst failure available, and a pod
restart could cause it.

### The premise was wrong, and checking it made the milestone smaller

The roadmap, the README and backlog #39 all said there was **no outbound HTTP anywhere in
`src/`** — no `AddHttpClient`, no notification package. `GrafanaAnnotator` had been POSTing to
Grafana's `/api/annotations` through a client registered with `AddHttpClient` since
`v0.1.0-rc2`.

The correction was worth more than the time it saved. What was actually missing was a
*notification stack*, not the ability to make a request — and that reframed the annotator from a
counterexample into the **template**: conditional registration on config being present, a `Null*`
no-op when it is not, a per-call timeout linked to the caller's cancellation token, a
`Describe()` line at startup saying whether it is on and why not, and a doc-comment rule that
nothing in it may fail an investigation. Every one of those is in `INotificationChannel` now
because it was already in the file the plan called a counterexample.

Corrected in its own commit before any code, which is the discipline backlog #7 established:
doing it the other way round leaves a window in which the fix justifies the lie.

### Four things that were silently inert, and one that was silently wrong

The pattern from v0.2.0 repeated, which by now is less a surprise than a method.

**`GrafanaAnnotator.Describe` had no caller.** Its own remarks say the absence of Grafana
configuration *"is reported once at startup by `GrafanaAnnotator.Describe`"*. The method exists,
is correct, and `grep -rn` across `src/` and `tests/` found nothing but that sentence. The
reasoning behind it is right — a warning per incident would train people to ignore the log on
exactly the installs that chose not to wire Grafana up — and the line was never emitted.

It mattered more than a missing log line. **Every outbound integration here degrades silently
when unconfigured**, which is correct per call and wrong overall: the failure mode of the whole
feature is that nothing happens, and "nothing happened" looks identical whether it was never
switched on or is broken. Shipping notifications on top of that would have built the same trap
one storey higher. `OutboundStartupReport` is the caller now, and `Describe()` is on the
`INotificationChannel` interface rather than a convention, so a channel cannot be added without
answering what it says at startup.

**`SilenceAlert` was allow-eligible.** It sat in the policy engine's `LowRisk` set, so an
operator could have put it in `autoEnabledActionTypes` and had the agent silence its own alerts
unattended. It satisfies every word of that set's description — cheap, reversible,
single-object. What it fails is subtler and took reading the set's *purpose* rather than its
definition: every other action on that list **fails visibly** when it is wrong. A bad restart
shows up as a pod still crash-looping. A bad silence shows up as nothing at all, for as long as
it lasts. It now has its own routing case and can never be automatic, and a test asserts that
from the other side — auto-enabled, in `Auto` mode, still `RequireApproval`.

**`Notifications:GrafanaUrl` had no reader**, caught before the commit that introduced it. It
would have shipped as the third instance of backlog #19 in the same release that closed the first
two, which is a good argument for the standing rule being a rule.

**`alertmanager.maxDuration` was `2h`.** That is what every other duration in a Kubernetes values
file looks like, and `TimeSpan.Parse` rejects it outright — the agent would have failed to start
with a binding error naming a key nobody would connect to that line. `values.schema.json` now
enforces the `hh:mm:ss` shape so `helm template` refuses it first.

**`Math.Clamp` propagates `NaN`.** A jitter value from a random source could throw out of
`TimeSpan` multiplication on the delivery path. Found by a test written to assert the clamp
worked, not to find a bug in it.

### The decision the milestone rests on

Delivery could have been an enqueue call at each of the ten places an incident commits a
transition. The reason it is not is that **the failure of that design was already in the
codebase**: `IncidentTriage` reaches `Escalated` twice — the self-signal arm and the storm
circuit breaker — and published no live event at all. Nobody had noticed, because nothing
asserted it. The storm one is precisely the bulk case.

`IncidentStateMachine.Transition` appends an `IncidentEvent` on every edge without exception;
that is the log the audit trail is built from. A `SaveChanges` interceptor over those rows turns
the property from a matter of diligence into a matter of construction: **an incident cannot reach
a notifiable state without a delivery row, because one commit writes both.** The two silent
triage paths were fixed the day it landed, without being touched.

It costs three constraints, because it runs on every save in the process. It must be cheap — a
stock install has no routes and leaves after one field read. It must not query — everything comes
from the change graph already in memory, and an incident missing from it yields a thinner message
rather than none, because "incident X escalated, look here" is enormously better than silence. And
it must not throw: a notification bug that could roll back an incident write would be a far worse
defect than the silence it was built to fix.

### Two places where the honest answer was "no call at all"

A **dry run of `SilenceAlert` sends nothing.** Every other action routes its dry run through the
API server's own `dryRun=All`, which validates without mutating. Alertmanager has no equivalent,
so the only honest dry run is not to ask — a "validated" silence that actually silenced something
would make DryRun a liar about the one action whose entire effect is to hide things.

The **Teams `Describe()` prints scheme and host only.** A Workflows trigger URL is a bearer
credential in a query string; the thing every other channel here can safely do, print its
configured URL, would write a live credential into the pod log.

### What is still not claimed

**Nothing has been delivered from a cluster.** 989 unit tests and 53 integration tests pass,
including the transactional guarantee asserted against a real Postgres over all thirteen
escalation reasons — and verified falsifiable, the way `IncidentMetricsTests` was, by commenting
out the enqueue and watching 15 tests go red. The `notify` e2e phase is written and wired and has
never been executed.

That is the same debt v0.2.0 ended on, and the two now compound. They are also **one run**:
`--mode Auto` exercises the executor #41 is waiting on, and every notification this release built
fires on the outcomes that run produces. Filed as #45 rather than left implied.

The v0.2.0 precedent is the reason not to round this off: running that acceptance test once found
three bugs, and every one of them needed a cluster to find.

## Resolved open items

Items that spent time on the open list and are now closed. Kept with their original reasoning,
because "why was this ever a problem" is the question that recurs.

### ~~1. `AgentMode.Off` does not stop the automatic loop~~ — **fixed, 2026-08-28**

Four gates added: `SignalIngestPipeline.IngestAsync` (the single funnel both producers reach
triage through), `InvestigationWorker`'s drain loop, `InvestigationCoordinator` before the LLM
call, and `StrandedIncidentRequeue` at startup. Each resolves the switch itself rather than
reading the poller's snapshot, matching the precedent in `IncidentQueries`.

Proved on a kind cluster, not just in tests: with `effectiveMode: Off` an injected
`ImagePullBackOff` held open incidents at 6 with the fault genuinely present, and lifting to
`Observe` took them to 8. `watchdogStale` stayed false throughout — the heartbeat is
deliberately ungated, or the agent would believe it had gone blind the moment it was switched
back on.

---

## v0.4.0 — a design language — **done, 2026-08-30**

The milestone was supposed to be about taste. Most of what it cost was about measurement.

### The safety net it depended on could not see a stylesheet

The roadmap made [#46](backlog.md#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto)
a hard prerequisite, reasoning that a refactor of 1268 lines of CSS behind a red Playwright suite
was unmanaged risk. Correct, and only half the problem: the suite asserts **behaviour**. All 34 of
its read-only assertions pass against a console whose layout has collapsed, and there was no
screenshot comparison anywhere in the repository. Turning it green would have produced a
confident-looking net that could not catch the class of regression the milestone was about to risk.

So the net was built rather than repaired, and the first thing it caught was itself:
`maxDiffPixelRatio: 0.01` sounds tight and is not. Changing `--accent` to hot pink and re-running
gave **sixteen passes**, because the accent is roughly 0.2% of a section's pixels. The threshold is
`maxDiffPixels: 0` now, and the same experiment fails exactly the four dark-theme shots the accent
appears in.

**The lesson is the one this repo keeps relearning.** A tolerance that sounds reasonable is a
guess. #1 was a suite reporting green on zero assertions; this was a suite reporting green on a
changed colour. Both were found by deliberately breaking something and checking the test noticed.

### The failing spec was not the bug it looked like

`acting.spec.ts` failed on every run that reached it, asserting the approve button never enabled:

```
44 × locator resolved to <button disabled ... data-testid="approve">approve</button>
```

Read literally, that says v0.3.0's approval control is broken — and v0.3.0's entire approval story
is a Teams card that deep-links to that button. It would have been the most serious defect in the
project.

It was not a product bug. This is a Blazor **Web App** with `<Routes @rendermode="InteractiveServer" />`,
so every component renders twice: once as static server HTML delivered with the document, then
again over the SignalR circuit, which replaces that DOM wholesale. Measured against a live console:

```
h1 visible at            52 ms      <- what open() waited for
_blazor websocket open   55 ms
first RenderBatch       102 ms
a click first lands     629 ms      <- what open() needed to wait for
```

Between those moments the page looks finished and is inert. Events dispatched into it are dropped.

**Why it stayed hidden for so long is the interesting part.** Reading static HTML is
indistinguishable from reading the interactive DOM, so every read-only assertion passed either way.
Only a spec that *interacts* could notice, and there was exactly one — which #46 caused to be
skipped in the default mode. In Observe it skipped and was never reached; in Auto it ran and was
read as a product defect. Two bugs hid each other for a release.

The helper's own comment asserted the opposite of the truth — *"the h1 is rendered by the component
itself"* — which was correct for Blazor Server before .NET 8 and stopped being correct without
anybody editing the sentence.

### Refactor first, choose second

The ordering that made the release reviewable, and it was not the plan's.

The tokens were extracted, the radius/z-index/type scales named, and `app.css` moved onto them with
**every value byte-identical**. The baselines proved that pixel-for-pixel. Only then was Forge
applied, as a data edit whose diff was exactly the intended change: 13 of 20 shots moved, and the
light theme did not move where it overrides a token separately.

That is why a 1268-line refactor and a total palette change could land in one release with both
still attributable. It also answered a question rather than guessing at it: `0.8rem` and `0.82rem`
were in use for the same role, and the baselines were the arbiter of whether collapsing them was
visible — 0.26px at a 13.5px root. They confirmed it was not, but only after the form controls were
added to the gallery, because until then nothing photographed used those sizes at all.

### Forge's cost was known in advance and still nearly shipped

The direction was chosen from three complete candidates, and its cost was written down before the
choice: a warm accent has to fight the semantic red. The first palette put `--accent` and
`--orange` **1.24:1** apart — two colours a reader cannot tell apart, so a link and a warning would
have looked identical.

Deepening the orange fixed that pair, and then the new guard test caught red at 1.28:1 and yellow
at 1.20:1, neither of which had been looked at. Knowing a risk is not the same as having handled it;
the test is the part that handled it.

### Three smaller things, all of the same shape

- `#10131a` was written twice as text on a `var(--red)` ground. Right in dark, where `--red` is a
  light pink; wrong in light, where it is a dark crimson. The error banner rendered near-black on
  dark red for three releases.
- `.hp-main` carried a comment claiming *"1200px is the floor the tables are laid out for"* directly
  above `min-width: 0`, which does the opposite. No such rule existed.
- The favicon's comment named the CSS token it used, and a double hyphen is illegal inside an XML
  comment. The mark was invalid XML and rendered as nothing, silently, until a light-mode baseline
  showed a broken image.

Each is a claim that was true when written, or never true, and nothing checked. That is the same
failure the token guards, the SVG well-formedness test and the `theme-color` test now cover.

### And then the harness ran, and found the real one

Everything outside the console phase passed on the first attempt. The console phase failed all
nine specs, and three separate things were stacked behind that.

The first was mine. #48's fix waited for a `_blazor` **websocket** before asserting anything —
which is a transport, not a state. SignalR negotiates, and where a websocket cannot be established
it falls back to server-sent events or long polling with the page perfectly interactive and no
websocket ever opening. It passed against a development image and timed out against a published
one. It now waits for the negotiation, which happens under every transport, and the one spec that
actually interacts retries the action rather than the assertion — because an event dispatched into
a not-yet-interactive page is dropped silently, so re-asserting alone waits forever on an input the
server never saw.

The second was also mine, and cost a whole run: a `kubectl port-forward` left running against the
*development* cluster owned port 18100, so the browser reached the wrong agent. The giveaway was a
console reporting `dryrun` and 106 incidents during an Observe run with five fixtures — the numbers
were impossible for the cluster under test, which is the only reason it was caught rather than
explained away.

Underneath both was the thing worth the whole milestone.

**`_framework/blazor.web.js` returned 404 in every image this project has ever published.** Blazor
never started. No circuit was ever established. The console was a static page in every released
build, and every interactive control on it was dead — approve, deny, re-arm, retry, the feedback
form, the filters. v0.3.0's entire approval story is a Teams card that deep-links to a button that
has never worked outside a development image.

The cause is one flag:

```
dotnet publish --no-restore    42489 byte manifest, 0 entries matching blazor   -> 404
dotnet publish                 56532 byte manifest, blazor.web.js present       -> 200
```

The Dockerfile restores in an earlier layer against the `.csproj` files alone, so a source-only
change reuses it. At that moment the project contains no Razor components, the Blazor static web
assets are never resolved, and `--no-restore` at publish reuses the incomplete result.
`@Assets[...]` finds no entry, returns its input unchanged, and the browser asks for a path nothing
serves.

**Why four releases missed it is the part worth keeping.** Nothing fails: the static render is
unaffected, so the console looks perfect and the pod logs nothing. Development never sees it,
because `dotnet watch` builds rather than publishes and the build manifest is complete — so every
manual check was done on the one image where it worked. And the console suite could not see it,
because until this milestone it asserted only read-only content, and reading a static render is
indistinguishable from reading a live one. Its single interacting spec was the one #46 caused to
skip in the default mode.

So the only assertion in the repository capable of catching this was also the only one that never
ran. Fixing #46 is what made the suite able to see it, and running the harness end to end is what
made it look.

Final state: **9 passed, 0 failed, 0 skipped** against a live kind cluster in the default mode,
verified by loading the fixed image into the running e2e cluster rather than by inspecting the
Dockerfile — and then again, properly, from a nightly built out of the fixed Dockerfile:

```
hephaisto end-to-end: 0.4.0-main.0.24     channel nightly, mode Observe
  0 failures in any phase
  PASSED -- 71 assertions, 5 skipped        11m 46s, $0.399 of Gemini
```

**The nightly was run before the rc deliberately, and it is the reason there is no dead tag.**
`build_rc` pushes the tag *before* release.yml runs, so cutting an rc first would have minted
`v0.4.0-rc1` against an image whose console was still a static page — permanently, and for the
second time in three releases, since v0.3.0's MinVer divergence would have left `v0.2.1-rc1`
tagged with nothing behind it. Both workflows build from the same `./Dockerfile` with the same
context, so a green nightly is genuine evidence about the rc's image rather than a rehearsal.

---

## v0.7.0 — one word, two thresholds — **found 2026-09-03**

The expensive finding of this release was not the feature. It was that
`SignalKind.ReadinessFlapping` had **two detectors that disagreed about what flapping means**,
three hundred lines apart in the same file.

`SignalMapper` classifies a pod's condition history and refuses to call anything flapping below
`SignalThresholds.ReadinessFlapCount` — four ready-transitions in the trend window. That is a real
measurement of oscillation, and its comment says so.

`SignalMapper.EventKind` classified a **single** `Unhealthy` warning as the same kind:

```csharp
"Unhealthy" when message.Contains("Readiness probe", StringComparison.OrdinalIgnoreCase)
    => SignalKind.ReadinessFlapping,
```

Two thresholds for one word: **four, and one.**

**Why it had never been noticed.** Every fixture that produced this event was one where the pod
really was unhealthy, so the label was wrong and the conclusion happened to be right. It took a
fixture whose pods are *deliberately healthy* to expose it.

**What it cost, measured.** A readiness probe fails once on any pod that takes longer to start
than its `initialDelaySeconds` — which is every ordinary rollout. On the v0.7.0 gate, `c14`'s
incident was opened **21 seconds** after its deliberate bad deploy, by an `Unhealthy` event,
classified `ReadinessFlapping`:

```
target: hephaisto-chaos/Pod/c14-bad-deploy-6d47849bf5-rtj67
signals: 2026-09-03T11:30:29  kind=ReadinessFlapping  reason=Unhealthy  src=KubernetesWatch
```

The error-rate alert the fixture exists to raise needs a five-minute rate window plus `for: 2m`,
so it arrived roughly seven minutes later and **correlated into an incident that was already
labelled a flap**. Since `SignalKind` selects the runbook, the investigation was handed
`ReadinessFlapping.md`, whose entire argument is that the fault is intermittent and that
restarting will not help — against a fixture whose correct answer is a rollback.

**This is the third distinct mechanism behind [#70](backlog.md#70), and the one that entry never
reached.** Its recorded cause was a race between two Prometheus rules, and the two rules it named
do not exist. The correction found that c3's real race was between two *ingestion paths*. This is
a third: **an ingestion path that fires within seconds on a normal rollout, and beats every
metric-derived signal by minutes because a metric needs a window and an event does not.**

That asymmetry is the durable lesson. An event-derived signal is always going to win a race
against a rate-derived one, so an event-derived signal that overclaims is not merely wrong about
its own incident — it captures every incident that follows it on that workload.

**The fix** is that the event path now requires `Count >= ReadinessFlapEventCount`, deliberately
the same number as the condition detector's, so the two agree on what the word requires.
Kubernetes aggregates repeated identical events, so `Count` *is* the evidence of repetition; a
null `Count` is read as one occurrence, because absent evidence of repetition must not be read as
evidence of it. Nothing is lost for a genuinely stuck pod: the event repeats, `Count` climbs, and
the shipped `KubePodNotReady` rule covers it at two minutes as `PodNotReady`.

**The v0.7.0 relabelling change does not rescue this, and that is worth stating** rather than
discovering twice. `IncidentTriage` now re-labels an incident upward when a more specific signal
arrives, and `HighErrorRate` outranks `ReadinessFlapping` — but re-labelling deliberately stops
once the incident leaves `Detected`/`Triaging`, and an investigation starts within seconds of
detection while the competing signal is minutes away. The safety argument for that gate still
holds. It simply means the gate must not be relied on to correct a signal that arrives first and
is wrong.
