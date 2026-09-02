# Install

Multi-arch images and the Helm chart are published to GHCR on every release tag, with build
provenance attested, and both are pullable anonymously.

```sh
helm install hephaisto oci://ghcr.io/flou21/charts/hephaisto
```

That resolves the newest published chart. Add `--version 0.6.0` to pin one; the chart version and
the app version are the same number, set by the tag.

::: tip It acts nowhere until you say otherwise
Installed as it ships, `policy.actionableNamespaces` is empty — so no write `Role` is rendered at
all — and `mode` is `Observe`. That is the intended first install. See
[the promotion path](/guide/promotion-path) for how to change it deliberately.
:::

## The chart creates no Secrets, ever

This is a deliberate constraint, not an omission: a Secret rendered by a chart lives in your
values file, your shell history and your CI logs. Make them first.

```sh
kubectl create namespace hephaisto

kubectl -n hephaisto create secret generic hephaisto-postgres \
  --from-literal=POSTGRES_USER=hephaisto \
  --from-literal=POSTGRES_PASSWORD="$(openssl rand -base64 24)" \
  --from-literal=POSTGRES_DB=hephaisto

# Both keys are optional; which one you need depends on Llm:Provider. GEMINI_API_KEY serves the
# gemini provider and, on any provider, the embedding generator. LLM_API_KEY serves the
# openai-compatible one.
kubectl -n hephaisto create secret generic hephaisto-llm \
  --from-literal=GEMINI_API_KEY=...
```

Then install:

```sh
helm install hephaisto oci://ghcr.io/flou21/charts/hephaisto -n hephaisto \
  --set prometheusOperator.selectorLabels.release=<your-kube-prometheus-stack-release> \
  --set postgres.embedded.enabled=true
```

Every secret **name** the chart expects is required rather than silently dangling — including the
conditional ones. Setting `grafanaMcp.url` with an empty `secrets.grafanaMcp`, enabling a signed
webhook with no secret, or enabling Teams with no secret are all hard render failures.

## The most dangerous setting

`prometheusOperator.selectorLabels` must match the release label your kube-prometheus-stack uses
to select rules.

Get it wrong and **everything looks fine**: every object is created, `kubectl get prometheusrule`
shows them all present, Prometheus selects none of them, and the agent reports itself perfectly
healthy while seeing nothing at all. There is no error anywhere.

```sh
# What your stack selects on
kubectl get prometheus -A -o jsonpath='{.items[*].spec.ruleSelector}'

# What the chart labelled its rules with
kubectl -n hephaisto get prometheusrule -o jsonpath='{.items[*].metadata.labels}'
```

Run [the verification steps](/operate/verify) after installing. `NOTES.txt` prints two of them and
they are worth actually running rather than reading.

## Three defaults worth knowing before you install

- **`policy.actionableNamespaces` is empty**, so no write Role is created and the agent may act
  nowhere. Naming a `kube-*` namespace, `default`, its own namespace or the observability
  namespace is a hard render failure, not a dropped entry.
- **`networkPolicy.extraIngressCIDRs` is empty.** It is sometimes the only way to keep kubelet
  probes working — but every CIDR you add can forge an alert to an unauthenticated,
  incident-creating endpoint. The Alertmanager webhook cannot authenticate its caller, so that
  NetworkPolicy *is* its authentication.
- **The cordon/drain ClusterRole is created unbound**, and no value binds it. That stays a
  hand-written `ClusterRoleBinding` in its own commit.

`charts/hephaisto/ci/negative-tests.sh` asserts all of the above as tests, so they cannot rot
quietly. See [Reserved env and safety rails](/reference/env-and-rails).

## Building from source

**On a laptop:**

```sh
git clone https://github.com/Flou21/hephaisto
cd hephaisto

./scripts/dev-db.sh up          # throwaway Postgres 17 + pgvector on :5433
dotnet run --project src/Hephaisto.AppHost
```

`Hephaisto.AppHost` is .NET Aspire, and it is **dev-time only** — excluded from the container
image, and no manifest references it.

## Upgrading

The chart version and the app version are the same number. Database migrations are applied by a
separate Job rather than at pod startup, because every replica would otherwise race the DDL —
`Persistence:ApplyMigrationsOnStartup` defaults to `false` for that reason and should stay there
in a cluster.

Check [the changelog](/project/changelog) before upgrading across a minor version.
