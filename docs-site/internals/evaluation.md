# Evaluation and evidence

Every capability claim this project makes has a number, a denominator, and the name of the
instrument that produced it. This page is where those live.

## The headline

**Root cause correct in 8 of 10** seeded chaos scenarios, on a real cluster.

The project's stated bar — quoted since v0.1.0 — is ≥ 7/10 over ≥ 10 scenarios. It took until
v0.5.0 for that bar to become *evaluable* at all, and the reason is worth knowing: an
investigation that is truncated produces no finding, and **no finding cannot be graded**. Three
earlier full runs scored 7/8, 7/7 and 7/7. The accuracy was never short; the denominator was.

The full end-to-end gate runs ten fixtures in 98 minutes for $0.115 against a local
`gpt-oss:120b`.

## How it is measured

Two instruments, deliberately different.

**The e2e harness** (`scripts/e2e/run.sh`) drives a real kind cluster with real seeded faults,
real Prometheus rules and a real agent. Slow, expensive, and the only thing that can prove the
whole path works.

**Cassette replay** (`hephaisto-eval run`) replays recorded tool traces against a model. Fast and
cheap, and it isolates the model from everything else.

::: warning A cassette records the tools, not the model
Deliberately — the model is the thing under test. The consequence is that replay is **not**
deterministic and **not** free: it is a live, paid model run over a fixed set of tool responses.
:::

## What the two instruments disagreeing turned out to mean

For a while this project published two numbers for how often the planner proposes an action: 4 of
8 in replay, 0 of 4 on a cluster. They disagreed, and neither was quoted as the rate.

Grouping every measurement by **model** dissolved the disagreement:

| Model | Runs | Proposed an action |
|---|---|---|
| `deepseek-v4-flash` | 8 | **4** |
| `gpt-oss:120b` | 18 | **0** |
| `gemini-3.7-flash` | 3 | **0** |

Fisher's exact test: **p = 0.0047**.

The "4 of 8" was the DeepSeek arm in its entirety. The cluster's "0 of 4" was `gpt-oss` — the gate
runs the free local model. **Hold the model fixed and replay and cluster agree exactly.** There was
never an instrument disagreement; it was a per-model property recorded as the agent's.

This is the third time that pattern has appeared in this project, and it is the single most useful
thing on this page for somebody choosing a model.

## What this means for you

If you need the agent to propose remediations, **model choice is the decision that determines
whether it ever does**. A cheap local model can diagnose well — `gpt-oss:120b` is part of how the
8/10 was reached — and still never propose an action.

Check `Llm:PlanningStructuredOutput` too: a provider that cannot constrain output to a JSON schema
produces the same observable behaviour for an entirely different reason. See
[troubleshooting](/operate/troubleshooting#it-diagnoses-correctly-but-never-proposes-an-action).

## Known limits of the measurement

Stated because a number without its caveats is worse than no number.

- **Every cassette in the corpus is stale against the shipped prompts.** The prompts have changed
  since the traces were recorded. `hephaisto-eval inspect` prints a freshness line for exactly
  this reason.
- **The token ceiling eats runs.** Three of twelve runs in each arm of a recent experiment
  terminated with `TokenBudgetExhausted`. The reserved concluding step cannot rescue those,
  because the concluding call resends the conversation — so they produce no finding and look
  identical to a decline in a summary line.
- **Ten fixtures is ten fixtures.** Six of them have never run in an automated gate on every
  change; the full corpus is an opt-in run, not a CI job.

## Four prompt experiments, all null

Prompt wording has been changed and measured four times to try to raise the action rate. All four
were null results. The most recent removed the rule the model was quoting back verbatim when it
declined; removing it did not change the outcome (0/9 → 1/9, **p = 1.0**) and the change was
reverted.

That is the finding that led to the per-model table above: the association is in the model, not
only in the wording.

## Reproducing it

```sh
# Replay the corpus against your own model
hephaisto-eval run --cassettes cassettes --repeats 3 --label mine --out results

# The full end-to-end gate against a kind cluster
scripts/e2e/run.sh --full
```

`--repeats` is not optional if you intend to quote a number. A single run of a
non-deterministic system is an anecdote.

## Where the rest is written down

`docs/backlog.md` in the repository is everything known to be broken, numbered and evidenced,
including the entries that block claims this project would like to make.
`docs/verification.md` is the hand-run acceptance checklist. Both are linked from
[the project record](/project/).
