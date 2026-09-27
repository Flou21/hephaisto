import { rm } from 'node:fs/promises';
import { join } from 'node:path';
import { APP_ROOT, GIT_IDENTITY, type RunnerEnv } from './config.js';
import { CommandError, type ExecResult, run } from './exec.js';
import { log } from './log.js';

// The DRIVER's git. Credentials reach git only through GIT_ASKPASS, which reads a variable that
// exists solely in the environment of these child processes - never in the agent's, never on
// disk, never on a command line (where /proc/<pid>/cmdline would show it).

export interface GitEnvOptions {
  token?: string | undefined;
  home: string;
  base?: NodeJS.ProcessEnv;
}

export function gitEnv(opts: GitEnvOptions): NodeJS.ProcessEnv {
  const base = opts.base ?? process.env;
  const env: NodeJS.ProcessEnv = {
    PATH: base.PATH,
    HOME: opts.home,
    LANG: 'C.UTF-8',
    GIT_TERMINAL_PROMPT: '0',
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: '/dev/null',
    GIT_AUTHOR_NAME: GIT_IDENTITY.name,
    GIT_AUTHOR_EMAIL: GIT_IDENTITY.email,
    GIT_COMMITTER_NAME: GIT_IDENTITY.name,
    GIT_COMMITTER_EMAIL: GIT_IDENTITY.email,
    GIT_ADVICE: '0',
  };
  // The Job routes egress through the coder-egress proxy; git honours these, so they must pass.
  for (const k of ['HTTPS_PROXY', 'HTTP_PROXY', 'NO_PROXY', 'ALL_PROXY', 'https_proxy', 'http_proxy', 'no_proxy', 'all_proxy', 'TMPDIR']) {
    if (base[k]) env[k] = base[k];
  }
  const cfg: [string, string][] = [
    ['advice.detachedHead', 'false'],
    ['init.defaultBranch', 'main'],
    ['protocol.file.allow', 'always'],
    ['core.hooksPath', '/dev/null'],
  ];
  env.GIT_CONFIG_COUNT = String(cfg.length);
  cfg.forEach(([k, v], i) => {
    env[`GIT_CONFIG_KEY_${i}`] = k;
    env[`GIT_CONFIG_VALUE_${i}`] = v;
  });
  if (opts.token) {
    env.GIT_ASKPASS = join(APP_ROOT, 'bin', 'askpass');
    env.CODEFIX_GIT_PASSWORD = opts.token;
  }
  return env;
}

export function driverGitEnv(env: RunnerEnv, home: string): NodeJS.ProcessEnv {
  return gitEnv({ token: env.githubToken, home, base: env.base });
}

export class Git {
  constructor(
    readonly cwd: string,
    readonly env: NodeJS.ProcessEnv,
    readonly signal?: AbortSignal,
  ) {}

  async try(args: string[], opts: { input?: string; timeoutMs?: number; cwd?: string } = {}): Promise<ExecResult> {
    return run('git', args, {
      cwd: opts.cwd ?? this.cwd,
      env: this.env,
      input: opts.input,
      timeoutMs: opts.timeoutMs ?? 10 * 60_000,
      signal: this.signal,
    });
  }

  async ok(args: string[], opts: { input?: string; timeoutMs?: number; cwd?: string } = {}): Promise<string> {
    const r = await this.try(args, opts);
    if (r.code !== 0) throw new CommandError(`git ${redactArgs(args).join(' ')}`, r);
    return r.stdout;
  }

  async head(): Promise<string> {
    return (await this.ok(['rev-parse', 'HEAD'])).trim();
  }

  async currentBranch(): Promise<string> {
    return (await this.ok(['rev-parse', '--abbrev-ref', 'HEAD'])).trim();
  }

  async hasCommit(sha: string): Promise<boolean> {
    return (await this.try(['cat-file', '-e', `${sha}^{commit}`])).code === 0;
  }
}

function redactArgs(args: string[]): string[] {
  return args.map((a) => a.replace(/\/\/[^/@\s]+@/g, '//***@'));
}

export async function clone(
  url: string,
  dest: string,
  env: NodeJS.ProcessEnv,
  opts: { branch?: string; filter?: 'blob:none' | 'none'; singleBranch?: boolean; signal?: AbortSignal } = {},
): Promise<void> {
  const args = ['clone', '--quiet'];
  if (opts.filter === 'blob:none') args.push('--filter=blob:none');
  if (opts.singleBranch) args.push('--single-branch');
  if (opts.branch) args.push('--branch', opts.branch);
  args.push('--', url, dest);
  log.info(`git clone ${opts.filter === 'blob:none' ? '(blobless) ' : ''}${redactArgs([url])[0]} -> ${dest}`);

  // The clone is the pod's first network call, and a NetworkPolicy controller admits a new pod's
  // IP asynchronously: on k3s the first second of connections is REJECTed while every rule is
  // correct. So a connection-level failure is retried with backoff - a bounded handful of times,
  // and never for anything else (a missing branch or a refused credential fails at once).
  const delays = [1_000, 2_000, 4_000, 8_000];
  for (let attempt = 0; ; attempt++) {
    const r = await run('git', args, { env, timeoutMs: 15 * 60_000, signal: opts.signal });
    if (r.code === 0) return;
    if (attempt >= delays.length || !isTransientNetworkFailure(r.stderr) || opts.signal?.aborted) {
      throw new CommandError(`git clone ${redactArgs([url])[0]}`, r);
    }
    log.warn(`git clone ${redactArgs([url])[0]} could not connect (attempt ${attempt + 1}); retrying in ${delays[attempt]! / 1000}s`);
    await rm(dest, { recursive: true, force: true });
    await new Promise((resolve) => setTimeout(resolve, delays[attempt]));
  }
}

/** A failure to reach the server at all, as opposed to the server refusing the request. */
export function isTransientNetworkFailure(stderr: string): boolean {
  return /Failed to connect|Couldn't connect|Could not connect|Connection refused|Connection timed out|Could not resolve host|Recv failure|Connection reset|Operation timed out|early EOF|RPC failed; curl (7|28|35|52|56)/i.test(stderr)
    && !/Authentication failed|could not read Username|Repository not found|not found in upstream|Remote branch .* not found/i.test(stderr);
}

/** Trailers the agent is told to write, and the driver writes on its own final commit. */
export function trailers(incidentId: string, attemptId: string): string {
  return `Hephaisto-Incident: ${incidentId}\nHephaisto-Attempt: ${attemptId}`;
}

/** 40-hex commit sha from an image reference: `repo:<sha>` or `repo:<anything>-<sha>` (fixtures: `:c15-<sha>`). */
export function shaFromImage(image: string | null | undefined): string | null {
  if (!image) return null;
  const noDigest = image.split('@')[0]!;
  const lastSlash = noDigest.lastIndexOf('/');
  const colon = noDigest.lastIndexOf(':');
  if (colon <= lastSlash) return null;
  const tag = noDigest.slice(colon + 1);
  const m = /^(?:.*-)?([0-9a-f]{40})$/.exec(tag);
  return m ? m[1]! : null;
}
