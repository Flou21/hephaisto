# Hephaisto roadmap

Written against what is **actually in the repo**, not against what was planned. Where the two
disagree, this file follows the code.

This file is **forward-looking only**. Two companions carry the rest:

- [`backlog.md`](backlog.md) — everything known-broken and unfixed, with the evidence for each.
  Milestones below link into it by number.
- [`history.md`](history.md) — what is already done, and what was learned doing it.

That rule has already earned its keep. The cause recorded here for the semantic-search bug turned
out to be wrong, and the correction was only possible because this document is meant to be checked
against the code rather than believed — see [backlog #9](backlog.md#9-semantic-search-returns-nothing-and-the-recorded-cause-was-wrong).

---

## Where it stands

`v0.3.0` is the current release. **The agent reaches people**: an escalation is written to a
Postgres outbox in the same transaction as the state change that caused it, and delivered to a
generic HTTP endpoint or a Teams card with retry, rate limiting and a link back to the incident.
It ships delivering nowhere — an empty routing table and no channel configured, two independent
things to change.

**The delivery path is measured, not claimed.** It ran against a real cluster and every assertion
passed, including the one the design exists for: the receiver taken down, the agent restarted
mid-flight, the receiver brought back, and the delivery arriving anyway.

**v0.2.0's acting criterion is still unmet**, and the three attempts at it are worth reading as a
sequence: gate 9 refused the restart; that was fixed; then the planner proposed no action at all.
[#41](backlog.md#41-c11-has-never-been-run-against-a-cluster) has the detail. It is not a safety
gate problem and never was after the first attempt.

`v0.2.0` shipped on 2026-08-30. **The agent can act**: it executes a narrow allowlist of
reversible actions, verifies them at T+60s / T+5m / T+15m with deterministic predicates,
reverts or escalates when they do not hold, and closes the incident when they do. It ships
configured to act nowhere — an empty namespace list, an empty autonomy list and `mode:
Observe`, three independent things to change.

The v0.2.0 acceptance test **has been run once, and it found three bugs.** Detection,
investigation and diagnosis are verified against a real cluster - 5 fixtures, 5 incidents, all
classified correctly, 2/2 graded correct for $0.56. **Acting is not yet demonstrated end to
end**, because the run stopped at the point it exists to test:

- Gate 9 refused every restart the feature exists for. It fired at `ReadyReplicas <= 1`, which
  includes zero, so it protected a replica that was not there - and a crash-looping pod is by
  definition not Ready. `RestartPod`, the one type promoted to auto, could never have fired.
- Verification passed on a still-crash-looping workload, which fails toward *yes*: no readiness
  probe means Ready the instant the container is Running, so a pod that runs two seconds per
  cycle reports one available replica for part of it.
- The harness made the identical mistake and reported "c11 is available after the restart"
  while the pod sat in CrashLoopBackOff with six restarts.

All three are fixed and unit-tested. **None of the fixes has been re-run against a cluster**,
which is deliberate: end-to-end re-verification is deferred to before v0.4.0, and until it
happens "the agent restarts a pod and closes the incident" remains built rather than observed.
See [backlog #41](backlog.md#41-c11-has-never-been-run-against-a-cluster).

`v0.1.0` shipped on 2026-08-29 after six release candidates, meeting its gate at **22/24
correct root cause** over cassette replay and 7/8 live. It took six candidates and the agent
was not the cause of any of them — five failed on the harness's own instrumentation rather than
on the thing being measured, which is worth remembering now that a second harness mode exists.

`v0.0.1` shipped the same week: multi-arch image and Helm chart on GHCR, build provenance
attested, both verified pulling anonymously.

---

## v0.1.0 — Diagnosis you can trust

**The gate on everything after it — and, measured on 2026-08-29, it is met.**

| | |
|---|---|
| **Cassette replay**, 3 passes over 8 scenarios | **22/24 correct**, 0 wrong, 2 no-finding, 180 steps, $1.92 |
| Excluding c10, which the harness itself flags as unmeasurable | **21/21** |
| **Live against the dev cluster**, one pass while recording | **7/8 correct** |

Per fixture, over three passes: c1, c2, c3, c4, c5, c7 and c8 are **3/3 each**, at 4.3 to 9.0 steps.
c10 is 1/3, and every c10 attempt failed the replay-coverage assertion, so the harness reports those
verdicts as unsound rather than as a score. **Nothing was ever diagnosed wrongly** — the two
failures are both "produced no finding at all", which stays the failure mode worth watching.

**Re-measured on the rc2 tree: 22/24 again**, 183 steps, $1.97 — c1, c2, c3, c4, c5, c7 and c8 all
3/3, c10 still 1/3. Two differences from the baseline row, both worth stating rather than rounding
away. The instrument came out *sounder* — 21 of 24 attempts passed every structural assertion,
against 19 — and one c10 attempt that produced no finding in the baseline produced a **wrong** one
here, so "nothing was ever diagnosed wrongly" is now a statement about the baseline run and not
about every run. Both c10 attempts that were not correct were flagged UNSOUND at 55–68% replay
miss, which is the harness saying those verdicts are not evidence about the agent — but the honest
version is that c10 has now been observed producing a wrong answer, not merely no answer.

Mean cost is **$0.080 per investigation** and mean length **7.5 steps**, against a `MaxSteps` of 12.
Both numbers matter for what comes next: the step ceiling is not the binding constraint, so the
experiment that assumed it was has already been answered.

**The previous number was 3 findings from 11 runs, and it is superseded rather than improved.**
Those eleven runs predate the transient-retry fix: 9 of 12 investigations were terminating
`Faulted` on the provider's own "high demand" wording, each discarding a complete run's tokens and
producing nothing. What looked like a reasoning problem was a retry problem. The full record stays
in [`history.md`](history.md#the-first-accuracy-measurement--2026-08-28) because a wrong diagnosis
that survived a week is worth keeping.

**So the four planned experiments are no longer gating work.** Raising `MaxSteps`, ordering tools
by `SignalKind`, capping discovery calls and runbook memory were all aimed at a number that has
already cleared the bar. They remain worth running — as **cost** experiments, against a baseline
that now exists, since 7.5 steps and $0.080 per investigation is the thing left to improve — but
nothing downstream waits on them.

### Done

1. **The eval harness.** `hephaisto-eval` with three commands: `record` runs a real investigation
   against the live cluster and captures every tool declaration and untruncated result; `run`
   replays a corpus and scores it, needing only the model; `inspect` reads a cassette. Recording is
   **in-process**, wrapping tools inside `SafeToolDecorator`, because a Postgres exporter cannot
   work — tool declarations are persisted nowhere, only 43 of 297 tool calls carried an untruncated
   blob, and arguments are stored post-redaction. It adopted
   [backlog #27](backlog.md#27-addhephaistollmwithoutpersistence-has-no-call-sites).

   Two decisions in it carry the number's credibility:

   - **The denominator is scenarios, not gradeable scenarios.** Both pre-existing instruments treat
     "no primary finding" as a *skip*, and no-finding was the dominant failure mode — so a
     regression that stopped producing findings would have pushed the reported score **up**.
   - **A high replay miss rate invalidates the instrument, not the agent.** Over 25% and the
     scenario is reported unsound; the answer is "re-record", never "it got worse". This is what
     flags c10 rather than quietly scoring it 1/3.

2. **[backlog #9](backlog.md#9-semantic-search-returns-nothing-and-the-recorded-cause-was-wrong) —
   fixed**, which was the dependency for runbook memory. The vector arm had never executed; a
   `pg_trgm` word-similarity arm was added beside it, and `q=crash` went from 0 hits to 10.

3. **Grafana annotations on state transitions — built in rc2.** `GrafanaAnnotator` marks the open
   as a point and the outcome as a region carrying the primary hypothesis, so a diagnosis is read
   against the graph it came from. It closes
   [backlog #20](backlog.md#20-the-mvp-acceptance-test-requires-grafana-annotations-which-are-unbuilt),
   whose instruction was "build the annotations or restate the test, do not silently drop the
   clause" — and the e2e now asserts them, so the clause in `verification.md` is checked rather
   than assumed. The token is a separate Editor service account: this is the only Grafana
   credential in the system that may write.

### Still open here

1. **Widen the corpus from 8 back toward 10 — deferred to v0.1.1.** c6 does not fire on
   `local-path` and c9 would evict the observability stack, so both need replacement fixtures that
   do not exist yet. That is open-ended design work against a gate already met at 22/24, so it is
   the one v0.1.0 item deliberately carried forward rather than finished. **The number stays n/8
   and says so.**

   Two defects found while recording bear on the fixtures that *are* in the corpus.
   [#31](backlog.md#31-grafana-mcp-exposes-no-tempo-tools-so-c10s-whole-reason-for-existing-is-untestable)
   (c10 cannot reach Tempo, so the correlation walk it exists to prove is untestable) is still open.
   [#34](backlog.md#34-c1-oomkill-never-produces-an-oomkill-on-this-node) (c1 presents as
   `CrashLoopBackOff`, never `OomKilled`) is open and documented as non-blocking.

   What rc2 *did* fix here is narrower and was blocking the widened e2e run:
   [#32](backlog.md#32-chaossh-maps-c10-to-sloburn-which-is-not-a-signalkind) mapped c10 to a
   `SignalKind` that does not exist, and nothing in `scripts/e2e/` ever built or `kind load`ed
   c10's image despite a comment saying it must — so asking for c10 produced an `ImagePullBackOff`
   and graded the agent on the test rig. All eight recordable fixtures now run in the harness,
   which also closes most of
   [#2](backlog.md#2-six-of-ten-chaos-fixtures-never-run-in-an-automated-gate).

### The experiments, now cost rather than accuracy

Kept because the method is worth running and the baseline exists; demoted because nothing waits on
them. One variable at a time, three repeats, against `results/baseline-*.json`:

- **Raise `MaxSteps`** from 12 — **probably pointless, and the baseline is why**: mean length is 7.5
  steps and the longest successful run was 13. Investigations are concluding, not running out. If
  it is tried anyway, `MaxWallClock` (10 min) and `MaxOuterTurns` (8) must move with it or they
  silently become the binding constraint, and exceeding `MaxOuterTurns` returns `Stalled`, which
  has no reserved-step rescue.
- **Rewrite `Runbooks/Unschedulable.md`**, which tells the model to query Loki and never mentions
  `get_events` or `list_nodes`. Note the baseline argues against this mattering much: c3 is 3/3 in
  5.7 steps despite it.
- **Cap discovery calls** in `SafeToolDecorator`, the only enforcement point that sees a tool name
  at decrement time.
- **Runbook memory** — retrieve the top-3 similar resolved incidents. The card must state that past
  incidents are **not citable**, or grounding discards anything quoted from them and the change
  *lowers* the finding count.

### Measurement integrity — done in rc2

A milestone whose entire purpose is producing a trustworthy number cannot ship on broken
instruments. All five landed:

- [#1](backlog.md#1-the-e2e-playwright-phase-reports-pass-on-a-zero-assertion-run) the console phase
  passed without asserting anything — 5 tests, 0 expected, 5 skipped, reported green. It now reads
  the JSON reporter's `stats` and fails on `expected == 0` or `skipped != 0`, and a lost exec bit no
  longer turns the phase into a silent skip.
- [#3](backlog.md#3-hephaistohumanfeedback-is-never-recorded) the false-positive rate is recorded.
  The instrument had to change on the way in: it emitted `helpful`/`unhelpful`, which the
  precision panel's `verdict=~"correct|incorrect|partial"` matches nothing of — so adding the
  missing call alone would have produced a metric that was recorded and still unreadable.
- [#4](backlog.md#4-hephaistoincidentsclosed-and-hephaistoincidentduration-are-never-recorded) MTTR
  is recorded, on **every terminal transition including `Escalated`**. That is what makes it
  measurable without [#11](backlog.md#11-there-is-no-production-path-to-resolved): in Observe mode
  nothing is fixed, so scoring only `Resolved` would have left the histogram exactly as empty.
- [#5](backlog.md#5-hephaistoincidentsopen-and-hephaistobudgetremaining-have-no-instrument-at-all)
  both instruments exist and are moved by production code. The guard test asserts *is recorded*,
  not *is created* — it drives the real `IncidentTriage` and listens through a `MeterListener` —
  and was itself verified by deleting a call site and watching it go red.
- [#6](backlog.md#6-audit-immutability-is-not-enforced-in-the-deployed-database) audit immutability
  is enforced. The agent serves on a **separate, non-owner role**, which is the only form this fix
  could ever have taken: Postgres cannot restrain a table's owner, so every version of this that
  kept the agent connected as `hephaisto` was enforcing nothing. "No audit, no action" is a
  standing constraint and now holds in the database as well as in the DbContext.

One decision inside #4/#5 is worth stating, because it makes two numbers deliberately disagree.
`hephaisto.incidents.open` decrements only when an incident leaves `OpenStates`, which does **not**
include `Escalated` — so it tracks `/api/status.openIncidents`, the number an operator will
cross-check it against. `hephaisto.incidents.closed` *does* count an escalation, because reaching a
human is an outcome. So `opened - closed != open`, on purpose. The dashboard's own spec table
claimed otherwise and was corrected rather than the code bent to match it.

### Exit criterion

**≥ 7/10 correct root cause over ≥ 10 seeded scenarios**, plus cost per investigation, time to
diagnosis, and the false-positive rate from the thumbs-up/down.

**Restated as n/8, and met.** Two of the ten fixtures cannot be recorded here and the reason is
written down for each: c6 does not fire on `local-path`, and c9 is node-wide and would evict the
observability stack along with the agent. Reporting n/10 while running eight would be the same
dishonesty the harness was built to remove. **Widening the corpus back toward ten is the one piece
of this criterion carried into v0.1.1** — it is about the denominator, not the ratio, and the ratio
is not in doubt.

**Which instrument produced which number, always.** The 22/24 is cassette replay. The 7/8 is live
against the dev cluster while recording. They were cross-checked on c4: recorded live, then
replayed, verdicts agreed with a **0% miss rate** — which is what makes the cheap instrument usable
for the rest. The two instruments disagree on nothing except c10, where replay recovered a finding
once in three attempts that the live run did not.

**All four numbers are now instrumented.** Cost per investigation has a baseline ($0.080 mean);
time to diagnosis is `hephaisto.incident.duration` and the false-positive rate is
`hephaisto.human.feedback`, both recorded by production code as of rc2. This criterion is defined
by all four, and until rc2 two of them had no value at all.

A caveat that belongs next to the claim rather than buried: instrumented is not the same as
populated. The false-positive rate needs a human to press the button — it is the one number in this
milestone the agent cannot generate for itself, which is exactly why it is worth having — so the
series exists and stays empty until someone reviews an incident.

**The gate that was set — if it lands at 4/10, v0.2.0 does not start — is passed.** It stays
written here because the executor being unbuilt until this held is the reason it can be trusted.

---

## v0.2.0 — It acts, carefully — **done**

**The executor exists, and the loop closes.** Investigating → Acting → Verifying → Resolved,
or → rollback → Escalated. Every edge in that sentence was implemented in
`IncidentStateMachine` a release early and called only from its own unit tests.

The roadmap's reading of this milestone — "almost everything except the executor already
exists, waiting for a caller" — was right about the inventory and wrong about the work. Four
things in that waiting machinery were **silently inert**, each failing in the direction that
looks fine, and none of them were in this file or the backlog:

| Found | Consequence |
|---|---|
| `PolicyOptions` was bound to configuration **nowhere** | The engine ran on a default-constructed instance, so `AllowedNamespaces` was empty and gate 2 denied everything — the right-looking answer for the wrong reason. The chart had been setting `Policy__AllowedNamespaces__N` since the write Role existed and nothing read it. |
| `ClusterFacts` was built with the clock, the mode and the quarantine stamp | Gates 3, 7, 8-fractional, 9, 10 and 13's budget downgrade were **all dead**, while passing their unit tests — the tests supply the facts the caller did not. |
| The database mode arm declared the seeded `agent_mode.mode` column | `mode: Auto` in the chart resolved to `Observe` on **every database that had ever been migrated**. The only way to lift it was a hand-written UPDATE. |
| `appsettings.json` — which ships in the image — named `hephaisto-chaos` | Binding `PolicyOptions` alone would have made the published default permit acting somewhere, with the chart's `actionableNamespaces` empty. |

None of these were visible from either side alone, which is the same shape as most of what
this project has already found. The first two would have been discovered by whoever first
turned autonomy on and watched the safety gates fail to fire.

**Gate 14 needed a decision, not an implementation.** It downgrades any action without a
rollback spec, and a pod delete has no inverse — the controller recreates the pod, which *is*
the restart. So `RestartPod`, the action this milestone exists to automate, could only reach
`Allow` if the model invented a fictional rollback spec, or stayed at `RequireApproval` forever
if it followed the prompt's own instruction to say plainly that an action cannot be undone.
Whether autonomy worked would have depended on how a model worded a JSON field. There is now a
named `SelfHealing` exemption, two types wide and pinned by a test, with the reasoning in the
code: **the recourse on a failed verification for these types is escalation, not rollback.**

### What shipped

| | |
|---|---|
| `ActionExecutor` | Snapshot → admit → mutate → record, over a closed enum. Five action types: `RestartPod`, `RolloutRestart`, `ScaleWorkload`, `DeleteStuckJob`, `DeleteFailedJobPods`. |
| `TryAdmitActionAsync` | Has a caller. It also had a latent duplicate-key bug — it always `Add`ed, and the coordinator already persists proposed actions — so it now transitions the row a proposal created. |
| `VerificationScheduler` | T+60s / T+5m / T+15m, deterministic C# predicates, only the last may conclude a failure. |
| `ActionRollback` | Typed reverts only; the model's rollback spec is read for values and never executed as written. |
| Approval | `POST .../approve` and `/deny`, UI buttons, `ApprovalSource.NotApplicable`. |
| Oscillation → quarantine | Against the **workload**, on the row admission already locks. |
| Path to `Resolved` | Granted by `hephaisto/verifier` once every executed action is verified. |
| Kubernetes Events | The action, on the object, for `kubectl describe`. |
| `c11-transient` | The only fixture a restart fixes. See [#41](backlog.md#41-c11-has-never-been-run-against-a-cluster). |
| Chart | `policy.autoEnabledActionTypes` as a first-class value, and the RBAC self-check's first positive assertion about writes. |

Closed: backlog [#7](backlog.md#7-the-planning-prompt-claims-a-verification-and-rollback-mechanism-that-does-not-exist),
[#8](backlog.md#8-nothing-writes-the-database-mode-arm) (by reclassification),
[#10](backlog.md#10-hephaistoiodestructive-actions-allowed-is-read-by-no-code),
[#11](backlog.md#11-there-is-no-production-path-to-resolved),
[#12](backlog.md#12-unbounded-label-cardinality-on-hephaistogroundingrejected),
[#16](backlog.md#16-four-declared-spans-are-never-started),
[#18](backlog.md#18-two-audit-event-types-are-named-and-never-written) (half), and
[#38](backlog.md#38-approval_source-reads-ui-on-actions-nobody-approved).

Also fixed because they stopped being harmless once the gates could fire: the maintenance
window had a gate and no producer, and `PolicyOptions` hot-reloads with nothing recording that
it moved.

### Mode is GitOps, and the database can only ever say no

The decision that shaped most of this milestone. The mode is a Helm value; it reaches the pod
on the env var and the projected ConfigMap, so raising autonomy is a reviewed commit. There is
deliberately **no endpoint and no UI control that sets it** — `SetModeAsync` is deleted, not
wired, and backlog #8 is resolved by reclassification rather than by building what it asked
for. The database arm restrains only: it carries the runaway latch and is otherwise silent.

The one write the switch exposes is **re-arm**, which clears a tripped latch and cannot name a
mode or exceed the deployment's ceiling. It writes `mode.changed` — the first thing ever to.

### What did not ship, and why

- **`PatchResources` and `RollbackDeployment`** — [#39](backlog.md#39-the-executor-covers-five-action-types-three-are-refused).
  `PatchResources` is the real remediation for c4 and c7, and doing it safely means a typed,
  restricted vocabulary rather than applying a model-authored merge patch verbatim. That is
  design work, not typing, and rushing it would hand the model the mutating handle the
  three-phase split exists to deny it. Refused before any call is made, so an approved one
  fails visibly rather than doing something unintended.
- **`SilenceAlert`** — needs an outbound client bound to Alertmanager, and a policy gate
  strict enough for an action whose whole effect is to stop a human being told. It arrives
  naturally with v0.3.0. (This bullet used to say the outbound HTTP client "does not exist in
  `src/`". It does — see the v0.3.0 section.)
- **A closed policy reason code** — [#40](backlog.md#40-policyresult-has-no-closed-reason-code-so-the-metric-cannot-say-why).
  Taking the free text out of the metric labels was urgent and is done; putting a safe
  breakdown back touches every gate in the safety argument and wants its own pass.
- **A cluster run of c11** — [#41](backlog.md#41-c11-has-never-been-run-against-a-cluster).
  The fixture is verified by simulating its container logic, not by running it. Until
  `--mode Auto` has been run once, the acceptance test below is written and unexecuted.

### Done when — status

A transiently-failing pod is auto-restarted, verification passes, the incident reaches
`Resolved`, and the audit trail reconstructs the decision **without reading a log file**; and a
seeded oscillating workload is quarantined after 3 attempts instead of looping forever.

**Half measured, half still a claim.** The run on 2026-08-30 proved the first half against a
real cluster: c11 applies, classifies as `CrashLoopBackOff`, and the agent proposes exactly the
right action for it. It then found three bugs that stood between the proposal and the restart -
see [Where it stands](#where-it-stands) - and those are fixed, unit-tested at 855 plus 28, and
**not re-run**.

So the honest state of the criterion is: everything up to and including "the agent decides to
restart the pod" is observed; everything after it is built and untested. Re-verification is
deferred to before v0.4.0 by decision, not by oversight.

---

## v0.3.0 — It reaches people — **done**

**Escalation leaves the process now.** An `INotificationChannel` abstraction, routing rules, a
Postgres outbox with retry, and two channels on it: a generic outbound HTTP endpoint and
Microsoft Teams. It ships delivering nowhere — an empty routing table and no channel configured,
two independent things to change, the same shape as `actionableNamespaces` plus
`autoEnabledActionTypes` plus `mode`.

**The premise this milestone was scoped on was wrong, and checking it made the work smaller.**
This file, `README.md` and backlog #39 all said there was no outbound HTTP anywhere in `src/`.
`GrafanaAnnotator` had been POSTing to Grafana through a client registered with `AddHttpClient`
since `v0.1.0-rc2`. What was missing was a *notification stack*, not the ability to make a
request — and the distinction mattered, because it made the annotator the **template** rather
than a counterexample: conditional registration, a `Null*` no-op, a per-call timeout linked to
the caller's token, a `Describe()` line at startup, and a standing rule that nothing in it may
fail an investigation. Corrected first, in its own commit, before any code — the discipline
backlog #7 established.

| Found | Consequence |
|---|---|
| `GrafanaAnnotator.Describe` is documented as *"reported once at startup"* and **had no caller** | Every outbound integration here degrades silently when unconfigured, so "nothing happened" read the same whether it was never switched on or was broken. Shipping notifications on that would have built the same trap one storey higher. Filed and fixed as [#43](backlog.md#43-grafanaannotatordescribe-is-documented-as-a-startup-line-and-has-no-caller). |
| `SilenceAlert` was in the policy engine's **LowRisk** set | Allow-eligible, so an operator could have promoted it and had the agent silence its own alerts unattended. It satisfies every word of that set's description; what it fails is subtler — every other low-risk action fails *visibly* when wrong, and a wrong silence fails by making the cluster look quiet. |
| `Notifications:GrafanaUrl` had no reader | Would have shipped as the third instance of [#19](backlog.md#19-maxautoscalereplicas-and-maxautoscalestep-have-no-readers), in the same release that closed the first two. Caught before the commit that introduced it. |
| `alertmanager.maxDuration` was written `2h` | What every other duration in a Kubernetes values file looks like, and `TimeSpan.Parse` rejects it outright. The agent would have failed to start with a binding error naming a key nobody would connect to that line. |
| `Math.Clamp` **propagates** `NaN` | A jitter value from a random source could throw out of `TimeSpan` multiplication on the delivery path. Found by a test written to assert the clamp, not the bug. |

### What shipped

| | |
|---|---|
| `NotificationEvent`, routing, rate limit, backoff | `Hephaisto.Core`, zero I/O, pure. Six events; `Unspecified = 0` so a default row cannot claim to be an escalation. |
| `notification_deliveries` | One row per (event × channel). Snapshot frozen at enqueue as `jsonb`; no FK to incidents, matching `audit_events`. |
| **Enqueue by construction** | A `SaveChanges` interceptor over new `IncidentEvent` rows. An incident cannot reach a notifiable state without a delivery row, because one commit writes both. |
| `NotificationDispatcher` | `VerificationScheduler`'s shape: prime once, `PeriodicTimer`, scope per tick, bounded read off `(status, next_attempt_at)`. The **only** retry authority — channels opt out of `AddStandardResilienceHandler`. |
| `HttpNotificationChannel` | Optional HMAC-SHA256 over the exact bytes sent, plus a stable delivery id for receiver-side dedup. |
| `TeamsNotificationChannel` | Power Automate Workflows, Adaptive Card 1.5. Links out; a test asserts no `Action.Submit` exists anywhere in the card. |
| `SilenceAlert` | Executor arm, always requiring approval, duration clamped, no call at all on a dry run. Closes a third of [#39](backlog.md#39-the-executor-covers-five-action-types-three-are-refused). |
| Chart | First-class values, closed enums in the schema, the Teams URL as a `secretRef` that a negative test proves can never render as a value. Egress NetworkPolicy, off by default. |
| e2e | A `notification-receiver` the harness builds and `kind load`s, and a `notify` phase whose fourth assertion restarts the agent mid-delivery. |

Closed: [#14](backlog.md#14-escalateonlyinvestigator-does-not-escalate),
[#17](backlog.md#17-hephaistokuberneteswatch_reconnects-bypasses-the-constants-file),
[#19](backlog.md#19-maxautoscalereplicas-and-maxautoscalestep-have-no-readers) (by deletion),
[#33](backlog.md#33-alertmanager-signals-lose-their-namespace-when-the-alert-labels-it-k8s_namespace_name),
[#35](backlog.md#35-allowedtools-is-documented-in-order-and-the-order-is-the-servers),
[#36](backlog.md#36-the-environment-card-never-names-a-datasource-uid-because-nothing-sets-them),
[#40](backlog.md#40-policyresult-has-no-closed-reason-code-so-the-metric-cannot-say-why),
[#43](backlog.md#43-grafanaannotatordescribe-is-documented-as-a-startup-line-and-has-no-caller),
and a third of [#39](backlog.md#39-the-executor-covers-five-action-types-three-are-refused).

### The decision that shaped the rest: enqueue is a consequence, not a call

The obvious design is an enqueue call at each of the ten places an incident commits a
transition. The obvious failure of that design was **already in this codebase**: `IncidentTriage`
reaches `Escalated` twice — the self-signal arm and the storm circuit breaker — and published no
live event at all. Nobody noticed, because nothing asserted it. An eleventh call site added next
year would have gone the same way.

`IncidentStateMachine.Transition` appends an `IncidentEvent` on every edge without exception, so
watching those rows gives the property directly rather than by diligence. The two silent triage
paths were covered the day the interceptor landed, without being touched.

**The guard test is the deliverable, not the interceptor.** It drives the real state machine
against a real database over all thirteen escalation reasons — and it was verified the way
`IncidentMetricsTests` was: commenting out the enqueue turns 15 tests red.

### What did not ship, and why

- **In-card approval.** Teams' interactive paths need a registered bot or Power Automate, and
  both mean accepting inbound calls on a service whose only inbound route is deliberately
  unauthenticated. A security change, not a feature increment. The identity story converges
  anyway: approving in Hephaisto's UI makes the free-text `ApprovedBy` the weak point, and the
  fix is OIDC — for a Teams shop, the same Entra ID the card was delivered through.
- **Slack, email/SMTP, PagerDuty.** Deferred as scoped. Two channels was the right number to
  design against; a third would have been designing against consumers that exist.
- **An approval-timeout sweeper.** `EscalationReason.ApprovalTimedOut` still has no producer, and
  now matters more: once a card says "approve this", *"awaiting approval and nobody was
  reminded"* is the slow-motion form of the failure this release fixed. Deciding what a timeout
  *does* is a policy question. [#44](backlog.md#opened-by-v030).
- **`PatchResources` and `RollbackDeployment`.** Unchanged from v0.2.0's reasoning.
- **A cluster run of anything.** See below.

### Done when — status

A fixture escalates, a card arrives in Teams and a body arrives at the outbound receiver each
carrying a working link; the receiver is taken down, the agent restarted, the receiver brought
back, and the delivery arrives anyway; a burst is rate-limited rather than repeated; and a stock
install delivers nowhere.

**Measured, against a real cluster, on 2026-08-30.** Every delivery assertion passed on two
independent runs:

```
pass  a notification reaches the receiver
pass  deliveries carry a stable delivery id
pass  deliveries carry a link back to the incident
pass  the delivered incident exists in the API
pass  a delivery survives an agent restart
```

The last one is the criterion. The receiver was taken to 503, an escalation queued against it,
the agent pod restarted **mid-flight**, the receiver brought back — and the delivery arrived. An
outbox that has never survived a restart is an outbox in name only.

Alongside it: 5/5 fixtures classified correctly, root cause **3/3 correct**, cost and token
ledgers reconciling with their per-step sums, 27 Grafana annotations, RBAC bounded, read-only and
non-root. 993 unit and 53 integration tests, and 45 chart checks.

**Three of the four clauses are met; two are not tested here and say so.** Teams needs a tenant
the harness does not have, and a signed delivery needs a Secret the chart deliberately will not
create — both are covered by unit tests, and neither is on the critical path.
See [#45](backlog.md#45-nothing-has-been-delivered-from-a-cluster).

**Three runs were needed to test one thing, and only one of the three failures was in the
product.** The first tested nothing, because the receiver image could not build against a
`.dockerignore` rule whose own comment describes that exact trap. The next two failed the
startup-line assertions while the agent emitted them perfectly — a container log is not a durable
record, and neither `--tail=400` nor `--tail=-1` can grep a startup line out of a rotated one.
That ratio is this harness's oldest pattern and it has not improved since v0.1.0's six release
candidates.

**v0.2.0's acting criterion is still not met**, and now for a third distinct reason: the planner
proposes no action for c11 at all. See
[#41](backlog.md#41-c11-has-never-been-run-against-a-cluster), which is a much sharper entry than
it was this morning.


---

## v0.4.0 — A design language

**Before anything visual gets built, decide what it should look like.** Three surfaces are coming —
the app UI that exists, a landing page, and a docs site — with nothing shared between them to build
against.

The honest starting position: **the app already has a design system, it is just not written down
and not reusable.** `src/Hephaisto.Agent/wwwroot/app.css` is 1268 lines of hand-written plain CSS
whose header states a real brief:

> Plain CSS, no framework, no CDN. This pod can run in a cluster with no egress, and an incident
> console whose stylesheet fails to load is unreadable at exactly the moment somebody needs it.
>
> Dark first, dense, monospace for anything a human might compare character by character — ids,
> workload keys, log excerpts, timestamps. Target reader: on call at 3am, on whatever monitor is in
> the room.
>
> STATE IS NEVER COLOUR ALONE. Every state, severity, risk and decision carries a glyph and a word
> next to it. Colour is the third channel, never the only one.

That is a better brief than most projects write. But it is a comment in one file, next to ~110 `hp-`
classes, a `:root` token block, and a light mode its own comment calls "a courtesy, not the design
target". Nothing else can consume it, and nobody deciding a landing-page question can find it.

### A — Direction, settled by asking before drafting

The forks that change everything downstream. Judgement calls, not technical ones, and answered
before any option is drawn:

- **Should the landing page look like the product, or contrast with it?** The biggest fork. Dark,
  dense and terminal-adjacent says "serious operator tool, here is exactly what you get". Light and
  editorial reaches people evaluating rather than operating. Both defensible; deciding late is
  expensive.
- **Is dark-first non-negotiable across all three surfaces, or app-only?** Docs are overwhelmingly
  read in light mode. A deliberate divergence is fine — an accidental one is not.
- **Does light mode stop being "a courtesy"?** A landing page brings evaluators, and some of them
  will open the UI in a bright room.
- **Typography, under a hard constraint.** The app cannot load a CDN — the pod may have no egress —
  so its fonts are self-hosted or system stacks. The landing page and docs have no such limit.
  Either the shared type system respects the tighter constraint, or the divergence is deliberate and
  documented.
- **How much personality?** Hephaisto is the god of the forge, which offers an obvious metaphor and
  an obvious cliché. Decide on purpose rather than drifting into anvils.
- **Who is the landing page's reader** — an SRE choosing a tool, a platform team assessing autonomy
  risk, or a potential contributor? The safety model is this project's most distinctive asset, and
  how prominent it is follows directly from this answer.

### B — Generate real options, not adjectives

**Three complete, genuinely distinct directions**, each rendered as something you can look at rather
than read about. A direction only counts as comparable if it commits to all of:

- a full palette in **both themes**, with contrast ratios checked rather than assumed
- a type pairing with a real fallback stack, honouring the no-CDN constraint
- a density and spacing scale — this app is deliberately dense at a 13px base, and a landing page
  must either inherit that or break it knowingly
- the same four hard components in every direction, so the comparison is like-for-like: an incident
  table row, a finding with its evidence citation, a budget meter, a code block
- a landing hero and a docs page in the same language

Judged side by side on the page, not in a spec.

### C — Choose one, then write it down

`docs/design.md` — the guideline this project does not have. It carries the rules that already exist
implicitly, plus everything A settled, and it is what a contributor is pointed at before touching
CSS.

### D — One token source, three consumers

The deliverable that makes this more than a document: **the `:root` custom properties become the
canonical token set**, and the VitePress theme and landing page consume the same tokens. A colour
that changes changes everywhere. Without it the guideline is advisory and the surfaces drift within
one release.

### E — Apply, with a safety net that must be repaired first

Refactoring 1268 lines of framework-free CSS onto new tokens is an ordered refactor, and the `hp-`
namespace is stable and semantic enough to survive it. There is nominally a safety net: a Playwright
suite and `data-testid` attributes.

**That suite currently runs nothing**
([backlog #1](backlog.md#1-the-e2e-playwright-phase-reports-pass-on-a-zero-assertion-run)). It is a
**hard dependency** of this milestone — without it the visual regression risk is entirely unmanaged.

Also lands here, because these are design outputs rather than project chores: the **wordmark**, the
**favicon** (currently disabled — `App.razor` has `<link rel="icon" href="data:," />`), and the
**social preview image**. The repo contains zero image files today.

Accessibility is part of acceptance, not a follow-up: contrast checked in both themes, visible focus
states, `prefers-reduced-motion` honoured.

**Done when** `docs/design.md` exists, one token source feeds all three surfaces, `app.css` is
refactored onto it with the UI unchanged except where the chosen direction says otherwise, and the
app has a favicon.

---

## v0.5.0 — Paying the debt down

**A release whose feature is that the list gets shorter.** Deliberately scheduled rather than
hoped for: every milestone so far has closed backlog items *alongside* a feature, which works
until the ones left are the ones no feature happens to touch. Three releases in, those are
accumulating.

The number is provisional — if v0.4.0 splits, this follows it regardless of what it ends up
called. What is not provisional is that it comes **after** v0.4.0 and before any new capability.

**Its contents are [`backlog.md`](backlog.md), not a list copied here.** A second ordering in this
file would drift from the first within a release, which is the reason priority lives in one place
and evidence in the other. What belongs here is the shape:

### The three that block a claim someone has already made

These are not the biggest, they are the ones that make an existing statement untrue:

- **[#41](backlog.md#41-c11-has-never-been-run-against-a-cluster)** — the planner proposes no
  action for c11, so v0.2.0's acceptance criterion remains unmet across three attempts and three
  distinct causes. Until it is settled, "the agent can act" is a statement about code rather than
  about behaviour. It is also the one item here that is genuinely open-ended: it asks whether the
  fixture is unfair or the planner is under-reading, and those want different fixes.
- **[#46](backlog.md#46-the-console-suite-cannot-pass-in-observe-so-a-green-run-needs---mode-auto)**
  — `run.sh` cannot exit 0 in its default mode. A harness that always fails is one people learn
  to read past, which is how #1 survived as long as it did.
- **[#2](backlog.md#2-six-of-ten-chaos-fixtures-never-run-in-an-automated-gate)** — the corpus is
  still n/8 against a bar written as n/10, carried since v0.1.0 and honestly labelled every time.

### The cheap ones that keep costing

**[#47](backlog.md#47-the-act-phase-reports-two-failures-that-are-consequences-of-the-first)**
(a report that is confidently wrong about why), **[#44](backlog.md#44-nothing-sweeps-awaitingapproval-so-approvaltimedout-has-no-producer)**
(nothing sweeps `AwaitingApproval`, which v0.3.0 made worse by putting an "approve this" card in
front of people), **[#13](backlog.md#13-the-retry-path-has-never-been-observed-firing-in-production)**,
**[#15](backlog.md#15-duplicate-instrument-registrations-with-conflicting-types-and-units)**,
**[#22](backlog.md#22-the-charts-budget-values-are-write-only)**,
**[#28](backlog.md#28-list_alert_rules-returns-empty-here-and-is-worked-around-in-the-prompt)**,
**[#31](backlog.md#31-grafana-mcp-exposes-no-tempo-tools-so-c10s-whole-reason-for-existing-is-untestable)**,
**[#34](backlog.md#34-c1-oomkill-never-produces-an-oomkill-on-this-node)**,
**[#37](backlog.md#37-the-judge-grades-a-different-incident-than-the-one-the-run-asserted-on)**.

### The rule this release exists to enforce

**An item leaves `backlog.md` by being fixed, or by being reclassified as a deliberate limitation
and written down somewhere permanent. It does not leave by being ignored.** That sentence has
been at the top of the file since it was written, and a scheduled release is what makes it
enforceable rather than aspirational.

### Done when

The blocking three are closed or reclassified with the reasoning recorded, `scripts/e2e/run.sh`
exits 0 in its default mode, and every remaining entry has been looked at once and either fixed
or given a fresh sentence saying why it is still there. **No new capability ships in it** — the
moment it grows a feature it becomes a release that also did some tidying, which is what every
release so far has been.

---

## The project track — landing page, docs, and the rest

Not version-numbered; a website does not version with the agent. Deliberately last: every page here
is an application of the design language, so building it first means building it twice.

### Where the site lives

**Same repository, `website/`. Not a separate repo.**

The argument for splitting is that a VitePress toolchain sits oddly in a .NET repo, and that site
commits add noise to the history. The argument against is decisive: **documentation in a separate
repo goes stale.** A PR that changes the HTTP surface should change the page describing the HTTP
surface, in the same diff and the same review. This repo documents itself unusually well and
unusually honestly; splitting the docs away is the most reliable way to lose that. The toolchain
objection is weak — `scripts/e2e/ui/` already carries a `package.json`.

**Hosting: GitHub Pages first, self-hosting kept open.** VitePress emits static files, so this is a
low-regret choice — a Pages workflow builds and deploys on push to `main`, the custom domain is a
DNS record, nothing to operate. If it should later live on a self-managed cluster, the same build
output goes into a small static image published by the same CI: a DNS change, not a rewrite.
Self-hosting a marketing site is ops work that buys nothing until there is traffic.

### Content mostly exists already

| Site section | Source |
|---|---|
| Landing / pitch | `README.md`'s one-liner and ASCII pipeline diagram |
| Architecture | `docs/architecture.md` |
| Install / operate | `README.md` "Running it", the chart's `values.schema.json` |
| Safety model | `README.md` "The safety model", plus the kill-switch material in [`history.md`](history.md) |
| Verification runbook | `docs/verification.md` |
| Incident reference | `src/Hephaisto.Agent/Runbooks/*.md` — 11 shipped runbooks |
| Chaos scenarios | `infra/chaos/README.md` |

### Screenshots should be generated, not taken

The repo contains no images. It does have a Playwright suite driving a UI with `data-testid`
attributes against a kind cluster full of real seeded incidents. So screenshots should be **captured
by a script in the e2e harness** — the alternative is a landing page showing a UI that shipped four
versions ago. Depends on [backlog #1](backlog.md#1-the-e2e-playwright-phase-reports-pass-on-a-zero-assertion-run).

### Branding

Settled by v0.4.0 and consumed here, not decided here.

### The rest — a checklist, all currently absent

- **Community files** — `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md` (there is no
  vulnerability reporting path at all today), `SUPPORT.md`, `CHANGELOG.md`, issue and PR templates,
  `CODEOWNERS`.
- **Repo metadata** — description, topics and homepage are empty. Discussions off. Wiki on and
  unused; turn it off rather than leave a second place for docs to rot.
- **Discoverability** — no `artifacthub-repo.yml`, so the chart publishes and nothing announces it.
  `Chart.yaml` has two Artifact Hub annotations but no `icon` (blank tile), no `maintainers`, no
  `screenshots`.
- **CI quality gates** — no `.editorconfig`, no `dotnet format`, no coverage, no link check, no
  spell check, no markdown lint, no CodeQL, no SBOM, no image scanning. Build provenance attestation
  is currently the only supply-chain signal.
- **README** — no badges, no screenshots.

---

## Housekeeping — three of four done in v0.2.0

1. ~~**The false verification claim in `Prompts/30-planning.md`.**~~ Corrected, in its own
   commit, *before* the mechanism was built — doing it the other way round would have meant a
   window in which the fix justified the lie.
2. ~~**`README.md`'s "Nothing is published yet".**~~ Replaced with the `helm install` line and,
   more usefully, with what it takes to make the agent able to act at all: four deliberate
   changes, in git.
3. **The GitHub description, topics and homepage.** Still empty. Ten minutes; the repo is
   already public. The one item here nobody can do from inside the repository.
4. ~~**The `grounding.rejected` cardinality bug.**~~ Fixed — and it turned up a worse instance
   of the same class on `hephaisto.policy.decisions`, which was writing timestamps into a label
   on a counter that fires for every proposed action.

---

## Later — a menu, not a queue

Roughly in order of value:

- **OIDC for approvals.** No schema change; `ApprovedBy` is populated from a verified claim instead
  of a text box. Stops being optional the moment more than one person operates this, or it points at
  anything that matters.
- **The deferred notification channels** — Slack, email/SMTP, PagerDuty/Opsgenie. The abstraction
  exists now: a channel is a `Name`, a `Describe()` and a `SendAsync` returning a
  `DeliveryResult`, with the outbox, routing, retry and rate limiting already behind it. Slack's
  incoming webhooks are the cheapest of the three.
- **Interactive in-card approval** — approve a `restart_pod` from the Teams card it arrived in,
  rather than following a link. Possible, and it is the payoff that joins this project's
  notification and approval halves; it is also the one item on this menu that changes the
  security posture rather than extending it. Written up below.
- **Change correlation** — "this started 4 minutes after the rollout of `x:sha`".
- **Postmortem generation**, drawing on the digest index for "this has happened N times".
- **Leading indicators** — PVC fill projection, memory trending to limit, cert expiry, HPA pinned at
  max.
- **Widen autonomy** to `rollout_restart` and `rollback_deployment`; widen namespaces.
- **Alert-noise reduction** — find chronically flapping rules, propose changes as PRs.
- **Topology and blast-radius reasoning** from the service graph.
- **MCP server mode**, so an agent can query incidents.
- **`--enforce-netpol`** tier in the e2e harness — Calico under kind, closing
  [backlog #23](backlog.md#23-networkpolicy-enforcement-is-unproven).
- Chaos self-testing, natural-language history queries, Pyroscope, multi-cluster.
- **Cheaper LLM providers**, Gemini Flash is a bit too expensive for rapid development and testing so a cheaper LLM solution should be searched
- **More expensive LLM providers**, Gemini Flash is pretty solid, but for real production usage a model like Opus or Fable are more appropriate

### Interactive in-card approval — what it would actually cost

The payoff is obvious: an escalation arrives in Teams, and the person who reads it approves the
restart without leaving the conversation. v0.3.0 deliberately shipped a **link** instead, and
that decision is worth revisiting only with the price written down, because the price is not the
card.

**The card is the easy part.** An Adaptive Card supports `Action.Submit` today, and the payload
Hephaisto already builds would need one more element. Nothing in `TeamsNotificationChannel`
resists this — there is a test asserting no `Action.Submit` exists anywhere in the card, and it
exists to make removing it a decision rather than a detail.

**The hard part is that Hephaisto would have to accept an inbound call from outside the
cluster.** Today its only inbound route is `/webhooks/alertmanager`, and the comment on it is
unusually blunt:

> That means the NetworkPolicy is load-bearing, not defence in depth. If it is ever removed or
> the pod is exposed through an Ingress, anything on the network can inject signals — which is a
> way to make the agent investigate whatever an attacker names, and in a future non-observe mode,
> to steer what it acts on. **Do not add an Ingress for /webhooks.**

Every interactive path requires Hephaisto to be reachable from Microsoft's side, which inverts
that posture. Doing it safely is a piece of security work, not a feature increment — which is
the whole reason v0.3.0 linked out.

#### Two mechanisms, and only one of them is cheap

**A — Power Automate holds the interaction.** The flow posts the card with
*"Post adaptive card and wait for a response"*, blocks, and on a click calls Hephaisto's existing
`POST /api/incidents/{id}/actions/{actionId}/approve`. Teams never talks to Hephaisto; the flow
does.

This is the cheap option and it has one genuinely useful property: **unlike Alertmanager, a Power
Automate HTTP action can set headers.** The whole reason `/webhooks` is unauthenticated is that
Alertmanager cannot send a credential — that constraint simply does not apply here, so the new
route can require a bearer token from a Secret and be a normal authenticated endpoint rather than
a second network-layer-only one. The exposure narrows to one authenticated path.

The costs are a flow that holds state for the life of an approval (with its own timeout, which
must not silently disagree with [#44](backlog.md#44-nothing-sweeps-awaitingapproval-so-approvaltimedout-has-no-producer)'s),
and Hephaisto being routable from Azure at all — an Ingress, or a tunnel.

**B — a registered bot.** Azure Bot Service, an Entra app registration, a Teams app package, and
Bot Framework JWT validation on an invoke endpoint. This is the only path to **Universal Actions
/ `Action.Execute`**, and therefore the only one that can *refresh the card in place*. Full-fat,
and a much larger surface.

#### The problem nobody thinks of first: stale cards

A card is delivered to a channel, and it stays there. Three people can open the same
already-approved action and press the button, and the second and third presses need to do
something sensible rather than something alarming.

The API is close to ready for this — `ReArmAsync` already sets the precedent with
`ReArmOutcome.NotLatched`, on the reasoning that *"a button that reports 'done' when it did
nothing teaches an operator that pressing it is meaningless"*. Approve and deny would need the
same treatment: idempotent, and able to say **"already decided by X at Y"** distinctly from
"approved just now". Only mechanism B can then update the card to show it; mechanism A can only
reply.

#### It could improve the identity story, or quietly wreck it

`ApprovedBy` is free text today — attribution, not authentication. A Teams click *knows who
clicked*, so in principle this is an upgrade.

In practice it is only an upgrade if the path is authenticated end to end. A Power Automate flow
can put any string in that field, so mechanism A without a verified claim moves the trust from
"whoever typed a name into a console" to "whoever can invoke the flow" — different, and not
obviously better. **OIDC should land first**, and for a Teams shop that is Entra ID, the same
directory the card was delivered through. The two converge, which is exactly why linking out cost
nothing.

#### Ordering, if this is ever picked up

1. **[#44](backlog.md#44-nothing-sweeps-awaitingapproval-so-approvaltimedout-has-no-producer)
   first.** The common failure is not that approving is inconvenient, it is that **nobody clicks
   at all** and nothing says so again. A sweeper is an afternoon; a bot is not.
2. **OIDC second**, so the identity the card asserts is one Hephaisto can verify.
3. **Then mechanism A**, with an authenticated route and idempotent approve/deny — most of the
   value, a fraction of the surface.
4. **Mechanism B only if card refresh proves necessary**, which is a question about how the cards
   read in a busy channel, and is unanswerable until people have lived with the link-out version.

---

## Standing constraints

- **The cluster is a single shared resource.** Code and unit tests parallelise; cluster verification
  does not.
- **Never `tilt down`** — it `helm uninstall`s the stack and takes Grafana's PVC with it.
- **Approval identity is attribution, not authentication** until OIDC lands. The risk to watch is
  habituation.
- **No audit, no action.** If Postgres is unreachable the executor must refuse.
- **Promote autonomy per action type, never globally.**
- **Config needs a reader in `src/` in the same commit.** Config that reads like configuration and
  behaves like a comment is worse than no documentation — see
  [backlog #19](backlog.md#19-maxautoscalereplicas-and-maxautoscalestep-have-no-readers) for the two
  that got through.
