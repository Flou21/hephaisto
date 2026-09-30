import { existsSync, realpathSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { type SimpleCommand, type Word, lex } from './shell.js';

// THE authoritative tool guard. One pure function, used three ways:
//   1. the in-process PreToolUse hook (fires for every tool call, including auto-allowed ones),
//   2. canUseTool (fires for anything not auto-allowed),
//   3. bin/guard, the same function behind the Claude Code hook protocol, which dev-context's
//      settings.json registers as a command hook - defence in depth if 1 is ever bypassed.
// It only ever sees what the model asked for. The driver's own git/gh calls never pass through
// here, which is how "push" can be forbidden to the agent and still happen.

export type GuardMode = 'plan' | 'implement' | 'investigate';

export interface GuardContext {
  /** Realpath of the target clone. Every write must land inside it. */
  targetDir: string;
  /** repos.yaml defaults.protectedPaths + the repo's protectedPaths. */
  protectedGlobs: string[];
  /** The only branch the agent may be on (implement). */
  allowedBranch?: string;
  /** Current directory of the Bash tool, when the hook reports it. */
  cwd?: string;
  /** What `~` expands to. */
  homeDir?: string;
  /** investigate: the only directories Read/Grep/Glob may touch (realpaths: /work/context, /work/repos). */
  readRoots?: string[];
}

export type Verdict = { allow: true } | { allow: false; reason: string };

const ALLOW: Verdict = { allow: true };
const deny = (reason: string): Verdict => ({ allow: false, reason });

/** Never editable, whatever repos.yaml says. */
export const ALWAYS_PROTECTED = ['.github/**', '.claude/**', '.git/**', '.mcp.json', '.gitmodules'];

const READ_TOOLS = new Set(['Read', 'Grep', 'Glob', 'LS', 'NotebookRead']);
const EDIT_TOOLS = new Set(['Edit', 'Write', 'MultiEdit', 'NotebookEdit']);
const HARMLESS_TOOLS = new Set(['TodoWrite', 'StructuredOutput', 'Skill']);
const NETWORK_TOOLS = new Set(['WebFetch', 'WebSearch']);
const AGENT_TOOLS = new Set(['Task', 'Agent']);

// ---------------------------------------------------------------------------------------------

export function evaluate(toolName: string, input: unknown, mode: GuardMode, ctx: GuardContext): Verdict {
  const args = (input && typeof input === 'object' ? input : {}) as Record<string, unknown>;
  if (mode === 'investigate') return evaluateInvestigate(toolName, args, ctx);
  if (toolName === 'Bash') return evaluateBash(args, mode, ctx);
  if (READ_TOOLS.has(toolName)) {
    const p = firstString(args.file_path, args.path, args.notebook_path);
    if (p !== undefined) {
      const bad = forbiddenReadPath(p);
      if (bad) return deny(bad);
    }
    return ALLOW;
  }
  if (EDIT_TOOLS.has(toolName)) {
    if (mode === 'plan') return deny(`${toolName} is not available in the plan phase: planning is read-only`);
    const p = firstString(args.file_path, args.notebook_path);
    if (!p) return deny(`${toolName} without a file path`);
    return checkWritePath(p, ctx.targetDir, ctx, { allowTmp: false });
  }
  if (HARMLESS_TOOLS.has(toolName)) return ALLOW;
  if (NETWORK_TOOLS.has(toolName)) return deny(`${toolName} is not available: the coder has no web access`);
  if (AGENT_TOOLS.has(toolName)) return deny(`${toolName} is not available: the coder does not spawn subagents`);
  if (toolName.startsWith('mcp__')) return deny('MCP tools are not available to the coder');
  return deny(`tool ${toolName} is not on the coder's allowlist`);
}

// ---------------------------------------------------------------------------------------------
// investigate: Hephaisto's MCP tools, and reading two directories. No shell, no writes, no web.

/** The one MCP server the investigator has; the runner registers it under this key. */
export const INVESTIGATOR_SERVER = 'hephaisto';
const INVESTIGATOR_TOOL = /^mcp__hephaisto__[A-Za-z0-9_-]+$/;
const INVESTIGATE_READ_TOOLS = new Set(['Read', 'Grep', 'Glob']);

function evaluateInvestigate(toolName: string, args: Record<string, unknown>, ctx: GuardContext): Verdict {
  if (INVESTIGATOR_TOOL.test(toolName)) return ALLOW;
  if (toolName.startsWith('mcp__')) return deny(`only the ${INVESTIGATOR_SERVER} MCP server is available to the investigator`);
  if (toolName === 'Bash') return deny('the investigator has no shell');
  if (EDIT_TOOLS.has(toolName)) return deny(`${toolName} is not available: the investigator cannot change anything`);
  if (NETWORK_TOOLS.has(toolName)) return deny(`${toolName} is not available: the investigator has no web access`);
  if (AGENT_TOOLS.has(toolName)) return deny(`${toolName} is not available: the investigator does not spawn subagents`);
  if (!INVESTIGATE_READ_TOOLS.has(toolName)) return deny(`tool ${toolName} is not on the investigator's allowlist`);

  const roots = (ctx.readRoots ?? []).map((r) => realish(r));
  if (roots.length === 0) return deny('the investigator has no readable directories configured');
  const base = ctx.cwd ?? ctx.targetDir;
  const paths: string[] = [];
  const p = firstString(args.file_path, args.path);
  if (toolName === 'Read' && p === undefined) return deny('Read without a file path');
  paths.push(p ?? base);
  // An absolute Glob pattern names its own directory: its literal prefix must be inside a root too.
  if (toolName === 'Glob' && typeof args.pattern === 'string') {
    const pattern = args.pattern;
    if (pattern.split(/[\\/]/).includes('..')) return deny('glob patterns may not climb out with ..');
    if (isAbsolute(pattern)) {
      const literal = pattern.split('/').filter((seg, i, all) => !/[*?[{]/.test(all.slice(0, i + 1).join('/')));
      paths.push(literal.join('/') || '/');
    }
  }
  for (const raw of paths) {
    const bad = forbiddenReadPath(raw);
    if (bad) return deny(bad);
    const abs = realish(isAbsolute(raw) ? raw : join(base, raw));
    if (!roots.some((r) => inside(abs, r))) return deny(`${toolName} is confined to ${(ctx.readRoots ?? []).join(' and ')} (${raw})`);
  }
  return ALLOW;
}

function firstString(...xs: unknown[]): string | undefined {
  for (const x of xs) if (typeof x === 'string' && x.length > 0) return x;
  return undefined;
}

// ---------------------------------------------------------------------------------------------
// Paths

function inside(p: string, dir: string): boolean {
  return p === dir || p.startsWith(dir.endsWith(sep) ? dir : dir + sep);
}

/** Resolves symlinks on the longest existing ancestor, so a link inside the repo cannot point a write outside it. */
export function realish(p: string): string {
  const abs = resolve(p);
  let probe = abs;
  const tail: string[] = [];
  while (!existsSync(probe)) {
    const parent = dirname(probe);
    if (parent === probe) return abs;
    tail.unshift(basename(probe));
    probe = parent;
  }
  try {
    return join(realpathSync(probe), ...tail);
  } catch {
    return abs;
  }
}

function forbiddenReadPath(p: string): string | null {
  const abs = resolve(p);
  if (abs === '/proc' || abs.startsWith('/proc/')) return 'access to /proc is not allowed: process environments hold credentials';
  if (abs.startsWith('/sys/')) return 'access to /sys is not allowed';
  if (abs.startsWith('/var/run/secrets') || abs.startsWith('/run/secrets')) return 'mounted secrets are not readable';
  if (/\.credentials(\.json)?$/.test(abs)) return 'credential files are not readable';
  return null;
}

export function globToRegExp(glob: string): RegExp {
  let g = glob.replace(/^\.\//, '').replace(/^\//, '');
  let re = '';
  for (let i = 0; i < g.length; i++) {
    const c = g[i]!;
    if (c === '*') {
      if (g[i + 1] === '*') {
        const slashAfter = g[i + 2] === '/';
        re += slashAfter ? '(?:.*/)?' : '.*';
        i += slashAfter ? 2 : 1;
      } else {
        re += '[^/]*';
      }
    } else if (c === '?') {
      re += '[^/]';
    } else {
      re += c.replace(/[.+^${}()|[\]\\]/g, '\\$&');
    }
  }
  if (re.endsWith('/.*')) re = `${re.slice(0, -3)}(?:/.*)?`;
  g = '';
  return new RegExp(`^${re}$`);
}

/** gitignore-flavoured: a pattern without a slash matches a name at any depth (and everything below it). */
export function matchesProtected(relPath: string, globs: string[]): string | null {
  const rel = relPath.split(sep).join('/');
  const segments = rel.split('/');
  for (const g of globs) {
    const pattern = g.replace(/\/$/, '');
    if (!pattern.includes('/')) {
      const re = globToRegExp(pattern);
      if (segments.some((s) => re.test(s))) return g;
    } else {
      const re = globToRegExp(pattern);
      if (re.test(rel)) return g;
      // a directory pattern protects its contents too
      for (let k = 1; k < segments.length; k++) if (re.test(segments.slice(0, k).join('/'))) return g;
    }
  }
  return null;
}

function checkWritePath(p: string, cwd: string, ctx: GuardContext, opts: { allowTmp: boolean }): Verdict {
  if (p === '/dev/null' || p === '/dev/stdout' || p === '/dev/stderr') return ALLOW;
  const home = ctx.homeDir ?? process.env.HOME ?? '/work/home';
  const expanded = p === '~' ? home : p.startsWith('~/') ? join(home, p.slice(2)) : p;
  const abs = realish(isAbsolute(expanded) ? expanded : join(cwd, expanded));
  const target = realish(ctx.targetDir);
  if (opts.allowTmp && (inside(abs, '/tmp') || inside(abs, realish('/tmp')))) return ALLOW;
  if (!inside(abs, target)) return deny(`writes outside the target repository are not allowed (${p})`);
  const rel = relative(target, abs);
  if (rel === '.git' || rel.startsWith(`.git${sep}`)) return deny('the .git directory is off-limits');
  const hit = matchesProtected(rel, [...ALWAYS_PROTECTED, ...ctx.protectedGlobs]);
  if (hit) return deny(`${rel} is a protected path (${hit})`);
  return ALLOW;
}

// ---------------------------------------------------------------------------------------------
// Bash

const SECRET_NAMES = /^(GITHUB_TOKEN|GH_TOKEN|GH_ENTERPRISE_TOKEN|CLAUDE_CODE_OAUTH_TOKEN|NUGET_GITHUB_TOKEN|ANTHROPIC_API_KEY|ANTHROPIC_AUTH_TOKEN|CODEFIX_GIT_PASSWORD)$/;
const SECRET_LIKE = /(TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|CREDENTIAL|_PAT$)/i;
const SECRET_LITERALS = /\b(GITHUB_TOKEN|GH_TOKEN|CLAUDE_CODE_OAUTH_TOKEN|NUGET_GITHUB_TOKEN|ANTHROPIC_API_KEY|ANTHROPIC_AUTH_TOKEN|CODEFIX_GIT_PASSWORD)\b/;
const DANGEROUS_ASSIGN = /^(PATH|LD_[A-Z_]*|GIT_[A-Z_]*|HTTPS?_PROXY|NO_PROXY|ALL_PROXY|https?_proxy|no_proxy|NODE_OPTIONS|BASH_ENV|ENV|PROMPT_COMMAND|IFS|HOME|CLAUDE_[A-Z_]*|ANTHROPIC_[A-Z_]*|GH_[A-Z_]*|NUGET_[A-Z_]*|DOTNET_[A-Z_]*|MSBUILD[A-Z_]*|PYTHONPATH|PYTHONSTARTUP|npm_config_[a-z_]*|NPM_CONFIG_[A-Z_]*)$/;

const NETWORK_CMDS = new Set(['curl', 'wget', 'nc', 'ncat', 'netcat', 'ssh', 'scp', 'sftp', 'rsync', 'telnet', 'socat', 'ftp', 'tftp', 'aria2c', 'httpie', 'http', 'https', 'lynx', 'links', 'w3m', 'dig', 'nslookup', 'host', 'ping', 'openssl']);
const CLUSTER_CMDS = new Set(['kubectl', 'helm', 'tilt', 'docker', 'podman', 'nerdctl', 'crictl', 'ctr', 'kind', 'k3s', 'k9s', 'kustomize', 'minikube', 'az', 'aws', 'gcloud', 'hcloud', 'terraform']);
const PRIV_CMDS = new Set(['sudo', 'su', 'doas', 'chroot', 'nsenter', 'unshare', 'setpriv', 'capsh']);
const SHELLS = new Set(['sh', 'bash', 'zsh', 'dash', 'ksh', 'fish', 'csh', 'tcsh']);
const ENV_DUMP = new Set(['env', 'printenv', 'export', 'declare', 'typeset', 'compgen', 'readonly']);
const NEVER = new Set(['eval', 'exec', 'source', '.', 'xargs', 'parallel', 'watch', 'crontab', 'at', 'nohup', 'disown', 'trap', 'alias', 'function', 'mkfifo', 'mknod', 'dd', 'shred', 'chmod', 'chown', 'chgrp', 'chattr', 'ln', 'mount', 'umount', 'kill', 'pkill', 'killall', 'strace', 'gdb', 'ltrace', 'perl', 'ruby', 'php', 'lua', 'tclsh', 'expect', 'script', 'screen', 'tmux', 'vi', 'vim', 'nano', 'emacs', 'less', 'more', 'man', 'top', 'htop', 'gh', 'hub', 'glab']);
const WRAPPERS = new Set(['time', 'nice', 'stdbuf', 'timeout', 'command', 'builtin']);
const CONTROL = new Set(['then', 'do', 'else', 'elif', 'if', 'while', 'until', '!', '{', '}']);
const CONTROL_END = new Set(['fi', 'done', 'esac']);

const READ_ONLY = new Set([
  'ls', 'cat', 'head', 'tail', 'wc', 'tree', 'file', 'stat', 'du', 'basename', 'dirname', 'realpath', 'readlink', 'pwd',
  'echo', 'printf', 'sort', 'uniq', 'cut', 'tr', 'diff', 'cmp', 'comm', 'nl', 'xxd', 'od', 'hexdump', 'strings', 'true',
  'false', 'test', '[', '[[', 'which', 'type', 'date', 'uname', 'whoami', 'id', 'column', 'paste', 'fold', 'rev', 'tac',
  'md5sum', 'sha1sum', 'sha256sum', 'grep', 'egrep', 'fgrep', 'rg', 'jq', 'yq', 'find', 'sed', 'awk', 'gawk', 'mawk',
  'git', 'dotnet', 'cd', ':', 'sleep', 'expr', 'seq', 'nproc', 'df', 'ps', 'iconv', 'base64', 'fd', 'fdfind', 'unzip', 'zcat',
  'node', 'bun', 'python', 'python3', 'npm', 'npx', 'bunx', 'set',
]);

interface BashState {
  cwd: string | null; // null = unknown (after `cd $X`, or any cd when subshells make it ambiguous)
  subshell: boolean;
}

function evaluateBash(args: Record<string, unknown>, mode: GuardMode, ctx: GuardContext): Verdict {
  const command = typeof args.command === 'string' ? args.command : '';
  if (!command.trim()) return deny('empty command');
  if (args.run_in_background === true) return deny('background processes are not allowed');
  if (args.dangerouslyDisableSandbox === true) return deny('the sandbox cannot be disabled');

  // raw-string checks first: they hold however the command is quoted or nested
  if (/\/proc(\/|\b)/.test(command)) return deny('access to /proc is not allowed: process environments hold credentials');
  if (/\/dev\/(tcp|udp)\//.test(command)) return deny('network redirections are not allowed');
  if (/\$\{?!/.test(command)) return deny('indirect variable expansion is not allowed');
  if (SECRET_LITERALS.test(command)) return deny('references to credential variables are not allowed');

  const lexed = lex(command);
  if (lexed.malformed) return deny('the command could not be parsed (unbalanced quotes or heredoc); simplify it');
  if (lexed.substitution) return deny('command and process substitution ($(...), `...`, <(...)) are not allowed; run the commands separately');
  if (lexed.indirect) return deny('indirect variable expansion is not allowed');
  for (const v of lexed.variables) {
    if (SECRET_NAMES.test(v) || SECRET_LIKE.test(v)) return deny(`references to credential-like variables are not allowed ($${v})`);
  }

  const state: BashState = { cwd: realish(ctx.cwd ?? ctx.targetDir), subshell: lexed.subshell };
  for (const cmd of lexed.commands) {
    const v = evaluateCommand(cmd, mode, ctx, state);
    if (!v.allow) return v;
  }
  return ALLOW;
}

function evaluateCommand(cmd: SimpleCommand, mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  // redirections apply whatever the command is
  for (const r of cmd.redirects) {
    const v = checkRedirect(r.op, r.target, mode, ctx, state);
    if (!v.allow) return v;
  }

  let words = [...cmd.words];
  // leading assignments
  while (words.length > 0 && /^[A-Za-z_][A-Za-z0-9_]*=/.test(words[0]!.value)) {
    const name = words[0]!.value.split('=')[0]!;
    if (DANGEROUS_ASSIGN.test(name) || SECRET_LIKE.test(name)) return deny(`setting ${name} for a command is not allowed`);
    words.shift();
  }
  // shell grammar words
  while (words.length > 0 && CONTROL.has(words[0]!.value)) words.shift();
  if (words.length === 0) return ALLOW;
  const head = words[0]!.value;
  if (CONTROL_END.has(head)) return words.length === 1 ? ALLOW : deny(`unexpected words after ${head}`);
  if (head === 'for' || head === 'case' || head === 'select' || head === 'in') return ALLOW; // loop headers run nothing
  if (words[1]?.value === '()' || /\(\)$/.test(head)) return deny('shell function definitions are not allowed');

  // wrappers run their argument
  if (WRAPPERS.has(head)) {
    const rest = unwrap(head, words.slice(1));
    if (rest === null) return deny(`${head} is only allowed in front of another command`);
    return evaluateCommand({ words: rest, redirects: [], pipedInput: cmd.pipedInput }, mode, ctx, state);
  }

  if (words[0]!.expands) return deny('the command name comes from a variable, so it cannot be checked');
  const name = head.includes('/') ? head : head;
  const base = basename(name);
  const rest = words.slice(1);

  if (base === 'git') return checkGit(rest, mode, ctx, state);
  if (base === 'gh' || base === 'hub') return deny('gh is reserved to the driver: the agent cannot talk to GitHub');
  if (NETWORK_CMDS.has(base)) return deny(`${base} is a network tool; the coder has no network access`);
  if (CLUSTER_CMDS.has(base)) return deny(`${base} is not allowed: the coder has no cluster or registry access`);
  if (PRIV_CMDS.has(base)) return deny(`${base} is not allowed`);
  if (ENV_DUMP.has(base)) return deny(`${base} is not allowed: it can print the environment`);
  if (base === 'set') return rest.length > 0 && rest.every((w) => /^[-+][a-zA-Z]+$/.test(w.value) || w.value === 'pipefail') ? ALLOW : deny('set without options prints the environment');
  if (NEVER.has(base)) return deny(`${base} is not allowed`);
  if (SHELLS.has(base)) return checkShell(base, rest, cmd.pipedInput, mode, ctx, state);

  // a path to a script inside the repo (./build.sh) is a script the repo could run in its tests anyway
  if (name.includes('/') && !['python', 'python3', 'pip', 'pip3', 'pytest'].includes(base)) {
    if (mode === 'plan') return deny('running scripts is not allowed in the plan phase');
    const abs = state.cwd ? realish(resolve(state.cwd, name)) : null;
    if (!abs || !inside(abs, realish(ctx.targetDir))) return deny(`only scripts inside the target repository may run (${name})`);
    return ALLOW;
  }

  if (base === 'cd') {
    if (rest.length === 0) return ALLOW;
    const t = rest[0]!;
    if (t.expands || state.subshell) state.cwd = null;
    else if (state.cwd) state.cwd = realish(resolve(state.cwd, expandHome(t.value, ctx)));
    return ALLOW;
  }

  switch (base) {
    case 'dotnet': {
      const v = checkDotnet(rest, mode);
      return v.allow ? checkOutputPaths(rest, mode, ctx, state) : v;
    }
    case 'bun':
    case 'bunx':
    case 'npm':
    case 'npx':
    case 'node': {
      if (rest.some((w) => /^--(prefix|cwd|global-folder|cache)(=|$)/.test(w.value))) return deny(`${base} with a relocated prefix/cwd/cache is not allowed`);
      const v = checkJs(base, rest, mode);
      return v.allow ? checkOutputPaths(rest, mode, ctx, state) : v;
    }
    case 'python':
    case 'python3':
    case 'pip':
    case 'pip3':
    case 'pytest':
      return checkPython(base, name, rest, mode, ctx, state);
    case 'find':
      return checkFind(rest);
    case 'sed':
      return checkSed(rest, mode, ctx, state);
    case 'awk':
    case 'gawk':
    case 'mawk':
      return checkAwk(rest);
    case 'jq':
    case 'yq':
      return rest.some((w) => /\benv\b|\$ENV|\$__prog_args|input_filename/.test(w.value)) ? deny(`${base} env access is not allowed`) : ALLOW;
    case 'rg':
      return rest.some((w) => /^--pre(=|$)|^--pre-glob/.test(w.value)) ? deny('rg --pre runs a program and is not allowed') : ALLOW;
    case 'grep':
    case 'egrep':
    case 'fgrep':
      return ALLOW;
    case 'tail':
      return rest.some((w) => /^-[a-zA-Z]*[fF]|^--follow/.test(w.value)) ? deny('tail -f never returns') : ALLOW;
    case 'sort':
      return checkOutputFlag(rest, ['-o', '--output'], mode, ctx, state);
    case 'tree':
      return rest.some((w) => w.value === '-o') ? deny('tree -o writes a file') : ALLOW;
    case 'uniq': {
      const pos = rest.filter((w) => !w.value.startsWith('-'));
      if (pos.length >= 2) return checkWriteWords([pos[1]!], mode, ctx, state, 'uniq');
      return ALLOW;
    }
    case 'unzip':
      return mode === 'plan' && !rest.some((w) => w.value === '-l' || w.value === '-p') ? deny('unzip extracts files; use unzip -l in the plan phase') : ALLOW;
    case 'base64':
    case 'iconv':
      return ALLOW;
  }

  if (READ_ONLY.has(base)) return ALLOW;

  // write commands: implement only, every path inside the target
  if (['rm', 'rmdir', 'mkdir', 'touch', 'cp', 'mv', 'tee', 'truncate', 'patch'].includes(base)) {
    if (mode === 'plan') return deny(`${base} writes files; the plan phase is read-only`);
    return checkFsWrite(base, rest, ctx, state);
  }
  if (base === 'make' || base === 'cmake' || base === 'msbuild') return deny(`${base} is not on the coder's allowlist; use the repository's documented build command`);
  return deny(`${base} is not on the coder's allowlist`);
}

function unwrap(head: string, rest: Word[]): Word[] | null {
  let r = [...rest];
  if (head === 'timeout') {
    while (r[0]?.value.startsWith('-')) r.shift();
    r.shift(); // the duration
  } else if (head === 'nice') {
    if (r[0]?.value === '-n') r = r.slice(2);
    else if (r[0]?.value.startsWith('-')) r.shift();
  } else if (head === 'stdbuf') {
    while (r[0]?.value.startsWith('-')) r.shift();
  } else if (head === 'command') {
    if (r[0]?.value === '-v' || r[0]?.value === '-V') return [{ value: 'true', expands: false, glob: false }];
  } else if (head === 'time') {
    while (r[0]?.value.startsWith('-')) r.shift();
  }
  return r.length > 0 ? r : null;
}

function expandHome(p: string, ctx: GuardContext): string {
  const home = ctx.homeDir ?? process.env.HOME ?? '/work/home';
  return p === '~' ? home : p.startsWith('~/') ? join(home, p.slice(2)) : p;
}

function checkRedirect(op: string, target: Word | null, mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  if (op === '<<' || op === '<<-' || op === '<<<') return ALLOW;
  if (!target) return deny('redirection without a target');
  if ((op === '>&' || op === '<&') && /^(\d+|-)$/.test(target.value)) return ALLOW;
  if (op === '<' || op === '<&') {
    const bad = forbiddenReadPath(expandHome(target.value, ctx));
    return bad ? deny(bad) : ALLOW;
  }
  // everything else writes
  if (['/dev/null', '/dev/stdout', '/dev/stderr'].includes(target.value)) return ALLOW;
  if (mode === 'plan') return deny('the plan phase is read-only: redirecting output to a file is not allowed');
  return checkWriteWords([target], mode, ctx, state, 'redirect');
}

function checkWriteWords(words: Word[], mode: GuardMode, ctx: GuardContext, state: BashState, what: string): Verdict {
  if (mode === 'plan') return deny(`${what} writes files; the plan phase is read-only`);
  for (const w of words) {
    if (w.expands) return deny(`cannot verify a write path built from a variable (${w.value})`);
    let p = expandHome(w.value, ctx);
    if (w.glob) {
      const firstGlob = p.search(/[*?[]/);
      p = dirname(p.slice(0, firstGlob) + 'x');
    }
    if (!isAbsolute(p) && state.cwd === null) return deny('cannot verify a relative write path after cd to a variable');
    const v = checkWritePath(p, state.cwd ?? ctx.targetDir, ctx, { allowTmp: true });
    if (!v.allow) return v;
  }
  return ALLOW;
}

function checkOutputFlag(rest: Word[], flags: string[], mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  for (let i = 0; i < rest.length; i++) {
    const v = rest[i]!.value;
    const eq = flags.find((f) => v.startsWith(`${f}=`));
    if (eq) return checkWriteWords([{ ...rest[i]!, value: v.slice(eq.length + 1) }], mode, ctx, state, v);
    if (flags.includes(v)) {
      const t = rest[i + 1];
      if (!t) return deny(`${v} without a path`);
      return checkWriteWords([t], mode, ctx, state, v);
    }
  }
  return ALLOW;
}

function checkFsWrite(base: string, rest: Word[], ctx: GuardContext, state: BashState): Verdict {
  const paths = rest.filter((w) => !w.value.startsWith('-') || w.value === '-');
  if (paths.length === 0) return base === 'tee' ? ALLOW : deny(`${base} without a path`);
  const target = realish(ctx.targetDir);
  if (base === 'cp') {
    // sources may be anywhere readable (the Cait reference, dev-context); the destination may not
    for (const s of paths.slice(0, -1)) {
      const bad = forbiddenReadPath(expandHome(s.value, ctx));
      if (bad) return deny(bad);
    }
    return checkWriteWords([paths[paths.length - 1]!], 'implement', ctx, state, 'cp');
  }
  if (base === 'rm' || base === 'rmdir') {
    for (const w of paths) {
      if (w.expands) return deny(`cannot verify an rm path built from a variable (${w.value})`);
      const raw = expandHome(w.value, ctx);
      if (!isAbsolute(raw) && state.cwd === null) return deny('cannot verify a relative rm path after cd to a variable');
      const probe = w.glob ? dirname(raw.slice(0, raw.search(/[*?[]/)) + 'x') : raw;
      const abs = realish(isAbsolute(probe) ? probe : join(state.cwd ?? target, probe));
      if (!inside(abs, target)) return deny(`rm outside the target repository is not allowed (${w.value})`);
      if (abs === target && !w.glob) return deny('removing the repository root is not allowed');
      const rel = relative(target, abs);
      if (rel === '.git' || rel.startsWith(`.git${sep}`)) return deny('the .git directory is off-limits');
    }
    return checkWriteWords(paths.filter((w) => !w.glob), 'implement', ctx, state, 'rm');
  }
  return checkWriteWords(paths, 'implement', ctx, state, base);
}

function checkShell(base: string, rest: Word[], piped: boolean, mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  if (piped) return deny(`piping into ${base} is not allowed`);
  if (rest.some((w) => /^-[a-zA-Z]*[cs]/.test(w.value) || w.value === '-i' || w.value === '--init-file' || w.value === '--rcfile')) {
    return deny(`${base} -c and friends are not allowed: run the command directly`);
  }
  if (mode === 'plan') return deny('running scripts is not allowed in the plan phase');
  const script = rest.find((w) => !w.value.startsWith('-'));
  if (!script) return deny(`${base} without a script file is not allowed`);
  if (script.expands) return deny('cannot verify a script path built from a variable');
  const abs = state.cwd ? realish(resolve(state.cwd, script.value)) : null;
  if (!abs || !inside(abs, realish(ctx.targetDir))) return deny(`only scripts inside the target repository may run (${script.value})`);
  return ALLOW;
}

// --- git ---------------------------------------------------------------------------------------

const GIT_READ = new Set(['status', 'log', 'show', 'diff', 'blame', 'branch', 'rev-parse', 'ls-files', 'ls-tree', 'cat-file', 'grep', 'shortlog', 'describe', 'rev-list', 'merge-base', 'name-rev', 'show-ref', 'reflog', 'stash', 'whatchanged', 'annotate', 'count-objects', 'var', 'check-ignore', 'check-attr', 'version', 'help']);
const GIT_WRITE = new Set(['add', 'commit', 'restore', 'mv', 'rm', 'apply', 'reset', 'switch', 'checkout', 'revert']);
const GIT_REASONS: Record<string, string> = {
  push: 'git push is reserved to the driver',
  remote: 'git remote is not allowed: remotes are the driver\'s',
  config: 'git config is not allowed: identity and settings are set by the driver',
  clean: 'git clean is not allowed',
  fetch: 'git fetch is not allowed: the coder has no network access',
  pull: 'git pull is not allowed: the coder has no network access',
  clone: 'git clone is not allowed: the workspace is prepared by the driver',
  submodule: 'git submodule is not allowed',
  'ls-remote': 'git ls-remote is not allowed: the coder has no network access',
  worktree: 'git worktree is not allowed',
  tag: 'git tag is not allowed',
  rebase: 'git rebase is not allowed',
  merge: 'git merge is not allowed',
  'cherry-pick': 'git cherry-pick is not allowed',
  'update-ref': 'git update-ref is not allowed',
  'symbolic-ref': 'git symbolic-ref is not allowed',
  'filter-branch': 'git filter-branch is not allowed',
  credential: 'git credential is not allowed',
};

function checkGit(rest: Word[], mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  let i = 0;
  // global options before the subcommand
  while (i < rest.length && rest[i]!.value.startsWith('-')) {
    const o = rest[i]!.value;
    if (['-c', '--config-env', '-C', '--git-dir', '--work-tree', '--exec-path', '--namespace', '--super-prefix'].some((f) => o === f || o.startsWith(`${f}=`))) {
      return deny(`git ${o} is not allowed`);
    }
    i++;
  }
  const subWord = rest[i];
  if (!subWord) return ALLOW; // bare `git` prints help
  if (subWord.expands) return deny('git subcommand from a variable cannot be checked');
  const sub = subWord.value;
  const args = rest.slice(i + 1);
  const vals = args.map((w) => w.value);

  for (const v of vals) {
    if (/^(--output|--upload-pack|--receive-pack|--exec|-O|--open-files-in-pager)(=|$)/.test(v)) return deny(`git ${sub} ${v} is not allowed`);
  }
  if (GIT_REASONS[sub]) return deny(GIT_REASONS[sub]!);

  if (sub === 'reset') {
    if (vals.some((v) => ['--hard', '--merge', '--keep'].includes(v))) return deny(`git reset ${vals.find((v) => ['--hard', '--merge', '--keep'].includes(v))} is not allowed`);
  }
  if (sub === 'branch') {
    const mutating = vals.find((v) => /^(-[dDmMcCfu]|--delete|--move|--copy|--force|--set-upstream-to|--unset-upstream|--edit-description|--track|--no-track)(=|$)/.test(v));
    if (mutating) return deny(`git branch ${mutating} is not allowed`);
    const listing = vals.some((v) => /^(--list|-l|--contains|--no-contains|--merged|--no-merged|--points-at)(=|$)/.test(v));
    if (!listing && vals.some((v) => !v.startsWith('-'))) return deny('creating branches is not allowed: the driver creates the assigned branch');
    return ALLOW;
  }
  if (sub === 'reflog') {
    if (vals[0] && !vals[0].startsWith('-') && vals[0] !== 'show') return deny(`git reflog ${vals[0]} is not allowed`);
    return ALLOW;
  }
  if (sub === 'stash' && mode === 'plan') {
    if (vals[0] && !['list', 'show'].includes(vals[0])) return deny('git stash modifies the working tree; the plan phase is read-only');
    if (!vals[0]) return deny('git stash modifies the working tree; the plan phase is read-only');
    return ALLOW;
  }
  if (GIT_READ.has(sub)) return ALLOW;
  if (GIT_WRITE.has(sub)) {
    if (mode === 'plan') return deny(`git ${sub} is not allowed in the plan phase: planning is read-only`);
    if (sub === 'switch') {
      if (vals.some((v) => /^(-c|-C|--create|--force-create|--orphan|--detach|-d)(=|$)/.test(v))) return deny('creating or detaching branches is not allowed: stay on the assigned branch');
      const target = vals.find((v) => !v.startsWith('-'));
      if (!ctx.allowedBranch || target !== ctx.allowedBranch) return deny(`switching to ${target ?? '(none)'} is not allowed: stay on ${ctx.allowedBranch ?? 'the assigned branch'}`);
      return ALLOW;
    }
    if (sub === 'checkout') {
      if (vals.some((v) => /^(-b|-B|--orphan|--detach)(=|$)/.test(v))) return deny('creating or detaching branches is not allowed: stay on the assigned branch');
      if (vals.includes('--')) return checkPathsAfterDashDash(args, ctx, state);
      const pos = vals.filter((v) => !v.startsWith('-'));
      if (pos.length === 1 && pos[0] === ctx.allowedBranch) return ALLOW;
      return deny('git checkout is only allowed as `git checkout -- <paths>` or to the assigned branch; use git restore for files');
    }
    if (sub === 'add' || sub === 'rm' || sub === 'mv' || sub === 'restore') {
      const paths = args.filter((w) => !w.value.startsWith('-'));
      if (sub !== 'add' && sub !== 'restore') return checkWriteWords(paths, mode, ctx, state, `git ${sub}`);
      return ALLOW;
    }
    return ALLOW;
  }
  return deny(`git ${sub} is not on the coder's allowlist`);
}

function checkPathsAfterDashDash(args: Word[], ctx: GuardContext, state: BashState): Verdict {
  const idx = args.findIndex((w) => w.value === '--');
  return checkWriteWords(args.slice(idx + 1), 'implement', ctx, state, 'git checkout');
}

// --- stacks ------------------------------------------------------------------------------------

/**
 * Build tools write where they are told: `-o`, `--output`, `--results-directory`, `--diag`,
 * `--outdir`, MSBuild binlogs and file loggers, and `-p:*Path=` / `-p:*Dir=` properties. Every
 * such destination must be inside the target (or /tmp), like any other write - otherwise
 * `dotnet build -o /work/.claude` would be a write the guard never saw.
 */
function checkOutputPaths(rest: Word[], mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  const valued = ['-o', '--output', '--results-directory', '--diag', '-d', '--outdir', '--outfile', '--out-dir'];
  for (let i = 0; i < rest.length; i++) {
    const w = rest[i]!;
    const v = w.value;
    let target: string | null = null;
    if (valued.includes(v)) {
      const next = rest[i + 1];
      if (!next) continue;
      if (next.expands) return deny(`cannot verify an output path built from a variable (${next.value})`);
      target = next.value;
      i++;
    } else {
      const eq = valued.find((f) => f.startsWith('--') && v.startsWith(`${f}=`));
      if (eq) target = v.slice(eq.length + 1);
      const bl = /^[-/](bl|binaryLogger|flp\d?|fileLoggerParameters\d?):(?:.*?logfile=)?([^;]+)/i.exec(v);
      if (bl) target = bl[2]!;
      const prop = /^(?:[-/]p(?:roperty)?:|--property:)([A-Za-z_]*(?:Path|Dir|Directory|Output)[A-Za-z_]*)=(.+)$/i.exec(v);
      if (prop) target = prop[2]!;
    }
    if (target === null) continue;
    if (w.expands || /\$/.test(target)) return deny(`cannot verify an output path built from a variable (${target})`);
    const verdict = checkWriteWords([{ value: target, expands: false, glob: false }], mode, ctx, state, v);
    if (!verdict.allow) return verdict;
  }
  return ALLOW;
}

function checkDotnet(rest: Word[], mode: GuardMode): Verdict {
  const sub = rest[0]?.value;
  const info = ['--info', '--list-sdks', '--list-runtimes', '--version', '-h', '--help'];
  if (sub === undefined || info.includes(sub)) return ALLOW;
  if (['run', 'watch'].includes(sub)) return deny(`dotnet ${sub} is not allowed: the coder never starts the service`);
  if (sub === 'nuget') return deny('dotnet nuget is not allowed');
  if (['restore', 'build', 'test', 'format', 'clean'].includes(sub)) {
    if (mode === 'plan') return deny(`dotnet ${sub} is not allowed in the plan phase: planning is read-only, no builds`);
    return ALLOW;
  }
  return deny(`dotnet ${sub} is not on the coder's allowlist`);
}

function checkJs(base: string, rest: Word[], mode: GuardMode): Verdict {
  const vals = rest.map((w) => w.value);
  if (vals.length === 1 && (vals[0] === '--version' || vals[0] === '-v')) return ALLOW;
  if (base === 'node') return deny('node is not allowed except `node --version`; use the repository\'s scripts');
  if (mode === 'plan') return deny(`${base} ${vals.join(' ')} is not allowed in the plan phase: no installs or builds`);
  const sub = vals[0];
  const safeScripts = ['build', 'test', 'typecheck', 'lint', 'generate', 'test:unit'];
  if (base === 'bun') {
    if (sub === 'install' || sub === 'i') return vals.some((v) => v === '-g' || v === '--global') ? deny('global installs are not allowed') : ALLOW;
    if (sub === 'test') return ALLOW;
    if (sub === 'run') {
      if (vals[1] === 'dev') return deny('bun run dev starts a server and is not allowed');
      return safeScripts.includes(vals[1] ?? '') ? ALLOW : deny(`bun run ${vals[1] ?? ''} is not on the allowlist (${safeScripts.join(', ')})`);
    }
    if (sub === 'x') return checkJsTool(vals.slice(1));
    return deny(`bun ${sub ?? ''} is not on the coder's allowlist`);
  }
  if (base === 'npm') {
    if (sub === 'publish') return deny('npm publish is not allowed');
    if (sub === 'ci') return ALLOW;
    if (sub === 'install' || sub === 'i') return vals.some((v) => v === '-g' || v === '--global') ? deny('global installs are not allowed') : ALLOW;
    if (sub === 'test' || sub === 't') return ALLOW;
    if (sub === 'run' || sub === 'run-script') {
      if (vals[1] === 'dev') return deny('npm run dev starts a server and is not allowed');
      return safeScripts.includes(vals[1] ?? '') ? ALLOW : deny(`npm run ${vals[1] ?? ''} is not on the allowlist (${safeScripts.join(', ')})`);
    }
    return deny(`npm ${sub ?? ''} is not on the coder's allowlist`);
  }
  return checkJsTool(vals); // npx / bunx
}

function checkJsTool(vals: string[]): Verdict {
  const tool = vals.find((v) => !v.startsWith('-'));
  if (tool === 'vue-tsc' || tool === 'tsc') return ALLOW;
  if (tool === 'nuxi' || tool === 'nuxt') return vals.includes('typecheck') ? ALLOW : deny(`${tool} is only allowed for typecheck`);
  return deny(`${tool ?? 'that tool'} is not on the coder's allowlist (vue-tsc, tsc, nuxi typecheck)`);
}

function checkPython(base: string, name: string, rest: Word[], mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  const vals = rest.map((w) => w.value);
  if (vals.length === 1 && (vals[0] === '--version' || vals[0] === '-V')) return ALLOW;
  if (mode === 'plan') return deny(`${base} is not allowed in the plan phase: no tests or installs`);
  if (name.includes('/')) {
    // a venv interpreter: must live inside the target or /tmp
    const abs = state.cwd ? realish(resolve(state.cwd, name)) : null;
    if (!abs || !(inside(abs, realish(ctx.targetDir)) || inside(abs, '/tmp') || inside(abs, realish('/tmp')))) {
      return deny(`only a virtualenv inside the repository may run (${name})`);
    }
  }
  if (base === 'pytest') return ALLOW;
  if (base === 'pip' || base === 'pip3') return checkPip(vals);
  if (vals[0] === '-m') {
    const mod = vals[1];
    if (mod === 'pytest') return ALLOW;
    if (mod === 'venv') {
      const dir = rest.slice(2).filter((w) => !w.value.startsWith('-'));
      return checkWriteWords(dir, mode, ctx, state, 'python -m venv');
    }
    if (mod === 'pip') return checkPip(vals.slice(2));
    return deny(`python -m ${mod ?? ''} is not on the allowlist (pytest, venv, pip install)`);
  }
  return deny('python is only allowed as -m pytest, -m venv or -m pip install; -c and scripts are not');
}

function checkPip(vals: string[]): Verdict {
  if (vals[0] !== 'install') return deny(`pip ${vals[0] ?? ''} is not allowed; only pip install inside a virtualenv`);
  if (vals.some((v) => ['--user', '--target', '-t', '--prefix', '--root', '--index-url', '-i', '--extra-index-url'].includes(v))) return deny('that pip install option is not allowed');
  return ALLOW;
}

function checkFind(rest: Word[]): Verdict {
  const bad = rest.find((w) => ['-exec', '-execdir', '-ok', '-okdir', '-delete', '-fprint', '-fprint0', '-fprintf', '-fls'].includes(w.value));
  return bad ? deny(`find ${bad.value} is not allowed`) : ALLOW;
}

function checkSed(rest: Word[], mode: GuardMode, ctx: GuardContext, state: BashState): Verdict {
  const vals = rest.map((w) => w.value);
  const inPlace = vals.some((v) => /^(-i|--in-place)/.test(v) || /^-[a-zA-Z]*i/.test(v));
  const quiet = vals.some((v) => v === '-n' || v === '--quiet' || v === '--silent' || /^-[a-zA-Z]*n/.test(v));
  // the script: -e arguments, else the first non-option word
  const scripts: string[] = [];
  const files: Word[] = [];
  for (let k = 0; k < rest.length; k++) {
    const v = rest[k]!.value;
    if (v === '-e' || v === '--expression') {
      scripts.push(rest[k + 1]?.value ?? '');
      k++;
    } else if (v.startsWith('--expression=')) scripts.push(v.slice(13));
    else if (v === '-f' || v === '--file') return deny('sed -f is not allowed');
    else if (!v.startsWith('-')) {
      if (scripts.length === 0 && !vals.some((x) => x === '-e' || x === '--expression' || x.startsWith('--expression='))) scripts.push(v);
      else files.push(rest[k]!);
    }
  }
  for (const s of scripts) {
    if (/(^|[;{}\n]|[0-9$\/]\s*)\s*[ewW](\s|$|;)/.test(s) || /\/[ewW]\s*$/.test(s) || /s(.).*\1.*\1[gpiImM0-9]*[ewW]/.test(s)) {
      return deny('sed e/w commands execute or write and are not allowed');
    }
  }
  if (mode === 'plan') {
    if (inPlace) return deny('sed -i edits files; the plan phase is read-only');
    if (!quiet) return deny('in the plan phase sed is allowed as `sed -n` only');
    return ALLOW;
  }
  if (inPlace) return checkWriteWords(files, mode, ctx, state, 'sed -i');
  return ALLOW;
}

function checkAwk(rest: Word[]): Verdict {
  for (const w of rest) {
    if (w.value === '-f' || w.value.startsWith('--file')) return deny('awk -f is not allowed');
    if (w.value === '-i' || w.value.startsWith('--include')) return deny('awk -i is not allowed');
    if (/system\s*\(|\|\s*getline|print[^;]*[|>]|printf[^;]*[|>]|getline\s*</.test(w.value)) {
      return deny('awk programs may not run commands or write files');
    }
  }
  return ALLOW;
}

// ---------------------------------------------------------------------------------------------

/** For the denial record: at most 500 characters of what was asked. */
export function describeInput(toolName: string, input: unknown): string {
  const args = (input && typeof input === 'object' ? input : {}) as Record<string, unknown>;
  let s: string;
  if (toolName === 'Bash' && typeof args.command === 'string') s = args.command;
  else if (typeof args.file_path === 'string') s = args.file_path;
  else s = JSON.stringify(input) ?? '';
  const cps = Array.from(s);
  return cps.length > 500 ? `${cps.slice(0, 499).join('')}…` : s;
}
