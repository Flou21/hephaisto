import { existsSync, readFileSync } from 'node:fs';
import { DEADLINE_HEADROOM_SECONDS, type RunnerEnv, parseEnv, workPaths } from './config.js';
import { billingOf, fakeScriptCandidates, runInvestigate } from './investigate.js';
import { log, redact, setLogPrefix } from './log.js';
import { type PhaseDeps, runImplement, runPlan } from './phases.js';
import { NIL_UUID, emitResult, hasEmitted, minimalFailed, validUuid } from './result.js';
import { type AnyResult, type CodeFixRequest, type InvestigateRequest, type InvestigateResult, type Phase, validate } from './schemas.js';
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
    phase: o.phase === 'implement' ? 'implement' : o.phase === 'investigate' ? 'investigate' : 'plan',
  };
}

export function internalDeadline(deadlineSeconds: number, now = Date.now()): number {
  const seconds = Math.max(deadlineSeconds - DEADLINE_HEADROOM_SECONDS, Math.floor(deadlineSeconds / 2));
  return now + seconds * 1000;
}

export async function main(opts: MainOptions = {}): Promise<{ phase: Phase; result: AnyResult; json: string | null }> {
  let identity: Identity = { attemptId: NIL_UUID, phase: 'plan' };
  let env: RunnerEnv | null = null;
  const abort = new AbortController();
  /** investigate: the result so far, so a SIGTERM still reports the cost, tokens and context it knows. */
  let live: InvestigateResult | null = null;

  const emit = (result: AnyResult) => {
    // the endpoint token is redacted from logs; an error text that quotes it must not carry it either
    const r = result.error ? { ...result, error: redact(result.error) } : result;
    return emitResult(identity.phase, r, { sink: env?.resultSink, write: opts.write });
  };
  const failed = (error: string): AnyResult => {
    if (identity.phase === 'investigate' && live) return { ...live, outcome: 'failed', error };
    return minimalFailed(identity.phase, identity.attemptId, error, billingOf(env));
  };

  if (opts.installSignalHandlers) {
    let terminating = false;
    const onSignal = (sig: NodeJS.Signals) => {
      if (terminating) return; // a second signal must not bypass the report
      terminating = true;
      log.error(`received ${sig}; reporting failure before the kubelet kills the pod`);
      abort.abort();
      if (!hasEmitted()) emit(failed(`runner terminated by ${sig} before it finished (Job deadline or eviction)`));
      process.exit(143);
    };
    process.on('SIGTERM', onSignal);
    process.on('SIGINT', onSignal);
    const onCrash = (e: unknown) => {
      log.error(`unhandled: ${(e as Error)?.stack ?? String(e)}`);
      if (!hasEmitted()) emit(failed(`runner crashed: ${(e as Error)?.message ?? String(e)}`));
      process.exit(1);
    };
    process.on('uncaughtException', onCrash);
    process.on('unhandledRejection', onCrash);
  }

  const finish = (result: AnyResult) => ({ phase: identity.phase, result, json: emit(result) });

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
  const investigating = identity.phase === 'investigate';
  const v = validate(investigating ? 'investigateRequest' : 'request', raw);
  if (!v.ok) {
    log.error(`request does not match ${investigating ? 'investigate' : 'codefix'}-request.schema.json: ${v.errors.join('; ')}`);
    return finish(minimalFailed(identity.phase, identity.attemptId, `request does not match the contract: ${v.errors.slice(0, 10).join('; ')}`, billingOf(env)));
  }

  // --- fake mode must never be able to spend money or reach a real model
  if (env.sdkMode === 'fake' && env.anthropicAuth) {
    return finish(minimalFailed(identity.phase, identity.attemptId, 'CODEFIX_SDK=fake refuses to start while CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is set', billingOf(env)));
  }
  if (env.sdkMode === 'real' && !env.anthropicAuth) {
    const error = 'no Anthropic credential: set CLAUDE_CODE_OAUTH_TOKEN (or ANTHROPIC_API_KEY)';
    if (investigating) return finish({ ...minimalFailed('investigate', identity.attemptId, error, billingOf(env)), outcome: 'no_credential' });
    return finish(minimalFailed(identity.phase, identity.attemptId, error));
  }

  if (investigating) {
    const ireq = raw as InvestigateRequest;
    const runnerEnv = env;
    const deadline = internalDeadline(ireq.budget.deadline_seconds);
    log.info(`attempt ${ireq.attempt_id} phase investigate incident ${ireq.incident_id} sdk=${env.sdkMode} deadline in ${Math.round((deadline - Date.now()) / 1000)}s`);
    try {
      const result = await runInvestigate(ireq, {
        env: runnerEnv,
        paths: workPaths(runnerEnv.workDir),
        deadline,
        abort,
        onProgress: (r) => (live = r),
        makeQuery: () =>
          loadQuery(runnerEnv.sdkMode, {
            scriptDir: runnerEnv.fakeScriptDir,
            repoName: '',
            phase: 'investigate',
            vars: {},
            scripts: fakeScriptCandidates(ireq, runnerEnv.fakeScript),
            request: ireq,
          }),
      });
      log.info(`outcome ${result.outcome}${result.error ? `: ${result.error}` : ''} (cost $${result.cost_usd.toFixed(4)}, ${result.billing})`);
      return finish(result);
    } catch (e) {
      return finish(failed(`runner failed: ${(e as Error).message}`));
    }
  }
  const req = raw as CodeFixRequest;

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
