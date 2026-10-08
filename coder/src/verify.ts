import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { runShell } from './exec.js';
import { log } from './log.js';
import type { RepoEntry, VerificationLevel } from './schemas.js';

// The driver - never the agent - runs the repository's own commands from repos.yaml and reports
// exit codes it observed itself. What the agent claims about builds is not evidence.
//
// Since #116 the full run happens in the coder role: a test suite is code the model wrote, and it
// must not execute beside a token. So verification has NO feed credentials. It builds from the
// package cache the prepare role filled (the pre-restore, which does hold NUGET_GITHUB_TOKEN); a
// package that is not in that cache cannot be fetched from a private feed here, and
// feedRefusal() is what turns the resulting 401 into a sentence a person can act on.

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


/**
 * What a set of checks amounts to, in one place: the level a plan is told to expect
 * (phases.ts) and the level a pull request reports are the same question asked before and
 * after. `tests` needs a test command AND unit tests behind it; without them a repository that
 * has a type check is `typecheck-only`, and one that only builds is `build-only`.
 *
 * Until 2026-10-08 a build came first, so a Nuxt app - which has both - was `build-only` here
 * while the prompt's own field description and the context's rules call it `typecheck-only`:
 * the first planner to meet that spent a note on reconciling its instructions, and the pull
 * request named one level in its table and the other in its notes.
 */
export function verificationLevel(has: { test: boolean; typecheck: boolean; build: boolean }, hasUnitTests: boolean): VerificationLevel {
  if (has.test && hasUnitTests) return 'tests';
  if (has.typecheck) return 'typecheck-only';
  if (has.build) return 'build-only';
  return 'none';
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
  const level = verificationLevel({ test: !!ran('test'), typecheck: !!ran('typecheck'), build: !!ran('build') }, repo.verification.hasUnitTests);

  const ranNames = steps.map((s) => s.name).join(', ') || 'nothing';
  let honestyNote = `The runner ran ${ranNames} itself; the exit codes above are its own, not the agent's.`;
  if (!repo.commands.test) honestyNote += ' repos.yaml has no test command for this repository: a green build is the only check.';
  else if (!repo.verification.hasUnitTests) honestyNote += ' repos.yaml says this repository has no unit tests, so a passing test step proves little.';
  if (repo.verification.note) honestyNote += ` ${repo.verification.note}`;

  return { level, steps, buildPassed, testsPassed, failed, logTail, honestyNote };
}

const FEED_REFUSAL =
  /\bNU1301\b|\bNU1101\b|\bNU1102\b|\bNU1103\b|Unable to load the service index for source|\b401 \(Unauthorized\)|\b403 \(Forbidden\)|status code does not indicate success: 40[13]|\bE401\b|\bE403\b|code E40[13]|Read-only file system.*nuget|nuget.*Read-only file system|Access to the path '[^']*nuget[^']*' is denied/i;

/**
 * The line of a failed step's output that shows a package feed refusing the request, a package
 * that no reachable source has, or a package cache that could not be written (the Job mounts a
 * shared NuGet cache read-only into the coder container) - or null. NuGet, then the npm spelling.
 */
export function feedRefusal(output: string): string | null {
  for (const line of output.split('\n')) {
    if (FEED_REFUSAL.test(line)) return line.trim().replace(/\s+/g, ' ').slice(0, 300);
  }
  return null;
}

/** What a result says, in plain words, when verification failed for want of a package it could not fetch. */
export function feedRefusalNote(step: StepName, line: string): string {
  return (
    `Verification runs without package-feed credentials (they stay in the prepare container, away from the code the agent wrote), ` +
    `and \`${step}\` needed a package that the pre-restore had not put in the cache - most likely this change adds or bumps a package reference. ` +
    `The build was not judged; a person has to restore and verify it with the feed. The line: ${line}`
  );
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
