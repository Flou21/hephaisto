import { lstatSync, mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, sep } from 'node:path';
import type { RunnerEnv } from './config.js';
import { run } from './exec.js';
import { Git, gitEnv, withNetworkRetry } from './git.js';
import { BUNDLE_FILE, type CoderHandoff, HandoffError, type PrepareHandoff, copyUntrusted, readCoderHandoff } from './handoff.js';
import { log, redact, secretIn } from './log.js';
import { changedFiles, policyCheck } from './policy.js';
import { BRANCH_RE, REMOTE_URL_RE, createDraftPr, ghEnv, inspectRemoteBranch, prTitle, pushBranch, renderPrBody } from './pr.js';
import { minimalFailed, normalizeResult } from './result.js';
import type { CodeFixRequest, ImplementResult } from './schemas.js';
import { versionLine } from './version.js';

// The publish role (backlog #116): the push and the Draft PR, in a container that holds
// GITHUB_TOKEN and starts only after the coder container - the model, its shell, the tests it
// wrote - has ended.
//
// THE RULE: nothing on the shared volume is believed, and no program is ever run on it.
//
// /work is where the model worked. Its repository's .git/ and working tree, the dev-context
// clone, HOME - all of it was writable by code the model chose, as this very uid. Running
// `git push` in that repository, as the driver did before #116, would let that code decide
// what runs beside the token. So this role does not open it. It takes exactly two files from
// /work, as bytes:
//
//   handoff/coder.json      what the coder role reports (schema-checked, never quoted)
//   handoff/branch.bundle   the branch, as a git bundle (copied out, size-capped)
//
// both through readUntrusted/copyUntrusted - O_NOFOLLOW, regular files only - because a
// symbolic link to /proc/self/environ would otherwise be read as "the handoff" and its content
// carried into an error text or a PR body. Everything else comes from the request (a ConfigMap)
// and from the SEALED prepare handoff, on a volume the coder container never mounted.
//
// Then, in a scratch directory of its own (this container's private /tmp):
//
//   1. `git init --bare`, and fetch the default branch from the REQUEST's URL.
//   2. Import the bundle into that repository. A bundle is a pack and a ref list: data.
//   3. Re-derive, on that copy, everything a push depends on, instead of reading it from the
//      handoff: the tip is the commit the driver verified; it descends from the base prepare
//      sealed, which is on the remote's default branch; every commit beyond the base carries
//      this attempt's trailer; the publishing policy holds on base..tip; the remote branch is
//      absent or this attempt's own.
//   4. Push that commit, by id, to the request's URL, as refs/heads/<the request's branch>.
//   5. Render the PR body here, from the request and the sealed template, and run gh from an
//      empty directory.
//
// WHAT THAT CLOSES. Every one of these lives in the shared repository or the shared HOME, and
// none is ever read by a process of this role:
//
//   - hooks: pre-push, and reference-transaction, which --no-verify does not skip
//   - core.hooksPath, core.fsmonitor, core.sshCommand, core.gitProxy, core.pager, core.askPass
//   - credential.helper (a command that is handed the credential), credential.<url>.*
//   - url.<x>.insteadOf / pushInsteadOf, remote.<name>.url / pushurl / push / receivepack /
//     proxy, http.proxy, http.extraHeader, http.<url>.*: the token sent to another host
//   - include.path / includeIf: any of the above, from any file
//   - filter.<x>.clean / smudge / process, diff.<x>.textconv / command, .gitattributes
//   - push.gpgSign with gpg.program, push.recurseSubmodules with .gitmodules
//   - refs/replace/*, info/grafts, objects/info/alternates, a .git that is a link elsewhere
//   - ~/.gitconfig, ~/.config/git/*, ~/.config/gh/* under the shared HOME
//   - the lazy fetch of a partial clone, from a promisor remote the config names
//
// The scratch repository's own configuration is written by this role and nothing else, and
// publishGitEnv still overrides the dangerous keys and switches hooks off, so that a mistake
// here does not quietly reopen one of them.
//
// WHAT REMAINS, stated rather than hidden:
//
//   - The verification verdict is the coder container's word. "Build and tests passed", the
//     log tail, the summary, the deviations and the cost come from coder.json, and code that
//     defeated the guard in that container could have written all of them, together with a
//     bundle to match. What it cannot do is push anywhere but the assigned branch, push a diff
//     that breaks the policy, or reach the token. The PR is a draft, and its CI runs on GitHub.
//   - git parses the bundle. A memory-safety bug in git's pack reader would run beside the
//     token - the exposure of any `git fetch` from a server one does not trust. fetch.fsckObjects
//     is asked for, but git applies it to a bundle only in releases newer than the image's;
//     nothing is ever checked out here, and GitHub checks what it receives.
//   - The pushed commit is the model's work. Whatever reacts to a push of hephaisto/codefix-*
//     with privileges of its own (a workflow with secrets) runs it; `.github/**` is a
//     protected path, so the diff cannot change which workflows those are.
//   - The NuGet cache PVC, when codeFix.nugetCache is on, outlives the Job and is filled by
//     the prepare role's restore, which holds both tokens. The Job mounts it read-only into
//     the coder container for that reason.
//   - `CODEFIX_ROLE` unset (tests, the eval harness, `docker run`) runs every role in one
//     process. None of this applies there.

