# See it without a cluster

Trying this properly needs Kubernetes, Prometheus, Alertmanager, prometheus-operator, Postgres
with pgvector and a model key. That is a reasonable production dependency list and an
unreasonable evaluation one.

There are two ways around it, and neither needs an account.

## The hosted demo

<https://demo.hephaisto.dev> renders ten investigations the agent actually ran, as static pages.
Nothing to install, nothing to run, no key.

## The local stack

Two containers, no API key, no Kubernetes, nothing fetched at runtime:

```sh
curl -fsSL https://raw.githubusercontent.com/Flou21/hephaisto/main/demo/compose.yaml \
  | docker compose -f - up
# then open http://localhost:8080
```

You get the real published image, the real schema and the real console, loaded with **ten
investigations the agent actually ran** against a k3s cluster full of seeded faults — the step
trace, the diagnosis, and every evidence excerpt linking back to the untruncated tool output it
came from.

Each incident's timeline says, in its first entry, which fixture it was replayed from, which model
investigated it and how it was graded. Nine of the ten were graded correct; the tenth is in there
too, labelled, because a demo showing only the ones that worked would be a different claim from
the one this project publishes.

## Why this had to be built rather than recorded

The obvious approach does not work, and the reason is worth stating because it rules it out
cleanly. A **cassette** in this repo records the *tools*, not the model — deliberately, since the
model is the thing under test. A cassette that also pinned the model's replies would assert only
that JSON round-trips. So replaying one is a live, paid, non-deterministic model run, and no
key-free demo could be built from the corpus as it stood.

What was missing was the *output* half: the steps, findings, evidence and plan that the evaluation
harness computed and then threw away after scoring. Recording those as committed **transcripts**
made every demo option key-free at once, including the console itself.

## What it cannot show you

The agent is connected to nothing, so it detects nothing and the executor refuses every action. It
is the product's output, not the product running.

Two consequences worth naming rather than discovering:

- **No transcript shows the agent acting.** Every one of the ten either correctly declined to act
  or produced no plan. That is a real property of the model the corpus was replayed against, not a
  limitation of the viewer — see [What it is](/guide/what-it-is#the-row-to-read-carefully).
- `Kubernetes:Enabled=false` is what makes booting without a kubeconfig possible, and it is **not
  a setting to put on a deployed agent**. One that watches nothing while reporting itself healthy
  is the worst failure mode this project has, so it says so at warning level on every start.
