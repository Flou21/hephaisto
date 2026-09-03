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

**`--full` is the DIAGNOSIS gate**: `c1,c2,c3,c4,c5,c7,c8,c10,c11,c12,c13` — every fixture that
can run on this hardware, which is eleven of the thirteen and the denominator the MVP bar was
always written against. c6 and c9 stay out and no flag overrides that; neither is a scheduling
choice.

**It is not the acting gate, and it cannot be.** `--full` applies its fixtures *simultaneously*,
and on a single node that many broken workloads is over `policy.clusterUnhealthyCeiling` — so the
policy engine correctly refuses every action as a cluster-wide event, and the act assertion can
never pass in a `--full` run. The harness now says so rather than failing, but the consequence for
the procedure is that **a release needs two runs**:

```sh
scripts/e2e/run.sh --tag <version> --full                       # diagnosis, ~2-4 h
scripts/e2e/run.sh --tag <version> --fixtures c13 --mode Auto   # acting, ~25 min
```

See backlog #97. Measured on v0.6.0: the wide run scored 8/8 correct and could not act; the
focused run passed 70 assertions in 24m37s, executing a `RestartPod` and closing the incident.

Budget **about two hours**. c8 alone cannot open an incident sooner than thirty minutes, because
its rule needs `changes(...)[30m] >= 4` — thirty minutes of evidence before the expression can
be true at all — and the incident deadline is raised to match rather than timing out a fixture
that is exactly on schedule. The four-fixture default stays the thing to run while working: a
two-hour gate nobody runs is worth less than a five-minute one everybody does.

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

### Running against a cheaper provider

Any OpenAI-compatible server — DeepSeek, OpenRouter, or a local Ollama or LM Studio — with four
environment variables. The key is validated against `/v1/models` before the run starts, for the
same reason the Gemini key is: a 401 discovered on the fourth investigation of a twelve-minute
run looks like a bug in the agent.

```sh
HEPHAISTO_LLM_PROVIDER=openai \
HEPHAISTO_LLM_ENDPOINT=https://api.deepseek.com/v1 \
HEPHAISTO_LLM_MODEL=deepseek-v4-flash \
HEPHAISTO_LLM_PLANNING_FORMAT=JsonObject \
HEPHAISTO_LLM_API_KEY=... \
    scripts/e2e/run.sh --mode Auto
```

`HEPHAISTO_LLM_PLANNING_FORMAT=JsonObject` is required for DeepSeek and wrong for most others:
it is a provider *capability*, not a preference, and it is the weaker of the two modes. Without
it DeepSeek answers `400` to every planning call, and because phase 1 is unaffected the run
reports correct diagnoses and no plans — which looks like an agent declining to act. A local
Ollama server needs no such setting: llama.cpp constrains generation with a grammar, so strict
schemas work there even on small models.

**The key is not put on the command line.** It is read from the environment, or from
`LLM_API_KEY` in `secrets/hephaisto-llm.secret.yaml`, which is gitignored twice over.

### Running against a local model, which is free

`gpt-oss-120b` on Ollama matches the hosted frontier model on the scenarios the corpus can carry
and costs nothing per token, which is what makes a two-hour ten-fixture gate affordable to run
before every release. No key is involved:

```sh
HEPHAISTO_LLM_PROVIDER=openai \
HEPHAISTO_LLM_ENDPOINT=http://100.91.41.104:11434/v1 \
HEPHAISTO_LLM_MODEL=gpt-oss:120b \
    scripts/e2e/run.sh --nightly --full
```

Note `--full` without `--mode Auto`: the two do not combine, for the reason above. Run the acting
half separately with `--fixtures c13 --mode Auto`.

Two things have to be true, and both were false on a fresh install:

**The endpoint must be an address the CLUSTER can reach.** `localhost` is the mistake, and it is
not an obvious one: from a pod, `127.0.0.1` is the pod. The agent runs in kind, kind runs in
Rancher Desktop's Lima VM, and the model runs on macOS outside all of it. Measured from inside
the VM, all of `192.168.2.77` (LAN), `100.91.41.104` (tailnet), `host.docker.internal` and
`192.168.5.2` (the Lima gateway) reach the host. **Prefer the tailnet address**: it does not
move with DHCP or with docker's bridge topology, and it is the same address the rest of this
machine's tooling already uses. The harness verifies this from a pod during `deps` and fails
there rather than forty minutes later.

**Ollama must be listening off loopback.** It ships bound to `127.0.0.1` only. The macOS app has
an *expose to the network* setting; without it the host probe passes and no pod can connect.
Check with `lsof -nP -iTCP:11434 -sTCP:LISTEN` — it should say `*:11434`, not `127.0.0.1:11434`.

No `HEPHAISTO_LLM_PLANNING_FORMAT` is needed: llama.cpp constrains generation with a grammar, so
the strict schema mode works locally even where a hosted DeepSeek needs the weakened one. The
step ceiling is raised to 20 automatically for any openai-compatible provider — see backlog #59
for what happens when it is not — and `HEPHAISTO_LLM_MAX_STEPS` overrides it either way.

**A local model does not make the image local.** The harness installs the *published* artifact
from GHCR by design, so `--nightly` still pushes the branch and builds it in Actions. What is
local, and free, is the cluster and the model.
