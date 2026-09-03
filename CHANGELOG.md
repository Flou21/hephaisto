# Changelog

What changed in each release, for someone deciding whether to upgrade.

Two companions carry the rest, and this file deliberately does not duplicate them:
[`docs/history.md`](docs/history.md) is the engineering record — what was learned doing the work,
including the wrong turns — and [`docs/backlog.md`](docs/backlog.md) is everything known to be
broken, with the evidence for each.

Versions are set by the git tag through MinVer; the chart version and the app version are always
the same number.

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
