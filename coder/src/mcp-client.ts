import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { log } from './log.js';

// The DRIVER's MCP client for Hephaisto's investigator endpoint (Streamable HTTP, stateless, JSON
// responses, Authorization: Bearer). Two users: the preflight in investigate.ts, which checks the
// token before a single model token is spent, and the fake SDK, which executes a script's
// mcp__hephaisto__* steps against the real endpoint. The real CLI has its own client; nothing
// here is on its path.
//
// The bearer token is a per-run secret: it travels only in the Authorization header, and every
// error message is built here without it.

export class EndpointUnauthorizedError extends Error {
  override name = 'EndpointUnauthorizedError';
}

export interface McpEndpoint {
  url: string;
  token: string;
}

export interface McpToolResult {
  text: string;
  isError: boolean;
  /** The HTTP status when the call failed at the transport, e.g. 401. */
  status: number | null;
}

/** 401 at the transport, however the SDK phrases it. */
export function isUnauthorized(e: unknown): boolean {
  if (e instanceof EndpointUnauthorizedError) return true;
  const err = e as { code?: unknown; name?: string; message?: string } | null;
  if (err?.code === 401) return true;
  return err?.name === 'UnauthorizedError' || /\b401\b|unauthori[sz]ed/i.test(err?.message ?? '');
}

export class InvestigatorClient {
  private client: Client | null = null;

  constructor(
    private readonly endpoint: McpEndpoint,
    private readonly name = 'hephaisto-coder',
  ) {}

  async connect(signal?: AbortSignal): Promise<void> {
    if (this.client) return;
    const transport = new StreamableHTTPClientTransport(new URL(this.endpoint.url), {
      requestInit: { headers: { Authorization: `Bearer ${this.endpoint.token}` }, ...(signal ? { signal } : {}) },
    });
    const client = new Client({ name: this.name, version: '1' }, { capabilities: {} });
    try {
      await client.connect(transport, { timeout: 30_000 });
    } catch (e) {
      await client.close().catch(() => undefined);
      if (isUnauthorized(e)) throw new EndpointUnauthorizedError('the investigator endpoint answered 401 Unauthorized');
      throw new Error(`could not connect to the investigator endpoint: ${(e as Error).message?.slice(0, 300) ?? String(e)}`);
    }
    this.client = client;
  }

  async listTools(): Promise<string[]> {
    await this.connect();
    const r = await this.client!.listTools(undefined, { timeout: 30_000 });
    return r.tools.map((t) => t.name);
  }

  /** Never throws for a tool-level or HTTP failure: the fake hands the text to the "model" as a tool result, like the CLI does. */
  async callTool(name: string, args: Record<string, unknown>, signal?: AbortSignal): Promise<McpToolResult> {
    try {
      await this.connect(signal);
      const r = await this.client!.callTool({ name, arguments: args }, undefined, { timeout: 120_000, ...(signal ? { signal } : {}) });
      const content = (r.content ?? []) as { type: string; text?: string }[];
      const text = content
        .filter((c) => c.type === 'text' && typeof c.text === 'string')
        .map((c) => c.text!)
        .join('\n');
      return { text, isError: r.isError === true, status: null };
    } catch (e) {
      if (isUnauthorized(e)) {
        // a stateless server that forgot the token: the next call must reconnect, and fail the same way
        await this.close();
        return { text: 'MCP error: the investigator endpoint answered HTTP 401 Unauthorized', isError: true, status: 401 };
      }
      log.warn(`MCP ${name} failed: ${(e as Error).message}`);
      return { text: `MCP error: ${(e as Error).message?.slice(0, 1000) ?? String(e)}`, isError: true, status: null };
    }
  }

  async close(): Promise<void> {
    const c = this.client;
    this.client = null;
    if (c) await c.close().catch(() => undefined);
  }
}
