# Hephaisto — working notes for agents

**Read `README.md` first** for what Hephaisto is, the safety model and the invariants. This
file is the part that does not belong in a public readme: how to run the thing on *this*
machine, and the traps that have already cost time.

## Everything here is in git

This is one project, so `~/hephaisto` **is** the git repo and every Dockerfile, manifest,
values file and Tiltfile is tracked. A fresh clone builds. Keep it that way. (This differs
from the `~/dev` workspace next door, which holds ten repos with untracked `Dockerfile.dev`
files — the reason `git worktree` is unusable there and fine here.)

The one deliberate exception is `tilt_config.json`: per-machine settings, ignored, with
`tilt_config.sample.json` tracked beside it and the Tiltfile defaulting to `localhost` so a
clone with no config still works.

There is no `nuget.config`, no private feed and **no PAT build argument anywhere**.
Hephaisto references no internal package. If you find yourself adding a credential to a
Dockerfile, something has gone wrong.

## Layout

```
src/Hephaisto.Core/            domain, state machine, policy, digester — ZERO I/O
src/Hephaisto.ServiceDefaults/ OTel wiring, health, resilience — deployed as a dll
src/Hephaisto.Agent/           THE pod: tools, hosted services, Blazor UI, persistence
src/Hephaisto.AppHost/         Aspire — dev-time orchestration only, NEVER deployed
src/Hephaisto.Simulator/       dev-only fault generator
infra/                         namespaces, observability stack, chaos fixtures, RBAC
```

**If you are tempted to add a package to `Core` that talks to something, the design has
drifted.** The fact belongs in `ClusterFacts`, gathered by the caller, and passed in. The
zero-I/O rule is what makes the whole safety surface unit-testable.

### Aspire is dev-time only

`Hephaisto.AppHost` gives you `dotnet run` with Postgres, the agent and the simulator on a
laptop. It is excluded from the container image and no manifest references it.
`Hephaisto.ServiceDefaults` is a plain class library with an ASP.NET framework reference and
**no `Aspire.Hosting.*` package** — referencing one there would drag the orchestrator into
the pod.

Manifests are **hand-written**. Do not use `aspire publish` or aspir8:
`Aspire.Hosting.Kubernetes` is preview and emits Helm charts, which would be a second source
of truth drifting from what Tilt applies. More importantly this pod's ServiceAccount,
ClusterRole and RoleBindings *are* the security boundary — they must be reviewable
human-written diffs with comments explaining why each verb is granted.
`infra/app/rbac.yaml` is the most valuable prose in the repo.

### Telemetry is never gated on a dev flag

There is deliberately no global off switch in `ServiceDefaults`, and no `DEV_LOGGING`-style
early return. For a project whose product *is* telemetry, being blind in development is a
defect rather than a tradeoff — and it is how you end up shipping an agent that is blind in
production. Every Aspire convenience degrades instead: no OTLP endpoint means console plus
`/metrics`, never a crash and never silence.

## Tilt deploys the chart, not the manifests

`tilt up` renders `charts/hephaisto` with `values-dev.yaml` and applies that. Every start is
therefore a render test of the chart a consumer installs, and dev and prod cannot drift into
two sources of truth. `infra/app/*.yaml` is kept as the reference for what this cluster ran
before the chart existed; it is no longer applied.

Two consequences that cost an afternoon to find:

- **Moving an object between Tilt resources needs a RESTART, not a re-evaluation.** Tilt
  re-evaluated the switch to the chart happily and reported everything ok - then garbage
  collected the ServiceAccount, ConfigMaps and NetworkPolicies a second after applying them,
  because the *previous* Tiltfile's `uncategorized` resource still owned them and they were no
  longer in its set. The symptom was `serviceaccount "hephaisto" not found` on a pod that
  could not start. Stop Tilt and `tilt up` again; never `tilt down`.
- **`values-dev.yaml` sets `securityContext.readOnlyRootFilesystem: false` and a 3Gi limit,
  and both are load-bearing.** Tilt runs the DEV image, whose entrypoint is `dotnet watch` - a
  compiler. With a read-only root it dies at startup with `Read-only file system:
  '/app/src/Hephaisto.Core/obj/Debug'`, which reads like a permissions bug and is a design
  mismatch; at 1Gi it is OOM-killed about ninety seconds after every hot reload. The chart's
  defaults (read-only, 1Gi) are correct for the published image and wrong for this one.

