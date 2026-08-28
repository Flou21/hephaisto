# PVC nearly full

A PersistentVolumeClaim is above 85% or 90% used.

## First moves

```promql
kubelet_volume_stats_used_bytes / kubelet_volume_stats_capacity_bytes
```
Then take a **derivative**, because the fill rate is the whole story:
```promql
predict_linear(kubelet_volume_stats_available_bytes{persistentvolumeclaim="<pvc>"}[6h], 24*3600)
```
`list_pvcs` for capacity and StorageClass, and `who_owns` to find the consuming workload.

## What actually matters

Not "how full", but **when it hits zero**. A PVC at 91% that has been at 90% for a month is
not an incident. One at 86% climbing steeply will page someone tonight. Lead with the
projection and state the time to exhaustion explicitly.

Also check what is filling it: unrotated logs and stale checkpoints are common and often
mean the workload is misconfigured rather than genuinely out of space.

## The hard constraint on this cluster

The only StorageClass is `local-path`, which **does not support volume expansion**. So the
usual remedy — grow the PVC — is unavailable here. The realistic options are freeing data
inside the volume or provisioning a larger one and migrating, both of which are human work.

## Usual correct action

`delete_pvc` is **permanently denied and not approvable**, for the obvious reason: it is
unrecoverable data loss and no confidence level justifies an agent doing it. Diagnose, give
the projection, escalate.
