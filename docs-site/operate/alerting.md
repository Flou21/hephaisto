# Alerting and `hephaisto_kind`

Every `PrometheusRule` the agent consumes carries a `hephaisto_kind` label whose value **must** be
a member of `Hephaisto.Core.Domain.SignalKind`. That label is how an alert selects the runbook the
model is given.

## Why this has its own page

`Enum.TryParse` fails **silently** on anything else. The classifier then falls back to guessing
from the alert name — which for something like `KubeContainerWaiting` yields `Unknown` and the
default runbook instead of the image-pull one.

Nothing about that is visible from either side: the YAML looks well-labelled and the classifier
looks correct. You get slightly worse diagnoses and no indication why.

## The valid values

These are the eleven kinds with a shipped runbook, plus the fallback:

| `hephaisto_kind` | Runbook |
|---|---|
| `CrashLoopBackOff` | The restart count is the symptom, never the cause |
| `ImagePullBackOff` | The event message is the entire diagnosis |
| `ConfigError` | A referenced ConfigMap/Secret key is missing — **no logs exist** |
| `OomKilled` | `exitCode: 137`, killed without warning, usually no logs |
| `Unschedulable` | The reason exists **only** in Kubernetes Events |
| `ReadinessFlapping` | The false-positive test — restarting a flapping pod is wrong |
| `NodePressure` | Escalate scope immediately; other pods are consequences |
| `PvcNearlyFull` | Above 85/90%, with the PromQL inline |
| `JobFailed` | Exhausted `backoffLimit` |
| `HighErrorRate` | Span-metrics; there may be **no** Kubernetes symptom at all |
| `Unknown` | `_Default.md` — the fallback |

See [Incident reference](/internals/runbooks/) for what each one actually tells the model.

## Writing your own rule

```yaml
- alert: MyServiceCrashLooping
  expr: |
    increase(kube_pod_container_status_restarts_total[15m]) > 3
  for: 5m
  labels:
    severity: warning
    hephaisto_kind: CrashLoopBackOff   # must parse, or you silently get Unknown
  annotations:
    summary: "{{ $labels.pod }} is restarting repeatedly"
```

The `namespace` and `pod` labels are what the agent uses to build its target, so make sure your
expression preserves them.

## How this is prevented from rotting

`ShippedAlertRulesTests` reads the real rule files and fails if any alert declares a kind that does
not parse, or classifies as `Unknown`. That covers the rules this chart ships. It cannot cover
yours — so if you add rules, check the resulting incidents actually get the runbook you meant.
