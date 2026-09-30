import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CONCLUDE_NOW, runInvestigator } from '../src/agent.js';
import { APP_ROOT, parseEnv, withNoProxyFor, workPaths } from '../src/config.js';
import { fakeQuery, template } from '../src/fake-sdk.js';
import { fakeScriptCandidates, runInvestigate, verifyCodeRefs } from '../src/investigate.js';
import { parseLastFrame } from '../src/result.js';
import { type InvestigateRequest, validate } from '../src/schemas.js';
import type { Options, QueryFn } from '../src/sdk.js';
import { ENDPOINT_TOKEN, INVESTIGATE_ATTEMPT, type World, git, investigateRequest, makeSourceRepo, makeWorld, runRequest, script } from './helpers.js';
import { type McpStub, STUB_EXCEPTION, STUB_POD, startMcpStub } from './mcp-stub.js';

// The investigate phase end to end through main(): the FAKE SDK executes each scripted step
// through the real guard and hooks, and every mcp__hephaisto__* step as a REAL MCP Streamable
// HTTP call against an in-test stub of Hephaisto's investigator endpoint.

const SHIPPED = join(APP_ROOT, 'fake-scripts');
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

let stub: McpStub;
let w: World;
let stderr: string[];

beforeEach(async () => {
  stub = await startMcpStub({ token: ENDPOINT_TOKEN });
  w = makeWorld();
  stderr = [];
  const orig = process.stderr.write.bind(process.stderr);
  vi.spyOn(process.stderr, 'write').mockImplementation(((chunk: string | Uint8Array, ...rest: unknown[]) => {
    stderr.push(String(chunk));
    return (orig as (c: string | Uint8Array, ...r: unknown[]) => boolean)(chunk, ...rest);
  }) as typeof process.stderr.write);
});

afterEach(async () => {
  vi.restoreAllMocks();
  await stub.close();
});

function expectValid(doc: Record<string, unknown>) {
  expect(validate('investigate', doc).errors).toEqual([]);
  expect(doc).toMatchObject({ attempt_id: INVESTIGATE_ATTEMPT, phase: 'investigate', contract_version: '1' });
}

const conclude = (evidence: unknown[], extra: Record<string, unknown> = {}) => ({
  tool: 'mcp__hephaisto__conclude',
  input: { summary: 's', confidence: 0.5, findings: [{ category: 'application', primary: true, confidence: 0.5, hypothesis: 'h', evidence }], ...extra },
});

