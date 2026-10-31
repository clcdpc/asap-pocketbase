'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const { providerGeometry, chipGeometry } = require('../helpers/workflow-settings.cjs');
const { json } = require('../helpers/assertions.cjs');

async function openSettings(t, scope = 'system') {
  await t.page.goto(t.origin + '/staff/?stage=settings&settingsScope=' + scope + '#settings-workflow');
  await t.page.waitForFunction(scope => document.querySelector('#settings-version').value &&
    document.querySelector('#settings-scope').value === scope && !document.querySelector('#settings-form').inert &&
    !document.querySelector('#settings-refresh').disabled, scope);
  await t.page.locator('#settings-nav-workflow').click();
}
const selected = page => page.locator('#patron-codes-editor [data-code-id]').evaluateAll(chips => chips.map(chip => chip.dataset.codeId));
const picker = page => page.getByRole('combobox', { name: 'Allowed patron-code IDs', exact: true });
async function addCode(page, term) {
  await picker(page).fill(term);
  await picker(page).press('ArrowDown');
  await picker(page).press('Enter');
}
async function evidence(t, name, target) {
  if (target) await t.page.locator(target).evaluate(node => node.scrollIntoView({ block: 'start' }));
  const geometry = { providers: await providerGeometry(t.page), chips: await chipGeometry(t.page) };
  const cdp = await t.context.newCDPSession(t.page);
  try { geometry.metrics = await cdp.send('Page.getLayoutMetrics'); }
  finally { await cdp.detach(); }
  await fs.writeFile(path.join(t.directory, name + '-geometry.json'), JSON.stringify(geometry, null, 2));
  geometry.nativeViewportCapture = geometry.metrics.visualViewport.zoom === 2;
  await checkpoint(t.page, t.directory, name, { nativeViewport: geometry.nativeViewportCapture });
  return geometry;
}
async function unchanged(t, scope, before) {
  const after = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=' + scope);
  assert.equal(after.version, before.version, 'Browser drafts leave the authoritative SQL version unchanged');
  assert.deepEqual(after.stored, before.stored, 'Browser drafts preserve all stored Settings');
  assert.equal(t.mutations.length, 0);
}
async function providerKeyboard(page) {
  const row = page.locator('.settings-provider-row').first();
  await row.getByLabel('URL template', { exact: true }).focus();
  await page.keyboard.press('Tab');
  assert.equal(await row.getByLabel('Enabled', { exact: true }).evaluate(node => node === document.activeElement), true);
  await page.keyboard.press('Space'); await page.keyboard.press('Space');
  if (await row.locator('.settings-domain-override-toggle').count()) await page.keyboard.press('Tab');
  for (const name of ['Move down', 'Delete']) {
    await page.keyboard.press('Tab');
    assert.equal(await row.getByRole('button', { name, exact: true }).evaluate(node => {
      const style = getComputedStyle(node), r = node.getBoundingClientRect();
      return node === document.activeElement && r.left >= 0 && r.right <= innerWidth &&
        r.top >= 0 && r.bottom <= innerHeight && style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
    }), true, name + ' is keyboard reachable with visible focus');
  }
}

