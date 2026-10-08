import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { type AgentRunResult, buildAgentEnv, guardEnvFor, runAgent } from './agent.js';
import { IMPLEMENT_MAX_TURNS, PATCH_MAX_BYTES, PLAN_MAX_TURNS, type RunnerEnv, type WorkPaths } from './config.js';
import { Git } from './git.js';
import { type GuardContext, realish } from './guard.js';
import { BUNDLE_FILE, type CoderHandoff, HANDOFF_VERSION, type PrepareHandoff, newPrepareHandoff } from './handoff.js';
import { log } from './log.js';
import { changedFiles, policyCheck } from './policy.js';
import { BRANCH_RE, findOpenPr, ghEnv, inspectRemoteBranch } from './pr.js';
import { fencedJson, loadTemplate, render, renderEvidenceBlock, renderIssueBlock, repoNotesBlock } from './prompts.js';
import { NOT_ENABLED, enabledRepo, protectedGlobs, repoDirName } from './repos.js';
import { minimalFailed } from './result.js';
import { type CodeFixRequest, type ImplementResult, type PlanResult, type RepoEntry, rawSchema, validateWith } from './schemas.js';
import type { QueryFn } from './sdk.js';
import { isWorkItem, subjectOf, trailersOf, untrustedText } from './subject.js';
import { type VerificationReport, feedRefusal, feedRefusalNote, runVerification } from './verify.js';
import { type Target, checkoutAnalysedRef, cloneTarget, ensureNugetConfig, makeDirs, nugetCredentialEnv, openPrepared, prepareCait, prepareContext, sanitizeTarget } from './workspace.js';

// plan and implement, each cut where the model starts (backlog #116):
//
//   prepare*   needs a GitHub or NuGet token and runs BEFORE the model exists. What it decided
//              travels in a PrepareHandoff - or, when the run ends here (the repository is not
//              enabled, an open PR already exists, a clone failed), in that handoff's `terminal`.
//   coder*     the agent, and whatever executes what the agent wrote. No git or NuGet token is
//              in this role's environment, so none of the git it runs can authenticate - and
//              none needs to: everything it reads was fetched by prepare.
//   publish    (publish.ts) the push and the Draft PR, from a copy this role does not share.

export interface PrepareDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  deadline: number;
  abort: AbortController;
  /** When this run started; it travels in the handoff, because the Job's deadline started then too. */
  startedAt: number;
}

export interface PhaseDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  deadline: number;
  abort: AbortController;
  /** Built lazily, once the workspace exists (the fake needs to know the target). */
  makeQuery: (fake: { repoName: string; vars: Record<string, string> }) => Promise<QueryFn>;
}

// ---------------------------------------------------------------------------------------------
// The agent's structured-output schemas are cut from the vendored result schemas, so the caps the
// model is held to are the caps the contract enforces.

const AGENT_PLAN_FIELDS = ['outcome', 'summary', 'root_cause', 'confidence', 'files', 'steps', 'verification', 'needs_cait', 'notes'] as const;

export function planOutputSchema(): Record<string, unknown> {
  const props = rawSchema('plan').properties as Record<string, unknown>;
  return {
    type: 'object',
    additionalProperties: false,
    required: [...AGENT_PLAN_FIELDS],
    properties: Object.fromEntries(AGENT_PLAN_FIELDS.map((k) => [k, props[k]])),
  };
}

export function implementOutputSchema(): Record<string, unknown> {
  const props = rawSchema('implement').properties as Record<string, Record<string, unknown>>;
  return {
    type: 'object',
    additionalProperties: false,
    required: ['files', 'deviations'],
    properties: {
      files: props.files,
      deviations: props.deviations,
      summary: { type: 'string', maxLength: 2000, description: 'The change as it should read in the pull request.' },
    },
  };
}

export type AgentPlan = Pick<PlanResult, (typeof AGENT_PLAN_FIELDS)[number]>;
export interface AgentImplement {
  files: string[];
  deviations: string[];
  summary?: string;
}

// ---------------------------------------------------------------------------------------------

