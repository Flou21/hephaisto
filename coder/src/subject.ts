import type { TemplateName } from './prompts.js';
import type { CodeFixRequest, IncidentRequest, WorkItemRequest } from './schemas.js';

// What a code-fix request is FOR: an incident (contract version 1), or a piece of work somebody
// handed over - a GitHub issue assigned to Hephaisto's account (version 2, v0.14.0).
//
// Everything after "what is this for" is the same for both: the clone, the guard, the agent, the
// verification, the bundle, the push, the draft pull request. What differs is here, so that no
// other file asks `req.incident` of a request that has none: which templates are rendered, which
// trailer a commit carries, whether there is a running image to analyse, and which words of the
// request were written by a stranger.

export function isWorkItem(req: CodeFixRequest): req is WorkItemRequest {
  return req.contract_version === '2';
}

export interface Subject {
  kind: 'incident' | 'work_item';
  /**
   * For a heading, a log line and the trailer: the incident's id, or `owner/repo#12`. Never text
   * somebody typed - the id is a uuid, and a work item's repository is Hephaisto's configuration
   * and its number an integer, both held to a pattern by the contract.
   */
  ref: string;
  /** The first trailer of every commit of the attempt; `Hephaisto-Attempt` is the second. */
  trailer: string;
  /** repo:tag of what is running. Null for a work item: an issue names a repository, not something that runs. */
  image: string | null;
  templates: { plan: TemplateName; implement: TemplateName; prBody: TemplateName };
}

export function subjectOf(req: CodeFixRequest): Subject {
  if (isWorkItem(req)) {
    const ref = `${req.work_item.repository}#${req.work_item.number}`;
    return {
      kind: 'work_item',
      ref,
      trailer: `Hephaisto-Issue: ${ref}`,
      image: null,
      templates: { plan: 'plan-issue', implement: 'implement-issue', prBody: 'pr-body-issue' },
    };
  }
  return {
    kind: 'incident',
    ref: req.incident_id,
    trailer: `Hephaisto-Incident: ${req.incident_id}`,
    image: req.incident.image,
    templates: { plan: 'plan', implement: 'implement', prBody: 'pr-body' },
  };
}

/** The two trailers the agent is told to write, and the driver writes on its own final commit. */
export function trailersOf(req: CodeFixRequest): string {
  return `${subjectOf(req).trailer}\nHephaisto-Attempt: ${req.attempt_id}`;
}

/** Every string of the request that somebody outside wrote, joined - for the scripted SDK to look for a file name in. */
export function untrustedText(req: CodeFixRequest): string {
  if (isWorkItem(req)) return [req.work_item.title, req.work_item.body, ...req.work_item.comments.map((c) => c.body)].join('\n');
  return [req.investigation_summary ?? '', ...req.findings.flatMap((f) => [f.hypothesis, ...f.evidence.map((e) => e.excerpt)])].join('\n');
}

/**
 * The type of a pull request's title for an issue: `fix` for a bug, `feat` for a feature or an
 * enhancement, `chore` for anything else - including an issue that says nothing about its kind.
 * Compared, never copied: the issue's type is a name somebody chose.
 */
export function prType(type: string | null): 'fix' | 'feat' | 'chore' {
  const t = (type ?? '').trim().toLowerCase();
  if (t === 'bug') return 'fix';
  if (t === 'feature' || t === 'enhancement') return 'feat';
  return 'chore';
}

export type { IncidentRequest, WorkItemRequest };
