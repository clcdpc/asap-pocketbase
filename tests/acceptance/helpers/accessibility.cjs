'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { must } = require('./environment.cjs');

// Only the scenario executor binds pages, using its pinned runtime/staff verifier.
// A workspace-shaped page or an arbitrary URL is never sufficient authorization.
const screenshotVerifiers = new WeakMap();
function registerScreenshotPage(page, verify) {
  screenshotVerifiers.set(page, verify);
  return () => screenshotVerifiers.delete(page);
}
function pngDimensions(buffer) {
  assert.ok(buffer.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])), 'Screenshot is a PNG');
  return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}
async function captureTarget(page, origin) {
  const url = new URL(page.url());
  must(origin === 'https://localhost:8080' && url.origin === origin && url.pathname.startsWith('/staff/'),
    'Screenshot requires the verified local acceptance application.');
  must(await page.locator('#workspace').isVisible(),
    'Authenticated application workspace is unavailable; screenshot suppressed.');
}

async function screenshot(page, directory, name, options = {}) {
  const verify = screenshotVerifiers.get(page);
  must(verify, 'Screenshot has no verified synthetic acceptance environment.');
  const environment = await verify(); // Rechecks Testing, SQL, providers and the pinned synthetic reviewer.
  await captureTarget(page, environment.origin);
  const { fullPage = false, clip, nativeViewport = false } = options;
  assert.ok(Object.keys(options).every(key => ['fullPage', 'clip', 'nativeViewport'].includes(key)), 'Only viewport or explicit page/crop captures are supported');
  let buffer;
  if (nativeViewport) {
    assert.ok(!fullPage && !clip, 'Native viewport capture cannot be combined with a page/crop capture');
    const cdp = await page.context().newCDPSession(page);
    try {
      assert.equal((await cdp.send('Page.getLayoutMetrics')).visualViewport.zoom, 2, 'Native viewport evidence requires actual 200 percent zoom');
      // Pinned Playwright constructs a clip from legacy pageY at native zoom.
      // On scrolled pages that captures blank pixels. Let CDP capture the actual
      // viewport without a clip, retaining the verifier and export checks below.
      buffer = Buffer.from((await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false })).data, 'base64');
    } finally { await cdp.detach(); }
  } else {
    buffer = await page.screenshot({ type: 'png', fullPage, ...(clip ? { clip } : {}) });
  }
  // Do not export pixels if navigation/session expiry raced the capture.
  await captureTarget(page, environment.origin);
  const dimensions = pngDimensions(buffer);
  if (!fullPage) {
    const expected = clip || page.viewportSize();
    assert.ok(expected, 'Acceptance capture has a configured viewport');
    assert.deepEqual(dimensions, { width: Math.round(expected.width), height: Math.round(expected.height) },
      'Screenshot dimensions match the requested viewport or corrected crop');
  }
  await fs.writeFile(path.join(directory, name + '.png'), buffer);
  return buffer;
}
async function checkpoint(page, directory, name, options = {}) {
  const { visible = [], ...captureOptions } = options;
  assert.equal(await page.locator('#request-dialog .request-more[open]').count(), 0, 'No unrelated More menu obscures screenshot evidence');
  const dialog = page.locator('#confirmation-dialog[open]');
  if (await dialog.count()) {
    await confirmationGeometry(page);
    await visibleEvidence(dialog.locator('.form-actions button'));
  }
  for (const locator of visible) await visibleEvidence(locator);
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  await page.evaluate(axe);
  const result = await page.evaluate(async () => ({
    violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)).map(v => v.id),
    overflow: document.documentElement.scrollWidth > innerWidth,
    images: [...document.images].filter(n => n.getClientRects().length).every(n => n.complete && n.naturalWidth > 0)
  }));
  await screenshot(page, directory, name, captureOptions);
  assert.deepEqual(result.violations, [], 'No serious/critical axe findings');
  assert.equal(result.overflow, false, 'No horizontal page overflow');
  assert.equal(result.images, true, 'Visible images loaded');
}
async function visibleEvidence(locator) {
  for (const element of await locator.all()) {
    await element.waitFor({ state: 'visible' });
    const fits = await element.evaluate(node => {
      const r = node.getBoundingClientRect(), v = visualViewport;
      let left = v.offsetLeft, top = v.offsetTop, right = left + v.width, bottom = top + v.height;
      for (let parent = node.parentElement; parent; parent = parent.parentElement) {
        const p = parent.getBoundingClientRect(), style = getComputedStyle(parent);
        if (style.overflowX !== 'visible') {
          left = Math.max(left, p.left + parent.clientLeft);
          right = Math.min(right, p.left + parent.clientLeft + parent.clientWidth);
        }
        if (style.overflowY !== 'visible') {
          top = Math.max(top, p.top + parent.clientTop);
          bottom = Math.min(bottom, p.top + parent.clientTop + parent.clientHeight);
        }
      }
      const hit = document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2);
      return r.width > 0 && r.height > 0 && r.left >= left - 1 && r.right <= right + 1 &&
        r.top >= top - 1 && r.bottom <= bottom + 1 && (hit === node || node.contains(hit));
    });
    assert.equal(fits, true, 'Required screenshot evidence is in the viewport without clipping or obstruction');
  }
  assert.ok(await locator.count() > 0, 'Required screenshot evidence exists');
}
async function partGeometry(locator, edge = null) {
  return locator.evaluate((node, edge) => {
    const rect = r => ({ left: r.left, top: r.top, right: r.right, bottom: r.bottom, width: r.width, height: r.height });
    const viewport = { left: visualViewport.offsetLeft, top: visualViewport.offsetTop,
      right: visualViewport.offsetLeft + visualViewport.width, bottom: visualViewport.offsetTop + visualViewport.height };
    const range = document.createRange(); range.selectNodeContents(node);
    const lines = () => [...range.getClientRects()].filter(r => r.width > 0 && r.height > 0);
    const target = () => edge === 'end' ? lines().at(-1) : lines()[0];
    const dialog = node.closest('dialog');
    const header = dialog?.querySelector('.dialog-header');
    // Reading long content may scroll, but only through user-scrollable areas.
    // Center the first/last line below a sticky heading, without CSS changes.
    if (edge) {
      for (let parent = node; parent && dialog?.contains(parent); parent = parent.parentElement) {
        if (!/^(auto|scroll)$/.test(getComputedStyle(parent).overflowY) || parent.scrollHeight <= parent.clientHeight) continue;
        const r = parent.getBoundingClientRect(), line = target();
        const stickyBottom = header && !header.contains(node) && getComputedStyle(header).position === 'sticky'
          ? header.getBoundingClientRect().bottom : viewport.top;
        const top = Math.max(viewport.top, r.top + parent.clientTop, stickyBottom);
        const bottom = Math.min(viewport.bottom, r.top + parent.clientTop + parent.clientHeight);
        if (line) parent.scrollTop += (line.top + line.bottom - top - bottom) / 2;
      }
    }
    const bounds = rect(node.getBoundingClientRect()), clip = { ...viewport };
    let rendered = true;
    for (let parent = node; parent; parent = parent.parentElement) {
      const s = getComputedStyle(parent), r = parent.getBoundingClientRect();
      rendered &&= s.display !== 'none' && s.visibility === 'visible' && Number(s.opacity) > 0;
      if (parent !== node || edge) {
        if (s.overflowX !== 'visible') {
          clip.left = Math.max(clip.left, r.left + parent.clientLeft);
          clip.right = Math.min(clip.right, r.left + parent.clientLeft + parent.clientWidth);
        }
        if (s.overflowY !== 'visible') {
          clip.top = Math.max(clip.top, r.top + parent.clientTop);
          clip.bottom = Math.min(clip.bottom, r.top + parent.clientTop + parent.clientHeight);
        }
      }
    }
    const text = lines().map(rect), style = getComputedStyle(node);
    const visible = r => r && r.width > 0 && r.height > 0 && r.left >= clip.left - 1 &&
      r.right <= clip.right + 1 && r.top >= clip.top - 1 && r.bottom <= clip.bottom + 1;
    const sample = edge ? rect(target() || node.getBoundingClientRect()) : bounds;
    const hit = document.elementFromPoint((sample.left + sample.right) / 2, (sample.top + sample.bottom) / 2);
    return { bounds, clip, rendered, text, reachable: visible(sample) && (hit === node || node.contains(hit)),
      horizontalTextFits: text.length > 0 && text.every(r => r.left >= clip.left - 1 && r.right <= clip.right + 1),
      controlTextFits: edge !== null || text.every(r => visible(r) && r.left >= bounds.left - 1 &&
        r.right <= bounds.right + 1 && r.top >= bounds.top - 1 && r.bottom <= bounds.bottom + 1),
      untruncated: style.textOverflow !== 'ellipsis' && ['none', '0'].includes(style.webkitLineClamp),
      focused: node === document.activeElement,
      focusIndicator: style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0 || style.boxShadow !== 'none' };
  }, edge);
}
function assertPart(geometry, label, focused = false) {
  assert.equal(geometry.rendered && geometry.untruncated && geometry.horizontalTextFits && geometry.controlTextFits && geometry.reachable, true,
    label + ' is visible without clipping in the visual viewport and ancestor scrollports');
  if (focused) {
    assert.equal(geometry.focused, true, label + ' receives keyboard focus');
    assert.equal(geometry.focusIndicator, true, label + ' has a visible focus indicator');
  }
}
async function confirmationGeometry(page) {
  const dialog = page.locator('#confirmation-dialog[open]');
  const geometry = await dialog.evaluate(node => {
    const r = node.getBoundingClientRect(), v = visualViewport;
    return { dialog: { x: r.x, y: r.y, width: r.width, height: r.height },
      viewport: { x: v.offsetLeft, y: v.offsetTop, width: v.width, height: v.height, scale: v.scale },
      scrollWidth: node.scrollWidth, clientWidth: node.clientWidth,
      scrollHeight: node.scrollHeight, clientHeight: node.clientHeight,
      overflowX: getComputedStyle(node).overflowX, overflowY: getComputedStyle(node).overflowY };
  });
  const { dialog: r, viewport: v } = geometry;
  assert.ok(r.width > 0 && r.height > 0 && r.x >= v.x - 1 && r.y >= v.y - 1 &&
    r.x + r.width <= v.x + v.width + 1 && r.y + r.height <= v.y + v.height + 1,
  'Complete confirmation dialog fits the actual visual viewport');
  assert.ok(geometry.scrollWidth <= geometry.clientWidth + 1, 'Confirmation has no horizontal internal overflow');
  for (const id of ['confirmation-title', 'confirmation-message']) {
    geometry[id] = [];
    for (const edge of ['start', 'end']) {
      const part = await partGeometry(dialog.locator('#' + id), edge);
      assertPart(part, id + ' ' + edge);
      geometry[id].push(part);
    }
  }
  return geometry;
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
  const geometry = await confirmationGeometry(page);
  assert.equal(await safe.isEnabled(), true, 'Cancel is usable');
  assert.equal(await action.isEnabled(), true, 'Approval is usable');
  await page.keyboard.press('Shift+Tab');
  geometry.approval = await partGeometry(action);
  assertPart(geometry.approval, 'Approval control', true);
  await page.keyboard.press('Tab');
  geometry.cancel = await partGeometry(safe);
  assertPart(geometry.cancel, 'Cancel control', true);
  await page.keyboard.press('Tab');
  assertPart(await partGeometry(action), 'Approval control', true);
  await page.keyboard.press('Tab');
  assertPart(await partGeometry(safe), 'Cancel control', true);
  return geometry;
}
async function zoomEvidence(context, page, directory, name, geometry) {
  const cdp = await context.newCDPSession(page);
  try {
    const metrics = await cdp.send('Page.getLayoutMetrics');
    const zoom = metrics.visualViewport.zoom;
    assert.equal(zoom, 2, 'Evidence is captured at actual native 200 percent');
    assert.ok(Math.abs(metrics.cssVisualViewport.clientWidth - geometry.viewport.width) < 1,
      'CDP and DOM agree on the actual CSS visual viewport');
    // Playwright fullPage/locator screenshots pass CSS geometry to CDP, whose
    // capture clip uses device-independent pixels at native browser zoom.
    // A viewport capture is reliable; scale only the targeted CDP clip.
    const viewport = await screenshot(page, directory, name, { fullPage: false });
    const r = geometry.dialog, v = geometry.viewport;
    assert.deepEqual(await page.locator('#confirmation-dialog[open]').boundingBox(), r,
      'Capture uses the measured confirmation geometry');
    const x = Math.floor((r.x - v.x) * zoom), y = Math.floor((r.y - v.y) * zoom);
    const clip = { x, y, width: Math.ceil((r.x - v.x + r.width) * zoom) - x,
      height: Math.ceil((r.y - v.y + r.height) * zoom) - y };
    const targeted = await screenshot(page, directory, name + '-dialog', { fullPage: false, clip });
    const dimensions = buffer => ({ width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) });
    const captures = { viewport: dimensions(viewport), dialog: dimensions(targeted), clip };
    assert.ok(captures.viewport.width >= metrics.visualViewport.clientWidth &&
      captures.viewport.height >= metrics.visualViewport.clientHeight, 'Viewport capture includes the full zoomed viewport');
    assert.ok(Math.abs(captures.dialog.width - clip.width) <= 1 && Math.abs(captures.dialog.height - clip.height) <= 1,
      'Targeted capture includes the complete zoomed confirmation bounds');
    await fs.writeFile(path.join(directory, name + '-geometry.json'), JSON.stringify({ actualBrowserZoom: zoom,
      devicePixelRatio: await page.evaluate(() => devicePixelRatio), geometry, metrics, captures }, null, 2));
  } finally { await cdp.detach(); }
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
module.exports = { screenshot, checkpoint, registerScreenshotPage, visibleEvidence, confirmationKeyboard, confirmationGeometry, nativeZoom, zoomEvidence };