const NOT_PUBLISHED: readonly ImplementResult['outcome'][] = ['failed', 'no_changes', 'policy_diff', 'build_failed', 'tests_failed'];

export interface PublishDeps {
  env: RunnerEnv;
  /** The latest moment a push or a PR may still be running. */
  deadline: number;
  abort: AbortController;
}

/** A refusal with a sentence for the result. Nothing was pushed when one is thrown before the push. */
class Refusal extends Error {}

/**
 * git for the scratch repository. HOME and the XDG directory are inside the scratch directory,
 * discovery cannot climb out of it, system and global configuration are off, and the keys a
 * configuration file could use to run a command or redirect a credential are set here, at the
 * precedence of the command line.
 */
export function publishGitEnv(env: RunnerEnv, scratch: string): NodeJS.ProcessEnv {
  const home = join(scratch, 'home');
  return gitEnv({
    token: env.githubToken,
    home,
    base: env.base,
    config: [
      ['core.fsmonitor', 'false'],
      ['core.sshCommand', 'false'],
      ['core.pager', 'cat'],
      ['credential.helper', ''],
      ['protocol.ext.allow', 'never'],
      ['push.recurseSubmodules', 'no'],
      ['fetch.recurseSubmodules', 'false'],
      ['submodule.recurse', 'false'],
      ['push.gpgSign', 'false'],
      ['gc.auto', '0'],
      ['maintenance.auto', 'false'],
    ],
    extra: {
      XDG_CONFIG_HOME: join(home, '.config'),
      GIT_CEILING_DIRECTORIES: scratch,
      GIT_ATTR_NOSYSTEM: '1',
      GIT_NO_REPLACE_OBJECTS: '1',
      GIT_OPTIONAL_LOCKS: '0',
    },
  });
}

function inside(child: string, parent: string): boolean {
  const c = realpathSync(child);
  let p = parent;
  try {
    p = realpathSync(parent);
  } catch {
    /* the workspace may not exist; then nothing is inside it */
  }
  return c === p || c.startsWith(p + sep);
}

function lastLine(text: string): string {
  return redact((text.trim().split('\n').pop() ?? '').slice(0, 300));
}

