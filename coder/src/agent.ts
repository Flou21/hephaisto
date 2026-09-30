import type { RunnerEnv, WorkPaths } from './config.js';
import { GIT_IDENTITY } from './config.js';
import { type GuardContext, type GuardMode, describeInput, evaluate } from './guard.js';
import { log } from './log.js';
import type { Denial } from './schemas.js';
import type { Validation } from './schemas.js';
import type { HookCallbackMatcher, HookJSONOutput, Options, PermissionResult, QueryFn, SDKMessage } from './sdk.js';

// runAgent: one query() with the runner's fixed posture - user-scope settings only (dev-context),
// no MCP, the guard on every tool call twice over, a deadline, a budget, and a JSON-schema
// structured output validated against the vendored contract with ONE repair turn.

export type AgentErrorKind = 'max_turns' | 'budget' | 'rate_limited' | 'execution' | 'deadline' | 'invalid_output';

export interface AgentRunOptions {
  phase: GuardMode;
  prompt: string;
  cwd: string;
  env: NodeJS.ProcessEnv;
  additionalDirectories: string[];
  maxTurns: number;
  maxBudgetUsd: number;
  outputSchema: Record<string, unknown>;
  validate: (value: unknown) => Validation;
  guard: GuardContext;
  deadline: number;
  abort: AbortController;
  query: QueryFn;
  model?: string | undefined;
  claudeExecutable?: string | undefined;
}

export interface AgentRunResult<T> {
  ok: boolean;
  output: T | null;
  costUsd: number;
  sessionId: string | null;
  error: string | null;
  errorKind: AgentErrorKind | null;
  denials: Denial[];
  numTurns: number;
}

// Bash is NOT in allowedTools: a bare allowedTools entry auto-approves the whole tool before
// canUseTool is consulted (the SDK warns CLAUDE_SDK_CAN_USE_TOOL_SHADOWED), which would leave the
// PreToolUse hook as the only check. Left out, every Bash call not matched by a dev-context
// settings.json allow rule reaches canUseTool - the guard a second time. Edits are auto-accepted
// by permissionMode acceptEdits in implement and stay behind the hook.
//
// `available` pins the tool set the model is offered at all. Measured with CLI 2.1.283, the
// default set also carries AskUserQuestion (a headless run has nobody to ask), Cron*,
// ScheduleWakeup, Enter/ExitWorktree, Workflow, SendMessage and more - each one a turn wasted on
// a guard denial at best. StructuredOutput is added by outputFormat regardless.
export const PLAN_TOOLS = {
  available: ['Read', 'Grep', 'Glob', 'Bash', 'Skill', 'TodoWrite'],
  allowed: ['Read', 'Grep', 'Glob'],
  disallowed: ['Edit', 'Write', 'MultiEdit', 'NotebookEdit', 'WebFetch', 'WebSearch', 'Task', 'Agent'],
};
export const IMPLEMENT_TOOLS = {
  available: ['Read', 'Grep', 'Glob', 'Bash', 'Skill', 'TodoWrite', 'Edit', 'Write', 'MultiEdit'],
  allowed: ['Read', 'Grep', 'Glob'],
  disallowed: ['NotebookEdit', 'WebFetch', 'WebSearch', 'Task', 'Agent'],
};

/**
 * The agent's environment: the runner's, minus every credential except the one Anthropic auth
 * variable the CLI needs. GITHUB_TOKEN and NUGET_GITHUB_TOKEN never reach the agent - the driver
 * holds them for its own children.
 *
 * CLAUDE_CODE_SUBPROCESS_ENV_SCRUB is deliberately NOT set: measured with CLI 2.1.283, it makes
 * the CLI refuse to start without bubblewrap ("bubblewrap is required for subprocess env
 * scrubbing"), and bwrap needs user namespaces a non-privileged pod does not get. So the
 * Anthropic credential IS visible to the Bash tool's children; the guard denies every reference
 * to it, /proc and env dumps, and egress is the proxy allowlist - the same-uid caveat.
 */
