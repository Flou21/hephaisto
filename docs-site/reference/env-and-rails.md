# Reserved env and safety rails

The chart refuses to render a number of configurations. This page is the list, and the reason each
one exists.

All of them are asserted by `charts/hephaisto/ci/negative-tests.sh`, which is a **pure-negative**
test harness: every check is something the chart must refuse, because positive rendering is
already covered by `helm template` in CI and it is the refusals that rot silently.

## Four environment variables `extraEnv` cannot set

| Variable | Why it is reserved |
|---|---|
| `GEMINI_API_KEY` | Comes from a Secret you created. A value in `extraEnv` lives in your values file. |
| `LLM_API_KEY` | Same. |
| `HEPHAISTO_MODE` | The kill switch's env arm. Shadowing it from a values file defeats the point of three independent arms. |
| `HEPHAISTO_SWITCHES_DIR` | Where the projected ConfigMap arm is mounted. Repointing it detaches an arm. |

`extraEnv` also cannot **half-override** the `OTEL_*` block — either the chart owns telemetry
configuration or you do — and cannot set `Policy__AllowedNamespaces__0` to widen the namespace
allowlist behind RBAC's back. That last one is the important one: the allowlist is what renders
the write `Role`, so widening it through the env would grant the agent permission the RBAC never
granted.

Every `extraEnv` entry must be named. `extraEnv` *can* still set any option the chart does not
expose — that is what it is for.

## Namespaces the write `Role` refuses

Naming any of these in `policy.actionableNamespaces` is a hard render failure:

- `kube-system`, `kube-public`, `default`
- the release namespace (the agent's own)
- the observability namespace

Including when a bad namespace is hidden behind a good one further up the list — the test asserts
index 1 specifically, because a naive implementation validates only the first entry.

The reasoning for the agent's own namespace and the observability namespace is the same: a
self-inflicted outage would also blind the agent to the fact that it had caused one.

## Secret names are required, not optional

Every secret name the chart expects is required rather than silently dangling, **including the
conditional ones**:

- `grafanaMcp.url` set with an empty `secrets.grafanaMcp`
- a signed webhook (`notifications.webhook.signed: true`) with no secret
- Teams enabled with no secret

Each is a render failure. The alternative — rendering a Deployment referencing a Secret that does
not exist — produces a pod stuck in `CreateContainerConfigError`, which is a fault the agent
itself has a runbook for. Failing at `helm template` is cheaper.

## Routing is validated at render time

- a route naming a channel that does not exist
- a route carrying an event that is not a member of the event enum
- a route with no events at all
- an invalid severity

All refused. Routing is additive-only with no deny rule, so a typo'd channel name would otherwise
mean silence rather than an error.

## The two things that are never rendered

- **A Secret.** The chart creates none, ever.
- **A `ClusterRoleBinding` for cordon/drain.** The `ClusterRole` is created unbound and no value
  binds it. Binding it stays a hand-written object in its own commit.

## Running the tests yourself

```sh
charts/hephaisto/ci/negative-tests.sh
```

Each check asserts that `helm template` fails **with an explanatory message** — matching
`may not contain|may not set|is required|don't meet the specifications of the schema` — rather
than merely failing. A refusal a reader cannot understand is a bug in the refusal.
