# Readiness flapping

The pod alternates ready and not-ready. Endpoints churn, `up` flaps, the Service
intermittently has no backends.

## This is the false-positive test

The instinct is to treat flapping as "down" and restart. That is wrong, and it is the
failure mode this runbook exists to prevent. The workload is **running** — it is
intermittently failing a probe. Restarting resets the symptom and destroys the evidence.

## First moves

1. Readiness over time, and specifically its *shape*:
   ```promql
   kube_pod_status_ready{namespace="<ns>", condition="true"}
   ```
2. `get_service_endpoints` — an empty or oscillating Endpoints list is the user-visible
   impact, and is a top-5 root cause of "the service is down" reports.
3. Logs **without** `previous` — the container has not restarted, so current logs are the
   relevant ones. Look for slow dependency calls near probe failures.
4. `describe_pod` → the probe's `timeoutSeconds`, `periodSeconds` and `failureThreshold`.

## Common causes

- **Probe timeout shorter than a real dependency call.** The classic. The app is healthy but
  slow, and the probe is impatient.
- **GC pauses or CPU throttling** near the limit. Check
  `container_cpu_cfs_throttled_seconds_total` — throttling correlating with probe failures
  is conclusive.
- **A dependency that is itself flapping**, one layer down. Follow it before concluding.
- Genuine intermittent failure under load only.

## Usual correct action

Almost never a restart. The fix is usually a probe configuration change or a dependency fix,
both human decisions. If restarts are climbing *as well*, re-triage: that is a different
incident and this runbook is the wrong one.