export function buildAgentEnv(env: RunnerEnv, paths: WorkPaths, guardEnv: Record<string, string>): NodeJS.ProcessEnv {
  const out: NodeJS.ProcessEnv = {};
  for (const [k, v] of Object.entries(env.base)) {
    if (v === undefined) continue;
    // a Claude Code session the runner was started from (a developer's shell) must not leak its identity into the agent
    if (/^(CLAUDECODE|CLAUDE_CONFIG_DIR|CLAUDE_PID|CLAUDE_JOB_DIR|CLAUDE_EFFORT|CLAUDE_CODE_(ENTRYPOINT|SESSION|CHILD|MESSAGING|BRIDGE|EXECPATH|AGENT))/.test(k)) continue;
    if (/TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|_KEY$|CREDENTIAL|_PAT$|PRIVATE/i.test(k)) continue;
    if (/^(GH_|GITHUB_|GIT_|CODEFIX_|GUARD_|NUGET_GITHUB|NPM_CONFIG_|npm_config_)/i.test(k)) continue;
    if (k === 'username' || k === 'token') continue;
    out[k] = v;
  }
  if (env.anthropicAuth) out[env.anthropicAuth.name] = env.anthropicAuth.value;
  Object.assign(out, {
    HOME: paths.home,
    CLAUDE_CONFIG_DIR: paths.claudeConfig,
    DISABLE_AUTOUPDATER: '1',
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
    CLAUDE_AGENT_SDK_CLIENT_APP: 'hephaisto-coder',
    NUGET_PACKAGES: paths.nugetPackages,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    DOTNET_NOLOGO: '1',
    GIT_TERMINAL_PROMPT: '0',
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: '/dev/null',
    GIT_AUTHOR_NAME: GIT_IDENTITY.name,
    GIT_AUTHOR_EMAIL: GIT_IDENTITY.email,
    GIT_COMMITTER_NAME: GIT_IDENTITY.name,
    GIT_COMMITTER_EMAIL: GIT_IDENTITY.email,
    ...guardEnv,
  });
  return out;
}

/** What bin/guard (the settings.json command hook) needs to reach the same verdicts as the in-process hook. */
export function guardEnvFor(mode: GuardMode, ctx: GuardContext): Record<string, string> {
  const e: Record<string, string> = {
    GUARD_MODE: mode,
    GUARD_TARGET_DIR: ctx.targetDir,
    GUARD_PROTECTED_GLOBS: JSON.stringify(ctx.protectedGlobs),
  };
  if (ctx.allowedBranch) e.GUARD_ALLOWED_BRANCH = ctx.allowedBranch;
  if (ctx.homeDir) e.GUARD_HOME = ctx.homeDir;
  if (ctx.readRoots) e.GUARD_READ_ROOTS = JSON.stringify(ctx.readRoots);
  return e;
}

class DenialLog {
  readonly list: Denial[] = [];
  private seen = new Set<string>();
  private dropped = 0;
  record(id: string | undefined, tool: string, input: unknown, reason: string): void {
    const key = id ?? `${tool}:${describeInput(tool, input)}`;
    if (this.seen.has(key)) return;
    this.seen.add(key);
    log.warn(`guard denied ${tool}: ${describeInput(tool, input)} - ${reason}`);
    if (this.list.length >= 50) {
      this.dropped++;
      return;
    }
    this.list.push({ tool: tool.slice(0, 64), input: describeInput(tool, input), reason: reason.slice(0, 500) });
  }
  get droppedCount(): number {
    return this.dropped;
  }
}

function isRateLimit(text: string): boolean {
  return /\b429\b|rate[ _-]?limit|too many requests|usage limit/i.test(text);
}

function describeMessage(m: SDKMessage): string | null {
  if (m.type === 'assistant') {
    const parts: string[] = [];
    for (const b of (m.message.content ?? []) as unknown as { type: string; name?: string; input?: unknown; text?: string }[]) {
      if (b.type === 'tool_use') parts.push(`tool_use ${b.name}: ${describeInput(b.name ?? '', b.input).slice(0, 200)}`);
      else if (b.type === 'text' && b.text) parts.push(`text: ${b.text.slice(0, 200).replace(/\n/g, ' ')}`);
    }
    return parts.length ? `assistant ${parts.join(' | ')}` : null;
  }
  if (m.type === 'result') return `result ${m.subtype} turns=${m.num_turns} cost=$${m.total_cost_usd.toFixed(4)}`;
  if (m.type === 'system' && m.subtype === 'init') return `session ${m.session_id} model=${m.model} cli=${m.claude_code_version}`;
  return null;
}

