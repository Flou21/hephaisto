# ImagePullBackOff / ErrImagePull

The kubelet cannot fetch the image. The pod has never run, so **there are no logs and never
will be** — do not spend steps looking for them.

## First moves

1. `get_events` — the event message is the entire diagnosis here and names the exact cause.
2. `describe_pod` → the container's `state.waiting.message`.

## Reading the event message

| Message contains | Cause | Action |
|---|---|---|
| `not found`, `manifest unknown` | Bad tag or bad repository | A typo or an image that was never pushed. Human. |
| `unauthorized`, `authentication required` | Missing or wrong `imagePullSecrets` | Human. Watchtower has no access to secrets by design. |
| `no such host`, `i/o timeout` | Registry unreachable | Infrastructure, not this workload. Check whether other pulls are failing too. |
| `toomanyrequests` | Registry rate limit | Transient. Likely resolves itself; say so rather than acting. |

## Discriminating from ConfigError

Both leave the pod stuck in `Waiting` with no logs, and they are easy to confuse.
The container state reason is the discriminator: `ImagePullBackOff`/`ErrImagePull` is the
image, `CreateContainerConfigError` is a missing ConfigMap or Secret reference. Read the
reason field — do not infer it from the message.

## Usual correct action

None available to the agent. Every cause is a human fix (push the image, fix the tag, add
the pull secret). Diagnose precisely, name the exact missing reference, escalate. A precise
escalation is a good outcome here, not a failure.
