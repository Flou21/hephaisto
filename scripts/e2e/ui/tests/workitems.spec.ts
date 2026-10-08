import { test, expect, Page } from '@playwright/test';
import { open, settle, status } from './helpers';

// Work items (v0.14.0): the GitHub issues assigned to Hephaisto's account, and what became of
// each. The page is checked against /api/workitems, never against itself; where this install
// was never handed an issue, the specs that need one skip with a PRECONDITION marker, and the
// page itself - with what it says when the feature is off - must render either way.

/** Kept in step with Limit in Components/Pages/WorkItems.razor, for #49's reason. */
const WORKITEM_LIMIT = 200;

type WorkItem = { id: string; repository: string; number: number; url: string; title: string; state: string; stateReason: string | null };

async function workItems(page: Page, state = 'any'): Promise<WorkItem[]> {
  const res = await page.request.get(`/api/workitems?state=${state}&limit=${WORKITEM_LIMIT}`);
  expect(res.ok()).toBeTruthy();

  const body = await res.json();
  expect(body.length,
    `${WORKITEM_LIMIT} work items came back, so the list is truncated; raise the cap here and in WorkItems.razor together`)
    .toBeLessThan(WORKITEM_LIMIT);

  return body;
}

test.describe('work items', () => {
  test('the nav links to the page, which says whether issues are taken at all', async ({ page }) => {
    await open(page, '/');

    const link = page.getByTestId('nav-workitems');
    await expect(link).toBeVisible();
    await expect(link).toHaveAttribute('href', /workitems$/);

    await open(page, '/workitems');
    await expect(page.locator('h1')).toHaveText('work items');

    // Off is the configuration working, not the feature broken, and the page says which it is:
    // exactly one of the two sentences. `github` in /api/status is NotConfigured both when the
    // feature is off and when the agent is Off, so only an answer from GitHub proves "on".
    const s = await status(page);
    const github = (s.connections ?? []).find((c: { name: string }) => c.name === 'github');
    const on = await page.getByTestId('workitems-on').count();
    const off = await page.getByTestId('workitems-off').count();

    expect(on + off, 'the page says either that issues are taken or that they are not').toBe(1);

    if (github && github.state !== 'NotConfigured') {
      expect(on, `github is ${github.state}, so the feature is on`).toBe(1);
    }

    if (off === 1) {
      await expect(page.getByTestId('workitems-off')).toContainText('github.enabled');
    }
  });

  test('every work item the API lists renders as a row, with its state, newest first', async ({ page }) => {
    let listed: WorkItem[] = [];

    await settle(async () => {
      await open(page, '/workitems');
      listed = await workItems(page);

      const rows = page.getByTestId('workitem-row');
      await expect(rows).toHaveCount(listed.length, { timeout: 5_000 });

      const rendered = await rows.evaluateAll(els =>
        els.map(e => `${(e as HTMLElement).dataset.workitem}:${(e as HTMLElement).dataset.state}`));

      // In the API's order: newest first.
      expect(rendered).toEqual(listed.map(w => `${w.id}:${w.state}`));
    });

    if (listed.length === 0) {
      await expect(page.getByTestId('workitems-empty')).toBeVisible();
    }

    test.skip(listed.length === 0,
      'PRECONDITION: no GitHub issue was handed to this agent, so no row could render');

    for (const w of listed.slice(0, 10)) {
      const row = page.locator(`[data-testid="workitem-row"][data-workitem="${w.id}"]`);

      // The issue by its reference, linked to GitHub in a new tab; its title as text.
      const link = row.getByTestId('workitem-issue-link');
      await expect(link).toHaveText(`${w.repository}#${w.number}`);
      await expect(link).toHaveAttribute('href', w.url);
      await expect(link).toHaveAttribute('target', '_blank');
      await expect(link).toHaveAttribute('rel', /noopener/);

      const title = row.getByTestId('workitem-title');
      await expect(title).toHaveAttribute('title', w.title);
      await expect(title.locator('*')).toHaveCount(0);

      // Why it ended, when it did.
      if (w.stateReason) {
        await expect(row.getByTestId('workitem-reason')).toHaveText(w.stateReason);
      }

      // Its attempt, linked to the attempt's own page - checked against the work item itself.
      const one = await (await page.request.get(`/api/workitems/${w.id}`)).json();
      const attempt = (one.attempts ?? [])[0];

      if (attempt) {
        const to = row.getByTestId('workitem-attempt-link');
        await expect(to).toHaveAttribute('href', `codefixes/${attempt.id}`);
        await expect(to).toHaveAttribute('data-attempt-state', attempt.state);
      } else {
        await expect(row.getByTestId('workitem-attempt-link')).toHaveCount(0);
      }

      // An issue that was planned again (v0.14.0, /replan): the link above is its NEWEST
      // attempt, and the ones before it are named beside it, oldest first, each with its page.
      // The API lists a work item's attempts newest first.
      const earlier = (one.attempts ?? []).slice(1).reverse();
      const before = row.getByTestId('workitem-earlier-attempt');
      await expect(before).toHaveCount(earlier.length);

      for (let i = 0; i < earlier.length; i++) {
        await expect(before.nth(i)).toHaveAttribute('href', `codefixes/${earlier[i].id}`);
        await expect(before.nth(i)).toHaveAttribute('data-attempt-state', earlier[i].state);
      }
    }
  });

  test('the state filter is the one the API has', async ({ page }) => {
    const all = await workItems(page);

    test.skip(all.length === 0,
      'PRECONDITION: no GitHub issue was handed to this agent, so there was nothing to filter');

    await open(page, '/workitems');

    for (const state of ['Taken', 'Done', 'Cancelled']) {
      // Retried as a unit, the choice included: a change event dispatched before the circuit
      // is up is dropped, and a work item can end between the API read and the table read.
      await settle(async () => {
        await page.getByTestId('workitems-filter').selectOption(state);

        const wanted = await workItems(page, state);
        const rows = page.getByTestId('workitem-row');
        await expect(rows).toHaveCount(wanted.length, { timeout: 3_000 });

        const rendered = await rows.evaluateAll(els => els.map(e => (e as HTMLElement).dataset.workitem));
        expect(rendered).toEqual(wanted.map(w => w.id));

        if (wanted.length === 0) {
          await expect(page.getByTestId('workitems-empty')).toBeVisible();
        }
      });
    }
  });

  test('a row leads to the plan: from the work item to its attempt\'s page', async ({ page }) => {
    await open(page, '/workitems');

    const link = page.getByTestId('workitem-attempt-link').first();

    test.skip((await workItems(page)).length === 0 || await link.count() === 0,
      'PRECONDITION: no work item of this agent has an attempt, so there was no plan to open');

    const attempt = await link.getAttribute('data-attempt');

    await settle(async () => {
      await page.getByTestId('workitem-attempt-link').first().click();
      await expect(page).toHaveURL(new RegExp(`/codefixes/${attempt}$`), { timeout: 5_000 });
    });

    await expect(page.getByTestId('codefix-detail')).toHaveAttribute('data-attempt', attempt!);
    await expect(page.getByTestId('codefix-detail')).toHaveAttribute('data-subject', 'workitem');
  });
});
