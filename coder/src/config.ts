import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { z } from 'zod';

/** coder/ when running from src/ under vitest, /opt/coder when running dist/ in the image. */
export const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/**
 * Which part of the run this process is (backlog #116). A Job starts the same image three times:
 *
 *   prepare   everything that needs a GitHub or NuGet token BEFORE the model exists: the clones,
 *             the open-PR and remote-branch checks, the pre-restore. Holds no model credential.
 *   coder     the agent, and everything that executes what the agent wrote (build, tests). Holds
 *             the model credential and nothing else - and refuses to start beside a git token.
 *   publish   implement only, started after `coder` has ended: push and Draft PR. Holds
 *             GITHUB_TOKEN and trusts nothing on the shared volume.
 *
 * `all` (the default, when the variable is unset) runs the three in ONE process, one after the
 * other, through the same handoff files. That is for tests, the eval harness and `docker run`:
 * nothing separates the model from the tokens there, and no Job Hephaisto starts uses it.
 */
export const ROLES = ['all', 'prepare', 'coder', 'publish'] as const;
export type Role = (typeof ROLES)[number];

const EnvSchema = z.object({
  CODEFIX_ROLE: z.enum(ROLES).default('all'),
  CODEFIX_REQUEST: z.string().default('/work/in/request.json'),
  CODEFIX_SDK: z.enum(['fake', 'real']).default('real'),
  CODEFIX_FAKE_SCRIPT_DIR: z.string().optional(),
  /** investigate + fake only: a script in the script dir to play instead of the one the target selects. */
  CODEFIX_FAKE_SCRIPT: z.string().optional(),
  CODEFIX_GH: z.enum(['real', 'shim']).default('real'),
  CODEFIX_GH_SHIM_DIR: z.string().optional(),
  CODEFIX_WORK_DIR: z.string().default('/work'),
  /** Where prepare hands over to coder, and coder to publish. Default `<work>/handoff`. */
  CODEFIX_HANDOFF_DIR: z.string().optional(),
  /** Where prepare hands over to publish, out of the agent's reach. Default `/sealed`. */
  CODEFIX_SEAL_DIR: z.string().optional(),
  CODEFIX_RESULT_SINK: z.string().optional(),
  CODEFIX_MODEL: z.string().optional(),
  CODEFIX_CLAUDE_EXECUTABLE: z.string().optional(),
  GITHUB_TOKEN: z.string().optional(),
  NUGET_GITHUB_TOKEN: z.string().optional(),
  CLAUDE_CODE_OAUTH_TOKEN: z.string().optional(),
  ANTHROPIC_API_KEY: z.string().optional(),
});

export interface RunnerEnv {
  /** The environment the runner was started with; every child environment is derived from THIS, never from process.env. */
  base: NodeJS.ProcessEnv;
  role: Role;
  handoffDir: string;
  /**
   * A directory the Job mounts into `prepare` (writable) and `publish` (read-only) and NOT into
   * `coder`: what prepare decided reaches publish without passing through anything the agent
   * could write. In `all` mode it is a directory beside the workspace and separates nothing.
   */
  sealDir: string;
  requestPath: string;
  sdkMode: 'fake' | 'real';
  fakeScriptDir: string;
  fakeScript: string | undefined;
  ghMode: 'real' | 'shim';
  ghShimDir: string;
  workDir: string;
  resultSink: string | undefined;
  model: string | undefined;
  claudeExecutable: string | undefined;
  githubToken: string | undefined;
  nugetToken: string | undefined;
  anthropicAuth: { name: 'CLAUDE_CODE_OAUTH_TOKEN' | 'ANTHROPIC_API_KEY'; value: string } | undefined;
}

export function parseEnv(env: NodeJS.ProcessEnv = process.env): RunnerEnv {
  const e = EnvSchema.parse(env);
  const anthropicAuth = e.CLAUDE_CODE_OAUTH_TOKEN
    ? { name: 'CLAUDE_CODE_OAUTH_TOKEN' as const, value: e.CLAUDE_CODE_OAUTH_TOKEN }
    : e.ANTHROPIC_API_KEY
      ? { name: 'ANTHROPIC_API_KEY' as const, value: e.ANTHROPIC_API_KEY }
      : undefined;
  const workDir = resolve(e.CODEFIX_WORK_DIR);
  return {
    base: env,
    role: e.CODEFIX_ROLE,
    handoffDir: resolve(e.CODEFIX_HANDOFF_DIR ?? join(workDir, 'handoff')),
    sealDir: resolve(e.CODEFIX_SEAL_DIR ?? (e.CODEFIX_ROLE === 'all' ? join(workDir, 'sealed') : '/sealed')),
    requestPath: e.CODEFIX_REQUEST,
    sdkMode: e.CODEFIX_SDK,
    fakeScriptDir: e.CODEFIX_FAKE_SCRIPT_DIR ?? join(APP_ROOT, 'fake-scripts'),
    fakeScript: e.CODEFIX_FAKE_SCRIPT || undefined,
    ghMode: e.CODEFIX_GH,
    ghShimDir: e.CODEFIX_GH_SHIM_DIR ?? defaultShimDir(),
    workDir,
    resultSink: e.CODEFIX_RESULT_SINK,
    model: e.CODEFIX_MODEL,
    claudeExecutable: e.CODEFIX_CLAUDE_EXECUTABLE ?? defaultClaudeExecutable(),
    githubToken: e.GITHUB_TOKEN || undefined,
    nugetToken: e.NUGET_GITHUB_TOKEN || undefined,
    anthropicAuth,
  };
}