function commandsBlock(repo: RepoEntry): string {
  const lines = (['restore', 'build', 'test', 'typecheck'] as const)
    .filter((k) => repo.commands[k])
    .map((k) => `- ${k}: \`${repo.commands[k]}\``);
  return lines.length ? lines.join('\n') : '- (repos.yaml lists no commands for this repository)';
}

function expectedLevel(repo: RepoEntry): string {
  if (repo.commands.test && repo.verification.hasUnitTests) return 'tests';
  if (repo.commands.build) return 'build-only';
  if (repo.commands.typecheck) return 'typecheck-only';
  return 'none';
}

function baseVars(req: CodeFixRequest, repo: RepoEntry, target: Target, paths: WorkPaths): Record<string, string> {
  const common = {
    attempt_id: req.attempt_id,
    phase: req.phase,
    repo_url: req.repository.url,
    repo_name: repo.name,
    default_branch: req.repository.default_branch,
    branch: req.repository.branch,
    commands: commandsBlock(repo),
    verification_level: expectedLevel(repo),
    repo_notes: repoNotesBlock(target.claudeMd),
    cait_ref: target.caitDir ? join(paths.ref, 'Cait') : '(none: this repository does not pin Cait)',
    memory_dir: join(paths.context, 'memory'),
    workspace: target.dir,
    trailers: trailersOf(req),
  };
  if (isWorkItem(req)) {
    return {
      ...common,
      // owner/repo#n: Hephaisto's configuration and an integer. Everything of the issue that
      // somebody typed - title, author, body, comments - is INSIDE issue_block and nowhere else.
      issue_ref: subjectOf(req).ref,
      issue_block: renderIssueBlock(req, paths.context),
    };
  }
  return {
    ...common,
    incident_id: req.incident_id,
    image: req.incident.image ?? '(unknown)',
    incident_title: req.incident.title,
    incident_kind: req.incident.kind,
    incident_severity: req.incident.severity,
    workload: req.incident.target.workload,
    escalation_reason: req.incident.escalation_reason,
    evidence_block: renderEvidenceBlock(req, paths.context),
    // investigation_summary is log-derived: it lives INSIDE the evidence element, never here
    investigation_summary: '',
  };
}

function additionalDirs(paths: WorkPaths, target: Target): string[] {
  const dirs = [join(paths.context, 'memory')];
  if (target.caitDir) dirs.unshift(join(paths.ref, 'Cait'));
  return dirs.filter((d) => existsSync(d));
}

