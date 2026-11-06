'use strict';
const assert = require('node:assert/strict');
const { decideConfirmation } = require('./confirmation.cjs');

async function runLibraryFormatRuleDrafts(page, args, report, ready) {
  const read = () => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${scope}`);
    if (!response.ok) throw new Error(`Settings GET ${response.status}`);
    return response.json();
  }, args.scopeId);
  await page.locator('#settings-nav-patron').click();
  const initial = await read();
  const originalRule = initial.stored.formatRules.find(rule => rule.code === args.formatCode);
  const savedKey = Object.keys(originalRule.customFields)[0];
  assert.equal(args.formatCode, `custom_${initial.effective.formats.length + 1}`, 'Seed the generated-code collision.');
  assert.equal(savedKey, `field_${initial.stored.customFields.length + 1}`, 'Seed the generated-key collision.');
  const existing = page.locator(`#format-rules-editor [data-rule-code="${args.formatCode}"]`);
  await existing.locator('[data-rule-toggle]').click();
  await existing.locator('[data-format-label="title"]').fill('Existing title draft preserved');
  const existingControl = await existing.locator('[data-custom-rule-property="mode"]').elementHandle();
  const historicalFormat = page.locator('#material-formats-editor [data-format-code="' + args.formatCode + '"]');
  await historicalFormat.locator('[data-material-field="label"]').fill('Renamed historical format');
  const historicalField = page.locator('#additional-fields-editor [data-field-key="' + savedKey + '"]');
  await historicalField.locator('[data-custom-field="label"]').fill('Renamed historical field');
  await historicalField.locator('[data-custom-field="enabled"]').uncheck();
  await historicalField.locator('[data-option-id="selected-choice"] [data-option-field="label"]').fill('Renamed historical selection');
  await historicalField.locator('[data-option-id="selected-choice"] [data-option-field="enabled"]').uncheck();
  const sharedFormat = page.locator('#material-formats-editor [data-format-code="book"]');

  await sharedFormat.locator('[data-material-field="label"]').fill('Historical witness library book');

  await page.locator('#add-material-format').click();
  const material = page.locator('#material-formats-editor [data-domain-row]').last();
  const generatedCode = await material.getAttribute('data-format-code');
  assert.notEqual(generatedCode.toLowerCase(), args.formatCode.toLowerCase());
  const newCode = generatedCode;
  await material.locator('[data-material-field="label"]').fill('New draft format');
  assert.equal(await existingControl.evaluate(node => node.isConnected && node.closest('[data-rule-code]').dataset.ruleCode), args.formatCode);
  await page.locator('#add-format-rule').click();
  const addedRule = page.locator(`#format-rules-editor [data-rule-code="${newCode}"]`);
  assert.equal(await addedRule.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'true');

  await page.locator('#add-custom-field').click();
  const generatedKey = await page.locator('#additional-fields-editor [data-domain-row]').last().getAttribute('data-field-key');
  const definition = page.locator('#additional-fields-editor [data-field-key="' + generatedKey + '"]');
  assert.notEqual(generatedKey.toLowerCase(), savedKey.toLowerCase());
  await definition.locator('input[type="checkbox"]').uncheck();
  assert.equal(await addedRule.locator(`[data-custom-rule-key="${generatedKey}"] .settings-custom-rule-status`).innerText(),
    'Disabled field — not shown to patrons');
  await definition.locator('input[type="checkbox"]').check();
  const newKey = generatedKey;
  await definition.locator('[data-custom-field="label"]').fill('New draft field');
  const custom = addedRule.locator(`[data-custom-rule-key="${newKey}"]`);
  await custom.locator('select').selectOption('required');
  await custom.locator('input').fill('New rule witness');
  await existingControl.dispose();

  await definition.locator('[data-custom-field="type"]').selectOption('select');
  await definition.getByRole('button', { name: 'Add option', exact: false }).click();
  await definition.locator('[data-option-field="label"]').last().fill('Same selection');
  await definition.getByRole('button', { name: 'Add option', exact: false }).click();
  await definition.locator('[data-option-field="label"]').last().fill('Same selection');
  const optionIds = await definition.locator('[data-option-row]').evaluateAll(rows => rows.map(row => row.dataset.optionId));
  assert.equal(new Set(optionIds).size, 2, 'Identical selection labels retain distinct values.');
  await historicalField.locator('[data-row-action="down"]').click();
  assert.equal(await historicalField.getAttribute('data-field-key'), savedKey, 'Reordering a renamed disabled definition retains its stored selection key.');

  assert.equal(await page.locator('#add-publication-option').isEnabled(), true);
  const publication = page.locator('#publication-options-editor [data-domain-row]');
  const existingPublicationIds = await publication.evaluateAll(rows => rows.map(row => row.dataset.optionId));
  const historicalPublication = page.locator('#publication-options-editor [data-option-id="historical-publication"]');
  await historicalPublication.locator('[data-publication-field="label"]').fill('Renamed historical publication');
  await historicalPublication.locator('[data-publication-field="enabled"]').uncheck();
  await historicalPublication.locator('[data-row-action="down"]').click();
  await page.locator('#add-publication-option').click();
  const publicationKey = await publication.last().getAttribute('data-option-id');
  await publication.last().locator('[data-publication-field="label"]').fill('Draft publication timing');
  await publication.last().locator('[data-publication-field="enabled"]').uncheck();
  await publication.last().locator('[data-row-action="up"]').press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.closest('[data-domain-row]')?.dataset.optionId), publicationKey);
  const publicationIds = await publication.evaluateAll(rows => rows.map(row => row.dataset.optionId));
  assert.ok(existingPublicationIds.every(key => publicationIds.includes(key)));

  await page.locator('#settings-nav-templates').click();
  await page.locator('#add-email-template').click();
  const namedTemplate = page.locator('#email-templates-editor [data-template-id=""]');
  const templateKey = await namedTemplate.getAttribute('data-template-key');
  await namedTemplate.locator('.template-display-name').fill('Draft rejection label');
  await namedTemplate.locator('.template-subject').fill('Draft {{title}}');
  await namedTemplate.locator('.template-body').fill('Body for {{title}}');
  assert.equal(await namedTemplate.locator('.settings-template-preview-body').textContent(), 'Body for Example title');
  assert.equal(await namedTemplate.locator('.template-placeholder-trigger').first().isEnabled(), true);

  const editorIdentities = () => page.evaluate(() => ({
    formats: [...document.querySelectorAll('#material-formats-editor [data-domain-row]')].map(row => row.dataset.formatCode).sort(),
    fields: [...document.querySelectorAll('#additional-fields-editor [data-domain-row]')].map(row => ({
      key: row.dataset.fieldKey, options: [...row.querySelectorAll('[data-option-row]')].map(option => option.dataset.optionId)
    })).sort((left, right) => left.key.localeCompare(right.key)),
    publication: [...document.querySelectorAll('#publication-options-editor [data-domain-row]')].map(row => row.dataset.optionId).sort(),
    templates: [...document.querySelectorAll('#email-templates-editor [data-domain-row]')].map(row => row.dataset.templateKey).sort(),
    rules: [...document.querySelectorAll('#format-rules-editor [data-rule-code]')].map(row => ({
      code: row.dataset.ruleCode, fields: [...row.querySelectorAll('[data-custom-rule-key]')].map(field => field.dataset.customRuleKey).sort()
    })).sort((left, right) => left.code.localeCompare(right.code))
  }));
  const generatedIdentities = await editorIdentities();
  const recovery = report.formatRuleDraftSaveRecovery = { rejected: [], attemptedPayloads: [] };
  const settingsPost = request => request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/settings';
  const interceptSave = async route => {
    if (!settingsPost(route.request())) { await route.fallback(); return; }
    recovery.attemptedPayloads.push(route.request().postDataJSON());
    if (recovery.attemptedPayloads.length < 3) { await route.fallback(); return; }
    // Forward the actual third POST and await its real committed acknowledgement.
    // Only the browser-facing response is lost; no successful commit is fabricated.
    await args.beforeCommit?.();
    const response = await route.fetch();
    recovery.committedResponse = { status: response.status(), body: await response.json() };
    await route.abort('failed');
  };
  await page.route('**/api/asap/staff/settings', interceptSave);
  let payload;
  try {
    // A required Select with no enabled choice fails the owning service's real
    // transaction after draft creation. Both rejected attempts must leave SQL
    // unchanged and retain all generated IDs for an explicit safe retry.
    await page.locator('#settings-nav-patron').click();
    for (const enabled of await definition.locator('[data-option-field="enabled"]').all()) await enabled.uncheck();
    for (let attempt = 0; attempt < 2; attempt++) {
      const rejectedResponse = page.waitForResponse(response => settingsPost(response.request()));
      await page.locator('#settings-save').click();
      const rejected = await rejectedResponse;
      const body = await rejected.text();
      recovery.rejected.push({ status: rejected.status(), body });
      assert.equal(rejected.status(), 400, body);
      assert.match(body, /required select field must have at least one enabled option/i);
      await page.waitForFunction(() => !document.getElementById('settings-form').inert &&
        !document.getElementById('settings-save').disabled);
      assert.deepEqual(await editorIdentities(), generatedIdentities, 'Rejected saves retain every generated control identity/reference.');
      const unchanged = await read();
      assert.equal(unchanged.version, initial.version, 'A rejected transaction does not advance the accepted settings version.');
      assert.deepEqual(unchanged.stored, initial.stored, 'A rejected transaction leaves all accepted SQL-backed settings unchanged.');
    }
    assert.deepEqual(recovery.attemptedPayloads[1], recovery.attemptedPayloads[0], 'The explicit failed-save retry sends the identical payload.');
    recovery.rejectedStateUnchanged = true;
    recovery.retryPayloadStable = true;

    for (const enabled of await definition.locator('[data-option-field="enabled"]').all()) await enabled.check();
    const responseLost = page.waitForEvent('requestfailed', { predicate: settingsPost });
    await page.locator('#settings-save').click();
    await responseLost;
    await page.locator('#settings-save-title').filter({ hasText: 'Outcome uncertain; reload needed' }).waitFor();
    assert.equal(recovery.committedResponse.status, 200);
    assert.equal(recovery.committedResponse.body.code, 'saved');
    const corrected = structuredClone(recovery.attemptedPayloads[0]);
    corrected.customFields.find(field => field.key === newKey).options.forEach(option => { option.enabled = true; });
    payload = recovery.attemptedPayloads[2];
    assert.deepEqual(payload, corrected, 'Only the corrected Enabled flags change; all IDs, references and remaining payload values stay stable.');
    assert.deepEqual(await editorIdentities(), generatedIdentities, 'Response loss leaves the original generated draft identities mounted.');
    const committed = await read();
    assert.equal(committed.version, recovery.committedResponse.body.data.version);
    assert.ok(committed.effective.formats.some(format => format.code === newCode && format.id));
    assert.deepEqual(committed.stored.customFields.find(field => field.key === newKey).options.map(option => option.id), optionIds);
    assert.ok(committed.stored.libraryOverride.publicationOptions.values.some(option => option.id === publicationKey));
    assert.ok(committed.stored.templates.some(template => template.templateKey === templateKey && template.isCustom));
    assert.deepEqual(committed.stored.formatRules.find(rule => rule.code === newCode).customFields[newKey],
      { mode: 'required', labelOverride: 'New rule witness' });
    recovery.committedIdentitiesVerified = true;

    assert.equal(await page.locator('#settings-form').evaluate(form => form.inert), true);
    assert.equal(await page.locator('#settings-save').isDisabled(), true);
    await page.locator('#settings-scope').selectOption('system');
    assert.equal(await page.locator('#settings-scope').inputValue(), args.scopeId, 'An uncertain save cannot transfer its draft to another scope.');
    await page.locator('#settings-form').dispatchEvent('submit');
    await page.waitForLoadState('networkidle');
    assert.equal(recovery.attemptedPayloads.length, 3, 'An uncertain committed POST is never automatically or programmatically replayed.');
    assert.deepEqual(await editorIdentities(), generatedIdentities);
    recovery.uncertainReplayBlocked = true;

    await decideConfirmation(page, () => page.locator('#settings-refresh').click(), dialog => dialog.accept());
    await page.waitForFunction(() => !document.getElementById('settings-form').inert &&
      document.getElementById('settings-save-title').textContent === 'No changes');
    assert.deepEqual(await editorIdentities(), generatedIdentities, 'Guarded Refresh reconciles to the same saved identifiers and references.');
    assert.equal(await page.locator('#settings-save').isDisabled(), true);
    assert.equal(recovery.attemptedPayloads.length, 3);
    recovery.guardedReloadReconciled = true;
  } finally {
    await page.unroute('**/api/asap/staff/settings', interceptSave);
  }

  // Creating the field and format in one POST exercises the real transaction's
  // intermediate SaveChanges, which can persist a stolen rule if collection is wrong.
  assert.ok(payload.formats.some(format => format.code === newCode && format.id === null));
  assert.ok(payload.customFields.some(field => field.key === newKey && field.id === null));
  assert.deepEqual(payload.formatRules.find(rule => rule.code === args.formatCode).customFields[savedKey],
    originalRule.customFields[savedKey]);
  assert.deepEqual(payload.formatRules.find(rule => rule.code === newCode).customFields[newKey],
    { mode: 'required', labelOverride: 'New rule witness' });
  assert.deepEqual(payload.customFields.find(field => field.key === newKey).options.map(option => option.id), optionIds);
  assert.ok(payload.patron.publicationOptions.some(option => option.id === publicationKey && !option.enabled));
  assert.ok(payload.templates.some(template => template.templateKey === templateKey && template.isCustom));
  await page.reload({ waitUntil: 'networkidle' });
  await ready(args.scopeId);
  await page.locator('#settings-nav-patron').click();
  const saved = await read();
  const savedRule = saved.stored.formatRules.find(rule => rule.code === args.formatCode);
  assert.deepEqual(savedRule.customFields, originalRule.customFields, 'Existing saved rules survive the combined SQL save.');
  assert.equal(saved.effective.formats.find(format => format.code === args.formatCode).title.label, 'Existing title draft preserved');
  assert.ok(saved.effective.formats.find(format => format.code === newCode)?.id);
  assert.ok(saved.stored.customFields.find(field => field.key === newKey)?.id);
  assert.deepEqual(saved.stored.formatRules.find(rule => rule.code === newCode).customFields,
    { [newKey]: { mode: 'required', labelOverride: 'New rule witness' } });
  await addedRule.locator('[data-rule-toggle]').click();
  assert.equal(await custom.locator('select').inputValue(), 'required');
  assert.equal(await custom.locator('input').inputValue(), 'New rule witness');
  assert.deepEqual(saved.stored.customFields.find(field => field.key === newKey).options.map(option => option.id), optionIds);
  assert.deepEqual(saved.stored.libraryOverride.publicationOptions.values.map(option => option.id), publicationIds);
  assert.equal(saved.stored.templates.find(template => template.templateKey === templateKey)?.displayName, 'Draft rejection label');
  assert.equal(await page.locator('#material-formats-editor [data-format-code="' + newCode + '"]').count(), 1);
  assert.equal(await page.locator('#additional-fields-editor [data-field-key="' + newKey + '"]').count(), 1);
  assert.equal(await addedRule.locator('.settings-rule-identity strong').textContent(), 'New draft format');
  assert.equal(saved.stored.customFields.find(field => field.key === savedKey).label, 'Renamed historical field');
  assert.equal(saved.stored.customFields.find(field => field.key === savedKey).enabled, false);
  assert.equal(saved.stored.customFields.find(field => field.key === savedKey).options.find(option => option.id === 'selected-choice').enabled, false);
  assert.equal(saved.stored.libraryOverride.publicationOptions.values.find(option => option.id === 'historical-publication').label,
    'Renamed historical publication');
  assert.equal(saved.stored.libraryOverride.publicationOptions.values.find(option => option.id === 'historical-publication').enabled, false);
  assert.equal(saved.effective.formats.find(format => format.code === 'book').label, 'Historical witness library book');
  report.formatRuleDrafts = { newCode, newKey, savedKey, optionIds, publicationKey, templateKey, roundTrip: true };
}

module.exports = { runLibraryFormatRuleDrafts };
