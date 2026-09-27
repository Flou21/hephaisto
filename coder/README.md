# hephaisto-coder

The runner a Kubernetes Job starts when Hephaisto decides an incident is a code bug. It clones
one repository, lets the Claude Agent SDK analyse the bug and write a plan (the **plan** phase,
read-only, automatic), and - after a human approved that plan, in a fresh pod - implements it
on an assigned branch, re-runs the repository's build and tests itself, and opens a **Draft PR**
(the **implement** phase). It never touches a cluster, never merges, never deploys.

Its only output is one framed JSON result, the last thing on stdout. Hephaisto reads it from the
pod log; the coder holds no Hephaisto credential and exposes nothing.

```
coder/
  src/main.ts        entry: env, request validation, dispatch, the one framed result (also on crash / SIGTERM)
  src/phases.ts      plan and implement, end to end
  src/workspace.ts   dev-context, CLAUDE_CONFIG_DIR, target clone, analysed commit, Cait, nuget.config
  src/guard.ts       THE tool guard (pure): what the agent may run, read and write, per phase
  src/shell.ts       the small shell lexer the guard sees commands through
  src/agent.ts       runAgent(): query() with the runner's fixed posture, budget, deadline, one repair turn
  src/sdk.ts         the ONLY importer of @anthropic-ai/claude-agent-sdk; real or fake
  src/fake-sdk.ts    scripted SDK for tests and $0 plumbing runs
  src/verify.ts      the driver's own restore/build/test/typecheck run
  src/git.ts pr.ts   the driver's git and gh; push of the assigned branch only; PR body
  src/result.ts      framing, schema caps, 512 KiB limit
  src/schemas.ts     ajv over the vendored contract + zod mirrors for types
  contracts/         vendored from dev-context/schemas by scripts/sync-schemas.sh - never edit by hand
  prompts/           built-in templates (dev-context/prompts/*.md wins when present)
  fake-scripts/      default.{plan,implement}.json + per-repository scripts
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

The final word is always `--in-image`. A fake end-to-end run needs no credential:

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

The Job sets (names only; secrets come from the `hephaisto-codefix` Secret via `secretKeyRef`):

| Variable | Needed | What |
|---|---|---|
| `CLAUDE_CODE_OAUTH_TOKEN` | real mode | Anthropic auth for the CLI (`ANTHROPIC_API_KEY` is accepted instead). The only credential the agent's process sees. |
| `GITHUB_TOKEN` | private repos, implement | Driver-only: `git` via `GIT_ASKPASS`, `gh` as `GH_TOKEN`. Never in the agent's env. |
| `NUGET_GITHUB_TOKEN` | .NET repos with private packages | Driver-only: becomes `username`/`token` for the TR `nuget.config` placeholders in the restore/build/test children. |
| `HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY` | with the egress proxy | Passed to git, gh, the CLI and builds. |
| `CODEFIX_REQUEST` | no | Request path, default `/work/in/request.json`. |
| `CODEFIX_SDK` | no | `real` (default) or `fake`. Fake refuses to start while an Anthropic credential is set, and prefixes every log line `FAKE SDK`. |
| `CODEFIX_GH` | no | `real` (default) or `shim` - puts `/opt/coder/gh-shim` first on PATH for local-cluster runs against a git server `gh` would refuse. |
| `CODEFIX_MODEL` | no | Model override for the CLI. |
| `CODEFIX_RESULT_SINK` | no | `file:///path` also writes the framed block there (eval harness). |
| `CODEFIX_WORK_DIR`, `CODEFIX_FAKE_SCRIPT_DIR`, `CODEFIX_GH_SHIM_DIR`, `CODEFIX_CLAUDE_EXECUTABLE` | no | Tests and development; the image's defaults are right for a Job. |

