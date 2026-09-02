# The safety model

The design target is **L3**: auto-execute a narrow allowlist of low-risk, reversible actions in
allowlisted namespaces, budget-capped, with automatic rollback. Everything else escalates to a
human.

Five things are meant to make that defensible. They are listed in order of how much you should
trust them — the first does not depend on any of the others being correct.

## 1. RBAC is the hard floor

Read access is cluster-wide. Write access is a `Role` bound into specific namespaces and nowhere
else. **No access to Secrets at all, ever.** The cordon/drain `ClusterRole` ships *unbound* —
binding it is an explicit, separate human act.

No amount of prompt injection changes what a ServiceAccount is allowed to do. This is the only
control on the list that holds even if every other one has a bug in it, which is why it is first.

It is also driven by configuration you can read: an empty `policy.actionableNamespaces` renders no
write `Role` at all. [Verify it](/operate/verify#_3-rbac-is-genuinely-bounded) rather than
trusting it.

## 2. The model never holds a mutating tool handle

The three phases are separated on purpose:

- **investigate** — read-only tools only
- **plan** — **no tools at all**; emits JSON against a fixed schema
- **execute** — pure C# over a closed `ActionType` enum

A prompt injection in a log line can, at its very best, produce a plan that the policy engine then
rejects. It cannot reach an API call directly.

This is why [the tool contract prompt](/internals/prompts) is described there as a hint rather
than a control. The architecture is the control.

## 3. The policy engine is pure and default-deny

It is a function over facts passed in by the caller — no I/O — which is exactly what makes it
exhaustively unit-testable. An empty namespace allowlist means *act nowhere*, and that is the
default.

`Policy:ProtectedNamespaces` is never actionable whatever the allowlist says, and includes
Hephaisto's own namespace and the observability stack. A self-inflicted outage would also blind
the agent to the fact that it had caused one.

## 4. Every auto action is verified

At T+60s / T+5m / T+15m, by deterministic C# predicates — **never by a model**. The state machine
refuses a model identity as the granter of a `Resolved`.

The three checks answer different questions rather than retrying one, so only the last may
conclude a failure: a pod still pulling its image at T+60s is not a fault, and reverting on it
would make the agent the cause of the next incident.

On a final failure the action is reverted where a revert exists, and escalated where one does not.
**Two honest limits there:**

- The rollback spec is written by the model, so it is read for typed values and never executed as
  written. The revert is built as an ordinary action over the same closed enum, and today only
  `ScaleWorkload` has an inverse that can be expressed.
- A pod delete has **no inverse at all**: the controller recreates the pod, which *is* the
  restart. So the recourse on a failed verification is escalation rather than undo. The policy
  engine exempts self-healing actions from needing a rollback spec instead of accepting a
  fictional one.

## 5. Budgets, cooldowns and oscillation detection

These cap the worst sustained case at roughly **ten pod restarts an hour** — indistinguishable
from a badly tuned HPA. Oscillation detection is wired to a workload quarantine, so a workload the
agent keeps acting on stops being actionable rather than being acted on faster.

## Two invariants that must never be weakened

**No audit, no action.** If Postgres is unreachable, the executor refuses to act. An unreadable
mode ConfigMap is read as `Observe`, never as `Auto`.

**The budget check, the cooldown check, the kill-switch check and the action INSERT are one
transaction.** This is why the agent is a single pod with `strategy: Recreate`. Split them across
replicas and it becomes a distributed TOCTOU race on the one code path where losing the race means
an unintended `kubectl delete`.

That constraint is the reason there is deliberately no `replicaCount` value in the chart.

## The kill switch

Three independent arms — an environment variable, a projected ConfigMap, and a database row — and
**the most restrictive one wins**.

| Mode | Behaviour |
|---|---|
| `Off` | Ingest nothing, investigate nothing |
| `Observe` | Never mutate |
| `DryRun` | Run the whole flow, every Kubernetes call carries `dryRun=All` |
| `Auto` | Execute allowlisted actions in allowlisted namespaces |

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Off
```

Most-restrictive-wins means a values file cannot quietly re-enable something an operator turned
off at the pod.

## Approval identity is attribution, not authentication

`ApprovedBy` is free text typed into the UI. On a single-operator cluster that is an acceptable
trade, and the schema is already OIDC-shaped so the upgrade populates the same string from a
verified claim.

**The risk to watch is habituation.** If this ever runs somewhere with more than one operator,
OIDC stops being a roadmap item and becomes a blocker.

## What is deliberately not claimed

The executor covers exactly the verbs the write `Role` grants: `RestartPod`, `RolloutRestart`,
`ScaleWorkload`, `DeleteStuckJob` and `DeleteFailedJobPods`. `RollbackDeployment` and
`PatchResources` are **refused before a call is made**, with `outcome=unsupported` and nothing
attempted.

The last step of the acting path — an incident the agent acted on reaching `Resolved` — was
unobserved on a cluster until 2026-09-02, and has now been seen **once**, on one fixture, in 41
seconds. That is stated here with its denominator rather than left to be discovered, because
"the path works and has been seen to work" and "the path is reliable" are different claims and
only the first is supported.
