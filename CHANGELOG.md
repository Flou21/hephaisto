# Changelog

What changed in each release, for someone deciding whether to upgrade.

Two companions carry the rest, and this file deliberately does not duplicate them:
[`docs/history.md`](docs/history.md) is the engineering record — what was learned doing the work,
including the wrong turns — and [`docs/backlog.md`](docs/backlog.md) is everything known to be
broken, with the evidence for each.

Versions are set by the git tag through MinVer; the chart version and the app version are always
the same number.

## v0.7.0 — unreleased

**It survives a bad deploy.** The agent can now roll a Deployment back, there is finally a
fixture whose fault has a *cause* rather than merely a presence, and the incident card says when
a rollout preceded the incident.

Two defects in this list reached every install of the chart and were found while planning the
release rather than by anyone running it.

### Added
- **`rollback_deployment` can actually be carried out.** Everything around this action was built
  two releases ago and unit-tested — the policy gate with its two tuned windows, the revision
  facts, the `get_rollout_history` tool, the RBAC grant, the runbook guidance, the model-facing
  description — and the only thing between the model and a rollback it had correctly reasoned its
  way to was `ActionCapability.IsImplemented` returning false, which rendered the action to the
  planner as *"Not available in this build."*

  Three things worth knowing if you enable it. There is **no rollback subresource** — Kubernetes
  removed that API in 1.16, so a rollback is a client-side patch of the previous ReplicaSet's pod
  template, which is why the existing `patch` grant was already sufficient and **no RBAC
  changed**. The `pod-template-hash` label is stripped, because the controller owns it. And a
  rollback **does not restore the old revision number** — rolling back from revision 3 to 2
  produces revision *4* — so verification asserts on the **ReplicaSet**, which the controller
  re-scales rather than recreating. The obvious predicate would have failed on every successful
  rollback and rolled the cluster forward onto the revision that caused the incident.

  A rollback has **no inverse, deliberately**. `ActionRollback` is only ever called because a
  verification failed, which for a rollback means the previous revision is not healthy either —
  so the one situation where rolling forward is reachable is exactly the situation where it is
  the worst available move. It escalates instead.
- **`c14-bad-deploy`, the first fixture whose setup has a timeline.** All thirteen fixtures before
  it inject a fault that is simply *there*; not one performs a rollout. So the corpus could not
  ask the question an on-call engineer asks first — **what changed?** c14 deploys healthy, dwells,
  and is then broken by a rollout. It is also the first fixture where a **restart is the wrong
  answer**: its answer key accepts `RollbackDeployment` and nothing else, because restarting its
  pods replaces them with more pods running the same bad revision.
- **Change correlation in the incident card.** An incident on a workload whose current revision is
  minutes old now says so, with the revision, the images and the gap, *before* the investigation
  starts — because #74 established that step budget is the binding constraint on accuracy, and a
  fact given for free is a step not spent. It is phrased as evidence with its own caveat rather
  than as a conclusion, and a rollout that happened *after* the incident opened is never offered,
  since that is often somebody deploying the fix.
- **The console shows whether an action worked.** `AgentAction.Verifications` has carried the
  T+60s / T+5m / T+15m outcomes since v0.2.0, persisted and read by nothing — while *"everything
  it does is verified, then reverted if it did not work"* is the safety claim on the landing page.
  The rows now render, `hephaisto-eval export` carries them into transcripts, and the design
  gallery photographs them.
- **Five runbooks**, for kinds that shipped an alert rule and fell through to the default one:
  `HighLatency`, `TargetDown`, `ReplicaMismatch`, `RestartStorm` and the new `PodNotReady`. The
  default runbook is Kubernetes-shaped — *who owns this, get_events, previous-container logs* —
  which is not merely unhelpful for a burn-rate alert computed from span metrics, it points the
  investigation at the wrong evidence.
- **`SignalKind.PodNotReady`**, so `ReadinessFlapping` means flapping again. See Changed.
- **Three policy settings became chart values**: `policy.rollbackFreshRevisionWindow`,
  `policy.rollbackPreviousHealthyMinimum` and `policy.clusterUnhealthyCeiling`. They were code
  defaults with no way to reach them, which was defensible only while `rollback_deployment` was
  unimplemented.