The image itself sets `CLAUDE_CONFIG_DIR=/work/.claude`, `HOME=/work/home`,
`DISABLE_AUTOUPDATER=1`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`, `DOTNET_CLI_TELEMETRY_OPTOUT=1` and
`NUGET_PACKAGES=/work/nuget/packages`.

## What the runner does, in order

**Both phases.** Validate the request against the vendored schema (any violation → a framed
`failed`). Clone dev-context at `context.ref` → `/work/context` and record its sha. Populate
`CLAUDE_CONFIG_DIR` from `dev-context/.claude` (settings, hooks, skills, rules) plus
`dev-context/CLAUDE.md`. Look the repository up in `repos.yaml` - absent or `coderEnabled: false`
is `failed: repository not enabled in dev-context repos.yaml` **without starting the agent**.
Blobless single-branch clone of the target. Delete its `.claude/settings*.json` and `.mcp.json`
(skip-worktree, so the deletion is never committed). If `cait.pinned`, clone Cait at the commit
that set the pinned `<Version>` to `/work/repos/Cait` and link `/work/ref/Cait` to it; failure to
do so is a note, not an error.

**plan.** Check out the commit from the image tag (`:<sha>` or `:<anything>-<sha>`; otherwise
default-branch HEAD, and a note says so). Run the agent read-only. The result carries the plan,
`analysed_ref`, `context_sha`, cost, session and every guard denial.

**implement.** Refuse a plan that is not `planned` or needs Cait. An open PR from the assigned
branch → `already_exists`, no agent. A remote branch without a PR is replaced only if every
commit on it carries this attempt's `Hephaisto-Attempt:` trailer. Pre-restore in a driver child
(the agent's shell has no feed credentials), run the agent on the assigned branch, commit
leftovers with the trailers, then: zero diff → `no_changes`; protected path, `.github/**`,
`.claude/**`, binary > 1 MB or a `ghp_`/`github_pat_`/`sk-ant-` line → `policy_diff`; the driver's
own restore/build/typecheck/test red → `build_failed`/`tests_failed` with the patch (≤ 1 MB) in
the pod log between `---HEPHAISTO-PATCH-BEGIN/END---`; otherwise push **only** the assigned branch
and `gh pr create --draft`. Nothing is pushed on any other outcome.

## The guard

`src/guard.ts` decides every tool call, three times over: the in-process PreToolUse hook (fires
for every call), `canUseTool` (Bash is deliberately not in `allowedTools`, so it reaches this
too), and `bin/guard` registered by dev-context's `settings.json`. In short: no `git
push|remote|config|fetch|clean|reset --hard`, no `gh`, no cluster or network tools, no `sudo`, no
environment dumps or `$*TOKEN*` references, no `/proc`, no command or process substitution, no
`sh -c`; writes only inside the target and never to protected globs. The plan phase allows only
Read/Grep/Glob and Bash whose every segment is on a read-only allowlist. `test/hooks.test.ts` is
the table.

**Same-uid caveat, stated rather than hidden:** the agent's Bash runs as the driver's uid and the
CLI needs the Anthropic credential in its environment, so a determined agent could in principle
reach it. The real controls are the egress proxy allowlist, a GitHub token scoped to the
allowlisted repositories, branch protection on default branches, and the guard's denial log.
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` would help but makes CLI 2.1.283 refuse to start without
bubblewrap, which a non-privileged pod cannot run.

## Fake scripts

`CODEFIX_SDK=fake` replaces `query()` with a script that yields real `SDKMessage` shapes and
sends every tool call through the **same** hooks and `canUseTool` as the real CLI, so a scripted
`git push --force origin main` produces a real denial record. Scripts are chosen as
`<repos.yaml name>.<phase>.json`, falling back to `default.<phase>.json`, from
`CODEFIX_FAKE_SCRIPT_DIR` (default `/opt/coder/fake-scripts`).

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
the fake SDK against a bare remote and the gh shim), and `real-sdk` - the real SDK and the pinned
CLI against a localhost mock of the Messages API with a dummy key, which pins what an SDK bump
must keep true: structured output arrives as `result.structured_output`, `settingSources:
['user']` loads `$CLAUDE_CONFIG_DIR/CLAUDE.md` and `rules/` but not the target's `CLAUDE.md`, the
hook sees every call, and the offered tool set is exactly the pinned one.

SDK and CLI move in lockstep (`@anthropic-ai/claude-agent-sdk` 0.3.N with
`@anthropic-ai/claude-code` 2.1.N); the Dockerfile refuses to build a mismatched pair.
