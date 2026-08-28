# Watchtower roadmap

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

## Where the project stands today

**Built, and verified by running it:**

| Area | State |
|---|---|
| `Watchtower.Core` — domain, state machine, policy engine, digester, oscillation, fingerprinting | Complete, zero I/O, ~500 unit tests |
| Persistence — Postgres 17 + pgvector, migrations, hybrid RRF search, LLM budget, audit trail | Complete; admission is one `Serializable` transaction |
| Kubernetes — watchers, 17 read-only tools, RBAC self-check, signal mapping | Complete, read-only by construction |
| LLM — Gemini client, grafana-mcp, three-phase loop, grounding verifier, budget guard | Complete |
| Ingest — dedup, flap suppression, correlation, storm breaker | Complete |
| Blazor UI + HTTP API + webhooks | Complete |
| Observability stack, alert rules, 10 chaos fixtures, RBAC manifests, Tiltfile | Complete, chart-rendered and PromQL-parsed |

**Not built.** All of it is Phase 2 or later by design, except the first row:

| Missing | Belongs to |
|---|---|
| `WATCHTOWER_MODE` env var and ConfigMap kill switches | **Should have been MVP — see below** |
| Grafana annotations on state transitions | MVP item 10, deferred |
| `ActionExecutor`, `dryRun=All` shadow, `PreState` snapshots | Phase 2 |
| `VerificationScheduler`, auto-rollback | Phase 2 |
| Approval workflow and its UI | Phase 2 |
| Kubernetes Event mirroring onto target objects | Phase 2 |
| Runbook memory (retrieving similar past incidents into the prompt) | Phase 2 |
| Eval harness | Phase 2 |

---

## Step 0 — close the kill-switch gap (do this first)

**The design specified three independent kill switches. Only one is wired.**

`AgentMode` is read solely from the `agent_mode` database row. The `WATCHTOWER_MODE`
environment variable is set by `infra/app/watchtower.yaml` and by the Aspire AppHost, and the
manifest carries a ConfigMap `mode` key with a comment saying the two "must agree" — but
**no code reads either of them.**

Today this is harmless, because the missing controls fail in the safe direction: the database
row defaults to `Observe`, and nothing can execute anything anyway. It stops being harmless
the moment Phase 2 lands, and the dangerous direction is the one that looks safe:

> An operator who sets `WATCHTOWER_MODE=observe` to **stop** an agent running in `auto` will
> find it keeps acting. The big red button is painted on.

Fix before any executor work:

1. Read `WATCHTOWER_MODE` at startup; the **most restrictive** of (env, ConfigMap, DB row)
   wins. This is deliberately not "last writer wins".
2. Add the `SwitchWatcher` hosted service watching the ConfigMap live, so the button takes
   effect in seconds without a restart.
3. An unreadable ConfigMap or env value reads as `Observe`, never as `Auto`. Unreachable
   Postgres means refuse to act.
4. Unit-test the precedence table, including every unreadable-source case.

---

## Step 1 — first light against the cluster

Nothing has ever run in k3s. This is a day's work and mostly verification.

```sh
cd ~/watchtower && ./scripts/dev-db.sh up   # optional, for local dev
tilt up --port 10351
```

Then work `docs/verification.md` top to bottom. The two that matter most:

- **Step 4, span metrics reaching Prometheus.** If `traces_spanmetrics_calls_total` is empty,
  Tempo's generator samples are being rejected as out-of-order and nothing says so.
- **The five-hop correlation test.** Exemplar → trace → logs → metrics → service graph. If all
  five work, the entire observability stack is proven at once.

Expect to spend the time on chart reality rather than code: the values files are rendered and
schema-checked but have never been *applied*.

Migration of the old stack (`docs/verification.md` and the plan's §6) is deliberately manual
and destructive — repoint CaitBackstage and the `litellm-config` ConfigMap first, and back up
the old Grafana PVC, which holds hand-made dashboards in SQLite and nothing else.

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
5. Bind the write `RoleBinding` — **into `watchtower-chaos` only.**
6. Enable `auto` for exactly **one** action type: `restart_pod`.
7. Mirror actions to Kubernetes Events on the target object, so `kubectl describe pod` shows
   why something was restarted. That is where an on-call engineer actually looks.

Done when a transiently-failing pod in `watchtower-chaos` is auto-restarted, verification
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
