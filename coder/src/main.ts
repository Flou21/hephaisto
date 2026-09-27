import { existsSync, readFileSync } from 'node:fs';
import { DEADLINE_HEADROOM_SECONDS, type RunnerEnv, parseEnv, workPaths } from './config.js';
import { log, setLogPrefix } from './log.js';
import { type PhaseDeps, runImplement, runPlan } from './phases.js';
import { NIL_UUID, emitResult, hasEmitted, minimalFailed, validUuid } from './result.js';
import { type CodeFixRequest, type ImplementResult, type Phase, type PlanResult, validate } from './schemas.js';
import { loadQuery } from './sdk.js';

// Entry point. One invariant above all others: exactly ONE framed result is the last thing on
// stdout, whatever happens - an invalid request, a crash, SIGTERM from the kubelet. A Job that
// exits without one is a ContractViolation on Hephaisto's side, which is the worst way to fail.

export interface MainOptions {
  env?: NodeJS.ProcessEnv;
  write?: (s: string) => void;
  /** Tests run several mains in one process; the CLI entry installs signal handlers only once. */
  installSignalHandlers?: boolean;
}

interface Identity {
  attemptId: string;
  phase: Phase;
}

function identityFrom(raw: unknown): Identity {
  const o = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  return {
    attemptId: typeof o.attempt_id === 'string' && validUuid(o.attempt_id) ? o.attempt_id : NIL_UUID,
    phase: o.phase === 'implement' ? 'implement' : 'plan',
  };
}

export function internalDeadline(deadlineSeconds: number, now = Date.now()): number {
  const seconds = Math.max(deadlineSeconds - DEADLINE_HEADROOM_SECONDS, Math.floor(deadlineSeconds / 2));
  return now + seconds * 1000;
}

export async function main(opts: MainOptions = {}): Promise<{ phase: Phase; result: PlanResult | ImplementResult; json: string | null }> {
  let identity: Identity = { attemptId: NIL_UUID, phase: 'plan' };
  let env: RunnerEnv | null = null;
  const abort = new AbortController();

  const emit = (result: PlanResult | ImplementResult) => emitResult(identity.phase, result, { sink: env?.resultSink, write: opts.write });

  if (opts.installSignalHandlers) {
    let terminating = false;
    const onSignal = (sig: NodeJS.Signals) => {
      if (terminating) return; // a second signal must not bypass the report
      terminating = true;
      log.error(`received ${sig}; reporting failure before the kubelet kills the pod`);
      abort.abort();
      if (!hasEmitted()) emit(minimalFailed(identity.phase, identity.attemptId, `runner terminated by ${sig} before it finished (Job deadline or eviction)`));
      process.exit(143);
    };
    process.on('SIGTERM', onSignal);
    process.on('SIGINT', onSignal);
    const onCrash = (e: unknown) => {
      log.error(`unhandled: ${(e as Error)?.stack ?? String(e)}`);
      if (!hasEmitted()) emit(minimalFailed(identity.phase, identity.attemptId, `runner crashed: ${(e as Error)?.message ?? String(e)}`));
      process.exit(1);
    };
    process.on('uncaughtException', onCrash);
    process.on('unhandledRejection', onCrash);
  }

  const finish = (result: PlanResult | ImplementResult) => ({ phase: identity.phase, result, json: emit(result) });

  // --- environment
  try {
    env = parseEnv(opts.env ?? process.env);
  } catch (e) {
    return finish(minimalFailed(identity.phase, identity.attemptId, `invalid runner environment: ${(e as Error).message.slice(0, 1000)}`));
  }
  if (env.sdkMode === 'fake') setLogPrefix('FAKE SDK');

  // --- request
  let raw: unknown;
  try {
    if (!existsSync(env.requestPath)) throw new Error(`request file ${env.requestPath} does not exist`);
    raw = JSON.parse(readFileSync(env.requestPath, 'utf8'));
  } catch (e) {
    log.error(`request could not be read: ${(e as Error).message}`);
    return finish(minimalFailed(identity.phase, identity.attemptId, `request could not be read: ${(e as Error).message}`));
  }
  identity = identityFrom(raw);
  const v = validate('request', raw);
  if (!v.ok) {
    log.error(`request does not match codefix-request.schema.json: ${v.errors.join('; ')}`);
    return finish(minimalFailed(identity.phase, identity.attemptId, `request does not match the contract: ${v.errors.slice(0, 10).join('; ')}`));
  }
  const req = raw as CodeFixRequest;

  // --- fake mode must never be able to spend money or reach a real model
  if (env.sdkMode === 'fake' && env.anthropicAuth) {
    return finish(minimalFailed(identity.phase, identity.attemptId, 'CODEFIX_SDK=fake refuses to start while CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is set'));
  }
  if (env.sdkMode === 'real' && !env.anthropicAuth) {
    return finish(minimalFailed(identity.phase, identity.attemptId, 'no Anthropic credential: set CLAUDE_CODE_OAUTH_TOKEN (or ANTHROPIC_API_KEY)'));
  }

  const deadline = internalDeadline(req.budget.deadline_seconds);
  log.info(`attempt ${req.attempt_id} phase ${req.phase} repo ${req.repository.url} sdk=${env.sdkMode} deadline in ${Math.round((deadline - Date.now()) / 1000)}s`);
  const runnerEnv = env;
  const deps: PhaseDeps = {
    env: runnerEnv,
    paths: workPaths(runnerEnv.workDir),
    deadline,
    abort,
    makeQuery: (fake) =>
      loadQuery(runnerEnv.sdkMode, { scriptDir: runnerEnv.fakeScriptDir, repoName: fake.repoName, phase: req.phase, vars: fake.vars }),
  };
  try {
    const result = req.phase === 'plan' ? await runPlan(req, deps) : await runImplement(req, deps);
    log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)})`);
    return finish(result);
  } catch (e) {
    return finish(minimalFailed(identity.phase, identity.attemptId, `runner failed: ${(e as Error).message}`));
  }
}

// CLI entry (dist/main.js in the image)
const invokedDirectly = process.argv[1] && /main\.(js|ts)$/.test(process.argv[1]) && !process.env.VITEST;
if (invokedDirectly) {
  main({ installSignalHandlers: true })
    .then(() => process.exit(0))
    .catch((e: unknown) => {
      log.error(`fatal: ${(e as Error)?.stack ?? String(e)}`);
      if (!hasEmitted()) emitResult('plan', minimalFailed('plan', NIL_UUID, `runner crashed: ${(e as Error)?.message ?? String(e)}`));
      process.exit(1);
    });
}
