'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { checkpoint, nativeZoom, visibleEvidence } = require('../helpers/accessibility.cjs');
const { decideConfirmation } = require('../../browser/confirmation.cjs');
const { runLibraryFormatRuleDrafts } = require('../../browser/settings-format-rule-drafts.cjs');
const { verifyInheritanceInteractions } = require('../../browser/settings-inheritance-interactions.cjs');
const { verifySettingsDraftBlockers } = require('../../browser/settings-draft-blockers.cjs');

const settingsPath = '/api/asap/staff/settings';
const isSave = response => response.request().method() === 'POST' && new URL(response.url()).pathname === settingsPath;
async function ready(page, scope) {
  await page.waitForFunction(scope => document.getElementById('settings-scope').value === scope &&
    document.getElementById('settings-version').value && !document.getElementById('settings-form').inert &&
    !document.getElementById('settings-refresh').disabled, scope);
}
async function open(t, scope, section = 'patron') {
  await t.page.goto(`${t.origin}/staff/?stage=settings&settingsScope=${scope}#settings-${section}`);
  await ready(t.page, scope);
  await t.page.locator(`#settings-nav-${section}`).click();
  return read(t, scope);
}
async function api(t, method, url, data, status = 200) {
  // Page fetch uses the ordinary session/antiforgery and the runner's request
  // guard, including read-only mode. Do not install native-harness identities.
  const response = await t.page.evaluate(async ({ method, url, data }) => {
    const session = await (await fetch('/api/asap/staff/session')).json();
    const response = await fetch(url, { method, headers: { 'Content-Type': 'application/json',
      'X-ASAP-Antiforgery': session.antiforgeryToken }, ...(data === undefined ? {} : { body: JSON.stringify(data) }) });
    return { status: response.status, text: await response.text() };
  }, { method, url, data });
  assert.equal(response.status, status, `Expected ${method} ${url} status`);
  return JSON.parse(response.text);
}
const read = (t, scope) => api(t, 'GET', `${settingsPath}?orgId=${scope}`);
async function post(t, scope, values) {
  const before = await read(t, scope);
  return api(t, 'POST', settingsPath, { orgId: scope, version: before.version, ...values });
}
async function save(t) {
  const response = t.page.waitForResponse(isSave);
  await t.page.locator('#settings-save').click();
  assert.equal((await response).status(), 200, 'The real Settings transaction succeeds');
  await t.page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' &&
    !document.getElementById('settings-form').inert);
}
async function discard(t) {
  await decideConfirmation(t.page, () => t.page.locator('#settings-discard').click(), dialog => dialog.accept());
  await t.page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' &&
    !document.getElementById('settings-form').inert);
}
async function sql(t, scope) {
  const row = (await t.verify()).evidence.settings.find(row => row.organizationId === scope);
  assert.ok(row, 'The independently seeded SQL evidence scope exists');
  return row;
}
const writeEvidence = (t, value) => fs.writeFile(path.join(t.directory, t.id + '-state.json'), JSON.stringify(value, null, 2));
async function hiddenIdentities(t) {
  for (const selector of ['#material-formats-editor', '#publication-options-editor', '#additional-fields-editor', '#email-templates-editor']) {
    const labels = await t.page.locator(`${selector} label`).allTextContents();
    assert.ok(labels.every(label => !/^(?:Code|Stable ID|Stable key|Option stable ID|Template key)\s*$/.test(label.trim())),
      'Normal editors omit internal identity controls');
  }
}

