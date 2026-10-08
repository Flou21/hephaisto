import { existsSync, readFileSync, rmSync, statSync } from 'node:fs';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { type InvestigatorRunResult, type InvestigatorStop, buildAgentEnv, guardEnvFor, runInvestigator } from './agent.js';
import { type RunnerEnv, type WorkPaths, withNoProxyFor } from './config.js';
import { type GuardContext, realish } from './guard.js';
import { type PrepareHandoff, newPrepareHandoff } from './handoff.js';
import { addRedaction, log } from './log.js';
import { EndpointUnauthorizedError, InvestigatorClient } from './mcp-client.js';
import { loadTemplate, render } from './prompts.js';
import { findRepo, repoDirName } from './repos.js';
import { minimalFailed } from './result.js';
import type { Billing, CodeRef, InvestigateOutcome, InvestigateRequest, InvestigateResult, RepoEntry } from './schemas.js';
import type { QueryFn } from './sdk.js';
import { checkoutAnalysedRef, cloneTarget, makeDirs, prepareContext, sanitizeTarget } from './workspace.js';

// The investigate phase (v0.12.0): the runner investigates one incident through Hephaisto's
// investigator MCP endpoint and nothing else, and reports how the run went. The findings are not
// in the result - the agent records them by calling `conclude`, which Hephaisto persists - so the
// result is the runner's account: outcome, cost and who paid, tokens, the source checkout, and
// the code references it could confirm in that checkout.
//
//   /work/context        dev-context @ request.context.ref (notes and memory, read-only)
//   /work/repos/<name>   the workload's source, read-only, when request.source is set
//
// Two halves since #116. prepareInvestigate makes those two clones - the only thing here that
// needs a GitHub token, and dev-context is private in production, so it is needed by every run -
// in the prepare role. runInvestigate is the coder role: the endpoint preflight, the model, the
// result. Its container is handed the model credential and no GitHub token.

export interface InvestigatePrepareDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  abort: AbortController;
  startedAt: number;
}

export interface InvestigateDeps {
  env: RunnerEnv;
  paths: WorkPaths;
  deadline: number;
  abort: AbortController;
  makeQuery: () => Promise<QueryFn>;
  /** The result so far, for a report that survives SIGTERM. */
  onProgress?: (r: InvestigateResult) => void;
}

export function billingOf(env: RunnerEnv | null): Billing {
  if (env?.sdkMode === 'fake') return 'fake';
  return env?.anthropicAuth?.name === 'CLAUDE_CODE_OAUTH_TOKEN' ? 'subscription' : 'api';
}

function safeName(s: string): string | null {
  const n = s.trim();
  return /^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$/.test(n) && !n.includes('..') ? n : null;
}

/**
 * Fake scripts, in the order tried: CODEFIX_FAKE_SCRIPT, then the workload's name (the last
 * segment of `ns/Kind/name` - incident targets are usually pods), then the target's name, then
 * default. Untrusted names never become a path unless they are plain file names.
 */
export function fakeScriptCandidates(req: InvestigateRequest, override: string | undefined): string[] {
  if (override) {
    const f = safeName(override.endsWith('.json') ? override : `${override}.investigate.json`);
    if (!f) throw new Error(`CODEFIX_FAKE_SCRIPT ${JSON.stringify(override)} is not a plain file name`);
    return [f];
  }
  const out: string[] = [];
  for (const n of [req.incident.target.workload.split('/').pop() ?? '', req.incident.target.name]) {
    const f = safeName(n);
    if (f && !out.includes(`${f}.investigate.json`)) out.push(`${f}.investigate.json`);
  }
  out.push('default.investigate.json');
  return out;
}

/** One initialize against the endpoint before any model token is spent: a 401 here is a dead token. */
export async function preflight(endpoint: { url: string; token: string }, signal?: AbortSignal): Promise<{ ok: true; tools: string[] } | { ok: false; error: string }> {
  // the pod's first connections can be refused while the NetworkPolicy admits it (see git.clone)
  const delays = [1_000, 2_000, 4_000];
  for (let attempt = 0; ; attempt++) {
    const client = new InvestigatorClient(endpoint, 'hephaisto-coder-preflight');
    try {
      await client.connect(signal);
      const tools = await client.listTools();
      return { ok: true, tools };
    } catch (e) {
      if (e instanceof EndpointUnauthorizedError) return { ok: false, error: 'endpoint_unauthorized' };
      if (attempt >= delays.length || signal?.aborted) return { ok: false, error: `endpoint_unreachable: ${(e as Error).message.slice(0, 500)}` };
      log.warn(`investigator endpoint not reachable yet (attempt ${attempt + 1}): ${(e as Error).message}; retrying in ${delays[attempt]! / 1000}s`);
      await new Promise((r) => setTimeout(r, delays[attempt]));
    } finally {
      await client.close();
    }
  }
}

