# CrashLoopBackOff

The container starts, exits non-zero, and the kubelet backs off exponentially before
retrying. The restart count is the symptom. It is never the cause.

## First moves, in this order

1. **`get_pod_logs(previous: true)`** — always, and always first. The current container is
   either not running or has only just started; the logs that explain the failure belong to
   the *previous* instance. Skipping this is the single most common wasted investigation.
2. `describe_pod` → read `lastState.terminated`: `exitCode`, `reason`, `finishedAt`.
3. `get_events` on the namespace, filtered to this pod.

## Discriminating between causes

| Evidence | Cause | Note |
|---|---|---|
| `exitCode: 137` + `reason: OOMKilled` | Memory limit, **not** a crash | Switch to the OomKilled runbook. Very common misdiagnosis. |
| `exitCode: 1` + a `FATAL`/`panic` log line | Application-level failure | Read the line. It usually names the dependency. |
| `exitCode: 0` repeatedly | Process is completing, not failing | Wrong workload type — this should probably be a Job. |
| Logs mention connection refused / timeout to another service | Dependency, not this workload | Check the dependency's Endpoints before blaming this pod. |
| No logs at all, ever | Container never reached the entrypoint | Image, command or config error — check `describe_pod` for the container state reason. |
| Restarts began at a specific time | Correlate with a rollout | `get_rollout_history`; a deploy minutes earlier is usually the answer. |

## Evidence worth capturing

The `FATAL`/`panic` line verbatim, the exit code, the restart count with its time range, and
the rollout revision plus age if a deploy is nearby.

## Usual correct action

A restart fixes almost nothing here — the container will crash again on the same cause, and
restarting it is how you end up quarantined by the oscillation detector. If the crash began
right after a rollout and the previous revision was healthy, **rollback** is the right move.
Otherwise this is a code or config problem and belongs with a human.
