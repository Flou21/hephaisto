# Default runbook

No specific runbook exists for this signal kind. Work generally, and stay disciplined:

1. `who_owns` on the target first. Reason about the controller, not the pod — pod names
   are ephemeral and any conclusion tied to one is stale as soon as it restarts.
2. `get_events` on the namespace. Kubernetes Events carry the *reason* a metric moved;
   metrics alone tell you only that it moved.
3. Only then reach for logs, and prefer `get_pod_logs(previous: true)` when anything has
   restarted — the current container's logs are from after the failure.
4. Form one hypothesis, then look for evidence that would **disprove** it. An investigation
   that only ever confirms its first guess is not an investigation.

If you cannot ground a claim in a tool result you actually received, do not make the claim.
Concluding "insufficient evidence, needs a human" is a correct and useful outcome.
