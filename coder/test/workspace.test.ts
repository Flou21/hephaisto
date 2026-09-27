import { existsSync, lstatSync, mkdirSync, readFileSync, readlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { buildAgentEnv } from '../src/agent.js';
import { type RunnerEnv, workPaths } from '../src/config.js';
import { Git, gitEnv, shaFromImage } from '../src/git.js';
import { ghEnv } from '../src/pr.js';
import { findRepo } from '../src/repos.js';
import type { RepoEntry, Repos } from '../src/schemas.js';
import { type Target, ensureNugetConfig, findCaitVersion, nugetCredentialEnv, prepareCait } from '../src/workspace.js';
import { git, makeWorld } from './helpers.js';

const SHA = '0123456789abcdef0123456789abcdef01234567';

function runnerEnv(over: Partial<RunnerEnv> = {}): RunnerEnv {
  return {
    base: process.env,
    requestPath: '/work/in/request.json',
    sdkMode: 'fake',
    fakeScriptDir: '/x',
    ghMode: 'shim',
    ghShimDir: '/opt/coder/gh-shim',
    workDir: '/work',
    resultSink: undefined,
    model: undefined,
    claudeExecutable: undefined,
    githubToken: 'ghp_driverOnlyToken000000000000000000000',
    nugetToken: 'ghp_nugetOnlyToken0000000000000000000000',
    anthropicAuth: { name: 'CLAUDE_CODE_OAUTH_TOKEN', value: 'sk-ant-oat01-x' },
    ...over,
  };
}

describe('image tag → analysed commit', () => {
  it.each([
    [`ghcr.io/flou21/hephaisto-fixture-dotnet:c15-${SHA}`, SHA],
    [`muehlhansfl/caitmatchingservice:${SHA}`, SHA],
    [`registry:5000/org/svc:${SHA}@sha256:${'a'.repeat(64)}`, SHA],
    ['muehlhansfl/caitmatchingservice:3f2a9c1e-7b1d-4c1e-9f7a-2d7c1b0e9a11', null],
    ['muehlhansfl/caitmatchingservice', null],
    ['registry:5000/svc', null],
    [`svc:${SHA.slice(0, 39)}`, null],
    [null, null],
  ])('%s → %s', (image, sha) => {
    expect(shaFromImage(image)).toBe(sha);
  });
});

describe('repos.yaml matching', () => {
  const entry = (name: string, url: string): RepoEntry => ({ name, url, defaultBranch: 'main', stack: 'dotnet', coderEnabled: true, workloads: [], commands: {}, verification: { hasUnitTests: false } });
  const repos: Repos = {
    defaults: { pr: { assignee: 'Flou21', labels: [], branchPrefix: 'hephaisto/', draft: true }, clone: { filter: 'blob:none' }, imageTagIsCommitSha: true, protectedPaths: [] },
    repos: [entry('CaitMatchingService', 'https://github.com/TrueRelevance/CaitMatchingService')],
  };
  it('matches by URL, ignoring .git and case', () => {
    expect(findRepo(repos, 'https://github.com/truerelevance/caitmatchingservice.git')?.name).toBe('CaitMatchingService');
  });
  it('falls back to the repository name for a different host (the in-cluster git server)', () => {
    expect(findRepo(repos, 'http://coder-git.hephaisto-coder.svc/TrueRelevance/CaitMatchingService.git')?.name).toBe('CaitMatchingService');
  });
  it('never borrows an entry by name on the same host', () => {
    expect(findRepo(repos, 'https://github.com/someone-else/CaitMatchingService')).toBeUndefined();
  });
});

describe('credentials stay with the driver', () => {
  it("the agent's environment has no GitHub or NuGet token, only the Anthropic auth", () => {
    const base = { PATH: '/usr/bin', GITHUB_TOKEN: 'ghp_leak', NUGET_GITHUB_TOKEN: 'ghp_leak', GH_TOKEN: 'ghp_leak', SOME_API_KEY: 'leak', token: 'leak', CLAUDECODE: '1', CLAUDE_CODE_ENTRYPOINT: 'cli', ANTHROPIC_BASE_URL: 'http://mock' };
    {
      const e = buildAgentEnv(runnerEnv({ base }), workPaths('/work'), { GUARD_MODE: 'plan' });
      expect(Object.values(e)).not.toContain('ghp_leak');
      expect(Object.values(e)).not.toContain('leak');
      expect(Object.keys(e).filter((k) => /TOKEN|KEY/i.test(k))).toEqual(['CLAUDE_CODE_OAUTH_TOKEN']);
      expect(e).toMatchObject({ CLAUDE_CONFIG_DIR: '/work/.claude', HOME: '/work/home', GUARD_MODE: 'plan', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' });
      // measured: SCRUB=1 makes CLI 2.1.283 refuse to start without bubblewrap
      expect(e.CLAUDE_CODE_SUBPROCESS_ENV_SCRUB).toBeUndefined();
      // a developer's Claude Code session does not leak into the agent; routing settings do pass
      expect(e.CLAUDECODE).toBeUndefined();
      expect(e.CLAUDE_CODE_ENTRYPOINT).toBeUndefined();
      expect(e.ANTHROPIC_BASE_URL).toBe('http://mock');
    }
  });
  it("the driver's git gets the token only via askpass, gh only as GH_TOKEN", () => {
    const g = gitEnv({ token: 'ghp_x', home: '/work/home' });
    expect(g.CODEFIX_GIT_PASSWORD).toBe('ghp_x');
    expect(g.GIT_ASKPASS).toMatch(/bin\/askpass$/);
    expect(Object.entries(g).filter(([, v]) => v === 'ghp_x').map(([k]) => k)).toEqual(['CODEFIX_GIT_PASSWORD']);
    expect(ghEnv(runnerEnv({ githubToken: 'ghp_y' }), '/work/home').GH_TOKEN).toBe('ghp_y');
  });
  it('git children keep the egress proxy variables', () => {
    const g = gitEnv({ home: '/h', base: { PATH: '/usr/bin', HTTPS_PROXY: 'http://coder-egress:3128', http_proxy: 'http://coder-egress:3128', NO_PROXY: 'localhost' } });
    expect(g.HTTPS_PROXY).toBe('http://coder-egress:3128');
    expect(g.http_proxy).toBe('http://coder-egress:3128');
    expect(g.NO_PROXY).toBe('localhost');
  });
  it('the NuGet feed credential is the %username%/%token% pair, and only when a token exists', () => {
    expect(nugetCredentialEnv(runnerEnv({ nugetToken: 'ghp_n' }))).toEqual({ username: 'x-access-token', token: 'ghp_n' });
    expect(nugetCredentialEnv(runnerEnv({ nugetToken: undefined }))).toEqual({});
  });
});

function fakeTarget(dir: string, repo: Partial<RepoEntry>): Target {
  return {
    repo: { name: 'Svc', url: 'file:///x', defaultBranch: 'main', stack: 'dotnet', coderEnabled: true, workloads: [], commands: {}, verification: { hasUnitTests: false }, ...repo },
    dir,
    git: new Git(dir, gitEnv({ home: dir })),
    notes: [],
    claudeMd: null,
    caitDir: null,
  };
}

describe('nuget.config', () => {
  it("uses the repository's own nuget.config when it has one", () => {
    const w = makeWorld();
    const t = join(w.root, 'repos', 'Svc');
    mkdirSync(t, { recursive: true });
    writeFileSync(join(t, 'NuGet.Config'), '<configuration/>');
    expect(ensureNugetConfig(fakeTarget(t, {}), workPaths(w.root))).toBe(join(t, 'NuGet.Config'));
    expect(existsSync(join(w.root, 'repos', 'nuget.config'))).toBe(false);
  });
  it('writes one a level above a repository without one: placeholders, no token', () => {
    const w = makeWorld();
    const t = join(w.root, 'repos', 'Svc');
    mkdirSync(t, { recursive: true });
    const p = ensureNugetConfig(fakeTarget(t, {}), workPaths(w.root))!;
    expect(p).toBe(join(w.root, 'repos', 'nuget.config'));
    const xml = readFileSync(p, 'utf8');
    expect(xml).toContain('%username%');
    expect(xml).toContain('%token%');
    expect(xml).toContain('https://nuget.pkg.github.com/TrueRelevance/index.json');
    expect(xml).not.toMatch(/ghp_|github_pat_/);
  });
  it('does nothing for a non-.NET repository', () => {
    const w = makeWorld();
    expect(ensureNugetConfig(fakeTarget(w.root, { stack: 'nuxt' }), workPaths(w.root))).toBeNull();
  });
});

describe('Cait at the pinned version', () => {
  function caitWorld() {
    const w = makeWorld();
    // a Cait remote next to the target (same owner dir), three versions
    const seed = join(w.root, 'cait-seed');
    mkdirSync(join(seed, 'Cait'), { recursive: true });
    git(seed, 'init', '-q', '-b', 'main');
    const shas: Record<string, string> = {};
    for (const v of ['51.30.0', '51.31.0', '51.32.0']) {
      writeFileSync(join(seed, 'Cait', 'Cait.csproj'), `<Project>\n  <PropertyGroup>\n    <Version>${v}</Version>\n  </PropertyGroup>\n</Project>\n`);
      writeFileSync(join(seed, 'Cait', `Note${v}.cs`), `// ${v}\n`);
      git(seed, 'add', '-A');
      git(seed, 'commit', '-q', '-m', `bump ${v}`);
      shas[v] = git(seed, 'rev-parse', 'HEAD');
    }
    git(w.root, 'clone', '-q', '--bare', seed, join(w.root, 'Cait.git'));
    const target = join(w.root, 'work', 'repos', 'Svc');
    mkdirSync(target, { recursive: true });
    writeFileSync(join(target, 'Svc.csproj'), '<Project><ItemGroup><PackageReference Include="Cait" Version="51.31.0" /></ItemGroup></Project>');
    return { w, shas, target };
  }
  const repos: Repos = {
    defaults: { pr: { assignee: 'Flou21', labels: [], branchPrefix: 'hephaisto/', draft: true }, clone: { filter: 'none' }, imageTagIsCommitSha: true, protectedPaths: [] },
    repos: [],
  };

  it('finds the PackageReference version', () => {
    const { target } = caitWorld();
    expect(findCaitVersion(target)).toBe('51.31.0');
  });

  it('clones Cait as a sibling at the commit that set that version, and links /work/ref/Cait to it', async () => {
    const { w, shas, target } = caitWorld();
    const paths = workPaths(join(w.root, 'work'));
    mkdirSync(paths.ref, { recursive: true });
    const t = fakeTarget(target, { cait: { pinned: true, sibling: 'never' } });
    await prepareCait(t, `file://${w.root}/Svc.git`, repos, runnerEnv({ githubToken: undefined }), paths);
    expect(t.notes).toEqual([]);
    expect(t.caitDir).toBe(join(paths.repos, 'Cait'));
    expect(git(join(paths.repos, 'Cait'), 'rev-parse', 'HEAD')).toBe(shas['51.31.0']);
    expect(lstatSync(join(paths.ref, 'Cait')).isSymbolicLink()).toBe(true);
    expect(readlinkSync(join(paths.ref, 'Cait'))).toBe(join(paths.repos, 'Cait'));
    expect(existsSync(join(paths.ref, 'Cait', 'Cait', 'Note51.32.0.cs'))).toBe(false);
  });

  it('continues with a note when Cait cannot be cloned', async () => {
    const { w, target } = caitWorld();
    const paths = workPaths(join(w.root, 'work'));
    mkdirSync(paths.ref, { recursive: true });
    const t = fakeTarget(target, { cait: { pinned: true } });
    await prepareCait(t, `file://${w.root}/nowhere/Svc.git`, repos, runnerEnv({ githubToken: undefined }), paths);
    expect(t.caitDir).toBeNull();
    expect(t.notes.join(' ')).toMatch(/could not be cloned/);
    expect(existsSync(join(paths.repos, 'Cait'))).toBe(false);
  });

  it('does nothing unless repos.yaml says the repository pins Cait', async () => {
    const { w, target } = caitWorld();
    const paths = workPaths(join(w.root, 'work'));
    const t = fakeTarget(target, {});
    await prepareCait(t, `file://${w.root}/Svc.git`, repos, runnerEnv({ githubToken: undefined }), paths);
    expect(t.caitDir).toBeNull();
    expect(existsSync(join(paths.repos, 'Cait'))).toBe(false);
  });
});
