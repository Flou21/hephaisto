# Changelog

What changed in each release, for someone deciding whether to upgrade.

Two companions carry the rest, and this file deliberately does not duplicate them:
[`docs/history.md`](docs/history.md) is the engineering record — what was learned doing the work,
including the wrong turns — and [`docs/backlog.md`](docs/backlog.md) is everything known to be
broken, with the evidence for each.

Versions are set by the git tag through MinVer; the chart version and the app version are always
the same number.

## Unreleased

### Added
- **A demo that needs no cluster.** `demo/compose.yaml` brings up Postgres and the published
  image with ten recorded investigations loaded — the step trace, the diagnosis, and every
  evidence excerpt linked back to the raw tool output it came from. No API key, no Kubernetes,
  nothing fetched at runtime.
- **`Kubernetes:Enabled`.** The agent can now start without a cluster, which it could not before:
  the RBAC self-check ran forty-odd access reviews at boot and building a client outside a pod
  fell back to a kubeconfig that was not there. Disabling it skips the watchers and leaves the
  executor that refuses everything.
- **`Llm:EmbeddingProvider`.** Embeddings are configured separately from chat and can now point
  at any endpoint serving `/v1/embeddings`, so a self-hosted install keeps the semantic arm of
  search without a Google account. The default is unchanged.
- **`hephaisto-eval run --transcripts`**, and a `redact` verb. Recording what a replay computes
  and then discards is what makes the demo possible.
- **A vulnerability reporting path** — [`SECURITY.md`](SECURITY.md), through GitHub private
  advisories.
- A README for the chart, so Artifact Hub stops rendering an empty page.

### Fixed
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
