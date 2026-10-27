'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { Blocked } = require('./reporting.cjs');

async function screenshot(page, directory, name) {
  if (!await page.locator('#workspace').isVisible()) {
    throw new Blocked('Authenticated application workspace is unavailable; screenshot suppressed.');
  }
  // Real staff identity and saved configuration can coexist with synthetic requests.
  await page.screenshot({ path: path.join(directory, name + '.png'), fullPage: true,
    mask: [page.locator('.app-header, .request-activity, .detail-meta span:last-child'),
      page.locator('#notification-email, #weekly-email, input[type="password"], [data-email-test]')] });
}
async function checkpoint(page, directory, name) {
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  await page.evaluate(axe);
  const result = await page.evaluate(async () => ({
    violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)).map(v => v.id),
    overflow: document.documentElement.scrollWidth > innerWidth,
    images: [...document.images].filter(n => n.getClientRects().length).every(n => n.complete && n.naturalWidth > 0)
  }));
  await screenshot(page, directory, name);
  assert.deepEqual(result.violations, [], 'No serious/critical axe findings');
  assert.equal(result.overflow, false, 'No horizontal page overflow');
  assert.equal(result.images, true, 'Visible images loaded');
}
async function confirmationKeyboard(page, title, approve, cancel = 'Cancel') {
  const dialog = page.locator('#confirmation-dialog[open]');
  await dialog.waitFor();
  assert.equal(await dialog.getAttribute('aria-labelledby'), 'confirmation-title');
  assert.equal(await dialog.getAttribute('aria-describedby'), 'confirmation-message');
  await dialog.getByRole('heading', { name: title, exact: true }).waitFor();
  assert.ok((await dialog.locator('#confirmation-message').innerText()).trim().length > 15);
  const safe = dialog.getByRole('button', { name: cancel, exact: true });
  const action = dialog.getByRole('button', { name: approve, exact: true });
  assert.equal(await safe.evaluate(n => n === document.activeElement), true, 'Safe initial focus');
  await page.keyboard.press('Shift+Tab');
  assert.equal(await action.evaluate(n => n === document.activeElement), true);
  await page.keyboard.press('Tab');
  assert.equal(await safe.evaluate(n => n === document.activeElement), true);
  const focus = await safe.evaluate(n => {
    const s = getComputedStyle(n), r = n.getBoundingClientRect();
    return { visible: s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 ||
      s.boxShadow !== 'none', fits: r.width > 0 && r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight };
  });
  assert.equal(focus.visible, true, 'Visible focus indicator');
  assert.equal(focus.fits, true, 'Safe confirmation control is not clipped');
  await page.keyboard.press('Tab');
  assert.equal(await action.evaluate(n => n === document.activeElement), true);
  await page.keyboard.press('Tab');
  assert.equal(await safe.evaluate(n => n === document.activeElement), true);
  const bounds = await action.boundingBox();
  assert.ok(bounds && bounds.x >= 0 && bounds.y >= 0 &&
    bounds.x + bounds.width <= await page.evaluate(() => innerWidth) &&
    bounds.y + bounds.height <= await page.evaluate(() => innerHeight), 'Approval control is not clipped');
  return dialog;
}
async function nativeZoom(context, page, factor) {
  const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
  const ratio = await page.evaluate(() => devicePixelRatio);
  const actual = await worker.evaluate(async ({ url, factor }) => {
    const tab = (await chrome.tabs.query({})).find(t => t.url === url);
    if (!tab) throw new Error('Application tab missing');
    await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
    await chrome.tabs.setZoom(tab.id, factor);
    return chrome.tabs.getZoom(tab.id);
  }, { url: page.url(), factor });
  assert.equal(actual, factor);
  await page.waitForFunction(({ ratio, factor }) => Math.abs(devicePixelRatio / ratio - factor) < 0.01, { ratio, factor });
  const cdp = await context.newCDPSession(page);
  try {
    assert.equal((await cdp.send('Page.getLayoutMetrics')).visualViewport.zoom, factor);
    assert.deepEqual(await page.evaluate(() => [getComputedStyle(document.documentElement).zoom,
      getComputedStyle(document.body).zoom, visualViewport.scale]), ['1', '1', 1]);
  } finally { await cdp.detach(); }
}
module.exports = { screenshot, checkpoint, confirmationKeyboard, nativeZoom };
