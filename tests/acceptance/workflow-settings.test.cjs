'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright');
const { nativeZoom } = require('./helpers/accessibility.cjs');
const { obscurePicker, pickerGeometry } = require('./helpers/workflow-settings.cjs');
const frontend = path.resolve(__dirname, '../../src/Asap.Web/Frontend');

// Focused browser regression with the shipped DOM/CSS/editor and a deterministic
// catalog. Real SQL-backed scope and read-only behavior stay in the S1 suite.
test('patron-code picker clears the sticky footer at native zoom and limited heights', async t => {
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-picker-layout-'));
  const extension = path.resolve(__dirname, '../browser/native-zoom-extension');
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium', headless: true, viewport: { width: 1440, height: 1000 },
    args: ['--disable-extensions-except=' + extension, '--load-extension=' + extension]
  });
  t.after(async () => {
    await context.close();
    await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  });
  const errors = [], external = [];
  const page = context.pages()[0];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', async dialog => { errors.push('Unexpected native dialog: ' + dialog.type()); await dialog.dismiss(); });
  await context.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'https://localhost:8080') { external.push(url.origin); await route.abort(); return; }
    if (url.pathname === '/staff/') {
      const html = (await fs.readFile(path.join(frontend, 'staff/index.html'), 'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, '');
      await route.fulfill({ contentType: 'text/html', body: html });
    } else {
      const contentType = url.pathname.endsWith('.js') ? 'text/javascript' : url.pathname.endsWith('.css') ? 'text/css' : 'application/octet-stream';
      await route.fulfill({ contentType, body: await fs.readFile(path.join(frontend, url.pathname)) });
    }
  });
  await page.goto('https://localhost:8080/staff/');
  await page.evaluate(async () => {
    const { createSettingsDomainEditors } = await import('/staff/js/settings-domains.js');
    const root = document.querySelector('#settings-view');
    for (let node = root; node; node = node.parentElement) node.hidden = false;
    for (const node of document.querySelectorAll('#workspace > section')) node.hidden = node !== root;
    root.querySelector('#settings-form').hidden = false;
    for (const panel of root.querySelectorAll('.settings-panel')) panel.hidden = panel.id !== 'settings-workflow';
    for (const tab of root.querySelectorAll('[role="tab"]')) tab.setAttribute('aria-selected', String(tab.id === 'settings-nav-workflow'));
    const config = { allowedPatronCodeIds: { exists: true, values: [1, 77] }, providers: [], formats: [], templates: [] };
    window.editors = createSettingsDomainEditors({ root });
    window.editors.populate({ orgId: 'system', stored: { configuredSystem: config }, effective: {},
      patronCodeChoices: Array.from({ length: 16 }, (_, index) => ({ id: index + 2,
        description: index === 15 ? 'Wrapping description with a very long word ' + 'patron'.repeat(22) : 'Branch card ' + index })) }, true);
    const footer = root.querySelector('.settings-save-bar');
    footer.classList.add('attention');
    root.querySelector('#settings-save-title').textContent = 'Unsaved changes';
    root.querySelector('#settings-discard').hidden = false;
    root.querySelector('#settings-reset').hidden = false;
  });
  const input = page.locator('#patron-code-picker');
  for (const [width, height, zoom] of [[1440, 1000, 2], [1280, 900, 1.5], [1440, 1000, 1], [390, 844, 1], [720, 340, 1]]) {
    await page.setViewportSize({ width, height });
    await nativeZoom(context, page, zoom);
    await obscurePicker(page);
    await input.focus();
    await pickerGeometry(page, { open: false });
    await input.fill('Branch');
    await input.press('ArrowDown');
    const opened = await pickerGeometry(page);
    for (let count = 0; count < 14; count++) {
      await input.press('ArrowDown');
      const navigated = await pickerGeometry(page);
      assert.equal(navigated.scrollY, opened.scrollY, 'Only the suggestion list scrolls during keyboard navigation');
    }
    await input.press('ArrowUp');
    await pickerGeometry(page);
    await input.press('Escape');
    assert.equal((await pickerGeometry(page, { open: false })).scrollY, opened.scrollY, 'Escape does not jump the page');
    await input.fill('no matching code');
    await pickerGeometry(page, { open: false });
    await input.fill('');
    await input.press('ArrowUp');
    await pickerGeometry(page);
    await input.press('Tab');
    assert.equal(await input.getAttribute('aria-expanded'), 'false');
    assert.equal(await input.evaluate(node => node === document.activeElement), false);
    await page.keyboard.press('Shift+Tab');
    await pickerGeometry(page, { open: false });
    await input.fill('Branch');
    await input.press('ArrowDown');
    await input.evaluate(node => window.scrollBy(0, node.getBoundingClientRect().top - visualViewport.offsetTop - 12));
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await settle();
    const below = await pickerGeometry(page);
    assert.ok(below.list.top >= below.input.bottom, 'Scrolling near the top updates to downward placement');
    await page.setViewportSize({ width, height: height - 40 });
    await settle();
    await pickerGeometry(page);
    await page.locator('#settings-save-detail').evaluate(node => { node.textContent = 'Wrapped unsaved status '.repeat(8); });
    await settle();
    await pickerGeometry(page);
    const priorScroll = await page.evaluate(() => scrollY);
    await page.evaluate(() => window.scrollBy(0, 150));
    await settle();
    assert.equal(await page.evaluate(() => scrollY), priorScroll + 150, 'Scrolling away never traps the page at the focused input');
    assert.equal(await input.getAttribute('aria-expanded'), 'false', 'An offscreen anchor dismisses its list');
    await input.press('ArrowDown');
    const choosing = await pickerGeometry(page);
    const chosen = await input.getAttribute('aria-activedescendant');
    await input.press('Enter');
    assert.equal((await pickerGeometry(page, { open: false })).scrollY, choosing.scrollY, 'Choosing a suggestion does not jump the page');
    const chip = page.locator('#patron-codes-editor [data-code-id="' + chosen.replace('patron-code-option-', '') + '"]');
    assert.equal(await chip.count(), 1);
    await chip.getByRole('button').click();
    await page.locator('#settings-save-detail').evaluate(node => { node.textContent = 'Everything in this settings context is saved.'; });
  }
  await page.evaluate(require('axe-core').source);
  assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations
    .filter(item => ['serious', 'critical'].includes(item.impact)).map(item => item.id)), []);
  assert.equal(await page.evaluate(() => [...document.images].filter(node => node.getClientRects().length)
    .every(node => node.complete && node.naturalWidth > 0)), true);
  assert.deepEqual(errors, []);
  assert.deepEqual(external, []);
});