/** Evidence-named file for the fake scripts: a path in an excerpt - or in the issue - else a type name from a stack frame, else the first tracked file. */
async function firstEvidenceFile(req: CodeFixRequest, git: Git): Promise<string> {
  const files = (await git.ok(['ls-files'])).split('\n').filter(Boolean);
  const text = untrustedText(req);
  for (const m of text.matchAll(/[\w./-]+\.(?:cs|ts|js|vue|py|sh|go|java|json|ya?ml)\b/g)) {
    const hit = files.find((f) => f === m[0] || f.endsWith(`/${m[0]}`) || m[0].endsWith(`/${f}`));
    if (hit) return hit;
  }
  for (const m of text.matchAll(/\bat ([\w.]+)\.[\w<>]+\(/g)) {
    const typeName = m[1]!.split('.').pop()!;
    const hit = files.find((f) => f.split('/').pop()?.replace(/\.[^.]+$/, '') === typeName);
    if (hit) return hit;
  }
  return files[0] ?? 'README.md';
}

/** What an issue - or an incident's evidence - asks the scripted model to say again. */
export const FAKE_REPEAT_MARKER = 'FAKE-SDK-REPEAT:';

/**
 * For the fake scripts, as `{{repeated}}`: a sentence that repeats what follows
 * FAKE-SDK-REPEAT: on a line of the request's untrusted text, or nothing. A model does repeat
 * what a stranger wrote - a mention, a closing keyword, another issue's address - and the
 * scripted one could not, so nothing end to end ever put such text where GitHub reads it. With
 * the marker absent the variable is empty and a script that uses it reads as it always did.
 */
export function fakeRepeated(req: CodeFixRequest): string {
  const text = untrustedText(req);
  const at = text.indexOf(FAKE_REPEAT_MARKER);
  if (at < 0) return '';
  const said = (text.slice(at + FAKE_REPEAT_MARKER.length).split(/\r?\n/)[0] ?? '').replace(/`/g, '').trim().slice(0, 300);
  return said ? ` The reporter asked for this to be repeated: ${said}` : '';
}

function lineComment(file: string): string {
  if (/\.(cs|ts|js|java|go|vue|kt|swift|c|cpp|h)$/.test(file)) return '//';
  if (/\.(md|html|xml|csproj)$/.test(file)) return '<!--';
  return '#';
}

function relFiles(files: string[], targetDir: string): string[] {
  return files.map((f) => (f.startsWith(targetDir) ? relative(targetDir, f) : f.replace(/^\.\//, '')));
}

// =============================================================================================
// plan

export async function preparePlan(req: CodeFixRequest, deps: PrepareDeps): Promise<PrepareHandoff> {
  const { env, paths } = deps;
  const h = newPrepareHandoff(req.attempt_id, 'plan', deps.startedAt);
  const end = (error: string): PrepareHandoff => {
    const result = minimalFailed('plan', req.attempt_id, error) as PlanResult;
    return { ...h, terminal: { ...result, context_sha: h.context_sha, analysed_ref: h.analysed_ref } };
  };
  try {
    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    h.context_sha = ctx.contextSha;
    const repo = enabledRepo(ctx.repos, req.repository.url);
    if (!repo) {
      log.warn(`${req.repository.url}: ${NOT_ENABLED}; the agent is not started`);
      return end(NOT_ENABLED);
    }
    const target = await cloneTarget(req, ctx.repos, repo, env, paths, deps.abort.signal);
    h.repo_dir = repoDirName(repo);
    h.claude_md = target.claudeMd;
    if (isWorkItem(req)) {
      // An issue names a repository, not something that runs: there is no image whose commit
      // could be analysed, and nothing "deployed" for the default branch's HEAD to differ from.
      h.analysed_ref = await target.git.head();
      log.info(`analysing ${req.repository.default_branch} HEAD ${h.analysed_ref} (an issue names no running image)`);
    } else {
      h.analysed_ref = await checkoutAnalysedRef(target, req.incident.image, req.repository.default_branch);
    }
    await sanitizeTarget(target);
    await prepareCait(target, req.repository.url, ctx.repos, env, paths, deps.abort.signal);
    h.cait = target.caitDir !== null;
    h.notes = [...target.notes];
    return h;
  } catch (e) {
    log.error(`plan could not be prepared: ${(e as Error).stack ?? String(e)}`);
    return end((e as Error).message);
  }
}

export async function coderPlan(req: CodeFixRequest, deps: PhaseDeps, h: PrepareHandoff): Promise<PlanResult> {
  const { env, paths } = deps;
  const result = minimalFailed('plan', req.attempt_id, 'plan did not complete') as PlanResult;
  result.context_sha = h.context_sha;
  result.analysed_ref = h.analysed_ref;
  try {
    const { repos, repo, target } = openPrepared(req.repository.url, h, env, paths, deps.abort.signal);

    const vars = { ...baseVars(req, repo, target, paths), analysed_ref: result.analysed_ref ?? '', result_schema: JSON.stringify(planOutputSchema(), null, 2) };
    const prompt = render(loadTemplate(subjectOf(req).templates.plan, paths.context).text, vars);
    const guard: GuardContext = { targetDir: realish(target.dir), protectedGlobs: protectedGlobs(repos, repo), homeDir: paths.home };
    const query = await deps.makeQuery({
      repoName: repo.name,
      vars: { ...vars, target: target.dir, first_evidence_file: await firstEvidenceFile(req, target.git), repeated: fakeRepeated(req) },
    });
    const schema = planOutputSchema();
    const agent: AgentRunResult<AgentPlan> = await runAgent<AgentPlan>({
      phase: 'plan',
      prompt,
      cwd: target.dir,
      env: buildAgentEnv(env, paths, guardEnvFor('plan', guard)),
      additionalDirectories: additionalDirs(paths, target),
      maxTurns: PLAN_MAX_TURNS,
      maxBudgetUsd: req.budget.max_cost_usd,
      outputSchema: schema,
      validate: (v) => validateWith(schema, v),
      guard,
      deadline: deps.deadline,
      abort: deps.abort,
      query,
      model: env.model,
      claudeExecutable: env.claudeExecutable,
    });
    result.cost_usd = agent.costUsd;
    result.session_id = agent.sessionId;
    result.denied_tool_calls = agent.denials;
    result.notes = [...target.notes];
    if (!agent.ok || !agent.output) {
      result.error = agent.error;
      return result;
    }
    const o = agent.output;
    return {
      ...result,
      outcome: o.outcome,
      summary: o.summary,
      root_cause: o.root_cause,
      confidence: o.confidence,
      files: relFiles(o.files, target.dir),
      steps: o.steps,
      verification: o.verification,
      needs_cait: o.needs_cait,
      notes: [...target.notes, ...o.notes],
      error: o.outcome === 'failed' ? (o.summary || 'the agent reported failed') : null,
    };
  } catch (e) {
    log.error(`plan failed: ${(e as Error).stack ?? String(e)}`);
    result.error = (e as Error).message;
    return result;
  }
}

// =============================================================================================
// implement

/** Every commit on the branch carries this attempt's trailers, so a retried Job can recognise its own work. */
async function ensureTrailers(git: Git, base: string, req: CodeFixRequest): Promise<void> {
  const commits = (await git.ok(['log', '--format=%H%x1f%(trailers:key=Hephaisto-Attempt,valueonly,separator=%x2c)', `${base}..HEAD`])).split('\n').filter(Boolean);
  if (commits.every((c) => (c.split('\x1f')[1] ?? '').includes(req.attempt_id))) return;
  log.info('adding Hephaisto trailers to the agent\'s commits that lack them');
  // ids are schema-validated uuids, safe to put in the exec line
  // and a work item's is owner/repo#n, held to a pattern with no quote and no space in it
  const exec = `git -c trailer.ifexists=addIfDifferent commit --amend --no-edit --no-verify --quiet --trailer '${subjectOf(req).trailer}' --trailer 'Hephaisto-Attempt: ${req.attempt_id}'`;
  await git.ok(['rebase', '--quiet', '--exec', exec, base]);
}

/**
 * The environment of the driver's own restore/build/test. In the prepare role that includes the
 * two variables the TR nuget.config files read; in the coder role nugetCredentialEnv is empty -
 * there is no token in that container to pass on.
 */
function driverChildEnv(env: RunnerEnv, paths: WorkPaths): NodeJS.ProcessEnv {
  const out: NodeJS.ProcessEnv = {};
  for (const [k, v] of Object.entries(env.base)) {
    if (v === undefined) continue;
    if (/TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|_KEY$|CREDENTIAL|_PAT$|PRIVATE/i.test(k)) continue;
    if (/^(GH_|GITHUB_|CODEFIX_|GUARD_)/.test(k)) continue;
    out[k] = v;
  }
  return {
    ...out,
    HOME: paths.home,
    NUGET_PACKAGES: paths.nugetPackages,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    DOTNET_NOLOGO: '1',
    ...nugetCredentialEnv(env),
  };
}

/**
 * Everything of implement that needs a token, before the model runs: the clones, the open-PR and
 * remote-branch checks, the assigned branch, the pre-restore that fills the package cache the
 * coder role will build from. An open PR, a foreign branch or any failure ends the run here.
 */
export async function prepareImplement(req: CodeFixRequest, deps: PrepareDeps): Promise<PrepareHandoff> {
  const { env, paths } = deps;
  const h = newPrepareHandoff(req.attempt_id, 'implement', deps.startedAt);
  const deviations: string[] = [];
  const end = (r: Partial<ImplementResult>): PrepareHandoff => {
    const result = minimalFailed('implement', req.attempt_id, 'implement did not complete') as ImplementResult;
    return { ...h, deviations, terminal: { ...result, branch: req.repository.branch, base_commit: h.base_commit, ...r } };
  };
  const plan = req.plan;
  try {
    if (!plan) return end({ error: 'implement phase requires request.plan' });
    if (plan.outcome !== 'planned') return end({ error: `the plan's outcome is ${plan.outcome}; only a planned result can be implemented` });
    if (plan.needs_cait) return end({ error: 'the plan needs a Cait change: multi-repository fixes are not implemented in v1 (Cait first, by a human)' });
    if (!BRANCH_RE.test(req.repository.branch)) return end({ error: `assigned branch ${req.repository.branch} is not a hephaisto/codefix-<id> branch` });

    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    h.context_sha = ctx.contextSha;
    const repo = enabledRepo(ctx.repos, req.repository.url);
    if (!repo) {
      log.warn(`${req.repository.url}: ${NOT_ENABLED}; the agent is not started`);
      return end({ error: NOT_ENABLED });
    }
    const target = await cloneTarget(req, ctx.repos, repo, env, paths, deps.abort.signal);
    const git = target.git;
    const base = await git.head();
    h.base_commit = base;
    h.repo_dir = repoDirName(repo);
    h.claude_md = target.claudeMd;
    const gh = ghEnv(env, paths.home);

    // idempotency: an open PR from the assigned branch means a previous Job finished the work
    const existing = await findOpenPr(req.repository.url, req.repository.branch, gh, target.dir);
    if (existing) {
      log.info(`an open PR already exists for ${req.repository.branch}: ${existing.url}; the agent is not started`);
      return end({ outcome: 'already_exists', pr_url: existing.url, pr_number: existing.number, error: null });
    }
    // publish asks the remote again before it pushes; this one stops a run that could never push
    const remote = await inspectRemoteBranch(git, req.repository.branch, req.repository.default_branch, req.attempt_id);
    if (remote.kind === 'foreign') return end({ error: remote.reason });
    if (remote.kind === 'reset') deviations.push(`The branch ${req.repository.branch} already existed from an earlier run of this attempt; it was rebuilt from ${req.repository.default_branch} and replaced.`);

    await sanitizeTarget(target);
    await prepareCait(target, req.repository.url, ctx.repos, env, paths, deps.abort.signal);
    ensureNugetConfig(target, paths);
    await git.ok(['switch', '--quiet', '-c', req.repository.branch]);

    let mainMoved = `The default branch has not been compared with the analysed commit.`;
    if (plan.analysed_ref && /^[0-9a-f]{40}$/.test(plan.analysed_ref)) {
      if (!(await git.hasCommit(plan.analysed_ref))) await git.try(['fetch', '--quiet', '--filter=blob:none', 'origin', plan.analysed_ref]);
      const count = await git.try(['rev-list', '--count', `${plan.analysed_ref}..HEAD`]);
      const isAncestor = (await git.try(['merge-base', '--is-ancestor', plan.analysed_ref, 'HEAD'])).code === 0;
      if (count.code === 0) {
        const n = Number(count.stdout.trim());
        mainMoved = isAncestor
          ? `\`${req.repository.default_branch}\` moved ${n} commit${n === 1 ? '' : 's'} since the analysed commit.`
          : `The analysed commit is not an ancestor of \`${req.repository.default_branch}\` (it has ${n} commit${n === 1 ? '' : 's'} the analysed commit lacks); re-check every file:line.`;
      } else {
        mainMoved = `The analysed commit ${plan.analysed_ref} is not in the repository; re-check every file:line against the current code.`;
      }
    }
    target.notes.push(mainMoved);

    // The pre-restore: the ONE moment a package feed is asked with credentials. The agent's shell
    // has none, and neither has the verification that follows it - both build from this cache.
    if (repo.commands.restore) {
      const pre = await runVerification(repo, { cwd: target.dir, env: driverChildEnv(env, paths), deadline: deps.deadline, signal: deps.abort.signal, steps: ['restore'] });
      if (pre.failed) {
        const refused = feedRefusal(pre.logTail);
        deviations.push(
          `The driver's pre-restore failed (exit ${pre.failed.exit}); the agent worked without restored packages` +
            (refused
              ? `, and its output shows a package feed refusing the request (${refused}) - is NUGET_GITHUB_TOKEN in the coder Secret? Verification has no feed credentials at all and will fail the same way.`
              : '.'),
        );
      }
    }

    h.cait = target.caitDir !== null;
    h.notes = [...target.notes];
    h.deviations = deviations;
    h.main_moved = mainMoved;
    h.protected_globs = protectedGlobs(ctx.repos, repo);
    h.pr = { assignee: ctx.repos.defaults.pr.assignee, labels: ctx.repos.defaults.pr.labels, template: loadTemplate(subjectOf(req).templates.prBody, paths.context).text };
    return h;
  } catch (e) {
    log.error(`implement could not be prepared: ${(e as Error).stack ?? String(e)}`);
    return end({ error: (e as Error).message, deviations });
  }
}

/**
 * The agent, and then everything that executes what the agent wrote. What comes out is a
 * CoderHandoff and never a push: either `publish: false` with the outcome, or - verification
 * green - the branch as a bundle for the publish role to check for itself.
 */
export async function coderImplement(req: CodeFixRequest, deps: PhaseDeps, h: PrepareHandoff): Promise<CoderHandoff> {
  const { env, paths } = deps;
  const result = minimalFailed('implement', req.attempt_id, 'implement did not complete') as ImplementResult;
  result.branch = req.repository.branch;
  result.base_commit = h.base_commit;
  const deviations: string[] = [...h.deviations];
  const handoff = (r: Partial<ImplementResult>, publish?: Pick<CoderHandoff, 'head' | 'change_summary' | 'report'>): CoderHandoff => ({
    handoff_version: HANDOFF_VERSION,
    role: 'coder',
    attempt_id: req.attempt_id,
    publish: publish !== undefined,
    result: { ...result, deviations, ...r },
    head: publish?.head ?? null,
    change_summary: publish?.change_summary ?? '',
    report: publish?.report ?? null,
  });
  try {
    const plan = req.plan;
    const base = h.base_commit;
    if (!plan || !base) return handoff({ error: 'the prepare handoff carries no plan or no base commit' });
    const { repos, repo, target } = openPrepared(req.repository.url, h, env, paths, deps.abort.signal);
    const git = target.git;

    const guard: GuardContext = {
      targetDir: realish(target.dir),
      protectedGlobs: protectedGlobs(repos, repo),
      allowedBranch: req.repository.branch,
      homeDir: paths.home,
    };
    const planFiles = plan.files.length ? plan.files.map((f) => `- \`${f}\``).join('\n') : '- (the plan named none)';
    const vars = {
      ...baseVars(req, repo, target, paths),
      analysed_ref: plan.analysed_ref ?? '(unknown)',
      main_moved: h.main_moved ?? '',
      plan_json: fencedJson(plan),
      plan_steps: plan.steps.map((s, i) => `${i + 1}. ${s}`).join('\n'),
      plan_files: planFiles,
      result_schema: JSON.stringify(implementOutputSchema(), null, 2),
    };
    const prompt = render(loadTemplate(subjectOf(req).templates.implement, paths.context).text, vars);
    const firstFile = plan.files[0] ?? 'README.md';
    const query = await deps.makeQuery({
      repoName: repo.name,
      vars: { ...vars, target: target.dir, plan_file: firstFile, line_comment: lineComment(firstFile) },
    });
    const schema = implementOutputSchema();
    const agent = await runAgent<AgentImplement>({
      phase: 'implement',
      prompt,
      cwd: target.dir,
      env: buildAgentEnv(env, paths, guardEnvFor('implement', guard)),
      additionalDirectories: additionalDirs(paths, target),
      maxTurns: IMPLEMENT_MAX_TURNS,
      maxBudgetUsd: req.budget.max_cost_usd,
      outputSchema: schema,
      validate: (v) => validateWith(schema, v),
      guard,
      deadline: deps.deadline,
      abort: deps.abort,
      query,
      model: env.model,
      claudeExecutable: env.claudeExecutable,
    });
    result.cost_usd = agent.costUsd;
    result.session_id = agent.sessionId;
    result.denied_tool_calls = agent.denials;
    if (!agent.ok || !agent.output) return handoff({ error: agent.error });
    deviations.push(...agent.output.deviations);

    // the agent must still be on its branch, on top of the base it was given
    const branchNow = await git.currentBranch();
    if (branchNow !== req.repository.branch) return handoff({ error: `the agent left the assigned branch (now on ${branchNow}); nothing was pushed` });
    if ((await git.try(['merge-base', '--is-ancestor', base, 'HEAD'])).code !== 0) {
      return handoff({ error: 'the branch no longer descends from the default branch HEAD it was created from; nothing was pushed' });
    }

    // anything the agent left uncommitted becomes one final commit with the trailers
    const status = await git.ok(['status', '--porcelain']);
    if (status.trim()) {
      await git.ok(['add', '-A']);
      const staged = await git.try(['diff', '--cached', '--quiet']);
      if (staged.code !== 0) {
        await git.ok(['commit', '--quiet', '--no-verify', '-F', '-'], {
          input: `chore(hephaisto): commit changes the agent left uncommitted\n\n${trailersOf(req)}\n`,
        });
        deviations.push('The agent left uncommitted changes; the driver committed them.');
      }
    }
    await ensureTrailers(git, base, req);

    const files = await changedFiles(git, base, 'HEAD');
    result.files = files;
    if (files.length === 0) return handoff({ outcome: 'no_changes', error: null, log_tail: 'The agent made no changes.' });
    const claimed = relFiles(agent.output.files, target.dir);
    const unclaimed = files.filter((f) => !claimed.includes(f));
    if (unclaimed.length > 0) deviations.push(`The diff also touches files the agent did not report: ${unclaimed.join(', ')}.`);

    // Checked here so that a violation costs no build. publish checks it again, on its own copy.
    const head = await git.head();
    const policy = await policyCheck(git, base, head, files, guard.protectedGlobs);
    if (!policy.ok) return handoff({ outcome: 'policy_diff', error: `the diff violates the publishing policy: ${policy.reasons.join('; ')}` });

    const report: VerificationReport = await runVerification(repo, { cwd: target.dir, env: driverChildEnv(env, paths), deadline: deps.deadline, signal: deps.abort.signal });
    result.build_passed = report.buildPassed;
    result.tests_passed = report.testsPassed;
    result.log_tail = report.logTail;
    if (report.failed) {
      const outcome = report.failed.name === 'test' ? 'tests_failed' : 'build_failed';
      const refused = feedRefusal(report.logTail);
      if (refused) deviations.push(feedRefusalNote(report.failed.name, refused));
      deviations.push(await savePatch(git, base, paths));
      return handoff({
        outcome,
        error:
          `${report.failed.name} failed (exit ${report.failed.exit}${report.failed.timedOut ? ', timed out' : ''}): \`${report.failed.command}\`; nothing was pushed` +
          (refused ? '. It could not get a package: verification has no feed credentials and builds only from what the pre-restore cached (see deviations)' : ''),
      });
    }

    // Green. The branch leaves this container as a file; the role that holds the token never
    // opens this repository.
    if ((await git.head()) !== head) return handoff({ error: 'the branch moved while it was being verified; nothing was pushed' });
    mkdirSync(env.handoffDir, { recursive: true });
    const bundle = join(env.handoffDir, BUNDLE_FILE);
    rmSync(bundle, { force: true });
    await git.ok(['bundle', 'create', '--quiet', bundle, `${base}..refs/heads/${req.repository.branch}`]);
    return handoff(
      { error: 'verified, and handed to the publish role, which has not reported' },
      { head, change_summary: agent.output.summary ?? '', report },
    );
  } catch (e) {
    log.error(`implement failed: ${(e as Error).stack ?? String(e)}`);
    return handoff({ error: (e as Error).message });
  }
}

/** A red build is not pushed; the change still reaches a human, via the pod log (≤ 1 MB, before the result block). */
async function savePatch(git: Git, base: string, paths: WorkPaths): Promise<string> {
  const patch = await git.ok(['format-patch', '--stdout', `${base}..HEAD`]);
  const bytes = Buffer.byteLength(patch, 'utf8');
  const file = join(paths.out, 'changes.patch');
  writeFileSync(file, patch);
  if (bytes > PATCH_MAX_BYTES) return `The patch (${bytes} bytes) exceeds 1 MB and was not attached; nothing was pushed.`;
  process.stderr.write(`---HEPHAISTO-PATCH-BEGIN bytes=${bytes}---\n${patch}\n---HEPHAISTO-PATCH-END---\n`);
  return `The unpushed patch (${bytes} bytes) is in the coder container's log between ---HEPHAISTO-PATCH-BEGIN/END--- markers.`;
}
