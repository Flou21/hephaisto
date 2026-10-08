import { delimiter } from 'node:path';
import type { RunnerEnv } from './config.js';
import { type ExecResult, run } from './exec.js';
import type { Git } from './git.js';
import { log } from './log.js';
import { evidenceMarkdown, fence, inert, render } from './prompts.js';
import type { CodeFixRequest, PlanResult } from './schemas.js';
import { isWorkItem, prType, subjectOf } from './subject.js';
import { type VerificationReport, verificationTable } from './verify.js';

// Everything that talks to GitHub. The agent never reaches any of it: `gh` is denied by the
// guard, and since #116 GITHUB_TOKEN is not in its container at all - the open-PR and
// remote-branch checks run in the prepare role, the push and the PR in the publish role.

export const BRANCH_RE = /^hephaisto\/codefix-[0-9a-f]{12}$/;

export function ghEnv(env: RunnerEnv, home: string): NodeJS.ProcessEnv {
  const e: NodeJS.ProcessEnv = {
    PATH: env.ghMode === 'shim' ? `${env.ghShimDir}${delimiter}${env.base.PATH ?? ''}` : env.base.PATH,
    HOME: home,
    GH_CONFIG_DIR: `${home}/.config/gh`,
    GH_PROMPT_DISABLED: '1',
    GH_NO_UPDATE_NOTIFIER: '1',
    GH_SPINNER_DISABLED: '1',
    NO_COLOR: '1',
  };
  for (const [k, v] of Object.entries(env.base)) {
    if (k.startsWith('GH_SHIM_') || /^(HTTPS?_PROXY|NO_PROXY|https?_proxy|no_proxy|TMPDIR)$/.test(k)) e[k] = v;
  }
  if (env.githubToken) e.GH_TOKEN = env.githubToken;
  return e;
}

/** `https://github.com/org/repo(.git)` → the form gh accepts for --repo. */
export function ghRepoArg(url: string): string {
  return url.replace(/\.git$/, '').replace(/\/+$/, '');
}

async function gh(args: string[], env: NodeJS.ProcessEnv, cwd: string): Promise<ExecResult> {
  log.info(`gh ${args.filter((a) => !a.startsWith('--body')).slice(0, 8).join(' ')}`);
  return run('gh', args, { env, cwd, timeoutMs: 120_000 });
}

export interface ExistingPr {
  number: number;
  url: string;
}

export async function findOpenPr(repoUrl: string, branch: string, env: NodeJS.ProcessEnv, cwd: string): Promise<ExistingPr | null> {
  const r = await gh(['pr', 'list', '--repo', ghRepoArg(repoUrl), '--head', branch, '--state', 'open', '--json', 'number,url,headRefName'], env, cwd);
  if (r.code !== 0) throw new Error(`gh pr list failed: ${(r.stderr || r.stdout).trim().slice(-500)}`);
  const list = JSON.parse(r.stdout || '[]') as { number: number; url: string; headRefName: string }[];
  const hit = list.find((p) => p.headRefName === branch);
  return hit ? { number: hit.number, url: hit.url } : null;
}

export type BranchState = { kind: 'fresh' } | { kind: 'reset'; lease: string } | { kind: 'foreign'; reason: string };

/**
 * The remote branch, if it exists without an open PR, is ours to reuse only if EVERY commit on
 * it (past the default branch) carries this attempt's trailer - a retried Job. Anything else on
 * that branch is someone's work and is never overwritten.
 */
export async function inspectRemoteBranch(git: Git, branch: string, defaultBranch: string, attemptId: string): Promise<BranchState> {
  const ls = await git.try(['ls-remote', '--heads', 'origin', `refs/heads/${branch}`]);
  if (ls.code !== 0) throw new Error(`git ls-remote failed: ${ls.stderr.trim().slice(-300)}`);
  const line = ls.stdout.trim();
  if (!line) return { kind: 'fresh' };
  const remoteSha = line.split(/\s+/)[0]!;
  const fetched = await git.try(['fetch', '--quiet', 'origin', `+refs/heads/${branch}:refs/remotes/origin/${branch}`]);
  if (fetched.code !== 0) return { kind: 'foreign', reason: `remote branch ${branch} exists and could not be fetched` };
  const log = await git.ok(['log', '--format=%H%x1f%(trailers:key=Hephaisto-Attempt,valueonly,separator=%x2c)%x1e', `origin/${defaultBranch}..${remoteSha}`]);
  const commits = log.split('\x1e').map((s) => s.trim()).filter(Boolean);
  if (commits.length === 0) return { kind: 'reset', lease: remoteSha };
  const foreign = commits.filter((c) => !(c.split('\x1f')[1] ?? '').split(',').map((s) => s.trim()).includes(attemptId));
  if (foreign.length > 0) {
    return { kind: 'foreign', reason: `remote branch ${branch} has ${foreign.length} commit(s) without Hephaisto-Attempt: ${attemptId}; refusing to overwrite it` };
  }
  return { kind: 'reset', lease: remoteSha };
}

