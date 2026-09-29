import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { APP_ROOT } from './config.js';
import type { CodeFixRequest } from './schemas.js';

// Templates are data: dev-context/prompts/*.md wins when present, so prompt iteration is a
// dev-context commit (live for the next attempt), and coder/prompts/*.md is the floor that
// ships in the image. Rendering is `{{name}}` substitution and nothing else - no conditionals,
// no includes - so what a template can do is exactly what the driver hands it.

export type TemplateName = 'plan' | 'implement' | 'evidence-block' | 'pr-body' | 'investigate';

/** investigate.md, the section appended to Hephaisto's investigation prompt. */
export const INVESTIGATE_VARS = ['context_dir', 'memory_dir', 'source_block'] as const;

export const PROMPT_VARS = [
  'attempt_id', 'incident_id', 'phase', 'repo_url', 'repo_name', 'default_branch', 'branch', 'analysed_ref', 'image',
  'incident_title', 'incident_kind', 'incident_severity', 'workload', 'escalation_reason', 'evidence_block',
  'investigation_summary', 'plan_json', 'plan_steps', 'plan_files', 'main_moved', 'commands', 'verification_level',
  'repo_notes', 'result_schema', 'cait_ref', 'memory_dir', 'workspace',
] as const;

export const PR_BODY_VARS = [
  'incident_link', 'summary', 'root_cause', 'evidence_md', 'files', 'deviations', 'verification_table',
  'verification_weak', 'notes', 'cost', 'versions', 'attempt_id', 'incident_id', 'workload', 'image', 'analysed_ref',
  'branch', 'repo_url', 'change_summary', 'approved_by',
] as const;

export function loadTemplate(name: TemplateName, contextDir: string | null): { text: string; source: string } {
  let p = join(APP_ROOT, 'prompts', `${name}.md`);
  if (contextDir && existsSync(join(contextDir, 'prompts', `${name}.md`))) p = join(contextDir, 'prompts', `${name}.md`);
  // <!-- ... --> comments are notes for template authors, not for the model or the PR reader
  const text = readFileSync(p, 'utf8').replace(/<!--[\s\S]*?-->\s*/g, '');
  return { text, source: p };
}

/** `{{ name }}` → value; unknown or missing names render empty. Single pass, so a value can never introduce a placeholder. */
export function render(template: string, vars: Record<string, string | undefined>): string {
  return template.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (_, k: string) => vars[k] ?? '');
}

export function xmlEscape(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/**
 * Every untrusted string in the request - the investigation summary, each finding's hypothesis
 * and each evidence excerpt - goes into ONE <untrusted-evidence> element, escaped, so no excerpt
 * can close the element and start talking as the prompt.
 */
export function buildEvidenceElement(req: CodeFixRequest): string {
  const lines: string[] = ['<untrusted-evidence>'];
  if (req.investigation_summary) lines.push(`<investigation-summary>${xmlEscape(req.investigation_summary)}</investigation-summary>`);
  const ordered = [...req.findings].sort((a, b) => Number(b.primary) - Number(a.primary));
  for (const f of ordered) {
    lines.push(
      `<finding id="${xmlEscape(f.id)}" primary="${f.primary}" category="${xmlEscape(f.category)}" confidence="${f.confidence}">`,
    );
    lines.push(`<hypothesis>${xmlEscape(f.hypothesis)}</hypothesis>`);
    for (const e of f.evidence) {
      lines.push(`<excerpt step="${xmlEscape(e.step_id)}" tool="${xmlEscape(e.tool)}">${xmlEscape(e.excerpt)}</excerpt>`);
    }
    lines.push('</finding>');
  }
  lines.push('</untrusted-evidence>');
  return lines.join('\n');
}

/**
 * The `evidence_block` variable: evidence-block.md is a preamble with no placeholders, placed
 * directly before the escaped element. A template that does carry `{{evidence_element}}` gets
 * it there instead; either way the element appears exactly once and is never dropped.
 */
export function renderEvidenceBlock(req: CodeFixRequest, contextDir: string | null): string {
  const element = buildEvidenceElement(req);
  const { text } = loadTemplate('evidence-block', contextDir);
  const preamble = text.replace(/<\/?untrusted-evidence[^>]*>/g, '');
  const rendered = render(preamble, { evidence_element: element });
  return rendered.includes(element) ? rendered.trim() : `${rendered.trim()}\n\n${element}`;
}

/** For a ```json fence: JSON with every backtick escaped (only possible inside strings), so no value can close the fence. */
export function fencedJson(value: unknown): string {
  return JSON.stringify(value, null, 2).replace(/`/g, '\\u0060');
}

/**
 * The target repo's CLAUDE.md. settingSources ['user'] means Claude Code does NOT load project
 * CLAUDE.md files (the SDK documents "Must include 'project' to load CLAUDE.md files"), so the
 * driver injects it itself. It is team-authored, so it is not in the untrusted block, but it is
 * delimited, and a closing tag inside it is neutralised so it cannot end its own element.
 */
export function repoNotesBlock(claudeMd: string | null): string {
  if (!claudeMd || !claudeMd.trim()) return '';
  const capped = claudeMd.length > 24_000 ? `${claudeMd.slice(0, 24_000)}\n…(truncated)` : claudeMd;
  return `<repo-notes trust="team-authored">\n${capped.replace(/<\/repo-notes/gi, '&lt;/repo-notes')}\n</repo-notes>`;
}

/** Verbatim evidence for the PR body: fenced so nothing in a log line renders as markdown or pings anyone. */
export function evidenceMarkdown(req: CodeFixRequest): string {
  const out: string[] = [];
  const primary = req.findings.filter((f) => f.primary);
  for (const f of primary.length > 0 ? primary : req.findings.slice(0, 1)) {
    out.push(`**Hypothesis** (${f.category}, confidence ${f.confidence}):`);
    out.push(fence(f.hypothesis));
    for (const e of f.evidence.slice(0, 5)) {
      out.push(`\`${e.tool}\`:`);
      out.push(fence(e.excerpt));
    }
  }
  return out.join('\n\n');
}

export function fence(text: string): string {
  const longest = Math.max(2, ...Array.from(text.matchAll(/`+/g), (m) => m[0].length));
  const f = '`'.repeat(longest + 1);
  return `${f}text\n${text}\n${f}`;
}