export async function runAgent<T>(o: AgentRunOptions): Promise<AgentRunResult<T>> {
  const denials = new DenialLog();
  const mode = o.phase;
  const tools = mode === 'plan' ? PLAN_TOOLS : IMPLEMENT_TOOLS;

  const preToolUse: HookCallbackMatcher = {
    hooks: [
      async (input, toolUseID): Promise<HookJSONOutput> => {
        if (input.hook_event_name !== 'PreToolUse') return {};
        const v = evaluate(input.tool_name, input.tool_input, mode, { ...o.guard, cwd: input.cwd || o.guard.cwd });
        if (v.allow) return {};
        denials.record(toolUseID ?? input.tool_use_id, input.tool_name, input.tool_input, v.reason);
        return { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: v.reason } };
      },
    ],
  };
  const postToolUse: HookCallbackMatcher = {
    hooks: [
      async (input): Promise<HookJSONOutput> => {
        if (input.hook_event_name === 'PostToolUse') log.info(`tool ${input.tool_name} done: ${describeInput(input.tool_name, input.tool_input).slice(0, 200)}`);
        return {};
      },
    ],
  };
  const canUseTool = async (toolName: string, input: Record<string, unknown>, opts: { toolUseID: string }): Promise<PermissionResult> => {
    const v = evaluate(toolName, input, mode, o.guard);
    if (v.allow) return { behavior: 'allow', updatedInput: input };
    denials.record(opts.toolUseID, toolName, input, v.reason);
    return { behavior: 'deny', message: v.reason };
  };

  let costUsd = 0;
  let sessionId: string | null = null;
  let numTurns = 0;
  let rateLimited = false;
  let deadlineHit = false;

  const remainingMs = o.deadline - Date.now();
  const timer = setTimeout(() => {
    deadlineHit = true;
    log.warn('internal deadline reached; aborting the agent');
    o.abort.abort();
  }, Math.max(0, remainingMs));

  const baseOptions = (budget: number, maxTurns: number): Options => ({
    cwd: o.cwd,
    env: o.env,
    additionalDirectories: o.additionalDirectories,
    settingSources: ['user'],
    mcpServers: {},
    strictMcpConfig: true,
    maxTurns,
    maxBudgetUsd: budget,
    outputFormat: { type: 'json_schema', schema: o.outputSchema },
    tools: tools.available,
    allowedTools: tools.allowed,
    disallowedTools: tools.disallowed,
    permissionMode: mode === 'plan' ? 'default' : 'acceptEdits',
    hooks: { PreToolUse: [preToolUse], PostToolUse: [postToolUse] },
    canUseTool,
    abortController: o.abort,
    skills: 'all',
    stderr: (d: string) => log.info(`claude: ${d.trimEnd().slice(0, 2000)}`),
    ...(o.model ? { model: o.model } : {}),
    ...(o.claudeExecutable ? { pathToClaudeCodeExecutable: o.claudeExecutable } : {}),
  });

  interface Attempt {
    structured: unknown;
    subtype: string | null;
    errors: string[];
    isError: boolean;
    apiStatus: number | null;
    resultText: string;
  }

  const consume = async (prompt: string, options: Options): Promise<Attempt> => {
    const a: Attempt = { structured: undefined, subtype: null, errors: [], isError: false, apiStatus: null, resultText: '' };
    for await (const m of o.query({ prompt, options })) {
      if ('session_id' in m && typeof m.session_id === 'string' && m.session_id) sessionId = m.session_id;
      const d = describeMessage(m);
      if (d) log.info(d);
      if (m.type === 'assistant' && m.error === 'rate_limit') rateLimited = true;
      if (m.type === 'system' && m.subtype === 'api_retry' && m.error_status === 429) rateLimited = true;
      if (m.type === 'rate_limit_event' && m.rate_limit_info.status === 'rejected') rateLimited = true;
      if (m.type === 'result') {
        costUsd = Math.max(costUsd, m.total_cost_usd);
        numTurns += m.num_turns;
        a.subtype = m.subtype;
        a.isError = m.is_error;
        if (m.subtype === 'success') {
          a.structured = m.structured_output;
          a.apiStatus = m.api_error_status ?? null;
          a.resultText = m.result;
        } else {
          a.errors = m.errors;
        }
      }
    }
    return a;
  };

  const fail = (kind: AgentErrorKind, error: string): AgentRunResult<T> => {
    const e = kind === 'rate_limited' && !error.startsWith('RateLimited') ? `RateLimited: ${error}` : error;
    return { ok: false, output: null, costUsd, sessionId, error: e, errorKind: kind, denials: denials.list, numTurns };
  };

  const classify = (a: Attempt): AgentRunResult<T> | null => {
    const errText = [...a.errors, a.resultText].join(' ');
    if (costUsd > o.maxBudgetUsd + 1e-9) return fail('budget', `budget exceeded: spent $${costUsd.toFixed(4)} of $${o.maxBudgetUsd}`);
    switch (a.subtype) {
      case 'error_max_turns':
        return fail('max_turns', `the agent reached maxTurns (${o.maxTurns}) without finishing`);
      case 'error_max_budget_usd':
        return fail('budget', `budget exceeded: the SDK stopped at $${costUsd.toFixed(4)} (cap $${o.maxBudgetUsd})`);
      case 'error_during_execution':
        return rateLimited || isRateLimit(errText) ? fail('rate_limited', errText || 'rate limited') : fail('execution', `agent execution error: ${errText.slice(0, 1500) || 'unknown'}`);
      case null:
        return fail('execution', 'the agent ended without a result message');
    }
    if (a.subtype === 'success' && a.isError) {
      if (a.apiStatus === 429 || rateLimited || isRateLimit(errText)) return fail('rate_limited', errText || 'HTTP 429');
      return fail('execution', `API error${a.apiStatus ? ` ${a.apiStatus}` : ''}: ${errText.slice(0, 1500)}`);
    }
    return null;
  };

  try {
    let a = await consume(o.prompt, baseOptions(o.maxBudgetUsd, o.maxTurns));
    let failure = a.subtype === 'error_max_structured_output_retries' ? null : classify(a);
    if (failure) return failure;
    let v = a.subtype === 'success' ? o.validate(a.structured) : { ok: false, errors: ['the CLI gave up producing structured output'] };
    if (!v.ok) {
      // ONE repair turn, in the same session.
      const remaining = o.maxBudgetUsd - costUsd;
      if (!sessionId || remaining <= 0) return fail(remaining <= 0 ? 'budget' : 'invalid_output', `structured output invalid and no repair possible: ${v.errors.slice(0, 5).join('; ')}`);
      log.warn(`structured output invalid (${v.errors.slice(0, 5).join('; ')}); one repair turn`);
      const repairPrompt = `Your structured output did not validate against the required JSON schema:\n${v.errors.slice(0, 20).map((e) => `- ${e}`).join('\n')}\nReturn the complete result again, corrected, as the structured output. Do not use any other tools.`;
      const before = costUsd;
      a = await consume(repairPrompt, { ...baseOptions(remaining, 3), resume: sessionId });
      // a resumed session reports a running total; be conservative if a producer does not
      if (costUsd < before) costUsd += before;
      failure = classify(a);
      if (failure) return failure;
      v = o.validate(a.structured);
      if (!v.ok) return fail('invalid_output', `structured output still invalid after one repair turn: ${v.errors.slice(0, 5).join('; ')}`);
    }
    if (denials.droppedCount > 0) log.warn(`${denials.droppedCount} further denials not recorded in the result (cap 50)`);
    return { ok: true, output: a.structured as T, costUsd, sessionId, error: null, errorKind: null, denials: denials.list, numTurns };
  } catch (e) {
    const msg = (e as Error).message ?? String(e);
    if (deadlineHit) return fail('deadline', `internal deadline reached after ${Math.round((Date.now() - (o.deadline - remainingMs)) / 1000)}s; the agent was aborted`);
    if ((e as Error).name === 'AbortError') return fail('execution', `the agent was aborted: ${msg}`);
    if (rateLimited || isRateLimit(msg)) return fail('rate_limited', msg);
    return fail('execution', `agent failed: ${msg.slice(0, 1500)}`);
  } finally {
    clearTimeout(timer);
  }
}

