import { test, expect } from '@playwright/test';
const H = 'http://127.0.0.1:18100';
test('what transport does the circuit use through a plain port-forward', async ({ page }) => {
  const ws: string[] = [];
  const reqs: string[] = [];
  page.on('websocket', w => ws.push(w.url()));
  page.on('request', r => { if (r.url().includes('_blazor')) reqs.push(`${r.method()} ${r.url()}`); });

  await page.goto(`${H}/status`, { waitUntil: 'domcontentloaded' });
  await expect(page.locator('h1')).toBeVisible();
  await page.waitForTimeout(8000);

  console.log('WEBSOCKETS:', JSON.stringify(ws));
  console.log('BLAZOR REQUESTS:', JSON.stringify(reqs, null, 1));
});
