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

`v0.2.0` is the current release. **The agent can act**: it executes a narrow allowlist of
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

## v0.3.0 — It reaches people

Today **nothing leaves the process on an incident's behalf.** Escalation is a database state
change, an `incident_events` row, an audit row, and a nudge to any open browser tab. The only
out-of-band path to a human is *your* Alertmanager firing on Hephaisto's self-check rules, and
that rule file ships disabled.

**There is outbound HTTP in `src/`, and this file said there was not.** `GrafanaAnnotator` posts
to Grafana's `/api/annotations` through a typed client registered with `AddHttpClient`
(`Llm/LlmServiceCollectionExtensions.cs:66`), and `ServiceDefaults` applies
`AddStandardResilienceHandler` to every client the factory builds. What is missing is a
*notification* stack, not the ability to make a request — and the distinction matters, because
`GrafanaAnnotator` is the template this milestone should copy rather than a counterexample:
conditional registration, a `Null*` no-op when unconfigured, a per-call timeout linked to the
caller's token, a one-line `Describe()` at startup saying whether it is on and why not, and a
standing rule that nothing in it may fail an investigation. (No `PostAsJsonAsync` — it uses
`JsonContent.Create` — and no notification package: the two halves of the old claim that were
true.)

**Scope: two channels — a generic webhook and Microsoft Teams.** Slack, email/SMTP and
PagerDuty/Opsgenie are deliberately deferred to [Later](#later--a-menu-not-a-queue). Building
`INotificationChannel` properly is what makes each of them a small, self-contained addition
afterwards.

### Three traps to design around

**`IIncidentNotifier` is not the transport.** It is an in-process `Channel<T>` fan-out to Blazor
circuits, bounded at 64 with `DropOldest`, and it never blocks and never throws. That makes it a
fine *hook point* and a catastrophic *delivery mechanism* — it is designed to drop. Delivery needs
an **outbox with retry**, because "escalated, and nobody was told" is the worst failure this system
has, and a pod restart must not be able to cause it.

**Microsoft retired Office 365 connectors in Teams.** The classic "Incoming Webhook" URL in every
tutorial is deprecated and being switched off. The supported path is a **Power Automate Workflows**
trigger, or a Graph-based bot for anything interactive. Confirm against current Microsoft
documentation when implementing rather than following an old blog post into a dead end.

**A notifier can amplify a storm.** Ingest has dedup, flap suppression and a storm circuit breaker.
The outbound side inherits none of it. Per-channel rate limiting, and reuse of the existing
fingerprint, are part of the feature rather than a follow-up.

### Order

1. **`INotificationChannel`, routing rules, and the outbox.** Which kinds, severities and namespaces
   go where; at-least-once delivery with retry. Two channels is the right number to design against —
   one lets channel-specific detail leak into the core.
2. **Generic webhook first.** No third-party account, so it is testable in the e2e harness against a
   local sink, and it proves the outbox and routing before any vendor-shaped payload is involved.
   It is also the escape hatch for anyone using something this milestone does not ship.
3. **Microsoft Teams**, via a Power Automate Workflows trigger posting an Adaptive Card.
4. **Egress NetworkPolicy.** The chart is `policyTypes: [Ingress]` only. Nothing forces this today,
   but once the agent posts outward it is worth being explicit about where it may talk.
5. **Deep links in every message.** `generate_deeplink` is already an allowlisted grafana-mcp tool,
   so a card can carry a real Grafana Explore link beside the diagnosis, plus a link to the incident.
6. **Secrets by `secretRef` only.** A Workflows trigger URL is a bearer credential in a query
   string and must never be a plaintext value.

### Approvals from Teams — and why v1 should not be interactive

The payoff that joins this milestone to v0.2.0 is approving a `restart_pod` from where the
escalation arrived. But Teams is the harder platform for it: its interactive paths go through Power
Automate or a registered bot, and both mean accepting inbound calls on a service whose only current
inbound route is deliberately unauthenticated and protected solely by NetworkPolicy. That is a real
security change, not a feature increment.

So **v1 of the card carries a deep link into Hephaisto's own approval UI**, not an Approve button.
No new inbound surface, no signature verification, and the approval still happens where the audit
row already lives. In-card approval gets its own gate later.

That also lines the identity story up correctly: approving in Hephaisto's UI makes the free-text
`ApprovedBy` the weak point, and the fix is OIDC — which for a Teams shop is Entra ID, the same
directory the card was delivered through. Interactive approval and OIDC approval converge on the
same answer, so the link-out costs nothing and skips a throwaway design.

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
- **The deferred notification channels** — Slack, email/SMTP, PagerDuty/Opsgenie. Small additions
  once v0.3.0's abstraction exists.
- **Interactive in-card approval**, with the inbound signature verification it requires.
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