// =============================================================================================
// investigate: the same guard and hooks around a different posture. Hephaisto's MCP endpoint is
// the only MCP server, Read/Grep/Glob the only built-ins, NO settings sources (dev-context's
// CLAUDE.md and rules are about code fixes), Hephaisto's own system prompt, and no structured
// output: the answer is the endpoint's `conclude` call, which Hephaisto persists itself.

export const INVESTIGATE_TOOLS = {
  available: ['Read', 'Grep', 'Glob'],
  allowed: ['Read', 'Grep', 'Glob'],
  disallowed: ['Bash', 'Edit', 'Write', 'MultiEdit', 'NotebookEdit', 'WebFetch', 'WebSearch', 'Task', 'Agent'],
};

export const CONCLUDE_TOOL = 'mcp__hephaisto__conclude';
/** After conclude, the one call left: the plan, made in the Job so Hephaisto never calls a model of its own. */
export const PROPOSE_PLAN_TOOL = 'mcp__hephaisto__propose_plan';
export const CONCLUDE_NOW = 'Conclude now with the evidence you have; call conclude.';
/** The one "conclude now" turn may spend this share of the budget beyond the cap, and never more. */
export const CONCLUDE_GRACE_SHARE = 0.1;

export type InvestigatorStop = 'concluded' | 'no_conclusion' | 'max_turns' | 'budget' | 'rate_limited' | 'unauthorized' | 'deadline' | 'failed';

