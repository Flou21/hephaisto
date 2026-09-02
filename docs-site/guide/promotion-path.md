# Observe → DryRun → Auto

The agent ships able to act **nowhere**. Getting from there to auto-remediation is four
independent changes, and they are independent on purpose: no single edit, and no single mistake,
can take you from a fresh install to an agent deleting pods.

This page is the order to make them in, and what to check after each.

## The four gates

| Gate | Where it lives | Default |
|---|---|---|
| 1. The namespace is named | `policy.actionableNamespaces` (Helm) | empty — act nowhere |
| 2. The namespace is labelled | `hephaisto.io/destructive-actions-allowed: "true"` on the namespace | absent |
| 3. The action type is promoted | `policy.autoEnabledActionTypes` (Helm) | empty — everything needs approval |
| 4. The mode is raised | `mode`, or `HEPHAISTO_MODE`, or the database arm | `Observe` |

Gates 1 and 2 are deliberately in different systems. Gate 1 is in your values file and reviewed
in git; gate 2 is on the cluster object itself and requires someone with access to that namespace.
Neither alone is sufficient, so a values file merged by mistake still cannot act.

Gate 1 also does more than it looks: **it drives RBAC**. An empty list renders no write `Role` at
all, so the ServiceAccount has no verb to abuse regardless of what the model proposes.

## Step 0 — Observe, for longer than feels necessary

`mode: Observe` runs the entire pipeline — ingest, dedup, correlation, investigation, diagnosis,
plan generation — and never mutates anything. This is where you find out whether the diagnoses are
any good **on your cluster**, which is the only question that matters and the one nobody else can
answer for you.

What to look for before moving on:

- Are incidents being created at all? If not, the problem is almost always
  [`prometheusOperator.selectorLabels`](/guide/install#the-most-dangerous-setting) or a missing
  [`hephaisto_kind`](/operate/alerting).
- Are the diagnoses right? Use the thumbs-up/down on each incident — feedback is stored and is the
  only signal you will have later.
- Are the plans it proposes ones you would have approved? Read them in `Observe`, where they cost
  nothing.

Give this weeks, not hours. The failure mode of rushing is not a dramatic outage; it is
habituation — approving things without reading them, which defeats the entire approval design.

## Step 1 — DryRun

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=DryRun
```

`DryRun` runs the whole flow and every Kubernetes call carries `dryRun=All`. The API server
validates and admits the request, then discards it. This is the step that catches RBAC gaps and
admission-webhook rejections **without** catching them during an incident.

Note what `DryRun` cannot tell you: a dry-run restart does not restart anything, so nothing
converges and no verification can pass. Assertions about an incident reaching `Resolved` are not
meaningful here.

## Step 2 — Name and label one namespace

Pick the least important namespace you have.

```yaml
# values.yaml
policy:
  actionableNamespaces:
    - my-scratch-namespace
```

```sh
kubectl label namespace my-scratch-namespace \
  hephaisto.io/destructive-actions-allowed=true
```

The chart **refuses to render** if you name `kube-system`, `kube-public`, `default`, the release
namespace or the observability namespace — including when a bad namespace is hidden behind a good
one further up the list. That is a test, not a convention.

`policy.protectedNamespaces` is never actionable whatever the allowlist says, and includes
Hephaisto's own namespace and the observability stack. A self-inflicted outage would also blind
the agent to the fact that it had caused one.

## Step 3 — Promote exactly one action type

```yaml
policy:
  autoEnabledActionTypes:
    - RestartPod
```

Everything not on this list still runs the full pipeline and then **waits for a human**. This is
the gate to move slowest on. `RestartPod` is the usual first choice because it is self-healing —
the controller recreates the pod, which *is* the restart.

That property matters for a reason that is easy to miss: **a pod delete has no inverse.** The
recourse on a failed verification is escalation, not undo. The policy engine exempts self-healing
actions from needing a rollback spec instead of accepting a fictional one.

## Step 4 — Auto

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Auto
```

What now happens automatically is narrow by construction: the named action types, in the named and
labelled namespaces, within the policy engine's rate caps, verified at T+60s / T+5m / T+15m by
deterministic C# predicates — never by a model. The state machine refuses a model identity as the
granter of a `Resolved`.

Budgets, cooldowns and oscillation detection cap the worst sustained case at roughly **ten pod
restarts an hour** — indistinguishable from a badly tuned HPA.

## Going back

The kill switch has three arms — an environment variable, a projected ConfigMap, and a database
row — and **the most restrictive one wins**.

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Off
```

`Off` ingests nothing and investigates nothing. An unreadable mode ConfigMap is read as `Observe`,
never as `Auto`.

## One thing to know about approval identity

`ApprovedBy` is free text typed into the UI. On a single-operator cluster that is an acceptable
trade, and the schema is already OIDC-shaped so the upgrade populates the same string from a
verified claim.

**If this ever runs somewhere with more than one operator, OIDC stops being a roadmap item and
becomes a blocker.** Attribution that anyone can type is not authentication.
