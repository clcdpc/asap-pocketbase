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
  const page = context.pages()[0], errors = [], external = [], mutations = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.method() === 'POST') mutations.push(request.url()); });
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
    const formats = ['book', 'digital', 'other', 'audio', 'video', 'journal'].map((code, index) => ({ id: String(10 + index), code,
      label: index ? 'Configured long format ' + 'wrapping'.repeat(18) : 'Configured Book', ownerOrganizationId: 1,
      overridden: false, version: 'AAAAAAAAAAE=',
      isEnabled: true, messageBehavior: 'none', title: { mode: 'required', label: 'Title' },
      author: { mode: 'optional', label: 'Author' }, identifier: { mode: 'hidden', label: 'Identifier' },
      publication: { mode: 'hidden', label: 'Publication' },
      customFields: { note: { mode: 'required', labelOverride: 'Draft label' }, retired: { mode: 'required', labelOverride: 'Historical label' } } }));
    const fields = [true, false].map((enabled, index) => ({ id: String(index + 1), key: index ? 'retired' : 'note',
      type: index ? 'text' : 'select', label: index ? 'Retired description' : 'Custom label ' + 'long'.repeat(20), enabled,
      options: index ? [] : [{ id: 'selected-option', label: 'Selected option', enabled: true },
        { id: 'disabled-option', label: 'Disabled option', enabled: false }] }));
    const config = { publicationOptions: { exists: true, values: [
      { id: 'published', label: 'Published', enabled: true },
      { id: 'forthcoming', label: 'Forthcoming', enabled: true },
      { id: 'any-time', label: 'Any time', enabled: false }
    ] }, commonCreators: { exists: false, values: [] },
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
  await page.addScriptTag({ path: require.resolve('axe-core/axe.min.js') });
  async function assertNoSeriousRuleAccessibilityFindings() {
    const findings = await page.evaluate(async () => {
      const block = document.querySelector('#format-rules-editor').parentElement;
      const results = await window.axe.run(block);
      return results.violations.filter(item => ['serious', 'critical'].includes(item.impact))
        .map(item => `${item.id}: ${item.help}`);
    });
    assert.deepEqual(findings, []);
  }
  const cards = page.locator('#format-rules-editor [data-rule-code]');
  const initialRuleCount = await cards.count();
  assert.equal(initialRuleCount, 6, 'The reported six-card exhausted state is represented by fixture data.');
  const addRule = page.locator('#add-format-rule');
  const ruleAvailability = page.locator('#format-rule-availability');
  assert.equal(await addRule.isDisabled(), true, 'Add rule is unavailable when every material format has a rule.');
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');
  assert.equal(await addRule.getAttribute('aria-describedby'), 'format-rule-availability');
  await assertNoSeriousRuleAccessibilityFindings();
  const first = cards.first(), button = first.locator('[data-rule-toggle]');
  assert.equal(await cards.locator('.settings-rule-details:visible').count(), 0);
  const collapsedHeight = (await first.boundingBox()).height;
  assert.equal(await first.locator('[data-rule-summary]').innerText(), '2 required · 1 optional · 2 hidden');
  const baseline = await page.evaluate(() => window.editors.snapshot());
  await button.focus(); await page.keyboard.press('Enter');
  await cards.nth(1).locator('[data-rule-toggle]').press('Space');
  assert.equal(await cards.locator('.settings-rule-details:visible').count(), 2);
  assert.ok((await first.boundingBox()).height > collapsedHeight * 2, 'Collapsed summaries are substantially shorter than the complete editor.');
  await button.press('Space');
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => Boolean(document.activeElement.closest('.settings-rule-details'))), false);
  assert.deepEqual(await page.evaluate(() => [window.editors.snapshot(), window.editors.collect().changed, window.ruleChanges]), [baseline, false, 0]);

  const availabilityFormatRows = page.locator('#material-formats-editor [data-domain-row]');
  const availabilityCodes = await page.evaluate(() => ({
    formats: [...document.querySelectorAll('#material-formats-editor [data-domain-row]')].map(row => row.dataset.formatCode),
    rules: [...document.querySelectorAll('#format-rules-editor [data-rule-code]')].map(row => row.dataset.ruleCode),
    expanded: [...document.querySelectorAll('#format-rules-editor [data-rule-code] [data-rule-toggle]')]
      .map(toggle => toggle.getAttribute('aria-expanded'))
  }));
  await page.locator('#add-material-format').click();
  const transientMaterial = availabilityFormatRows.last();
  const transientCode = await transientMaterial.getAttribute('data-format-code');
  assert.match(transientCode, /^custom_[a-f0-9]{32}$/);
  assert.equal(await addRule.isDisabled(), false,
    'Adding an unrepresented custom material format enables Add rule.');
  assert.equal(await ruleAvailability.isHidden(), true);
  assert.equal(await cards.count(), initialRuleCount, 'The new material format has no rule until Add rule is activated.');
  assert.equal(await page.locator(`#format-rules-editor [data-rule-code="${transientCode}"]`).count(), 0);
  await transientMaterial.locator('.settings-row-actions [data-row-action="delete"]').press('Enter');
  assert.equal(await addRule.isDisabled(), true,
    'Removing the unruled custom format returns Add rule to the exhausted state.');
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');
  assert.deepEqual(await page.evaluate(() => ({
    formats: [...document.querySelectorAll('#material-formats-editor [data-domain-row]')].map(row => row.dataset.formatCode),
    rules: [...document.querySelectorAll('#format-rules-editor [data-rule-code]')].map(row => row.dataset.ruleCode),
    expanded: [...document.querySelectorAll('#format-rules-editor [data-rule-code] [data-rule-toggle]')]
      .map(toggle => toggle.getAttribute('aria-expanded'))
  })), availabilityCodes, 'Removing an unruled format preserves existing identities and accordion state.');
  assert.equal((await page.evaluate(() => window.editors.collect())).changed, false,
    'Adding and removing the unruled format leaves no dirty settings draft.');
  assert.equal(await page.evaluate(() => window.editors.snapshot()), baseline);
  assert.deepEqual(mutations, [], 'The availability transition performs no Settings POST.');

  await button.click();
  const title = first.locator('[data-format-label="title"]');
  const material = page.locator('#material-formats-editor [data-domain-row]').first();
  await material.locator('.settings-domain-override-toggle').check();
  const label = material.locator('[data-material-field="label"]');
  const retainedTitle = await title.elementHandle();
  await label.fill('Renamed Book <draft>');
  assert.equal(await first.locator('.settings-rule-identity strong').innerText(), 'Renamed Book <draft>');
  assert.equal(await button.getAttribute('aria-label'), 'Collapse Renamed Book <draft> format rules');
  assert.equal(await title.getAttribute('aria-label'), 'Title patron-facing label for Renamed Book <draft>');
  assert.equal(await retainedTitle.evaluate(node => node.isConnected), true);
  assert.deepEqual(await page.evaluate(() => [window.editors.collect().domainsChanged.formats, window.editors.collect().domainsChanged.rules]), [true, false]);
  await button.click();
  await label.fill('Collapsed Book');
  assert.equal(await button.getAttribute('aria-label'), 'Expand Collapsed Book format rules');
  await button.click();
  assert.equal(await button.getAttribute('aria-label'), 'Collapse Collapsed Book format rules');
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
  const publication = page.locator('#publication-options-editor');
  const publicationRow = publication.locator('[data-option-id="published"]');
  assert.equal(await publication.locator('input[type="text"]').count(), 3, 'Only the readable label is an input.');
  assert.equal(await publicationRow.locator('[data-row-action="up"]').isDisabled(), true);
  assert.equal(await publication.locator('[data-domain-row]').last().locator('[data-row-action="down"]').isDisabled(), true);
  await publicationRow.locator('[data-publication-field="label"]').fill('Readable publication label');
  await publicationRow.locator('[data-row-action="down"]').press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.closest('[data-domain-row]')?.dataset.optionId), 'published');
  assert.equal(await publicationRow.locator('[data-row-action="down"]').getAttribute('aria-label'),
    'Move Readable publication label, publication option 2 down');
  await publicationRow.locator('[data-row-action="up"]').press('Enter');
  assert.equal(await publicationRow.locator('[data-publication-field="label"]').evaluate(node => node === document.activeElement), true);
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  for (const [name, width, height, zoom, columns, narrow] of [
    ['desktop', 1440, 1000, 1, 2, false], ['narrow-container', 1440, 1000, 1, 1, true],
    ['mobile', 390, 844, 1, 1, false], ['native-200', 1440, 1000, 2, 1, false]
  ]) {
    await page.setViewportSize({ width, height });
    await nativeZoom(context, page, zoom);
    await page.locator('#format-rules-editor').evaluate((node, narrow) => { node.style.maxWidth = narrow ? '540px' : ''; }, narrow);
    await publication.evaluate((node, narrow) => { node.style.maxWidth = narrow ? '360px' : ''; }, narrow);
    await page.locator('#additional-fields-editor').evaluate((node, narrow) => { node.style.maxWidth = narrow ? '360px' : ''; }, narrow);
    const geometry = await formatRuleGeometry(page, columns);
    geometry.publication = await publication.evaluate(node => {
      const bounds = element => {
        const r = element.getBoundingClientRect();
        return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width, height: r.height };
      };
      const row = node.querySelector('[data-domain-row]');
      return { container: bounds(node), row: bounds(row),
        label: bounds(row.querySelector('[data-publication-field="label"]')),
        enabled: bounds(row.querySelector('.settings-domain-check')),
        actions: bounds(row.querySelector('.settings-row-actions')),
        controls: [...row.querySelectorAll('input, button')].map(bounds),
        viewport: innerWidth, scrollWidth: document.documentElement.scrollWidth };
    });
    const p = geometry.publication;
    if (narrow) {
      assert.equal(p.viewport, 1440, 'Narrow publication qualification keeps the desktop viewport.');
      assert.ok(Math.abs(p.container.width - 360) <= 1, 'The publication container itself is constrained to 360px.');
      assert.ok(p.label.width >= 280 && p.actions.width >= 136, 'Wrapped rows retain an adequate label and complete action group.');
    }
    assert.ok(p.scrollWidth <= p.viewport, name + ': publication reflow has no page overflow');
    for (const control of p.controls) {
      assert.ok(control.width > 0 && control.left >= p.row.left && control.right <= p.row.right,
        name + ': publication controls fit their row: ' + JSON.stringify(p));
    }
    if (p.container.width >= 440) {
      assert.ok(p.label.right <= p.enabled.left && p.enabled.right <= p.actions.left, name + ': Label | Enabled | actions');
      assert.ok(Math.abs(p.label.bottom - p.actions.bottom) <= 1 && p.row.height <= 100,
        name + ': publication uses one compact desktop row: ' + JSON.stringify(p));
    } else {
      assert.ok(p.enabled.top >= p.label.bottom && p.actions.top >= p.enabled.bottom,
        name + ': publication wraps intentionally in a narrow container');
    }
    geometry.customOptions = await page.locator('#additional-fields-editor [data-option-row]').evaluateAll(rows => rows.map(row => {
      const bounds = element => {
        const r = element.getBoundingClientRect();
        return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width, height: r.height };
      };
      const text = document.createRange();
      text.selectNodeContents(row.querySelector('.settings-domain-check span'));
      return { row: bounds(row), input: bounds(row.querySelector('[data-option-field="label"]')),
        checkbox: bounds(row.querySelector('[data-option-field="enabled"]')),
        enabled: bounds(row.querySelector('.settings-domain-check')), text: bounds(text),
        remove: bounds(row.querySelector('button')), viewport: innerWidth };
    }));
    assert.equal(geometry.customOptions.length, 2, name + ': visible Select options are measured');
    const fits = (inside, outside) => inside.left >= outside.left - 1 && inside.right <= outside.right + 1 &&
      inside.top >= outside.top - 1 && inside.bottom <= outside.bottom + 1;
    const overlaps = (left, right) => left.left < right.right - 1 && left.right > right.left + 1 &&
      left.top < right.bottom - 1 && left.bottom > right.top + 1;
    for (const option of geometry.customOptions) {
      assert.equal(option.checkbox.width, 18, name + ': option Enabled checkbox retains its intended width');
      assert.equal(option.checkbox.height, 18, name + ': option Enabled checkbox retains its intended height');
      for (const part of [option.input, option.checkbox, option.text, option.remove]) {
        assert.ok(part.width > 0 && part.height > 0 && fits(part, option.row) &&
          part.left >= 0 && part.right <= option.viewport, name + ': option controls and actual Enabled text fit their row and viewport');
      }
      assert.ok(fits(option.text, option.enabled) && !overlaps(option.text, option.remove) &&
        !overlaps(option.text, option.checkbox), name + ': Enabled text stays visible without overlapping checkbox or Delete');
    }
    await page.evaluate(axe);
    assert.deepEqual(await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)).map(item => item.id)), [], name + ': axe');
    assert.equal(await page.evaluate(() => [...document.images].filter(image => image.getClientRects().length)
      .every(image => image.complete && image.naturalWidth > 0)), true);
    const publicationLabel = publicationRow.locator('[data-publication-field="label"]');
    await publicationLabel.focus(); await publicationLabel.press('Tab'); await page.keyboard.press('Shift+Tab');
    geometry.publication.focus = await publicationLabel.evaluate(node => ({
      focused: node === document.activeElement, visible: node.matches(':focus-visible'),
      outline: getComputedStyle(node).outlineStyle, width: parseFloat(getComputedStyle(node).outlineWidth)
    }));
    assert.ok(geometry.publication.focus.focused && geometry.publication.focus.visible &&
      geometry.publication.focus.outline !== 'none' && geometry.publication.focus.width >= 3, name + ': visible publication keyboard focus');
    await publicationRow.evaluate(node => node.scrollIntoView({ block: 'center' }));
    const publicationCdp = await context.newCDPSession(page);
    try {
      const capture = await publicationCdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(directory, 'publication-' + name + '.png'), Buffer.from(capture.data, 'base64'));
      await page.locator('#additional-fields-editor [data-option-row]').first().evaluate(node => node.scrollIntoView({ block: 'center' }));
      const optionCapture = await publicationCdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(directory, 'custom-option-' + name + '.png'), Buffer.from(optionCapture.data, 'base64'));
    } finally { await publicationCdp.detach(); }
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
  const addFormat = page.locator('#add-material-format');
  const formatRows = page.locator('#material-formats-editor [data-domain-row]');
  await addFormat.click();
  const firstCustomMaterial = formatRows.last();
  const firstCustomCode = await firstCustomMaterial.getAttribute('data-format-code');
  assert.equal(await addRule.isDisabled(), false, 'A newly available material format enables Add rule.');
  assert.equal(await ruleAvailability.isHidden(), true);
  await firstCustomMaterial.locator('[data-material-field="label"]').fill('First draft format');
  const beforeFirstAdd = await page.evaluate(() => window.editors.collect());
  await addRule.focus();
  await page.keyboard.press('Enter');
  const firstCustomCard = page.locator(`#format-rules-editor [data-rule-code="${firstCustomCode}"]`);
  assert.equal(await firstCustomCard.count(), 1, 'Exactly one rule is created for the first eligible format.');
  assert.equal(await firstCustomCard.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');
  assert.equal(await firstCustomCard.locator('[data-rule-toggle]').evaluate(node => node === document.activeElement), true,
    'Keyboard activation focuses the expanded new rule.');
  assert.equal((await page.evaluate(() => window.editors.collect())).domainsChanged.rules, true);
  assert.equal((await page.evaluate(() => window.editors.collect())).changed, true);
  assert.equal(await formatRows.count(), initialRuleCount + 1, 'Adding a rule does not create another material format.');
  const firstCustomTitle = firstCustomCard.locator('[data-format-label="title"]');
  await firstCustomTitle.fill('Unsaved first rule draft');

  await addFormat.click();
  const secondCustomMaterial = formatRows.last();
  const draftCode = await secondCustomMaterial.getAttribute('data-format-code');
  const customMaterial = page.locator(`#material-formats-editor [data-format-code="${draftCode}"]`);
  assert.notEqual(draftCode, firstCustomCode);
  assert.equal(await addRule.isDisabled(), false, 'The next unrepresented format remains eligible.');
  assert.equal(await ruleAvailability.isHidden(), true);
  const formatsBeforeSecondRule = await formatRows.count();
  await addRule.focus();
  await page.keyboard.press('Space');
  const customCard = page.locator(`#format-rules-editor [data-rule-code="${draftCode}"]`);
  const retainedCustom = await customCard.elementHandle();
  assert.equal(await customCard.count(), 1, 'The next activation adds one distinct rule.');
  assert.equal(await customCard.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');
  assert.equal(await customCard.locator('[data-rule-toggle]').evaluate(node => node === document.activeElement), true);
  assert.equal(await formatRows.count(), formatsBeforeSecondRule);
  assert.equal(await firstCustomCard.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true',
    'An existing expansion survives adding another rule.');
  assert.equal(await firstCustomTitle.inputValue(), 'Unsaved first rule draft', 'An existing rule draft survives rerender.');
  assert.equal(await addRule.isDisabled(), true);
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');
  await page.evaluate(() => document.getElementById('add-format-rule').dispatchEvent(new MouseEvent('click', { bubbles: true })));
  assert.equal(await page.locator('#format-rules-editor [data-rule-code]').count(), initialRuleCount + 2,
    'A repeated activation after exhaustion cannot append a duplicate rule.');
  assert.equal((await page.evaluate(() => window.editors.collect())).values.rules.length, initialRuleCount + 2);
  assert.ok(beforeFirstAdd.values.formats.length < (await page.evaluate(() => window.editors.collect())).values.formats.length);

  await customCard.locator('.settings-row-actions [data-row-action="delete"]').click();
  assert.equal(await addRule.isDisabled(), false, 'Deleting a rule makes its existing format available again.');
  assert.equal(await ruleAvailability.isHidden(), true);
  await addRule.focus();
  await page.keyboard.press('Enter');
  assert.equal(await page.locator(`#format-rules-editor [data-rule-code="${draftCode}"]`).count(), 1,
    'Deleting and re-adding keeps one rule for the same stable format code.');
  assert.equal(await addRule.isDisabled(), true);
  assert.equal(await firstCustomTitle.inputValue(), 'Unsaved first rule draft');

  const readdedCustomCard = page.locator(`#format-rules-editor [data-rule-code="${draftCode}"]`);
  const retainedReaddedCustom = await readdedCustomCard.elementHandle();
  const panelId = await customCard.locator('[data-rule-toggle]').getAttribute('aria-controls');
  const customTitle = readdedCustomCard.locator('[data-format-label="title"]');
  await customTitle.fill('Unsaved custom title');
  assert.match(draftCode, /^custom_[a-f0-9]{32}$/);
  await customMaterial.locator('[data-material-field="label"]').fill('Configured Book');
  assert.equal(await retainedReaddedCustom.evaluate(node => node.isConnected), true);
  assert.equal(await readdedCustomCard.getAttribute('data-rule-code'), draftCode);
  assert.equal(await readdedCustomCard.locator('[data-rule-toggle]').getAttribute('aria-controls'), panelId);
  assert.equal(await readdedCustomCard.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');
  assert.equal(await customTitle.inputValue(), 'Unsaved custom title');
  const position = await customCard.evaluate(node => [...node.parentElement.children].indexOf(node) + 1);
  for (const name of [`Move Configured Book, format ${position} up`, `Move Configured Book, format ${position} down`,
    `Delete Configured Book, format ${position} format rule`]) {
    assert.equal(await customCard.getByRole('button', { name, exact: true }).getAttribute('title'), name);
  }
  await customMaterial.getByRole('button', { name: 'Move up', exact: true }).click();
  const redrawnMaterial = page.locator(`#material-formats-editor [data-format-code="${draftCode}"]`);
  await redrawnMaterial.locator('[data-material-field="label"]').fill('Renamed after reorder');
  assert.equal(await redrawnMaterial.getAttribute('data-format-code'), draftCode);
  assert.equal(await retainedReaddedCustom.evaluate(node => node.isConnected && node.dataset.ruleCode), draftCode);
  assert.equal(await readdedCustomCard.locator('[data-rule-toggle]').getAttribute('aria-controls'), panelId);
  assert.equal(await customTitle.inputValue(), 'Unsaved custom title');
  assert.equal(await page.evaluate(() => window.editors.collect().values.rules.some(rule => rule.code === 'book')), true);
  await retainedCustom.dispose();
  await retainedReaddedCustom.dispose();

  await page.evaluate(() => {
    const data = structuredClone(window.ruleData);
    data.stored.configuredSystem.formats = [];
    data.stored.libraryOverride.formats = [];
    data.stored.formats = [];
    data.stored.formatRules = [];
    data.effective.formats = [];
    window.emptyFormatRuleData = data;
    window.editors.populate(data, false);
  });
  assert.equal(await cards.count(), 0);
  assert.equal(await addRule.isDisabled(), true);
  assert.equal(await ruleAvailability.innerText(), 'Add a material format before adding a rule.');
  await assertNoSeriousRuleAccessibilityFindings();
  assert.equal((await page.evaluate(() => window.editors.collect())).changed, false,
    'Availability feedback does not dirty an empty settings draft.');
  await page.evaluate(() => window.editors.populate(window.emptyFormatRuleData, true));
  assert.equal(await cards.count(), 0);
  assert.equal(await addRule.isDisabled(), true);
  assert.equal(await ruleAvailability.innerText(), 'Add a material format before adding a rule.');

  await page.evaluate(() => window.editors.populate(window.ruleData, true));
  assert.equal(await addRule.isDisabled(), true, 'System scope independently reports its exhausted formats.');
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');
  await page.evaluate(() => window.editors.populate(window.ruleData, false));
  assert.equal(await addRule.isDisabled(), true, 'Returning to the inherited library scope recomputes availability.');
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');

  await page.evaluate(() => {
    const data = structuredClone(window.ruleData);
    const format = data.effective.formats[0];
    data.stored.configuredSystem.formats = [format];
    data.stored.libraryOverride.formats = [];
    data.stored.formats = [format];
    data.stored.formatRules = data.stored.formatRules.filter(rule => rule.code === format.code);
    data.effective.formats = [format];
    window.singleFormatRuleData = data;
    window.editors.populate(data, true);
  });
  assert.equal(await cards.count(), 1);
  const lastRuleDelete = cards.first().locator('.settings-row-actions [data-row-action="delete"]');
  await lastRuleDelete.focus();
  await page.keyboard.press('Enter');
  assert.equal(await cards.count(), 0);
  assert.equal(await addRule.isDisabled(), false, 'Deleting the only rule makes Add rule immediately available.');
  assert.equal(await ruleAvailability.isHidden(), true);
  assert.equal(await addRule.evaluate(node => node === document.activeElement), true,
    'After keyboard deletion, focus lands on the newly enabled Add rule control.');
  await page.keyboard.press('Enter');
  assert.equal(await cards.count(), 1);
  assert.equal(await addRule.isDisabled(), true);
  assert.equal(await ruleAvailability.innerText(), 'All available formats already have rules.');
  assert.deepEqual(mutations, [], 'Add rule only updates the draft and never saves implicitly.');
  assert.deepEqual(errors, []);
  assert.deepEqual(external, []);
});
