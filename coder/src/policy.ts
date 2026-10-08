import { BINARY_MAX_BYTES } from './config.js';
import type { Git } from './git.js';
import { matchesProtected } from './guard.js';

// The publishing policy: what a diff must not contain to be pushed at all. It is checked twice
// since #116, by the same function - by the coder role on its checkout, so a violation is
// reported before a build is spent on it, and by the publish role on its own copy of exactly
// the commit it is about to push, which is the check that counts.

const SECRET_IN_DIFF = /ghp_|github_pat_|sk-ant-/;

/**
 * The paths base..tip touches, exactly as git stores them. -z, because without it git prints a
 * name with a non-ASCII byte, a quote or a backslash in C quotes (`".github/workflows/\303\251.yml"`),
 * and a quoted name matches no protected glob.
 */
export async function changedFiles(git: Git, base: string, tip: string): Promise<string[]> {
  return (await git.ok(['diff', '--name-only', '-z', '--no-renames', `${base}..${tip}`])).split('\0').filter(Boolean);
}

export interface PolicyVerdict {
  ok: boolean;
  reasons: string[];
}

/**
 * `base..tip`, by commit: no protected paths, no big binaries, no credential shapes.
 *
 * --text and --no-textconv: a `.gitattributes` in the diff itself (`* -diff`) or a diff driver
 * would otherwise turn any file into "Binary files differ", and the credential check would read
 * nothing. Both checks read what git stores, never what an attribute says about it.
 */
export async function policyCheck(git: Git, base: string, tip: string, files: string[], globs: string[]): Promise<PolicyVerdict> {
  const reasons: string[] = [];
  for (const f of files) {
    const hit = matchesProtected(f, globs);
    if (hit) reasons.push(`${f} is a protected path (${hit})`);
  }
  const numstat = await git.ok(['diff', '--numstat', '-z', '--no-renames', '--no-ext-diff', '--no-textconv', `${base}..${tip}`]);
  for (const line of numstat.split('\0').filter(Boolean)) {
    const [add, del, path] = line.split('\t');
    if (add === '-' && del === '-' && path) {
      const size = await git.try(['cat-file', '-s', `${tip}:${path}`]);
      if (size.code === 0 && Number(size.stdout.trim()) > BINARY_MAX_BYTES) reasons.push(`${path} is a binary larger than 1 MB`);
    }
  }
  const diff = await git.ok(['diff', '--no-color', '--no-ext-diff', '--no-textconv', '--text', `${base}..${tip}`]);
  const added = diff.split('\n').filter((l) => l.startsWith('+') && !l.startsWith('+++'));
  if (added.some((l) => SECRET_IN_DIFF.test(l))) reasons.push('the diff adds a line that looks like a credential (ghp_/github_pat_/sk-ant-)');
  return { ok: reasons.length === 0, reasons };
}
