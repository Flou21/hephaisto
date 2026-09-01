import { test, expect } from '@playwright/test';
import { open, settle, status } from './helpers';

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

    // A STATED skip, not a failure. Whether any incident produces a plan is the planner's
    // judgement - measured at roughly half of runs on the acting fixture (#66) - so failing
    // here reports a model declining as if the console were broken, which is the conflation
    // #79 removed from the shell assertions.
    //
    // It is still not silent, which is what #1 was actually about: that entry was filed
    // because the suite reported a PASS on a run that asserted nothing. Naming the precondition
    // in the skip is the "or say so" half of #1's own rule, and ui/run.sh admits a skip only
    // when it carries this PRECONDITION marker - a bare skip still fails the phase.
    //
    // The two specs share this precondition, so they must share its verdict.
    test.skip(
      withPlan === null,
      'PRECONDITION: no incident in this run produced a plan, so the approval controls were never rendered',
    );

    await open(page, `/incidents/${withPlan!.id}`);

    // Present exactly when the API says an action is awaiting a human. The negative half
    // matters as much: an approve button on a denied or already-executed action would invite
    // someone to authorise something twice.
    await expect(page.getByTestId('approve')).toHaveCount(withPlan!.awaiting ? 1 : 0);
    await expect(page.getByTestId('deny')).toHaveCount(withPlan!.awaiting ? 1 : 0);

    // Every action row states what became of it, whether or not it can be approved.
    await expect(page.getByTestId('action-state').first()).toBeVisible();
  });

  /**
   * This spec used to `test.skip` when nothing was awaiting approval, which in the default mode
   * is *always* - Observe denies at the kill-switch gate, long before the risk routing that
   * would ever produce an approval. So the suite could not exit 0 in the mode it ships in, and
   * `ui/run.sh` correctly refused to call a run with a skip in it green. See docs/backlog.md #46.
   *
   * Relaxing the `skipped != 0` rule was not an option - that rule is the whole fix for #1, and
   * it is worth more than this spec. So the spec asserts the contract in BOTH directions
   * instead, and the branch it takes is decided by the API rather than by the mode:
   *
   *   - approval offered  -> it must require a name before it will act
   *   - none offered      -> the console must not be showing anyone a button to authorise
   *                          something the policy engine already refused
   *
   * The second half is not a consolation assertion. In Observe it is the more important of the
   * two: an approve button on a page where every action was denied would be an invitation to
   * authorise something the agent is not permitted to do.
   */
  test('approval is offered only where policy asked for it, and it requires a name', async ({ page }) => {
    const res = await page.request.get('/api/incidents?limit=100');
    expect(res.ok()).toBeTruthy();

    const incidents = await res.json();

    let awaiting: string | null = null;
    let anyPlan: string | null = null;

    for (const summary of incidents) {
      const detail = await page.request.get(`/api/incidents/${summary.id}`);
      if (!detail.ok()) continue;

      const body = await detail.json();
      const actions = (body.investigations ?? []).flatMap(
        (i: { plan?: { actions?: { state: string }[] } }) => i.plan?.actions ?? []);

      if (actions.length === 0) continue;
      anyPlan ??= summary.id;

      if (actions.some((a: { state: string }) => a.state === 'AwaitingApproval')) {
        awaiting = summary.id;
        break;
      }
    }

    // Same stated skip as above, and for the same reason: naming the precondition is what
    // stops the next reader debugging the approval control instead of the run that fed it.
    test.skip(
      anyPlan === null,
      'PRECONDITION: no incident in this run produced a plan, so the approval contract was never exercised',
    );

    if (awaiting === null) {
      await open(page, `/incidents/${anyPlan}`);

      // Anchored on a rendered plan, so this cannot pass by finding an empty page.
      await expect(page.getByTestId('action-state').first()).toBeVisible();

      await expect(page.getByTestId('approve')).toHaveCount(0);
      await expect(page.getByTestId('deny')).toHaveCount(0);
      return;
    }

    await open(page, `/incidents/${awaiting}`);

    // approved_by is the only record of who authorised a change to the cluster. It is
    // attribution rather than authentication until OIDC lands, and an empty one would make
    // the audit row useless at exactly the moment it is read.
    await expect(page.getByTestId('approve')).toBeDisabled();

    // The only interaction in this suite, and therefore the only assertion that can tell a live
    // circuit from a page that merely looks finished. Retried as a unit: filling the box before
    // the circuit is interactive drops the event silently, so re-asserting alone would wait
    // forever on an input the server never saw.
    await settle(async () => {
      await page.getByTestId('approval-actor').fill('e2e');
      await expect(page.getByTestId('approve')).toBeEnabled({ timeout: 2_000 });
    });
  });
});
