# hephaisto-coder

The runner a Kubernetes Job starts when Hephaisto decides an incident is a code bug. It clones
one repository, lets the Claude Agent SDK analyse the bug and write a plan (the **plan** phase,
read-only, automatic), and - after a human approved that plan, in a fresh pod - implements it
on an assigned branch, re-runs the repository's build and tests itself, and opens a **Draft PR**
(the **implement** phase). It never touches a cluster, never merges, never deploys.

Since v0.12.0 the same image also **investigates** an incident (the **investigate** phase):
Hephaisto hands it a rendered prompt and an investigator MCP endpoint, and the runner lets Claude
Code investigate through that endpoint's read-only cluster tools until it calls `conclude`.

Its only output is one framed JSON result, the last thing on stdout. Hephaisto reads it from the
pod log; the coder holds no Hephaisto credential and exposes nothing.

## Three roles, three containers

A Job starts this image up to three times, with `CODEFIX_ROLE` saying which part of the run each
container is (v0.13.0, backlog #116). The point is who holds what: the container the model runs
in is handed the model's credential and **no GitHub or NuGet token**, and since the containers
share no process namespace, nothing the model runs can read one out of another's environment.

| Role | In a Job | Holds | Does |
|---|---|---|---|
| `prepare` | init container, always | `GITHUB_TOKEN`, `NUGET_GITHUB_TOKEN` | everything that needs a token before the model exists: the clones, the open-PR and remote-branch checks, the assigned branch, the pre-restore |
| `coder` | the regular container of a plan or an investigation; the second init container of an implementation | `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY` | the agent, and everything that executes what the agent wrote: build, tests |
| `publish` | implementation only, the regular container | `GITHUB_TOKEN` | push and Draft PR, from a copy of its own; prints the result |

Exactly one container prints the framed result, and it is the pod's regular one: `coder` for a
plan or an investigation, `publish` for an implementation. A role that does not print hands its
*result* on through a file and exits 0, so a failure in `prepare` still reaches Hephaisto as an
ordinary framed block. A role that is started beside a credential that is not its own - `coder`
with `GITHUB_TOKEN` set, say - refuses to run and says so.

**The handoff** (`src/handoff.ts`), three files:

| File | From → to | Believed? |
|---|---|---|
| `/work/handoff/prepare.json` | prepare → coder | yes: written and read before the model exists |
| `/sealed/prepare.json` | prepare → publish | yes: `/sealed` is not mounted in the coder container |
| `/work/handoff/coder.json`, `/work/handoff/branch.bundle` | coder → publish | **no** |

`prepare.json` carries what was prepared - the dev-context sha, the checkout's directory, the base
commit, notes and deviations so far, the protected paths and the PR's assignee, labels and
template - or, in `terminal`, an early result (`already_exists`, a failure) that ends the run.
`coder.json` carries either an outcome (`publish: false`) or, after a green verification, the
verified commit and the verification report beside `branch.bundle`, the branch as a git bundle.
Both are schema-checked and closed; neither ever holds a secret.

**`publish` trusts nothing on `/work`.** That volume is where the model worked: its repository's
`.git/config` and hooks, the dev-context clone and `HOME` were all writable by code the model
chose. So `publish` never runs git there. It reads `coder.json` and `branch.bundle` as bytes (no
symbolic links, regular files only, size-capped, never quoted in an error), builds a bare
repository in its own `/tmp`, fetches the default branch from the request's URL, imports the
bundle, and derives from that copy everything a push depends on: the tip is the commit the driver
verified, it descends from the base `prepare` sealed, every commit carries this attempt's trailer,
the publishing policy holds, the remote branch is absent or this attempt's own. Then it pushes
that commit by id to the request's URL and runs `gh` from an empty directory. The header of
`src/publish.ts` lists every git mechanism this takes out of play, and what remains.

**Without `CODEFIX_ROLE`** all three roles run in one process, one after the other, through the
same files and each with the environment its container would have had. That is what `docker run`
and the tests use. Nothing separates the model from the tokens there; no Job uses it.

```
coder/
  src/main.ts        entry: env, request validation, the role, the one framed result (also on crash / SIGTERM)
  src/config.ts      the environment, the roles, which credential belongs to which
  src/handoff.ts     what one role leaves for the next; how publish reads what it does not trust
  src/phases.ts      plan and implement: the prepare half and the coder half of each
  src/publish.ts     the publish role: the push and the PR, from a repository of its own
  src/policy.ts      the publishing policy on a diff - run by coder, and again by publish
  src/investigate.ts investigate: the clones (prepare), then preflight, the model, code_refs (coder)
  src/mcp-client.ts  the driver's MCP client for the investigator endpoint (preflight, fake SDK)
  src/workspace.ts   dev-context, CLAUDE_CONFIG_DIR, target clone, analysed commit, Cait, nuget.config
  src/guard.ts       THE tool guard (pure): what the agent may run, read and write, per phase
  src/shell.ts       the small shell lexer the guard sees commands through
  src/agent.ts       runAgent(): query() with the runner's fixed posture, budget, deadline, one repair turn;
                     runInvestigator(): the investigate posture, conclude detection, one conclude-now turn
  src/sdk.ts         the ONLY importer of @anthropic-ai/claude-agent-sdk; real or fake
  src/fake-sdk.ts    scripted SDK for tests and $0 plumbing runs
  src/verify.ts      the driver's own restore/build/test/typecheck run; naming a feed's refusal
  src/git.ts pr.ts   the driver's git and gh; push of one commit to the assigned branch; PR body
  src/result.ts      framing, schema caps, 512 KiB limit
  src/schemas.ts     ajv over the vendored contract + zod mirrors for types
  contracts/         vendored from dev-context/schemas by scripts/sync-schemas.sh - never edit by hand
  prompts/           built-in templates (dev-context/prompts/*.md wins when present)
  fake-scripts/      default.{plan,implement,investigate}.json + per-repository / per-workload scripts
  bin/guard          the guard behind the Claude Code hook protocol (dev-context settings.json)
  bin/askpass        GIT_ASKPASS for the driver's git children
  test/gh-shim/gh    gh stand-in (tests, local-cluster plumbing); installed at /opt/coder/gh-shim/gh
```

## Running it

```sh
npm ci
scripts/test.sh                 # natively: compiles, runs vitest, enforces the test-count floor
docker build -t hephaisto/coder:dev .
scripts/test.sh --in-image      # the same suite inside the image, read-only root, uid 64198
```

The final word is always `--in-image`. A fake end-to-end run needs no credential, and with no
`CODEFIX_ROLE` it is all three roles in this one container:

```sh
docker run --rm --read-only \
  --tmpfs /tmp:rw,exec,mode=1777 --tmpfs /work:rw,exec,size=4g,uid=64198,gid=64198 \
  -e CODEFIX_SDK=fake -e CODEFIX_GH=shim \
  -v "$PWD/request.json:/work/in/request.json:ro" \
  hephaisto/coder:dev
```

`request.json` must name reachable `repository.url` and `context.repository_url` (`https://`,
`http://` for the in-cluster dev git server, or `file://`). Docker's `--tmpfs` defaults to
`noexec` and 64 MiB; `/work` needs `exec` and room for a clone and a build.

## Environment

The Job sets (names only; secrets come from the `hephaisto-codefix` Secret via `secretKeyRef`,
each key to the containers named):

| Variable | Needed | What |
|---|---|---|
| `CODEFIX_ROLE` | in a Job | `prepare`, `coder` or `publish`; unset is all three in one process. |
| `CLAUDE_CODE_OAUTH_TOKEN` | real mode, `coder` only | Anthropic auth for the CLI (`ANTHROPIC_API_KEY` is accepted instead). The only credential in the container the model runs in. `prepare` and `publish` refuse to start beside it. |
| `GITHUB_TOKEN` | private repos; `prepare` and `publish` only | `git` via `GIT_ASKPASS`, `gh` as `GH_TOKEN`. `coder` refuses to start beside it, or beside `GH_TOKEN`. |
| `NUGET_GITHUB_TOKEN` | .NET repos with private packages; `prepare` only | Becomes `username`/`token` for the TR `nuget.config` placeholders in the pre-restore. Verification in `coder` has no feed credentials. |
| `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY` (and lowercase) | with the egress proxy, every container | Passed to git, gh, the CLI and builds. |
| `CODEFIX_REQUEST` | no | Request path, default `/work/in/request.json`. |
| `CODEFIX_SDK` | no | `real` (default) or `fake`. Fake refuses to start while an Anthropic credential is set, and prefixes every log line `FAKE SDK`. |
| `CODEFIX_GH` | no | `real` (default) or `shim` - puts `/opt/coder/gh-shim` first on PATH for local-cluster runs against a git server `gh` would refuse. |
| `CODEFIX_MODEL` | no | Model override for the CLI. |
| `CODEFIX_RESULT_SINK` | no | `file:///path` also writes the framed block there (eval harness). |
| `CODEFIX_FAKE_SCRIPT` | no | investigate + fake only: play this script (`fail`, `slow`, or a file name) instead of the one the target selects. |
| `CODEFIX_WORK_DIR`, `CODEFIX_HANDOFF_DIR`, `CODEFIX_SEAL_DIR`, `CODEFIX_FAKE_SCRIPT_DIR`, `CODEFIX_GH_SHIM_DIR`, `CODEFIX_CLAUDE_EXECUTABLE` | no | Tests and development; the image's defaults (`/work`, `/work/handoff`, `/sealed`) are right for a Job. |

The image itself sets `CLAUDE_CONFIG_DIR=/work/.claude`, `HOME=/work/home`,
`DISABLE_AUTOUPDATER=1`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`, `DOTNET_CLI_TELEMETRY_OPTOUT=1` and
`NUGET_PACKAGES=/work/nuget/packages`.

## What the runner does, in order

Each paragraph says which role does what; in a Job that is which container.

**Both phases, `prepare`.** Validate the request against the vendored schema (any violation → a
framed `failed`, printed by the role that prints). Clone dev-context at `context.ref` →
`/work/context` and record its sha. Populate
`CLAUDE_CONFIG_DIR` from `dev-context/.claude` (settings, hooks, skills, rules) plus
`dev-context/CLAUDE.md`. Look the repository up in `repos.yaml` - absent or `coderEnabled: false`
is `failed: repository not enabled in dev-context repos.yaml` **without starting the agent**.
Blobless single-branch clone of the target. Delete its `.claude/settings*.json` and `.mcp.json`
(skip-worktree, so the deletion is never committed). If `cait.pinned`, clone Cait at the commit
that set the pinned `<Version>` to `/work/repos/Cait` and link `/work/ref/Cait` to it; failure to
do so is a note, not an error.

**plan.** `prepare`: check out the commit from the image tag (`:<sha>` or `:<anything>-<sha>`;
otherwise default-branch HEAD, and a note says so). `coder`: run the agent read-only. The result
carries the plan, `analysed_ref`, `context_sha`, cost, session and every guard denial.

**implement.** `prepare`: refuse a plan that is not `planned` or needs Cait. An open PR from the
assigned branch → `already_exists`, no agent. A remote branch without a PR is acceptable only if
every commit on it carries this attempt's `Hephaisto-Attempt:` trailer. Create the assigned
branch and pre-restore - the one moment a package feed is asked with credentials. `coder`: run
the agent on the assigned branch, commit leftovers with the trailers, then: zero diff →
`no_changes`; protected path, `.github/**`, `.claude/**`, binary > 1 MB or a
`ghp_`/`github_pat_`/`sk-ant-` line → `policy_diff`; the driver's own
restore/build/typecheck/test red → `build_failed`/`tests_failed` with the patch (≤ 1 MB) in the
coder container's log between `---HEPHAISTO-PATCH-BEGIN/END---`; otherwise write the branch as a
bundle. `publish`: check that bundle on a copy of its own (see above), ask the remote about the
branch again, push **only** that one commit to the assigned branch and `gh pr create --draft`.
Nothing is pushed on any other outcome.

Verification builds from the packages the pre-restore cached and has no feed credentials. When a
step fails because a feed refused it, a package is on no reachable source, or the cache could not
be written, the result's deviations say so in words - the fix most likely adds or bumps a package
reference, and a person has to restore and verify it - instead of leaving a bare 401.

**investigate.** Validate against `investigate-request.schema.json`. The endpoint token is
redacted from every log line from here on. `prepare`: clone dev-context - private in production,
so this needs the GitHub token in every run - and, if `source` is set, a blobless clone into
`/work/repos/<name>` at `source.ref` (else the image tag's sha, else default-branch HEAD),
sanitised like a target - no Cait, no restore, no build; a failure becomes `source.error` and the
run goes on. `coder`: a real run without an Anthropic credential is `no_credential`, not a crash.
**Preflight:** one MCP `initialize` + `tools/list` against `endpoint.url` with the bearer - a 401
is `failed: endpoint_unauthorized` before any model token is spent (connection failures are
retried three times, as for a clone). Then the agent runs with Hephaisto's
`system_prompt` plus `prompts/investigate.md` appended, the opening message as the prompt,
`settingSources: []`, `strictMcpConfig`, the endpoint as MCP server `hephaisto` (so its tools are
`mcp__hephaisto__*`), built-ins `Read`/`Grep`/`Glob` only, `maxTurns` and `maxBudgetUsd` from the
request, and `NO_PROXY`/`no_proxy` extended by the endpoint host so the MCP traffic bypasses the
egress proxy.

`concluded` means a `mcp__hephaisto__conclude` call's tool result came back without an error; the
guard then refuses every further call. Stopped by max turns or budget without it, the session is
resumed ONCE with "Conclude now with the evidence you have; call conclude." and only `conclude`
allowed (that turn may spend up to 10% of the budget beyond the cap); still nothing is
`max_turns` / `budget_exhausted`. Two 401s in a row from the endpoint mid-run (Hephaisto
restarted and forgot the token) abort the run as `failed: endpoint_unauthorized`. The result's
`code_refs` are the conclude call's `code_refs` that exist in the clone (file present, line in
range), paths relative to the repository root; `billing` is `subscription`
(CLAUDE_CODE_OAUTH_TOKEN), `api` (ANTHROPIC_API_KEY) or `fake`. On SIGTERM the frame is the result
so far (context sha, source, cost, tokens) with outcome `failed`.

## The guard

`src/guard.ts` decides every tool call, three times over: the in-process PreToolUse hook (fires
for every call), `canUseTool` (Bash is deliberately not in `allowedTools`, so it reaches this
too), and `bin/guard` registered by dev-context's `settings.json`. In short: no `git
push|remote|config|fetch|clean|reset --hard`, no `gh`, no cluster or network tools, no `sudo`, no
environment dumps or `$*TOKEN*` references, no `/proc`, no command or process substitution, no
`sh -c`; writes only inside the target and never to protected globs. The plan phase allows only
Read/Grep/Glob and Bash whose every segment is on a read-only allowlist. `test/hooks.test.ts` is
the table.

Mode `investigate` is a separate, shorter list: `mcp__hephaisto__*` allowed, every other `mcp__`
server denied, no Bash, no Edit/Write/MultiEdit/NotebookEdit, no web, no subagents, and
Read/Grep/Glob only under `/work/context` and `/work/repos` (realpaths; an absolute Glob pattern's
literal prefix counts too) - so `/work/in/request.json`, which holds the endpoint token, is out of
reach. `bin/guard` accepts the mode (with `GUARD_READ_ROOTS`) and still fails closed on any other.

**What the guard is not, since v0.13.0:** the thing between the model and the GitHub and NuGet
tokens. Those are in other containers. Its rules against `env`, `/proc` and every `*TOKEN*` name
are unchanged - defence in depth - and for one credential they are still the only defence.

**What is left of the same-uid caveat, stated rather than hidden:** the agent's Bash runs as the
driver's uid in the `coder` container, and the CLI needs the Anthropic credential in its
environment, so a determined agent could in principle reach *that one*. It calls a model; it
pushes nothing. The controls are the egress proxy allowlist and the guard's denial log.
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` would help but makes CLI 2.1.283 refuse to start without
bubblewrap, which a non-privileged pod cannot run. And the verdict "build and tests passed" is
that container's word: `publish` re-checks what is pushed, not that it was tested.

## Fake scripts

`CODEFIX_SDK=fake` replaces `query()` with a script that yields real `SDKMessage` shapes and
sends every tool call through the **same** hooks and `canUseTool` as the real CLI, so a scripted
`git push --force origin main` produces a real denial record. Scripts are chosen as
`<repos.yaml name>.<phase>.json`, falling back to `default.<phase>.json`, from
`CODEFIX_FAKE_SCRIPT_DIR` (default `/opt/coder/fake-scripts`).

A script is the model's stand-in, so it plays in the `coder` role and nowhere else. `prepare` and
`publish` of a scripted Job are the real ones - real clones, a real bundle check, a real push -
and with `CODEFIX_GH=shim` their `gh` is the stand-in. A scripted Job is handed no model
credential in any container, and every role refuses fake mode beside one.

```json
{
  "description": "free text",
  "steps": [
    { "tool": "Bash", "input": { "command": "git log --oneline -5" } },
    { "tool": "Read", "input": { "file_path": "{{target}}/src/Startup/Endpoints.cs" } },
    { "patch_file": "hephaisto-fixture-dotnet.fix.patch" },
    { "patch": "--- a/x\n+++ b/x\n@@ ... unified diff ..." },
    { "append": { "path": "src/x.cs", "text": "// marker\n" } },
    { "commit": "fix(api): endpoints tolerate a missing section" },
    { "text": "assistant prose" },
    { "sleep_ms": 1000 },
    { "throw": "simulated crash" },
    { "result": { "cost_usd": 1.2, "structured_output": { } } }
  ],
  "repair": [ { "result": { "structured_output": { } } } ]
}
```

- `tool` - any tool call; executed only if the guard and permission path allow it (Bash, Read,
  Write and Edit really run; others are no-ops).
- `patch` / `patch_file` - a unified diff applied with `git apply`; each touched file first asks
  permission as an `Edit`. `patch_file` is relative to the script directory.
- `append` - appends via a `Write` tool call. `commit` - `git add -A` + `git commit` as Bash tool
  calls, message plus the `Hephaisto-Incident:`/`Hephaisto-Attempt:` trailers.
- `result` - ends the run. `subtype` defaults to `success`; `error_max_turns`,
  `error_max_budget_usd`, `error_during_execution` (with `errors`, e.g. a 429 text) and
  `error_max_structured_output_retries` are available. A cost above the request's budget turns a
  success into `error_max_budget_usd`, as the CLI would.
- `repair` - played instead of `steps` when the runner resumes the session for its one repair
  turn after an invalid structured output.

**investigate scripts** (`<workload>.investigate.json`, where the workload is the last segment
of `incident.target.workload`, then `<incident.target.name>.investigate.json`, then
`default.investigate.json`; `CODEFIX_FAKE_SCRIPT` overrides) run each `mcp__hephaisto__*` step as
a REAL MCP call against the request's endpoint, with the headers the real CLI would get. A tool
step may `capture: { "as": "logs", "regex": "(NullReferenceException[^\\n]*)" }`; later strings
use `${logs}` (group 1, or the whole match), `${logs.step}` (the uuid from that result's
`[step <uuid>]` header) and `${request.incident.target.namespace}` (any request field). A resumed
turn plays `repair` and still sees the session's captures. Ending without a `result` step is a
successful end, and the outcome is whatever conclude did. Shipped: `default` (list_pods,
get_events, low-confidence `unknown`), `shop-api` (c15: the NullReferenceException line and
`code_refs` Endpoints.cs:17), `fail` (no conclude), `slow` and `catalog-api` (sleep 180 s first).

Every string may use `{{target}}`, `{{attempt_id}}`, `{{incident_id}}`, `{{branch}}`,
`{{default_branch}}`, `{{repo_name}}`, `{{analysed_ref}}`, and per phase `{{first_evidence_file}}`
(plan: a file named by the evidence) or `{{plan_file}}` / `{{line_comment}}` (implement: the
approved plan's first file and a comment prefix for it).

The plan-phase structured output is the plan result's `outcome, summary, root_cause, confidence,
files, steps, verification, needs_cait, notes`; the implement phase's is `files, deviations` and
an optional `summary`. Both are cut from the vendored result schemas.

## Tests

`scripts/test.sh` fails below its test-count floor. The suites: `hooks` (guard table + `bin/guard`
protocol), `schema` + `contracts` (samples, lock hashes, zod ↔ JSON Schema), `prompt`, `result`,
`workspace` (image tags, credentials, nuget.config, Cait pinning), `driver` (main() end to end with
the fake SDK against a bare remote and the gh shim, every role in one process), `roles` (each role
as its own main() with only its container's environment: who prints, who refuses which
credential, that no token reaches the shared volume, and `publish` against a repository with
planted hooks and a planted `.git/config`, a tampered bundle and a tampered handoff), and
`real-sdk` - the real SDK and the pinned
CLI against a localhost mock of the Messages API with a dummy key, which pins what an SDK bump
must keep true: structured output arrives as `result.structured_output`, `settingSources:
['user']` loads `$CLAUDE_CONFIG_DIR/CLAUDE.md` and `rules/` but not the target's `CLAUDE.md`, the
hook sees every call, and the offered tool set is exactly the pinned one.

SDK and CLI move in lockstep (`@anthropic-ai/claude-agent-sdk` 0.3.N with
`@anthropic-ai/claude-code` 2.1.N); the Dockerfile refuses to build a mismatched pair.