export const REMOTE_URL_RE = /^(https?|file):\/\//;

/**
 * Pushes ONE commit to the assigned branch and nothing else: the URL is the request's, never a
 * remote name somebody could have re-pointed, and the source is a commit id the caller has just
 * checked, never a ref that could have moved since. A lease is only used to replace this
 * attempt's own earlier push.
 */
export async function pushBranch(git: Git, url: string, sha: string, branch: string, assigned: string, lease?: string): Promise<void> {
  if (branch !== assigned || !BRANCH_RE.test(branch)) throw new Error(`refusing to push ${branch}: only ${assigned} may be pushed`);
  if (!REMOTE_URL_RE.test(url)) throw new Error('refusing to push: the repository URL is not http(s) or file');
  if (!/^[0-9a-f]{40,64}$/.test(sha)) throw new Error('refusing to push: the source is not a commit id');
  const args = ['push', '--porcelain', '--no-verify', '--no-recurse-submodules'];
  if (lease) args.push(`--force-with-lease=refs/heads/${branch}:${lease}`);
  args.push(url, `${sha}:refs/heads/${branch}`);
  await git.ok(args, { timeoutMs: 5 * 60_000 });
  log.info(`pushed ${sha} to ${branch}${lease ? ' (replacing this attempt\'s earlier push)' : ''}`);
}

export interface CreatedPr {
  number: number;
  url: string;
  deviations: string[];
}

export async function createDraftPr(
  opts: {
    repoUrl: string;
    base: string;
    head: string;
    title: string;
    bodyFile: string;
    assignee: string;
    labels: string[];
    /**
     * The description again, for a pull request that is opened with something missing: given
     * what is missing, it returns the file of a body that says so. Without it the body is the
     * one that was written before anybody knew.
     */
    bodyFileWith?: (deviations: string[]) => string;
  },
  env: NodeJS.ProcessEnv,
  cwd: string,
): Promise<CreatedPr> {
  const args = (bodyFile: string): string[] => {
    const a = ['pr', 'create', '--repo', ghRepoArg(opts.repoUrl), '--draft', '--base', opts.base, '--head', opts.head, '--title', opts.title, '--body-file', bodyFile];
    if (opts.assignee) a.push('--assignee', opts.assignee);
    return a;
  };
  const deviations: string[] = [];
  let r = await gh([...args(opts.bodyFile), ...opts.labels.flatMap((l) => ['--label', l])], env, cwd);
  if (r.code !== 0 && opts.labels.length > 0) {
    // Creating a missing label is not the runner's business; open the PR without it and say so
    // - in the result, and in the description: the first pull request that lost its label
    // (2026-10-08, a repository without one named hephaisto) said "Deviations: none".
    const why = (r.stderr || r.stdout).trim().split('\n').pop() ?? '';
    log.warn(`gh pr create with labels failed (${why}); retrying without labels`);
    deviations.push(`The PR was opened without the label(s) ${opts.labels.join(', ')}: labelling failed (${why.slice(0, 300)}).`);
    r = await gh(args(opts.bodyFileWith ? opts.bodyFileWith(deviations) : opts.bodyFile), env, cwd);
  }
  if (r.code !== 0) throw new Error(`gh pr create failed: ${(r.stderr || r.stdout).trim().slice(-800)}`);
  const url = r.stdout.trim().split('\n').filter(Boolean).pop() ?? '';
  const m = /\/pull\/(\d+)\s*$/.exec(url);
  if (!m) throw new Error(`gh pr create printed no PR URL: ${r.stdout.slice(-300)}`);
  return { number: Number(m[1]), url, deviations };
}

export interface PrBodyInput {
  req: CodeFixRequest;
  plan: PlanResult;
  changeSummary: string;
  files: string[];
  deviations: string[];
  notes: string[];
  report: VerificationReport;
  costUsd: number;
  versions: string;
  /** The pr-body template's text. The caller loads it; publish gets it sealed by prepare and never reads dev-context. */
  template: string;
}

