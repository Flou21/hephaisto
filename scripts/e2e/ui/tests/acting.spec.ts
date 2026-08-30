import { test, expect } from '@playwright/test';
import { open, status } from './helpers';

/**
 * The console's half of the acting story.
 *
 * Asserted against /api, never against the page's own earlier state - the rule the console
 * suite already follows. A UI checked against itself agrees with itself.
 */
test.describe('acting', () => {
  test('the configured mode is the deployment ceiling, and the page agrees with the API', async ({ page }) => {
    const s = await status(page);

    await open(page, '/status');

    // `configured` is the Helm value as it reaches the pod, NOT the agent_mode row - that
    // column is gone, and while it existed the page would have said Observe while the chart
    // said Auto. `effective` is the most restrictive arm.
    await expect(page.getByTestId('configured-mode')).toHaveText(String(s.mode).toLowerCase());
    await expect(page.getByTestId('effective-mode')).toHaveText(String(s.effectiveMode).toLowerCase());
  });

  test('the re-arm control appears only when the latch is set', async ({ page }) => {
    const s = await status(page);

    await open(page, '/status');

    // The only kill-switch write the product exposes. It cannot name a mode and cannot lift
    // the agent above what the deployment already grants, which is why it is a button at all.
    if (s.runawayLatched) {
      await expect(page.getByTestId('re-arm')).toBeVisible();
    } else {
      await expect(page.getByTestId('re-arm')).toHaveCount(0);
    }
  });

  test('an action awaiting approval offers approve and deny, and nothing else does', async ({ page }) => {
    const res = await page.request.get('/api/incidents?limit=100');
    expect(res.ok()).toBeTruthy();

    const incidents = await res.json();
    expect(Array.isArray(incidents)).toBeTruthy();

    // Find one with a plan, via the API, so the page is checked against the database rather
    // than against whatever it happens to render.
    let withPlan: { id: string; awaiting: boolean } | null = null;

    for (const summary of incidents) {
      const detail = await page.request.get(`/api/incidents/${summary.id}`);
      if (!detail.ok()) continue;

      const body = await detail.json();
      const actions = (body.investigations ?? []).flatMap(
        (i: { plan?: { actions?: { state: string }[] } }) => i.plan?.actions ?? []);

      if (actions.length === 0) continue;

      withPlan = {
        id: summary.id,
        awaiting: actions.some((a: { state: string }) => a.state === 'AwaitingApproval'),
      };
      break;
    }

    test.skip(withPlan === null, 'no incident in this run produced a plan');

    await open(page, `/incidents/${withPlan!.id}`);

    // Present exactly when the API says an action is awaiting a human. The negative half
    // matters as much: an approve button on a denied or already-executed action would invite
    // someone to authorise something twice.
    await expect(page.getByTestId('approve')).toHaveCount(withPlan!.awaiting ? 1 : 0);
    await expect(page.getByTestId('deny')).toHaveCount(withPlan!.awaiting ? 1 : 0);

    // Every action row states what became of it, whether or not it can be approved.
    await expect(page.getByTestId('action-state').first()).toBeVisible();
  });

  test('approve is disabled until someone says who they are', async ({ page }) => {
    const res = await page.request.get('/api/incidents?limit=100');
    const incidents = await res.json();

    let target: string | null = null;

    for (const summary of incidents) {
      const detail = await page.request.get(`/api/incidents/${summary.id}`);
      if (!detail.ok()) continue;

      const body = await detail.json();
      const awaiting = (body.investigations ?? []).flatMap(
        (i: { plan?: { actions?: { state: string }[] } }) => i.plan?.actions ?? [])
        .some((a: { state: string }) => a.state === 'AwaitingApproval');

      if (awaiting) { target = summary.id; break; }
    }

    test.skip(target === null, 'no action is awaiting approval in this run');

    await open(page, `/incidents/${target}`);

    // approved_by is the only record of who authorised a change to the cluster. It is
    // attribution rather than authentication until OIDC lands, and an empty one would make
    // the audit row useless at exactly the moment it is read.
    await expect(page.getByTestId('approve')).toBeDisabled();

    await page.getByTestId('approval-actor').fill('e2e');
    await expect(page.getByTestId('approve')).toBeEnabled();
  });
});
