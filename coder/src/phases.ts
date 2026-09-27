import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { type AgentRunResult, buildAgentEnv, guardEnvFor, runAgent } from './agent.js';
import { APP_ROOT, BINARY_MAX_BYTES, IMPLEMENT_MAX_TURNS, PATCH_MAX_BYTES, PLAN_MAX_TURNS, type RunnerEnv, type WorkPaths } from './config.js';
import { Git, trailers } from './git.js';
import { type GuardContext, matchesProtected, realish } from './guard.js';
import { log } from './log.js';
import { BRANCH_RE, createDraftPr, findOpenPr, ghEnv, inspectRemoteBranch, prTitle, pushBranch, renderPrBody } from './pr.js';
import { fencedJson, loadTemplate, render, renderEvidenceBlock, repoNotesBlock } from './prompts.js';
import { NOT_ENABLED, enabledRepo, protectedGlobs } from './repos.js';
import { minimalFailed } from './result.js';
import { type CodeFixRequest, type ImplementResult, type PlanResult, type RepoEntry, type Repos, rawSchema, validateWith } from './schemas.js';
import type { QueryFn } from './sdk.js';
import { type VerificationReport, runVerification } from './verify.js';
import { type Target, checkoutAnalysedRef, cloneTarget, ensureNugetConfig, makeDirs, nugetCredentialEnv, prepareCait, prepareContext, sanitizeTarget } from './workspace.js';

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
  return {
    attempt_id: req.attempt_id,
    incident_id: req.incident_id,
    phase: req.phase,
    repo_url: req.repository.url,
    repo_name: repo.name,
    default_branch: req.repository.default_branch,
    branch: req.repository.branch,
    image: req.incident.image ?? '(unknown)',
    incident_title: req.incident.title,
    incident_kind: req.incident.kind,
    incident_severity: req.incident.severity,
    workload: req.incident.target.workload,
    escalation_reason: req.incident.escalation_reason,
    evidence_block: renderEvidenceBlock(req, paths.context),
    // investigation_summary is log-derived: it lives INSIDE the evidence element, never here
    investigation_summary: '',
    commands: commandsBlock(repo),
    verification_level: expectedLevel(repo),
    repo_notes: repoNotesBlock(target.claudeMd),
    cait_ref: target.caitDir ? join(paths.ref, 'Cait') : '(none: this repository does not pin Cait)',
    memory_dir: join(paths.context, 'memory'),
    workspace: target.dir,
  };
}

function additionalDirs(paths: WorkPaths, target: Target): string[] {
  const dirs = [join(paths.context, 'memory')];
  if (target.caitDir) dirs.unshift(join(paths.ref, 'Cait'));
  return dirs.filter((d) => existsSync(d));
}

