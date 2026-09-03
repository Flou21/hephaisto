# High latency (span metrics, burn rate)

Fired from Tempo's metrics-generator, so it applies to **any traced service with no
application changes at all**. There is usually no Kubernetes symptom whatsoever — the pods
are Ready, restart counts are flat, and nothing is crashing. That is normal for this alert
and is not a contradiction to be explained away.

**The instinct to distrust: this is not an outage, so do not look for one.** A burn-rate
alert fires on a service that is working and is too slow. If you spend the investigation
looking for a dead pod you will find nothing and conclude nothing.

Note which alert fired, because they ask different questions:

- `ServiceLatencyBudgetBurnFast` — 14.4x burn over 1h, still burning in the last 5m. Real,
  sustained, happening now.
- `ServiceLatencyBudgetBurnSlow` — 6x over 6h. Slow drift. The answer is usually *what
  changed*, not *what is broken*.
- `ServiceLatencyExtreme` — over half of requests past 1s. Look for a blocked dependency or
  a saturated resource, not a code regression.

## First moves

1. Find **where** the time goes. A service-level number is not actionable; a span name is:
   ```promql
   histogram_quantile(0.95, sum by (le, span_name) (rate(traces_spanmetrics_latency_bucket{service="<svc>", k8s_namespace_name="<ns>"}[5m])))
   ```
   One span name far above the others is the whole diagnosis. Latency spread evenly across
   every span name is a different finding entirely — that is saturation or a noisy
   neighbour, not a slow code path.

2. **Get an actual slow trace.** This is the step that makes this runbook worth reading, and
   the latency histogram carries exemplars precisely so you do not have to guess:
   ```traceql
   {resource.service.name="<svc>" && duration > 1s}
   ```

3. Read the trace top-down and find the span that *owns* the time — the one whose own
   duration is large, not one that is merely long because its children are.

## Reading the trace

- **Time concentrated in one child span** → that dependency is slow and this service is a
  victim. Say so, name the dependency, and check the service graph
  (`traces_service_graph_request_*`) for who else it affects. Restarting the victim is
  worse than doing nothing, because it looks decisive.
- **Time in this service's own span, evenly across requests** → saturation. Check CPU
  throttling and memory before concluding it is the code:
  ```promql
  rate(container_cpu_cfs_throttled_seconds_total{namespace="<ns>"}[5m])
  ```
- **Time in this service's own span, only on some requests** → a slow path taken by some
  inputs: a cache miss, an N+1 query, a retry with a backoff.
- **Onset is a sharp edge rather than a ramp** → correlate with `get_rollout_history`. A
  sharp edge is a deploy until proven otherwise. A ramp is growth, a leak, or a dependency
  degrading.

## Usual correct action

If it began at a rollout and the previous revision was healthy for a while,
`rollback_deployment` is the strongest candidate — and say which revision, with the time of
the rollout beside the time of the onset, so the claim can be checked.

Otherwise **diagnose precisely and escalate.** Naming the specific slow span and the
dependency behind it is far more valuable than restarting something. There is no action in
this build that makes a service faster, and pretending otherwise wastes the one thing an
incident is short of.