### Changed
- **An incident's kind is no longer decided permanently by whichever signal opened it.**
  `IncidentTriage.Attach` folded every later signal in while updating only `LastSignalAt` and
  `Severity`. It now also re-labels upward when a later signal identifies the failure more
  specifically — a `PodNotReady` incident becomes `ImagePullBackOff` when the signal that knows
  the mechanism arrives. Since `SignalKind` selects the runbook, a stale kind did not merely
  mislabel the incident, it handed the model instructions written for a different failure.

  Re-labelling stops once the incident leaves `Detected`/`Triaging`: after a runbook has been read
  into a prompt, changing the label underneath it conceals that the investigation ran against the
  wrong instructions rather than correcting it. The change is audited.
- **BREAKING for anyone matching on it — `KubePodNotReady` now declares
  `hephaisto_kind: PodNotReady`, not `ReadinessFlapping`.** Two rules shared one kind and only one
  of them meant it: flapping is *intermittent*, and that rule fires on a pod that is persistently
  **stuck**. A stuck pod was being handed a runbook whose entire argument is that the fault is
  intermittent and a restart will not help. Nothing needs doing unless you filter on that label.
- **`Llm:Budget:MaxTokensPerHour` defaults to 50,000,000, up from 2,000,000.** See Fixed.
- **`--full` runs twelve fixtures**, adding c14. The acting gate is still a separate run per #97,
  and is now two runs testing two different action types.

### Fixed
- **A shipped alert rule named a chaos fixture and fired forever on every other cluster.**
  `ServiceNoTraffic` asserted `absent(traces_spanmetrics_calls_total{service="faulty-service"})`,
  and `alerts.slo` defaults to **true** — so on any install that is not this repo's dev cluster
  running c10, the series is permanently absent, the alert fires after five minutes and never
  stops. It carries `hephaisto_kind: TargetDown`, so the agent did not merely log it: it opened an
  incident, spent its budget investigating a workload that does not exist, escalated, and
  repeated. The rule moved to its own file behind `alerts.noTraffic`, defaulting to **false**.
  **This one directly undercut v0.6.0's whole theme, and nobody could have hit it here.**
- **Every latency incident was un-actionable by construction.** The three latency rules aggregated
  `sum by (service)` while the error-rate rules twenty lines above used
  `sum by (service, k8s_namespace_name)`. An empty namespace fails the policy engine's allow-list
  gate, is part of the signal fingerprint, and is what notification routes filter on. #33 fixed
  the *reader* half of this in v0.3.0 and everyone recorded "namespace: solved"; the rules were
  never fixed to emit what the reader reads.
- **The hourly token cap was the real budget, and the cost cap was decoration.** The two caps imply
  a price — $3.00 over 2M tokens is $1.50/1M — which is *exactly* `gemini-3.7-flash`'s blended
  rate. On `gpt-oss-120b` at $0.065/1M, 2M tokens is thirteen cents, so the first full `--mode
  Auto` run refused **14 of 27** investigations outright, escalating them `BudgetExhausted`,
  having spent **$0.066 against a $3.00 cap**. It cost a milestone rather than a run: the MVP bar
  reported *"not applicable: 8 scenarios scored, the bar needs 10"* with accuracy at 7 of 8. The
  harness had worked around it, which made the harness the only configuration where the budgets
  were right — so a stranger installing the chart got the broken calibration. That workaround is
  now deleted rather than adjusted.
- **A crashed investigation was reported as a budget ceiling, with no exception.** `Faulted` is not
  a ceiling — a ceiling is a control working, a fault is a bug — and pooling them let a real
  exception hide behind a tolerance written for budget exhaustion. The exception was in the
  database, in the API response, and in the harness's own snapshot the whole time; it was simply
  never printed. The run report now names the fault, attributes it to a fixture, and prints the
  message, and *"only 10 of 11 fixture incidents were investigated"* now says which and why.
- **A single readiness-probe failure was classified as "flapping", so every ordinary rollout
  opened a spurious incident — and captured every real one that followed it.** `SignalMapper` has
  two detectors for `ReadinessFlapping`: one counts ready-transitions and refuses to claim it
  below four, and the other claimed it from **one** `Unhealthy` event. A probe fails once on any
  pod slower to start than its `initialDelaySeconds`, so the spurious incident opened *first* —
  seconds in, against minutes for anything metric-derived — and every later signal correlated into
  an incident already carrying the wrong kind and therefore the wrong runbook. The event path now
  requires the same repetition count as the detector beside it.
