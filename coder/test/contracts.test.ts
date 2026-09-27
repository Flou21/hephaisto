import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { CONTRACTS_DIR, ImplementResultZ, PlanResultZ, RequestZ, ReposZ, type SchemaName, rawSchema } from '../src/schemas.js';

// The vendored contract must be byte-identical to what SCHEMAS.lock pins (so drift shows up as a
// lock change in a PR, never as a runtime ContractViolation), and the zod mirrors that give the
// runner its TypeScript types must describe the same shape as the JSON the runner validates with.

function listJson(dir: string): string[] {
  const out: string[] = [];
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) out.push(...listJson(p));
    else if (e.endsWith('.json')) out.push(relative(CONTRACTS_DIR, p));
  }
  return out.sort();
}

describe('SCHEMAS.lock', () => {
  const lock = readFileSync(join(CONTRACTS_DIR, 'SCHEMAS.lock'), 'utf8')
    .split('\n')
    .filter((l) => l && !l.startsWith('#'))
    .map((l) => {
      const [sha, file] = l.split(/\s+/);
      return { sha: sha!, file: file! };
    });

  it('names a dev-context commit', () => {
    expect(readFileSync(join(CONTRACTS_DIR, 'SCHEMAS.lock'), 'utf8')).toMatch(/^# dev-context [0-9a-f]{40}$/m);
  });
  it('covers exactly the vendored files', () => {
    expect(lock.map((l) => l.file).sort()).toEqual(listJson(CONTRACTS_DIR));
  });
  for (const { sha, file } of lock) {
    it(`pins ${file}`, () => {
      expect(createHash('sha256').update(readFileSync(join(CONTRACTS_DIR, file))).digest('hex')).toBe(sha);
    });
  }
});

// ---- zod <-> JSON Schema shape parity

type Shape = { kind: 'object'; props: Record<string, Shape>; required: string[] } | { kind: 'array'; items: Shape } | { kind: 'leaf'; enum?: string[] };

type Obj = Record<string, unknown>;

/** Follows $ref, switching root when the ref names another vendored file. */
function deref(node: Obj, root: Obj): [Obj, Obj] {
  const ref = node.$ref;
  if (typeof ref !== 'string') return [node, root];
  const [file, pointer = ''] = ref.split('#');
  const base: Obj = file === 'codefix-plan-result.schema.json' ? rawSchema('plan') : root;
  let cur: unknown = base;
  for (const part of pointer.split('/').filter(Boolean)) cur = (cur as Obj)[part];
  return deref(cur as Obj, base);
}

function shape(node: Obj, rootIn: Obj): Shape {
  const [n, root] = deref(node, rootIn);
  const alts = (n.anyOf ?? n.oneOf) as Obj[] | undefined;
  if (alts) {
    const nonNull = alts.map((a) => deref(a, root)).filter(([a]) => a.type !== 'null');
    if (nonNull.length === 1) return shape(nonNull[0]![0], nonNull[0]![1]);
  }
  const type = Array.isArray(n.type) ? (n.type as string[]).filter((t) => t !== 'null')[0] : n.type;
  if (type === 'object' || n.properties) {
    const props = (n.properties ?? {}) as Record<string, Record<string, unknown>>;
    return {
      kind: 'object',
      props: Object.fromEntries(Object.entries(props).map(([k, v]) => [k, shape(v, root)])),
      required: [...((n.required as string[]) ?? [])].sort(),
    };
  }
  if (type === 'array') return { kind: 'array', items: shape((n.items ?? {}) as Record<string, unknown>, root) };
  if (Array.isArray(n.enum)) return { kind: 'leaf', enum: [...(n.enum as string[])].sort() };
  return { kind: 'leaf' };
}

const mirrors: [SchemaName, z.ZodType][] = [
  ['request', RequestZ],
  ['plan', PlanResultZ],
  ['implement', ImplementResultZ],
  ['repos', ReposZ],
];

describe('zod mirrors match the vendored JSON Schemas', () => {
  for (const [name, zod] of mirrors) {
    it(`${name}: same properties, required members and enums at every level`, () => {
      const fromZod = z.toJSONSchema(zod, { io: 'input', unrepresentable: 'any' }) as Record<string, unknown>;
      const expected = shape(rawSchema(name), rawSchema(name));
      // no green that compared nothing: the walk must have reached real properties
      expect(expected.kind === 'object' && Object.keys(expected.props).length >= 2).toBe(true);
      expect(shape(fromZod, fromZod)).toEqual(expected);
    });
  }
});

// ---- the same samples through zod

function samples(kind: 'valid' | 'invalid'): { file: string; schema: SchemaName; doc: unknown }[] {
  const dir = join(CONTRACTS_DIR, 'samples', kind);
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .map((f) => ({
      file: f,
      schema: (f.startsWith('request') ? 'request' : f.startsWith('plan-result') ? 'plan' : 'implement') as SchemaName,
      doc: JSON.parse(readFileSync(join(dir, f), 'utf8')) as unknown,
    }));
}
const zodFor: Record<SchemaName, z.ZodType> = { request: RequestZ, plan: PlanResultZ, implement: ImplementResultZ, repos: ReposZ };

it('the parity walk notices drift (positive control)', () => {
  const drifted = PlanResultZ.extend({ risk: z.string() });
  const fromZod = z.toJSONSchema(drifted, { io: 'input', unrepresentable: 'any' }) as Record<string, unknown>;
  expect(shape(fromZod, fromZod)).not.toEqual(shape(rawSchema('plan'), rawSchema('plan')));
  const nested = RequestZ.extend({ budget: z.object({ max_cost_usd: z.number(), deadline_seconds: z.number(), extra: z.number() }).strict() });
  const n = z.toJSONSchema(nested, { io: 'input', unrepresentable: 'any' }) as Record<string, unknown>;
  expect(shape(n, n)).not.toEqual(shape(rawSchema('request'), rawSchema('request')));
});

describe('zod mirrors agree with the samples', () => {
  for (const s of samples('valid')) {
    it(`accepts valid/${s.file}`, () => {
      const r = zodFor[s.schema].safeParse(s.doc);
      expect(r.success, JSON.stringify(r.error?.issues)).toBe(true);
    });
  }
  for (const s of samples('invalid')) {
    it(`rejects invalid/${s.file}`, () => {
      expect(zodFor[s.schema].safeParse(s.doc).success).toBe(false);
    });
  }
});