## Tilt is the inner loop, on port 10351

**Run Tilt from `~/hephaisto`.** It coexists with the `~/dev` instance already running
detached on 10350; the two share a cluster but no ports.

```sh
cd ~/hephaisto && tilt up --host $HOST_IP --port 10351

# every other tilt CLI call needs the same two flags, because the client
# defaults to localhost:10350 and the server is no longer there
tilt logs   -f hephaisto        --host $HOST_IP --port 10351
tilt trigger  hephaisto         --host $HOST_IP --port 10351
```

`$HOST_IP` is the `host-ip` value from `tilt_config.json`. **`--host` is not optional.** The
port-forwards in the Tiltfile bind to that interface because each one passes `host=`
explicitly, but Tilt's own web UI is a separate server that defaults to `127.0.0.1` — so
without the flag every service below is reachable from another machine and the Tilt UI alone
is not, which reads as a network problem rather than a missing flag.

Because the forwards bind to that interface, **use that hostname, not `localhost`** — not
even from a shell on this machine. Throughout the docs it is `$H`:

```fish
set -x H (jq -r '.host // "localhost"' ~/hephaisto/tilt_config.json)
```

| What | Port | |
|---|---|---|
| Hephaisto UI | 8100 |
| Prometheus | 9090 |
| Grafana | 3030 |
| Alertmanager | 9093 |
| Loki | 3100 |
| Tempo | 3200 |
| OTel Collector (OTLP grpc / http) | 4317 / 4318 |
| grafana-mcp | 8200 |
| Aspire Dashboard | 18888 |
| Postgres | 5433 |
| Tilt UI | 10351 | (needs `--host`, see above) |

### The Gemini key

`scripts/bootstrap-secrets.sh` creates every secret except this one, which it skips unless
`HEPHAISTO_GEMINI_API_KEY` is exported. The alternative, and the easier one:

```sh
$EDITOR secrets/hephaisto-llm.secret.yaml     # replace the placeholder
kubectl apply -f secrets/hephaisto-llm.secret.yaml
kubectl -n hephaisto rollout restart deploy/hephaisto
```

It uses `stringData`, so the key goes in verbatim — no base64. The file is gitignored twice
(`secrets/` and `*.secret.yaml`) and is the only place in the repo allowed to hold a live
credential.

**A wrong key is quiet.** The agent does not crash without a valid one: the model call fails,
the incident escalates with the error, and detection, dedup, correlation and the UI carry on.
So confirm it took, rather than assuming — the chat span in Tempo carries
`gen_ai.usage.input_tokens` once a call actually succeeds:

```sh
curl -s -G "http://$H:3200/api/search" --data-urlencode 'q={name=~"chat.*"}' | jq '.traces | length'
```

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

## Alert rules must declare a `hephaisto_kind` that is a real `SignalKind`

Every `PrometheusRule` carries a `hephaisto_kind` label whose value has to be a member name of
`Hephaisto.Core.Domain.SignalKind`. That label is how an alert selects the runbook the model
is given.

`Enum.TryParse` fails **silently** on anything else, and the classifier then falls back to
guessing from the alertname — which for a name like `KubeContainerWaiting` yields `Unknown`
and the default runbook instead of the image-pull one. Nothing about that is visible from
either side: the YAML looks well-labelled and the classifier looks correct.

`ShippedAlertRulesTests` reads the real files in `charts/hephaisto/files/alerts/` and fails if
any alert declares a kind that does not parse, or classifies as `Unknown`. If you add a rule
whose failure mode has no matching `SignalKind`, add the member **and its runbook** rather
than inventing a label value.

## Versioning: never call `minver` without `-p main.0`

`Directory.Build.props` sets `MinVerDefaultPreReleaseIdentifiers=main.0`, but `minver-cli`
does not read it - it has its own default of `alpha.0`. So:

