// A deliberately small POSIX-shell lexer, just enough for the guard to see every command a Bash
// tool call would run. It does not try to be a shell: anything it cannot see through (command
// or process substitution, indirect expansion) is reported as such and the guard denies it.
// Erring towards "cannot verify, denied" is the whole point.

export interface Word {
  /** Value after quote removal. Expansions are left in as written (`$HOME`). */
  value: string;
  /** Contains an unquoted or double-quoted `$` expansion, so the value is not what will run. */
  expands: boolean;
  /** Contains an unquoted glob character. */
  glob: boolean;
}

export interface Redirect {
  op: string;
  fd: string;
  target: Word | null;
}

export interface SimpleCommand {
  words: Word[];
  redirects: Redirect[];
  /** The separator BEFORE this command was a pipe. */
  pipedInput: boolean;
}

export interface Lexed {
  commands: SimpleCommand[];
  /** `$(...)`, backticks, `<(...)`, `>(...)` anywhere outside single quotes (heredoc bodies included). */
  substitution: boolean;
  /** Variable names referenced by `$NAME` / `${NAME...}`. */
  variables: string[];
  /** `${!name}` indirect expansion. */
  indirect: boolean;
  /** The lexer hit something it could not close (unterminated quote). */
  malformed: boolean;
  /** A `(` subshell or grouping appeared: a `cd` inside it does not outlive it. */
  subshell: boolean;
}

const SEPARATORS = new Set(['|', '|&', '||', '&&', '&', ';', ';;', '\n', '(', ')']);
const REDIRECT_OPS = ['&>>', '&>', '>>', '>|', '>&', '<&', '<<<', '<<-', '<<', '<>', '>', '<'];

type Token = { kind: 'word'; word: Word } | { kind: 'op'; op: string; fd: string };