describe('investigate through main() with the fake SDK and a real MCP endpoint', () => {
  it('c15: the shipped shop-api script concludes, citing the templated step id and a verbatim excerpt', async () => {
    const src = makeSourceRepo(w);
    const req = investigateRequest(w, stub.url, {
      source: { url: src.url, default_branch: 'main', path: '', ref: src.sha, image: `ghcr.io/flou21/hephaisto-fixture-dotnet:c15-${src.sha}` },
    });
    const { doc, valid, stdout } = await runRequest(w, req, { CODEFIX_FAKE_SCRIPT_DIR: SHIPPED });
    expect(valid).toBe(true);
    expectValid(doc);
    expect(doc.outcome).toBe('concluded');
    expect(doc.error).toBeNull();
    expect(doc.billing).toBe('fake');
    expect(doc.context_sha).toBe(git(w.context, 'rev-parse', 'HEAD'));
    expect(doc.source).toEqual({ cloned: true, analysed_ref: src.sha, error: null });
    expect(doc.code_refs).toEqual([{ finding: 0, path: 'src/Shop.Api/Startup/Endpoints.cs', line: 17, end_line: null, note: 'options.Endpoints.Count is read without a null check' }]);
    expect(doc.denied_tool_calls).toEqual([]);

    // what reached the endpoint: the target namespace, the captured pod name, then conclude
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods', 'get_pod_logs', 'conclude']);
    expect(stub.calls[0]!.arguments).toEqual({ namespace: 'hephaisto-chaos' });
    expect(stub.calls[1]!.arguments).toEqual({ namespace: 'hephaisto-chaos', name: STUB_POD, previous: false });
    const c = stub.calls[2]!;
    expect(c.isError).toBe(false);
    const evidence = (c.arguments.findings as { evidence: { step_id: string; excerpt: string }[] }[])[0]!.evidence;
    expect(evidence[0]).toEqual({ step_id: stub.calls[1]!.stepId, excerpt: STUB_EXCEPTION });
    expect(evidence[1]).toEqual({ step_id: stub.calls[0]!.stepId, excerpt: STUB_POD });
    expect(evidence[0]!.step_id).toMatch(UUID);
    // every endpoint request carried the bearer; the token itself never reached stdout or the log
    expect(stub.requests.every((r) => r.auth)).toBe(true);
    expect(stdout).not.toContain(ENDPOINT_TOKEN);
    expect(stderr.join('')).not.toContain(ENDPOINT_TOKEN);
    // the checkout is the analysed commit, sanitised like a code-fix target
    const clone = join(w.work, 'repos', 'shop');
    expect(git(clone, 'rev-parse', 'HEAD')).toBe(src.sha);
    expect(existsSync(join(clone, '.mcp.json'))).toBe(false);
  });

  it('selects the script by the workload name, not the pod name, and falls back to default', async () => {
    const req = investigateRequest(w, stub.url);
    req.incident.target = { namespace: 'shop', kind: 'Pod', name: 'svc-abc-123', workload: 'shop/Deployment/unknown-svc' };
    const { doc } = await runRequest(w, req, { CODEFIX_FAKE_SCRIPT_DIR: SHIPPED });
    expectValid(doc);
    expect(doc.outcome).toBe('concluded');
    expect(doc.source).toBeNull();
    expect(doc.code_refs).toEqual([]);
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods', 'get_events', 'conclude']);
    expect(stub.calls[0]!.arguments).toEqual({ namespace: 'shop' });
    const f = (stub.calls[2]!.arguments.findings as { category: string; confidence: number }[])[0]!;
    expect(f.category).toBe('unknown');
    expect(f.confidence).toBeLessThan(0.5);
  });

  it('CODEFIX_FAKE_SCRIPT=fail ends without conclude: no_conclusion', async () => {
    const { doc } = await runRequest(w, investigateRequest(w, stub.url), { CODEFIX_FAKE_SCRIPT_DIR: SHIPPED, CODEFIX_FAKE_SCRIPT: 'fail' });
    expectValid(doc);
    expect(doc.outcome).toBe('no_conclusion');
    expect(doc.error).toMatch(/without calling conclude/);
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods']);
  });

  it('a conclude the endpoint rejects (excerpt not verbatim) is not a conclusion', async () => {
    script(w, 'default.investigate.json', {
      steps: [
        { tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' }, capture: { as: 'pods', regex: '(redis-0)' } },
        conclude([{ step_id: '${pods.step}', excerpt: 'this text was never in the result' }]),
      ],
    });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expect(doc.outcome).toBe('no_conclusion');
    expect(stub.calls[1]!.isError).toBe(true);
  });

  it('a dead token is caught before the agent starts: failed, endpoint_unauthorized', async () => {
    script(w, 'default.investigate.json', { steps: [{ throw: 'THE AGENT MUST NOT RUN' }] });
    const req = investigateRequest(w, stub.url);
    req.endpoint.token = 'x'.repeat(40);
    const { doc } = await runRequest(w, req);
    expectValid(doc);
    expect(doc).toMatchObject({ outcome: 'failed', error: 'endpoint_unauthorized' });
    expect(stub.calls).toEqual([]);
  });

  it('a token that dies mid-run (agent restarted) fails the run after two 401s', async () => {
    await stub.close();
    stub = await startMcpStub({ token: ENDPOINT_TOKEN, unauthorizedAfter: 1 });
    script(w, 'default.investigate.json', {
      steps: [
        { tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' }, capture: { as: 'pods', regex: '(redis-0)' } },
        { tool: 'mcp__hephaisto__get_events', input: { namespace: 'x' } },
        { tool: 'mcp__hephaisto__get_events', input: { namespace: 'x' } },
        { sleep_ms: 5_000 },
        conclude([{ step_id: '${pods.step}', excerpt: '${pods}' }]),
      ],
    });
    const started = Date.now();
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc).toMatchObject({ outcome: 'failed', error: 'endpoint_unauthorized' });
    expect(Date.now() - started).toBeLessThan(4_000); // aborted, not played to the end
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods']);
  });

  it('the guard confines the investigator: no shell, no request file, no writes; notes stay readable', async () => {
    script(w, 'default.investigate.json', {
      steps: [
        { tool: 'Bash', input: { command: 'cat /work/in/request.json' } },
        { tool: 'Read', input: { file_path: join(w.root, 'in', 'request.json') } },
        { tool: 'Read', input: { file_path: `${join(w.work, 'context')}/memory/INDEX.md` } },
        { tool: 'Write', input: { file_path: `${join(w.work, 'context')}/memory/INDEX.md`, content: 'pwned' } },
        { tool: 'mcp__grafana__query_loki_logs', input: {} },
        { tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' }, capture: { as: 'pods', regex: '(redis-0)' } },
        conclude([{ step_id: '${pods.step}', excerpt: '${pods}' }]),
      ],
    });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc.outcome).toBe('concluded');
    // Bash and Write are not even offered; the request file is refused by the guard
    const denials = doc.denied_tool_calls as { tool: string; input: string; reason: string }[];
    expect(denials).toEqual([{ tool: 'Read', input: join(w.root, 'in', 'request.json'), reason: expect.stringMatching(/confined/) }]);
    expect(stderr.join('')).toMatch(/Bash denied: No such tool available/);
    expect(stderr.join('')).toMatch(/mcp__grafana__query_loki_logs denied: No such tool available/);
  });

  it('max turns without conclude: ONE resumed turn that may only conclude, and it concludes', async () => {
    script(w, 'default.investigate.json', {
      steps: [
        { tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' }, capture: { as: 'pods', regex: '(redis-0)' } },
        { result: { subtype: 'error_max_turns', cost_usd: 0.3, errors: ['max turns'] } },
      ],
      repair: [
        { tool: 'mcp__hephaisto__get_events', input: { namespace: 'x' } },
        conclude([{ step_id: '${pods.step}', excerpt: '${pods}' }]),
        { result: { cost_usd: 0.05 } },
      ],
    });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc.outcome).toBe('concluded');
    expect(doc.error).toBeNull();
    expect(stub.calls.map((c) => c.name)).toEqual(['list_pods', 'conclude']);
    const denials = doc.denied_tool_calls as { tool: string; reason: string }[];
    expect(denials).toEqual([expect.objectContaining({ tool: 'mcp__hephaisto__get_events', reason: expect.stringMatching(/only mcp__hephaisto__conclude/) })]);
    expect(doc.cost_usd).toBeCloseTo(0.35);
    expect(stderr.join('')).toMatch(/one resumed turn that may only call conclude/);
  });

  it('max turns, and the conclude-now turn does not conclude either: max_turns', async () => {
    script(w, 'default.investigate.json', {
      steps: [{ result: { subtype: 'error_max_turns', cost_usd: 0.3, errors: ['max turns'] } }],
      repair: [{ text: 'I would rather keep looking.' }],
    });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc.outcome).toBe('max_turns');
    expect(doc.error).toMatch(/conclude-now turn did not conclude/);
  });

  it('budget exhausted without conclude, and the conclude-now turn cannot investigate further: budget_exhausted', async () => {
    script(w, 'default.investigate.json', {
      steps: [{ result: { subtype: 'error_max_budget_usd', cost_usd: 2.1, errors: ['budget'] } }],
      repair: [{ tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' } }, { result: { cost_usd: 0.01 } }],
    });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc.outcome).toBe('budget_exhausted');
    expect(stub.calls).toEqual([]);
  });

  it('rate limited', async () => {
    script(w, 'default.investigate.json', { steps: [{ result: { subtype: 'success', is_error: true, api_error_status: 429, text: 'API Error: 429 rate_limit_error', cost_usd: 0 } }] });
    const { doc } = await runRequest(w, investigateRequest(w, stub.url));
    expectValid(doc);
    expect(doc.outcome).toBe('rate_limited');
  });

  it('real mode without a credential reports no_credential, not a crash', async () => {
    const { doc, valid } = await runRequest(w, investigateRequest(w, stub.url), { CODEFIX_SDK: 'real' });
    expect(valid).toBe(true);
    expectValid(doc);
    expect(doc.outcome).toBe('no_credential');
    expect(stub.requests).toEqual([]);
  });

  it('fake mode refuses to start next to a real credential', async () => {
    const { doc } = await runRequest(w, investigateRequest(w, stub.url), { ANTHROPIC_API_KEY: 'sk-ant-' + 'api03-not-a-real-key' });
    expectValid(doc);
    expect(doc.outcome).toBe('failed');
    expect(doc.error).toMatch(/refuses to start/);
  });

  it('a source that cannot be cloned is reported, and the investigation goes on', async () => {
    const req = investigateRequest(w, stub.url, { source: { url: `file://${w.root}/nope.git`, default_branch: 'main', path: '', ref: null, image: null } });
    const { doc } = await runRequest(w, req, { CODEFIX_FAKE_SCRIPT_DIR: SHIPPED });
    expectValid(doc);
    expect(doc.outcome).toBe('concluded');
    expect(doc.source).toMatchObject({ cloned: false, analysed_ref: null });
    expect((doc.source as { error: string }).error).toMatch(/clone/);
    expect(doc.code_refs).toEqual([]);
  });

  it('an invalid request is a framed investigate failure that echoes the attempt', async () => {
    const req = investigateRequest(w, stub.url) as unknown as Record<string, unknown>;
    (req.budget as Record<string, unknown>).max_turns = 0;
    const { doc } = await runRequest(w, req);
    expectValid(doc);
    expect(doc.outcome).toBe('failed');
    expect(doc.error).toMatch(/does not match the contract/);
  });

  it('on SIGTERM, from the real entry point: a valid investigate frame that keeps what it knew', async () => {
    if (!existsSync(join(APP_ROOT, 'dist', 'main.js'))) execFileSync('npx', ['tsc', '-p', 'tsconfig.json'], { cwd: APP_ROOT });
    const reqPath = join(w.root, 'req.json');
    writeFileSync(reqPath, JSON.stringify(investigateRequest(w, stub.url)));
    const child = spawn('node', [join(APP_ROOT, 'dist', 'main.js')], {
      env: { PATH: process.env.PATH, CODEFIX_REQUEST: reqPath, CODEFIX_SDK: 'fake', CODEFIX_FAKE_SCRIPT_DIR: SHIPPED, CODEFIX_FAKE_SCRIPT: 'slow', CODEFIX_WORK_DIR: w.work },
    });
    let stdout = '';
    let err = '';
    child.stdout.on('data', (d) => (stdout += d));
    let sent = false;
    child.stderr.on('data', (d) => {
      err += d;
      if (!sent && /FAKE SDK script/.test(err)) {
        sent = true;
        child.kill('SIGTERM');
      }
    });
    const code = await new Promise<number | null>((res) => child.on('close', res));
    expect(code).toBe(143);
    const p = parseLastFrame(stdout)!;
    expect(p.valid).toBe(true);
    const doc = JSON.parse(p.json);
    expectValid(doc);
    expect(doc).toMatchObject({ outcome: 'failed', billing: 'fake', context_sha: git(w.context, 'rev-parse', 'HEAD') });
    expect(doc.error).toMatch(/SIGTERM/);
    expect(err).not.toContain(ENDPOINT_TOKEN);
  });
});

// =============================================================================================

describe('the investigator posture (options handed to the SDK)', () => {
  it('Hephaisto MCP only, no settings sources, Hephaisto prompt + the workspace section, no output schema, NO_PROXY for the endpoint', async () => {
    script(w, 'default.investigate.json', { steps: [{ tool: 'mcp__hephaisto__list_pods', input: { namespace: 'x' } }] });
    const req = investigateRequest(w, stub.url, { system_prompt: 'HEPHAISTO-PROMPT-MARKER', opening_message: 'OPENING-MARKER' });
    const env = parseEnv({ PATH: process.env.PATH, CODEFIX_SDK: 'fake', CODEFIX_FAKE_SCRIPT_DIR: w.scripts, CODEFIX_WORK_DIR: w.work, HTTPS_PROXY: 'http://squid:3128', NO_PROXY: 'localhost', GITHUB_TOKEN: 'ghp_' + 'x'.repeat(36) });
    const seen: { prompt: string; options: Options }[] = [];
    const query: QueryFn = (p) => {
      seen.push(p);
      return fakeQuery(p, { scriptDir: w.scripts, repoName: '', phase: 'investigate', vars: {}, scripts: ['default.investigate.json'], request: req });
    };
    const r = await runInvestigate(req, { env, paths: workPaths(w.work), deadline: Date.now() + 60_000, abort: new AbortController(), makeQuery: async () => query });
    expect(r.outcome).toBe('no_conclusion');
    const o = seen[0]!.options;
    expect(seen[0]!.prompt).toBe('OPENING-MARKER');
    expect(o.settingSources).toEqual([]);
    expect(o.strictMcpConfig).toBe(true);
    expect(o.mcpServers).toEqual({ hephaisto: { type: 'http', url: stub.url, headers: { Authorization: `Bearer ${ENDPOINT_TOKEN}` } } });
    expect(o.outputFormat).toBeUndefined();
    expect(o.permissionMode).toBe('default');
    expect(o.maxTurns).toBe(req.budget.max_turns);
    expect(o.maxBudgetUsd).toBe(req.budget.max_cost_usd);
    expect(o.tools).toEqual(['Read', 'Grep', 'Glob']);
    expect(o.disallowedTools).toEqual(expect.arrayContaining(['Bash', 'Edit', 'Write', 'WebFetch', 'Task', 'Agent']));
    const sp = String(o.systemPrompt);
    expect(sp.startsWith('HEPHAISTO-PROMPT-MARKER\n\n## Where you are running')).toBe(true);
    expect(sp).toContain(join(w.work, 'context'));
    expect(sp).toContain('No source checkout is available in this run.');
    expect(sp).toContain('Always finish by calling `mcp__hephaisto__conclude`');
    expect(sp).not.toContain('{{');
    // the agent's environment: no git/GitHub credential, no endpoint token, the endpoint host bypasses the proxy
    const e = o.env as NodeJS.ProcessEnv;
    expect(e.GITHUB_TOKEN).toBeUndefined();
    expect(JSON.stringify(e)).not.toContain(ENDPOINT_TOKEN);
    expect(e.NO_PROXY).toBe('localhost,127.0.0.1');
    expect(e.no_proxy).toBe('localhost,127.0.0.1');
    expect(e.HTTPS_PROXY).toBe('http://squid:3128');
    expect(e.GUARD_MODE).toBe('investigate');
  });

  it('the conclude-now prompt is the contract text', () => {
    expect(CONCLUDE_NOW).toBe('Conclude now with the evidence you have; call conclude.');
  });

  it('the agent is aborted at the internal deadline', async () => {
    script(w, 'default.investigate.json', { steps: [{ sleep_ms: 30_000 }] });
    const started = Date.now();
    const r = await runInvestigator({
      systemPrompt: 'x',
      prompt: 'x',
      cwd: w.root,
      env: { PATH: process.env.PATH },
      additionalDirectories: [],
      maxTurns: 5,
      maxBudgetUsd: 1,
      guard: { targetDir: w.root, protectedGlobs: [], readRoots: [w.root] },
      deadline: Date.now() + 300,
      abort: new AbortController(),
      endpoint: { url: stub.url, token: ENDPOINT_TOKEN },
      query: (p) => fakeQuery(p, { scriptDir: w.scripts, repoName: '', phase: 'investigate', vars: {}, scripts: ['default.investigate.json'] }),
    });
    expect(Date.now() - started).toBeLessThan(10_000);
    expect(r.stop).toBe('deadline');
  });
});

describe('pieces', () => {
  const req = (target: InvestigateRequest['incident']['target']) => ({ incident: { title: '', kind: '', severity: '', target } }) as InvestigateRequest;

  it('fake script selection: override, workload segment, target name, default; hostile names never become paths', () => {
    expect(fakeScriptCandidates(req({ namespace: 'n', kind: 'Pod', name: 'catalog-api-56776568f4-b7khz', workload: 'hephaisto-chaos/Deployment/catalog-api' }), undefined)).toEqual([
      'catalog-api.investigate.json',
      'catalog-api-56776568f4-b7khz.investigate.json',
      'default.investigate.json',
    ]);
    expect(fakeScriptCandidates(req({ namespace: 'n', kind: 'Deployment', name: 'shop-api', workload: 'n/Deployment/shop-api' }), undefined)).toEqual(['shop-api.investigate.json', 'default.investigate.json']);
    expect(fakeScriptCandidates(req({ namespace: 'n', kind: 'Pod', name: '../../etc/x', workload: '' }), undefined)).toEqual(['default.investigate.json']);
    expect(fakeScriptCandidates(req({ namespace: 'n', kind: 'Pod', name: 'x', workload: 'x' }), 'slow')).toEqual(['slow.investigate.json']);
    expect(fakeScriptCandidates(req({ namespace: 'n', kind: 'Pod', name: 'x', workload: 'x' }), 'fail.investigate.json')).toEqual(['fail.investigate.json']);
    expect(() => fakeScriptCandidates(req({ namespace: 'n', kind: 'Pod', name: 'x', workload: 'x' }), '../x.json')).toThrow();
  });

  it('templating: captures, their step ids and request fields', () => {
    const caps = new Map([['logs', { value: 'NRE here', step: '0192a6f0-0000-7000-8000-0000000000c1' }]]);
    expect(template({ a: ['${logs}', '${logs.step}'], b: '${request.incident.target.namespace}/${missing}' }, caps, { incident: { target: { namespace: 'ns' } } })).toEqual({
      a: ['NRE here', '0192a6f0-0000-7000-8000-0000000000c1'],
      b: 'ns/',
    });
  });

  it('code refs: only files in the clone, lines within them, paths relative to the root', () => {
    const repo = join(w.root, 'refs');
    mkdirSync(join(repo, 'src', 'Api'), { recursive: true });
    writeFileSync(join(repo, 'src', 'Api', 'A.cs'), 'l1\nl2\nl3\n');
    writeFileSync(join(w.root, 'outside.cs'), 'x\n'.repeat(50));
    const refs = verifyCodeRefs(
      [
        { finding: 0, path: 'src/Api/A.cs', line: 3, end_line: 9, note: 'n' },
        { finding: 1, path: `${repo}/src/Api/A.cs`, line: 1, end_line: 2 },
        { finding: 0, path: 'Api/A.cs', line: 2 },
        { finding: 0, path: 'src/Api/A.cs', line: 4 },
        { finding: 0, path: 'src/Api/Missing.cs', line: 1 },
        { finding: 0, path: '../outside.cs', line: 1 },
        { finding: 10, path: 'src/Api/A.cs', line: 1 },
        { finding: 0, path: 'src/Api/A.cs', line: 0 },
        'garbage',
      ],
      repo,
      'src',
    );
    expect(refs).toEqual([
      { finding: 0, path: 'src/Api/A.cs', line: 3, end_line: null, note: 'n' },
      { finding: 1, path: 'src/Api/A.cs', line: 1, end_line: 2, note: null },
      { finding: 0, path: 'src/Api/A.cs', line: 2, end_line: null, note: null },
    ]);
    expect(verifyCodeRefs(undefined, repo, '')).toEqual([]);
  });

  it('NO_PROXY gains the endpoint host once, in both spellings', () => {
    expect(withNoProxyFor({ NO_PROXY: 'a,b', no_proxy: 'b,c' }, 'http://hephaisto.hephaisto.svc:8084/x')).toMatchObject({ NO_PROXY: 'a,b,c,hephaisto.hephaisto.svc', no_proxy: 'a,b,c,hephaisto.hephaisto.svc' });
    expect(withNoProxyFor({ NO_PROXY: 'hephaisto.hephaisto.svc' }, 'http://hephaisto.hephaisto.svc:8084/x').NO_PROXY).toBe('hephaisto.hephaisto.svc');
    expect(withNoProxyFor({}, 'http://[::1]:8084/x').NO_PROXY).toBe('::1');
  });
});
