import { describe, expect, it } from 'vitest';
import { isTransientNetworkFailure } from '../src/git.js';

describe('isTransientNetworkFailure', () => {
  it.each([
    "fatal: unable to access 'http://coder-git/x.git/': Failed to connect to proxy port 3128 after 2 ms: Couldn't connect to server",
    "fatal: unable to access 'https://github.com/o/r/': Could not resolve host: github.com",
    'error: RPC failed; curl 56 Recv failure: Connection reset by peer',
    "fatal: unable to access 'https://github.com/o/r/': Connection timed out after 10001 milliseconds",
  ])('retries a failure to reach the server: %s', (stderr) => {
    expect(isTransientNetworkFailure(stderr)).toBe(true);
  });

  it.each([
    "fatal: Remote branch fixture/nope not found in upstream origin",
    "remote: Repository not found.\nfatal: repository 'https://github.com/o/nope/' not found",
    "fatal: Authentication failed for 'https://github.com/o/r/'",
    "fatal: could not read Username for 'https://github.com': terminal prompts disabled",
  ])('never retries the server refusing the request: %s', (stderr) => {
    expect(isTransientNetworkFailure(stderr)).toBe(false);
  });
});
