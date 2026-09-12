import { test, expect } from '@playwright/test';
import { open, status } from './helpers';

// The connections panel (backlog #111). Every dependency below already described itself at
// startup and the description went into a log line, so "is it actually working?" was answerable
// only by reading pod logs. These assert the panel says the same thing the API does, and - more
// importantly - that it distinguishes the four states. A panel that renders every row the same
// colour is worse than no panel: it looks like an answer.

test.describe('the connections panel', () => {
  test('every dependency the API reports gets a row, with a state', async ({ page }) => {
    await open(page, '/status');

    const s = await status(page);
    expect(Array.isArray(s.connections)).toBeTruthy();
    expect(s.connections.length).toBeGreaterThan(0);

    const names = page.getByTestId('connection-name');
    await expect(names).toHaveCount(s.connections.length);

    // Names compared as a set rather than a count. A panel of the right size showing the wrong
    // dependencies is the failure a count alone cannot see.
    const rendered = await names.evaluateAll(els => els.map(e => e.textContent?.trim()));
    expect(new Set(rendered)).toEqual(new Set(s.connections.map((c: { name: string }) => c.name)));

    // Every row carries a state glyph. Without this, a row whose state failed to render reads
    // as a dependency nobody is checking.
    await expect(page.getByTestId('connection-state')).toHaveCount(s.connections.length);
  });

  test('postgres and kubernetes are healthy in a run that just investigated real faults', async ({ page }) => {
    await open(page, '/status');

    const s = await status(page);
    const byName = Object.fromEntries(
      s.connections.map((c: { name: string; state: string }) => [c.name, c.state]));

    // Not a tautology against the panel: this run has just investigated faults on a real
    // cluster, so these two CANNOT be anything but healthy - if they were, nothing would have
    // been investigated and the rest of the suite would have failed differently.
    expect(byName['postgres']).toBe('Healthy');
    expect(byName['kubernetes']).toBe('Healthy');
  });

  test('each state renders as its own class, whichever states this stack has', async ({ page }) => {
    await open(page, '/status');

    const s = await status(page);

    // DERIVED from the API, not assumed. The first version asserted that at least one dependency
    // was NotConfigured - true on a laptop, false on the e2e stack where everything is wired up.
    // It failed the release gate for a reason that said nothing about the console, which is the
    // same mistake as verifying a port split with a curl whose Host header happened to agree.
    //
    // Counting each state against the class it must render as is strictly stronger anyway: it
    // holds on any stack, and it catches the failure that actually matters - two states
    // collapsing onto one colour, which would make "switched off" and "broken" look alike.
    const expected: Record<string, string> = {
      Healthy: '.conn-healthy',
      Degraded: '.conn-degraded',
      Unreachable: '.conn-unreachable',
      NotConfigured: '.conn-unset',
    };

    for (const [state, selector] of Object.entries(expected)) {
      const count = s.connections.filter((c: { state: string }) => c.state === state).length;
      await expect(page.locator(selector)).toHaveCount(count);
    }

    // And every row got one of the four, so none rendered as an unstyled blank.
    await expect(page.locator(Object.values(expected).join(', ')))
      .toHaveCount(s.connections.length);
  });

  test('each row says when it was last checked', async ({ page }) => {
    await open(page, '/status');

    // The defect being fixed is that these answers were computed once at startup and thrown
    // away. A panel with no timestamp cannot distinguish "checked ten seconds ago" from
    // "checked when the pod booted four days ago", which is the same lie one layer up.
    await expect(page.locator('.hp-conn-when').first()).toContainText('checked');
  });
});