const scenarios = [
  { id: 's1-picker-draft', requirements: ['#392: grouping, saved/unknown IDs, description/ID search, keyboard, counts, no transient dirty state or duplicate IDs'], writes: false,
    run: async t => {
      await openSettings(t);
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=system');
      assert.deepEqual(await selected(t.page), ['1', '77']);
      await t.page.getByRole('button', { name: 'Remove Unavailable code (77)', exact: true }).waitFor();
      assert.equal(await t.page.locator('#patron-code-eligibility').count(), 1);
      assert.equal(await t.page.locator('#common-creators-editor').locator('..').locator('[id^="patron-code"]').count(), 0);
      assert.equal(await t.page.locator('#add-patron-code, #patron-codes-select-all, #patron-codes-clear-all, #patron-code-search').count(), 0);
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
      await picker(t.page).fill('branch');
      assert.deepEqual(await t.page.locator('#patron-code-options').getByRole('option').allTextContents(), ['Branch use card (17)']);
      await picker(t.page).press('ArrowDown');
      assert.equal(await picker(t.page).getAttribute('aria-activedescendant'), 'patron-code-option-17');
      await evidence(t, t.id + '-popup');
      await picker(t.page).press('Escape');
      assert.equal(await picker(t.page).getAttribute('aria-expanded'), 'false');
      await picker(t.page).fill('27'); await picker(t.page).press('Tab');
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Search, highlight and Tab never mark Settings dirty');
      await addCode(t.page, 'branch');
      assert.deepEqual(await selected(t.page), ['1', '77', '17']);
      assert.equal(await picker(t.page).inputValue(), '');
      assert.equal(await t.page.locator('#settings-save').isEnabled(), true);
      await picker(t.page).fill('17');
      assert.equal(await t.page.locator('#patron-code-options').getByRole('option').count(), 0, 'Selected IDs disappear from choices');
      await picker(t.page).press('Enter');
      await addCode(t.page, '27');
      assert.equal(await t.page.locator('#patron-code-count').innerText(), '4 patron codes selected.');
      const remove = t.page.getByRole('button', { name: 'Remove Branch use card (17)', exact: true });
      await remove.focus(); await t.page.keyboard.press('Enter');
      assert.equal(await t.page.getByRole('button', { name: 'Remove Consortium eCard (27)', exact: true }).evaluate(n => n === document.activeElement), true);
      await t.page.getByRole('button', { name: 'Remove Consortium eCard (27)', exact: true }).press('Enter');
      assert.deepEqual(await selected(t.page), ['1', '77']);
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Returning to the original IDs clears the draft');
      await unchanged(t, 'system', before);
    } },
  { id: 's1-inheritance-draft', requirements: ['#392: inherited chips disabled, explicit empty library draft, reset to system and restored override draft'], writes: false,
    run: async t => {
      await openSettings(t, '2');
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=2');
      const inherit = t.page.locator('#patron-codes-use-system');
      assert.equal(await inherit.isChecked(), true);
      assert.equal(await picker(t.page).isDisabled(), true);
      assert.equal(await t.page.locator('#patron-codes-editor button:enabled').count(), 0);
      assert.deepEqual(await selected(t.page), ['1', '77']);
      await evidence(t, t.id + '-inherited', '#patron-code-eligibility');
      await inherit.uncheck();
      while (await t.page.locator('#patron-codes-editor button').count()) await t.page.locator('#patron-codes-editor button').first().click();
      assert.equal(await t.page.locator('#patron-code-count').innerText(), '0 patron codes selected.');
      assert.equal(await inherit.isChecked(), false);
      assert.equal(await picker(t.page).evaluate(n => n === document.activeElement), true);
      await inherit.check();
      assert.deepEqual(await selected(t.page), ['1', '77']);
      await inherit.uncheck();
      assert.deepEqual(await selected(t.page), [], 'Empty library draft survives an inheritance toggle');
      await addCode(t.page, '17');
      assert.equal(await t.page.locator('#patron-code-count').innerText(), '1 patron code selected.');
      await unchanged(t, '2', before);
    } },
  { id: 's1-catalog-unavailable', requirements: ['#392: failed GET preserves IDs, meaningful retry, successful lookup preserves an unrelated draft'], writes: false,
    run: async t => {
      const failLookup = route => route.abort('failed');
      await t.page.route('**/api/asap/staff/polaris/patron-codes?*', failLookup);
      await openSettings(t);
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=system');
      assert.deepEqual(await selected(t.page), ['1', '77']);
      assert.equal(await picker(t.page).isDisabled(), true);
      assert.match(await t.page.locator('#patron-code-availability').innerText(), /unavailable.*preserved/);
      assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
      await t.page.locator('#patron-code-eligibility-message').fill('Unsaved message survives catalog retry');
      await t.page.unroute('**/api/asap/staff/polaris/patron-codes?*', failLookup);
      await t.page.getByRole('button', { name: 'Retry patron-code lookup', exact: true }).click();
      await picker(t.page).waitFor({ state: 'visible' });
      await t.page.locator('#patron-code-retry').waitFor({ state: 'hidden' });
      assert.equal(await picker(t.page).isEnabled(), true);
      assert.deepEqual(await selected(t.page), ['1', '77']);
      assert.equal(await t.page.locator('#patron-code-eligibility-message').inputValue(), 'Unsaved message survives catalog retry');
      await evidence(t, t.id);
      await unchanged(t, 'system', before);
    } },
  { id: 's1-provider-draft', requirements: ['#394: zero/one/multiple providers, persistent help, long URL, stable identities, add/edit/toggle/move/delete with no SQL writes'], writes: false,
    run: async t => {
      await openSettings(t);
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=system');
      const rows = t.page.locator('#external-search-provider-editor .settings-provider-row');
      assert.equal(await rows.count(), 4);
      const identities = await rows.evaluateAll(nodes => nodes.map(n => [n.dataset.providerId, n.dataset.providerKey]));
      await evidence(t, t.id + '-four', '#external-search-provider-help');
      const longUrl = 'https://example.com/search?q={{title}}&context=' + 'long-value-'.repeat(50);
      await rows.first().getByLabel('Label', { exact: true }).fill('Draft provider');
      await rows.first().getByLabel('URL template', { exact: true }).fill(longUrl);
      await rows.first().getByLabel('Enabled', { exact: true }).uncheck();
      await rows.first().getByRole('button', { name: 'Move down', exact: true }).click();
      assert.deepEqual(await rows.nth(1).evaluate(n => [n.dataset.providerId, n.dataset.providerKey]), identities[0]);
      assert.equal(await rows.nth(1).getByLabel('URL template', { exact: true }).inputValue(), longUrl);
      while (await rows.count() > 1) await rows.last().getByRole('button', { name: 'Delete', exact: true }).click();
      await evidence(t, t.id + '-one', '#external-search-provider-help');
      await rows.first().getByRole('button', { name: 'Delete', exact: true }).click();
      const help = t.page.locator('#external-search-provider-help');
      await help.scrollIntoViewIfNeeded();
      assert.match(await help.innerText(), /URL templates: use \{\{title\}\}/);
      assert.match(await help.innerText(), /https:\/\/example.com\/search\?q=\{\{title\}\}/);
      assert.equal(await help.isVisible(), true);
      await evidence(t, t.id + '-empty', '#external-search-provider-help');
      await t.page.locator('#add-provider').click();
      assert.equal(await rows.count(), 1);
      assert.equal(await rows.first().getByLabel('Enabled', { exact: true }).isChecked(), false);
      const addedKey = await rows.first().getAttribute('data-provider-key');
      assert.ok(!identities.some(([, key]) => key === addedKey), 'Deleted saved provider keys are not reused');
      await unchanged(t, 'system', before);
    } },
  ...['system', '2'].map(scope => ({ id: 's1-layout-' + scope,
    requirements: ['#392; #394: actual pane width, desktop/mobile/native 200%, inherited controls, keyboard focus and axe'], writes: false,
    run: async t => {
      await openSettings(t, scope);
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=' + scope);
      for (const width of [1440, 1000, 390]) {
        await t.page.setViewportSize({ width, height: 1000 });
        await t.page.locator('#external-search-provider-help').evaluate(node => node.scrollIntoView({ block: 'start' }));
        const geometry = await evidence(t, t.id + '-' + width);
        assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Layout creates no draft');
        if (width === 1440) assert.ok(geometry.providers.paneWidth >= (scope === 'system' ? 720 : 820), 'Wide pane exercises single-row layout');
        if (scope === '2') {
          const override = t.page.locator('.settings-provider-row .settings-domain-override-toggle').first();
          await override.check();
          assert.equal(await t.page.locator('.settings-provider-row input[type="url"]').first().isEnabled(), true);
          await evidence(t, t.id + '-' + width + '-override');
          await providerKeyboard(t.page);
          await override.uncheck();
        } else await providerKeyboard(t.page);
        assert.equal(await t.page.locator('#settings-save').isDisabled(), true, 'Keyboard review returns provider values to baseline');
      }
      await t.page.setViewportSize({ width: 1440, height: 1000 });
      await nativeZoom(t.context, t.page, 2);
      {
        await t.page.locator('#patron-code-eligibility').evaluate(node => node.scrollIntoView({ block: 'start' }));
        if (scope === '2') await t.page.locator('#patron-codes-use-system').uncheck();
        await picker(t.page).fill('branch'); await picker(t.page).press('ArrowDown');
        await evidence(t, t.id + '-native-200-picker');
        await picker(t.page).press('Escape');
        assert.equal(await picker(t.page).evaluate(n => n === document.activeElement), true);
        await t.page.locator('#external-search-provider-help').evaluate(node => node.scrollIntoView({ block: 'start' }));
        const geometry = await evidence(t, t.id + '-native-200-providers');
        assert.equal(geometry.metrics.visualViewport.zoom, 2);
        const override = t.page.locator('.settings-provider-row .settings-domain-override-toggle').first();
        if (scope === '2') await override.check();
        await providerKeyboard(t.page);
        if (scope === '2') await override.uncheck();
      } // Zoom is per-tab; the runner closes this page after the scenario.
      await unchanged(t, scope, before);
    } }))
];

module.exports = { id: 's1', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: false, scenarios,
  human: [['human-s1-accessibility', '#398: screen reader announcements, forced colors and visual acceptance']],
  uncovered: [['s1-persistence', '#392; #394: actual Settings Save, Discard, scope/version races and patron eligibility',
    'This suite exercises browser drafts only and cannot submit Settings mutations. Persisted save/empty override/null reset, concurrency and patron authorization remain in retained real-SQL integration/browser tests.']] };