export interface InvestigatorRunOptions {
  systemPrompt: string;
  prompt: string;
  cwd: string;
  env: NodeJS.ProcessEnv;
  additionalDirectories: string[];
  maxTurns: number;
  maxBudgetUsd: number;
  guard: GuardContext;
  deadline: number;
  abort: AbortController;
  query: QueryFn;
  endpoint: { url: string; token: string };
  model?: string | undefined;
  claudeExecutable?: string | undefined;
  /** Called with every change worth reporting if the pod dies mid-run (SIGTERM). */
  onProgress?: (p: InvestigatorProgress) => void;
}

export interface InvestigatorProgress {
  costUsd: number;
  sessionId: string | null;
  model: string | null;
  numTurns: number;
  inputTokens: number;
  outputTokens: number;
  denials: Denial[];
}

export interface InvestigatorRunResult extends InvestigatorProgress {
  stop: InvestigatorStop;
  /** The input of the conclude call that succeeded. */
  conclusion: Record<string, unknown> | null;
  error: string | null;
}

type Block = { type: string; id?: string; name?: string; input?: unknown; tool_use_id?: string; is_error?: boolean; content?: unknown };

function blocksOf(m: SDKMessage): Block[] {
  const c = (m as { message?: { content?: unknown } }).message?.content;
  return Array.isArray(c) ? (c as Block[]) : [];
}

function resultText(content: unknown): string {
  if (typeof content === 'string') return content;
  if (Array.isArray(content)) return content.map((c) => (c && typeof c === 'object' && typeof (c as { text?: unknown }).text === 'string' ? (c as { text: string }).text : '')).join('\n');
  return '';
}

const UNAUTHORIZED = /\b401\b|unauthori[sz]ed/i;

