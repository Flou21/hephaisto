import { Page, expect } from '@playwright/test';

/**
 * Navigate and wait until the Blazor circuit has actually rendered.
 *
 * `page.goto` resolves on the static HTML, which for a Blazor Server page contains the layout
 * and none of the data. Waiting for an element that only the interactive render produces is
 * the difference between a suite that passes and one that passes intermittently.
 */
export async function open(page: Page, path: string) {
  await page.goto(path, { waitUntil: 'domcontentloaded' });

  // The nav is server-rendered, so it is not proof of anything. The h1 is rendered by the
  // component itself.
  await expect(page.locator('h1')).toBeVisible();

  // And the circuit must not have failed on the way up. Both of these are in the layout at
  // all times and hidden until something goes wrong, so asserting they are hidden is a real
  // check rather than a tautology.
  await expect(page.locator('#components-reconnect-modal')).toBeHidden();
  await expect(page.locator('#blazor-error-ui')).toBeHidden();
}

/** The status endpoint, so the UI can be checked against the API rather than against itself. */
export async function status(page: Page) {
  const res = await page.request.get('/api/status');
  expect(res.ok()).toBeTruthy();
  return res.json();
}

/**
 * Percentages render via Display.Percent, so the exact formatting is the app's business
 * rather than this suite's. Parse whatever number is in the string.
 */
export function parsePercent(text: string | null): number {
  if (!text) return NaN;
  const m = text.match(/-?[\d.]+/);
  return m ? parseFloat(m[0]) : NaN;
}
