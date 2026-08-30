import { Page, expect } from '@playwright/test';

/**
 * Navigate and wait until the Blazor circuit has started coming up.
 *
 * This is a Blazor **Web App** with `<Routes @rendermode="InteractiveServer" />`, so every
 * component renders TWICE: once as static server-rendered HTML delivered with the document, and
 * then again over the circuit, which replaces that DOM wholesale.
 *
 * Between those two moments the page looks completely finished and is completely inert. The
 * elements are there, they are visible, their text is correct - and a click or an input event
 * dispatched into them is dropped, because the handlers belong to a render that has not happened
 * yet. Measured against a live console: `h1` becomes visible at ~50ms and a click does not take
 * effect until ~600ms. Waiting on `h1` alone was never enough; see docs/backlog.md #48.
 *
 * WAIT ON THE NEGOTIATION, NOT ON A WEBSOCKET. The first version of this waited for a `_blazor`
 * websocket, which is a TRANSPORT rather than a state: SignalR negotiates, and where a websocket
 * cannot be established it falls back to server-sent events or long polling and the page is
 * perfectly interactive with no websocket ever opening. That version passed against a
 * development image and timed out against all nine specs on the published one - asserting how
 * the circuit connected rather than that it had.
 *
 * `/_blazor/negotiate` happens under every transport, so it is the portable signal. It is also
 * only the START of the handshake, which is why anything that INTERACTS must additionally be
 * written to retry - see `settle` below.
 */
export async function open(page: Page, path: string) {
  // Registered before navigating, or the request can be made before anyone is listening.
  const circuit = page.waitForRequest(r => r.url().includes('/_blazor'), { timeout: 30_000 });

  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await circuit;

  // The nav is server-rendered, so it is not proof of anything; the h1 is rendered by the
  // component itself.
  await expect(page.locator('h1')).toBeVisible();

  // And the circuit must not have failed on the way up. Both of these are in the layout at
  // all times and hidden until something goes wrong, so asserting they are hidden is a real
  // check rather than a tautology.
  await expect(page.locator('#components-reconnect-modal')).toBeHidden();
  await expect(page.locator('#blazor-error-ui')).toBeHidden();
}

/**
 * Perform an interaction, retrying it until the circuit actually processes it.
 *
 * The honest way to wait for hydration, and the only one that does not depend on knowing how
 * Blazor connected or how long it took. An event dispatched into a not-yet-interactive page is
 * dropped silently - no error, no console entry - so the only reliable signal that the circuit
 * is live is that an interaction had an effect.
 *
 * `toPass` re-runs the whole block, so the action is repeated as well as the assertion. That
 * matters: retrying only the assertion would wait forever on an event that was already lost.
 */
export async function settle(action: () => Promise<void>, timeout = 30_000) {
  await expect(action).toPass({ timeout });
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
