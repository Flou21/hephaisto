import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';

// A localhost stand-in for the Anthropic Messages API, so the REAL Agent SDK and the REAL pinned
// Claude Code CLI can run end to end in tests with a dummy key and no network. It answers every
// /v1/messages call from a script keyed on how many tool results the conversation already holds,
// and records each request body - which is how the tests see what the CLI actually sent (which
// CLAUDE.md files it loaded, which tools it offered).

type Block = { type: 'text'; text: string } | { type: 'tool_use'; id: string; name: string; input: unknown };

export interface MockApi {
  url: string;
  requests: { url: string; body: Record<string, unknown> }[];
  close(): Promise<void>;
}

export async function startMockApi(script: { plan: Block[]; implement: Block[] }): Promise<MockApi> {
  const requests: MockApi['requests'] = [];
  let n = 0;
  const server: Server = createServer(async (req, res) => {
    let raw = '';
    for await (const c of req) raw += c;
    n++;
    const url = req.url ?? '';
    if (!url.startsWith('/v1/messages')) {
      res.writeHead(url === '/api/hello' ? 200 : 404, { 'content-type': 'application/json' });
      res.end('{}');
      return;
    }
    if (url.includes('count_tokens')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end('{"input_tokens":100}');
      return;
    }
    const body = JSON.parse(raw) as { tools?: { name: string }[]; messages: unknown[]; stream?: boolean };
    requests.push({ url, body: body as Record<string, unknown> });
    const tools = (body.tools ?? []).map((t) => t.name);
    const results = JSON.stringify(body.messages).split('"tool_result"').length - 1;
    let content: Block[];
    let stop = 'tool_use';
    if (!tools.includes('StructuredOutput')) {
      content = [{ type: 'text', text: 'ok' }];
      stop = 'end_turn';
    } else {
      const seq = tools.includes('Edit') ? script.implement : script.plan;
      content = [seq[Math.min(results, seq.length - 1)]!];
    }
    const message = { id: `msg_${n}`, type: 'message', role: 'assistant', model: 'claude-mock', content, stop_reason: stop, stop_sequence: null, usage: { input_tokens: 100, output_tokens: 50 } };
    if (!body.stream) {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(message));
      return;
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    const ev = (type: string, data: object) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
    ev('message_start', { message: { ...message, content: [], stop_reason: null, usage: { input_tokens: 100, output_tokens: 1 } } });
    content.forEach((b, i) => {
      if (b.type === 'text') {
        ev('content_block_start', { index: i, content_block: { type: 'text', text: '' } });
        ev('content_block_delta', { index: i, delta: { type: 'text_delta', text: b.text } });
      } else {
        ev('content_block_start', { index: i, content_block: { type: 'tool_use', id: b.id, name: b.name, input: {} } });
        ev('content_block_delta', { index: i, delta: { type: 'input_json_delta', partial_json: JSON.stringify(b.input) } });
      }
      ev('content_block_stop', { index: i });
    });
    ev('message_delta', { delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 50 } });
    ev('message_stop', {});
    res.end();
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  const port = (server.address() as AddressInfo).port;
  return {
    url: `http://127.0.0.1:${port}`,
    requests,
    close: () => new Promise<void>((r) => server.close(() => r())),
  };
}
