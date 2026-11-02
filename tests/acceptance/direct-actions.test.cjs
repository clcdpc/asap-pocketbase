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
    const { requestRowActions, eligibleRowActions } = await import('/staff/js/request-row-actions.js');
    window.calls = []; window.current = true;
    window.mountActions = actions => {
      window.ref?.(null);
      const host = document.createElement('div'); host.className = 'request-row-actions';
      document.querySelector('#fixture').replaceChildren(host);
      window.ref = requestRowActions({ id: '27', actions,
        live: event => window.current && event.currentTarget.isConnected, onOpen: (id, opener, action) => calls.push({ id, connected: opener.isConnected, ...action }),
        fallback: document.querySelector('#fallback') });
      window.ref(host);
    };
    const actor = { id: '20', role: 'staff', organizationId: 2 };
    window.workflow = request => eligibleRowActions({ libraryOrgId: 2, ...request }, actor);
    const suggestions = workflow({ status: 'suggestion', capabilities: { allowedActions: ['purchase', 'reject', 'alreadyOwn', 'silentClose'] } });
    window.mount = count => mountActions(suggestions.slice(0, count));
  });
  const fixture = page.locator('#fixture');
  for (const count of [0, 1, 2, 6]) {
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
  const suggestionKeys = ['purchase', 'reject', 'alreadyOwn', 'claim', 'assign', 'silentClose'];
  for (const width of ['900px', '120px']) {
    await fixture.evaluate((node, width) => { node.style.width = width; }, width); await settleActions(page);
    for (const key of suggestionKeys) await activateRowAction(fixture, key, width === '900px' ? 'Enter' : 'Space');
  }
  assert.deepEqual(await page.evaluate(() => calls.map(call => call.action)), [...suggestionKeys, ...suggestionKeys], 'Every secondary command uses the same dispatch directly and in overflow');
  await page.evaluate(() => { calls = []; });
  await fixture.evaluate(node => { node.style.width = '210px'; }); await settleActions(page);
  const more = fixture.locator('.request-row-more');
  await more.press('Space'); assert.equal(await more.getAttribute('aria-expanded'), 'true');
  await page.keyboard.press('Escape');
  assert.equal(await more.getAttribute('aria-expanded'), 'false'); assert.equal(await more.evaluate(n => n === document.activeElement), true);
  await more.press('Enter'); await page.setViewportSize({ width: 1440, height: 860 }); await settleActions(page);
  assert.equal(await more.getAttribute('aria-expanded'), 'false', 'Viewport-only resize dismisses a menu even when its column width is unchanged');
  assert.equal(await more.evaluate(n => n === document.activeElement), true);
  await more.press('Enter');
  const focusedKey = await page.evaluate(() => document.activeElement.dataset.requestAction);
  await fixture.evaluate(node => { node.style.width = 'min(900px, 100%)'; }); await settleActions(page);
  assert.equal(await more.count(), 0); assert.equal(await page.evaluate(() => document.activeElement.dataset.requestAction), focusedKey);
  await activateRowAction(fixture, 'assign', 'Enter');
  await fixture.evaluate(node => { node.style.width = '150px'; }); await settleActions(page);
  await activateRowAction(fixture, 'assign');
  assert.deepEqual(await page.evaluate(() => calls), [{ id: '27', connected: true, action: 'assign' }, { id: '27', connected: true, action: 'assign' }]);
  await page.evaluate(() => {
    const fixture = document.querySelector('#fixture'), outer = document.createElement('div'), inner = document.createElement('div');
    outer.id = 'horizontal-actions'; outer.style.cssText = 'width:240px;max-width:100%;overflow-x:auto';
    inner.style.width = '1200px'; fixture.before(outer); outer.append(inner); inner.append(fixture);
    fixture.style.width = '210px'; fixture.style.marginLeft = '800px';
  });
  await nativeZoom(context, page, 2); await settleActions(page);
  for (const activation of ['Enter', 'Space', 'Enter']) {
    await page.locator('#fallback').focus();
    await page.locator('#horizontal-actions').evaluate(node => { node.scrollLeft = 0; }); await settleActions(page);
    await more.focus(); await page.keyboard.press(activation); await settleActions(page);
    assert.equal(await more.getAttribute('aria-expanded'), 'true', 'Focus scrolling before activation must not dismiss the new popup');
    assert.ok(await page.locator('#horizontal-actions').evaluate(node => node.scrollLeft > 0), 'Keyboard focus reveals the offscreen command');
    await page.locator('#horizontal-actions').dispatchEvent('scroll');
    assert.equal(await more.getAttribute('aria-expanded'), 'true', 'A delayed scroll event with an unchanged anchor keeps the popup open');
    await page.locator('#horizontal-actions').evaluate(node => { node.scrollLeft -= 20; }); await settleActions(page);
    assert.equal(await more.getAttribute('aria-expanded'), 'false', 'An actual anchor move dismisses the popup');
    assert.equal(await more.evaluate(node => node === document.activeElement), true);
    await more.press(activation);
    await page.keyboard.press('Escape');
  }
  await nativeZoom(context, page, 1);
  await page.evaluate(() => {
    const fixture = document.querySelector('#fixture'), outer = document.querySelector('#horizontal-actions');
    outer.before(fixture); outer.remove(); fixture.style.marginLeft = '';
  }); await settleActions(page);
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await fixture.evaluate(node => { node.style.width = '100%'; });
    await nativeZoom(context, page, 2); await settleActions(page); await actionGeometry(fixture);
    await page.evaluate(() => mount(1)); await settleActions(page);
    assert.equal((await actionGeometry(fixture)).more, 0);
    await nativeZoom(context, page, 1); await page.evaluate(() => mount(6)); await settleActions(page);
  }
  for (const [status, capabilities, primary, autohold = true] of [
    ['outstanding_purchase', { allowedActions: ['catalogFound'] }, 'catalogFound', true],
    ['outstanding_purchase', { allowedActions: ['catalogFound'] }, 'catalogFound', false],
    ['pending_hold', { canPlaceHold: true, canChangeWorkflowState: true }, 'place-hold'],
    ['pending_hold', { canPlaceHold: false }, 'assign'],
    ['hold_placed', { allowedActions: ['close'] }, 'close'],
    ['closed', { allowedActions: ['reopen'] }, 'reopen'],
    ['closed', { allowedActions: [], blockingReason: 'hold_history_retained' }, null]
  ]) {
    await fixture.evaluate(node => { node.style.width = 'min(900px, 100%)'; });
    await page.evaluate(({ status, capabilities, autohold }) => mountActions(workflow({ status, capabilities, autohold, claimedByStaffUserId: '20' })),
      { status, capabilities, autohold }); await settleActions(page);
    const wide = await actionGeometry(fixture);
    assert.equal(wide.inline[0] || null, primary); assert.equal(wide.more, 0);
    if (primary) {
      const before = await page.evaluate(() => calls.length);
      await activateRowAction(fixture, primary, 'Enter');
      assert.equal(await page.evaluate(() => calls.length), before + 1);
      assert.equal(await page.evaluate(() => calls.at(-1).action), primary);
      await page.evaluate(() => { current = false; });
      await activateRowAction(fixture, primary); assert.equal(await page.evaluate(() => calls.length), before + 1);
      await page.evaluate(() => { current = true; });
    }
    await fixture.evaluate(node => { node.style.width = '100px'; }); await nativeZoom(context, page, 2); await settleActions(page);
    const narrow = await actionGeometry(fixture);
    assert.equal(narrow.inline[0] || null, primary);
    if (narrow.keys.length <= 1) assert.equal(narrow.more, 0);
    await nativeZoom(context, page, 1);
  }
  // A hidden/zero-width cell remains unpainted until measurable, then presents
  // the actual action set. Replacing it also removes the previous menu.
  await fixture.evaluate(node => { node.style.width = '0'; });
  await page.evaluate(() => mount(6)); await settleActions(page);
  assert.equal(await fixture.locator('.request-row-actions').evaluate(node => getComputedStyle(node).visibility), 'hidden');
  await fixture.evaluate(node => { node.style.width = '210px'; }); await settleActions(page); await actionGeometry(fixture);
  await fixture.locator('.request-row-more').press('Enter');
  await fixture.evaluate(node => { node.style.width = '0'; }); await settleActions(page);
  assert.equal(await fixture.locator('.request-row-actions').evaluate(node => getComputedStyle(node).visibility), 'hidden', 'An already measured cell also retires its visible layout at zero width');
  assert.equal(await fixture.locator('[popover]:popover-open').count(), 0);
  assert.equal(await page.locator('#fallback').evaluate(node => node === document.activeElement), true);
  await fixture.evaluate(node => { node.style.width = '210px'; }); await settleActions(page); await actionGeometry(fixture);
  await page.emulateMedia({ forcedColors: 'active' }); await settleActions(page); await actionGeometry(fixture);
  await fixture.evaluate(node => { node.style.fontFamily = 'monospace'; }); await settleActions(page); await actionGeometry(fixture);
  const dispatches = await page.evaluate(() => calls.length);
  await page.evaluate(() => { window.current = false; });
  await fixture.locator('.request-row-more').click();
  assert.equal(await fixture.locator('[popover]:popover-open').count(), 0, 'A revoked session cannot reopen More');
  await fixture.locator('[data-request-action]').evaluateAll(buttons => buttons.forEach(button => button.click()));
  assert.equal(await page.evaluate(() => calls.length), dispatches, 'Stale direct and overflow commands cannot dispatch');
  await page.evaluate(() => { window.old = document.querySelector('[data-request-action]'); ref(null); old.click(); });
  await page.setViewportSize({ width: 800, height: 700 }); await settleActions(page);
  assert.equal(await page.evaluate(() => calls.length), dispatches); assert.equal(await fixture.locator('button').count(), 0);
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