- **Verification predicates were workload-shaped**, which is right for a restart — the pod is gone
  by definition — and wrong for a rollback, whose previous revision's pods were Ready throughout.
  A predicate that passes on a no-op is worse than none, because it closes the incident.
- **`c13` was absent from `infra/chaos/README.md`** — no row, no listing, and the header still said
  "Twelve" — while `CLAUDE.md` calls that table the agent's regression suite. The "Expected alert
  name" column is now labelled as the specification it is: **not one** of those `Chaos*` names is
  implemented by any `PrometheusRule`, and reading it as an inventory is what produced #70's wrong
  cause and left it standing for four releases.
- **`c5` could never score an action.** It is the obvious `DeleteStuckJob` / `DeleteFailedJobPods`
  fixture and had no `AcceptableActions` at all.
- **Four documentation surfaces described a harness and two limitations that no longer exist**:
  `docs/verification.md`'s fixture set and `ACT_FIXTURE` default, a limitation on
  `docs-site/reference/agent-options.md` closed by #82, `docs-site/project/index.md` calling a
  closed backlog entry open, and `TryGrantConcludingStep`'s own comment claiming it grants one
  step when it has granted two since #78.

## v0.6.0 — 2026-09-03

**Someone else can run it.** Three public sites, a demo that runs on a laptop with one command
and no API key, and — for the first time in the project's life — an incident the agent acted on
reaching `Resolved`.

### Added
- **The agent has been observed closing an incident it acted on.** On `c13-wedged-lock`:
  `Detected → Triaging → Investigating → Acting → Verifying → Resolved`, 41 seconds after the
  restart, granted by `hephaisto/verifier`, 70 assertions. **This retires v0.5.0's "Not
  established" line below.** It is one run on one fixture; the acting path is now demonstrated
  end to end rather than reliable, and the docs say which. Reproduced on the release gate:
  `--fixtures c13 --mode Auto` passed 70 assertions in 24m37s, executing a `RestartPod` and
  closing the incident.
- **The release gate is two runs, not one.** `--full` applies its fixtures simultaneously, which
  on a single node crosses `policy.clusterUnhealthyCeiling` — so the policy engine correctly
  refuses every action as a cluster-wide event and the acting path cannot be tested in the same
  run that tests diagnosis. Diagnosis: `--full`, which scored **8/8 correct** over 85 assertions.
  Acting: `--fixtures c13 --mode Auto`. The harness now says so instead of reporting a working
  safety gate as a broken executor.
