'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright');
const { nativeZoom } = require('./helpers/accessibility.cjs');
const { obscurePicker, pickerGeometry, providerGeometry } = require('./helpers/workflow-settings.cjs');
const frontend = path.resolve(__dirname, '../../src/Asap.Web/Frontend');

// Focused browser regression with the shipped DOM/CSS/editor and a deterministic
// catalog. Real SQL-backed scope and read-only behavior stay in the S1 suite.
test('system and inherited patron-code pickers clear the footer at native zoom and limited heights', async t => {
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
    window.pickerData = { stored: { configuredSystem: config, libraryOverride: {
      allowedPatronCodeIds: { exists: false, values: [] }, providers: [], formats: [], templates: []
    } }, effective: {},
      patronCodeChoices: Array.from({ length: 16 }, (_, index) => ({ id: index + 2,
        description: index === 15 ? 'Wrapping description with a very long word ' + 'patron'.repeat(22) : 'Branch card ' + index })) };
    const footer = root.querySelector('.settings-save-bar');
    footer.classList.add('attention');
    root.querySelector('#settings-save-title').textContent = 'Unsaved changes';
    root.querySelector('#settings-discard').hidden = false;
    root.querySelector('#settings-reset').hidden = false;
  });
  const input = page.locator('#patron-code-picker');
  const sizes = [[1440, 1000, 2], [1280, 900, 1.5], [1440, 1000, 1], [390, 844, 1], [720, 340, 1]];
  for (const [system, width, height, zoom] of [true, false].flatMap(system => sizes.map(size => [system, ...size]))) {
    await page.evaluate(system => window.editors.populate({ ...window.pickerData, orgId: system ? 'system' : '2' }, system), system);
    await page.setViewportSize({ width, height });
    await nativeZoom(context, page, zoom);
    await page.locator('#patron-code-eligibility-message').focus();
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
    const selected = await pickerGeometry(page, { open: false });
    const reflow = selected.inheritanceHeight - choosing.inheritanceHeight;
    const scroll = selected.scrollY - choosing.scrollY;
    if (reflow === 0) assert.equal(scroll, 0, 'Choosing a suggestion preserves page position without ownership reflow');
    else {
      assert.ok(scroll >= Math.min(0, reflow) - 1 && scroll <= Math.max(0, reflow) + 1,
        'Selection scroll is bounded by ownership status reflow');
      assert.ok(Math.abs(selected.input.top + scroll - choosing.input.top - reflow) <= 1,
        'Ownership status accounts for the picker position change');
    }
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

test('provider inputs and actions align with inherited and pinned column statuses', async t => {
  const browser = await chromium.launch({ headless: true });
  t.after(() => browser.close());
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [], external = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', async dialog => { errors.push('Unexpected native dialog: ' + dialog.type()); await dialog.dismiss(); });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'https://localhost:8080') {
      external.push(url.origin); await route.abort(); return;
    }
    if (url.pathname === '/staff/') {
      const html = (await fs.readFile(path.join(frontend, 'staff/index.html'), 'utf8'))
        .replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, '');
      await route.fulfill({ contentType: 'text/html', body: html });
    } else {
      await route.fulfill({ contentType: url.pathname.endsWith('.js') ? 'text/javascript' :
        url.pathname.endsWith('.css') ? 'text/css' : 'application/octet-stream',
      body: await fs.readFile(path.join(frontend, url.pathname)) });
    }
  });
  await page.goto('https://localhost:8080/staff/');
  const artifacts = process.env.ASAP_PROVIDER_LAYOUT_ARTIFACTS;
  if (artifacts) await fs.mkdir(artifacts, { recursive: true });
  for (const [width, height] of [[1440, 1000], [1120, 900], [1000, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    for (const mode of ['system', 'inherited', 'pinned']) {
      await page.evaluate(async mode => {
        const { createSettingsDomainEditors } = await import('/staff/js/settings-domains.js');
        const root = document.querySelector('#settings-view');
        for (let node = root; node; node = node.parentElement) node.hidden = false;
        for (const node of document.querySelectorAll('#workspace > section')) node.hidden = node !== root;
        root.querySelector('#settings-form').hidden = false;
        for (const panel of root.querySelectorAll('.settings-panel')) panel.hidden = panel.id !== 'settings-workflow';
        const provider = { id: '20', key: 'catalog', label: 'System catalog',
          urlTemplate: 'https://catalog.example.org?q={query}', isEnabled: true, sortOrder: 0 };
        const absent = { exists: false, values: [] };
        const configured = { providers: [provider], formats: [], templates: [],
          publicationOptions: absent, commonCreators: absent, allowedPatronCodeIds: absent };
        const pinned = mode === 'pinned';
        const current = pinned ? { ...provider, label: 'Library catalog', isEnabled: false, overridden: true } : provider;
        const library = { ...configured, providers: pinned ? [{ id: '20', kind: 'libraryOverride',
          label: current.label, urlTemplate: null, isEnabled: false, version: 'provider-version' }] : [] };
        window.providerEditors?.dispose();
        window.providerEditors = createSettingsDomainEditors({ root });
        window.providerEditors.populate({ orgId: mode === 'system' ? 'system' : '2',
          stored: { configuredSystem: configured, libraryOverride: mode === 'system' ? null : library,
            providers: [current], formats: [], templates: [], customFields: [], formatRules: [], autoClaimRules: [] },
          effective: { externalSearchProviders: [current], formats: [], customFields: [] }, autoClaimStaff: [] }, mode === 'system');
      }, mode);
      await page.locator('#external-search-provider-editor').scrollIntoViewIfNeeded();
      if (artifacts) await page.screenshot({ path: path.join(artifacts, `${mode}-${width}.png`) });
      const geometry = await providerGeometry(page);
      assert.equal(geometry.rows.length, 1);
      assert.equal(geometry.rows[0].library, mode !== 'system');
      if (artifacts) await fs.writeFile(path.join(artifacts, `${mode}-${width}.json`), JSON.stringify(geometry, null, 2));
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    }
  }
  await page.evaluate(require('axe-core').source);
  assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations
    .filter(item => ['serious', 'critical'].includes(item.impact)).map(item => item.id)), []);
  assert.equal(await page.evaluate(() => [...document.images].filter(node => node.getClientRects().length)
    .every(node => node.complete && node.naturalWidth > 0)), true);
  assert.deepEqual(errors, []);
  assert.deepEqual(external, []);
});
