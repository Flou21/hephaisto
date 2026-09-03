# Scrape target down

A Prometheus target stopped answering. **The alert is about the scrape, not about the
service**, and confusing the two is the mistake this runbook exists to prevent.

**The instinct to distrust: "target down" does not mean "service down".** Three quite
different situations produce this alert and only one of them is an outage:

1. The workload is genuinely gone or failing.
2. The workload is fine and its *metrics endpoint* is broken — a port renamed, a
   ServiceMonitor selector that no longer matches, a container that serves traffic before it
   serves `/metrics`.
3. The scrape path itself is broken — Prometheus cannot reach a whole namespace, or the
   endpoint list is empty because every pod went NotReady at once.

Establish which one before saying anything else.

## First moves

1. **Does the workload exist and is it healthy?** Answer this from Kubernetes, not from
   metrics — metrics are the thing that is missing:
   ```
   who_owns → get_workload → get_events
   ```
   A Deployment with its full complement of Ready pods, plus a down target, is case 2 or 3.
   Say so plainly; that is a different incident with a different owner.

2. **How many targets are down?** One instance of one job is a pod problem. Every target in
   a namespace, or every target of a job, is a scrape-configuration or network problem:
   ```promql
   count by (job, namespace) (up == 0)
   ```

3. **Did the series disappear, or go to zero?** With Kubernetes endpoints discovery a
   NotReady pod is *removed from discovery entirely* — its `up` series vanishes rather than
   going to 0. A vanished series with a healthy pod points at readiness; `up == 0` with a
   Running pod points at the metrics endpoint.

## What distinguishes this from a flap

If the target is appearing and disappearing rather than staying gone, this is the wrong
runbook and the wrong conclusion. `TargetFlapping` covers that case, and the correct
response there is to investigate the readiness probe or a slow dependency — **not** to
restart, which fixes nothing and destroys the evidence.

## Usual correct action

If a pod is genuinely unhealthy, treat it as whatever it actually is — crash loop, OOM,
config error — and use that runbook's reasoning. `restart_pod` is right only when you have
evidence of a stuck process, not merely an absent scrape.

If the workload is healthy and only the scrape is broken, **escalate with the finding**. A
broken ServiceMonitor selector is not something this build can fix, and reporting "the
service is down" when it is serving traffic normally is worse than reporting nothing.
