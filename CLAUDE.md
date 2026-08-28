# Watchtower — an autonomous SRE agent living in the cluster

Watchtower watches a Kubernetes cluster, receives Alertmanager webhooks, investigates what
is wrong using PromQL, LogQL and traces, and — eventually — fixes a narrow allowlist of
problems by itself. It is also a first-class *producer* of telemetry: an investigation is a
trace you can open in Grafana and then ask the agent about.

**This repo is standalone.** It shares no code, no namespace, no database and no LLM gateway
with the Cait project in `~/dev`. That separation is deliberate and worth preserving.

## Unlike `~/dev`, everything here is in git

`~/dev` is a workspace of ten repos with untracked `Dockerfile.dev` files and an
observability stack that ran for 17 days without appearing in any manifest. Watchtower is
one project, so `~/watchtower` **is** the git repo and every Dockerfile, manifest, values
file and Tiltfile is tracked. A fresh clone builds. Keep it that way.

Consequences worth knowing:

- `git worktree` works fine here, unlike in `~/dev`.
- There is no `nuget.config`, no private feed and **no PAT build argument anywhere**.
  Watchtower references no internal package. If you find yourself adding a credential to a
  Dockerfile, something has gone wrong.

## Layout

```
src/Watchtower.Core/            domain, state machine, policy, digester — ZERO I/O
src/Watchtower.ServiceDefaults/ OTel wiring, health, resilience — deployed as a dll
src/Watchtower.Agent/           THE pod: tools, hosted services, Blazor UI, persistence
src/Watchtower.AppHost/         Aspire — dev-time orchestration only, NEVER deployed
src/Watchtower.Simulator/       dev-only fault generator
infra/                          namespaces, observability stack, chaos fixtures, RBAC
```

### `Watchtower.Core` has zero I/O dependencies, on purpose

Every safety-critical decision — the policy engine, the state machine, budgets, oscillation
detection, log digestion — is a pure function over facts passed in by the caller. Nothing in
`Core` opens a socket, reads a file or touches a database.

That is what makes the safety surface testable: the entire policy engine is covered by fast
in-memory unit tests with no cluster, no Postgres and no LLM. **If you are tempted to add a
package to `Core` that talks to something, the design has drifted** — the fact belongs in
`ClusterFacts`, gathered by the caller, and passed in.

### Aspire is dev-time only

`Watchtower.AppHost` gives you `dotnet run` with Postgres, the agent and the simulator on a
laptop. It is excluded from the container image and no manifest references it.
`Watchtower.ServiceDefaults` is a plain class library with an ASP.NET framework reference and
**no `Aspire.Hosting.*` package** — referencing one there would drag the orchestrator into
the pod.

Manifests are **hand-written**. Do not use `aspire publish` or aspir8:
`Aspire.Hosting.Kubernetes` is preview and emits Helm charts, which would be a second source
of truth drifting from what Tilt applies. More importantly this pod's ServiceAccount,
ClusterRole and RoleBindings *are* the security boundary — they must be reviewable
human-written diffs with comments explaining why each verb is granted.
`infra/app/rbac.yaml` is the most valuable prose in the repo.

### Telemetry is never gated on a dev flag

Cait's `OpenTelmetryConfiguration.cs` hard-returns when `DEV_LOGGING=true` — the mode every
`dev-infra/apps/*.yaml` sets — so it is blind in development. For a project whose product
*is* telemetry that is a defect, not a tradeoff. There is deliberately no global off switch
in `ServiceDefaults`. Every Aspire convenience degrades instead: no OTLP endpoint means
console plus `/metrics`, never a crash and never silence.

## Tilt is the inner loop, on port 10351

**Run Tilt from `~/watchtower`.** It coexists with the `~/dev` instance already running
detached on 10350; the two share a cluster but no ports.

```sh
cd ~/watchtower && tilt up --port 10351
tilt logs -f watchtower
```

