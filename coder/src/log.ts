// Every byte of progress goes to stderr. stdout carries exactly one thing: the framed result,
// last. Hephaisto reads the tail of the pod log, and a runner that chatters on stdout is one
// that can push its own result out of the window it is read from.

let prefix = '';

/** Set once at start-up. Fake mode prefixes every line with `FAKE SDK` so a log can never be mistaken for a real run. */
export function setLogPrefix(p: string): void {
  prefix = p ? `${p} ` : '';
}

const secrets: string[] = [];

/** Every later log line has this value replaced by `***` (the investigate endpoint's bearer token). */
export function addRedaction(secret: string): void {
  if (secret.length >= 8 && !secrets.includes(secret)) secrets.push(secret);
}

export function redact(s: string): string {
  let out = s;
  for (const secret of secrets) out = out.split(secret).join('***');
  return out;
}

function write(level: string, msg: string): void {
  const ts = new Date().toISOString();
  for (const line of redact(msg).split('\n')) {
    process.stderr.write(`${prefix}${ts} ${level} ${line}\n`);
  }
}

export const log = {
  info: (msg: string) => write('INFO ', msg),
  warn: (msg: string) => write('WARN ', msg),
  error: (msg: string) => write('ERROR', msg),
};
