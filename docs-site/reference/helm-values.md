# Helm values

<div class="hp-source">

This page **is** `charts/hephaisto/values.yaml`, transcluded from the chart rather than described.
Its comments are the reference; a copy would drift from the chart within a release.

</div>

Every value is also validated by `charts/hephaisto/values.schema.json`, which enforces the enums
for `mode`, `image.pullPolicy` and `otel.protocol`, and the `TimeSpan` shape of
`alertmanager.maxDuration`. An invalid value is a render failure, not a runtime surprise.

## The four that decide what the agent can do

Read these before the rest.

| Value | Default | Why it matters |
|---|---|---|
| `mode` | `Observe` | `Off`, `Observe`, `DryRun`, `Auto`. The env arm and database arm can only make this *more* restrictive. |
| `policy.actionableNamespaces` | `[]` | Empty means act nowhere — **and renders no write `Role` at all**. |
| `policy.autoEnabledActionTypes` | `[]` | Empty means everything waits for a human. |
| `prometheusOperator.selectorLabels.release` | `kube-prometheus-stack` | Wrong value fails **silently**: rules exist, Prometheus selects none, agent reports healthy. |

## The file

<<< ../../charts/hephaisto/values.yaml{yaml}

## Worked examples

A minimal install, and an everything-on install, both of which CI renders on every push:

::: code-group

<<< ../../charts/hephaisto/ci/minimal-values.yaml{yaml} [minimal-values.yaml]

<<< ../../charts/hephaisto/ci/full-values.yaml{yaml} [full-values.yaml]

:::
