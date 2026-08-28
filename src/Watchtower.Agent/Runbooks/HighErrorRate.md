# High error rate / high latency (span metrics)

Fired from Tempo's metrics-generator, so it applies to **any traced service with no
application changes at all**. There may be no Kubernetes symptom whatsoever — the pods are
Ready and restart counts are flat. That is normal for this alert and not a contradiction.

## First moves

1. Confirm and scope the rate:
   ```promql
   sum by (span_name) (rate(traces_spanmetrics_calls_total{service="<svc>", status_code="STATUS_CODE_ERROR"}[5m]))
   ```
   ```promql
   histogram_quantile(0.95, sum by (le, span_name) (rate(traces_spanmetrics_latency_bucket{service="<svc>"}[5m])))
   ```
2. **Get an actual failing trace** — this is the step that makes this runbook worth reading:
   ```traceql
   {resource.service.name="<svc>" && status=error}
   ```
3. From the trace, read which span failed and how deep it sits. Then fetch logs for that
   span's `trace_id` — Watchtower's own logs and any OTLP-shipped logs carry it as
   structured metadata:
   ```logql
   {service_name="<svc>"} | trace_id="<id>"
   ```

## Reading the trace

- **The error originates in a leaf span** → the failure is in that dependency, not this
  service. Follow it down.
- **Latency concentrated in one child span** → that dependency is slow; this service is a
  victim. Check the service graph for who else it affects.
- **Errors spread evenly across span names** → something systemic: a resource limit,
  saturation, or a config change.
- **Onset time is sharp** → correlate with `get_rollout_history`. A sharp edge is a deploy
  until proven otherwise.

## Usual correct action

If it began right after a rollout and the previous revision was healthy for a while,
`rollback_deployment` is the strongest candidate. Otherwise diagnose precisely and escalate:
naming the specific failing span and its dependency is far more valuable than restarting
something.