export function lex(input: string): Lexed {
  const tokens: Token[] = [];
  const variables = new Set<string>();
  let substitution = false;
  let indirect = false;
  let malformed = false;
  const pendingHeredocs: { delim: string; strip: boolean; expand: boolean }[] = [];

  let i = 0;
  let cur: Word | null = null;
  let curRawDigits = true; // whether the current word so far is only digits (fd prefix)

  const pushWord = () => {
    if (cur) tokens.push({ kind: 'word', word: cur });
    cur = null;
    curRawDigits = true;
  };
  const ensure = (): Word => {
    if (!cur) cur = { value: '', expands: false, glob: false };
    return cur;
  };

  const scanDollar = (s: string, at: number): number => {
    // s[at] === '$'; returns index after the expansion head that was consumed (just the name)
    const next = s[at + 1];
    if (next === '(') {
      substitution = true;
      return at + 2;
    }
    if (next === '{') {
      if (s[at + 2] === '!') indirect = true;
      const m = /^[A-Za-z_][A-Za-z0-9_]*/.exec(s.slice(at + 2 + (s[at + 2] === '!' ? 1 : 0)));
      if (m) variables.add(m[0]);
      return at + 2;
    }
    const m = /^[A-Za-z_][A-Za-z0-9_]*/.exec(s.slice(at + 1));
    if (m) {
      variables.add(m[0]);
      return at + 1 + m[0].length;
    }
    return at + 1;
  };

  const scanHeredocBody = (body: string, expand: boolean) => {
    if (!expand) return;
    for (let k = 0; k < body.length; k++) {
      if (body[k] === '\\') {
        k++;
        continue;
      }
      if (body[k] === '`') substitution = true;
      if (body[k] === '$') scanDollar(body, k);
    }
  };

  while (i < input.length) {
    const c = input[i]!;

    // Heredoc bodies start after the newline that ends the line their operator is on.
    if (c === '\n' && pendingHeredocs.length > 0) {
      pushWord();
      tokens.push({ kind: 'op', op: '\n', fd: '' });
      i++;
      for (const h of pendingHeredocs.splice(0)) {
        const bodyLines: string[] = [];
        let closed = false;
        while (i <= input.length) {
          const nl = input.indexOf('\n', i);
          const line = nl === -1 ? input.slice(i) : input.slice(i, nl);
          i = nl === -1 ? input.length + 1 : nl + 1;
          if ((h.strip ? line.replace(/^\t+/, '') : line) === h.delim) {
            closed = true;
            break;
          }
          bodyLines.push(line);
          if (nl === -1) break;
        }
        if (!closed) malformed = true;
        scanHeredocBody(bodyLines.join('\n'), h.expand);
      }
      if (i > input.length) i = input.length;
      continue;
    }

    if (c === ' ' || c === '\t') {
      pushWord();
      i++;
      continue;
    }
    if (c === '\\') {
      if (input[i + 1] === '\n') {
        i += 2;
        continue;
      }
      ensure().value += input[i + 1] ?? '';
      curRawDigits = false;
      i += 2;
      continue;
    }
    if (c === '#' && !cur) {
      while (i < input.length && input[i] !== '\n') i++;
      continue;
    }
    if (c === "'") {
      const end = input.indexOf("'", i + 1);
      if (end === -1) {
        malformed = true;
        ensure().value += input.slice(i + 1);
        i = input.length;
      } else {
        ensure().value += input.slice(i + 1, end);
        i = end + 1;
      }
      curRawDigits = false;
      continue;
    }
    if (c === '"') {
      const w = ensure();
      curRawDigits = false;
      i++;
      let closed = false;
      while (i < input.length) {
        const d = input[i]!;
        if (d === '"') {
          closed = true;
          i++;
          break;
        }
        if (d === '\\' && i + 1 < input.length && '"\\$`\n'.includes(input[i + 1]!)) {
          if (input[i + 1] !== '\n') w.value += input[i + 1];
          i += 2;
          continue;
        }
        if (d === '`') substitution = true;
        if (d === '$') {
          w.expands = true;
          const after = scanDollar(input, i);
          w.value += input.slice(i, after);
          i = after;
          continue;
        }
        w.value += d;
        i++;
      }
      if (!closed) malformed = true;
      continue;
    }
    if (c === '`') {
      substitution = true;
      ensure().value += c;
      curRawDigits = false;
      i++;
      continue;
    }
    if (c === '$') {
      const w = ensure();
      w.expands = true;
      curRawDigits = false;
      const after = scanDollar(input, i);
      w.value += input.slice(i, after);
      i = after;
      continue;
    }
    if ((c === '<' || c === '>') && input[i + 1] === '(') {
      substitution = true;
      i += 2;
      continue;
    }
    // redirections (with an optional all-digit fd prefix glued to the current word)
    if (c === '<' || c === '>' || (c === '&' && input[i + 1] === '>')) {
      const op = REDIRECT_OPS.find((o) => input.startsWith(o, i))!;
      let fd = '';
      if (cur && curRawDigits && (cur as Word).value.length > 0 && !(cur as Word).expands) {
        fd = (cur as Word).value;
        cur = null;
        curRawDigits = true;
      } else {
        pushWord();
      }
      tokens.push({ kind: 'op', op, fd });
      i += op.length;
      if (op === '<<' || op === '<<-') {
        // read the delimiter word now
        while (input[i] === ' ' || input[i] === '\t') i++;
        const m = /^(['"]?)([^\s'";|&<>()]+)\1/.exec(input.slice(i));
        if (m) {
          pendingHeredocs.push({ delim: m[2]!, strip: op === '<<-', expand: m[1] === '' });
          tokens.push({ kind: 'word', word: { value: m[2]!, expands: false, glob: false } });
          i += m[0].length;
        } else {
          malformed = true;
        }
      }
      continue;
    }
    const two = input.slice(i, i + 2);
    if (two === '||' || two === '&&' || two === ';;' || two === '|&') {
      pushWord();
      tokens.push({ kind: 'op', op: two, fd: '' });
      i += 2;
      continue;
    }
    if (c === '|' || c === '&' || c === ';' || c === '\n' || c === '(' || c === ')') {
      pushWord();
      tokens.push({ kind: 'op', op: c, fd: '' });
      i++;
      continue;
    }
    const w = ensure();
    if (c === '*' || c === '?' || c === '[') w.glob = true;
    if (!/[0-9]/.test(c)) curRawDigits = false;
    w.value += c;
    i++;
  }
  pushWord();
  if (pendingHeredocs.length > 0) malformed = true;

  // group tokens into simple commands
  const commands: SimpleCommand[] = [];
  let current: SimpleCommand = { words: [], redirects: [], pipedInput: false };
  const flush = (nextPiped: boolean) => {
    if (current.words.length > 0 || current.redirects.length > 0) commands.push(current);
    current = { words: [], redirects: [], pipedInput: nextPiped };
  };
  for (let k = 0; k < tokens.length; k++) {
    const t = tokens[k]!;
    if (t.kind === 'word') {
      current.words.push(t.word);
      continue;
    }
    if (SEPARATORS.has(t.op)) {
      flush(t.op === '|' || t.op === '|&');
      continue;
    }
    const next = tokens[k + 1];
    const target = next && next.kind === 'word' ? next.word : null;
    if (target) k++;
    else malformed = true;
    current.redirects.push({ op: t.op, fd: t.fd, target });
  }
  flush(false);

  const subshell = tokens.some((t) => t.kind === 'op' && t.op === '(');
  return { commands, substitution, variables: [...variables], indirect, malformed, subshell };
}
