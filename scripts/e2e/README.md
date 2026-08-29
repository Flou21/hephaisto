# The end-to-end release harness

One command that takes a published build, stands it up next to a real observability stack on a
throwaway cluster, breaks things on purpose, and reports what happened.

```sh
scripts/e2e/run.sh                    # dispatch a nightly and test it
scripts/e2e/run.sh --rc               # cut a real release candidate and test it (prompts)
scripts/e2e/run.sh --tag 0.0.1-rc2    # test something already published
```

## Why it exists

`ci.yml`'s `e2e-kind` job states its own limits in a comment:

> - kind's default CNI ACCEPTS NetworkPolicy objects and does not enforce them.
> - No Prometheus here, so the `release:` selector is only covered by the render-time grep.
> - **No real LLM key, so nothing exercises an investigation end to end.**

Nothing anywhere proved that a *published* artifact, installed from GHCR beside a real
Prometheus, Loki, Tempo and collector, detects a real fault and investigates it. This does.

It closes the second and third of those limits. **The first is still open**, and the summary
says so at the end of every run rather than letting a green tick imply otherwise.

## What one run does

| | |
|---|---|
| 1-2 | get a build (dispatch `nightly.yml`, or cut an rc) and wait until it is genuinely pullable |
| 3 | create a single-node kind cluster on a pinned Kubernetes |
| 4 | install kube-prometheus-stack, Loki, Tempo, the OTel collector and grafana-mcp |
| 5 | `helm install` the published chart from `oci://ghcr.io/flou21/charts` |
| 6 | apply chaos fixtures `c2 c3 c4 c7` simultaneously |
| 7 | assert detection, investigation, budget arithmetic, RBAC, zero mutation; grade the diagnoses |
| 8 | delete the cluster |
| 9 | print a summary and write `report.md` |

Roughly 25 minutes, and under a dollar of Gemini spend.

## Two things that are structural rather than remembered

**It cannot reach production.** `~/.kube/config` on this machine holds seventeen contexts,
several of them production clusters. The harness never reads it: `kind` writes to a dedicated
kubeconfig and only that is exported. A `kc()` wrapper additionally refuses any context that is
not `kind-hephaisto-e2e`. The context guard alone would not be enough - a stray `--context`
defeats it - which is why the file is the primary mechanism and the guard is the backstop.

**Teardown is an `EXIT` trap, not a final step.** It runs on success, on a failed assertion, on
a `set -e` abort and on Ctrl-C alike, deleting the cluster and restoring the system-wide inotify
limit. A teardown that only works on the happy path leaves both behind exactly when something
went wrong.

## The fixtures, and the ones left out

Default is `c2,c3,c4,c7` - chosen to discriminate rather than to cover, because
`infra/chaos/README.md` records a known-correct answer for each.

- **c4 + c7** are the pair that matters. ImagePullBackOff and CreateContainerConfigError look
  nearly identical in Kubernetes and have entirely different causes; the README calls giving
  both the same diagnosis a failure. It is the one assertion an agent cannot pass by
  pattern-matching the symptom.
- **c2** carries a decisive log line, so it tests that Loki is genuinely reached.
- **c3** has its cause in an Event and in *no metric at all*, so it tests the OTel `k8s_events`
  receiver specifically.

Excluded by default: **c9 is node-wide** and would evict Prometheus and the agent itself (the
harness refuses it even if asked); c6 does not fire on `local-path`; c1's OOM event is
unreliable on containerd; c8 needs 30 minutes; c10 needs a local image build.

## Reusing the real values files

The observability stack is installed from `infra/observability/*.values.yaml` **byte for byte
unmodified**, at the versions the Tiltfile pins. Those files are as much under test as the chart
is. Only two things are overridden on the command line, both genuinely cluster-specific:
`crds.enabled=true` (the file disables them because the dev cluster manages them separately) and
the `cluster` external label.

`local-path-sc.yaml` is what makes that possible: it aliases the StorageClass name k3s uses onto
the provisioner kind runs. Without it every PVC in the stack sits Pending forever.

## Resuming

A twenty-five minute script you cannot resume is unusable while you are debugging the script.

```sh
scripts/e2e/run.sh --tag 0.0.1-rc2 --from validate --keep-cluster
scripts/e2e/run.sh --tag 0.0.1-rc2 --only ui
```

## Requirements

`kind`, `kubectl`, `helm`, `gh`, `docker`, `jq`, `git`, `curl`. Runs on stock macOS bash 3.2 -
no associative arrays, no `brew install bash`.

`HEPHAISTO_GEMINI_API_KEY`, or a real key in `secrets/hephaisto-llm.secret.yaml`. Without one
the run still exercises detection end to end and reports the investigation assertions as
skipped rather than failing them.
