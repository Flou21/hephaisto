import { defineConfig, devices } from '@playwright/test';

// The console is Blazor Server. That is the single fact that shapes this whole config.
//
// A page is delivered as static HTML and then opens a SignalR circuit; the interactive
// content arrives over that websocket afterwards. So `goto` followed immediately by an
// assertion races the circuit, and the failure is intermittent rather than obvious. Every
// spec here waits on something that only exists after the circuit is up, and every assertion
// uses Playwright's auto-retrying `expect` - which also absorbs the 1-second clock timer that
// re-renders the duration column continuously, and the 10-second data refresh.
export default defineConfig({
  testDir: './tests',

  // Serial. These run against one live agent investigating real incidents, and two specs
  // navigating at once produce interleaved SignalR traffic that makes a failure unreadable.
  // There are five of them; the parallelism would buy seconds.
  workers: 1,
  fullyParallel: false,

  // One retry. A Blazor circuit can genuinely drop - app.js reloads the page when the server
  // rejects a reconnect - and that is a flake rather than a finding. More than one retry
  // starts hiding real intermittency.
  retries: 1,

  timeout: 60_000,
  expect: { timeout: 20_000 },

  reporter: process.env.CI
    ? [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]]
    : [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],

  use: {
    // Set by scripts/e2e/run.sh to its supervised port-forward.
    baseURL: process.env.HEPHAISTO_URL ?? 'http://127.0.0.1:18100',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 15_000,
  },

  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