const OUTCOME: Record<InvestigatorStop, InvestigateOutcome> = {
  concluded: 'concluded',
  no_conclusion: 'no_conclusion',
  max_turns: 'max_turns',
  budget: 'budget_exhausted',
  rate_limited: 'rate_limited',
  unauthorized: 'failed',
  deadline: 'failed',
  failed: 'failed',
};

function lineCount(text: string): number {
  if (text.length === 0) return 0;
  const n = text.split('\n').length;
  return text.endsWith('\n') ? n - 1 : n;
}

/**
 * The conclude call's code_refs, kept only where the clone agrees: the file exists inside the
 * repository and the line is within it. Paths come back relative to the repository root.
 */
export function verifyCodeRefs(raw: unknown, repoDir: string, sourcePath: string): CodeRef[] {
  if (!Array.isArray(raw)) return [];
  const root = realish(repoDir);
  const out: CodeRef[] = [];
  for (const r of raw.slice(0, 100)) {
    if (out.length >= 20) break;
    if (!r || typeof r !== 'object') continue;
    const ref = r as Record<string, unknown>;
    const finding = ref.finding;
    const line = ref.line;
    if (!Number.isInteger(finding) || (finding as number) < 0 || (finding as number) > 9) continue;
    if (!Number.isInteger(line) || (line as number) < 1) continue;
    if (typeof ref.path !== 'string' || !ref.path.trim()) continue;
    let p = ref.path.trim().replace(/\\/g, '/');
    if (isAbsolute(p)) p = relative(repoDir, p);
    p = p.replace(/^\.\//, '');
    const candidates = [p];
    if (sourcePath) candidates.push(join(sourcePath, p));
    for (const c of candidates) {
      const abs = realish(resolve(repoDir, c));
      if (abs !== root && !abs.startsWith(root + sep)) continue;
      const rel = relative(root, abs).split(sep).join('/');
      if (rel === '.git' || rel.startsWith('.git/')) continue;
      if (!existsSync(abs) || !statSync(abs).isFile()) continue;
      const count = lineCount(readFileSync(abs, 'utf8'));
      if ((line as number) > count) continue;
      const end = ref.end_line;
      const endLine = Number.isInteger(end) && (end as number) >= (line as number) && (end as number) <= count ? (end as number) : null;
      const note = typeof ref.note === 'string' && ref.note ? Array.from(ref.note).slice(0, 500).join('') : null;
      out.push({ finding: finding as number, path: rel.slice(0, 512), line: line as number, end_line: endLine, note });
      break;
    }
  }
  return out;
}

function syntheticEntry(url: string, defaultBranch: string): RepoEntry {
  const name = url.replace(/\/+$/, '').replace(/\.git$/i, '').split('/').pop() || 'source';
  return { name, url, defaultBranch, stack: 'other', coderEnabled: false, workloads: [], commands: {}, verification: { hasUnitTests: false } };
}

/**
 * The prepare role of an investigation: dev-context, and the workload's source when the request
 * names one. A context clone that fails ends the run (as it always did); a source clone that
 * fails is reported in the result and the investigation goes on without it.
 */
export async function prepareInvestigate(req: InvestigateRequest, deps: InvestigatePrepareDeps): Promise<PrepareHandoff> {
  const { env, paths } = deps;
  addRedaction(req.endpoint.token);
  const h = newPrepareHandoff(req.attempt_id, 'investigate', deps.startedAt);
  h.source = req.source ? { cloned: false, analysed_ref: null, error: null } : null;
  try {
    makeDirs(paths);
    const ctx = await prepareContext(req, env, paths, deps.abort.signal);
    h.context_sha = ctx.contextSha;

    // Part 8: the source, read-only. Any failure is reported and the investigation goes on without it.
    if (req.source) {
      const src = req.source;
      const entry = findRepo(ctx.repos, src.url) ?? syntheticEntry(src.url, src.default_branch);
      const dir = join(paths.repos, repoDirName(entry));
      try {
        const target = await cloneTarget({ repository: { url: src.url, default_branch: src.default_branch } }, ctx.repos, entry, env, paths, deps.abort.signal);
        const analysedRef = await checkoutAnalysedRef(target, src.image, src.default_branch, src.ref);
        await sanitizeTarget(target);
        for (const n of target.notes) log.info(`source: ${n}`);
        h.repo_dir = repoDirName(entry);
        h.source = { cloned: true, analysed_ref: analysedRef, error: null };
      } catch (e) {
        rmSync(dir, { recursive: true, force: true });
        const msg = (e as Error).message.split('\n')[0]!;
        log.warn(`the source ${src.url} could not be cloned: ${msg}; investigating without it`);
        h.source = { cloned: false, analysed_ref: null, error: Array.from(msg).slice(0, 1000).join('') };
      }
    }
    return h;
  } catch (e) {
    log.error(`investigate could not be prepared: ${(e as Error).stack ?? String(e)}`);
    // billing is the printing role's to say: this one holds no model credential to judge it by
    const result = minimalFailed('investigate', req.attempt_id, (e as Error).message, 'api');
    return { ...h, terminal: { ...result, context_sha: h.context_sha, source: h.source } };
  }
}

export async function runInvestigate(req: InvestigateRequest, deps: InvestigateDeps, h: PrepareHandoff): Promise<InvestigateResult> {
  const { env, paths } = deps;
  addRedaction(req.endpoint.token);
  const result = minimalFailed('investigate', req.attempt_id, 'investigate did not complete', billingOf(env));
  result.context_sha = h.context_sha;
  result.source = h.source;
  const progress = () => deps.onProgress?.(structuredClone(result));
  progress();
  try {
    makeDirs(paths);

    const pre = await preflight(req.endpoint, deps.abort.signal);
    if (!pre.ok) {
      log.error(`investigator endpoint preflight failed: ${pre.error}; the agent is not started`);
      result.error = pre.error;
      return result;
    }
    log.info(`investigator endpoint ok: ${pre.tools.length} tools${pre.tools.includes('conclude') ? '' : ' (WARNING: no conclude tool)'}`);

    const repoDir = req.source && h.source?.cloned && h.repo_dir && existsSync(join(paths.repos, h.repo_dir)) ? join(paths.repos, h.repo_dir) : null;
    const analysedRef = h.source?.analysed_ref ?? null;

    const sourceBlock = repoDir
      ? `\`${repoDir}\` - the workload's source, read-only, checked out at commit \`${analysedRef}\`, the commit the running image was built from when it was known (otherwise ${req.source!.default_branch} HEAD)${req.source!.path ? `; the workload is built from its \`${req.source!.path}\` subdirectory` : ''}.`
      : req.source
        ? 'No source checkout: cloning the workload\'s repository failed, so work from the cluster evidence alone.'
        : 'No source checkout is available in this run.';
    const section = render(loadTemplate('investigate', paths.context).text, {
      context_dir: paths.context,
      memory_dir: join(paths.context, 'memory'),
      source_block: sourceBlock,
    });
    const systemPrompt = `${req.system_prompt}\n\n${section.trim()}\n`;

    const guard: GuardContext = { targetDir: realish(paths.repos), protectedGlobs: [], homeDir: paths.home, readRoots: [realish(paths.context), realish(paths.repos)] };
    const agentEnv = withNoProxyFor(buildAgentEnv(env, paths, guardEnvFor('investigate', guard)), req.endpoint.url);
    const query = await deps.makeQuery();
    const agent: InvestigatorRunResult = await runInvestigator({
      systemPrompt,
      prompt: req.opening_message,
      cwd: paths.repos,
      env: agentEnv,
      additionalDirectories: [paths.context, paths.repos].filter((d) => existsSync(d)),
      maxTurns: req.budget.max_turns,
      maxBudgetUsd: req.budget.max_cost_usd,
      guard,
      deadline: deps.deadline,
      abort: deps.abort,
      query,
      endpoint: req.endpoint,
      model: env.model,
      claudeExecutable: env.claudeExecutable,
      onProgress: (p) => {
        Object.assign(result, {
          cost_usd: p.costUsd,
          session_id: p.sessionId,
          model: p.model,
          turns: p.numTurns,
          input_tokens: p.inputTokens,
          output_tokens: p.outputTokens,
          denied_tool_calls: p.denials,
        });
        progress();
      },
    });
    Object.assign(result, {
      outcome: OUTCOME[agent.stop],
      cost_usd: agent.costUsd,
      session_id: agent.sessionId,
      model: agent.model,
      turns: agent.numTurns,
      input_tokens: agent.inputTokens,
      output_tokens: agent.outputTokens,
      denied_tool_calls: agent.denials,
      error: agent.error,
    });
    if (agent.conclusion && repoDir) {
      result.code_refs = verifyCodeRefs(agent.conclusion.code_refs, repoDir, req.source?.path ?? '');
      const offered = Array.isArray(agent.conclusion.code_refs) ? agent.conclusion.code_refs.length : 0;
      if (offered > result.code_refs.length) log.warn(`${offered - result.code_refs.length} of ${offered} code_refs were not found in the clone and are not reported`);
    }
    return result;
  } catch (e) {
    log.error(`investigate failed: ${(e as Error).stack ?? String(e)}`);
    result.outcome = 'failed';
    result.error = (e as Error).message;
    return result;
  }
}
