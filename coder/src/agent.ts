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
