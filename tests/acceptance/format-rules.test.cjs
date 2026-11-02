'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright');
const { nativeZoom } = require('./helpers/accessibility.cjs');
const { formatRuleGeometry } = require('./helpers/format-rules.cjs');
const frontend = path.resolve(__dirname, '../../src/Asap.Web/Frontend');

// Fast regression against shipped markup, CSS and controller. SQL, scope and
// the real global Save/Discard/Reset flows remain in the integration journeys.
test('format cards retain complete drafts and use the actual container width at native zoom', async t => {
  const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-format-rules-'));
  const directory = path.resolve(__dirname, '../../.artifacts/browser/i390-layout');
  await fs.mkdir(directory, { recursive: true });
  const extension = path.resolve(__dirname, '../browser/native-zoom-extension');
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium', headless: true, viewport: { width: 1440, height: 1000 },
    args: ['--disable-extensions-except=' + extension, '--load-extension=' + extension]
  });
  t.after(async () => {
    await context.close();
    await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  });
  const page = context.pages()[0], errors = [], external = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', async dialog => { errors.push('Unexpected native dialog: ' + dialog.type()); await dialog.dismiss(); });
  await context.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'https://localhost:8080') { external.push(url.origin); await route.abort(); return; }
    if (url.pathname === '/staff/') {
      const html = (await fs.readFile(path.join(frontend, 'staff/index.html'), 'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, '');
      return route.fulfill({ contentType: 'text/html', body: html });
    }
    const contentType = url.pathname.endsWith('.js') ? 'text/javascript' : url.pathname.endsWith('.css') ? 'text/css' : 'application/octet-stream';
    await route.fulfill({ contentType, body: await fs.readFile(path.join(frontend, url.pathname)) });
  });
  await page.goto('https://localhost:8080/staff/');
  await page.evaluate(async () => {
    const { createSettingsDomainEditors } = await import('/staff/js/settings-domains.js');
    const root = document.querySelector('#settings-view');
    for (let node = root; node; node = node.parentElement) node.hidden = false;
    for (const node of document.querySelectorAll('#workspace > section')) node.hidden = node !== root;
    root.querySelector('#settings-form').hidden = false;
    for (const panel of root.querySelectorAll('.settings-panel')) panel.hidden = panel.id !== 'settings-patron';
    for (const tab of root.querySelectorAll('[role="tab"]')) tab.setAttribute('aria-selected', String(tab.id === 'settings-nav-patron'));
    const formats = ['book', 'digital', 'other'].map((code, index) => ({ id: String(10 + index), code,
      label: index ? 'Configured long format ' + 'wrapping'.repeat(18) : 'Configured Book', ownerOrganizationId: 1,
      overridden: false, version: 'AAAAAAAAAAE=',
      isEnabled: true, messageBehavior: 'none', title: { mode: 'required', label: 'Title' },
      author: { mode: 'optional', label: 'Author' }, identifier: { mode: 'hidden', label: 'Identifier' },
      publication: { mode: 'hidden', label: 'Publication' },
      customFields: { note: { mode: 'required', labelOverride: 'Draft label' }, retired: { mode: 'required', labelOverride: 'Historical label' } } }));
    const fields = [true, false].map((enabled, index) => ({ id: String(index + 1), key: index ? 'retired' : 'note',
      type: 'text', label: index ? 'Retired description' : 'Custom label ' + 'long'.repeat(20), enabled, options: [] }));
    const config = { publicationOptions: { exists: false, values: [] }, commonCreators: { exists: false, values: [] },
      allowedPatronCodeIds: { exists: false, values: [] }, providers: [], formats, templates: [] };
    window.ruleChanges = 0;
    window.editors = createSettingsDomainEditors({ root, onChange: () => window.ruleChanges++ });
    window.ruleData = { orgId: '2', stored: { configuredSystem: config, libraryOverride: { ...config, formats: [], providers: [], templates: [] },
      providers: [], formats, templates: [], customFields: fields, autoClaimRules: [], origins: [],
      formatRules: formats.map(format => ({ code: format.code, customFields: format.customFields })) },
      effective: { formats, customFields: fields.filter(field => field.enabled), externalSearchProviders: [] },
      patronCodeChoices: [], autoClaimStaff: [] };
    window.editors.populate(window.ruleData, false);
  });
  const cards = page.locator('#format-rules-editor [data-rule-code]');
  const first = cards.first(), button = first.locator('[data-rule-toggle]');
  assert.equal(await cards.locator('.settings-rule-details:visible').count(), 0);
  assert.equal(await first.locator('[data-rule-summary]').innerText(), '2 required · 1 optional · 2 hidden');
  const baseline = await page.evaluate(() => window.editors.snapshot());
  await button.focus(); await page.keyboard.press('Enter');
  await cards.nth(1).locator('[data-rule-toggle]').press('Space');
  assert.equal(await cards.locator('.settings-rule-details:visible').count(), 2);
  await button.press('Space');
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => Boolean(document.activeElement.closest('.settings-rule-details'))), false);
  assert.deepEqual(await page.evaluate(() => [window.editors.snapshot(), window.editors.collect().changed, window.ruleChanges]), [baseline, false, 0]);
  await button.click();
  const title = first.locator('[data-format-label="title"]');
  const material = page.locator('#material-formats-editor [data-domain-row]').first();
  await material.locator('.settings-domain-override-toggle').check();
  const label = material.locator('input[type="text"]').nth(1);
  const retainedTitle = await title.elementHandle();
  await label.fill('Renamed Book <draft>');
  assert.equal(await first.locator('.settings-rule-identity strong').innerText(), 'Renamed Book <draft>');
  assert.equal(await button.getAttribute('aria-label'), 'Collapse Renamed Book <draft> (book) format rules');
  assert.equal(await title.getAttribute('aria-label'), 'Title patron-facing label for Renamed Book <draft> (book)');
  assert.equal(await retainedTitle.evaluate(node => node.isConnected), true);
  assert.deepEqual(await page.evaluate(() => [window.editors.collect().domainsChanged.formats, window.editors.collect().domainsChanged.rules]), [true, false]);
  await button.click();
  await label.fill('Collapsed Book');
  assert.equal(await button.getAttribute('aria-label'), 'Expand Collapsed Book (book) format rules');
  await button.click();
  assert.equal(await button.getAttribute('aria-label'), 'Collapse Collapsed Book (book) format rules');
  await label.fill('Configured Book');
  await material.locator('.settings-domain-override-toggle').uncheck();
  assert.equal(await page.evaluate(() => window.editors.collect().changed), false);
  await retainedTitle.dispose();
  await title.fill('Unsaved title draft');
  const author = first.locator('[data-format-field="author"]');
  await author.selectOption('required');
  assert.equal(await first.locator('[data-rule-summary]').innerText(), '3 required · 0 optional · 2 hidden');
  await first.getByRole('button', { name: 'Move Configured Book down', exact: true }).press('Enter');
  const book = page.locator('#format-rules-editor [data-rule-code="book"]');
  assert.equal(await book.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');
  assert.equal(await book.locator('[data-format-label="title"]').inputValue(), 'Unsaved title draft');
  assert.equal(await page.evaluate(() => document.activeElement.isConnected && document.activeElement.getAttribute('aria-label')), 'Move Configured Book down');
  await book.getByRole('button', { name: 'Move Configured Book up', exact: true }).click();
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const [name, width, height, zoom, columns, narrow] of [
    ['desktop', 1440, 1000, 1, 2, false], ['narrow-container', 1440, 1000, 1, 1, true],
    ['mobile', 390, 844, 1, 1, false], ['native-200', 1440, 1000, 2, 1, false]
  ]) {
    await page.setViewportSize({ width, height });
    await nativeZoom(context, page, zoom);
    await page.locator('#format-rules-editor').evaluate((node, narrow) => { node.style.maxWidth = narrow ? '540px' : ''; }, narrow);
    const geometry = await formatRuleGeometry(page, columns);
    await page.evaluate(axe);
    assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)).map(item => item.id)), [], name + ': axe');
    assert.equal(await page.evaluate(() => [...document.images].filter(image => image.getClientRects().length)
      .every(image => image.complete && image.naturalWidth > 0)), true);
    await first.evaluate(node => node.scrollIntoView({ block: 'start' }));
    const cdp = await context.newCDPSession(page);
    try {
      geometry.metrics = await cdp.send('Page.getLayoutMetrics');
      const capture = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(directory, name + '.png'), Buffer.from(capture.data, 'base64'));
    } finally { await cdp.detach(); }
    await fs.writeFile(path.join(directory, name + '-geometry.json'), JSON.stringify(geometry, null, 2));
  }
  // The geometry assertion must detect the old four squeezed columns.
  await nativeZoom(context, page, 1);
  await first.locator('.settings-rule-fields').evaluate(node => { node.style.gridTemplateColumns = 'repeat(4, minmax(0, 1fr))'; });
  await assert.rejects(formatRuleGeometry(page, 2), /Identifier and Publication/);
  await first.locator('.settings-rule-fields').evaluate(node => node.style.removeProperty('grid-template-columns'));
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await button.focus();
  await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
  assert.equal(await button.evaluate(node => getComputedStyle(node).outlineStyle !== 'none'), true);
  assert.deepEqual(errors, []);
  assert.deepEqual(external, []);
});
