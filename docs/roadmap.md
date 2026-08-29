# Hephaisto roadmap

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

## Where the project stands today

**Built, and verified by running it:**

| Area | State |
|---|---|
| `Hephaisto.Core` — domain, state machine, policy engine, digester, oscillation, fingerprinting | Complete, zero I/O, ~500 unit tests |
| Persistence — Postgres 17 + pgvector, migrations, hybrid RRF search, LLM budget, audit trail | Complete; admission is one `Serializable` transaction |
| Kubernetes — watchers, 17 read-only tools, RBAC self-check, signal mapping | Complete, read-only by construction |
| LLM — Gemini client, grafana-mcp, three-phase loop, grounding verifier, budget guard | Complete |
| Ingest — dedup, flap suppression, correlation, storm breaker | Complete |
| Blazor UI + HTTP API + webhooks | Complete |
| Observability stack, alert rules, 10 chaos fixtures, RBAC manifests, Tiltfile | Complete, chart-rendered and PromQL-parsed |

**Not built.** All of it is Phase 2 or later by design, except the first row:

| Missing | Belongs to |
|---|---|
| `HEPHAISTO_MODE` env var and ConfigMap kill switches | **Should have been MVP — see below** |
| Grafana annotations on state transitions | MVP item 10, deferred |
| `ActionExecutor`, `dryRun=All` shadow, `PreState` snapshots | Phase 2 |
| `VerificationScheduler`, auto-rollback | Phase 2 |
| Approval workflow and its UI | Phase 2 |
| Kubernetes Event mirroring onto target objects | Phase 2 |
| Runbook memory (retrieving similar past incidents into the prompt) | Phase 2 |
| Eval harness | Phase 2 |

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

## Step 2 — measure, before deciding anything

**This is the real output of the MVP, and the gate on everything after it.**

Run the ten chaos fixtures. For each, record whether the agent named the correct root cause,
citing grounded evidence, and changed nothing.

- Target: **≥ 7/10 correct root cause** over at least 10 seeded scenarios.
- Also record: cost per investigation, time to diagnosis, and the false-positive rate from the
  thumbs-up/down.

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

## Open — carried forward

Verified against the running cluster on 2026-08-28, not inferred. Roughly in priority order.

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

### 2. Audit immutability is not enforced in the deployed configuration

"No audit, no action" is a standing constraint and, in the deployed database, nothing enforces
it. Measured:

```
connected_as     | hephaisto
app_role_exists  | 0
is_superuser     | t
can_update_audit | t
can_delete_audit | t
```

The agent connects as a **superuser**, and the `hephaisto_app` role does not exist — so the
migration's `GRANT`/`REVOKE` block, wrapped in `IF EXISTS (SELECT 1 FROM pg_roles ...)`,
silently no-opped. The integration test that asserts a 42501 on `UPDATE audit_events` passes
in CI (which creates the role) and proves nothing about this cluster.

Fix: create `hephaisto_app`, connect as it rather than as the owner, and re-run the migration
so the REVOKE actually applies. The chart should make the connecting role a value.

### 3. The retry path has never been observed firing in production

`TransientRetryChatClient` is unit-tested nine ways, and the overload it exists for has not
recurred since. It is the difference between "tested" and "proven", and it is worth forcing
once — a fault-injecting `IChatClient` behind a dev-only flag would settle it.

### 4. Semantic incident search returns `[]` for some queries

`/api/incidents/search?q=out+of+memory` and `q=crash` return empty while `q=ImagePullBackOff`,
`q=image` and `q=pod` work. Hybrid RRF over pgvector + pg_trgm, so the likely suspects are the
embedding of short conceptual queries, or an RRF weighting that lets exact-match dominate.
Unmeasured — do not guess at the fix before reproducing it against a fixed corpus.

### 5. Two more declared metrics are never emitted

`hephaisto.incidents.open` (an UpDownCounter) and `hephaisto.budget.remaining` (a gauge) are
declared in `HephaistoTelemetry.cs` and drawn on the dashboard, and no instrument is ever
created for either. Same class of bug as `hephaisto.llm.budget_utilization`, which was fixed on
2026-08-29 — that one had two alert rules resting on it, which is why it went first.

