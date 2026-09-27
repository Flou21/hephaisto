import { spawn } from 'node:child_process';

export interface ExecOptions {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  timeoutMs?: number;
  input?: string;
  /** Output kept per stream, from the END (the tail is what explains a failure). */
  maxOutputBytes?: number;
  signal?: AbortSignal;
}

export interface ExecResult {
  code: number;
  stdout: string;
  stderr: string;
  durationMs: number;
  timedOut: boolean;
}

class Tail {
  private chunks: Buffer[] = [];
  private size = 0;
  constructor(private readonly max: number) {}
  push(b: Buffer): void {
    this.chunks.push(b);
    this.size += b.length;
    while (this.size > this.max && this.chunks.length > 1) {
      const first = this.chunks.shift()!;
      this.size -= first.length;
    }
  }
  toString(): string {
    const all = Buffer.concat(this.chunks);
    return (all.length > this.max ? all.subarray(all.length - this.max) : all).toString('utf8');
  }
}

/**
 * Runs a program WITHOUT a shell. stdout of the child is always captured, never inherited:
 * a child writing to our stdout would land between us and the framed result.
 */
export function run(cmd: string, args: string[], opts: ExecOptions = {}): Promise<ExecResult> {
  const started = Date.now();
  const max = opts.maxOutputBytes ?? 1024 * 1024;
  return new Promise((resolve) => {
    const out = new Tail(max);
    const err = new Tail(max);
    let timedOut = false;
    let settled = false;
    const child = spawn(cmd, args, {
      cwd: opts.cwd,
      env: opts.env ?? process.env,
      stdio: ['pipe', 'pipe', 'pipe'],
      detached: true,
    });
    const killTree = () => {
      if (child.pid === undefined) return;
      try {
        process.kill(-child.pid, 'SIGKILL');
      } catch {
        try {
          child.kill('SIGKILL');
        } catch {
          /* already gone */
        }
      }
    };
    const timer = opts.timeoutMs
      ? setTimeout(() => {
          timedOut = true;
          killTree();
        }, opts.timeoutMs)
      : undefined;
    const onAbort = () => {
      timedOut = true;
      killTree();
    };
    opts.signal?.addEventListener('abort', onAbort, { once: true });
    child.stdout.on('data', (b: Buffer) => out.push(b));
    child.stderr.on('data', (b: Buffer) => err.push(b));
    child.stdin.on('error', () => {
      /* child exited before reading its input */
    });
    if (opts.input !== undefined) child.stdin.end(opts.input);
    else child.stdin.end();
    const finish = (code: number, extraErr = '') => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      opts.signal?.removeEventListener('abort', onAbort);
      resolve({
        code,
        stdout: out.toString(),
        stderr: err.toString() + extraErr,
        durationMs: Date.now() - started,
        timedOut,
      });
    };
    child.on('error', (e) => finish(127, `\n${e.message}`));
    child.on('close', (code, sig) => finish(code ?? (sig ? 128 + 9 : 1)));
  });
}

/** Runs a command line through /bin/sh. Only ever used for repos.yaml commands, which are team-authored. */
export function runShell(command: string, opts: ExecOptions = {}): Promise<ExecResult> {
  return run('/bin/sh', ['-c', command], opts);
}

export class CommandError extends Error {
  constructor(
    readonly command: string,
    readonly result: ExecResult,
  ) {
    super(`${command} exited ${result.code}: ${(result.stderr || result.stdout).trim().slice(-2000)}`);
  }
}