export function renderPrBody(i: PrBodyInput): string {
  const { req, plan } = i;
  const bullets = (xs: string[], empty: string) => (xs.length ? xs.map((x) => `- ${x}`).join('\n') : empty);
  const weak =
    i.report.level !== 'tests'
      ? [
          '### Verification weak',
          '',
          `The runner could only verify this change at level \`${i.report.level}\`. Not verifiable here:`,
          '',
          bullets(
            plan.verification.not_verifiable.length > 0 ? plan.verification.not_verifiable : ['(the plan listed nothing; a reviewer should decide what to check before merging)'],
            '',
          ),
        ].join('\n')
      : '';
  const common = {
    files: bullets(i.files.map((f) => `\`${f}\``), '- (none)'),
    verification_table: verificationTable(i.report),
    cost: `$${i.costUsd.toFixed(2)} (implement phase, API-equivalent estimate)`,
    versions: i.versions,
    attempt_id: req.attempt_id,
    analysed_ref: plan.analysed_ref ?? '(unknown)',
    branch: req.repository.branch,
    repo_url: req.repository.url,
  };
  // What a model wrote is made inert (prompts.ts) for BOTH kinds of request. GitHub reads a
  // pull request's body for closing keywords, references and mentions whoever it was opened
  // for: until v0.14.0 only an issue's description was treated, and an incident's carried the
  // model's summary and root cause as written - so a model repeating "fixes #<n>" from a log
  // line would have closed that issue on merge, in a repository whose issues are real.
  const model = {
    summary: inert(withoutComparison(plan.summary)),
    root_cause: inert(plan.root_cause),
    change_summary: inert(i.changeSummary || withoutComparison(plan.summary)),
    deviations: bullets(i.deviations.map(inert), '- none'),
    verification_weak: weak ? inert(weak) : '',
    notes: bullets([...plan.notes, ...i.notes].map(inert), '- none'),
  };
  if (isWorkItem(req)) {
    // This one is for an issue anybody may have opened. So: the one `Closes` is the template's
    // line, built from issue_ref - the runner's own, and not made inert; the issue's title is
    // in a fence, where nothing is linked; and its body is not here at all.
    return render(i.template, {
      ...common,
      ...model,
      issue_ref: subjectOf(req).ref,
      issue_url: req.work_item.url,
      issue_md: fence(req.work_item.title),
      default_branch: req.repository.default_branch,
    });
  }
  return render(i.template, {
    ...common,
    ...model,
    // Hephaisto's own words, and an id in a code span: as written.
    incident_link: `Hephaisto incident \`${req.incident_id}\``,
    // An alert's words - an annotation, a label - outside any fence: inert like the model's.
    incident_title: inert(req.incident.title),
    // Verbatim evidence is fenced (evidenceMarkdown), and GitHub links nothing inside a fence.
    evidence_md: evidenceMarkdown(req),
    incident_id: req.incident_id,
    workload: req.incident.target.workload,
    image: req.incident.image ?? '(unknown)',
  });
}

/** How a plan that replaces an earlier one begins the paragraph that says what changed (prompts/replan-block.md). */
export const COMPARISON = 'Compared with the earlier plan:';

/**
 * A plan's summary as a pull request's description: without its last paragraph when that
 * paragraph compares the plan with an earlier one. On the issue that paragraph is what a person
 * who read the earlier plan looks for. A reviewer of the pull request never saw the earlier
 * plan - the first one that came from a replanned issue (2026-10-08) opened with "What changed
 * since the earlier plan: ..." and "Nothing is left open". From the marker to the end, and the
 * whole summary when nothing would be left.
 */
export function withoutComparison(summary: string): string {
  const at = summary.toLowerCase().lastIndexOf(COMPARISON.toLowerCase());
  const kept = at > 0 ? summary.slice(0, at).trimEnd() : summary;
  return kept.length > 0 ? kept : summary;
}

/**
 * `fix(<workload>): <first sentence of the plan's summary>` for an incident, and for an issue
 * `<type>: <the same>`, where the type is the issue's kind (subject.ts prType) - an issue names
 * no workload to scope by. Capped at 120 characters either way.
 */
export function prTitle(req: CodeFixRequest, plan: PlanResult, firstCommitSubject: string | null = null): string {
  const first = (plan.summary.split(/(?<=[.!?])\s/)[0] ?? plan.summary).trim().replace(/\s+/g, ' ');
  const prefix = isWorkItem(req) ? prType(req.work_item.type, firstCommitSubject) : `fix(${req.incident.target.workload.split('/').pop() || 'service'})`;
  // a title notifies and links like any other text: it is made inert as the body is, for an
  // incident as for an issue (the prefix is the runner's: a type, and a workload's name)
  const sentence = inert(first);
  const t = `${prefix}: ${lowerFirstWord(sentence)}`.replace(/\.$/, '');
  return t.length > 120 ? `${t.slice(0, 117)}...` : t;
}

/**
 * A conventional title goes on in lower case after its type - but only a word that is
 * capitalised because it starts the sentence is lowered: one upper-case letter, then lower-case
 * ones ("The loop ..." -> "the loop ..."). An acronym (HTTP), an identifier
 * (Endpoints.Primary, NullReferenceException) and a word with a digit or a capital inside it
 * are names, and "hTTP client" is not one. The first real pull requests on github.com were
 * titled "fix: fAKE SDK plan: ...".
 */
export function lowerFirstWord(sentence: string): string {
  const word = /^\S+/.exec(sentence)?.[0] ?? '';
  return /^\p{Lu}[\p{Ll}'\u2019-]*[,;:]?$/u.test(word) ? `${sentence.charAt(0).toLowerCase()}${sentence.slice(1)}` : sentence;
}
