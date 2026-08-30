import { Page, expect } from '@playwright/test';

/**
 * Navigate and wait until the Blazor circuit has actually taken the page over.
 *
 * This is a Blazor **Web App** with `<Routes @rendermode="InteractiveServer" />`, which means
 * every component renders TWICE: once as static server-rendered HTML delivered with the
 * document, and then again over the SignalR circuit, which replaces that DOM wholesale.
 *
 * Between those two moments the page looks completely finished and is completely inert. The
 * elements are there, they are visible, their text is correct - and a click or an input event
 * dispatched into them is dropped on the floor, because the handlers belong to a render that
 * has not happened yet. Measured against this app: `h1` becomes visible at ~50ms and a click
 * does not take effect until ~600ms.
 *
 * That is why waiting on `h1` was not enough, and why the gap stayed invisible for so long.
 * Reading static HTML is indistinguishable from reading the interactive DOM, so every
 * read-only assertion in this suite passed either way. Only the specs that actually interact
 * could ever have noticed, and there is exactly one of those - which failed on every run and
 * was read as a product bug in the approval control. It was not; see docs/backlog.md #48.
 *
 * So the gate is the circuit's first render batch. Waiting for the websocket to open is not
 * sufficient either - it opens at ~55ms, still before the takeover.
 */
export async function open(page: Page, path: string) {
  // Registered before navigating, or the socket can open before anyone is listening.
  const circuit = page.waitForEvent('websocket', ws => ws.url().includes('_blazor'));

  await page.goto(path, { waitUntil: 'domcontentloaded' });

  const ws = await circuit;

  // The frames are MessagePack, so this matches the method name inside the payload rather
  // than parsing it. The first RenderBatch IS the interactive takeover.
  await ws.waitForEvent('framereceived', {
    predicate: frame => String(frame.payload).includes('RenderBatch'),
    timeout: 30_000,
  });

  // Only now is an element that the spec finds part of the interactive tree. The nav is
  // server-rendered, so it is not proof of anything; the h1 is rendered by the component.
  await expect(page.locator('h1')).toBeVisible();

  // And the circuit must not have failed on the way up. Both of these are in the layout at
  // all times and hidden until something goes wrong, so asserting they are hidden is a real
  // check rather than a tautology.
  await expect(page.locator('#components-reconnect-modal')).toBeHidden();
  await expect(page.locator('#blazor-error-ui')).toBeHidden();
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
