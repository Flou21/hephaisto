import { randomUUID } from 'node:crypto';
import { type IncomingMessage, type Server, type ServerResponse, createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

// A localhost stand-in for Hephaisto's investigator endpoint, speaking the same protocol: MCP
// Streamable HTTP, stateless, JSON responses (no SSE), Authorization: Bearer, GET -> 405,
// notifications -> 202. Every tool result starts with `[step <uuid>] <tool>`, and `conclude`
// is checked the way Hephaisto checks it: every evidence item must cite an issued step id with an
// excerpt that occurs verbatim in that step's result. It records every call.

export interface StubCall {
  name: string;
  arguments: Record<string, unknown>;
  stepId: string | null;
  text: string;
  isError: boolean;
}

export interface StubOptions {
  token: string;
  /** Answer 401 to everything once this many tools/call requests have succeeded. */
  unauthorizedAfter?: number;
  /** Per-tool result bodies (after the [step] header); `(args) => string` for arguments-dependent text. */
  results?: Record<string, string | ((args: Record<string, unknown>) => string)>;
}

export interface McpStub {
  url: string;
  calls: StubCall[];
  /** Every HTTP request: method, JSON-RPC method, the Authorization header's presence. */
  requests: { http: string; rpc: string | null; auth: boolean }[];
  close(): Promise<void>;
}

export const STUB_POD = 'shop-api-7f9c6d5b8-abcde';
export const STUB_EXCEPTION = 'NullReferenceException: Object reference not set to an instance of an object.';

const DEFAULT_RESULTS: Record<string, string | ((args: Record<string, unknown>) => string)> = {
  list_pods: (a) => `NAME                        READY  STATUS            RESTARTS  AGE\n${STUB_POD}   0/1    CrashLoopBackOff  7         12m\nredis-0                     1/1    Running           0         3d\n(namespace ${String(a.namespace)})`,
  get_pod_logs: () => `Unhandled exception. System.${STUB_EXCEPTION}\n   at Shop.Api.Startup.Endpoints.Primary(ShopOptions options) in /src/Shop.Api/Startup/Endpoints.cs:line 17\n   at Program.<Main>$(String[] args)`,
  get_events: () => 'Warning  BackOff  pod/shop-api-7f9c6d5b8-abcde  Back-off restarting failed container (x7 over 12m)',
};

const TOOLS = ['list_pods', 'get_pod_logs', 'get_events', 'describe_pod', 'conclude'].map((name) => ({
  name,
  description: `stub ${name}`,
  inputSchema: { type: 'object', properties: {}, additionalProperties: true },
}));

export async function startMcpStub(opts: StubOptions): Promise<McpStub> {
  const calls: StubCall[] = [];
  const requests: McpStub['requests'] = [];
  const steps = new Map<string, string>();
  let succeeded = 0;
  let concluded = false;
  const results = { ...DEFAULT_RESULTS, ...(opts.results ?? {}) };

  const json = (res: ServerResponse, status: number, body: unknown) => {
    res.writeHead(status, { 'content-type': 'application/json' });
    res.end(JSON.stringify(body));
  };

  const toolCall = (name: string, args: Record<string, unknown>): { text: string; isError: boolean; stepId: string | null } => {
    if (concluded) return { text: 'the investigation is already concluded; no further calls are accepted', isError: true, stepId: null };
    const stepId = randomUUID();
    if (name === 'conclude') {
      const findings = Array.isArray(args.findings) ? (args.findings as Record<string, unknown>[]) : [];
      if (findings.length === 0) return { text: 'conclude rejected: at least one finding is required', isError: true, stepId: null };
      for (const f of findings) {
        for (const e of (Array.isArray(f.evidence) ? f.evidence : []) as { step_id?: string; excerpt?: string }[]) {
          const src = steps.get(e.step_id ?? '');
          if (!src) return { text: `conclude rejected: step ${e.step_id} was never issued`, isError: true, stepId: null };
          if (!e.excerpt || !src.includes(e.excerpt)) return { text: `conclude rejected: the excerpt is not verbatim in step ${e.step_id}`, isError: true, stepId: null };
        }
      }
      concluded = true;
      return { text: `[step ${stepId}] conclude\nrecorded ${findings.length} finding(s)`, isError: false, stepId };
    }
    const r = results[name];
    if (r === undefined) return { text: `unknown tool ${name}`, isError: true, stepId: null };
    const body = typeof r === 'function' ? r(args) : r;
    const text = `[step ${stepId}] ${name}\n${body}`;
    steps.set(stepId, text);
    return { text, isError: false, stepId };
  };

  const handle = async (req: IncomingMessage, res: ServerResponse) => {
    let raw = '';
    for await (const c of req) raw += c;
    const auth = req.headers.authorization ?? '';
    let rpc: { jsonrpc: string; id?: number | string; method?: string; params?: Record<string, unknown> } | null = null;
    try {
      rpc = raw ? JSON.parse(raw) : null;
    } catch {
      rpc = null;
    }
    requests.push({ http: req.method ?? '?', rpc: rpc?.method ?? null, auth: auth.length > 0 });
    const unauthorized = auth !== `Bearer ${opts.token}` || (opts.unauthorizedAfter !== undefined && succeeded >= opts.unauthorizedAfter);
    if (unauthorized) {
      res.writeHead(401, { 'content-type': 'application/json', 'www-authenticate': 'Bearer' });
      res.end('{"error":"unauthorized"}');
      return;
    }
    if (req.method !== 'POST') {
      res.writeHead(405, { allow: 'POST' });
      res.end();
      return;
    }
    if (!rpc || typeof rpc.method !== 'string') return json(res, 400, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'parse error' } });
    if (rpc.id === undefined) {
      res.writeHead(202);
      res.end();
      return;
    }
    const ok = (result: unknown) => json(res, 200, { jsonrpc: '2.0', id: rpc!.id, result });
    switch (rpc.method) {
      case 'initialize':
        return ok({ protocolVersion: (rpc.params?.protocolVersion as string) ?? '2025-06-18', capabilities: { tools: {} }, serverInfo: { name: 'hephaisto-stub', version: '1' } });
      case 'ping':
        return ok({});
      case 'tools/list':
        return ok({ tools: TOOLS });
      case 'tools/call': {
        const name = String(rpc.params?.name ?? '');
        const args = (rpc.params?.arguments ?? {}) as Record<string, unknown>;
        const r = toolCall(name, args);
        calls.push({ name, arguments: args, stepId: r.stepId, text: r.text, isError: r.isError });
        if (!r.isError) succeeded++;
        return ok({ content: [{ type: 'text', text: r.text }], isError: r.isError });
      }
      default:
        return json(res, 200, { jsonrpc: '2.0', id: rpc.id, error: { code: -32601, message: `method ${rpc.method} not found` } });
    }
  };

  const server: Server = createServer((req, res) => {
    handle(req, res).catch((e) => {
      res.writeHead(500);
      res.end(String(e));
    });
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const port = (server.address() as AddressInfo).port;
  return {
    url: `http://127.0.0.1:${port}/investigator/mcp`,
    calls,
    requests,
    close: () =>
      new Promise<void>((r) => {
        server.closeAllConnections?.();
        server.close(() => r());
      }),
  };
}
