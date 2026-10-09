import { describe, expect, it } from 'vitest';
import { draftReasons, openedAs } from '../src/pr.js';
import { ReposZ, validate } from '../src/schemas.js';
import type { VerificationReport } from '../src/verify.js';

// #297: a pull request is a draft when something speaks against a review, and no longer because
// every one is. What speaks against it is decided in one function, from what the runner saw.

const report = (level: VerificationReport['level']): VerificationReport => ({
  level,
  steps: [],
  buildPassed: level !== 'none',
  testsPassed: level === 'tests',
  failed: null,
  logTail: '',
  honestyNote: '',
});

describe('draftReasons', () => {
  it('true is "always": a reason whatever the runner saw, and it names the setting', () => {
    const why = draftReasons(true, report('tests'), []);
    expect(why).toHaveLength(1);
    expect(why[0]).toContain('defaults.pr.draft: true');
  });

  it('unless-ready has none when a check passed and nothing was reported - at any level but none', () => {
    for (const level of ['tests', 'typecheck-only', 'build-only'] as const) expect(draftReasons('unless-ready', report(level), [])).toEqual([]);
  });

  it('unless-ready: no check to run is a reason', () => {
    expect(draftReasons('unless-ready', report('none'), [])).toEqual([expect.stringContaining('no check to run')]);
  });

  it('unless-ready: a reported deviation is a reason, counted and not quoted', () => {
    // what a model wrote goes into the description through `inert`, in the list above this
    // sentence; the sentence itself is the runner's and repeats none of it
    const one = draftReasons('unless-ready', report('tests'), ['@octocat fixes #1']);
    expect(one).toEqual(['a deviation from the approved plan was reported (listed above)']);
    expect(draftReasons('unless-ready', report('tests'), ['a', 'b'])).toEqual(['2 deviations from the approved plan were reported (listed above)']);
  });

  it('unless-ready: both reasons are both said', () => {
    expect(draftReasons('unless-ready', report('none'), ['a'])).toHaveLength(2);
  });
});

describe('openedAs', () => {
  it('names the reasons of a draft', () => {
    expect(openedAs(['one', 'two'], report('tests'))).toBe('**Opened as a draft:** one; two.');
  });

  it('names the level a ready pull request was verified at', () => {
    expect(openedAs([], report('typecheck-only'))).toBe(
      "**Opened ready for review:** the runner's own checks passed (level `typecheck-only`) and no deviation from the approved plan was reported.",
    );
  });
});

describe('repos.yaml defaults.pr.draft', () => {
  const repos = (draft: unknown) => ({
    defaults: { pr: { assignee: 'someone', labels: [], branchPrefix: 'hephaisto/', draft }, clone: { filter: 'blob:none' }, imageTagIsCommitSha: true, protectedPaths: [] },
    repos: [],
  });

  it.each([true, 'unless-ready'])('%s is a value, for the vendored schema and for its mirror', (draft) => {
    expect(validate('repos', repos(draft))).toMatchObject({ ok: true });
    expect(ReposZ.safeParse(repos(draft)).success).toBe(true);
  });

  // `false` would read as "never a draft": a pull request that asks for a review although
  // nothing was checked. There is no such setting.
  it.each([false, 'never', 'always', null])('%s is not', (draft) => {
    expect(validate('repos', repos(draft)).ok).toBe(false);
    expect(ReposZ.safeParse(repos(draft)).success).toBe(false);
  });
});
