import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { implementOutputSchema, planOutputSchema } from '../src/phases.js';
import { minimalFailed } from '../src/result.js';
import { CONTRACTS_DIR, type SchemaName, rawSchema, validate, validateWith } from '../src/schemas.js';

// ajv against the vendored contract is what the runner uses at runtime: for every request before
// anything runs, and for every result before it is written.

function samples(kind: 'valid' | 'invalid') {
  const dir = join(CONTRACTS_DIR, 'samples', kind);
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .map((f) => ({
      file: f,
      schema: (f.startsWith('request') ? 'request' : f.startsWith('plan-result') ? 'plan' : 'implement') as SchemaName,
      doc: JSON.parse(readFileSync(join(dir, f), 'utf8')) as Record<string, unknown>,
    }));
}

describe('vendored samples', () => {
  it('there are valid and invalid samples for every document kind', () => {
    for (const kind of ['valid', 'invalid'] as const) {
      expect(new Set(samples(kind).map((s) => s.schema))).toEqual(new Set(['request', 'plan', 'implement']));
    }
  });
  for (const s of samples('valid')) {
    it(`valid/${s.file} validates`, () => {
      const v = validate(s.schema, s.doc);
      expect(v.errors).toEqual([]);
    });
  }
  for (const s of samples('invalid')) {
    it(`invalid/${s.file} is rejected`, () => {
      expect(validate(s.schema, s.doc).ok).toBe(false);
    });
  }
});

describe('runner-side schema facts', () => {
  const planReq = samples('valid').find((s) => s.file === 'request-plan.json')!.doc;

  it('the minimal failed results are themselves valid', () => {
    expect(validate('plan', minimalFailed('plan', '0192a6f0-0000-7000-8000-000000000001', 'x')).errors).toEqual([]);
    expect(validate('implement', minimalFailed('implement', '0192a6f0-0000-7000-8000-000000000001', 'x')).errors).toEqual([]);
  });
  it('the request accepts the local-cluster git server and file:// URLs, and nothing else', () => {
    for (const url of ['https://github.com/Flou21/x', 'http://coder-git.hephaisto-coder.svc/Flou21/x.git', 'file:///tmp/remote.git']) {
      expect(validate('request', { ...planReq, repository: { ...(planReq.repository as object), url } }).ok).toBe(true);
    }
    for (const url of ['ssh://git@github.com/x', 'git@github.com:x/y.git', '/tmp/remote.git']) {
      expect(validate('request', { ...planReq, repository: { ...(planReq.repository as object), url } }).ok).toBe(false);
    }
  });
  it('an unknown member in a request is rejected', () => {
    expect(validate('request', { ...planReq, extra: 1 }).ok).toBe(false);
  });
  it("the plan agent's output schema is cut from the plan result schema, caps included", () => {
    const s = planOutputSchema() as { properties: Record<string, unknown>; required: string[] };
    const full = rawSchema('plan') as { properties: Record<string, unknown> };
    for (const k of s.required) expect(s.properties[k]).toEqual(full.properties[k]);
    const sample = samples('valid').find((x) => x.file === 'plan-result.json')!.doc;
    const agentPart = Object.fromEntries(s.required.map((k) => [k, sample[k]]));
    expect(validateWith(planOutputSchema(), agentPart).errors).toEqual([]);
    expect(validateWith(planOutputSchema(), { ...agentPart, risk: 'low' }).ok).toBe(false);
  });
  it("the implement agent's output schema accepts files + deviations and nothing else", () => {
    expect(validateWith(implementOutputSchema(), { files: ['a.cs'], deviations: [], summary: 'x' }).ok).toBe(true);
    expect(validateWith(implementOutputSchema(), { files: ['a.cs'] }).ok).toBe(false);
    expect(validateWith(implementOutputSchema(), { files: [], deviations: [], build_passed: true }).ok).toBe(false);
  });
});
