# `hephaisto-eval`

The evaluation harness. It is a development tool, **excluded from the shipped image** — the agent
itself has no CLI, and its interface is [the HTTP surface](/reference/http-api).

You need it only if you are measuring the agent rather than running it.

```
hephaisto-eval <command> [options]
```

Exit codes: `0` ok, `1` failures, `2` bad arguments or unknown command, `130` cancelled. The first
Ctrl-C is handled, so an interrupted recording still writes what it has.

## `record`

Runs a real investigation against a live cluster and records every tool call as a **cassette**.

```sh
hephaisto-eval record --incident <guid> --fixture c4 \
  --expect "the container is being OOMKilled" \
  --description "CrashLoopBackOff on c4" \
  --out cassettes
```

Needs a database, a cluster and an API key.

::: warning A cassette records the tools, not the model
Deliberately — the model is the thing under test, and a cassette that also pinned the model's
replies would assert only that JSON round-trips. The consequence is that **replaying a cassette is
a live, paid, non-deterministic model run.**

Cassettes are also not committed to this repository: redaction covers tool *arguments*, not tool
*results*, so raw `describe_pod` and `get_pod_logs` output carries env vars, hostnames and log
contents verbatim.
:::

## `run`

Replays the corpus and scores it. Needs only a model — no cluster, no database.

```sh
hephaisto-eval run --cassettes cassettes --repeats 3 --label baseline --out results
hephaisto-eval run --cassettes cassettes --transcripts src/Hephaisto.Agent/Demo/transcripts
```

| Option | Default | What it does |
|---|---|---|
| `--cassettes <dir>` | — | Or pass cassette paths positionally |
| `--repeats <n>` | `1` | Repeat each scenario; the only way to see variance |
| `--label <name>` | `unlabelled` | Names the arm in the results file |
| `--no-judge` | off | Skip the model-graded verdict |
| `--out <dir>` | `results` | Where scores are written |
| `--transcripts <dir>` | — | Also write the step trace, findings, evidence and grade |
| `--set Key=Value` | — | Override any config key for this run |

`--transcripts` is what produces the demo corpus. A transcript records **its own score**, so a run
where the agent was wrong is still publishable and still ships.

## `inspect`

Validates a cassette and describes what it holds. Needs nothing.

```sh
hephaisto-eval inspect cassettes/*.json
```

Prints the expected cause, tool counts, the incident card, the recording's origin, and a
**prompt-freshness** line — whether the prompts have changed since the cassette was recorded.
Exits 1 if a cassette references tools it does not declare.

## `redact`

Re-runs transcripts through the redactor and reports which changed. Needed only when the redaction
rules change.

```sh
hephaisto-eval redact src/Hephaisto.Agent/Demo/transcripts
```

::: warning This rewrites files in place
It is not a read-only inspection command. The redactor replaces IPv4 addresses with `0.0.0.0`,
running over the **serialized document** rather than a list of fields — an earlier version walked a
field list and missed one.
:::
