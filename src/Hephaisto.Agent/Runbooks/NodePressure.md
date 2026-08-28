# Node pressure (memory / disk)

The kubelet has set a pressure condition on a node. Under MemoryPressure it begins evicting
pods; under DiskPressure it garbage-collects images and may evict.

## Escalate the scope immediately

This is a **node-level** event. Pods failing on this node are consequences, not causes, and
the correlation logic should be absorbing their signals into this one rather than opening a
dozen incidents. If it has not, say so — that is a bug worth reporting.

Note also that the stability gate deliberately blocks pod-level actions once the
cluster-wide unhealthy fraction is high: restarting one evicted pod while a node is out of
memory is treating a symptom on the wrong object.

## First moves

```promql
kube_node_status_condition{condition="MemoryPressure", status="true"}
node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes
sum by (pod) (container_memory_working_set_bytes{node="<node>"})
```
`get_node` for conditions, allocatable versus requests, and taints.

## Finding the culprit

Sort pods by working-set memory on that node. Look specifically for a pod with **no memory
limit** — one unbounded workload is the usual cause of an entire node going under, and it is
exactly what chaos fixture C9 (`memhog`) reproduces.

## Usual correct action

`cordon_node` and `drain_node` require approval — drain needs a second approver, and on a
single-node cluster draining is equivalent to an outage. Say that plainly if a drain looks
tempting. The useful output here is usually a precise diagnosis: this pod, this much memory,
no limit set.
