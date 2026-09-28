# hephaisto

An autonomous SRE agent that investigates Kubernetes incidents and reports what it found.

It receives Alertmanager webhooks, investigates with PromQL, LogQL and the Kubernetes API
under a step, token, cost and wall-clock budget, writes a diagnosis citing the evidence it
used, and — where you have allowed it to — executes a narrow allowlist of reversible actions,
verifies them, and reverts or escalates when they do not hold.

```sh
helm install hephaisto oci://ghcr.io/flou21/charts/hephaisto \
  --namespace hephaisto --create-namespace \
  --set cluster.name=<the cluster label on this cluster's metrics>
```

`cluster.name` is the one value with no default, and rendering fails without it: it is in every
signal fingerprint and in every query the model writes.

**Installed as it ships, the agent acts nowhere.** `policy.actionableNamespaces` is empty, so no
write `Role` is rendered at all; `policy.autoEnabledActionTypes` is empty; and `mode` is
`Observe`. Enabling anything takes four deliberate changes, in git — naming a namespace,
labelling that namespace `hephaisto.dev/destructive-actions-allowed: "true"`, promoting one
action type, and raising the mode.

## What it needs

| | |
|---|---|
| Kubernetes | any recent version; the chart targets prometheus-operator CRDs |
| **PostgreSQL 17 with `pgvector`** | required — the agent fails fast without it, on purpose |
| Prometheus + Alertmanager + prometheus-operator | required — the shipped `PrometheusRule`s are its input |
| A model API key | Gemini, or any OpenAI-compatible endpoint including a local Ollama |
| Grafana + `grafana-mcp` | optional; without it the agent degrades to Kubernetes-only reads |

`postgres.embedded.enabled=true` brings up a single-replica StatefulSet for evaluation. It is
explicitly **not** a production database.

## This chart creates no Secrets, ever

A value passed to a chart ends up in `helm get values`, in the release Secret, and in whatever
git repo holds your Argo Application — forever, and readable by anyone with `get` on Secrets in
that namespace. So every secret is *referenced* by name and each reference is wrapped in
`required`, which makes a missing one fail at template time rather than twenty minutes later as
`CreateContainerConfigError` on a pod nobody is watching yet.

```sh
kubectl create namespace hephaisto

kubectl -n hephaisto create secret generic hephaisto-postgres \
  --from-literal=POSTGRES_USER=hephaisto \
  --from-literal=POSTGRES_PASSWORD='...' \
  --from-literal=POSTGRES_DB=hephaisto \
  --from-literal=POSTGRES_APP_PASSWORD='...'

# Both keys are optional; which you need depends on Llm:Provider.
kubectl -n hephaisto create secret generic hephaisto-llm \
  --from-literal=GEMINI_API_KEY='...'

helm install hephaisto oci://ghcr.io/flou21/charts/hephaisto \
  -n hephaisto \
  --set cluster.name=<your-cluster-label> \
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release>
```

Two more are worth setting on day one. `secrets.webhookToken` names a Secret whose key `token`
the Alertmanager webhook then requires as a bearer token - Alertmanager sends it with
`http_config.authorization.credentials_file` on the receiver. And `llm.pricing`, if your model is
not in the built-in price table: with a cost cap set (they all are, by default) the agent refuses
to start on a model it cannot price, because an unpriced model bills as $0 and no cap would bind.

That last `--set` matters more than it looks: if your Prometheus does not select the shipped
`PrometheusRule`s, the agent detects nothing and reports itself perfectly healthy. `NOTES.txt`
prints the two commands that verify it, and they are worth running.

## Configuring it

`values.yaml` is densely commented and `values.schema.json` rejects invalid combinations at
template time rather than at runtime — including several that would look like a working install.

The agent binds far more configuration than the chart promotes to values: `Llm:Model`,
`Llm:Investigation:MaxSteps`, `Llm:Budget:MaxCostUsdPerHour` and the rest are settable as
`Section__Key` environment variables through `extraEnv`. That is deliberate — mirroring every
options class in YAML would duplicate them and drift the first time one is renamed.

## Code fixes (v0.9.0)

When an investigation's grounded primary finding is a bug in application code, nothing in the
cluster can fix it. With `codeFix.enabled`, Hephaisto hands that finding to a **coder** — Claude
Code in a Kubernetes Job of its own — which clones the repository the workload is mapped to and
writes a fix **plan**, read-only and automatically. After a human approves the plan, a second Job
implements it on a `hephaisto/codefix-*` branch, runs the build and tests, and opens a **Draft
PR**. A human reviews, merges and deploys; nothing here does.

It ships **off, and unrendered**: `codeFix.enabled: false` renders no coder object at all, and
`codeFix.mode` (`off | plan | pr`) is its own axis, independent of the agent's `mode`. An
`Observe` agent can still plan code fixes — planning writes nothing anywhere — and the agent's kill
switch still stops the stage.

**What the coder can reach.** No cluster identity: its ServiceAccount is bound to nothing and its
token is never mounted. No Hephaisto credential and no inbound surface: it answers by printing a
framed result to its own log, which Hephaisto reads. Egress only through a squid allowlist proxy
(`codeFix.egressProxy`), enforced by a NetworkPolicy that is independent of the top-level one —
DNS and the proxy, nothing else — and every request it makes is a line in the proxy's log.

**What Hephaisto gains.** `create jobs`, in exactly one namespace. The chart refuses to render if
that namespace is `default`, `kube-*`, the release or observability namespace, or any
`policy.actionableNamespaces` entry, and the agent refuses to boot if it holds `create jobs`
anywhere else.

```sh
kubectl create namespace hephaisto-coder
# Every key optional. GITHUB_TOKEN: a FINE-GRAINED PAT, contents:write + pull_requests:write,
# limited to the mapped repositories. Hephaisto never reads this Secret.
kubectl -n hephaisto-coder create secret generic hephaisto-codefix \
  --from-literal=CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-... \
  --from-literal=GITHUB_TOKEN=github_pat_...

helm upgrade hephaisto oci://ghcr.io/flou21/charts/hephaisto -n hephaisto --reuse-values \
  --set codeFix.enabled=true --set codeFix.mode=plan \
  --set codeFix.contextRepository.url=https://github.com/you/dev-context \
  --set 'codeFix.repositories[0].workload=shop/Deployment/shop-api' \
  --set 'codeFix.repositories[0].url=https://github.com/you/shop'
```

Protect the default branch of every mapped repository before the first attempt — no direct push,
no force push, for everyone including the coder's token. It is the one control the chart cannot
check, and `NOTES.txt` lists the repositories it applies to. Start in `plan`, read the plans, and
move to `pr` only with `auth.enabled: true`: approving a repository write on an unauthenticated
click is refused at render time and again at startup.

## Try it without a cluster

The published image can run with no Kubernetes behind it at all, loaded with recorded
investigations from a real cluster:

```sh
curl -fsSL https://raw.githubusercontent.com/Flou21/hephaisto/main/demo/compose.yaml \
  | docker compose -f - up
```

## Links

- [Source, and the documentation](https://github.com/Flou21/hephaisto)
- [What is known to be broken](https://github.com/Flou21/hephaisto/blob/main/docs/backlog.md)
- [How it is verified](https://github.com/Flou21/hephaisto/blob/main/docs/verification.md)

Licensed AGPL-3.0-only.