Everything binds to the Tailscale interface, so **use the hostname, not `localhost`** — not
even from a shell on this machine:

    http://macstudio-von-florian.tail3043f4.ts.net:<port>

| What | Port |
|---|---|
| Watchtower UI | 8100 |
| Prometheus | 9090 |
| Grafana | 3030 |
| Alertmanager | 9093 |
| Loki | 3100 |
| Tempo | 3200 |
| OTel Collector (OTLP grpc / http) | 4317 / 4318 |
| grafana-mcp | 8200 |
| Aspire Dashboard | 18888 |
| Postgres | 5433 |
| Tilt UI | 10351 |

### Never run a bare `tilt down`

It runs `helm uninstall` on the observability stack and takes the Grafana PVC with it. Every
dashboard and datasource here is declarative precisely so that losing the PVC costs nothing —
**do not create dashboards by hand in the Grafana UI**, they will not survive and they will
not be in git.

### Chaos fixtures never start on their own

`infra/chaos/` breaks the cluster in ten documented ways. Each scenario is
`auto_init=False, trigger_mode=TRIGGER_MODE_MANUAL`, so `tilt up` never brings up a
deliberately broken cluster by accident. `c9-memhog` causes node-level memory pressure —
**run it alone and deliberately.**

`infra/chaos/README.md` maps each scenario to the alert, PromQL, LogQL and Kubernetes event
it should produce. That table is the agent's regression suite; keep it accurate.

### Pin every chart version

A `helm_resource` without an explicit `--version` resolves "latest", which will silently
major-upgrade Prometheus on some future `tilt up`.

## Alert rules must declare a `watchtower_kind` that is a real `SignalKind`

Every `PrometheusRule` carries a `watchtower_kind` label, and its value has to be a member
name of `Watchtower.Core.Domain.SignalKind`. That label is how an alert selects the runbook
the model is given.

`Enum.TryParse` fails **silently** on anything else, and the classifier then falls back to
guessing from the alertname — which for a name like `KubeContainerWaiting` yields `Unknown`
and the default runbook instead of the image-pull one. Nothing about that is visible from
either side: the YAML looks well-labelled and the classifier looks correct.

`ShippedAlertRulesTests` reads the real files in `infra/observability/alerts/` and fails if
any alert declares a kind that does not parse, or classifies as `Unknown`. If you add a rule
whose failure mode has no matching `SignalKind`, add the member **and its runbook** rather
than inventing a label value.

## The cluster is a single shared resource

There is one k3s node, shared with the whole Cait stack from `~/dev`. Parallel agents can
draft code and run unit tests freely, but **cluster verification is serial**. Two agents
applying chaos fixtures at once produce garbage for both.

## Safety model, in short

The agent runs at **L3**: it may auto-execute a narrow allowlist of low-risk actions in
allowlisted namespaces, budget-capped, with automatic rollback. Everything else escalates.

What makes that defensible:

1. **RBAC is the hard floor.** Read access is cluster-wide; write access is a `Role` bound
   into `watchtower-chaos` and nowhere else. No access to Secrets at all, ever. The
   cordon/drain ClusterRole exists in the file but is deliberately **not bound** — binding it
   is the explicit human act that enables that capability.
2. **The LLM never holds a mutating tool handle.** Investigation has read-only tools;
   planning has *no* tools and emits JSON against a schema; execution is pure C# over a
   closed `ActionType` enum. A prompt injection in a log line can at most produce a plan the
   policy engine then rejects.
3. **The policy engine is pure and default-deny**, so it is exhaustively unit-tested.
4. **Every auto action is reversible and is actually reverted** on failed verification at
   T+60s / T+5m / T+15m.
5. **Budgets, cooldowns and oscillation detection** cap the worst sustained case at roughly
   ten pod restarts an hour — indistinguishable from a badly tuned HPA.

Two invariants that must never be weakened:

- **No audit, no action.** If Postgres is unreachable the executor refuses to act. An
  unreadable ConfigMap is read as `observe`, never as `auto`.
