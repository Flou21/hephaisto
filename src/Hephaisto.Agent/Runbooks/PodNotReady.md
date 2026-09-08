# Pod not ready

A pod has been `Pending` or `Unknown` for long enough that it is not a normal start-up. This
is a **deliberately generic** signal: it says a pod is not running and does not claim to know
why.

**The instinct to distrust: this is not a flap.** A flapping pod goes Ready and NotReady
repeatedly; this one is *stuck*, and the two want opposite responses. If you find the pod
oscillating rather than stuck, this is the wrong runbook — see `ReadinessFlapping`.

## First moves

The whole job here is to turn this into a more specific finding. Do that before anything
else.

1. **`get_events` on the namespace, first.** For a `Pending` pod with no container statuses
   at all, the reason exists **only** as a Kubernetes Event — no metric carries it. That is
   the single fact most worth knowing about this alert.

2. Read the pod's phase and its container statuses together:
   - `Pending`, no container statuses, `FailedScheduling` event → **Unschedulable.**
     Insufficient CPU or memory, a node selector or taint nothing tolerates, or an unbound
     PVC.
   - `Pending`, container waiting with `ImagePullBackOff` / `ErrImagePull` → **the image.**
     The event message is the entire diagnosis.
   - `Pending`, container waiting with `CreateContainerConfigError` → **a missing ConfigMap
     or Secret key.** The pod never starts, so there will never be logs. Do not look for
     them.
   - `Unknown` → usually the **node**, not the pod. Check whether the node is Ready and
     whether its other pods are affected.

3. Only once you know which of those it is, follow that kind's reasoning.

## Why this signal exists separately

`ReadinessFlapping` used to carry both meanings, and an incident opened by this alert was
handed the flap runbook — which says, correctly for a flap and wrongly here, that restarting
will not help and the probe is the suspect. It was accidentally harmless and still wrong
information. Being honestly generic beats being confidently mislabelled.

An incident that opens on this kind will be **re-labelled automatically** if a more specific
signal arrives for the same workload, so a precise conclusion here is what closes the gap
when it does not.

## Usual correct action

Rarely an action. A pod that cannot schedule needs capacity, a pod that cannot pull needs a
real image tag, and a pod that cannot find a Secret key needs that key — none of which this
build creates.

Diagnose to the specific cause and escalate with it. "Pending because the scheduler found no
node with 500Gi of memory" is a complete answer; "the pod is not ready" is not.