export async function runPublish(req: CodeFixRequest, deps: PublishDeps, sealed: PrepareHandoff): Promise<ImplementResult> {
  const { env } = deps;
  const branch = req.repository.branch;
  const result = minimalFailed('implement', req.attempt_id, 'publish did not complete') as ImplementResult;
  result.branch = branch;
  result.base_commit = sealed.base_commit;
  result.deviations = [...sealed.deviations];
  const refuse = (why: string): ImplementResult => {
    log.error(`not publishing: ${why}`);
    return { ...result, outcome: 'failed', pr_url: null, pr_number: null, error: `${why}; nothing was pushed` };
  };

  // --- what the coder role reports: hostile bytes until proven a handoff
  let h: CoderHandoff | null;
  try {
    h = readCoderHandoff(env.handoffDir);
  } catch (e) {
    return refuse(e instanceof HandoffError ? e.message : "the coder's handoff could not be read");
  }
  if (!h) return refuse('the coder role left no handoff');
  if (h.attempt_id !== req.attempt_id) return refuse("the coder's handoff is for another attempt");
  const carried = normalizeResult('implement', h.result, req.attempt_id);
  if (!carried) return refuse("the coder's handoff carries a result that does not match the contract");
  Object.assign(result, {
    cost_usd: carried.cost_usd,
    session_id: carried.session_id,
    denied_tool_calls: carried.denied_tool_calls,
    deviations: carried.deviations,
    files: carried.files,
    build_passed: carried.build_passed,
    tests_passed: carried.tests_passed,
    log_tail: carried.log_tail,
  });

  if (!h.publish) {
    // pr_opened and already_exists are not the coder role's to claim: it cannot open a PR
    if (!NOT_PUBLISHED.includes(carried.outcome)) return refuse(`the coder role reported ${carried.outcome} without handing over a branch`);
    return { ...result, outcome: carried.outcome, pr_url: null, pr_number: null, error: carried.error };
  }

  const base = sealed.base_commit;
  const plan = req.plan;
  if (!base || !sealed.pr || !plan) return refuse('the sealed prepare handoff carries no base commit, PR settings or plan');
  if (!BRANCH_RE.test(branch)) return refuse(`assigned branch ${branch} is not a hephaisto/codefix-<id> branch`);
  if (!REMOTE_URL_RE.test(req.repository.url)) return refuse('the repository URL is not http(s) or file');
  if (!h.head || !h.report || h.report.failed) return refuse("the coder's handoff asks to publish without a green verification");
  result.build_passed = h.report.buildPassed;
  result.tests_passed = h.report.testsPassed;

  const scratch = mkdtempSync(join(env.base.TMPDIR || tmpdir(), 'hephaisto-publish-'));
  const timer = setTimeout(() => {
    log.warn('the publish deadline was reached; aborting');
    deps.abort.abort();
  }, Math.max(0, deps.deadline - Date.now()));
  let pushed = false;
  try {
    // In a Job this container has its own /tmp. If the scratch directory is on the shared volume
    // after all, the whole argument above is void, and it is better to stop.
    if (env.role === 'publish' && inside(scratch, env.workDir)) throw new Refusal('the scratch directory of the publish role is on the shared volume');

    const repoDir = join(scratch, 'repo.git');
    const cwd = join(scratch, 'empty');
    mkdirSync(join(scratch, 'home'), { recursive: true });
    mkdirSync(cwd);
    const genv = publishGitEnv(env, scratch);
    const init = await run('git', ['init', '--quiet', '--bare', repoDir], { cwd, env: genv, signal: deps.abort.signal });
    if (init.code !== 0) throw new Error(`git init failed: ${lastLine(init.stderr)}`);
    const git = new Git(repoDir, genv, deps.abort.signal);
    const def = req.repository.default_branch;
    await git.ok(['remote', 'add', 'origin', req.repository.url]);
    const fetched = await withNetworkRetry(git, ['fetch', '--quiet', '--no-tags', '--filter=blob:none', 'origin', `+refs/heads/${def}:refs/remotes/origin/${def}`], { timeoutMs: 15 * 60_000 });
    if (fetched.code !== 0) throw new Error(`the default branch could not be fetched from the remote: ${lastLine(fetched.stderr)}`);

    // --- the base prepare sealed is still on the remote's default branch
    if (!(await git.hasCommit(base)) || (await git.try(['merge-base', '--is-ancestor', base, `refs/remotes/origin/${def}`])).code !== 0) {
      throw new Refusal(`the base commit ${base} is not on ${def} of the remote any more`);
    }

    // --- the branch, as data
    const bundle = join(scratch, 'branch.bundle');
    try {
      if (!lstatSync(env.handoffDir).isDirectory()) throw new Error('not a directory');
      const bytes = await copyUntrusted(join(env.handoffDir, BUNDLE_FILE), bundle);
      log.info(`copied the coder's bundle (${bytes} bytes) out of the shared volume`);
    } catch (e) {
      throw new Refusal(`the coder's bundle could not be read as a plain file (${(e as NodeJS.ErrnoException).code ?? 'refused'})`);
    }
    const imported = await git.try(['-c', 'fetch.fsckObjects=true', 'fetch', '--quiet', '--no-tags', '--no-write-fetch-head', bundle, `+refs/heads/${branch}:refs/heads/${branch}`]);
    if (imported.code !== 0) throw new Refusal(`the branch could not be read from the coder's bundle (${lastLine(imported.stderr)})`);

    // --- everything a push depends on, from this copy
    const tip = (await git.ok(['rev-parse', '--verify', '--quiet', `refs/heads/${branch}^{commit}`])).trim();
    if (tip !== h.head) throw new Refusal(`the bundle's tip ${tip} is not the commit the driver verified`);
    if ((await git.try(['merge-base', '--is-ancestor', base, tip])).code !== 0) throw new Refusal('the branch does not descend from the base commit it was created from');
    const commits = (await git.ok(['log', '--format=%H%x1f%(trailers:key=Hephaisto-Attempt,valueonly,separator=%x2c)%x1e', `${base}..${tip}`]))
      .split('\x1e')
      .map((c) => c.trim())
      .filter(Boolean);
    if (commits.length === 0) throw new Refusal('the branch has no commit beyond its base');
    const foreign = commits.filter((c) => !(c.split('\x1f')[1] ?? '').split(',').map((t) => t.trim()).includes(req.attempt_id));
    if (foreign.length > 0) throw new Refusal(`${foreign.length} commit(s) on the branch do not carry Hephaisto-Attempt: ${req.attempt_id}`);
    const files = await changedFiles(git, base, tip);
    if (files.length === 0) throw new Refusal('the branch changes no file');
    result.files = files;

    const policy = await policyCheck(git, base, tip, files, sealed.protected_globs);
    if (!policy.ok) {
      log.error(`the publish role's own policy check refused the diff: ${policy.reasons.join('; ')}`);
      return { ...result, outcome: 'policy_diff', pr_url: null, pr_number: null, error: `the diff violates the publishing policy: ${policy.reasons.join('; ')}` };
    }

    // --- the remote branch, asked now: absent, or this attempt's own earlier push
    const remote = await inspectRemoteBranch(git, branch, def, req.attempt_id);
    if (remote.kind === 'foreign') throw new Refusal(remote.reason);

    // --- the PR body is rendered here, and may not carry what this container holds
    const body = renderPrBody({
      req,
      plan,
      changeSummary: h.change_summary,
      files,
      deviations: result.deviations,
      notes: sealed.notes,
      report: h.report,
      costUsd: result.cost_usd,
      versions: versionLine(sealed.context_sha),
      template: sealed.pr.template,
    });
    const title = prTitle(req, plan);
    if (secretIn(body) || secretIn(title)) throw new Refusal('the pull request text would contain a credential of this container');
    const bodyFile = join(scratch, 'pr-body.md');
    writeFileSync(bodyFile, body, { mode: 0o600 });

    await pushBranch(git, req.repository.url, tip, branch, branch, remote.kind === 'reset' ? remote.lease : undefined);
    pushed = true;

    // gh runs in an empty directory: with --repo and --head it needs no repository, and it would
    // run `git status` in one if it had it.
    const pr = await createDraftPr(
      {
        repoUrl: req.repository.url,
        base: def,
        head: branch,
        title,
        bodyFile,
        assignee: sealed.pr.assignee,
        labels: sealed.pr.labels,
      },
      ghEnv(env, join(scratch, 'home')),
      cwd,
    );
    return { ...result, outcome: 'pr_opened', pr_url: pr.url, pr_number: pr.number, deviations: [...result.deviations, ...pr.deviations], error: null };
  } catch (e) {
    if (e instanceof Refusal) return refuse(e.message);
    log.error(`publish failed: ${redact((e as Error).stack ?? String(e))}`);
    const msg = redact((e as Error).message);
    return { ...result, outcome: 'failed', pr_url: null, pr_number: null, error: pushed ? `the branch was pushed, but then: ${msg}` : `${msg}; nothing was pushed` };
  } finally {
    clearTimeout(timer);
    rmSync(scratch, { recursive: true, force: true });
  }
}
