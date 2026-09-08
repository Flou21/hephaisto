# Incident reference

The agent ships fifteen runbooks plus a fallback. An alert selects one through its
[`hephaisto_kind` label](/operate/alerting).

`SignalKind` has more members than there are runbooks, and that is deliberate rather than a gap:
three of them (`ObservabilityDegraded`, `BudgetExhausted`, `Watchdog`) describe Hephaisto's own
health rather than a workload's, and `Unknown` is the absence of a classification. Those four fall
through to `_Default`. Every kind that a **shipped alert rule** can produce has a runbook of its
own — which was not true before v0.7.0, when four of them did not.

::: info These are prompts, not operator runbooks
They are written in the second person and addressed to **the model** — they tell it what to
investigate first and which instinct to distrust. They read well enough for a human that they are
worth publishing, but that is not who they were written for.
:::

Each page below is the real file, transcluded. If a runbook changes, this changes.

| Kind | What it tells the model |
|---|---|
| [`CrashLoopBackOff`](/internals/runbooks/CrashLoopBackOff) | The restart count is the symptom, never the cause. `previous: true` logs, always and first. |
| [`ImagePullBackOff`](/internals/runbooks/ImagePullBackOff) | The event message is the entire diagnosis. Do not hunt for logs that will never exist. |
| [`ConfigError`](/internals/runbooks/ConfigError) | A referenced ConfigMap or Secret key is missing. The pod never starts, so there are no logs. |
| [`OomKilled`](/internals/runbooks/OomKilled) | `exitCode: 137`. The kernel killed it without warning, so usually no logs. |
| [`Unschedulable`](/internals/runbooks/Unschedulable) | The reason exists **only** in Kubernetes Events. No metric tells you why. |
| [`ReadinessFlapping`](/internals/runbooks/ReadinessFlapping) | The false-positive test. The instinct to restart a flapping pod is wrong. |
| [`NodePressure`](/internals/runbooks/NodePressure) | Escalate the scope immediately — pods failing on that node are consequences. |
| [`PvcNearlyFull`](/internals/runbooks/PvcNearlyFull) | A volume above 85/90%, with the PromQL inline. |
| [`JobFailed`](/internals/runbooks/JobFailed) | A Job exhausted its `backoffLimit`. |
| [`HighErrorRate`](/internals/runbooks/HighErrorRate) | Span metrics. There may be no Kubernetes symptom at all. |
| [`HighLatency`](/internals/runbooks/HighLatency) | A burn-rate alert. The service is working and too slow — do not go looking for an outage. |
| [`TargetDown`](/internals/runbooks/TargetDown) | "Target down" is about the scrape, not the service. Three situations produce it and one is an outage. |
| [`ReplicaMismatch`](/internals/runbooks/ReplicaMismatch) | Do not restart the replicas that work. Ask why the missing ones cannot become Ready. |
| [`RestartStorm`](/internals/runbooks/RestartStorm) | Which of four killers is doing it. `previous: true` logs before anything else. |
| [`PodNotReady`](/internals/runbooks/PodNotReady) | Deliberately generic: a stuck pod, not a flapping one. Turn it into something specific. |
| [`_Default`](/internals/runbooks/_default) | The fallback. Reason about the controller, not the ephemeral pod. |

## Why there is a false-positive test in the set

`ReadinessFlapping` exists to catch an agent that reaches for a restart because a restart is the
tool it has. A flapping readiness probe is usually a dependency problem or a probe tuned too
tightly, and restarting the pod makes it worse while looking decisive.

An agent that scores well on ten scenarios but restarts this one has not learned to diagnose. It
has learned to act.

## Why `PodNotReady` and `ReadinessFlapping` are separate

They were one kind until v0.7.0, and the `KubePodNotReady` rule carried the flapping label. So an
incident on a pod that was *stuck* — unschedulable, or waiting on an image that does not exist —
was handed a runbook whose entire argument is that the fault is **intermittent** and that
restarting will not help.

That was wrong information, and only accidentally harmless: the flap runbook's advice not to
restart happens to be right for a stuck pod too, for completely different reasons.

`PodNotReady` is the honest version. It says a pod is not running and does not claim to know why,
and it ranks at the bottom of the specificity ordering — so an incident that opens on it is
re-labelled automatically as soon as a signal that knows the mechanism arrives for the same
workload. See backlog #70.
