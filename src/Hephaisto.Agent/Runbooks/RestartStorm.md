# Restart storm

Containers in a workload are restarting repeatedly. The restart count is the **symptom, and
never the cause** — every conclusion that ends at "it keeps restarting" is a non-finding.

**The instinct to distrust: restarting it again.** Whatever is killing the container will
kill it again, and a restart resets the evidence you need. If the answer were a restart, the
kubelet would already have found it — it has tried, repeatedly, which is why you are here.

## The first question: who is doing the killing?

There are only a few killers, and they leave different fingerprints:

| Killer | Fingerprint |
|---|---|
| The kernel, out of memory | `exitCode: 137`, `reason: OOMKilled` on the *last* state |
| A failing liveness probe | Events say `Unhealthy` then `Killed`; the process itself looks fine in logs |
| The process exiting | A non-137 exit code, and logs that end at the same place every time |
| The node | Evictions, node pressure, or every pod on one node restarting together |

Get this from `get_workload` (last terminated state and exit code) and `get_events` before
you read a single log line. It changes which logs are worth reading.

## Then

1. **`get_pod_logs(previous: true)`, always and first.** The current container started after
   the failure; its logs describe a process that has not failed yet. This is the single most
   common wasted step in this investigation.

2. If it is OOM, this is really `OomKilled` — use that reasoning. The limit and the actual
   usage are the finding, not the restart count.

3. If it is a liveness probe, the fix is nearly always the probe or the dependency it
   reaches, not the application. A probe with a timeout shorter than a cold start turns a
   slow boot into an infinite restart loop, and it looks exactly like a crashing app.

4. **Is it one pod or all of them?** All replicas restarting together points at something
   shared — a config change, a dependency, a rollout. One pod on one node points at that
   node.

## Distinguish this from a crash loop

If the container is in `CrashLoopBackOff` the kubelet is already backing off and
`CrashLoopBackOff` is the runbook you want. This alert also fires on a workload that
restarts steadily *without* ever entering backoff, which is a different and easier-to-miss
shape: something is restarting it on purpose.

## Usual correct action

`restart_pod` is almost never right here and is usually actively harmful.

If a rollout is in the frame, `get_rollout_history` and consider `rollback_deployment`.
Otherwise diagnose to a cause — the exit code, the probe, the limit — and escalate. Naming
which of the four killers it is, with the evidence, is a complete and useful answer.