```sh
dotnet minver -t v            # 0.0.0-alpha.0.47   <- WRONG, and looks fine
dotnet minver -t v -p main.0  # 0.0.0-main.0.47    <- what MSBuild stamps
```

Get this wrong in a build script and the image tag disagrees with the assembly inside it,
which surfaces as `/api/version` reporting something the registry has never heard of.

The dev image reports `0.0.0-dev` on purpose: there is no `.git` inside the build context,
and a dev image reporting a release-shaped number is how a dev build gets mistaken for a
release in a screenshot.

## The cluster is a single shared resource

There is one k3s node, shared with the whole stack in the `~/dev` workspace. Parallel agents
can draft code and run unit tests freely, but **cluster verification is serial**. Two agents
applying chaos fixtures at once produce garbage for both.

## Verifying a change

Prefer the running cluster over reasoning about it.

```sh
cd ~/hephaisto && dotnet build && ./scripts/test.sh
tilt trigger hephaisto --host $HOST_IP --port 10351
kubectl -n hephaisto logs deploy/hephaisto --tail=50
curl -s http://$H:8100/healthz

# RBAC is actually bounded: first three must be "no", the last "yes"
kubectl auth can-i delete secrets             --as=system:serviceaccount:hephaisto:hephaisto -A
kubectl auth can-i delete pods -n kube-system --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i create clusterrolebindings --as=system:serviceaccount:hephaisto:hephaisto
kubectl auth can-i delete pods -n hephaisto-chaos --as=system:serviceaccount:hephaisto:hephaisto
```

The **five-hop correlation test** is the acceptance test for the whole observability stack —
exemplar → trace → logs → metrics → service graph. It is written out in
`docs/verification.md`; if all five hops work, the traces, exemplar, OTLP metrics, OTLP logs
and both Grafana correlation configs are simultaneously proven.

## Testing

### Run tests with `./scripts/test.sh`, not `dotnet test`

`dotnet test` **cannot run this suite** on the current toolchain. Without a `global.json`
`test.runner` entry it hard-errors; with one it starts the test executable in
`--server dotnettestcli` mode and exits in ~200 ms reporting *"Zero tests ran"*.

This was checked against xunit.v3 3.2.2 and 4.0.0, with and without the VSTest adapter, and
with and without central transitive pinning — identical every time, so it is xunit.v3's
server-mode support rather than anything in this repo. A xunit.v3 test project is an
executable, and running it directly works: every test discovers and runs, and the exit code
is honest (0 on pass, non-zero otherwise), so CI is safe.

There is deliberately **no `global.json` in this repo.** Adding one turns a loud, actionable
error into a quiet *"Zero tests ran"* — and in a repo whose tests are the safety argument,
the loud failure is worth more. Revisit after a xunit.v3 bump.

Assertions are **AwesomeAssertions**, the Apache-2.0 community fork of FluentAssertions,
API- and namespace-compatible. FluentAssertions 8.x moved to a paid Xceed licence for
commercial use, so bumping to it is a procurement decision rather than a dependency bump.

The tests covering `PolicyEngine` are not routine coverage — **they are the argument that L3
is safe.** Treat a change that weakens them as a change to the safety model.

### Local database

`./scripts/dev-db.sh up` starts a throwaway Postgres 17 + pgvector on port **5433** and
applies the migrations; `down` removes it. It is a plain container, not the cluster's
database, so it cannot disturb anything in k3s. `tests/Hephaisto.IntegrationTests` reads
`ConnectionStrings__hephaisto` and **throws** if it is unset, rather than skipping silently.

If the pull fails with *"keychain cannot be accessed"*, docker's credential helper cannot
reach the macOS keychain from a non-interactive shell — run the script from a normal
terminal, or unlock the keychain once with
`security -v unlock-keychain ~/Library/Keychains/login.keychain-db`.

**The agent does not start without a database.** `Program.cs` awaits
`MigrateHephaistoDatabaseAsync()` before `RunAsync()`, and `AddHephaistoPersistence` throws at
registration when there is no connection string. That is deliberate — an agent that cannot
persist must not pretend to be healthy, because "no audit, no action" is only enforceable if
the audit store is known to be there.