- **Three sites**, on Cloudflare Pages: [hephaisto.dev](https://hephaisto.dev),
  [docs.hephaisto.dev](https://docs.hephaisto.dev) and
  [demo.hephaisto.dev](https://demo.hephaisto.dev). The docs site *transcludes* the repository —
  the runbooks, the prompt fragments, `values.yaml`, `architecture.md` and this file are included
  from their real locations rather than copied, and `ignoreDeadLinks` is false so a moved file
  breaks the build instead of rendering a blank section.
- **`c13-wedged-lock`**, a thirteenth chaos fixture, because the acting path had no fixture that
  could measure it. `c11` and `c12` both put the wedged state on a PVC, so acting means overriding
  the correct rule that PVC contents survive a pod replacement — and a decline is then ambiguous
  between "will not act" and "did not make the inference". c13 puts the same fault on an
  `emptyDir`, which the planning prompt already names as pod-scoped. It measures willingness to
  act; c12 keeps measuring the inference. **Quote the two numbers separately.**
- **`hephaisto-eval export`**, a fifth CLI verb. Snapshots a *finished* incident out of the
  database into a transcript — the transitions it made, the action it executed or was refused,
  and the policy decision behind it. `run` can never produce those: replay constructs an
  investigation runner and no executor, no policy engine and no state machine.
- **Termination reporting in the eval harness.** A run cut off by a token or step budget used to
  render as a bare `no finding` and read as a wrong answer. The per-scenario line now names the
  termination reason, and the summary prints the histogram — with the number that matters beside
  it: how many attempts the planner actually ran in.
- **A demo that needs no cluster.** `demo/compose.yaml` brings up Postgres and the published
  image with twelve recorded investigations loaded — the step trace, the diagnosis, and every
  evidence excerpt linked back to the raw tool output it came from. No API key, no Kubernetes,
  nothing fetched at runtime. Ten are replays; two are live captures of the agent acting and of
  policy refusing it, which a replay cannot produce.
- **`Kubernetes:Enabled`.** The agent can now start without a cluster, which it could not before:
  the RBAC self-check ran forty-odd access reviews at boot and building a client outside a pod
  fell back to a kubeconfig that was not there. Disabling it skips the watchers and leaves the
  executor that refuses everything.
- **`Llm:EmbeddingProvider`.** Embeddings are configured separately from chat and can now point
  at any endpoint serving `/v1/embeddings`, so a self-hosted install keeps the semantic arm of
  search without a Google account. The default is unchanged.
- **`hephaisto-eval run --transcripts`**, and a `redact` verb. Recording what a replay computes
  and then discards is what makes the demo possible.
- **Screenshots of the shipping console**, in `design/shots/`, photographed by
  `scripts/console-shots.sh` from the published image running `demo/compose.yaml` — so they are
  the product rather than a rendering of the design system, and they are regenerated rather than
  retouched. Taking them found a bug: the console told every escalated incident that no diagnosis
  had been produced, over the top of the diagnosis.
- **A vulnerability reporting path** — [`SECURITY.md`](SECURITY.md), through GitHub private
  advisories.
- A README for the chart, so Artifact Hub stops rendering an empty page.

### Changed
- **BREAKING — the Kubernetes label prefix is `hephaisto.dev/`, not `hephaisto.io/`.** A label
  prefix is meant to be a DNS domain you control, and `hephaisto.io` never was one. Three labels
  moved, all read by the policy engine:

  | before | after |
  |---|---|
  | `hephaisto.io/destructive-actions-allowed` | `hephaisto.dev/destructive-actions-allowed` |
  | `hephaisto.io/allow-single-replica-restart` | `hephaisto.dev/allow-single-replica-restart` |
  | `hephaisto.io/protected` | `hephaisto.dev/protected` |

  There is **no compatibility shim and no dual-prefix read**, deliberately: a policy engine that
  accepts two spellings of "this namespace opted in" is a worse thing to reason about than one
  that accepts one.

  **Upgrading.** This fails *closed*. An upgraded agent looks for a label that is not there, the
  policy engine denies, and the reason is recorded on the action row — nothing is silently
  permitted, and the failure mode of skipping this note is an agent that stops acting, not one
  that acts where it should not. Relabel every namespace you had opted in:

  ```sh
  kubectl label ns <ns> hephaisto.dev/destructive-actions-allowed=true
  kubectl label ns <ns> hephaisto.io/destructive-actions-allowed-
  ```

  Same for any workload carrying `hephaisto.io/protected` or
  `hephaisto.io/allow-single-replica-restart`. These are **code defaults, not chart values**, so
  if you overrode `Policy:RequiredNamespaceLabel`, `Policy:ProtectedLabels` or
  `Policy:AllowSingleReplicaRestartLabel` in config or env, update those too — the defaults moved
  and your overrides did not.
- **`ACT_FIXTURE` now defaults to `c13`**, with `c11` and `c12` still selectable. `--full` runs
  eleven fixtures rather than ten.
- **`PlanVerdict` gained `PlannerNeverRan`.** `NoPlan` pooled four outcomes, and any action rate
  over the total counted a run that never reached the planner as a decline.
- Hosting moved from GitHub Pages to Cloudflare Pages: one Pages site binds one custom domain,
  and this needs three.
- The README leads with what it is, what it looks like and how to try it, before the safety
  argument.

### Fixed
- **An incident that was successfully acted on sat in `Verifying` forever.** The resolve
  transition wrote its audit detail as a bare string into a `jsonb` column, so Postgres rejected
  it (`22P02`) and rolled back the transition that had just succeeded. Four more layers sat on
  top of it: an action with no owner reference could not be verified, a harness wait that ended
  before verification did, an `instance` label read as a node name (so `ClusterFacts` came back
  empty and default-deny refused every action), and finally an assertion that read `.target.name`
  from a list endpoint that does not return it — an assertion that could never pass, and that had
  agreed with four genuine bugs in a row.
- **The `conclude` tool asked for a wrapper `gpt-oss` never sends**, and three budget ceilings hid
  it. `TokenBudgetExhausted` went from 6 of 24 attempts to 0 of 24.
- **Confidence was offered in two places and read from one**, so a well-formed conclusion could be
  scored as though it had none.
- **An auto-executed action left `ApprovedBy` null**, against an invariant three doc comments
  assert. Nothing had ever executed an action on a cluster, so the assertion had no subject.
- **The demo site shipped another state's glyph for three states.** `Escalated` rendered with
  `AwaitingApproval`'s marker, `Investigating` with `Detected`'s, `Detected` with `Expired`'s — on
  every page, since the site existed. The vocabulary is now parsed from `Display.cs`, which
  `docs/design.md` names as its owner, instead of copied.
- **A seeded replay was reported as `PolicyDenied`** by a policy engine the demo stack never
  constructs.
- **The console told every escalated incident that "no diagnosis was produced"**, on pages whose
  primary finding sat directly below the banner. An incident escalates for eleven reasons and only
  some of them mean nothing was produced — one refused by the policy engine is escalated *and*
  fully diagnosed.
- **The redactor's `\b` missed an address that reached a rendered page**, because `\n` before a
  digit is not a word boundary — and then its replacement lost to `\u0022`, which the serializer
  writes for a nested quote and which ends in a digit. The same defect twice, from the same cause:
  this runs over an escaped document, and an escape ends in whatever the encoder chose.
- **`--nightly` published the branch it claimed to be testing**, not the one asked for.
- The README announced `Status: v0.2.0` on a v0.5.0 repository and its install command pinned
  `--version 0.2.0`, so anyone following it installed a three-release-old chart.

## v0.5.0 — 2026-09-01

**Paying the debt down.** A release whose feature is that the list got shorter, scheduled rather
than hoped for.

- **The end-to-end gate went green on the full corpus for the first time** — ten fixtures, 77
  assertions, 98 minutes, $0.115 against a local `gpt-oss-120b`.
- **The MVP bar became evaluable, and was met:** `8/10 correct root cause` against a bar of ≥ 7/10
  over ≥ 10 scenarios, quoted since v0.1.0 and never before gradeable. A truncated investigation
  produces no finding, and no finding cannot be graded — the accuracy was never short, the
  denominator was.
- **Cheaper providers**, pulled forward mid-milestone: `Llm:Provider=openai` reaches DeepSeek,
  OpenRouter and a local Ollama or LM Studio through one factory, at $0.031 per investigation
  against $0.080.
- Three product bugs on the acting path: an auto-executed action left `ApprovedBy` null against
  its own documented invariant; a workload cooldown refused an action as its own precedent, which
  had left four safety gates dormant on the entire auto path; and a `RestartPod` could never be
  verified because its target carried no owner.
- Eighteen fixes in total, most of them in the instrument rather than the product.

**Not established:** the agent has still not been observed closing an incident it acted on.

## v0.4.0 — 2026-08-30

**A design language.** One canonical token set that the console and the landing page both consume
from the same file, canonical by test rather than by convention, and a visual regression net that
photographs every component in both themes on every pull request. Light mode stopped being a
courtesy.

It also found that the console had **never been interactive in any released image** —
`blazor.web.js` returned 404 in every published build, so every button was dead across four
releases, and nothing had been able to see it.

## v0.3.0 — 2026-08-30

**It reaches people.** An escalation is written to a Postgres outbox in the same transaction as
the state change that caused it, and delivered to a generic HTTP endpoint or a Teams card with
retry, rate limiting and a link back to the incident. Ships delivering nowhere.

Measured against a real cluster, including the assertion the design exists for: receiver taken
down, agent restarted mid-flight, receiver brought back, delivery arriving anyway.

## v0.2.0 — 2026-08-30

**It acts, carefully.** Executes a narrow allowlist of reversible actions, verifies them at
T+60s / T+5m / T+15m against deterministic predicates, reverts or escalates when they do not hold,
and closes the incident when they do. Ships configured to act nowhere.

## v0.1.0 — 2026-08-29

**Diagnosis you can trust.** The eval harness and a cassette corpus, so a prompt change is
measurable without a cluster. Met its gate at 22/24 correct root cause over replay.

## v0.0.1 — 2026-08-29

Multi-arch image and Helm chart published to GHCR with build provenance attested.
