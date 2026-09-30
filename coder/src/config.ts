import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { z } from 'zod';

/** coder/ when running from src/ under vitest, /opt/coder when running dist/ in the image. */
export const APP_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const EnvSchema = z.object({
  CODEFIX_REQUEST: z.string().default('/work/in/request.json'),
  CODEFIX_SDK: z.enum(['fake', 'real']).default('real'),
  CODEFIX_FAKE_SCRIPT_DIR: z.string().optional(),
  /** investigate + fake only: a script in the script dir to play instead of the one the target selects. */
  CODEFIX_FAKE_SCRIPT: z.string().optional(),
  CODEFIX_GH: z.enum(['real', 'shim']).default('real'),
  CODEFIX_GH_SHIM_DIR: z.string().optional(),
  CODEFIX_WORK_DIR: z.string().default('/work'),
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
  return {
    base: env,
    requestPath: e.CODEFIX_REQUEST,
    sdkMode: e.CODEFIX_SDK,
    fakeScriptDir: e.CODEFIX_FAKE_SCRIPT_DIR ?? join(APP_ROOT, 'fake-scripts'),
    fakeScript: e.CODEFIX_FAKE_SCRIPT || undefined,
    ghMode: e.CODEFIX_GH,
    ghShimDir: e.CODEFIX_GH_SHIM_DIR ?? defaultShimDir(),
    workDir: resolve(e.CODEFIX_WORK_DIR),
    resultSink: e.CODEFIX_RESULT_SINK,
    model: e.CODEFIX_MODEL,
    claudeExecutable: e.CODEFIX_CLAUDE_EXECUTABLE ?? defaultClaudeExecutable(),
    githubToken: e.GITHUB_TOKEN || undefined,
    nugetToken: e.NUGET_GITHUB_TOKEN || undefined,
    anthropicAuth,
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
export const BINARY_MAX_BYTES = 1024 * 1024;
export const GIT_IDENTITY = {
  name: 'hephaisto-coder',
  email: 'hephaisto-coder@users.noreply.github.com',
};
