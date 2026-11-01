'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright');
const { nativeZoom } = require('./helpers/accessibility.cjs');
const { actionGeometry, settleActions, activateRowAction } = require('./helpers/request-actions.cjs');
const frontend = path.resolve(__dirname, '../../src/Asap.Web/Frontend');

test('real rendered row actions fit, resize, dispatch, dismiss and retire at native zoom', async t => {
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-actions-'));
  const extension = path.resolve(__dirname, '../browser/native-zoom-extension');
  const context = await chromium.launchPersistentContext(profile, { channel: 'chromium', headless: true,
    viewport: { width: 1440, height: 900 }, args: ['--disable-extensions-except=' + extension, '--load-extension=' + extension] });
  t.after(async () => { await context.close(); await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); });
  const page = context.pages()[0], errors = [], external = [];
  page.on('pageerror', error => errors.push(error.message));
  await context.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'https://localhost:8080') { external.push(url.href); await route.abort(); return; }
    if (url.pathname === '/staff/') {
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><head><title>Actions fixture</title>' +
        '<link rel="stylesheet" href="/vendor/font-awesome/4.7.0/css/font-awesome.css"><link rel="stylesheet" href="/staff/styles.css">' +
        '</head><body><main><h1>Request actions</h1><button id="fallback">Refresh</button><div id="fixture" style="width: min(900px, 100%)"></div></main></body></html>' });
    } else {
      const file = path.join(frontend, url.pathname);
      try { await route.fulfill({ contentType: url.pathname.endsWith('.js') ? 'text/javascript' : url.pathname.endsWith('.css') ? 'text/css' : 'application/octet-stream', body: await fs.readFile(file) }); }
      catch { errors.push('Missing fixture asset: ' + url.pathname); await route.abort(); }
    }
  });
  await page.goto('https://localhost:8080/staff/');
  await page.evaluate(require('axe-core').source);
  await page.evaluate(async () => {
    const { requestRowActions } = await import('/staff/js/request-row-actions.js');
    window.calls = []; window.current = true;
    window.mount = count => {
      window.ref?.(null);
      const host = document.createElement('div'); host.className = 'request-row-actions';
      document.querySelector('#fixture').replaceChildren(host);
      const names = ['Purchase', 'Reject', 'Open details', 'Already own', 'Claim', 'Assign', 'Close silently'];
      const keys = ['purchase', 'reject', 'edit', 'alreadyOwn', 'claim', 'assign', 'silentClose'];
      const glyphs = ['shopping-cart', 'ban', 'pencil', 'book', 'user-plus', 'users', 'archive'];
      window.ref = requestRowActions({ id: '27', actions: names.slice(0, count).map((label, i) => ({ key: keys[i], label, glyph: glyphs[i] })),
        live: event => window.current && event.currentTarget.isConnected, onOpen: (id, opener, action) => calls.push({ id, connected: opener.isConnected, ...action }),
        fallback: document.querySelector('#fallback') });
      window.ref(host);
    };
  });
  const fixture = page.locator('#fixture');
  for (const count of [0, 1, 2, 7]) {
    await page.evaluate(count => mount(count), count); await settleActions(page);
    assert.equal(await fixture.locator('[data-request-action]').count(), count);
    if (!count) { assert.equal(await fixture.locator('button, [popover]').count(), 0); continue; }
    const wide = await actionGeometry(fixture);
    assert.equal(wide.inline.length, count); assert.equal(wide.more, 0);
    await fixture.evaluate(node => { node.style.width = '120px'; }); await settleActions(page);
    const narrow = await actionGeometry(fixture);
    assert.equal(narrow.inline.length, 1); assert.equal(narrow.more, count > 1 ? 1 : 0);
    assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)).map(v => v.id)), []);
    await fixture.evaluate(node => { node.style.width = 'min(900px, 100%)'; }); await settleActions(page);
  }
  await fixture.evaluate(node => { node.style.width = '210px'; }); await settleActions(page);
  const more = fixture.locator('.request-row-more');
  await more.press('Space'); assert.equal(await more.getAttribute('aria-expanded'), 'true');
  await page.keyboard.press('Escape');
  assert.equal(await more.getAttribute('aria-expanded'), 'false'); assert.equal(await more.evaluate(n => n === document.activeElement), true);
  await more.press('Enter');
  const focusedKey = await page.evaluate(() => document.activeElement.dataset.requestAction);
  await fixture.evaluate(node => { node.style.width = 'min(900px, 100%)'; }); await settleActions(page);
  assert.equal(await more.count(), 0); assert.equal(await page.evaluate(() => document.activeElement.dataset.requestAction), focusedKey);
  await activateRowAction(fixture, 'assign', 'Enter');
  await fixture.evaluate(node => { node.style.width = '150px'; }); await settleActions(page);
  await activateRowAction(fixture, 'assign');
  assert.deepEqual(await page.evaluate(() => calls), [{ id: '27', connected: true, action: 'assign' }, { id: '27', connected: true, action: 'assign' }]);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await fixture.evaluate(node => { node.style.width = '100%'; });
    await nativeZoom(context, page, 2); await settleActions(page); await actionGeometry(fixture);
    await page.evaluate(() => mount(1)); await settleActions(page);
    assert.equal((await actionGeometry(fixture)).more, 0);
    await nativeZoom(context, page, 1); await page.evaluate(() => mount(7)); await settleActions(page);
  }
  await page.emulateMedia({ forcedColors: 'active' }); await settleActions(page); await actionGeometry(fixture);
  await fixture.evaluate(node => { node.style.fontFamily = 'monospace'; }); await settleActions(page); await actionGeometry(fixture);
  await page.evaluate(() => { window.current = false; });
  await activateRowAction(fixture, 'purchase'); assert.equal(await page.evaluate(() => calls.length), 2);
  await page.evaluate(() => { window.old = document.querySelector('[data-request-action]'); ref(null); old.click(); });
  await page.setViewportSize({ width: 800, height: 700 }); await settleActions(page);
  assert.equal(await page.evaluate(() => calls.length), 2); assert.equal(await fixture.locator('button').count(), 0);
  await page.evaluate(() => { current = true; mount(2); }); await settleActions(page);
  await page.evaluate(() => { window.retiredHost = document.querySelector('.request-row-actions'); retiredHost.remove(); });
  await settleActions(page); await settleActions(page);
  await page.evaluate(() => document.querySelector('#fixture').append(retiredHost)); await settleActions(page);
  assert.equal(await fixture.locator('button, [popover]').count(), 0, 'Removed rows retire their observers and controls before reinsertion');
  await page.evaluate(require('axe-core').source);
  assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)).map(v => v.id)), []);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
});
