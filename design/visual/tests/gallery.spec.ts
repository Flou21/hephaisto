import { test, expect } from '@playwright/test';
import * as path from 'path';
import { pathToFileURL } from 'url';

const GALLERY = pathToFileURL(path.resolve(__dirname, '../../gallery.html')).href;

/**
 * One baseline for the whole page, and one per component.
 *
 * The per-component shots are not redundant with the full-page one. A full-page diff says "the
 * page changed" and points at a wall of pixels; a component diff names the thing that moved,
 * which is the difference between a useful failure and one people learn to re-baseline past.
 */
test.describe('the design language', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto(GALLERY);
    // The stylesheet is linked, so it must actually have loaded. Comparing against an unstyled
    // page would produce a perfectly stable baseline of the wrong thing.
    await expect(page.locator('.hp-shell')).toBeVisible();
    const styled = await page.evaluate(() =>
      getComputedStyle(document.body).backgroundColor !== 'rgba(0, 0, 0, 0)');
    expect(styled, 'app.css did not load; the baseline would be of an unstyled page').toBe(true);
  });

  test('the whole gallery', async ({ page }) => {
    await expect(page).toHaveScreenshot('gallery-full.png', { fullPage: true });
  });

  const sections = [
    ['tokens', 'Colour tokens'],
    ['vocabulary', 'State, severity, risk, decision'],
    ['incident-row', 'Hard component 1'],
    ['finding', 'Hard component 2'],
    ['budget-meter', 'Hard component 3'],
    ['code-block', 'Hard component 4'],
    ['callouts', 'Callouts'],
  ] as const;

  for (const [slug, heading] of sections) {
    test(slug, async ({ page }) => {
      const section = page.locator('.g-sec').filter({ has: page.getByRole('heading', { name: heading }) });
      await expect(section).toHaveCount(1);
      await expect(section).toHaveScreenshot(`${slug}.png`);
    });
  }
});