/** Evidence-named file for the fake scripts: a path in an excerpt, else a type name from a stack frame, else the first tracked file. */
async function firstEvidenceFile(req: CodeFixRequest, git: Git): Promise<string> {
  const files = (await git.ok(['ls-files'])).split('\n').filter(Boolean);
  const text = [req.investigation_summary ?? '', ...req.findings.flatMap((f) => [f.hypothesis, ...f.evidence.map((e) => e.excerpt)])].join('\n');
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

function lineComment(file: string): string {
  if (/\.(cs|ts|js|java|go|vue|kt|swift|c|cpp|h)$/.test(file)) return '//';
  if (/\.(md|html|xml|csproj)$/.test(file)) return '<!--';
  return '#';
}

function relFiles(files: string[], targetDir: string): string[] {
  return files.map((f) => (f.startsWith(targetDir) ? relative(targetDir, f) : f.replace(/^\.\//, '')));
}

function contextDenied(req: CodeFixRequest, repos: Repos): RepoEntry | null {
  return enabledRepo(repos, req.repository.url);
}

// =============================================================================================
// plan

export async function runPlan(req: CodeFixRequest, deps: PhaseDeps): Promise<PlanResult> {
  const { env, paths } = deps;
  const result = minimalFailed('plan', req.attempt_id, 'plan did not complete') as PlanResult;
  try {
    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    result.context_sha = ctx.contextSha;
    const repo = contextDenied(req, ctx.repos);
    if (!repo) {
      log.warn(`${req.repository.url}: ${NOT_ENABLED}; the agent is not started`);
      result.error = NOT_ENABLED;
      return result;
    }
    const target = await cloneTarget(req, ctx.repos, repo, env, paths, deps.abort.signal);
    result.analysed_ref = await checkoutAnalysedRef(target, req.incident.image, req.repository.default_branch);
    await sanitizeTarget(target);
    await prepareCait(target, req.repository.url, ctx.repos, env, paths, deps.abort.signal);

    const vars = { ...baseVars(req, repo, target, paths), analysed_ref: result.analysed_ref ?? '', result_schema: JSON.stringify(planOutputSchema(), null, 2) };
    const prompt = render(loadTemplate('plan', paths.context).text, vars);
    const guard: GuardContext = { targetDir: realish(target.dir), protectedGlobs: protectedGlobs(ctx.repos, repo), homeDir: paths.home };
    const query = await deps.makeQuery({
      repoName: repo.name,
      vars: { ...vars, target: target.dir, first_evidence_file: await firstEvidenceFile(req, target.git) },
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

const SECRET_IN_DIFF = /ghp_|github_pat_|sk-ant-/;

export interface PolicyVerdict {
  ok: boolean;
  reasons: string[];
}

/** The diff the driver is about to publish, checked by the driver: no protected paths, no big binaries, no credential shapes. */
export async function policyCheck(git: Git, base: string, files: string[], globs: string[]): Promise<PolicyVerdict> {
  const reasons: string[] = [];
  for (const f of files) {
    const hit = matchesProtected(f, globs);
    if (hit) reasons.push(`${f} is a protected path (${hit})`);
  }
  const numstat = await git.ok(['diff', '--numstat', '--no-renames', `${base}..HEAD`]);
  for (const line of numstat.split('\n').filter(Boolean)) {
    const [add, del, path] = line.split('\t');
    if (add === '-' && del === '-' && path) {
      const size = await git.try(['cat-file', '-s', `HEAD:${path}`]);
      if (size.code === 0 && Number(size.stdout.trim()) > BINARY_MAX_BYTES) reasons.push(`${path} is a binary larger than 1 MB`);
    }
  }
  const diff = await git.ok(['diff', '--no-color', '--no-ext-diff', `${base}..HEAD`]);
  const added = diff.split('\n').filter((l) => l.startsWith('+') && !l.startsWith('+++'));
  if (added.some((l) => SECRET_IN_DIFF.test(l))) reasons.push('the diff adds a line that looks like a credential (ghp_/github_pat_/sk-ant-)');
  return { ok: reasons.length === 0, reasons };
}

/** Every commit on the branch carries this attempt's trailers, so a retried Job can recognise its own work. */
async function ensureTrailers(git: Git, base: string, req: CodeFixRequest): Promise<void> {
  const commits = (await git.ok(['log', '--format=%H%x1f%(trailers:key=Hephaisto-Attempt,valueonly,separator=%x2c)', `${base}..HEAD`])).split('\n').filter(Boolean);
  if (commits.every((c) => (c.split('\x1f')[1] ?? '').includes(req.attempt_id))) return;
  log.info('adding Hephaisto trailers to the agent\'s commits that lack them');
  // ids are schema-validated uuids, safe to put in the exec line
  const exec = `git -c trailer.ifexists=addIfDifferent commit --amend --no-edit --no-verify --quiet --trailer 'Hephaisto-Incident: ${req.incident_id}' --trailer 'Hephaisto-Attempt: ${req.attempt_id}'`;
  await git.ok(['rebase', '--quiet', '--exec', exec, base]);
}

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
    // the TR nuget.config files read these two; set for the driver's own restore/build/test only
    ...nugetCredentialEnv(env),
  };
}

export async function runImplement(req: CodeFixRequest, deps: PhaseDeps): Promise<ImplementResult> {
  const { env, paths } = deps;
  const result = minimalFailed('implement', req.attempt_id, 'implement did not complete') as ImplementResult;
  result.branch = req.repository.branch;
  const deviations: string[] = [];
  const plan = req.plan;
  try {
    if (!plan) {
      result.error = 'implement phase requires request.plan';
      return result;
    }
    if (plan.outcome !== 'planned') {
      result.error = `the plan's outcome is ${plan.outcome}; only a planned result can be implemented`;
      return result;
    }
    if (plan.needs_cait) {
      result.error = 'the plan needs a Cait change: multi-repository fixes are not implemented in v1 (Cait first, by a human)';
      return result;
    }
    if (!BRANCH_RE.test(req.repository.branch)) {
      result.error = `assigned branch ${req.repository.branch} is not a hephaisto/codefix-<id> branch`;
      return result;
    }
    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    const repo = contextDenied(req, ctx.repos);
    if (!repo) {
      log.warn(`${req.repository.url}: ${NOT_ENABLED}; the agent is not started`);
      result.error = NOT_ENABLED;
      return result;
    }
    const target = await cloneTarget(req, ctx.repos, repo, env, paths, deps.abort.signal);
    const git = target.git;
    const base = await git.head();
    result.base_commit = base;
    const gh = ghEnv(env, paths.home);

    // idempotency: an open PR from the assigned branch means a previous Job finished the work
    const existing = await findOpenPr(req.repository.url, req.repository.branch, gh, target.dir);
    if (existing) {
      log.info(`an open PR already exists for ${req.repository.branch}: ${existing.url}; the agent is not started`);
      return { ...result, outcome: 'already_exists', pr_url: existing.url, pr_number: existing.number, error: null };
    }
    const remote = await inspectRemoteBranch(git, req.repository.branch, req.repository.default_branch, req.attempt_id);
    if (remote.kind === 'foreign') {
      result.error = remote.reason;
      return result;
    }
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

    // pre-restore in a driver child: the agent's shell has no feed credentials
    const childEnv = driverChildEnv(env, paths);
    if (repo.commands.restore) {
      const pre = await runVerification(repo, { cwd: target.dir, env: childEnv, deadline: deps.deadline, signal: deps.abort.signal, steps: ['restore'] });
      if (pre.failed) deviations.push(`The driver's pre-restore failed (exit ${pre.failed.exit}); the agent worked without restored packages.`);
    }

    const guard: GuardContext = {
      targetDir: realish(target.dir),
      protectedGlobs: protectedGlobs(ctx.repos, repo),
      allowedBranch: req.repository.branch,
      homeDir: paths.home,
    };
    const planFiles = plan.files.length ? plan.files.map((f) => `- \`${f}\``).join('\n') : '- (the plan named none)';
    const vars = {
      ...baseVars(req, repo, target, paths),
      analysed_ref: plan.analysed_ref ?? '(unknown)',
      main_moved: mainMoved,
      plan_json: fencedJson(plan),
      plan_steps: plan.steps.map((s, i) => `${i + 1}. ${s}`).join('\n'),
      plan_files: planFiles,
      result_schema: JSON.stringify(implementOutputSchema(), null, 2),
    };
    const prompt = render(loadTemplate('implement', paths.context).text, vars);
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
    if (!agent.ok || !agent.output) {
      result.error = agent.error;
      result.deviations = deviations;
      return result;
    }
    deviations.push(...agent.output.deviations);

    // the agent must still be on its branch, on top of the base it was given
    const branchNow = await git.currentBranch();
    if (branchNow !== req.repository.branch) {
      result.error = `the agent left the assigned branch (now on ${branchNow}); nothing was pushed`;
      result.deviations = deviations;
      return result;
    }
    if ((await git.try(['merge-base', '--is-ancestor', base, 'HEAD'])).code !== 0) {
      result.error = 'the branch no longer descends from the default branch HEAD it was created from; nothing was pushed';
      result.deviations = deviations;
      return result;
    }

    // anything the agent left uncommitted becomes one final commit with the trailers
    const status = await git.ok(['status', '--porcelain']);
    if (status.trim()) {
      await git.ok(['add', '-A']);
      const staged = await git.try(['diff', '--cached', '--quiet']);
      if (staged.code !== 0) {
        await git.ok(['commit', '--quiet', '--no-verify', '-F', '-'], {
          input: `chore(hephaisto): commit changes the agent left uncommitted\n\n${trailers(req.incident_id, req.attempt_id)}\n`,
        });
        deviations.push('The agent left uncommitted changes; the driver committed them.');
      }
    }
    await ensureTrailers(git, base, req);

    const files = (await git.ok(['diff', '--name-only', '--no-renames', `${base}..HEAD`])).split('\n').filter(Boolean);
    result.files = files;
    if (files.length === 0) {
      return { ...result, outcome: 'no_changes', deviations, error: null, log_tail: 'The agent made no changes.' };
    }
    const claimed = relFiles(agent.output.files, target.dir);
    const unclaimed = files.filter((f) => !claimed.includes(f));
    if (unclaimed.length > 0) deviations.push(`The diff also touches files the agent did not report: ${unclaimed.join(', ')}.`);

    const policy = await policyCheck(git, base, files, guard.protectedGlobs);
    if (!policy.ok) {
      return { ...result, outcome: 'policy_diff', deviations, error: `the diff violates the publishing policy: ${policy.reasons.join('; ')}` };
    }

    const report: VerificationReport = await runVerification(repo, { cwd: target.dir, env: childEnv, deadline: deps.deadline, signal: deps.abort.signal });
    result.build_passed = report.buildPassed;
    result.tests_passed = report.testsPassed;
    result.log_tail = report.logTail;
    if (report.failed) {
      const outcome = report.failed.name === 'test' ? 'tests_failed' : 'build_failed';
      deviations.push(await savePatch(git, base, paths));
      return {
        ...result,
        outcome,
        deviations,
        error: `${report.failed.name} failed (exit ${report.failed.exit}${report.failed.timedOut ? ', timed out' : ''}): \`${report.failed.command}\`; nothing was pushed`,
      };
    }

    await pushBranch(git, req.repository.branch, req.repository.branch, remote.kind === 'reset' ? remote.lease : undefined);
    const bodyFile = join(paths.out, 'pr-body.md');
    const versions = versionLine(ctx.contextSha);
    writeFileSync(
      bodyFile,
      renderPrBody({
        req,
        plan,
        changeSummary: agent.output.summary ?? '',
        files,
        deviations,
        notes: target.notes,
        report,
        costUsd: agent.costUsd,
        versions,
        contextDir: paths.context,
      }),
    );
    const assignee = ctx.repos.defaults.pr.assignee;
    const pr = await createDraftPr(
      {
        repoUrl: req.repository.url,
        base: req.repository.default_branch,
        head: req.repository.branch,
        title: prTitle(req, plan),
        bodyFile,
        assignee,
        labels: ctx.repos.defaults.pr.labels,
      },
      gh,
      target.dir,
    );
    deviations.push(...pr.deviations);
    return { ...result, outcome: 'pr_opened', pr_url: pr.url, pr_number: pr.number, deviations, error: null };
  } catch (e) {
    log.error(`implement failed: ${(e as Error).stack ?? String(e)}`);
    result.error = (e as Error).message;
    result.deviations = deviations;
    return result;
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
  return `The unpushed patch (${bytes} bytes) is in the pod log between ---HEPHAISTO-PATCH-BEGIN/END--- markers.`;
}

export function versionLine(contextSha: string | null): string {
  let coder = '0.0.0-dev';
  let sdk = '?';
  let cli = '?';
  try {
    coder = (JSON.parse(readFileSync(join(APP_ROOT, 'package.json'), 'utf8')) as { version: string }).version;
    const lock = JSON.parse(readFileSync(join(APP_ROOT, 'package-lock.json'), 'utf8')) as { packages: Record<string, { version?: string }> };
    sdk = lock.packages['node_modules/@anthropic-ai/claude-agent-sdk']?.version ?? '?';
    cli = lock.packages['node_modules/@anthropic-ai/claude-code']?.version ?? '?';
  } catch {
    /* versions are informational */
  }
  return `hephaisto-coder ${process.env.CODER_VERSION ?? coder} · agent-sdk ${sdk} · claude-code ${cli} · dev-context ${contextSha ?? '?'}`;
}
