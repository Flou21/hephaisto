# Replica mismatch

A Deployment has fewer available replicas than it wants, for long enough that it is not a
rolling update in progress. The mismatch is the *symptom*; every useful answer is about
**why the missing pods cannot become Ready**.

**The instinct to distrust: do not restart the pods that are working.** They are not the
problem. Restarting a healthy replica in a workload that is already short of capacity makes
the outage worse, and it is the single most tempting wrong move here.

## First moves

1. Get the numbers apart. "Desired" and "available" differing tells you nothing on its own —
   what matters is which stage the missing pods are stuck at:
   ```promql
   kube_deployment_spec_replicas{namespace="<ns>", deployment="<name>"}
   kube_deployment_status_replicas_available{namespace="<ns>", deployment="<name>"}
   kube_deployment_status_replicas_updated{namespace="<ns>", deployment="<name>"}
   ```
   `updated` well below `desired` means the rollout itself is stalled, which points at the
   new revision.

2. **Look at the pods that are not Ready, individually.** `get_workload` then `get_events`
   on the namespace. The reason is almost always in Events, and almost never in a metric.

3. Classify what you find, because each has its own runbook and its own action:
   - `Pending` with no container statuses → scheduling. The reason exists **only** as a
     `FailedScheduling` Event. Insufficient CPU/memory, a node selector nothing matches, an
     unbound PVC.
   - `ImagePullBackOff` / `ErrImagePull` → the new revision's image tag.
   - `CrashLoopBackOff` → read `get_pod_logs(previous: true)`, always.
   - Running but never Ready → a readiness probe or a dependency it is waiting on.

## The question worth asking early

**Did this start at a rollout?** Call `get_rollout_history`. A mismatch that begins within
minutes of a new revision, on a Deployment whose previous revision was healthy, is a bad
deploy — and that is a different and much more actionable finding than "three replicas are
missing".

If the pods of the *new* ReplicaSet are the ones failing while the old ones are fine, say
that explicitly. It is the strongest evidence there is.

## Usual correct action

If it began at a rollout and the previous revision was healthy for a while,
`rollback_deployment` is the strongest candidate — name the revision and put the rollout
time beside the onset time.

If the pods cannot schedule, no action in this build creates capacity. Diagnose it exactly —
which resource, how much short, on which nodes — and escalate. `scale_workload` **down** to
fit is occasionally right and is a decision for a human, not a default.
