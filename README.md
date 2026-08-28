# Hephaisto

An autonomous SRE agent that lives in your Kubernetes cluster. It receives Alertmanager
webhooks, investigates what is wrong using PromQL, LogQL, traces and the Kubernetes API,
and writes up a diagnosis with the evidence it used.

It is also a first-class *producer* of telemetry: every investigation is a trace you can
open in Grafana, step through, and then ask the agent about.

> **Status: pre-0.0.1, not yet released.** The detection and investigation half is
> deployed and exercised against a chaos-fixture suite. The *acting* half — see
> [What exists today](#what-exists-today) — is designed, policy-checked and unit-tested,
> but the executor itself is not written yet. Do not point this at anything you care
> about.

## What it does

```
Alertmanager ──▶ ingest ──▶ dedup + correlate ──▶ incident
                                                    │
                                                    ▼
                                          investigation loop
                                    (read-only tools, budget-capped)
                                                    │
                                                    ▼
                                  diagnosis + evidence + proposed plan
                                                    │
                                             ┌──────┴──────┐
                                             ▼             ▼
                                        escalate      policy engine
                                        to a human    (default-deny)
```

An investigation is a bounded agentic loop. The model gets read-only tools — Kubernetes
reads, PromQL, LogQL, trace search — and a step, token, cost and wall-clock budget. It
ends by calling `conclude` with a root cause, the evidence it relied on, and a confidence.
Everything it did is persisted as a step trace you can replay in the UI.

## What exists today

Being precise about this matters, because the difference is the whole safety argument.

| | |
|---|---|
| Alertmanager ingest, dedup, correlation, suppression | **works** |
| Investigation loop with read-only tools, budgets, step trace | **works** |
| Diagnosis, evidence, semantic incident search | **works** |
| Policy engine — default-deny, pure, exhaustively unit-tested | **works** |
| Kill switch — three independent arms, most restrictive wins | **works** |
| Audit log, budgets, cooldowns, oscillation detection | **works** |
| Plan generation (schema-constrained, no tools) | **works** |
| **Executing a plan against the cluster** | **not built** |
| **Automatic verification and rollback** | **not built** |
| Approval workflow, runbook memory | not built |

So today Hephaisto runs at `Observe`: it detects, investigates, diagnoses and annotates,
and it never mutates anything. The machinery that would *gate* an action is built and
tested ahead of the action itself, deliberately — the policy engine is the argument that
auto-remediation would be safe, and it should exist and be trusted before anything can act.

## The safety model

The design target is **L3**: auto-execute a narrow allowlist of low-risk, reversible
actions in allowlisted namespaces, budget-capped, with automatic rollback. Everything else
escalates to a human. Five things are meant to make that defensible.

**1. RBAC is the hard floor.** Read access is cluster-wide. Write access is a `Role` bound
into specific namespaces and nowhere else. **No access to Secrets at all, ever.** The
cordon/drain `ClusterRole` ships *unbound* — binding it is an explicit, separate human act.
No amount of prompt injection changes what a ServiceAccount is allowed to do.

**2. The model never holds a mutating tool handle.** The three phases are separated on
purpose:

- *investigate* — read-only tools only
- *plan* — **no tools at all**; emits JSON against a fixed schema
- *execute* — pure C# over a closed `ActionType` enum

A prompt injection in a log line can, at its very best, produce a plan that the policy
engine then rejects. It cannot reach an API call directly.

**3. The policy engine is pure and default-deny.** It is a function over facts passed in by
the caller — no I/O — which is exactly what makes it exhaustively unit-testable. An empty
namespace allowlist means *act nowhere*, and that is the default.

**4. Every auto action must be reversible**, and gets verified and reverted on failure at
T+60s / T+5m / T+15m. *(Design; the verification scheduler is not built yet.)*

**5. Budgets, cooldowns and oscillation detection** cap the worst sustained case at roughly
ten pod restarts an hour — indistinguishable from a badly tuned HPA.

### Two invariants that must never be weakened

- **No audit, no action.** If Postgres is unreachable, the executor refuses to act. An
  unreadable mode ConfigMap is read as `Observe`, never as `Auto`.
- **The budget check, the cooldown check, the kill-switch check and the action INSERT are
  one transaction.** This is why the agent is a single pod with `strategy: Recreate`. Split
  them across replicas and it becomes a distributed TOCTOU race on the one code path where
  losing the race means an unintended `kubectl delete`.

### The kill switch

Three independent arms — an environment variable, a projected ConfigMap, and a database row
— and **the most restrictive one wins**. Modes are `Off` (ingest nothing, investigate
nothing), `Observe` (never mutate), `DryRun` (run the whole flow, but every Kubernetes call
carries `dryRun=All`), and `Auto`.

```sh
kubectl -n hephaisto set env deploy/hephaisto HEPHAISTO_MODE=Off
```

### Approval identity is attribution, not authentication

`ApprovedBy` is free text typed into the UI. On a single-operator cluster that is an
acceptable trade, and the schema is already OIDC-shaped so the upgrade populates the same
string from a verified claim. **The risk to watch is habituation.** If this ever runs
somewhere with more than one operator, OIDC stops being a roadmap item and becomes a
blocker.

## Requirements

- A Kubernetes cluster and a Prometheus/Alertmanager stack that can POST to a webhook
- **PostgreSQL 17 with `pgvector`** — the agent fails fast without it, on purpose
- A Gemini API key (the provider is pluggable via `Llm:Provider`; only `gemini` ships today)
- Optionally Grafana + `grafana-mcp`, which is what gives the agent PromQL and LogQL tools.
  Without it the agent degrades to Kubernetes-only reads and says so in its logs.

## Running it

Nothing is published yet — no image on a registry, no chart in an OCI repo. That is what
0.0.1 is for. Today you build from source.

**On a laptop:**

```sh
git clone https://github.com/Flou21/hephaisto
cd hephaisto

./scripts/dev-db.sh up          # throwaway Postgres 17 + pgvector on :5433
dotnet run --project src/Hephaisto.AppHost
```

`Hephaisto.AppHost` is .NET Aspire, and it is **dev-time only** — it is excluded from the
container image and no manifest references it.

**In a cluster**, via the chart in `charts/hephaisto`:

```sh
# The chart creates no Secrets, ever. Make them first.
kubectl create namespace hephaisto
kubectl -n hephaisto create secret generic hephaisto-postgres \
  --from-literal=POSTGRES_USER=hephaisto \
  --from-literal=POSTGRES_PASSWORD="$(openssl rand -base64 24)" \
  --from-literal=POSTGRES_DB=hephaisto
kubectl -n hephaisto create secret generic hephaisto-llm --from-literal=GEMINI_API_KEY=...

helm install hephaisto ./charts/hephaisto -n hephaisto \
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release> \
  --set postgres.embedded.enabled=true
```

Then read what `NOTES.txt` prints, and actually run the two checks it gives you. The chart's
most dangerous setting is `prometheusOperator.selectorLabels`: get it wrong and every object
is created, `kubectl get prometheusrule` shows them all present, Prometheus selects none of
them, and the agent reports itself perfectly healthy while seeing nothing at all.

What the chart installs: the agent, its RBAC, both NetworkPolicies, the PodMonitor, the alert
rules, a Grafana dashboard, and — opt-in — an evaluation Postgres. What it does **not**
install: Prometheus, Alertmanager, Grafana, Loki, Tempo or a collector. You already run those.

Three defaults worth knowing before you install:

- `policy.actionableNamespaces` is **empty**, so no write Role is created at all and the agent
  may act nowhere. Naming a `kube-*` namespace, `default`, its own namespace or the
  observability namespace is a hard render failure, not a dropped entry.
- `networkPolicy.extraIngressCIDRs` is **empty**. It is sometimes the only way to keep kubelet
  probes working — but every CIDR you add can forge an alert to an unauthenticated,
  incident-creating endpoint.
- The cordon/drain ClusterRole is created **unbound**, and no value binds it. That stays a
  hand-written `ClusterRoleBinding` in its own commit.

`charts/hephaisto/ci/negative-tests.sh` asserts all of the above as tests, so they cannot rot
quietly.

`infra/` still holds the hand-written manifests this repo's own dev cluster runs. They are
hand-written rather than generated because this pod's ServiceAccount, ClusterRole and
RoleBindings *are* the security boundary and must be reviewable human diffs.
`infra/app/rbac.yaml` is the most valuable prose in the repo.

### Configuration

Standard .NET configuration, so every key below is also an environment variable with `__`
for `:`.

| Section | What it holds |
|---|---|
| `Persistence:ConnectionString` | Postgres. Required — startup fails without it. |
| `Llm:Provider`, `Llm:Model` | Provider selection and the investigating model |
| `Llm:Budget` | Global rolling token/cost windows, counted in Postgres |
| `Investigation` | Per-run step, token, cost and wall-clock budgets |
| `Policy` | `AllowedNamespaces`, protected namespaces and labels, rate caps |
| `Grafana:McpUrl` | grafana-mcp, for PromQL/LogQL tools |
| `KillSwitch` | Arm configuration; env arm defaults to `HEPHAISTO_MODE` |

`Policy:AllowedNamespaces` defaults to **empty**, which means the agent may act nowhere.
`Policy:ProtectedNamespaces` is never actionable whatever the allowlist says, and includes
Hephaisto's own namespace and the observability stack — a self-inflicted outage would also
blind the agent to the fact that it caused one.

### HTTP surface

| | |
|---|---|
| `POST /webhooks/alertmanager` | Alertmanager receiver |
| `POST /webhooks/watchdog` | dead-man's-switch receiver |
| `GET /api/incidents`, `/{id}` | list and read incidents |
| `GET /api/incidents/search?q=` | semantic search over incidents |
| `POST /api/incidents/{id}/reinvestigate` | re-drive an incident's investigation |
| `POST /api/incidents/{id}/feedback` | mark a diagnosis right or wrong |
| `GET /api/status` | mode, budgets, kill-switch arms |
| `GET /api/version` | the running version and commit; touches no database |
| `GET /healthz`, `/readyz`, `/metrics` | health and Prometheus metrics |
| `/` | Blazor Server UI |

**The Alertmanager webhook is unauthenticated** (Alertmanager cannot authenticate to a
receiver). It is protected by a NetworkPolicy, and that NetworkPolicy is therefore its
*entire* authentication. If you deploy this, get that policy right.

## Alert rules must declare a real `hephaisto_kind`

Every `PrometheusRule` carries a `hephaisto_kind` label whose value must be a member of
`Hephaisto.Core.Domain.SignalKind`. That label is how an alert selects the runbook the model
is given.

`Enum.TryParse` fails **silently** on anything else, and the classifier then falls back to
guessing from the alert name — which for something like `KubeContainerWaiting` yields
`Unknown` and the default runbook instead of the image-pull one. Nothing about that is
visible from either side: the YAML looks well-labelled and the classifier looks correct.
`ShippedAlertRulesTests` reads the real rule files and fails if any alert declares a kind
that does not parse, or classifies as `Unknown`.

## Versioning

The git tag is the only source of truth. There is no `version.json` and no `<VersionPrefix>`
to bump: [MinVer](https://github.com/adamralph/MinVer) asks git, so a release is `git tag
v0.0.1` and nothing else.

```
tag v0.0.1          ->  0.0.1
main, 42 commits    ->  0.0.2-main.0.42        (+<commit> as build metadata)
```

The same number is the image tag, the chart `version` and the chart `appVersion`, because
the chart ships exactly one application and both are published from the same tag. A
chart-only fix is `git tag v0.0.2`; patch tags are free.

Two things worth knowing:

- **`dotnet minver -t v -p main.0`** — the `-p` is not optional. `minver-cli` defaults to
  `alpha.0` while this repo's MSBuild properties say `main.0`, so without it the CLI and the
  compiler disagree about what the same commit is called, and the image tag stops matching
  the assembly inside it.
- **`.dockerignore` excludes `.git/`**, correctly, so MinVer cannot run inside a docker
  build. The version is computed once outside and passed in as `--build-arg VERSION=`; the
  Dockerfile then publishes with `-p:MinVerSkip=true`.

What is running is reported in four places, all reading the same assembly attribute:
`GET /api/version`, the console footer, OTel `service.version` on every span and metric, and
`hephaisto_build_info{version,commit}` for joining against any other series.

### Releasing

`git tag v0.0.1 && git push --tags`. That is the whole procedure — `.github/workflows/release.yml`
builds the image on native amd64 and arm64 runners, pushes by digest, joins them into one
multi-arch tag, attaches build provenance, and publishes the chart to
`oci://ghcr.io/flou21/charts` with `version == appVersion == image tag`.

**There is no deploy job, and there must not be one.** CI holds no kubeconfig and no cluster
credential. It publishes versions that no running cluster references; a cluster moves only
when a human changes a pinned version in a GitOps repo. The gap between "CI is green" and
"the cluster changed" is where a person decides — and the thing being published can delete
pods.

## Repo layout

```
src/Hephaisto.Core/            domain, state machine, policy, digester — ZERO I/O
src/Hephaisto.ServiceDefaults/ OTel wiring, health, resilience
src/Hephaisto.Agent/           THE pod: tools, hosted services, Blazor UI, persistence
src/Hephaisto.AppHost/         Aspire — dev-time orchestration only, NEVER deployed
src/Hephaisto.Simulator/       dev-only fault generator
infra/                         namespaces, observability stack, chaos fixtures, RBAC
```

**`Hephaisto.Core` has zero I/O dependencies, on purpose.** Every safety-critical decision —
the policy engine, the state machine, budgets, oscillation detection, log digestion — is a
pure function over facts passed in by the caller. Nothing in `Core` opens a socket, reads a
file or touches a database. That is what makes the safety surface testable without a
cluster, a database or an LLM. If you are tempted to add a package to `Core` that talks to
something, the design has drifted: the fact belongs in `ClusterFacts`, gathered by the
caller, and passed in.

## Development

```sh
dotnet build
./scripts/test.sh               # NOT `dotnet test` — see below
./scripts/dev-db.sh up          # Postgres 17 + pgvector on :5433, for the integration suite
```

xUnit v3 on Microsoft.Testing.Platform, with
[AwesomeAssertions](https://github.com/AwesomeAssertions/AwesomeAssertions) — the Apache-2.0
community fork of FluentAssertions.

**`dotnet test` cannot run this suite** on the current toolchain. Without a `global.json`
`test.runner` entry it hard-errors; with one it starts the test executable in
`--server dotnettestcli` mode and exits in ~200 ms reporting *"Zero tests ran"*. This was
checked against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter — a xunit.v3
test project is an executable, and running it directly works and gives an honest exit code.
There is deliberately no `global.json` here: adding one turns a loud, actionable error into
a quiet "Zero tests ran", and in a repo whose tests are the safety argument, the loud
failure is worth more.

**The `PolicyEngine` tests are not routine coverage — they are the argument that L3 is
safe.** Treat a change that weakens them as a change to the safety model.

### Chaos fixtures

`infra/chaos/` breaks a cluster in ten documented ways. Every scenario is manual-trigger
only, so nothing brings up a deliberately broken cluster by accident. `infra/chaos/README.md`
maps each scenario to the alert, PromQL, LogQL and Kubernetes event it should produce; that
table is the agent's regression suite.

## Licence

[Apache-2.0](LICENSE).