/** Every variable that carries a GitHub or NuGet credential, in the runner's environment or a child's. */
export const GIT_SECRET_VARS = ['GITHUB_TOKEN', 'GH_TOKEN', 'GH_ENTERPRISE_TOKEN', 'NUGET_GITHUB_TOKEN', 'CODEFIX_GIT_PASSWORD'] as const;
export const MODEL_SECRET_VARS = ['CLAUDE_CODE_OAUTH_TOKEN', 'ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN'] as const;

/**
 * The credentials a role must NOT have been started with, by name. The Job's spec is what keeps
 * them apart; this is the runner refusing to go on when the spec did not - a `coder` beside a
 * git token is exactly the pod #116 is about, and it is better not run than run.
 */
export function roleViolation(env: RunnerEnv): string | null {
  const held = (names: readonly string[]) => names.filter((n) => env.base[n]);
  if (env.role === 'coder') {
    const h = held(GIT_SECRET_VARS);
    if (h.length > 0) return `the coder role runs the model and the code it wrote, and refuses to start while ${h.join(', ')} is set: that credential belongs to the prepare and publish containers`;
  }
  if (env.role === 'prepare' || env.role === 'publish') {
    const h = held(MODEL_SECRET_VARS);
    if (h.length > 0) return `the ${env.role} role holds a GitHub token and refuses to start while ${h.join(', ')} is set: the model credential belongs to the coder container`;
  }
  return null;
}

/**
 * `all` mode only: the environment one role would have had in a Job. The three still share a
 * process, so this separates nothing - it makes the single-process run take the same paths (a
 * verification without feed credentials, a publish without a model credential) as the Job does.
 */
export function roleEnv(env: RunnerEnv, role: Exclude<Role, 'all'>): RunnerEnv {
  const drop: readonly string[] =
    role === 'coder' ? GIT_SECRET_VARS : role === 'prepare' ? MODEL_SECRET_VARS : [...MODEL_SECRET_VARS, 'NUGET_GITHUB_TOKEN'];
  const base: NodeJS.ProcessEnv = {};
  for (const [k, v] of Object.entries(env.base)) if (!drop.includes(k)) base[k] = v;
  return {
    ...env,
    base,
    role,
    githubToken: role === 'coder' ? undefined : env.githubToken,
    nugetToken: role === 'prepare' ? env.nugetToken : undefined,
    anthropicAuth: role === 'coder' ? env.anthropicAuth : undefined,
  };
}

/**
 * The pinned @anthropic-ai/claude-code binary - the same `claude` that is on PATH in the image -
 * so the SDK and a manual `claude -p` smoke test run one and the same executable. The image
 * deletes the SDK's own bundled copy (a second 215 MB of the same version).
 */
function defaultClaudeExecutable(): string | undefined {
  const p = join(APP_ROOT, 'node_modules', '@anthropic-ai', 'claude-code', 'bin', 'claude.exe');
  return existsSync(p) ? p : undefined;
}

function defaultShimDir(): string {
  // In the image the shim is installed at /opt/coder/gh-shim/gh; in the source tree it lives
  // under test/, where the tests put it on PATH themselves.
  return APP_ROOT.startsWith('/opt/coder') ? join(APP_ROOT, 'gh-shim') : join(APP_ROOT, 'test', 'gh-shim');
}

export interface WorkPaths {
  root: string;
  context: string;
  claudeConfig: string;
  repos: string;
  ref: string;
  nuget: string;
  nugetPackages: string;
  home: string;
  out: string;
}

export function workPaths(workDir: string): WorkPaths {
  return {
    root: workDir,
    context: join(workDir, 'context'),
    claudeConfig: join(workDir, '.claude'),
    repos: join(workDir, 'repos'),
    ref: join(workDir, 'ref'),
    nuget: join(workDir, 'nuget'),
    nugetPackages: join(workDir, 'nuget', 'packages'),
    home: join(workDir, 'home'),
    out: join(workDir, 'out'),
  };
}

/**
 * NO_PROXY and no_proxy with `url`'s host added, so a client that honours the proxy variables
 * reaches Hephaisto's investigator endpoint directly instead of through the egress proxy (which
 * allows only package registries and GitHub). Both spellings: tools disagree about which counts.
 */
export function withNoProxyFor(env: NodeJS.ProcessEnv, url: string): NodeJS.ProcessEnv {
  let host: string;
  try {
    host = new URL(url).hostname.replace(/^\[|\]$/g, '');
  } catch {
    return env;
  }
  if (!host) return env;
  const merged = [env.NO_PROXY, env.no_proxy]
    .flatMap((v) => (v ?? '').split(','))
    .map((h) => h.trim())
    .filter(Boolean);
  if (!merged.includes(host)) merged.push(host);
  const value = [...new Set(merged)].join(',');
  return { ...env, NO_PROXY: value, no_proxy: value };
}

export const PLAN_MAX_TURNS = 60;
export const IMPLEMENT_MAX_TURNS = 120;
/** The Job's activeDeadlineSeconds kills the pod; the runner stops this much earlier so it can still report. */
export const DEADLINE_HEADROOM_SECONDS = 180;
export const RESULT_MAX_BYTES = 512 * 1024;
export const PATCH_MAX_BYTES = 1024 * 1024;
/** The branch as coder hands it to publish. A fix is kilobytes; this is the ceiling of what publish will copy. */
export const BUNDLE_MAX_BYTES = 256 * 1024 * 1024;
export const HANDOFF_MAX_BYTES = 4 * 1024 * 1024;
/** What publish keeps of the Job's deadline for itself after coder has stopped: a fetch, a push, a PR. */
export const PUBLISH_MARGIN_SECONDS = 15;
export const BINARY_MAX_BYTES = 1024 * 1024;
export const GIT_IDENTITY = {
  name: 'hephaisto-coder',
  email: 'hephaisto-coder@users.noreply.github.com',
};
