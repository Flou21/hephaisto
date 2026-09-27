import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { runShell } from './exec.js';
import { log } from './log.js';
import type { RepoEntry, VerificationLevel } from './schemas.js';

// The driver - never the agent - runs the repository's own commands from repos.yaml and reports
// exit codes it observed itself. What the agent claims about builds is not evidence.

export type StepName = 'restore' | 'build' | 'test' | 'typecheck';

export interface VerificationStep {
  name: StepName;
  command: string;
  exit: number;
  durationMs: number;
  timedOut: boolean;
  summary?: string;
}

export interface VerificationReport {
  level: VerificationLevel;
  steps: VerificationStep[];
  buildPassed: boolean;
  testsPassed: boolean;
  /** The step that failed first, if any. */
  failed: VerificationStep | null;
  logTail: string;
  honestyNote: string;
}

const DEFAULT_TIMEOUTS: Record<StepName, number> = { restore: 600, build: 900, test: 1200, typecheck: 900 };

export interface VerifyOptions {
  cwd: string;
  env: NodeJS.ProcessEnv;
  /** Latest moment a step may still be running. */
  deadline: number;
  signal?: AbortSignal;
  steps?: StepName[];
}

function timeoutFor(repo: RepoEntry, step: StepName): number {
  const t = repo.timeouts;
  const s = step === 'restore' ? t?.restoreSeconds : step === 'test' ? t?.testSeconds : t?.buildSeconds;
  return (s ?? DEFAULT_TIMEOUTS[step]) * 1000;
}

export async function runVerification(repo: RepoEntry, opts: VerifyOptions): Promise<VerificationReport> {
  const order: StepName[] = opts.steps ?? ['restore', 'build', 'typecheck', 'test'];
  const steps: VerificationStep[] = [];
  let logTail = '';
  let failed: VerificationStep | null = null;
  for (const name of order) {
    const command = repo.commands[name];
    if (!command) continue;
    const remaining = opts.deadline - Date.now();
    if (remaining <= 5_000) {
      const s: VerificationStep = { name, command, exit: -1, durationMs: 0, timedOut: true, summary: 'not run: deadline reached' };
      steps.push(s);
      failed = s;
      break;
    }
    log.info(`verify ${name}: ${command}`);
    const started = Date.now();
    const r = await runShell(command, {
      cwd: opts.cwd,
      env: opts.env,
      timeoutMs: Math.min(timeoutFor(repo, name), remaining - 2_000),
      maxOutputBytes: 64 * 1024,
      signal: opts.signal,
    });
    const step: VerificationStep = { name, command, exit: r.timedOut ? 124 : r.code, durationMs: r.durationMs, timedOut: r.timedOut };
    if (name === 'test') {
      const trx = summariseTrx(opts.cwd, started);
      if (trx) step.summary = trx;
    }
    steps.push(step);
    const output = `$ ${command}\n${r.stdout}${r.stderr ? `\n${r.stderr}` : ''}${r.timedOut ? '\n(timed out)' : ''}\n[exit ${step.exit}]`;
    log.info(`verify ${name}: exit ${step.exit} in ${Math.round(r.durationMs / 1000)}s`);
    if (step.exit !== 0) {
      logTail = output;
      failed = step;
      break;
    }
    if (name === 'test' || !logTail) logTail = output;
  }

  const ran = (n: StepName) => steps.find((s) => s.name === n && s.exit === 0);
  const buildLike = steps.filter((s) => s.name !== 'test');
  const buildPassed = buildLike.some((s) => s.name === 'build' || s.name === 'typecheck') && buildLike.every((s) => s.exit === 0);
  const testsPassed = !!ran('test') && !failed;
  const level: VerificationLevel = ran('test') && repo.verification.hasUnitTests
    ? 'tests'
    : ran('build')
      ? 'build-only'
      : ran('typecheck')
        ? 'typecheck-only'
        : 'none';

  const ranNames = steps.map((s) => s.name).join(', ') || 'nothing';
  let honestyNote = `The runner ran ${ranNames} itself; the exit codes above are its own, not the agent's.`;
  if (!repo.commands.test) honestyNote += ' repos.yaml has no test command for this repository: a green build is the only check.';
  else if (!repo.verification.hasUnitTests) honestyNote += ' repos.yaml says this repository has no unit tests, so a passing test step proves little.';
  if (repo.verification.note) honestyNote += ` ${repo.verification.note}`;

  return { level, steps, buildPassed, testsPassed, failed, logTail, honestyNote };
}

/** Counters from any .trx written since `since` (dotnet test --logger trx). */
export function summariseTrx(dir: string, since: number): string | null {
  const found: string[] = [];
  const walk = (d: string, depth: number) => {
    if (depth > 6 || found.length > 20) return;
    let entries;
    try {
      entries = readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (e.name === '.git' || e.name === 'node_modules' || e.name === 'obj') continue;
      const p = join(d, e.name);
      if (e.isDirectory()) walk(p, depth + 1);
      else if (e.name.endsWith('.trx') && statSync(p).mtimeMs >= since - 1000) found.push(p);
    }
  };
  if (existsSync(dir)) walk(dir, 0);
  if (found.length === 0) return null;
  let total = 0;
  let passed = 0;
  let failed = 0;
  for (const f of found) {
    const m = /<Counters\b[^>]*>/.exec(readFileSync(f, 'utf8'));
    if (!m) continue;
    const attr = (n: string) => Number(new RegExp(`${n}="(\\d+)"`).exec(m[0])?.[1] ?? 0);
    total += attr('total');
    passed += attr('passed');
    failed += attr('failed');
  }
  return `trx: ${passed}/${total} passed, ${failed} failed`;
}

export function verificationTable(report: VerificationReport): string {
  if (report.steps.length === 0) return '_The runner ran nothing: repos.yaml lists no commands for this repository._';
  const rows = report.steps.map(
    (s) => `| ${s.name} | \`${s.command.replace(/\|/g, '\\|').replace(/`/g, "'")}\` | ${s.exit}${s.timedOut ? ' (timeout)' : ''} | ${(s.durationMs / 1000).toFixed(1)} s${s.summary ? ` · ${s.summary}` : ''} |`,
  );
  return ['| Step | Command | Exit | Duration |', '|---|---|---|---|', ...rows, '', report.honestyNote].join('\n');
}
