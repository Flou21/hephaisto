import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { RESULT_MAX_BYTES } from '../src/config.js';
import { NIL_UUID, emitResult, finalizeResult, frame, minimalFailed, parseLastFrame, resetEmitted } from '../src/result.js';
import { type ImplementResult, type PlanResult, validate } from '../src/schemas.js';

const ATTEMPT = '0192a6f0-0000-7000-8000-000000000001';

function plan(overrides: Partial<PlanResult> = {}): PlanResult {
  return { ...(minimalFailed('plan', ATTEMPT, '') as PlanResult), outcome: 'planned', error: null, summary: 's', root_cause: 'r', confidence: 0.5, ...overrides };
}

describe('framing', () => {
  it('has the exact shape Hephaisto parses, with the sha and byte count of the UTF-8 JSON', () => {
    const json = JSON.stringify({ a: 'Grüße ✓' });
    const f = frame(json);
    const bytes = Buffer.from(json, 'utf8');
    expect(f).toBe(`---HEPHAISTO-RESULT-BEGIN sha256=${createHash('sha256').update(bytes).digest('hex')} bytes=${bytes.length}---\n${json}\n---HEPHAISTO-RESULT-END---\n`);
    expect(bytes.length).toBeGreaterThan(json.length); // multibyte: bytes, not characters
  });

  it('round-trips, and the last block wins', () => {
    const first = frame(JSON.stringify({ n: 1 }));
    const second = frame(JSON.stringify({ n: 2 }));
    const log = `noise\n${first}more noise\n${second}`;
    const p = parseLastFrame(log)!;
    expect(p.valid).toBe(true);
    expect(JSON.parse(p.json)).toEqual({ n: 2 });
  });

  it('a tampered block no longer verifies', () => {
    const f = frame(JSON.stringify({ outcome: 'planned' })).replace('planned', 'plannex');
    expect(parseLastFrame(f)!.valid).toBe(false);
  });
});

describe('finalizeResult', () => {
  it('echoes attempt_id, phase and contract_version and validates', () => {
    const json = finalizeResult('plan', plan());
    const doc = JSON.parse(json);
    expect(doc).toMatchObject({ contract_version: '1', attempt_id: ATTEMPT, phase: 'plan' });
    expect(validate('plan', doc).ok).toBe(true);
  });

  it('truncates an oversize result to the schema caps and under 512 KiB, still valid', () => {
    const huge = '😀'.repeat(10_000); // 4 bytes each
    const r = plan({
      summary: huge,
      root_cause: huge,
      files: Array.from({ length: 80 }, () => huge),
      steps: Array.from({ length: 40 }, () => huge),
      notes: Array.from({ length: 40 }, () => huge),
      verification: { level: 'none', not_verifiable: Array.from({ length: 40 }, () => huge) },
      denied_tool_calls: Array.from({ length: 80 }, () => ({ tool: huge, input: huge, reason: huge })),
    });
    const json = finalizeResult('plan', r);
    expect(Buffer.byteLength(json, 'utf8')).toBeLessThanOrEqual(RESULT_MAX_BYTES);
    const doc = JSON.parse(json);
    expect(validate('plan', doc).errors).toEqual([]);
    expect(doc.outcome).toBe('planned'); // truncated, not replaced
    expect(doc.denied_tool_calls.length).toBeLessThanOrEqual(50);
    expect(Array.from(doc.summary as string).length).toBeLessThanOrEqual(2000);
  });

  it('keeps the END of log_tail, where the failure is', () => {
    const r = { ...(minimalFailed('implement', ATTEMPT, 'x') as ImplementResult), log_tail: `${'x'.repeat(20_000)}FAILED: Endpoints test` };
    const doc = JSON.parse(finalizeResult('implement', r));
    expect(doc.log_tail.endsWith('FAILED: Endpoints test')).toBe(true);
    expect(Array.from(doc.log_tail as string).length).toBeLessThanOrEqual(8192);
  });

  it('replaces a result that would violate the contract by a minimal failed one saying why', () => {
    const bad = plan({ outcome: 'fixed' as PlanResult['outcome'] });
    const doc = JSON.parse(finalizeResult('plan', bad));
    expect(doc.outcome).toBe('failed');
    expect(doc.error).toMatch(/does not match the contract/);
    expect(validate('plan', doc).ok).toBe(true);
  });

  it('uses the nil uuid when the attempt id is not a uuid', () => {
    const doc = JSON.parse(finalizeResult('implement', { ...(minimalFailed('implement', 'not-a-uuid', 'x') as ImplementResult) }));
    expect(doc.attempt_id).toBe(NIL_UUID);
    expect(validate('implement', doc).ok).toBe(true);
  });
});

describe('emitResult', () => {
  it('writes exactly one framed block', () => {
    resetEmitted();
    const out: string[] = [];
    emitResult('plan', plan(), { write: (s) => out.push(s) });
    emitResult('plan', plan({ summary: 'second' }), { write: (s) => out.push(s) });
    expect(out).toHaveLength(1);
    expect(parseLastFrame(out[0]!)!.valid).toBe(true);
    resetEmitted();
  });
});
