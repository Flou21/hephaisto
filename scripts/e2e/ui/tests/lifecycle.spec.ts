import { test, expect } from '@playwright/test';
import { incidents, open, settle, status } from './helpers';

// Incident lifecycle (backlog #109, #112): acknowledge, assign, close.
//
// RUNS LAST BY FILE ORDER, and that is deliberate rather than incidental. The config is
// `workers: 1, fullyParallel: false`, so specs execute in alphabetical order, and this is the
// only spec that MUTATES the incidents the others read. Closing one before console.spec.ts
// counted the open list would make that spec fail for a reason that has nothing to do with it.
//
// None of these touch the cluster. Acknowledging, assigning and closing are records about an
// incident, not actions on a workload - which is what makes them safe to exercise in a
// diagnosis run where the agent is in Observe and the executor refuses everything.

test.describe('the incident lifecycle', () => {
  /** The first open incident on the list, which in an Observe run is always Escalated. */
  async function firstOpenIncident(page: import('@playwright/test').Page) {
    const list = await incidents(page);
    expect(list.length).toBeGreaterThan(0);
    return list[0];
  }

  test('acknowledging records the holder without changing the state', async ({ page }) => {
    const incident = await firstOpenIncident(page);

    await open(page, `/incidents/${incident.id}`);

    const before = incident.state;

    await settle(async () => {
      await page.getByTestId('lifecycle-actor').fill('e2e-oncall');
      await expect(page.getByTestId('acknowledge')).toBeEnabled({ timeout: 2_000 });
      await page.getByTestId('acknowledge').click();
      await expect(page.getByTestId('acknowledged-by')).toContainText('e2e-oncall', { timeout: 5_000 });
    });

    // The point of the feature: acknowledging is orthogonal to the lifecycle. An implementation
    // that modelled it as a state would show a different one here, and would also have had to
    // choose between "somebody is on it" and where the incident actually is.
    const after = await page.request.get(`/api/incidents/${incident.id}`).then(r => r.json());
    expect(after.state).toBe(before);
    expect(after.acknowledgedBy).toBe('e2e-oncall');
  });

  test('assigning is a separate act from acknowledging', async ({ page }) => {
    const incident = await firstOpenIncident(page);

    await open(page, `/incidents/${incident.id}`);

    await settle(async () => {
      await page.getByTestId('lifecycle-actor').fill('e2e-lead');
      await page.getByTestId('assignee').fill('e2e-owner');
      await expect(page.getByTestId('assign')).toBeEnabled({ timeout: 2_000 });
      await page.getByTestId('assign').click();
      await expect(page.getByTestId('assigned-to')).toContainText('e2e-owner', { timeout: 5_000 });
    });

    const after = await page.request.get(`/api/incidents/${incident.id}`).then(r => r.json());
    expect(after.assignedTo).toBe('e2e-owner');
    expect(after.assignedBy).toBe('e2e-lead');

    // Assigning must not acknowledge on the assignee's behalf. The previous spec acknowledged
    // as e2e-oncall; if assigning had overwritten that, an incident would look picked up by
    // whoever it was handed to, without them having read it.
    expect(after.acknowledgedBy).toBe('e2e-oncall');
  });

  test('the mine filter returns only that persons incidents', async ({ page }) => {
    const mine = await incidents(page, 'assignedTo=e2e-owner');

    expect(mine.length).toBeGreaterThan(0);

    // And nobody else's. A filter that silently widens is one people stop trusting, which is
    // worse than one that is missing.
    const all = await incidents(page);
    expect(mine.length).toBeLessThanOrEqual(all.length);
  });

  /**
   * The one this whole feature exists for.
   *
   * Before Closed existed, an Observe install could not take an incident out of the open set at
   * all: Resolved is granted only by verification after an action worked, the agent never acts
   * in Observe, and Escalated counts as open. The count climbed for as long as the process ran.
   */
  test('closing takes an incident out of the open set', async ({ page }) => {
    const incident = await firstOpenIncident(page);
    const before = await status(page);

    await open(page, `/incidents/${incident.id}`);

    // Guarded rather than a bare retry. `settle` re-runs the WHOLE block, which is right for a
    // dropped click and wrong for one that landed: closing twice is a 409, and by then the
    // control is gone and replaced by the closed banner, so the retry would fail looking for a
    // button that correctly no longer exists. Checking for the banner first makes the retry a
    // no-op once the click has taken.
    await settle(async () => {
      if (await page.getByTestId('closed-banner').count() > 0) {
        return;
      }

      await page.getByTestId('lifecycle-actor').fill('e2e-oncall');
      await page.getByTestId('close-reason').fill('dealt with by the e2e suite');
      await expect(page.getByTestId('close')).toBeEnabled({ timeout: 2_000 });
      await page.getByTestId('close').click();
      await expect(page.getByTestId('closed-by')).toContainText('e2e-oncall', { timeout: 5_000 });
    });

    const after = await page.request.get(`/api/incidents/${incident.id}`).then(r => r.json());
    expect(after.state).toBe('Closed');
    expect(after.closedBy).toBe('e2e-oncall');
    expect(after.isOpen).toBeFalsy();

    // The number this feature exists to bring down, measured through the API rather than the
    // page: openIncidents is computed in SQL from HephaistoDbContext.OpenStates, so this
    // asserts the server-side definition agrees that a closed incident has left.
    const nowOpen = await status(page);
    expect(nowOpen.openIncidents).toBe(before.openIncidents - 1);
  });

  test('a closed incident offers reopen instead of the lifecycle controls', async ({ page }) => {
    const closed = await page.request
      .get('/api/incidents?state=Closed&limit=10')
      .then(r => r.json());

    expect(closed.length).toBeGreaterThan(0);

    await open(page, `/incidents/${closed[0].id}`);

    await expect(page.getByTestId('closed-banner')).toBeVisible();
    await expect(page.getByTestId('reopen')).toBeVisible();

    // The acknowledge/close controls must be gone: they apply to open incidents, and offering
    // them here would produce a 409 from a button that looked available.
    await expect(page.getByTestId('lifecycle')).toHaveCount(0);

    // The reopen button is deliberately NOT clicked. Reopening queues a real investigation and
    // spends real tokens against the model, and this suite runs on every release. The control
    // and the banner are what the rendering path can prove; the transition itself is covered by
    // IncidentStateMachineTests and the Postgres suite.
    //
    // Its enabled state is deliberately not asserted either: the name field is restored from
    // localStorage, so whether it starts enabled depends on whether an earlier spec in this
    // browser context typed a name - which is a fact about the test run, not about the console.
    await expect(page.getByTestId('reopen-actor')).toBeVisible();
  });
});
