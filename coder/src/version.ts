import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { APP_ROOT } from './config.js';

/** The line at the foot of every PR body: which runner, SDK, CLI and dev-context produced it. */
export function versionLine(contextSha: string | null): string {
  let coder = '0.0.0-dev';
  let sdk = '?';
  let cli = '?';
  try {
    coder = (JSON.parse(readFileSync(join(APP_ROOT, 'package.json'), 'utf8')) as { version: string }).version;
    const lock = JSON.parse(readFileSync(join(APP_ROOT, 'package-lock.json'), 'utf8')) as { packages: Record<string, { version?: string }> };
    sdk = lock.packages['node_modules/@anthropic-ai/claude-agent-sdk']?.version ?? '?';
    cli = lock.packages['node_modules/@anthropic-ai/claude-code']?.version ?? '?';
  } catch {
    /* versions are informational */
  }
  return `hephaisto-coder ${process.env.CODER_VERSION ?? coder} · agent-sdk ${sdk} · claude-code ${cli} · dev-context ${contextSha ?? '?'}`;
}
