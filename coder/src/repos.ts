import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parse as parseYaml } from 'yaml';
import { ALWAYS_PROTECTED } from './guard.js';
import type { RepoEntry, Repos } from './schemas.js';
import { validate } from './schemas.js';

// dev-context/repos.yaml is the runner's knowledge of each repository AND its own opt-in. The
// chart's codeFix.repositories is the operator's authorization; a repository needs both, so a
// repo that is absent here, or present with coderEnabled: false, never reaches the agent.

export const NOT_ENABLED = 'repository not enabled in dev-context repos.yaml';

export function loadRepos(contextDir: string): Repos {
  const p = join(contextDir, 'repos.yaml');
  if (!existsSync(p)) throw new Error('dev-context has no repos.yaml');
  const doc: unknown = parseYaml(readFileSync(p, 'utf8'));
  const v = validate('repos', doc);
  if (!v.ok) throw new Error(`dev-context repos.yaml does not match repos.schema.json: ${v.errors.slice(0, 5).join('; ')}`);
  return doc as Repos;
}

export function normalizeRepoUrl(url: string): string {
  let u = url.trim();
  if (u.startsWith('file://')) u = u.slice('file://'.length);
  u = u.replace(/\/+$/, '').replace(/\.git$/i, '').replace(/\/+$/, '');
  return u.toLowerCase();
}

function hostOf(url: string): string {
  const m = /^[a-z][a-z0-9+.-]*:\/\/([^/]+)/i.exec(url.trim());
  return m ? m[1]!.toLowerCase().replace(/^[^@]*@/, '') : '';
}

function lastSegment(url: string): string {
  return normalizeRepoUrl(url).split('/').filter(Boolean).pop() ?? '';
}

/**
 * URL first. repos.yaml also documents a fallback for local-cluster runs, whose repositories
 * are served by an in-cluster git server under a different URL: the last path segment against
 * `name` / the entry url's last segment. The fallback only applies across HOSTS - a request for
 * github.com/someone-else/CaitMatchingService never borrows TrueRelevance's entry by name.
 */
export function findRepo(repos: Repos, url: string): RepoEntry | undefined {
  const want = normalizeRepoUrl(url);
  const exact = repos.repos.find((r) => normalizeRepoUrl(r.url) === want);
  if (exact) return exact;
  const host = hostOf(url);
  const seg = lastSegment(url);
  if (!seg) return undefined;
  return repos.repos.find(
    (r) => hostOf(r.url) !== host && (r.name.toLowerCase() === seg || lastSegment(r.url) === seg),
  );
}

/** Double opt-in, second half: the entry must exist AND say coderEnabled: true. */
export function enabledRepo(repos: Repos, url: string): RepoEntry | null {
  const r = findRepo(repos, url);
  return r && r.coderEnabled ? r : null;
}

export function protectedGlobs(repos: Repos, repo: RepoEntry): string[] {
  return [...new Set([...ALWAYS_PROTECTED, ...repos.defaults.protectedPaths, ...(repo.protectedPaths ?? [])])];
}

export function repoDirName(repo: RepoEntry): string {
  const n = repo.name.replace(/[^A-Za-z0-9._-]/g, '-');
  return n && n !== '.' && n !== '..' ? n : 'target';
}
