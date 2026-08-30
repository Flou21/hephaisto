import { defineConfig, devices } from '@playwright/test';

/**
 * The safety net for the v0.4.0 stylesheet refactor.
 *
 * This suite deliberately does NOT talk to a cluster. Its subject is design/gallery.html, a
 * static page loaded over file:// that links the shipping stylesheet - so it runs on a laptop
 * and in ordinary CI, on every pull request, without a kind cluster or an agent or a database.
 *
 * That is the whole reason it exists separately from scripts/e2e/ui. The console suite asserts
 * BEHAVIOUR against a live agent, and every one of its assertions can pass against a console
 * whose layout has collapsed. Nothing in this repository could see a visual regression before
 * this file, and "the Playwright suite is the safety net for the CSS refactor" was half true:
 * it caught nothing a stylesheet could break.
 *
 * A baseline against the live console was considered and is not possible - it would be
 * photographing model-written prose, real incident ids, a live cost counter and a duration
 * column that re-renders once a second.
 */
// Font rasterisation is not portable. The same stylesheet on the same browser renders
// measurably differently on macOS and on Linux, so a baseline taken on a laptop and compared in
// CI would fail on antialiasing and teach everyone to re-baseline past it - which is how a
// visual suite stops being a check and becomes a formality.
//
// So there is exactly one platform: the pinned Playwright container, used by CI and by
// scripts/visual-test.sh alike. The snapshot names therefore carry no platform suffix, and
// running this suite anywhere else is refused rather than allowed to quietly overwrite the
// baselines with renders from another machine.
if (process.platform !== 'linux' && !process.env.HEPHAISTO_VISUAL_HOST_OK) {
  throw new Error(
    `Visual baselines are only valid on linux (this is ${process.platform}).\n` +
    'Run scripts/visual-test.sh, which uses the pinned Playwright container.');
}

export default defineConfig({
  testDir: './tests',

  // No platform or browser suffix: one platform, pinned above.
  snapshotPathTemplate: '{testDir}/__screenshots__/{arg}-{projectName}{ext}',

  // Baselines are compared, not raced. One worker keeps rendering deterministic.
  workers: 1,
  fullyParallel: false,

  // No retries. A visual diff is never a flake: either the pixels changed or they did not, and
  // a retry that passes would mean the comparison is not deterministic, which is itself the bug.
  retries: 0,

  timeout: 60_000,
  reporter: [['list'], ['json', { outputFile: 'results.json' }]],

  expect: {
    toHaveScreenshot: {
      // NOT a ratio. The first version of this file allowed maxDiffPixelRatio: 0.01, on the
      // reasoning that a small tolerance absorbs antialiasing - and it was verified before
      // being believed, which is the only reason it is not still there. Changing --accent from
      // #58a6ff to hot pink and re-running produced SIXTEEN PASSES: the accent shows up in
      // links, chips and one swatch, which together are about 0.2% of a section, comfortably
      // under a 1% allowance. The net was decorative.
      //
      // The platform is pinned to one container, so rendering is deterministic run to run and
      // there is no reason to tolerate any differing pixel at all. `threshold` still absorbs
      // sub-perceptual per-channel noise; `maxDiffPixels: 0` means one pixel that moves past
      // that threshold fails the comparison, which is the entire point of having baselines.
      threshold: 0.15,
      maxDiffPixels: 0,
      animations: 'disabled',
      caret: 'hide',
      scale: 'css',
    },
  },

  use: {
    trace: 'off',
    video: 'off',
    // A fixed viewport, or the full-page height moves with the window and every baseline
    // becomes a diff.
    viewport: { width: 1280, height: 900 },
    deviceScaleFactor: 1,
  },

  // Both themes are first-class as of v0.4.0, so both are photographed. app.css keys light mode
  // off `prefers-color-scheme`, so the project sets that preference and no product-side toggle
  // is needed to test it.
  projects: [
    { name: 'dark', use: { ...devices['Desktop Chrome'], colorScheme: 'dark' } },
    { name: 'light', use: { ...devices['Desktop Chrome'], colorScheme: 'light' } },
  ],
});
