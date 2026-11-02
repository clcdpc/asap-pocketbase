'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const { formatRuleGeometry } = require('../helpers/format-rules.cjs');
const { json } = require('../helpers/assertions.cjs');

async function openSettings(t, scope) {
  await t.page.goto(t.origin + '/staff/?stage=settings&settingsScope=' + scope + '#settings-patron');
  await t.page.waitForFunction(scope => document.querySelector('#settings-version').value &&
    document.querySelector('#settings-scope').value === scope && !document.querySelector('#settings-form').inert &&
    !document.querySelector('#settings-refresh').disabled, scope);
  await t.page.locator('#settings-nav-patron').click();
  return json(t.context, t.origin, '/api/asap/staff/settings?orgId=' + scope);
}
const cards = page => page.locator('#format-rules-editor [data-rule-code]');
const toggle = card => card.locator('[data-rule-toggle]');
async function unchanged(t, scope, before) {
  const after = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=' + scope);
  assert.equal(after.version, before.version, 'Draft review preserves the SQL settings version');
  assert.deepEqual(after.stored, before.stored, 'Draft review preserves every stored setting');
  assert.deepEqual(t.mutations, []);
}
async function evidence(t, name, columns, zoom = false) {
  await cards(t.page).first().evaluate(node => node.scrollIntoView({ block: 'start' }));
  const geometry = await formatRuleGeometry(t.page, columns);
  const cdp = await t.context.newCDPSession(t.page);
  try { geometry.metrics = await cdp.send('Page.getLayoutMetrics'); }
  finally { await cdp.detach(); }
  if (zoom) assert.equal(geometry.metrics.visualViewport.zoom, 2);
  await fs.writeFile(path.join(t.directory, name + '-geometry.json'), JSON.stringify(geometry, null, 2));
  await checkpoint(t.page, t.directory, name, { nativeViewport: zoom });
}
async function initialCards(page, before) {
  assert.equal(await cards(page).count(), before.effective.formats.length);
  const definitions = before.stored.customFields || [];
  for (const [index, format] of before.effective.formats.entries()) {
    const card = cards(page).nth(index);
    assert.equal(await card.getAttribute('data-rule-code'), format.code);
    assert.equal(await toggle(card).getAttribute('aria-expanded'), 'false');
    assert.ok((await toggle(card).innerText()).includes(format.label));
    assert.ok((await toggle(card).innerText()).includes(format.code));
    assert.equal(await card.locator('.settings-rule-details').isVisible(), false);
    assert.equal(await card.locator('[data-format-field]').count(), 4);
    assert.equal(await card.locator('[data-format-label]').count(), 4);
    const counts = { required: 0, optional: 0, hidden: 0 };
    for (const field of ['title', 'author', 'identifier', 'publication']) counts[format[field].mode]++;
    const stored = before.stored.formatRules?.find(rule => rule.code === format.code)?.customFields || {};
    for (const definition of definitions.filter(field => field.enabled)) {
      counts[(stored[definition.key] || format.customFields[definition.key])?.mode || 'hidden']++;
    }
    assert.equal(await card.locator('[data-rule-summary]').innerText(),
      `${counts.required} required · ${counts.optional} optional · ${counts.hidden} hidden`);
  }
  assert.equal(await page.locator('#settings-save').isDisabled(), true);
}

