# Unschedulable (Pending)

The scheduler cannot place the pod. It is `Pending` with no node assigned.

## The critical point

**The reason exists only in Kubernetes Events.** No metric tells you *why* the scheduler
refused — `kube_pod_status_unschedulable` only tells you *that* it did. This is precisely
why the OTel Collector's `k8s_events` receiver is part of the MVP.

Query it in Loki:
```logql
{service_name="k8s-events"} |= "FailedScheduling" |= "<pod-name>"
```

## Reading `FailedScheduling`

The message enumerates every node and why each was rejected. Read it literally:

| Message | Cause |
|---|---|
| `insufficient memory` / `insufficient cpu` | Requests exceed remaining allocatable capacity. |
| `node(s) had untolerated taint` | Missing toleration, or the node was cordoned. |
| `node(s) didn't match Pod's node affinity/selector` | Affinity rules cannot be satisfied. |
| `pod has unbound immediate PersistentVolumeClaims` | PVC problem, not a scheduling problem. Follow the PVC. |
| `node(s) had volume node affinity conflict` | The volume is pinned to a node that cannot take the pod. |

On this single-node cluster, `insufficient memory` usually means the request is simply
larger than the node — compare against `kube_node_status_allocatable`.

## Usual correct action

Scaling down a competing workload, or lowering the request, both need approval. On a
one-node cluster, an impossible request is a human fix. Name the exact shortfall — "requests
500Gi, node allocatable is 115Gi" is a far more useful escalation than "unschedulable".
