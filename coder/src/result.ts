import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync, writeSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RESULT_MAX_BYTES } from './config.js';
import { log } from './log.js';
import { type ImplementResult, type Phase, type PlanResult, rawSchema, validate } from './schemas.js';

// The runner's only output that matters. Hephaisto reads the last 1 MiB of the pod log, takes
// the LAST begin/end pair, and checks bytes + sha256 before it deserialises anything, so the
// frame is the contract as much as the JSON is.

export const BEGIN = '---HEPHAISTO-RESULT-BEGIN';
export const END = '---HEPHAISTO-RESULT-END---';
export const NIL_UUID = '00000000-0000-0000-0000-000000000000';

export function frame(json: string): string {
  const bytes = Buffer.from(json, 'utf8');
  const sha = createHash('sha256').update(bytes).digest('hex');
  return `${BEGIN} sha256=${sha} bytes=${bytes.length}---\n${json}\n${END}\n`;
}

export interface ParsedFrame {
  json: string;
  sha256: string;
  bytes: number;
  valid: boolean;
}

/** The parser Hephaisto implements, in miniature: last pair wins, bytes and sha must both match. */
export function parseLastFrame(text: string): ParsedFrame | null {
  const re = /---HEPHAISTO-RESULT-BEGIN sha256=([0-9a-f]{64}) bytes=(\d+)---\n([\s\S]*?)\n---HEPHAISTO-RESULT-END---/g;
  let last: RegExpExecArray | null = null;
  for (let m = re.exec(text); m; m = re.exec(text)) last = m;
  if (!last) return null;
  const [, sha, n, json] = last as unknown as [string, string, string, string];
  const buf = Buffer.from(json, 'utf8');
  const valid = buf.length === Number(n) && createHash('sha256').update(buf).digest('hex') === sha;
  return { json, sha256: sha, bytes: Number(n), valid };
}

// ---------------------------------------------------------------------------------------------
// Caps. Walk the result schema itself, so a cap changed in dev-context is a cap changed here.

type Schema = Record<string, unknown>;

function resolveRef(ref: string, current: Schema): Schema {
  const [file, pointer = ''] = ref.split('#');
  let base = current;
  if (file) {
    const byFile: Record<string, Schema> = {
      'codefix-plan-result.schema.json': rawSchema('plan'),
      'codefix-implement-result.schema.json': rawSchema('implement'),
      'codefix-request.schema.json': rawSchema('request'),
    };
    base = byFile[file] ?? current;
  }
  let node: unknown = base;
  for (const part of pointer.split('/').filter(Boolean)) node = (node as Schema)[part];
  return node as Schema;
}

function truncateString(s: string, max: number, keepTail: boolean): string {
  const cps = Array.from(s);
  if (cps.length <= max) return s;
  if (max <= 1) return cps.slice(0, Math.max(0, max)).join('');
  return keepTail ? `…${cps.slice(cps.length - (max - 1)).join('')}` : `${cps.slice(0, max - 1).join('')}…`;
}

/** Clamps every string and array to its schema cap, scaled by `factor` when the whole still does not fit. */
export function clampToSchema(value: unknown, schema: Schema, root: Schema, factor = 1, key = ''): unknown {
  if (typeof schema.$ref === 'string') return clampToSchema(value, resolveRef(schema.$ref, root), root, factor, key);
  if (typeof value === 'string' && typeof schema.maxLength === 'number') {
    return truncateString(value, Math.max(1, Math.floor(schema.maxLength * factor)), key === 'log_tail');
  }
  if (Array.isArray(value)) {
    const maxItems = typeof schema.maxItems === 'number' ? Math.max(0, Math.floor(schema.maxItems * factor)) : value.length;
    const items = (schema.items as Schema | undefined) ?? {};
    return value.slice(0, maxItems).map((v) => clampToSchema(v, items, root, factor));
  }
  if (value && typeof value === 'object' && schema.properties) {
    const props = schema.properties as Record<string, Schema>;
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = props[k] ? clampToSchema(v, props[k], root, factor, k) : v;
    }
    return out;
  }
  return value;
}

