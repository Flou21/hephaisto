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

`v0.0.1` shipped on 2026-08-29: multi-arch image and Helm chart on GHCR, build provenance attested,
both verified pulling anonymously.

`v0.1.0-rc1` followed the same day, carrying the eval harness and the hybrid-search fix. It was a
release candidate with most of the milestone still open: Grafana annotations, four unrecorded
metrics, audit immutability in the deployed database, and the corpus at 8 fixtures rather than 10.

`v0.1.0-rc2` closes all of that except the corpus. Grafana annotations are built and asserted in
the e2e; the four metrics are recorded by production code and guarded by a test that asserts *is
recorded* rather than *is created*; and the agent now serves on a non-owner Postgres role, which is
the only way audit immutability was ever going to hold — privileges cannot restrain a table's
owner. **Widening the corpus from 8 back toward 10 is deferred to v0.1.1**, deliberately and with
the reason written down: c6 does not fire on `local-path` and c9 would evict the observability
stack, so both need replacement fixtures that do not exist yet, and inventing them is open-ended
work against a gate that is already met at 22/24.

**`v0.1.0-rc6` is green: 68 assertions passed, 0 failed, 2 skipped, in 9m19s for $1.41.** All
eight fixtures detected, 11 investigations all terminating `Concluded` and all citing evidence,
6/6 graded correct, 41 Grafana annotations written and read back with the agent's own token,
Observe mode held with zero actions executed, and the console suite reporting
`expected=5 skipped=0`. The two skips are the documented case where a fixture is detected by a
different shipped rule than the README names, which is a fact about rule `for:` durations racing,
not a detection failure.

**It took six release candidates, and the agent was not the cause of any of them.** rc2 could not
install; rc3, rc4 and rc5 each failed on the *harness's own instrumentation* rather than on the
thing being measured. That is worth stating plainly rather than rounding off, because a milestone
whose product is a trustworthy number spent five attempts discovering that its instruments lied:

- the incident wait counted incidents instead of requiring one per fixture, so the slowest fixture
  was failed while still on schedule;
- c10's incident was there the whole time under a target the harness never looked for, and was
  reported as undetected across two candidates;
- the `approvedBy` assertion covered actions the policy engine had **Denied**, which have no
  approver by construction;
- the console phase had no timeout, and its one hanging step was the one step with its output
  suppressed;
- the rule-selection check sampled once, 20 seconds before the operator finished reconciling.

Every one of those produced a red run about a healthy agent. None would have been found by the
four-fixture default set.

**`v0.1.0-rc2` does not install, and rc3 is the fix.** The e2e caught it at the phase it exists to
cover: `helm install` timed out with `Deployment/hephaisto not ready`. Making the agent serve on a
non-owner role had repointed the registered `DbContext` at that role, and migrations run through
that same `DbContext` — so on a database being migrated for the first time the agent tried to
authenticate as a role nothing had created yet, and never started. It passed every local check
because every developer and dev-cluster database already had the role; only a genuinely fresh
install could show it. The startup sequence is now one method whose ordering cannot be got wrong —
create the role, migrate as the owner, then grant — and two integration tests fail without it.

A release candidate still selects by no version range; the rc exists to exercise the publish path
for real, and this is exactly what it caught.

**Built, and verified by running it:**

| Area | State |
|---|---|
| `Hephaisto.Core` — domain, state machine, policy engine, digester, oscillation, fingerprinting | Complete, zero I/O, ~500 unit tests |
| Persistence — Postgres 17 + pgvector, migrations, hybrid search, LLM budget, audit trail | Complete; admission is one `Serializable` transaction |
| Kubernetes — watchers, 17 read-only tools, RBAC self-check, signal mapping | Complete, read-only by construction |
| LLM — Gemini client, grafana-mcp, three-phase loop, grounding verifier, budget guard | Complete |
| Ingest — dedup, flap suppression, correlation, storm breaker | Complete |
| Blazor UI + HTTP API + webhooks | Complete |
| Kill switch — three independent arms, most restrictive wins | Complete |
| Observability stack, alert rules, 10 chaos fixtures, RBAC manifests, Tiltfile | Complete |
| Release — three channels (release / rc / nightly), and `scripts/e2e/run.sh` | Complete |

**What it does not do yet:** act on anything, tell anyone, or look like a project rather than a
repository. That is what the milestones below are.

### The order, and why

Diagnosis quality → acting → notifications → design language → open source.

Each stage is a precondition for the next being worth doing. Acting on diagnoses that are wrong is
worse than not acting. Paging a team with diagnoses nobody trusts teaches them to ignore the
channel. Building three surfaces before there is a design language means building them twice. And
marketing any of it before the rest is true is the one ordering that cannot be undone.

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

4. **Widen the corpus from 8 back toward 10 — deferred to v0.1.1.** c6 does not fire on
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

## v0.2.0 — It acts, carefully

Gated on v0.1.0's number.

**This is much less greenfield than it looks.** Almost everything except the executor already
exists, built and tested, waiting for a caller:

