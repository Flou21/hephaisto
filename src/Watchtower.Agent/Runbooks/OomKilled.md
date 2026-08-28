# OOMKilled

The kernel killed the container for exceeding its memory limit. `exitCode: 137`,
`reason: OOMKilled`.

## The trap

**There are usually no logs.** The process was killed by the kernel without warning, so it
had no chance to write anything. An absent log is not a broken tool — here, absence is
itself the evidence, and it is consistent with OOM rather than a crash.

## First moves

1. `describe_pod` → confirm `lastState.terminated.reason == OOMKilled` and read the limit.
2. PromQL, the memory shape over the last hour:
   ```promql
   container_memory_working_set_bytes{namespace="<ns>", pod=~"<pod>.*"}
   ```
   ```promql
   kube_pod_container_resource_limits{namespace="<ns>", resource="memory"}
   ```
3. `kube_pod_container_status_restarts_total` for the restart cadence.

## Reading the shape — this is the whole diagnosis

- **Sawtooth, rising steadily to the limit then dropping to zero, repeatedly** → a memory
  leak, or a limit genuinely set too low. Distinguish by time-to-OOM: minutes suggests an
  undersized limit, many hours suggests a leak.
- **Flat, then a sudden vertical spike** → one oversized request or payload. Look for a
  correlated latency or error spike, and for what arrived just before it.
- **Stepped increases aligned with traffic** → correct behaviour under an incorrect limit.

## Usual correct action

Raising the limit is a `patch_resources`, which is **High risk and always requires
approval** — it consumes real node capacity and can push other workloads into eviction.
Do not propose a restart: it clears the symptom for exactly as long as it takes to fill the
memory again, and teaches the oscillation detector to quarantine you.
