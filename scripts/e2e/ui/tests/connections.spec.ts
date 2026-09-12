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

  test('a not-configured dependency is shown as a choice rather than a fault', async ({ page }) => {
    await open(page, '/status');

    const s = await status(page);
    const unset = s.connections.filter(
      (c: { state: string }) => c.state === 'NotConfigured');

    // The e2e stack configures no outbound notification channel, so there is always at least
    // one. If that ever changes this assertion should be re-pointed rather than deleted - the
    // distinction it protects is the whole reason the enum has four members.
    expect(unset.length).toBeGreaterThan(0);

    // The muted class, not the alarm one. A switched-off channel painted red teaches people to
    // ignore the panel, which costs more than the panel gains.
    const row = page.locator('.conn-unset');
    await expect(row.first()).toBeVisible();
    await expect(page.locator('.conn-unreachable')).toHaveCount(
      s.connections.filter((c: { state: string }) => c.state === 'Unreachable').length);
  });

  test('each row says when it was last checked', async ({ page }) => {
    await open(page, '/status');

    // The defect being fixed is that these answers were computed once at startup and thrown
    // away. A panel with no timestamp cannot distinguish "checked ten seconds ago" from
    // "checked when the pod booted four days ago", which is the same lie one layer up.
    await expect(page.locator('.hp-conn-when').first()).toContainText('checked');
  });
});
