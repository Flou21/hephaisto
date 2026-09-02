# How configuration works

Hephaisto uses standard .NET configuration. Every key has three interchangeable spellings, and
knowing the mapping between them is most of what you need.

| Form | Example |
|---|---|
| Config key | `Llm:Model` |
| Environment variable | `Llm__Model` |
| Helm value | `llm.model`, where the chart exposes one |

**A colon becomes a double underscore.** Array elements are indexed:
`Policy__AllowedNamespaces__0`, `Policy__AllowedNamespaces__1`. Nested objects keep nesting:
`Notifications__Routes__0__Channel`.

## Where a value can come from

In increasing order of precedence:

1. `appsettings.json` baked into the image
2. `appsettings.{Environment}.json`
3. Environment variables — which is how the chart sets everything
4. The kill-switch arms, for `mode` only, where **the most restrictive arm wins** rather than the
   last one read

## The escape hatch

The chart deliberately does not expose every option as a Helm value — exposing all ~110 would
mean a values file nobody reads and a schema that lags the code. Anything not exposed is set
through `extraEnv`:

```yaml
extraEnv:
  - name: Llm__Investigation__MaxSteps
    value: "16"
  - name: Policy__WorkloadCooldown
    value: "00:30:00"
```

`extraEnv` cannot shadow the four reserved variables, cannot half-override the `OTEL_*` block, and
cannot widen the namespace allowlist behind RBAC's back. Those are enforced as chart render
failures — see [Reserved env and safety rails](/reference/env-and-rails).

## Types that are not what they look like

- **Durations are .NET `TimeSpan` strings**, not Prometheus durations. `"02:00:00"` is two hours;
  `"2h"` is a parse failure. `"7.00:00:00"` is seven days.
- **Enums are their C# member names**, case-insensitively: `Observe`, `RestartPod`, `Enforce`.
- **`Llm:Pricing` is a dictionary keyed by model id.** An unpriced model is charged at **zero**,
  which switches the cost budget off rather than approximating it.

## Restarts

Most options are read through `IOptionsMonitor` and take effect on the next use. Two groups do
not, because they are captured at construction:

- `Llm:Provider`, `Llm:Model`, `Llm:Endpoint` — the chat client factory captures these. Changing
  them needs a pod restart, not just a ConfigMap edit.
- Anything the Kubernetes watcher stack reads at startup.

## Where to look next

- [Helm values](/reference/helm-values) — everything the chart exposes, with its own comments
- [Agent options](/reference/agent-options) — every option class, including ones with no Helm value
- [Reserved env and safety rails](/reference/env-and-rails) — what the chart refuses to render