async function ruleAvailability(t, scope) {
  const before = await open(t, scope), original = before.stored.configuredSystem.formats.find(row => row.code === 'book');
  const cards = t.page.locator('#format-rules-editor [data-rule-code]'), add = t.page.locator('#add-format-rule');
  assert.equal(await add.isDisabled(), true);
  await t.page.getByText('All available formats already have rules.', { exact: true }).waitFor();
  const count = await cards.count();
  // Locate by retained identity; readable labels remain the accessible UI.
  const first = t.page.locator('#format-rules-editor [data-rule-code="book"]');
  await first.locator('[data-rule-toggle]').click();
  await first.locator('[data-format-label="author"]').fill('Retained Add rule draft');
  const otherCode = await cards.nth(1).getAttribute('data-rule-code');
  await cards.nth(1).locator('[data-row-action="delete"]').click();
  assert.equal(await add.isEnabled(), true); assert.equal(await cards.count(), count - 1);
  const writes = t.mutations.length;
  await add.press('Enter');
  const restored = t.page.locator(`#format-rules-editor [data-rule-code="${otherCode}"]`);
  assert.equal(await restored.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');
  assert.equal(await restored.locator('[data-rule-toggle]').evaluate(node => node === document.activeElement), true);
  assert.equal(await t.page.locator('#settings-save').isEnabled(), true);
  await add.evaluate(node => { node.click(); node.click(); });
  assert.equal(await cards.count(), count); assert.equal(await add.isDisabled(), true);
  assert.equal(await first.locator('[data-format-label="author"]').inputValue(), 'Retained Add rule draft');
  assert.equal(t.mutations.length, writes, 'Add, removal, re-addition and repeated activation never save implicitly');
  await hiddenIdentities(t); await checkpoint(t.page, t.directory, t.id + '-draft');
  await save(t); await open(t, scope);
  assert.equal((await read(t, scope)).effective.formats.find(row => row.code === 'book').author.label, 'Retained Add rule draft');
  if (scope === 'system') await post(t, scope, { formatRules: [{ code: 'book', author: { label: original.authorLabel } }] });
  await writeEvidence(t, { scope, ruleCount: count, noImplicitWrites: true, savedAndReloaded: true });
}

async function compactRows(t, scope) {
  const before = await open(t, scope, 'workflow'), original = before.stored.configuredSystem;
  const creators = ['First synthetic creator', 'Long synthetic creator '.repeat(8)];
  await post(t, scope, { workflow: { commonAuthorsEnabled: true, commonAuthorsList: creators },
    patron: { publicationOptions: [{ id: 'i471_first', label: 'First synthetic publication', enabled: true },
      { id: 'i471_long', label: 'Long synthetic publication '.repeat(7), enabled: true }] } });
  try {
    await open(t, scope, 'workflow');
    const rows = t.page.locator('#common-creators-editor [data-domain-row]');
    assert.equal(await rows.first().locator('[data-row-action="up"]').isDisabled(), true);
    assert.equal(await rows.last().locator('[data-row-action="down"]').isDisabled(), true);
    await rows.first().locator('[data-row-action="down"]').press('Enter');
    assert.deepEqual(await rows.locator('input').evaluateAll(nodes => nodes.map(node => node.value)), [...creators].reverse());
    assert.equal(await rows.last().locator('[data-row-action="up"]').evaluate(node => node === document.activeElement), true);
    await t.page.locator('#add-common-creator').click();
    assert.equal(await rows.last().locator('input').evaluate(node => node === document.activeElement), true);
    await rows.last().locator('input').fill('Remove this draft');
    await rows.last().locator('[data-row-action="delete"]').press('Enter');
    await save(t); await open(t, scope, 'workflow');
    const accepted = await read(t, scope), writes = t.mutations.length;
    await rows.first().locator('input').fill('Discard this draft');
    await decideConfirmation(t.page, () => t.page.locator('#settings-discard').click(), dialog => dialog.cancel());
    assert.equal(await rows.first().locator('input').inputValue(), 'Discard this draft');
    await discard(t);
    assert.deepEqual((await read(t, scope)).stored, accepted.stored); assert.equal(t.mutations.length, writes);
    await t.page.locator('#settings-nav-patron').click();
    const pubs = t.page.locator('#publication-options-editor [data-domain-row]');
    await pubs.last().locator('[data-row-action="up"]').press('Enter');
    assert.equal(await t.page.evaluate(() => document.activeElement.closest('[data-domain-row]')?.dataset.optionId), 'i471_long');
    await pubs.first().locator('[data-publication-field="label"]').fill('Renamed synthetic publication');
    await save(t); await open(t, scope);
    assert.deepEqual((await read(t, scope)).effective.publicationOptions.map(row => row.id), ['i471_long', 'i471_first']);
    const geometry = [];
    for (const [name, width, zoom] of [['desktop', 1280, 1], ['mobile', 390, 1], ['native200', 1280, 2]]) {
      await t.page.setViewportSize({ width, height: 900 }); await nativeZoom(t.context, t.page, zoom);
      for (const [section, selector] of [['workflow', '#common-creators-editor'], ['patron', '#publication-options-editor']]) {
        await t.page.locator(`#settings-nav-${section}`).click();
        const row = t.page.locator(`${selector} [data-domain-row]`).first(), input = row.locator('input[type="text"]');
        await input.focus(); await input.press('End'); await visibleEvidence(input);
        const measured = await row.evaluate(node => {
          const rect = n => { const r = n.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; };
          return { row: rect(node), input: rect(node.querySelector('input[type="text"]')),
            actions: rect(node.querySelector('.settings-row-actions')), parts: [...node.querySelectorAll('input, button')].map(rect) };
        });
        for (const part of measured.parts) assert.ok(part.x >= measured.row.x - 1 && part.right <= measured.row.right + 1, 'Row controls fit without clipping');
        if (name === 'desktop') assert.ok(measured.actions.y < measured.input.bottom && measured.actions.bottom > measured.input.y, 'Desktop actions share the input row');
        assert.equal(await input.evaluate(node => { const style = getComputedStyle(node); return node === document.activeElement && (style.outlineStyle !== 'none' || style.boxShadow !== 'none'); }), true);
        geometry.push({ name, section, ...measured });
        await checkpoint(t.page, t.directory, `${t.id}-${section}-${name}`, { nativeViewport: zoom === 2 });
      }
    }
    await writeEvidence(t, { scope, geometry, saveReload: true, cancelDiscardNoWrites: true });
  } finally {
    await nativeZoom(t.context, t.page, 1); await t.page.setViewportSize({ width: 1280, height: 900 });
    if (scope === 'system') await post(t, scope, { workflow: { commonAuthorsEnabled: original.workflow.commonAuthorsEnabled,
      commonAuthorsList: original.commonCreators.values.map(row => typeof row === 'string' ? row : row.value) },
      patron: { publicationOptions: original.publicationOptions.values } });
  }
}

const scenarios = [
  { id: 'i471-identities-and-recovery', writes: true, requirements: ['#461: all five generated identities, references, rename/reorder/save/reload, retained SQL history', '#463: combined format-plus-rule transaction and truthful rejected/committed-response-loss recovery'], run: async t => {
    const scope = '47101', before = await open(t, scope), evidence = await sql(t, scope), report = {};
    assert.equal(evidence.requests, 1); assert.equal(evidence.events, 1);
    await hiddenIdentities(t);
    const code = before.stored.customFields[0].key && before.effective.formats.find(row => row.ownerOrganizationId === scope).code;
    await runLibraryFormatRuleDrafts(t.page, { scopeId: scope, formatCode: code, beforeCommit: t.verify }, report, scope => ready(t.page, scope));
    const after = await sql(t, scope);
    assert.equal(after.audits, evidence.audits + 1, 'Two rejected transactions produce no successful audit; the lost response committed exactly once');
    for (const name of ['requestsDigest', 'historyDigest', 'claimsDigest']) assert.equal(after[name], evidence[name], `Settings preserve exact accepted ${name}`);
    await checkpoint(t.page, t.directory, t.id);
    // The shared native journey retains payloads in memory for assertions. Export
    // only fixed identity/recovery witnesses, never the unrestricted document.
    await writeEvidence(t, { identities: report.formatRuleDrafts, sqlBefore: evidence, sqlAfter: after,
      rejectedStatuses: report.formatRuleDraftSaveRecovery.rejected.map(row => row.status),
      committedResponseStatus: report.formatRuleDraftSaveRecovery.committedResponse.status, guardedReloadReconciled: true });
  } },
  ...['system', '47106'].map(scope => ({ id: `i471-add-rule-${scope}`, writes: true,
    requirements: ['#462: exhausted, available, repeated activation, removal/re-addition, exact identity/focus/dirty and persisted System/library results'], run: t => ruleAvailability(t, scope) })),
  { id: 'i471-empty-format-prerequisite', writes: false, requirements: ['#462: explicit empty-catalog prerequisite with zero mutations (injected GET projection; SQL remains authoritative and unchanged)'], run: async t => {
    const before = await open(t, 'system');
    const empty = async route => {
      const response = await route.fetch(), data = await response.json();
      data.effective.formats = []; data.stored.configuredSystem.formats = []; data.stored.formatRules = [];
      await route.fulfill({ response, json: data });
    };
    await t.page.route('**/api/asap/staff/settings?orgId=system', empty);
    try {
      await t.page.reload(); await ready(t.page, 'system'); await t.page.locator('#settings-nav-patron').click();
      assert.equal(await t.page.locator('#add-format-rule').isDisabled(), true);
      await t.page.getByText('Add a material format before adding a rule.', { exact: true }).waitFor();
      await t.page.locator('#add-format-rule').evaluate(node => { node.click(); node.click(); });
      assert.equal(await t.page.locator('#format-rules-editor [data-rule-code]').count(), 0);
      assert.deepEqual(t.mutations, []); await checkpoint(t.page, t.directory, t.id);
    } finally { await t.page.unroute('**/api/asap/staff/settings?orgId=system', empty); }
    assert.deepEqual((await read(t, 'system')).stored, before.stored);
  } },
  { id: 'i471-inheritance-persistence', writes: true, requirements: ['#463: equal pins, false/zero/blanks/empty lists, per-field/template/list/item resets, future System changes, owned preservation'], run: async t => {
    const scope = '47102', initial = await open(t, scope), system = initial.stored.configuredSystem;
    const originalLimit = system.workflow.suggestionLimit, book = system.formats.find(row => row.code === 'book');
    const owned = { formats: initial.stored.libraryOverride.formats, fields: initial.stored.customFields,
      templates: initial.stored.templates.filter(row => row.isCustom) };
    const raw = '  Meaningful library blank follows: \r\n  ';
    await post(t, scope, { workflow: { suggestionLimit: originalLimit, commonAuthorsEnabled: false, commonAuthorsList: [], allowedPatronCodeIds: [] },
      patron: { successMessage: raw, publicationOptions: [] },
      formats: [{ id: book.id, version: book.version, pinOverrides: true, sortOrder: 0, isEnabled: false, message: '' }],
      templates: [{ templateKey: 'suggestion_submitted', subject: 'Pinned subject', body: raw }] });
    try {
      await open(t, scope, 'workflow'); const pinned = await read(t, scope), library = pinned.stored.libraryOverride;
      assert.equal(library.workflow.commonAuthorsEnabled, false);
      const format = library.formats.find(row => row.materialFormatId === book.id);
      assert.equal(format.sortOrder, 0); assert.equal(format.isEnabled, false); assert.equal(format.message, '');
      assert.equal(library.patron.successMessage, raw);
      for (const name of ['commonCreators', 'publicationOptions', 'allowedPatronCodeIds']) {
        assert.equal(library[name].exists, true); assert.deepEqual(library[name].values, []);
      }
      const limit = t.page.locator('#suggestion-limit'), field = t.page.locator('[data-setting-key="suggestionLimit"]');
      assert.match(await field.textContent(), /Library setting.*Matches system/);
      const writes = t.mutations.length; await limit.focus(); assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
      await field.locator('.settings-use-system').press('Enter');
      assert.equal(await limit.inputValue(), String(originalLimit)); assert.equal(await t.page.locator('#settings-save').isEnabled(), true);
      await discard(t); assert.equal(t.mutations.length, writes); assert.match(await field.textContent(), /Matches system/);
      await post(t, 'system', { workflow: { suggestionLimit: originalLimit + 1 } });
      await open(t, scope, 'workflow'); assert.equal(await limit.inputValue(), String(originalLimit), 'Equal-value pin survives a later System change');
      await field.locator('.settings-use-system').press('Enter'); await save(t);
      await post(t, 'system', { workflow: { suggestionLimit: originalLimit + 2 } });
      await open(t, scope, 'workflow'); assert.equal(await limit.inputValue(), String(originalLimit + 2));
      for (const selector of ['#common-creators-inherit-field', '#patron-codes-inherit-field']) {
        await decideConfirmation(t.page, () => t.page.locator(selector + ' .settings-use-system').click(), dialog => dialog.accept());
      }
      await t.page.locator('#settings-nav-patron').click();
      await decideConfirmation(t.page, () => t.page.locator('#publication-options-inherit-field .settings-use-system').click(), dialog => dialog.accept());
      await t.page.locator(`#material-formats-editor [data-format-code="book"] > .settings-inheritance .settings-use-system`).click();
      await t.page.locator('#settings-nav-templates').click();
      const template = t.page.locator('[data-template-key="suggestion_submitted"]');
      if (await template.locator('[data-template-toggle]').getAttribute('aria-expanded') !== 'true') await template.locator('[data-template-toggle]').click();
      await template.locator('[data-template-status="body"] .settings-use-system').click();
      await save(t); const reset = await read(t, scope), resetRaw = reset.stored.libraryOverride;
      assert.equal(resetRaw.workflow.suggestionLimit, null);
      for (const name of ['commonCreators', 'publicationOptions', 'allowedPatronCodeIds']) assert.equal(resetRaw[name].exists, false);
      assert.equal(resetRaw.formats.some(row => row.kind === 'systemOverride'), false);
      const lineage = reset.stored.templates.find(row => row.templateKey === 'suggestion_submitted' && row.organizationId === scope);
      assert.equal(lineage.body, null); assert.equal(lineage.subject, 'Pinned subject');
      assert.deepEqual(resetRaw.formats, owned.formats); assert.deepEqual(reset.stored.customFields, owned.fields);
      assert.deepEqual(reset.stored.templates.filter(row => row.isCustom), owned.templates);
      await checkpoint(t.page, t.directory, t.id);
      await writeEvidence(t, { equalPin: true, followsAfterExplicitReset: true, falseZeroBlankEmpty: true,
        ownedIdsPreserved: true, templateBodyResetSubjectRetained: true, sql: await sql(t, scope) });
    } finally { await post(t, 'system', { workflow: { suggestionLimit: originalLimit } }); }
  } },
  { id: 'i471-inheritance-interactions', writes: true, requirements: ['#463: expanded scalar/template comparisons stay open, direct tools/reset focus, mobile Cancel/Discard with no library writes'], run: async t => {
    await open(t, '47107'); const report = {};
    await verifyInheritanceInteractions(t.page, { scopeId: '47107', formatCode: 'i471' }, report,
      scope => ready(t.page, scope), name => checkpoint(t.page, t.directory, t.id + '-' + name));
    const witness = report.inheritanceInteractionWitness;
    await writeEvidence(t, { comparisons: witness.comparisons.map(row => ({ name: row.name, open: row.open, focused: row.focused })),
      firstDiscardConfirmed: witness.firstDiscardConfirmed, cancelPreservedAndAcceptDiscardedBoth: witness.cancelPreservedAndAcceptDiscardedBoth,
      libraryPosts: witness.libraryPosts.length });
  } },
  ...['system', '47105'].map(scope => ({ id: `i471-compact-rows-${scope}`, writes: true,
    requirements: ['#461/#465: creator/publication edit/reorder/delete, Save/Cancel/Discard, focus, desktop/mobile/native200 geometry'], run: t => compactRows(t, scope) })),
  { id: 'i471-draft-blockers', writes: true, requirements: ['#466: multiple blockers, exact destinations, preserved drafts, zero navigation writes, resolution/retry and stale/retired owners'], run: async t => {
    await open(t, '47103'); const report = {};
    await verifySettingsDraftBlockers(t.page, { scopeId: '47103', formatCode: 'i471', baseOrigin: t.origin,
      beforeCommit: t.verify, verifyNative200: async (_args, staffId, witness) => {
        for (const width of [1280, 1000]) {
          await t.page.setViewportSize({ width, height: 900 }); await nativeZoom(t.context, t.page, 2);
          try {
            await open(t, '47103', 'staff');
            const row = t.page.locator(`.settings-staff-row[data-staff-id="${staffId}"]`);
            await row.locator('.settings-staff-summary').click(); await row.getByLabel(/^Display name/).fill('Synthetic native draft');
            await row.getByLabel(/^Display name/).press('Escape'); await t.page.locator('#settings-nav-workflow').click();
            await t.page.locator('#suggestion-limit-message').fill('Synthetic native Workflow'); await t.page.locator('#settings-save').click();
            const link = t.page.locator('#settings-draft-blockers button'); await link.waitFor(); const count = t.mutations.length;
            await link.press('Enter'); const email = row.getByLabel(/^Email address/);
            assert.equal(await email.evaluate(node => node === document.activeElement), true); await visibleEvidence(email);
            assert.equal(t.mutations.length, count); await checkpoint(t.page, t.directory, `${t.id}-native200-${width}`, { nativeViewport: true });
            witness.native200.push({ width, exactEmailFocus: true, noNavigationWrites: true });
            await discard(t);
          } finally { await nativeZoom(t.context, t.page, 1); }
        }
      } }, report, scope => ready(t.page, scope), name => checkpoint(t.page, t.directory, t.id + '-' + name));
    const { focus, native200, writes, ...outcomes } = report.draftBlockers;
    await writeEvidence(t, { ...outcomes, focus, native200, writeCount: writes.length });
  } },
  { id: 'i471-real-conflict', writes: true, requirements: ['#463: real SQL version conflict, truthful authoritative refresh and audit state, successful explicit retry'], run: async t => {
    const scope = '47104', initial = await open(t, scope, 'workflow'), before = await sql(t, scope);
    const field = t.page.locator('#suggestion-limit-message'); await field.fill('Synthetic losing draft');
    await post(t, scope, { workflow: { suggestionLimitMessage: 'Synthetic concurrent winner' } });
    const winner = await read(t, scope), committed = await sql(t, scope);
    assert.notEqual(winner.version, initial.version); assert.equal(committed.audits, before.audits + 1);
    const response = t.page.waitForResponse(isSave); await t.page.locator('#settings-save').click();
    assert.equal((await response).status(), 409);
    await t.page.locator('#settings-message').filter({ hasText: 'These settings changed elsewhere. Review the refreshed values before saving again.' }).waitFor();
    await ready(t.page, scope);
    assert.equal(await field.inputValue(), 'Synthetic concurrent winner'); assert.equal(await t.page.locator('#settings-save').isDisabled(), true);
    assert.equal(await t.page.locator('#settings-save-title').textContent(), 'No changes');
    assert.equal((await read(t, scope)).version, winner.version); assert.equal((await sql(t, scope)).audits, committed.audits);
    await checkpoint(t.page, t.directory, t.id + '-blocked');
    await field.fill('Synthetic explicit retry'); await save(t);
    assert.equal((await read(t, scope)).workflow.suggestionLimitMessage, 'Synthetic explicit retry');
    assert.equal((await sql(t, scope)).audits, before.audits + 2);
    await writeEvidence(t, { status: 409, authoritativeRefresh: true, conflictWroteNoAudit: true, explicitRetrySaved: true });
  } }
];
module.exports = { id: 'i471', mutations: true, scenarios,
  requires: ['wslc-review-0.4.0', 'local-authentication', 'sql-server-2022', 'review-library-2', 'deterministic-providers', 'chromium-native-zoom'],
  human: [['i471-human', '#398: actual screen-reader, visual judgment and shared dark appearance acceptance']],
  uncovered: [['i471-lower-level', '#461/#463/#466: full authorization, inheritance inventory, identity/session races and collision matrices',
    'Mapped to retained frontend, native browser and real-SQL tests in tests/acceptance/i471-evidence.md; this runner is a focused representative suite.'],
  ['i471-live-host', 'Deployed host, Entra and live providers', 'Separate operational acceptance; no DEV/IIS or live-provider testing.']] };