const scenarios = ['system', '2'].flatMap(scope => [
  { id: 'i390-draft-' + scope, writes: false,
    requirements: ['#390: collapsed complete cards, live draft names/accessibility, SQL-backed counts, independent keyboard disclosure, draft summaries, fail-closed redraws and no presentation mutations'],
    run: async t => {
      const before = await openSettings(t, scope);
      await initialCards(t.page, before);
      const first = cards(t.page).first(), second = cards(t.page).nth(1);
      const summary = first.locator('[data-rule-summary]');
      const initial = await summary.innerText();
      await toggle(first).focus(); await t.page.keyboard.press('Enter');
      assert.equal(await toggle(first).getAttribute('aria-expanded'), 'true');
      assert.equal(await toggle(first).evaluate(node => node === document.activeElement), true);
      await t.page.keyboard.press('Tab');
      assert.equal(await first.locator('.settings-row-actions button').nth(1).evaluate(node => node === document.activeElement), true);
      await toggle(second).press('Space');
      assert.equal(await toggle(first).getAttribute('aria-expanded'), 'true');
      assert.equal(await toggle(second).getAttribute('aria-expanded'), 'true');
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Disclosures never enable Save');
      const materials = t.page.locator('#material-formats-editor [data-domain-row]');
      for (const [index, expanded] of [[0, true], [1, false]]) {
        const card = cards(t.page).nth(index), material = materials.nth(index);
        if (!expanded) await toggle(card).click();
        const label = material.locator('input[type="text"]').nth(1);
        const originalLabel = await label.inputValue(), code = await card.getAttribute('data-rule-code');
        const override = material.locator('.settings-domain-override-toggle');
        const originalOverride = scope === '2' && await override.isChecked();
        if (scope === '2') await override.check();
        const control = await card.locator('[data-format-label="title"]').elementHandle();
        const draftLabel = originalLabel + ' renamed draft';
        await label.fill(draftLabel);
        assert.equal(await card.locator('.settings-rule-identity strong').innerText(), draftLabel);
        assert.equal(await toggle(card).getAttribute('aria-label'), `${expanded ? 'Collapse' : 'Expand'} ${draftLabel} (${code}) format rules`);
        assert.equal(await card.locator('[data-format-field="title"]').getAttribute('aria-label'), `Title requirement for ${draftLabel} (${code})`);
        assert.equal(await card.locator('.settings-row-actions button').last().getAttribute('aria-label'), `Delete ${draftLabel} format rule`);
        assert.equal(await control.evaluate(node => node.isConnected), true, 'Renaming never replaces draft controls');
        await toggle(card).click(); await toggle(card).click();
        assert.equal(await toggle(card).getAttribute('aria-label'), `${expanded ? 'Collapse' : 'Expand'} ${draftLabel} (${code}) format rules`);
        await label.fill(originalLabel);
        if (scope === '2') await override.setChecked(originalOverride);
        await control.dispose();
      }
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Restoring labels and inheritance clears the draft');
      const author = first.locator('[data-format-field="author"]');
      const original = await author.inputValue();
      for (const mode of ['required', 'optional', 'hidden']) {
        await author.selectOption(mode);
        const counts = Object.fromEntries([...initial.matchAll(/(\d+) (required|optional|hidden)/g)].map(match => [match[2], Number(match[1])]));
        counts[original]--; counts[mode]++;
        assert.equal(await summary.innerText(), `${counts.required} required · ${counts.optional} optional · ${counts.hidden} hidden`);
      }
      await author.selectOption(original);
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Restoring requirement modes clears the draft');
      if (scope === '2') {
        const custom = first.locator('[data-custom-rule-key="i390_note"] select');
        const previous = await custom.inputValue();
        await custom.selectOption(previous === 'required' ? 'hidden' : 'required');
        assert.notEqual(await summary.innerText(), initial, 'Enabled custom requirements contribute to summaries');
        await custom.selectOption(previous);
        const retired = first.locator('[data-custom-rule-key="i390_retired"]');
        assert.match(await retired.innerText(), /Disabled field/);
        assert.equal(await retired.locator('input').inputValue(), 'Retained historical label');
        await retired.locator('select').selectOption('hidden');
        assert.equal(await summary.innerText(), initial, 'Disabled rules remain editable but do not enter counts');
        await retired.locator('select').selectOption('required');
      }
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
      await evidence(t, t.id + '-expanded', 2);
      await toggle(first).press('Enter');
      await t.page.keyboard.press('Tab');
      assert.equal(await t.page.evaluate(() => Boolean(document.activeElement.closest('.settings-rule-details'))), false,
        'Collapsed controls are skipped by native Tab navigation');
      await t.page.reload();
      await t.page.waitForFunction(() => document.querySelector('#settings-version').value && !document.querySelector('#settings-refresh').disabled);
      await initialCards(t.page, before);
      if (scope === '2') {
        const fieldCount = await t.page.locator('#additional-fields-editor [data-domain-row]').count();
        await toggle(first).click();
        await first.locator('[data-rule-property="messageBehavior"]').selectOption('message');
        await first.locator('[data-rule-property="message"]').fill('Incomplete hidden rule witness');
        await toggle(first).click();
        const missing = first.locator('[data-custom-rule-key="i390_retired"] select');
        await missing.evaluate(node => node.remove());
        await t.page.locator('#add-custom-field').click();
        assert.equal(await missing.count(), 0, 'Redraw cannot repair an incomplete hidden control with a default');
        assert.equal(await t.page.locator('#additional-fields-editor [data-domain-row]').count(), fieldCount);
        await t.page.locator('#settings-save').click();
        await t.page.waitForFunction(() => document.querySelector('#settings-message').classList.contains('error'));
        assert.match(await t.page.locator('#settings-message').innerText(), /incomplete/i);
      }
      await unchanged(t, scope, before);
    } },
  { id: 'i390-layout-' + scope, writes: false,
    requirements: ['#390: two columns at available editor width, one in a narrow container/mobile/native 200%, touch targets, forced colors, focus, axe and no overflow'],
    run: async t => {
      await t.page.setViewportSize({ width: 1440, height: 1000 });
      const before = await openSettings(t, scope);
      await initialCards(t.page, before);
      await evidence(t, t.id + '-collapsed', 2);
      await toggle(cards(t.page).first()).click();
      await evidence(t, t.id + '-desktop', 2);
      const editor = t.page.locator('#format-rules-editor');
      await editor.evaluate(node => { node.style.maxWidth = '540px'; });
      await evidence(t, t.id + '-narrow-container', 1);
      await editor.evaluate(node => node.style.removeProperty('max-width'));
      await t.page.setViewportSize({ width: 390, height: 844 });
      await evidence(t, t.id + '-mobile', 1);
      await t.page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
      const button = toggle(cards(t.page).first());
      await button.focus();
      await t.page.keyboard.press('Tab'); await t.page.keyboard.press('Shift+Tab');
      assert.equal(await button.evaluate(node => getComputedStyle(node).outlineStyle !== 'none'), true);
      await evidence(t, t.id + '-forced-colors', 1);
      await t.page.emulateMedia({ forcedColors: 'none', reducedMotion: 'no-preference' });
      await t.page.setViewportSize({ width: 1440, height: 1000 });
      await nativeZoom(t.context, t.page, 2);
      await evidence(t, t.id + '-native-200', 1, true);
      await button.click();
      await evidence(t, t.id + '-native-200-collapsed', 1, true);
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
      await unchanged(t, scope, before);
    } }
]);

module.exports = { id: 'i390', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: false, scenarios,
  human: [['human-i390-accessibility', '#398: screen reader announcements and final human visual acceptance']],
  uncovered: [['i390-persistence', '#390: persisted Save, Discard, inherited resets and patron requirements',
    'Draft-only suite preserves runner authorization. Actual persistence/scope/patron behavior remains in retained SQL/browser journeys; selected pr410 also exercises global Discard and approved Reset.']] };
