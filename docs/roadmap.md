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

## Step 1 — first light against the cluster — **done, 2026-08-28, with one bug open**

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

### Open: investigations do not persist

**The one thing not working.** Investigations run — the model is called, it uses tools, it
terminates — and then the save fails, so `investigations`, `steps`, `findings` and `evidence`
are all still zero and incidents sit in `Investigating`.

The cause is a class of EF Core bug this session fixed three instances of. Every domain
entity assigns its own key (`Guid.CreateVersion7()`), so when change detection discovers one
through a navigation it sees a set key, concludes the row exists, and emits an UPDATE that
matches nothing. It only bites on an **already-persisted** incident: a new one goes in via
`Incidents.Add`, which marks the whole graph Added.

Fixed instances: signals (broke dedup and correlation), the investigation graph, the plan and
its actions, and the escalation event on the failure path.

The remaining one is reported by the new diagnostic as:

```
offending entity: IncidentEvent state=Modified key=01a047c5-...
```

Every `stateMachine` call site is now accounted for, so the next step is to log **all**
tracked entries at save time rather than only the offending one, and identify which event is
Modified without a row behind it. Worth ruling out that a state transition mutates a
*previous* event rather than only appending a new one.

**Two lessons worth keeping.** `dotnet watch` silently declined several edits with *"No
managed code changes to apply"*, so a run of iterations tested stale code — when a fix seems
not to take, `tilt trigger hephaisto` for a real image build before believing the result.
And the diagnostic that names the offending entity found in one iteration what inference had
not found in five; it is committed, and it should be reached for early.

---

## Step 2 — measure, before deciding anything

**This is the real output of the MVP, and the gate on everything after it.**

Run the ten chaos fixtures. For each, record whether the agent named the correct root cause,
citing grounded evidence, and changed nothing.

- Target: **≥ 7/10 correct root cause** over at least 10 seeded scenarios.
- Also record: cost per investigation, time to diagnosis, and the false-positive rate from the
  thumbs-up/down.

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
