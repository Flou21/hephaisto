# What the agent is told

The system prompt is four fragments, concatenated in numeric order. They are transcluded below
from `src/Hephaisto.Agent/Prompts/` — this page is the real thing, not a summary of it.

Publishing them is deliberate. An agent with cluster credentials that will not show you its
instructions is asking for a kind of trust it has not earned.

::: info These are the shipped prompts, not the whole input
At runtime the model also receives an **environment card** (your cluster name, in-scope and
protected namespaces, workload owners, and any notes you configure) and the
[runbook](/internals/runbooks/) selected by the alert's `hephaisto_kind`.
:::

## `00-role.md` — who the agent is

<div class="hp-source">

Transcluded from <code>src/Hephaisto.Agent/Prompts/00-role.md</code>

</div>

<!--@include: ../../src/Hephaisto.Agent/Prompts/00-role.md-->

## `10-tool-contract.md` — the prompt-injection defence

<div class="hp-source">

Transcluded from <code>src/Hephaisto.Agent/Prompts/10-tool-contract.md</code>

</div>

This is the fragment worth reading if you only read one. It is the direct answer to the first
question anybody sensible asks about an agent that reads logs from workloads it does not trust.

Note that it is **not** the security control — it is a hint. The actual control is architectural:
the model never holds a mutating tool handle, the planning phase has no tools at all, and
execution is C# over a closed enum. A prompt injection that defeats this text still has nothing
to reach.

<!--@include: ../../src/Hephaisto.Agent/Prompts/10-tool-contract.md-->

## `20-output-contract.md` — how it concludes

<div class="hp-source">

Transcluded from <code>src/Hephaisto.Agent/Prompts/20-output-contract.md</code>

</div>

<!--@include: ../../src/Hephaisto.Agent/Prompts/20-output-contract.md-->

## `30-planning.md` — phase two, no tools

<div class="hp-source">

Transcluded from <code>src/Hephaisto.Agent/Prompts/30-planning.md</code>

</div>

The planning phase gets **no tools at all** and emits JSON against a fixed schema. Note the
section headed *"The default answer is nothing"* — proposing no action is the expected outcome for
most incidents, and the agent is told so explicitly rather than being left to infer it.

<!--@include: ../../src/Hephaisto.Agent/Prompts/30-planning.md-->

## Three prompt strings you can configure

`Investigation:OpeningMessage`, `Investigation:StallNudge` and
`Investigation:FinalConclusionNudge` are options rather than files. See
[agent options](/reference/agent-options#investigation).

## What changing these costs

Prompt wording has been the subject of four measured experiments in this project, and **all four
were null results**. The most recent removed the line the model was quoting back when it declined
to act; removing it did not change the rate (0/9 → 1/9, p = 1.0).

The conclusion recorded at the time is worth repeating here: the association was in the model, not
only in the wording. If you rewrite these, measure it — the harness for doing so is
[`hephaisto-eval`](/reference/cli).
