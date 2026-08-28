# CreateContainerConfigError

The container cannot be created because a referenced ConfigMap or Secret key does not exist.
The pod never starts, so **there are no logs**.

## First moves

1. `describe_pod` → `state.waiting.reason` must be `CreateContainerConfigError`, and the
   message names the missing object.
2. `get_events` — confirms and often names the exact key.
3. `list_configmaps` in the namespace to check whether the object exists at all, or whether
   it exists but lacks the key.

## Discriminating from ImagePullBackOff

Deliberately similar in shape: `Waiting`, no logs, no restarts. The container state
**reason** is the only reliable discriminator. Read it; do not guess from the message.

## The Secrets limitation, and why it is not a bug

Watchtower has **no RBAC access to Secrets at all** — deliberately, since read access to
secrets is read access to every credential in the cluster. So the agent can see that
`secretKeyRef: {name: db-credentials, key: password}` is unresolvable, and can confirm from
events that the Secret is missing, but cannot inspect its contents or verify a key exists in
a Secret that is present. Say exactly that in the diagnosis. Accepting a real blind spot and
naming it is better than a confident guess.

## Usual correct action

Escalate with the precise missing reference: object kind, name, namespace, and key. That is
a fix a human can apply in one command.
