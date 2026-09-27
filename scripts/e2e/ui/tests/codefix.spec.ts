import { test, expect, Page } from '@playwright/test';
import { incidents, open, settle } from './helpers';

// The code-fix views (v0.9.0). The user-facing promise is that a running code fix and the PR it
// opened are visible in the console, so these assert the console SHOWS what /api/codefixes
// reports - the same shape as the incident-list spec, against the API rather than a constant.
//
// Whether a run produces an attempt at all depends on the code-fix mode the harness installed and
// on the model's diagnosis, so the specs that need one skip with a PRECONDITION marker rather than
// failing; ui/run.sh admits only those. The section and the page themselves must always render.

/** Kept in step with Limit in Components/Pages/CodeFixes.razor, for #49's reason. */
const CODEFIX_LIMIT = 200;

type Attempt = { id: string; incidentId: string; state: string; prUrl: string | null };

async function attempts(page: Page): Promise<Attempt[]> {
  const res = await page.request.get(`/api/codefixes?limit=${CODEFIX_LIMIT}`);
  expect(res.ok()).toBeTruthy();

  const body = await res.json();
  expect(body.length,
    `${CODEFIX_LIMIT} code fixes came back, so the list is truncated; raise the cap here and in CodeFixes.razor together`)
    .toBeLessThan(CODEFIX_LIMIT);

  return body;
}

test.describe('code fixes', () => {
  test('the nav links to the code-fix page, which states the mode', async ({ page }) => {
    await open(page, '/');

    const link = page.getByTestId('nav-codefixes');
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute('href', /codefixes$/);

    await open(page, '/codefixes');
    await expect(page.locator('h1')).toHaveText('code fixes');

    // Checked against the API, not a constant: an empty table under mode Off is the configuration
    // working, and the mode is how a reader tells that apart from the feature being broken.
    const mode = await (await page.request.get('/api/codefixes/mode')).json();
    await expect(page.getByTestId('codefix-mode')).toContainText(String(mode.effective).toLowerCase());
  });

  test('every attempt the API lists renders as a row, with its state', async ({ page }) => {
    let listed: Attempt[] = [];

    // Retried as a unit, for the reason the incident list is: an attempt can move between the
    // API read and the table read, and both halves are telling the truth.
    await settle(async () => {
      await open(page, '/codefixes');
      listed = await attempts(page);

      const rows = page.getByTestId('codefix-row');
      await expect(rows).toHaveCount(listed.length, { timeout: 5_000 });

      const rendered = await rows.evaluateAll(els =>
        els.map(e => `${(e as HTMLElement).dataset.attempt}:${(e as HTMLElement).dataset.state}`));
      expect(new Set(rendered)).toEqual(new Set(listed.map(a => `${a.id}:${a.state}`)));
    });

    test.skip(listed.length === 0,
      'PRECONDITION: no code fix was attempted in this run, so no row could render');

    // Every running attempt says so, with its phase and elapsed time.
    const running = listed.filter(a => a.state === 'Planning' || a.state === 'Implementing');
    for (const a of running) {
      await expect(page.locator(`[data-testid="codefix-row"][data-attempt="${a.id}"]`)
        .getByTestId('codefix-running')).toBeVisible();
    }

    // Every row links to the code-fix section of its incident.
    await expect(page.getByTestId('codefix-incident-link').first())
      .toHaveAttribute('href', /^incidents\/[0-9a-f-]{36}#codefix$/);
  });

  test('an opened PR is linked from its row, in a new tab', async ({ page }) => {
    const listed = await attempts(page);
    const withPr = listed.filter(a => a.state === 'PrOpened' && a.prUrl);

    test.skip(withPr.length === 0,
      'PRECONDITION: no code fix in this run reached PrOpened, so there was no PR to link');

    await open(page, '/codefixes');

    for (const a of withPr) {
      const link = page.locator(`[data-testid="codefix-row"][data-attempt="${a.id}"]`)
        .getByTestId('codefix-pr-link');
      await expect(link).toHaveAttribute('href', a.prUrl!);
      await expect(link).toHaveAttribute('target', '_blank');
      await expect(link).toHaveAttribute('rel', /noopener/);
    }
  });

  test('an incident page carries the code-fix section, with its attempts', async ({ page }) => {
    const listed = await attempts(page);

    // An incident with an attempt when there is one, so the attempt itself is checked; otherwise
    // any incident, because the section - mode and verdict - renders on every incident.
    const target = listed[0]?.incidentId ?? (await incidents(page))[0]?.id;
    expect(target, 'no incident exists to open, which the console spec would already have failed on')
      .toBeTruthy();

    await open(page, `/incidents/${target}#codefix`);

    const section = page.getByTestId('codefix-section');
    await expect(section).toBeVisible();
    await expect(section).toHaveAttribute('id', 'codefix');
    await expect(section.getByTestId('codefix-mode')).toBeVisible();

    const mine = listed.filter(a => a.incidentId === target);
    for (const a of mine) {
      await expect(section.locator(`[data-testid="codefix-attempt"][data-attempt="${a.id}"]`)).toBeVisible();
    }

    // Approve and deny exist exactly when a plan is waiting - an approve button on a finished
    // attempt would invite somebody to authorise a write twice.
    const ready = mine.filter(a => a.state === 'PlanReady').length;
    await expect(section.getByTestId('codefix-approve')).toHaveCount(ready);
    await expect(section.getByTestId('codefix-deny')).toHaveCount(ready);
  });
});
