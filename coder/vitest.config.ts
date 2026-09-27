import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  // the image's /opt/coder is read-only; keep vite's cache out of it
  cacheDir: join(tmpdir(), 'coder-vitest-cache'),
  test: {
    include: ['test/**/*.test.ts'],
    // driver tests clone and push real git repositories into temp dirs
    testTimeout: 60_000,
    hookTimeout: 60_000,
    pool: 'forks',
  },
});
