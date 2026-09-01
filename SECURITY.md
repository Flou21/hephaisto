# Security policy

Hephaisto holds a ServiceAccount in your cluster and, when you have enabled it, deletes pods.
Please treat findings here as you would findings in anything else that can do that.

## Reporting a vulnerability

**Use GitHub's private vulnerability reporting:**
[open a draft advisory](https://github.com/Flou21/hephaisto/security/advisories/new). It is
private to the maintainers until an advisory is published, and it does not require an email
address from either side.

Please do not open a public issue for anything that could be used against a running install.

There is no formal SLA. This is a single-maintainer project and saying otherwise would be a
promise it cannot keep; expect a first response within a week, and say so in the report if the
issue is being actively exploited.

Supported: **the latest released version.** Fixes land on `main` and go out in the next tag.
Older tags do not receive backports.

## What is in scope

Anything that lets someone reach a cluster through the agent, or make it act against one:

- **Reaching the action path.** Getting an action executed that policy should have refused —
  bypassing the namespace allowlist, the namespace label, the autonomy list, the mode, a
  budget, a cooldown or the quarantine.
- **Prompt injection that changes what it does**, as opposed to what it says. The model emits
  an action *name* into a constrained schema and pure C# turns that into a typed call against a
  closed enum, so the intended ceiling is that a hostile pod's logs can at most name an action
  the policy engine then refuses. Anything that gets past that ceiling is a real finding.
- **Signal injection.** `POST /webhooks/alertmanager` is unauthenticated by design — Alertmanager
  cannot present a credential — so a NetworkPolicy is its *entire* authentication. Ways to
  reach it from outside that policy, or to make the policy not apply, are in scope.
- **Escaping the read-only investigation surface** — a "read" tool that can mutate, or a way to
  reach the Kubernetes client from the investigation phase.
- **Audit tampering.** The serving role holds INSERT but not UPDATE or DELETE on `audit_events`;
  ways to write history, or to make the executor act while the audit path is unavailable.
- **Credential disclosure** — a model API key, a Grafana token or a database credential reaching
  a log, a span, a prompt, the console, or a published artifact.

## Known and documented, so not a finding

These are deliberate, written down, and reachable from
[`docs/backlog.md`](docs/backlog.md). Reports that restate them are welcome as improvements to
the reasoning, but they are not vulnerabilities:

- **The Alertmanager webhook is unauthenticated.** See above. It is why the shipped NetworkPolicy
  is load-bearing rather than defence in depth, and why there is deliberately no Ingress for
  `/webhooks`.
- **`values-dev.yaml` disables that NetworkPolicy.** It is a development file and says so.
- **NetworkPolicy enforcement is unproven in the test harness**, because kind does not enforce
  it. [#23](docs/backlog.md).
- **Approval identity is attribution, not authentication.** `ApprovedBy` is free text and the
  console has no login; it is intended to run behind your own access control until OIDC lands.
- **The console has no authentication of its own.** Do not expose it.
- **Cordon and drain ship with an unbound ClusterRole**, so they cannot execute. That is on
  purpose.

## If you run it

The three things that matter most, in order:

1. **Do not expose the console or the webhook.** Neither authenticates.
2. **Keep `policy.actionableNamespaces` to namespaces you would accept a pod restart in**, and
   promote one action type at a time. Autonomy is per action type and never global.
3. **Give it its own database role.** The chart's two-role setup is what makes the audit log
   append-only from the agent's side; Postgres cannot restrain a table's owner.