export function minimalFailed(phase: Phase, attemptId: string, error: string): PlanResult | ImplementResult {
  const err = truncateString(error, 4000, false);
  if (phase === 'plan') {
    return {
      contract_version: '1',
      attempt_id: attemptId,
      phase: 'plan',
      outcome: 'failed',
      summary: '',
      root_cause: '',
      confidence: 0,
      files: [],
      steps: [],
      verification: { level: 'none', not_verifiable: [] },
      needs_cait: false,
      notes: [],
      analysed_ref: null,
      context_sha: null,
      cost_usd: 0,
      session_id: null,
      error: err,
      denied_tool_calls: [],
    };
  }
  return {
    contract_version: '1',
    attempt_id: attemptId,
    phase: 'implement',
    outcome: 'failed',
    branch: null,
    pr_url: null,
    pr_number: null,
    base_commit: null,
    files: [],
    build_passed: false,
    tests_passed: false,
    log_tail: '',
    deviations: [],
    cost_usd: 0,
    session_id: null,
    error: err,
    denied_tool_calls: [],
  };
}

/**
 * Turns a result into the exact JSON that goes on the wire: clamped to the schema caps, under
 * 512 KiB, and valid against the vendored result schema. If it cannot be made valid, the
 * result is REPLACED by a minimal `failed` one that says why - never written as-is.
 */
export function finalizeResult(phase: Phase, result: PlanResult | ImplementResult): string {
  const schema = rawSchema(phase);
  const attemptId = typeof result.attempt_id === 'string' && validUuid(result.attempt_id) ? result.attempt_id : NIL_UUID;
  const fixed = { ...result, contract_version: '1', phase, attempt_id: attemptId, cost_usd: sanitiseCost(result.cost_usd) };
  for (let factor = 1; factor >= 1 / 256; factor /= 2) {
    const clamped = clampToSchema(fixed, schema, schema, factor);
    const json = JSON.stringify(clamped);
    if (Buffer.byteLength(json, 'utf8') > RESULT_MAX_BYTES) continue;
    const v = validate(phase, clamped);
    if (v.ok) return json;
    const fallback = minimalFailed(phase, attemptId, `runner produced a result that does not match the contract: ${v.errors.join('; ')}`);
    log.error(`result failed schema validation, replaced by a minimal failed result: ${v.errors.join('; ')}`);
    return JSON.stringify(clampToSchema(fallback, schema, schema));
  }
  return JSON.stringify(minimalFailed(phase, attemptId, 'result could not be reduced below 512 KiB'));
}

function sanitiseCost(c: unknown): number {
  return typeof c === 'number' && Number.isFinite(c) && c >= 0 ? Math.round(c * 1e6) / 1e6 : 0;
}

export function validUuid(s: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(s);
}

// ---------------------------------------------------------------------------------------------
// Emission: exactly once per process, synchronously, so an exit right after cannot lose it.

let emitted = false;

export function hasEmitted(): boolean {
  return emitted;
}

/** For tests that drive several runs in one process. */
export function resetEmitted(): void {
  emitted = false;
}

export interface EmitOptions {
  /** `file:///path` also writes the framed block there (eval harness). */
  sink?: string | undefined;
  write?: (s: string) => void;
}

export function emitResult(phase: Phase, result: PlanResult | ImplementResult, opts: EmitOptions = {}): string | null {
  if (emitted) {
    log.warn('a result was already emitted; ignoring a second one');
    return null;
  }
  emitted = true;
  const json = finalizeResult(phase, result);
  const framed = frame(json);
  if (opts.sink?.startsWith('file://')) {
    try {
      const p = fileURLToPath(opts.sink);
      mkdirSync(dirname(p), { recursive: true });
      writeFileSync(p, framed);
    } catch (e) {
      log.warn(`could not write result sink ${opts.sink}: ${(e as Error).message}`);
    }
  }
  if (opts.write) opts.write(framed);
  else writeSync(1, framed);
  return json;
}
