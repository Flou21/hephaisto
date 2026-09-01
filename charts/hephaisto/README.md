# hephaisto

An autonomous SRE agent that investigates Kubernetes incidents and reports what it found.

It receives Alertmanager webhooks, investigates with PromQL, LogQL and the Kubernetes API
under a step, token, cost and wall-clock budget, writes a diagnosis citing the evidence it
used, and — where you have allowed it to — executes a narrow allowlist of reversible actions,
verifies them, and reverts or escalates when they do not hold.

```sh
helm install hephaisto oci://ghcr.io/flou21/charts/hephaisto
```

**Installed as it ships, the agent acts nowhere.** `policy.actionableNamespaces` is empty, so no
write `Role` is rendered at all; `policy.autoEnabledActionTypes` is empty; and `mode` is
`Observe`. Enabling anything takes four deliberate changes, in git — naming a namespace,
labelling that namespace `hephaisto.io/destructive-actions-allowed: "true"`, promoting one
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
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release>
```

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