- **The budget check, cooldown check, kill-switch check and the action INSERT are one
  transaction.** This is why the agent is a single pod with `strategy: Recreate`. Split them
  across replicas and it becomes a distributed TOCTOU race on the one code path where a race
  means an unintended `kubectl delete`.

### Approval identity is attribution, not authentication

`ApprovedBy` is free text typed into the UI. On a single-operator tailnet cluster that is an
acceptable trade, and the schema is already OIDC-shaped so the upgrade populates the same
string from a verified claim.

**The risk to watch is habituation.** If this ever runs anywhere with more than one operator,
or against anything that matters, OIDC stops being a roadmap item and becomes a blocker.
This paragraph exists so that is a decision someone makes rather than something everyone
forgets.

## Verifying a change

Prefer the running cluster over reasoning about it.

```sh
cd ~/watchtower && dotnet build && dotnet test
tilt trigger watchtower
kubectl -n watchtower logs deploy/watchtower --tail=50
curl -s http://macstudio-von-florian.tail3043f4.ts.net:8100/healthz

# RBAC is actually bounded: first three must be "no", the last "yes"
kubectl auth can-i delete secrets             --as=system:serviceaccount:watchtower:watchtower -A
kubectl auth can-i delete pods -n kube-system --as=system:serviceaccount:watchtower:watchtower
kubectl auth can-i create clusterrolebindings --as=system:serviceaccount:watchtower:watchtower
kubectl auth can-i delete pods -n watchtower-chaos --as=system:serviceaccount:watchtower:watchtower
```

The **five-hop correlation test** is the acceptance test for the whole observability stack —
exemplar → trace → logs → metrics → service graph. It is written out in
`docs/verification.md`; if all five hops work, the traces, exemplar, OTLP metrics, OTLP logs
and both Grafana correlation configs are simultaneously proven.

## Testing

xUnit v3 on Microsoft.Testing.Platform, with **AwesomeAssertions** — the Apache-2.0 community
fork of FluentAssertions, API- and namespace-compatible. FluentAssertions 8.x moved to a paid
Xceed licence for commercial use, so bumping to it is a procurement decision rather than a
dependency bump.

### Run tests with `./scripts/test.sh`, not `dotnet test`

`dotnet test` **cannot run this suite** on the current toolchain. Without a `global.json`
`test.runner` entry it hard-errors; with one it starts the test executable in
`--server dotnettestcli` mode and exits in ~200 ms reporting *"Zero tests ran"*.

This was checked against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter, and
with and without central transitive pinning — identical every time, so it is xunit.v3's
server-mode support rather than anything in this repo. A xunit.v3 test project is an
executable, and running it directly works: all 228 tests discover and run, and the exit code
is honest (0 on pass, non-zero otherwise), so CI is safe.

### Local database

`./scripts/dev-db.sh up` starts a throwaway Postgres 17 + pgvector on port **5433** and
applies the migrations; `down` removes it. It is a plain container, not the cluster's
database, so it cannot disturb anything in k3s.

If the pull fails with *"keychain cannot be accessed"*, docker's credential helper cannot
reach the macOS keychain from a non-interactive shell — run the script from a normal
terminal, or unlock the keychain once with
`security -v unlock-keychain ~/Library/Keychains/login.keychain-db`.

Without a database the agent still starts and serves `/healthz`, `/metrics`, the Blazor UI
and the webhooks; only the `/api/*` routes that read incidents return 500. That is the
intended degradation — a monitoring agent that refuses to boot because its own database is
down is useless at exactly the wrong moment.

There is deliberately **no `global.json` in this repo.** Adding one turns a loud, actionable
error into a quiet *"Zero tests ran"* — and in a repo whose tests are the safety argument,
the loud failure is worth more. Revisit after a xunit.v3 bump.

The tests in `Watchtower.Tests` covering `PolicyEngine` are not routine coverage — **they are
the argument that L3 is safe.** Treat a change that weakens them as a change to the safety
model.