| Component | State |
|---|---|
| `ActionRepository.TryAdmitActionAsync` | Complete `Serializable` admission — workload row lock, kill switch re-resolved inside the transaction, quarantine, five budget and cooldown gates, INSERT plus audit row in one commit. **Zero callers.** This is the seam. |
| `PolicyEngine` | Complete, default-deny, 14 numbered gates. Already runs for real on every investigation, even in Observe. |
| `AgentAction.DryRun` / `PreState` / `PostState` / `RollbackSpec` / `Outcome` / `IsRollbackOf` | Schema, migrations, indices. **Written by nothing.** |
| `Verification` entity | Table, DbSet, indices. **Never constructed.** |
| `OscillationDetector` | Pure, fully tested. **Never called.** |
| `AwaitApproval` / `BeginActing` / `BeginVerifying` / `Resolve` / `Reopen` | Implemented; **called only from tests**. |
| Write RBAC Role — delete pods, patch workloads and `*/scale`, delete jobs, create events | Rendered per `policy.actionableNamespaces`, empty by default. **Entirely unused.** |
| `ActionExecuted` / `ActionRolledBack` / `VerificationResult` metrics | Instrumented, zero callers |

### Order

1. **Fix the false claim in the prompt first**
   ([#7](backlog.md#7-the-planning-prompt-claims-a-verification-and-rollback-mechanism-that-does-not-exist)).
   The model is currently told that actions are checked at 60s / 5m / 15m and that a failed check
   triggers a rollback. Nothing of the sort exists. Correct the text immediately — see
   [Housekeeping](#housekeeping--small-and-now) — and build the mechanism at step 4.
2. **Wire the mode writer** ([#8](backlog.md#8-nothing-writes-the-database-mode-arm)). The arm
   documented as "the one a human flips from the UI" has no UI, no API, and no way to clear a
   tripped runaway latch without opening Postgres. A prerequisite for operating Act mode, not a
   nicety.
3. **`ActionExecutor`**, calling `TryAdmitActionAsync`, with `dryRun=All` and `PreState` snapshots.
   **Run in `dryrun` for two weeks.** The would-have-acted log is the evidence for enabling
   anything.
4. **`VerificationScheduler`** at T+60s / T+5m / T+15m, plus auto-rollback — making (1) true.
5. **Oscillation detector wired to quarantine.** The pure logic is built and tested; nothing calls
   it, and only flap suppression writes `QuarantinedUntil` today.
6. **Approval workflow and UI**, writing `ApprovedBy` and `ApprovalSource`.
7. **Wire the destructive-actions label into the policy engine**
   ([#10](backlog.md#10-hephaistoiodestructive-actions-allowed-is-read-by-no-code)). It is applied
   by manifests, documented as "a second independent confirmation", and asserted by the e2e
   harness — and read by no code. `TargetLabels` is passed empty, so no label check of any kind is
   live.
8. **Bind the write `RoleBinding` — into `hephaisto-chaos` only.**
9. **Enable `auto` for exactly one action type: `restart_pod`.**
10. **Mirror actions to Kubernetes Events** on the target object, so `kubectl describe pod` shows
    why something was restarted. That is where an on-call engineer actually looks, and the RBAC
    already grants `create` on events for it.
11. **Give incidents a path to `Resolved`**
    ([#11](backlog.md#11-there-is-no-production-path-to-resolved)). Today the only production
    terminal states are `Suppressed` and `Escalated`. An agent that fixes something and cannot close
    the incident is not finished — and MTTR has nothing to measure until this exists.
12. **Start `Spans.ActionExecute` and `Spans.Verification`** and record the three action metrics.
    All are declared and drawn already.

**Done when** a transiently-failing pod in `hephaisto-chaos` is auto-restarted, verification passes,
the incident reaches `Resolved`, and the audit trail reconstructs the whole decision **without
reading a log file** — and a seeded oscillating workload is quarantined after 3 attempts instead of
looping forever.

---

## v0.3.0 — It reaches people

Today **nothing leaves the process.** Escalation is a database state change, an `incident_events`
row, an audit row, and a nudge to any open browser tab. There is no outbound HTTP anywhere in
`src/` — no `AddHttpClient`, no `PostAsJsonAsync`, no notification package. The only out-of-band
path to a human is *your* Alertmanager firing on Hephaisto's self-check rules, and that rule file
ships disabled.

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

## Housekeeping — small, and now

Four things are cheap and currently wrong, and should not wait behind four milestones:

1. **The false verification claim in `Prompts/30-planning.md`** — a prompt telling the model a
   rollback safety net exists when it does not is a correctness bug, not a documentation task.
2. **`README.md`'s "Nothing is published yet — no image on a registry, no chart in an OCI repo."**
   `v0.0.1` is on GHCR.
3. **The GitHub description, topics and homepage.** Ten minutes; the repo is already public.
4. **The `grounding.rejected` cardinality bug**
   ([#12](backlog.md#12-unbounded-label-cardinality-on-hephaistogroundingrejected)) — one line, and
   it is currently writing GUIDs into a Prometheus label.

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