export async function runInvestigator(o: InvestigatorRunOptions): Promise<InvestigatorRunResult> {
  const denials = new DenialLog();
  const p: InvestigatorProgress = { costUsd: 0, sessionId: null, model: null, numTurns: 0, inputTokens: 0, outputTokens: 0, denials: denials.list };
  const progress = () => o.onProgress?.({ ...p, denials: [...denials.list] });

  let concludeOnly = false;
  let conclusion: Record<string, unknown> | null = null;
  let planned = false;
  let rateLimited = false;
  let deadlineHit = false;
  let unauthorizedStreak = 0;
  let unauthorized = false;
  let mcpFailure: string | null = null;
  const calls = new Map<string, { name: string; input: Record<string, unknown> }>();

  // a local controller: the driver aborts THIS run (401s, deadline) without aborting main's
  const local = new AbortController();
  const onOuterAbort = () => local.abort();
  o.abort.signal.addEventListener('abort', onOuterAbort, { once: true });
  const stopRun = () => {
    if (!local.signal.aborted) local.abort();
  };

  const verdict = (tool: string, input: unknown, cwd?: string) => {
    if (conclusion && (planned || tool !== PROPOSE_PLAN_TOOL)) {
      return { allow: false as const, reason: planned ? 'the investigation is concluded and planned; stop now' : `the investigation is concluded; only ${PROPOSE_PLAN_TOOL} is left, then stop` };
    }
    if (conclusion) return evaluate(tool, input, 'investigate', { ...o.guard, cwd: cwd || o.guard.cwd });
    if (concludeOnly && tool !== CONCLUDE_TOOL) return { allow: false as const, reason: `only ${CONCLUDE_TOOL} is available now: conclude with the evidence you have` };
    return evaluate(tool, input, 'investigate', { ...o.guard, cwd: cwd || o.guard.cwd });
  };

  const preToolUse: HookCallbackMatcher = {
    hooks: [
      async (input, toolUseID): Promise<HookJSONOutput> => {
        if (input.hook_event_name !== 'PreToolUse') return {};
        const v = verdict(input.tool_name, input.tool_input, input.cwd);
        if (v.allow) return {};
        if (!conclusion) denials.record(toolUseID ?? input.tool_use_id, input.tool_name, input.tool_input, v.reason);
        return { hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: v.reason } };
      },
    ],
  };
  const postToolUse: HookCallbackMatcher = {
    hooks: [
      async (input): Promise<HookJSONOutput> => {
        if (input.hook_event_name !== 'PostToolUse') return {};
        log.info(`tool ${input.tool_name} done: ${describeInput(input.tool_name, input.tool_input).slice(0, 200)}`);
        // the conclude call's arguments, kept by id; it counts only once its tool_result is not an error
        if (input.tool_name === CONCLUDE_TOOL && input.tool_input && typeof input.tool_input === 'object') {
          calls.set(input.tool_use_id, { name: input.tool_name, input: input.tool_input as Record<string, unknown> });
        }
        return {};
      },
    ],
  };
  const canUseTool = async (toolName: string, input: Record<string, unknown>, opts: { toolUseID: string }): Promise<PermissionResult> => {
    const v = verdict(toolName, input);
    if (v.allow) return { behavior: 'allow', updatedInput: input };
    if (!conclusion) denials.record(opts.toolUseID, toolName, input, v.reason);
    return { behavior: 'deny', message: v.reason };
  };

  const remainingMs = o.deadline - Date.now();
  const timer = setTimeout(() => {
    deadlineHit = true;
    log.warn('internal deadline reached; aborting the investigator');
    stopRun();
  }, Math.max(0, remainingMs));

  const options = (budget: number, maxTurns: number): Options => ({
    cwd: o.cwd,
    env: o.env,
    additionalDirectories: o.additionalDirectories,
    settingSources: [],
    systemPrompt: o.systemPrompt,
    mcpServers: { hephaisto: { type: 'http', url: o.endpoint.url, headers: { Authorization: `Bearer ${o.endpoint.token}` } } },
    strictMcpConfig: true,
    maxTurns,
    maxBudgetUsd: budget,
    tools: INVESTIGATE_TOOLS.available,
    allowedTools: INVESTIGATE_TOOLS.allowed,
    disallowedTools: INVESTIGATE_TOOLS.disallowed,
    permissionMode: 'default',
    hooks: { PreToolUse: [preToolUse], PostToolUse: [postToolUse] },
    canUseTool,
    abortController: local,
    stderr: (d: string) => log.info(`claude: ${d.trimEnd().slice(0, 2000)}`),
    ...(o.model ? { model: o.model } : {}),
    ...(o.claudeExecutable ? { pathToClaudeCodeExecutable: o.claudeExecutable } : {}),
  });

  interface Attempt {
    subtype: string | null;
    errors: string[];
    isError: boolean;
    apiStatus: number | null;
    resultText: string;
  }

  const consume = async (prompt: string, opts: Options): Promise<Attempt> => {
    const a: Attempt = { subtype: null, errors: [], isError: false, apiStatus: null, resultText: '' };
    const before = { cost: p.costUsd, input: p.inputTokens, output: p.outputTokens };
    for await (const m of o.query({ prompt, options: opts })) {
      if ('session_id' in m && typeof m.session_id === 'string' && m.session_id) p.sessionId = m.session_id;
      const d = describeMessage(m);
      if (d) log.info(d);
      if (m.type === 'system' && m.subtype === 'init') {
        p.model = m.model || p.model;
        const server = (m.mcp_servers ?? []).find((s) => s.name === 'hephaisto');
        if (server) log.info(`MCP server hephaisto: ${server.status}`);
        if (server?.status === 'needs-auth') {
          unauthorized = true;
          stopRun();
        } else if (server?.status === 'failed') {
          mcpFailure = 'the hephaisto MCP server failed to connect';
          stopRun();
        }
        progress();
      }
      if (m.type === 'assistant') {
        if (m.error === 'rate_limit') rateLimited = true;
        for (const b of blocksOf(m)) {
          if (b.type === 'tool_use' && b.id && b.name && !calls.has(b.id)) {
            calls.set(b.id, { name: b.name, input: (b.input && typeof b.input === 'object' ? b.input : {}) as Record<string, unknown> });
          }
        }
      }
      if (m.type === 'user') {
        for (const b of blocksOf(m)) {
          if (b.type !== 'tool_result' || !b.tool_use_id) continue;
          const call = calls.get(b.tool_use_id);
          if (!call || !call.name.startsWith('mcp__hephaisto__')) continue;
          const text = resultText(b.content);
          if (b.is_error === true && UNAUTHORIZED.test(text)) {
            unauthorizedStreak++;
            log.warn(`the investigator endpoint refused ${call.name} as unauthorized (${unauthorizedStreak} in a row)`);
            if (unauthorizedStreak >= 2) {
              unauthorized = true;
              stopRun();
            }
            continue;
          }
          unauthorizedStreak = 0;
          if (call.name === CONCLUDE_TOOL && b.is_error !== true && !conclusion) {
            conclusion = call.input;
            log.info('conclude returned without error: the investigation is concluded');
          }
          if (call.name === PROPOSE_PLAN_TOOL && b.is_error !== true && conclusion && !planned) {
            planned = true;
            log.info('propose_plan returned without error: the plan is recorded');
          }
        }
      }
      if (m.type === 'system' && m.subtype === 'api_retry' && m.error_status === 429) rateLimited = true;
      if (m.type === 'rate_limit_event' && m.rate_limit_info.status === 'rejected') rateLimited = true;
      if (m.type === 'result') {
        // a resumed session reports running totals; be conservative if a producer does not
        p.costUsd = m.total_cost_usd >= before.cost ? m.total_cost_usd : before.cost + m.total_cost_usd;
        p.numTurns += m.num_turns;
        const usage = Object.values(m.modelUsage ?? {});
        const input = usage.length
          ? usage.reduce((n, u) => n + u.inputTokens + u.cacheReadInputTokens + u.cacheCreationInputTokens, 0)
          : (m.usage?.input_tokens ?? 0) + (m.usage?.cache_read_input_tokens ?? 0) + (m.usage?.cache_creation_input_tokens ?? 0);
        const output = usage.length ? usage.reduce((n, u) => n + u.outputTokens, 0) : (m.usage?.output_tokens ?? 0);
        p.inputTokens = input >= before.input ? input : before.input + input;
        p.outputTokens = output >= before.output ? output : before.output + output;
        if (!p.model) p.model = Object.keys(m.modelUsage ?? {})[0] ?? null;
        a.subtype = m.subtype;
        a.isError = m.is_error;
        if (m.subtype === 'success') {
          a.apiStatus = m.api_error_status ?? null;
          a.resultText = m.result;
        } else {
          a.errors = m.errors;
        }
        progress();
      }
    }
    return a;
  };

  const done = (stop: InvestigatorStop, error: string | null): InvestigatorRunResult => {
    if (denials.droppedCount > 0) log.warn(`${denials.droppedCount} further denials not recorded in the result (cap 50)`);
    return { ...p, denials: denials.list, stop: conclusion ? 'concluded' : stop, conclusion, error: conclusion ? null : error };
  };

  /** Why the run stopped, when it did not conclude; null = it may get the one "conclude now" turn. */
  const classify = (a: Attempt): { stop: InvestigatorStop; error: string } | null => {
    const errText = [...a.errors, a.resultText].join(' ');
    if (unauthorized) return { stop: 'unauthorized', error: 'endpoint_unauthorized' };
    if (mcpFailure) return { stop: 'failed', error: mcpFailure };
    if (deadlineHit) return { stop: 'deadline', error: 'internal deadline reached; the investigator was aborted' };
    if (a.subtype === 'error_max_turns' || a.subtype === 'error_max_budget_usd' || p.costUsd > o.maxBudgetUsd + 1e-9) return null;
    const errored = a.isError || a.subtype === 'error_during_execution';
    if (errored && (rateLimited || a.apiStatus === 429 || isRateLimit(errText))) {
      return { stop: 'rate_limited', error: `RateLimited: ${errText || 'rate limited'}`.slice(0, 2000) };
    }
    if (a.subtype === 'error_during_execution') return { stop: 'failed', error: `agent execution error: ${errText.slice(0, 1500) || 'unknown'}` };
    if (a.subtype === null) return { stop: 'failed', error: 'the agent ended without a result message' };
    if (a.subtype === 'success' && a.isError) return { stop: 'failed', error: `API error${a.apiStatus ? ` ${a.apiStatus}` : ''}: ${errText.slice(0, 1500)}` };
    return { stop: 'no_conclusion', error: 'the investigator ended without calling conclude' };
  };

  try {
    let a = await consume(o.prompt, options(o.maxBudgetUsd, o.maxTurns));
    if (conclusion) return done('concluded', null);
    let c = classify(a);
    if (c) return done(c.stop, c.error);

    // max turns or budget, and no conclusion: ONE resumed turn in which only conclude is allowed
    const limit: InvestigatorStop = a.subtype === 'error_max_turns' ? 'max_turns' : 'budget';
    const limitError = limit === 'max_turns' ? `the investigator reached maxTurns (${o.maxTurns}) without concluding` : `budget exhausted: spent $${p.costUsd.toFixed(4)} of $${o.maxBudgetUsd} without concluding`;
    if (!p.sessionId) return done(limit, limitError);
    concludeOnly = true;
    const grace = o.maxBudgetUsd * CONCLUDE_GRACE_SHARE;
    const budget = Math.max(o.maxBudgetUsd - p.costUsd, 0) + grace;
    log.warn(`${limitError}; one resumed turn that may only call conclude (budget $${budget.toFixed(4)})`);
    a = await consume(CONCLUDE_NOW, { ...options(budget, 3), resume: p.sessionId });
    if (conclusion) return done('concluded', null);
    c = classify(a);
    if (c && c.stop !== 'no_conclusion') return done(c.stop, c.error);
    return done(limit, `${limitError}; the conclude-now turn did not conclude either`);
  } catch (e) {
    const msg = (e as Error).message ?? String(e);
    if (unauthorized) return done('unauthorized', 'endpoint_unauthorized');
    if (mcpFailure) return done('failed', mcpFailure);
    if (deadlineHit) return done('deadline', `internal deadline reached after ${Math.round((Date.now() - (o.deadline - remainingMs)) / 1000)}s; the investigator was aborted`);
    if ((e as Error).name === 'AbortError') return done('failed', `the investigator was aborted: ${msg}`);
    if (rateLimited || isRateLimit(msg)) return done('rate_limited', `RateLimited: ${msg}`);
    return done('failed', `agent failed: ${msg.slice(0, 1500)}`);
  } finally {
    clearTimeout(timer);
    o.abort.signal.removeEventListener('abort', onOuterAbort);
  }
}