The general fix is worth more than either: a test asserting that every `hephaisto_*` metric
named in the shipped alert rules and dashboard corresponds to a real instrument. It cannot be
added until these two are emitted, because it would fail on them.

### 6. The chart's budget values are write-only

`extraEnv` (added 2026-08-29) makes `Llm__Budget__*` settable, which unblocked the e2e harness,
but they are still not first-class values. Someone reading `values.yaml` cannot tell that a
budget exists. Worth promoting the four caps to real values once their names have settled.

### 7. NetworkPolicy enforcement is still unproven

The Alertmanager webhook is unauthenticated and the NetworkPolicy is its entire authentication.
Neither CI nor `scripts/e2e/run.sh` proves it works: kind's default CNI accepts the objects and
does not enforce them. Testing it means `disableDefaultCNI: true` plus Calico in the harness's
kind config — real install time and a real flake risk, so it is a documented `--enforce-netpol`
tier rather than part of the default run. Until then this is verified by reading, on a cluster
whose CNI does enforce.

### 8. Loose ends, small

- The old `data-postgres-0` PVC is orphaned on the dev cluster (the chart names its database
  `hephaisto-postgres`). Deliberately left; the data was dumped and restored first.
- `values-dev.yaml` sets `networkPolicy.extraIngressCIDRs: ["0.0.0.0/0"]` so kubelet probes
  work on this node. That is the webhook's entire authentication, disabled. Acceptable only
  because the cluster is single-tenant and reachable from one private network.
- The workflows have never run — there is no remote yet. They are statically validated only.

---

## Phase 3 — it acts, carefully

Only after Step 2 says the diagnoses are good enough. Order matters:

1. `ActionExecutor` with `dryRun=All` and `PreState` snapshots. **Run in `dryrun` for two
   weeks.** The would-have-acted log is the evidence for enabling anything.
2. `VerificationScheduler` at T+60s / T+5m / T+15m, plus auto-rollback.
3. Oscillation detector wired to quarantine (the pure logic is already built and tested).
4. Approval workflow and UI, capturing the free-text `ApprovedBy`.
5. Bind the write `RoleBinding` — **into `hephaisto-chaos` only.**
6. Enable `auto` for exactly **one** action type: `restart_pod`.
7. Mirror actions to Kubernetes Events on the target object, so `kubectl describe pod` shows
   why something was restarted. That is where an on-call engineer actually looks.

Done when a transiently-failing pod in `hephaisto-chaos` is auto-restarted, verification
passes, the incident closes, and the audit trail reconstructs the whole decision **without
reading a log file** — and a seeded oscillating workload is quarantined after 3 attempts
instead of looping forever.

---

## Phase 4 and beyond — a menu, not a queue

Roughly in order of value:

- **OIDC for approvals.** No schema change; `ApprovedBy` is populated from a verified claim
  instead of a text box. This stops being optional the moment more than one person operates
  this, or it points at anything that matters.
- **Change correlation** — "this started 4 minutes after the rollout of `x:sha`".
- **Postmortem generation**, drawing on the digest index for "this has happened N times".
- **Leading indicators** — PVC fill projection, memory trending to limit, cert expiry, HPA
  pinned at max.
- **Widen autonomy** to `rollout_restart` and `rollback_deployment`; widen namespaces.
- **Alert-noise reduction** — find chronically flapping rules, propose changes as PRs.
- **Topology / blast-radius reasoning** from the service graph.
- **MCP server mode** so Claude Code can query incidents.
- Chaos self-testing, natural-language history queries, Slack surfacing, Pyroscope,
  multi-cluster.

---

## Standing constraints

- **The cluster is a single shared resource.** Code and unit tests parallelise; cluster
  verification does not.
- **Never `tilt down`** — it `helm uninstall`s the stack and takes Grafana's PVC with it.
- **Approval identity is attribution, not authentication** until OIDC lands. The risk to watch
  is habituation.
- **No audit, no action.** If Postgres is unreachable the executor must refuse.
- Promote autonomy **per action type**, never globally.
