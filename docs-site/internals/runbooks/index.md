# Incident reference

The agent ships eleven runbooks, one per member of `SignalKind`, plus a fallback. An alert selects
one through its [`hephaisto_kind` label](/operate/alerting).

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
| [`_Default`](/internals/runbooks/_default) | The fallback. Reason about the controller, not the ephemeral pod. |

## Why there is a false-positive test in the set

`ReadinessFlapping` exists to catch an agent that reaches for a restart because a restart is the
tool it has. A flapping readiness probe is usually a dependency problem or a probe tuned too
tightly, and restarting the pod makes it worse while looking decisive.

An agent that scores well on ten scenarios but restarts this one has not learned to diagnose. It
has learned to act.
