import { test, expect } from '@playwright/test';
import { open, status, parsePercent } from './helpers';

// These run at the end of scripts/e2e/run.sh, against a live agent that has just investigated
// four real faults. They assert that the console SHOWS what the API reports - the API is
// already asserted directly by the harness, so a UI test that only re-checked the same numbers
// against themselves would prove nothing. What is worth proving here is the rendering path:
// the circuit connects, the data arrives, the numbers agree, and the pages link together.

test.describe('the console', () => {
  test('the incident list shows one row per fault, and each links to its detail', async ({ page }) => {
    await open(page, '/');
    await expect(page.locator('h1')).toHaveText('incidents');

    const rows = page.getByTestId('incident-row');
    await expect(rows.first()).toBeVisible();

    // Compared against the API rather than a hardcoded number, so the spec needs no edit when
    // --fixtures changes. Note the endpoint: the page's default filter is `open`, and
    // `/api/incidents` with no state parameter is open-only too. Asking for `state=all` here
    // would compare a filtered table against an unfiltered list and fail for the wrong reason.
    const res = await page.request.get('/api/incidents?limit=100');
    const open_incidents = await res.json();
    expect(open_incidents.length).toBeGreaterThan(0);
    await expect(rows).toHaveCount(open_incidents.length);

    // And every rendered row must name a real incident. Count agreement alone would pass on a
    // table of the right size showing the wrong workloads.
    const targets = await rows.evaluateAll(els =>
      els.map(e => (e as HTMLElement).dataset.target).filter(Boolean));
    const known = new Set(open_incidents.map((i: { targetName: string }) => i.targetName));
    expect(targets.length).toBe(open_incidents.length);
    for (const t of targets) {
      expect(known, `the table shows ${t}, which /api/incidents does not list`).toContain(t);
    }
  });

  test('a diagnosis is reachable from the list and cites its evidence', async ({ page }) => {
    await open(page, '/');

    // Follow the anchor rather than clicking the row. The row has an onclick handler and the
    // title cell has a real href; the anchor is the one that works without JavaScript and is
    // the honest thing to assert on.
    const link = page.getByTestId('incident-link').first();
    await expect(link).toBeVisible();
    await link.click();

    await expect(page).toHaveURL(/\/incidents\/[0-9a-f-]{36}$/);
    await expect(page.locator('#components-reconnect-modal')).toBeHidden();

    // The detail page must render a primary finding with a hypothesis. Not asserting the TEXT
    // of it: the model writes that and it differs every run, which is exactly why the harness
    // grades it with a separate judge instead.
    const primary = page.getByTestId('primary-finding').first();
    await expect(primary).toBeVisible();
    await expect(primary.getByTestId('hypothesis')).not.toBeEmpty();

    // A hypothesis with no evidence is a guess with a confidence score.
    await expect(primary.locator('.hp-evidence li').first()).toBeVisible();
  });

  test('status shows Observe, and the mode is not being held back', async ({ page }) => {
    await open(page, '/status');
    await expect(page.locator('h1')).toHaveText('status');

    const s = await status(page);

    await expect(page.getByTestId('effective-mode')).toHaveText(s.effectiveMode.toLowerCase());
    await expect(page.getByTestId('configured-mode')).toHaveText(s.mode.toLowerCase());

    // The harness installs with mode: Observe and asserts no mutation. If the console showed
    // anything else, one of the two is lying.
    await expect(page.getByTestId('effective-mode')).toHaveText('observe');

    // hp-alarm on the effective mode means it is being held BELOW what was configured - a kill
    // switch is engaged. Not expected here, and it would silently invalidate the mutation
    // assertions if it were.
    await expect(page.getByTestId('effective-mode')).not.toHaveClass(/hp-alarm/);
  });

  test('the budget meters agree with the API', async ({ page }) => {
    await open(page, '/status');
    const s = await status(page);

    const meter = page.getByTestId('meter-cost-this-hour-value');
    await expect(meter).toBeVisible();

    const shown = parsePercent(await meter.textContent());
    const expected = s.hourlyCostUtilization * 100;

    // A percentage point of tolerance: the page and the API call are seconds apart and the
    // window slides. The point is that they agree, not that they are simultaneous.
    expect(Math.abs(shown - expected)).toBeLessThan(1.0);

    // Non-zero, because zero is the shape of a failure rather than of thrift. An unpriced
    // model reports zero cost and silently disables the cost budget while this page reads a
    // contented 0.0%.
    expect(shown).toBeGreaterThan(0);

    // All three windows must render, not just the one asserted above.
    for (const id of ['meter-tokens-this-hour', 'meter-cost-this-hour', 'meter-cost-today']) {
      await expect(page.getByTestId(id)).toBeVisible();
    }
  });

  test('the alert path is shown as alive', async ({ page }) => {
    await open(page, '/status');
    const s = await status(page);

    await expect(page.getByTestId('watchdog-deliveries')).toHaveText(String(s.watchdogReceipts));
    expect(s.watchdogStale).toBe(false);

    // The callout that appears when the agent believes it has gone blind. Its absence is the
    // assertion; its presence would mean the whole detection path is untrustworthy and every
    // other result in the run with it.
    await expect(page.locator('.hp-callout.callout-escalated')).toHaveCount(0);
  });
});
