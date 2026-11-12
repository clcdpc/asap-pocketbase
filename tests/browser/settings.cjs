'use strict';
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');
const { runLibraryFormatRuleDrafts } = require('./settings-format-rule-drafts.cjs');
const { verifyEmailTemplateCards } = require('./email-template-cards.cjs');
const { verifyInheritanceInteractions } = require('./settings-inheritance-interactions.cjs');
const { verifySettingsDraftBlockers } = require('./settings-draft-blockers.cjs');
const { contrast } = require('./settings-accessibility.cjs');
const settingsSpacingRecorder = require('./settings-spacing.cjs');

function parseArguments(argv) {
  if (![5, 6, 8].includes(argv.length)) {
    throw new Error('Usage: node tests/browser/settings.cjs <baseURL> <artifactDirectory> <staffId> <tenantId> <email> [mode [scopeId formatCode]]');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    identity: { staffId: argv[2], tenantId: argv[3], email: argv[4] },
    mode: argv[5] || 'full',
    scopeId: argv[6] || 'system',
    formatCode: argv[7] || null
  };
}

function sameOrigin(url, origin) {
  try { return new URL(url).origin === origin; } catch { return false; }
}

async function scan(page, axeSource, artifactRoot, report, viewport, checkTimeoutGeometry = false, fullPage = true) {
  await report.recordSpacing?.(viewport);
  for (const selector of ['#organizations-status-message', '#polaris-api-key-status', '#polaris-admin-pass-status']) {
    if (!await page.locator(selector).isVisible()) continue;
    const result = await contrast(page, selector);
    assert.ok(result.ratios[0] >= 4.5, `${viewport}/${selector}: rendered text contrast including ancestor opacity: ${JSON.stringify(result)}`);
    (report.contrastChecks ||= []).push({ viewport, selector, ...result });
  }
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(checkTimeoutGeometry => {
    const bounds = element => {
      const rect = element.getBoundingClientRect();
      return { left: rect.left, right: rect.right, width: rect.width, height: rect.height };
    };
    const organizationRows = [...document.querySelectorAll('#settings-organization-catalog [data-organization-id]')]
      .map(row => ({ id: row.dataset.organizationId, row: bounds(row), children: [...row.children].map(bounds) }))
      .filter(row => row.row.width > 0 && row.row.height > 0);
    const timeoutControls = checkTimeoutGeometry
      ? [...document.querySelectorAll('.settings-timeout-rule input[type="number"], .settings-timeout-rule select')]
        .map(control => {
          const field = control.closest('.settings-field');
          const rule = control.closest('.settings-timeout-rule');
          const rect = element => {
            const bounds = element.getBoundingClientRect();
            return { left: bounds.left, right: bounds.right, width: bounds.width, height: bounds.height };
          };
          return { id: control.id, control: rect(control), field: rect(field), rule: rect(rule) };
        })
      : [];
    return {
      width: innerWidth,
      scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: [...document.images]
        .filter(image => {
          const bounds = image.getBoundingClientRect();
          return bounds.width > 0 && bounds.height > 0;
        })
        .every(image => image.complete && image.naturalWidth > 0),
      timeoutControls,
      organizationRows
    };
  }, checkTimeoutGeometry);
  await page.screenshot({ path: path.join(artifactRoot, `${viewport}-settings.png`), fullPage });
  report.states.push({ viewport, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility violation in ${viewport} settings`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow in ${viewport} settings`);
  assert.equal(layout.imagesLoaded, true, `Visible image failed to load in ${viewport} settings`);
  for (const row of layout.organizationRows) {
    assert.ok(row.row.left >= -1 && row.row.right <= layout.width + 1,
      `Organization ${row.id} fits the ${viewport} viewport.`);
    for (const child of row.children) {
      assert.ok(child.left >= row.row.left - 1 && child.right <= row.row.right + 1,
        `Organization ${row.id} contains its name/status/action in ${viewport}: ${JSON.stringify(row)}`);
    }
  }
  if (checkTimeoutGeometry) {
    assert.deepEqual(layout.timeoutControls.map(control => control.id), [
      'outstanding-timeout-days', 'outstanding-timeout-rejection-template-id',
      'hold-pickup-timeout-days', 'pending-hold-timeout-days', 'additional-copy-timeout-days'
    ], `All timeout number/select controls are visible in ${viewport}.`);
    const fitsInside = (inner, outer) => inner.left >= outer.left - 1 && inner.right <= outer.right + 1;
    for (const control of layout.timeoutControls) {
      assert.ok(control.control.width > 0 && control.control.height > 0, `${control.id} is visible in ${viewport}.`);
      assert.ok(fitsInside(control.control, control.field),
        `${control.id} fits its label in ${viewport}: ${JSON.stringify(control)}`);
      assert.ok(fitsInside(control.control, control.rule),
        `${control.id} fits its timeout rule in ${viewport}: ${JSON.stringify(control)}`);
      assert.ok(control.control.left >= -1 && control.control.right <= layout.width + 1,
        `${control.id} fits the viewport in ${viewport}: ${JSON.stringify(control)}`);
    }
  }
}

async function librarySetting(field) {
  const status = field.locator(':scope > .settings-inheritance .settings-inheritance-status');
  return await status.count() ? (await status.first().textContent()).startsWith('Library setting') : null;
}
async function useSystemValue(page, field, group = false) {
  const reset = field.locator(':scope > .settings-inheritance .settings-use-system');
  if (!await reset.count()) return;
  if (group) await decideConfirmation(page, () => reset.click(), dialog => dialog.accept());
  else await reset.click();
}
async function discardSettingsDraft(page) {
  if (!await page.locator('#settings-save').isEnabled()) return;
  await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
  await page.waitForFunction(() => document.getElementById('settings-save-title').textContent === 'No changes' && !document.getElementById('settings-form').inert);
}

async function verifyFormatRuleDraftNames(page) {
  const cards = page.locator('#format-rules-editor [data-rule-code]');
  const materials = page.locator('#material-formats-editor [data-domain-row]');
  const originals = [];
  for (const index of [0, 1]) {
    const card = cards.nth(index), material = materials.nth(index);
    const toggle = card.locator('[data-rule-toggle]');
    const code = await card.getAttribute('data-rule-code');
    const label = material.locator('[data-material-field="label"]');
    originals.push({ index, label: await label.inputValue(), expanded: await toggle.getAttribute('aria-expanded') });
    const control = await card.locator('[data-format-label="title"]').elementHandle();
    await label.fill('Shared label <draft>');
    const context = index ? 'Shared label <draft>, format 2' : 'Shared label <draft>';
    for (const name of [`Move ${context} up`, `Move ${context} down`, `Delete ${context} format rule`]) {
      const action = card.getByRole('button', { name, exact: true });
      assert.equal(await action.count(), 1);
      assert.equal(await action.getAttribute('title'), name);
    }
    assert.equal(await control.evaluate(node => node.isConnected), true, 'Label input preserves the complete draft DOM.');
    assert.equal(await toggle.getAttribute('aria-expanded'), originals[index].expanded);
    await control.dispose();
  }
  const names = await cards.locator('.settings-row-actions button').evaluateAll(buttons => buttons.map(button => button.getAttribute('aria-label')));
  assert.equal(new Set(names).size, names.length, 'Shared labels retain distinct header action names.');
  for (const original of originals) {
    const material = materials.nth(original.index);
    await material.locator('[data-material-field="label"]').fill(original.label);
  }
  if (await page.locator('#settings-scope').inputValue() !== 'system') await discardSettingsDraft(page);
}

async function runSystemFormatRuleEditor(page, report, waitForSettingsReady) {
  await page.locator('#settings-nav-patron').click();
  await verifyFormatRuleDraftNames(page);
  const rule = page.locator('#format-rules-editor [data-rule-code="book"]');
  assert.equal(await rule.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'false');
  assert.equal(await page.locator('#settings-save').isDisabled(), true);
  await rule.locator('[data-rule-toggle]').press('Enter');
  assert.equal(await page.locator('#settings-save').isDisabled(), true, 'Expansion alone leaves Settings clean.');
  await rule.locator('[data-rule-property="messageBehavior"]').selectOption('message');
  await rule.locator('[data-rule-property="message"]').fill('System format-rule integration witness');
  await rule.locator('[data-rule-toggle]').click();

  const requestPromise = page.waitForRequest(request => request.method() === 'POST' &&
    new URL(request.url()).pathname === '/api/asap/staff/settings');
  const responsePromise = page.waitForResponse(response => response.request().method() === 'POST' &&
    new URL(response.url()).pathname === '/api/asap/staff/settings');
  await page.locator('#settings-save').click();
  const [request, response] = await Promise.all([requestPromise, responsePromise]);
  const requestBody = request.postData();
  const responseBody = await response.text();
  report.systemFormatRulePost = {
    status: response.status(),
    requestBody,
    responseBody
  };

  const payload = request.postDataJSON();
  assert.equal(payload.orgId, 'system');
  const submittedRule = payload.formatRules?.find(item => item.code === 'book');
  assert.ok(submittedRule, 'The shipped System editor must submit the edited book format rule.');
  assert.equal(Object.hasOwn(submittedRule, 'customFields'), false,
    'A System format-rule request must omit the library-only customFields property.');
  assert.equal(response.status(), 200, responseBody);

  await page.reload({ waitUntil: 'networkidle' });
  await waitForSettingsReady('system');
  await page.locator('#settings-nav-patron').click();
  assert.equal(await rule.locator('[data-rule-toggle]').getAttribute('aria-expanded'), 'false');
  assert.equal(await page.locator('#format-rules-editor [data-rule-code="book"] [data-rule-property="messageBehavior"]')
    .inputValue(), 'message');
  assert.equal(await page.locator('#format-rules-editor [data-rule-code="book"] [data-rule-property="message"]')
    .inputValue(), 'System format-rule integration witness');
  report.systemFormatRuleRoundTrip = true;
}

async function runLibraryRuleCompletenessEditor(page, axeSource, args, report) {
  await page.locator('#settings-nav-patron').click();
  const targetRule = page.locator(`#format-rules-editor [data-rule-code="${args.formatCode}"]`);
  if (args.mode === 'library-rule-missing-row') {
    assert.equal(await targetRule.count(), 1,
      'The full library snapshot must initially render the format row backed by the saved custom rule.');
    report.libraryFormatRuleWitness.targetRowInitiallyPresent = true;
    await targetRule.evaluate(element => element.remove());
    report.libraryFormatRuleWitness.targetRowRemoved = true;
  } else {
    assert.equal(await targetRule.count(), 0,
      'The endpoint-shaped partial effective snapshot must omit the saved format row.');
  }

  const bookRule = page.locator('#format-rules-editor [data-rule-code="book"]');
  await bookRule.locator('[data-rule-toggle]').click();
  await bookRule.locator('[data-rule-property="messageBehavior"]').selectOption('message');
  await bookRule.locator('[data-rule-property="message"]').fill('Library rule completeness integration witness');
  assert.equal(await page.locator('#settings-save').isEnabled(), true,
    'Changing a visible rule should reach the settings completeness guard.');
  const responsePromise = page.waitForResponse(response => response.request().method() === 'POST' &&
    new URL(response.url()).pathname === '/api/asap/staff/settings', { timeout: 5000 })
    .then(async response => ({ status: response.status(), body: await response.text() }))
    .catch(() => null);
  await page.locator('#settings-save').click();
  await page.waitForFunction(() => {
    const message = document.getElementById('settings-message');
    const text = message?.textContent.trim() || '';
    return text.length > 0 && text !== 'Settings loaded.' && text !== 'Saving settings...';
  }, null, { timeout: 10000 });
  const response = await responsePromise;
  if (response) {
    report.libraryFormatRuleWitness.responseStatus = response.status;
    report.libraryFormatRuleWitness.responseBody = response.body;
  }
  report.libraryFormatRuleWitness.blocked = await page.locator('#settings-message')
    .evaluate(element => element.classList.contains('error'));
  report.libraryFormatRuleWitness.blockedMessage = await page.locator('#settings-message').textContent();
  await scan(page, axeSource, args.artifactRoot, report, 'desktop');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan(page, axeSource, args.artifactRoot, report, 'mobile');
}

async function verifyTemplateResetOnlyDrafts(page, args, report, saveSettings, ready) {
  const read = scope => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`);
    assertResponse(response);
    return response.json();
    function assertResponse(response) { if (!response.ok) throw new Error(`Template witness GET ${response.status}`); }
  }, scope);
  const post = async (scope, templates) => {
    const current = await read(scope);
    const result = await page.evaluate(async ({ scope, templates, version }) => {
      const session = await (await fetch('/api/asap/staff/session')).json();
      const response = await fetch('/api/asap/staff/settings', { method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken },
        body: JSON.stringify({ orgId: scope, version, templates }) });
      return { status: response.status, body: await response.text() };
    }, { scope, templates, version: current.version });
    assert.equal(result.status, 200, result.body);
  };
  const reload = async () => {
    await page.reload({ waitUntil: 'networkidle' }); await ready(args.scopeId);
    await page.locator('#settings-nav-templates').click();
  };
  const card = key => page.locator(`[data-template-key="${key}"]`);
  const open = async key => {
    const row = card(key), toggle = row.locator('[data-template-toggle]');
    if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
    return row;
  };
  const initial = await read(args.scopeId);
  const keys = ['suggestion_submitted', `rejection:audit_reset_${args.formatCode}`];
  const sources = keys.map(key => initial.stored.templates.find(row => row.organizationId === '1' && row.templateKey === key));
  assert.ok(sources.every(Boolean), 'Both built-in and additional System sources must be seeded.');
  const sourceIds = new Set(sources.map(row => row.id));
  const lineage = data => data.stored.templates.filter(row => row.organizationId === args.scopeId && sourceIds.has(row.sourceTemplateId));
  const custom = data => data.stored.templates.find(row => row.organizationId === args.scopeId &&
    row.templateKey === `rejection:audit_reset_custom_${args.formatCode}`);
  const owned = custom(initial);
  assert.ok(owned?.isCustom); assert.deepEqual(lineage(initial), []);
  await page.locator('#settings-nav-templates').click();
  try {
    for (const key of keys) {
      const row = await open(key);
      assert.equal(await row.locator('[data-template-field="subject"]').isEditable(), true);
      await row.locator('[data-template-field="subject"]').fill(`Temporary subject ${key}`);
      const reset = row.locator('[data-template-status="subject"] .settings-use-system');
      await reset.focus(); await reset.press('Enter');
      assert.match(await row.locator('[data-template-status="subject"]').textContent(), /Using system value.*Unsaved/);
    }
    assert.equal(await page.locator('#settings-save').isEnabled(), true, 'Reset intent stays a guarded unsaved draft until Global Save.');
    const resetPayload = await saveSettings();
    const afterReset = await read(args.scopeId);
    report.templateResetOnlyWitness = { before: lineage(initial), after: lineage(afterReset) };
    assert.deepEqual(lineage(afterReset), [], 'Edit → field reset → Save must not create either empty Library lineage.');
    assert.equal((resetPayload.templates || []).some(row => keys.includes(row.templateKey)), false,
      'No accepted lineage plus no remaining pin has no template write.');
    assert.deepEqual(custom(afterReset), owned);
    await post('system', sources.map(row => ({ templateKey: row.templateKey,
      subject: `Later system ${row.templateKey}`, body: `Later system body ${row.templateKey}` })));
    await reload();
    for (const key of keys) {
      const row = await open(key);
      assert.equal(await row.locator('[data-template-field="subject"]').inputValue(), `Later system ${key}`);
      assert.equal(await row.locator('[data-template-field="body"]').inputValue(), `Later system body ${key}`);
      for (const role of ['subject', 'body']) assert.match(await row.locator(`[data-template-status="${role}"]`).textContent(), /^Using system value/);
      if (key !== 'suggestion_submitted') assert.match(await row.locator('[data-template-item-status]').textContent(), /^Using system value/);
      await row.locator('[data-template-field="subject"]').fill(`Temporary reset beside pin ${key}`);
      await row.locator('[data-template-status="subject"] .settings-use-system').click();
      await row.locator('[data-template-field="body"]').fill(`Pinned Library body ${key}`);
    }
    report.templateResetOnlyNoLineage = true;
    const pinnedPayload = await saveSettings();
    const pinned = await read(args.scopeId);
    for (const source of sources) {
      const row = lineage(pinned).find(row => row.sourceTemplateId === source.id);
      assert.ok(row?.id && row.version); assert.equal(row.templateKey, source.templateKey);
      assert.equal(row.subject, null); assert.equal(row.body, `Pinned Library body ${source.templateKey}`);
      const posted = pinnedPayload.templates.find(row => row.templateKey === source.templateKey);
      assert.equal(Object.hasOwn(posted, 'subject'), false, 'Resetting an absent column does not suppress its sibling pin or materialize a null field.');
      assert.equal(posted.body, row.body);
    }
    await post(args.scopeId, sources.map(row => ({ templateKey: row.templateKey, sourceTemplateId: row.id,
      body: `  Accepted raw\r\nbody ${row.templateKey}  ` })));
    await reload();
    const acceptedRaw = await read(args.scopeId);
    for (const key of keys) {
      const row = await open(key);
      await row.locator('[data-template-field="subject"]').fill(`Reset existing lineage ${key}`);
      await row.locator('[data-template-status="subject"] .settings-use-system').click();
    }
    const acceptedResetPayload = await saveSettings();
    const acceptedAfter = await read(args.scopeId);
    assert.deepEqual(lineage(acceptedAfter), lineage(acceptedRaw),
      'Accepted lineage IDs/versions, sparse raw CRLF body and other-column pins remain exact.');
    assert.deepEqual(custom(acceptedAfter), owned);
    for (const key of keys) {
      const row = acceptedResetPayload.templates.find(row => row.templateKey === key);
      assert.equal(row.subject, null); assert.equal(Object.hasOwn(row, 'body'), false);
    }
    report.templateResetKeepsSiblingPinsAndRawRows = true;
    await post(args.scopeId, sources.map(row => ({ templateKey: row.templateKey, sourceTemplateId: row.id, reset: true })));
    const cleared = await read(args.scopeId);
    assert.deepEqual(lineage(cleared), []); assert.deepEqual(custom(cleared), owned);
  } finally {
    await post('system', sources.map(row => ({ templateKey: row.templateKey, subject: row.subject, body: row.body })));
    await reload();
  }
}

async function runLibraryOverrideMetadataEditor(page, args, report, saveSettings, waitForSettingsReady) {
  await verifyTemplateResetOnlyDrafts(page, args, report, saveSettings, waitForSettingsReady);
  const read = (scope = args.scopeId) => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`);
    if (!response.ok) throw new Error(`Settings GET ${response.status}`);
    return response.json();
  }, scope);
  const initial = await read();
  const providerId = key => initial.effective.externalSearchProviders.find(item => item.key === key).id;
  const formatId = code => initial.effective.formats.find(item => item.code === code).id;
  const keyA = `audit_provider_a_${args.formatCode}`;
  const keyB = `audit_provider_b_${args.formatCode}`;
  const codeA = `audit_format_a_${args.formatCode}`;
  const codeB = `audit_format_b_${args.formatCode}`;
  const customCode = `audit_custom_${args.formatCode}`;
  const sparseCode = `audit_sparse_${args.formatCode}`;
  const inheritedCode = `audit_inherited_${args.formatCode}`;
  const providerA = providerId(keyA);
  const providerB = providerId(keyB);
  const formatA = formatId(codeA);
  const formatB = formatId(codeB);
  const customId = formatId(customCode);
  const sparseId = formatId(sparseCode);
  const rawProvider = (data, id) => data.stored.libraryOverride.providers.find(item => item.id === id);
  const rawFormat = (data, id) => data.stored.libraryOverride.formats.find(item => item.materialFormatId === id);
  const rawOwned = (data, id) => data.stored.libraryOverride.formats.find(item => item.id === id && item.kind === 'custom');
  const initialSparse = rawOwned(initial, sparseId);
  let untouchedProvider = rawProvider(initial, providerB);
  let untouchedFormat = rawFormat(initial, formatB);
  const systemProviderB = initial.stored.configuredSystem.providers.find(item => item.id === providerB);
  const systemFormatB = initial.stored.configuredSystem.formats.find(item => item.id === formatB);
  for (const field of ['label', 'isEnabled', 'urlTemplate']) {
    assert.equal(untouchedProvider[field], systemProviderB[field], 'An API-created library override must survive a later matching system default.');
  }
  for (const field of ['label', 'sortOrder', 'isEnabled', 'messageBehavior', 'message', 'authorMode', 'authorLabel']) {
    assert.equal(untouchedFormat[field], systemFormatB[field], 'The fixture must contain a real converged persisted format override.');
  }
  for (const id of [providerA, providerB]) {
    assert.equal(initial.effective.externalSearchProviders.find(item => item.id === id).overridden, true,
      'The production Settings GET must preserve actual provider override state.');
  }
  for (const id of [formatA, formatB]) {
    const row = initial.effective.formats.find(item => item.id === id);
    assert.equal(row.overridden, true);
    assert.equal(row.version, rawFormat(initial, id).version);
  }
  assert.equal(initial.effective.formats.find(item => item.code === inheritedCode).overridden, false);
  assert.equal(initial.effective.formats.find(item => item.id === customId).ownerOrganizationId, args.scopeId);
  assert.equal(initial.effective.formats.find(item => item.id === customId).version,
    initial.stored.libraryOverride.formats.find(item => item.id === customId && item.kind === 'custom').version);
  for (const name of ['messageBehavior', 'titleMode', 'titleLabel', 'authorMode', 'authorLabel',
    'identifierMode', 'identifierLabel', 'publicationMode', 'publicationLabel']) {
    assert.equal(initialSparse[name], null, 'The active owned witness must retain nullable raw default fields.');
  }

  await page.locator('#settings-nav-workflow').click();
  await page.locator(`#external-search-provider-editor [data-provider-id="${providerA}"] input`).nth(0)
    .fill('Changed provider A');
  let payload = await saveSettings();
  assert.equal(payload.providers.some(item => item.id === providerB), false);
  let current = await read();
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider, 'Untouched provider SQL row/version must survive sibling save.');

  await page.locator('#settings-nav-patron').click();
  await page.locator(`#material-formats-editor [data-format-id="${formatA}"] [data-material-field="label"]`)
    .fill('Changed format A');
  payload = await saveSettings();
  assert.equal(payload.formats.some(item => item.id === formatB), false);
  current = await read();
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat, 'Untouched format SQL row/version must survive sibling save.');
  assert.deepEqual(rawOwned(current, sparseId), initialSparse, 'Untouched active owned SQL row/version must survive sibling save.');
  assert.equal(await page.locator('#format-rules-editor [data-rule-code="' + codeA + '"] .settings-rule-identity strong').textContent(),
    'Changed format A', 'Rule headings use the effective saved Library label.');

  await page.locator(`#material-formats-editor [data-format-id="${customId}"] [data-material-field="label"]`)
    .fill('Changed owned format');
  payload = await saveSettings();
  const savedCustom = payload.formats.find(item => item.id === customId);
  assert.equal(savedCustom.ownerOrganizationId, Number(args.scopeId));
  assert.equal(savedCustom.version, initial.effective.formats.find(item => item.id === customId).version);
  current = await read();
  assert.equal(current.effective.formats.find(item => item.id === customId).label, 'Changed owned format');
  const beforeStaleOwned = current;
  const postVersionWitness = (body, method = 'POST', path = '/api/asap/staff/settings') =>
    page.evaluate(async ({ body, method, path }) => {
      const session = await (await fetch('/api/asap/staff/session')).json();
      const response = await fetch(path, {
        method, headers: { 'content-type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken },
        ...(method === 'POST' ? { body: JSON.stringify(body) } : {})
      });
      return { status: response.status, body: await response.json() };
    }, { body, method, path });
  let rejected = await postVersionWitness({
    orgId: args.scopeId, version: current.version,
    patron: { loginNote: 'Must roll back with stale owned row' },
    formats: [{ ...savedCustom, label: 'Stale owned overwrite' }]
  });
  assert.equal(rejected.status, 409);
  assert.equal(rejected.body.code, 'stale_version');
  assert.deepEqual(await read(), beforeStaleOwned, 'A stale individual owned-format version must roll back the entire Settings mutation.');
  rejected = await postVersionWitness(null, 'DELETE',
    `/api/asap/staff/settings/formats/${customId}?version=${encodeURIComponent(savedCustom.version)}`);
  assert.equal(rejected.status, 409);
  assert.equal(rejected.body.code, 'stale_version');
  assert.deepEqual(await read(), beforeStaleOwned);
  rejected = await postVersionWitness({
    orgId: args.scopeId, version: current.version,
    patron: { loginNote: 'Must roll back with contradictory owner' },
    formats: [{ ...current.effective.formats.find(item => item.id === customId), ownerOrganizationId: 1 }]
  });
  assert.equal(rejected.status, 400);
  assert.equal(rejected.body.code, 'settings_invalid');
  assert.deepEqual(await read(), beforeStaleOwned);

  await useSystemValue(page, page.locator(`#material-formats-editor [data-format-id="${formatA}"]`));
  payload = await saveSettings();
  assert.equal(payload.formats.find(item => item.id === formatA).reset, true);
  current = await read();
  assert.equal(rawFormat(current, formatA), undefined);
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat);
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider);
  await page.locator('#patron-login-note').fill('Unrelated scalar metadata witness');
  payload = await saveSettings();
  assert.equal(Object.hasOwn(payload, 'providers'), false);
  assert.equal(Object.hasOwn(payload, 'formats'), false);
  assert.equal(Object.hasOwn(payload, 'formatRules'), false);

  const stale = await page.evaluate(async staleData => {
    const session = await (await fetch('/api/asap/staff/session')).json();
    const response = await fetch('/api/asap/staff/settings', {
      method: 'POST', headers: { 'content-type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken },
      body: JSON.stringify({ orgId: staleData.orgId, version: staleData.version, providers: [] })
    });
    return { status: response.status, body: await response.json() };
  }, initial);
  assert.equal(stale.status, 409);
  assert.equal(stale.body.code, 'stale_version');

  const deletePromise = page.waitForResponse(response => response.request().method() === 'DELETE' &&
    new URL(response.url()).pathname === `/api/asap/staff/settings/formats/${customId}`);
  await page.locator(`#material-formats-editor [data-format-id="${customId}"] button[aria-label="Delete"]`).click();
  await saveSettings();
  const deletion = await deletePromise;
  assert.equal(deletion.status(), 200, await deletion.text());
  current = await read();
  assert.equal(current.effective.formats.some(item => item.id === customId), false);
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat);
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider);

  const beforeProviderEdit = untouchedProvider;
  await page.locator('#settings-nav-workflow').click();
  await page.locator(`#external-search-provider-editor [data-provider-id="${providerB}"] input`).nth(0)
    .fill('Changed pinned provider B');
  await saveSettings();
  current = await read();
  untouchedProvider = rawProvider(current, providerB);
  assert.deepEqual({ ...untouchedProvider, label: beforeProviderEdit.label, version: beforeProviderEdit.version }, beforeProviderEdit,
    'Editing one provider field must preserve the other explicit converged fields.');
  assert.equal(untouchedProvider.label, 'Changed pinned provider B');
  assert.notEqual(untouchedProvider.version, beforeProviderEdit.version);

  const beforeFormatEdit = untouchedFormat;
  await page.locator('#settings-nav-patron').click();
  await page.locator(`#material-formats-editor [data-format-id="${formatB}"] [data-material-field="label"]`)
    .fill('Changed pinned format B');
  await saveSettings();
  current = await read();
  untouchedFormat = rawFormat(current, formatB);
  assert.deepEqual({ ...untouchedFormat, label: beforeFormatEdit.label, version: beforeFormatEdit.version }, beforeFormatEdit,
    'Editing one format field must preserve explicit converged order, enablement, message, and field rules.');
  assert.equal(untouchedFormat.label, 'Changed pinned format B');
  assert.notEqual(untouchedFormat.version, beforeFormatEdit.version);

  const system = await read('system');
  const systemFormat = system.effective.formats.find(item => item.id === formatB);
  const changedBase = await postVersionWitness({
    orgId: 'system', version: system.version,
    providers: [{ id: providerB, key: keyB, label: 'Later system provider B', isEnabled: true,
      urlTemplate: 'https://catalog.example.org/later-b?q={query}' }],
    formats: [{ id: formatB, code: codeB, ownerOrganizationId: 1, version: systemFormat.version,
      label: 'Later system format B', sortOrder: 9730, isEnabled: true, messageBehavior: 'none', message: null,
      author: { mode: 'optional', label: 'Later system creator B' } }]
  });
  assert.equal(changedBase.status, 200);
  assert.equal(changedBase.body.code, 'saved');
  current = await read();
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider);
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat);
  const resolvedProviderB = current.effective.externalSearchProviders.find(item => item.id === providerB);
  for (const field of ['label', 'isEnabled', 'urlTemplate']) assert.equal(resolvedProviderB[field], untouchedProvider[field]);
  const resolvedFormatB = current.effective.formats.find(item => item.id === formatB);
  for (const field of ['label', 'sortOrder', 'isEnabled', 'messageBehavior', 'message']) assert.equal(resolvedFormatB[field], untouchedFormat[field]);
  assert.equal(resolvedFormatB.author.mode, untouchedFormat.authorMode);
  assert.equal(resolvedFormatB.author.label, untouchedFormat.authorLabel);

  await page.reload({ waitUntil: 'networkidle' });
  await waitForSettingsReady(args.scopeId);
  const beforeProviderClear = untouchedProvider;
  await page.locator('#settings-nav-workflow').click();
  const providerRow = page.locator(`#external-search-provider-editor [data-provider-id="${providerB}"]`);
  await providerRow.locator('[data-provider-status="label"] .settings-use-system').click();
  await providerRow.locator('[data-provider-status="urlTemplate"] .settings-use-system').click();
  payload = await saveSettings();
  assert.equal(payload.providers.find(item => item.id === providerB).label, null);
  assert.equal(payload.providers.find(item => item.id === providerB).urlTemplate, null);
  current = await read();
  untouchedProvider = rawProvider(current, providerB);
  assert.equal(untouchedProvider.label, null, 'Clearing ordinary provider text in the shipped editor must resume inheritance.');
  assert.equal(untouchedProvider.urlTemplate, null);
  assert.deepEqual({ ...untouchedProvider, label: beforeProviderClear.label, urlTemplate: beforeProviderClear.urlTemplate,
    version: beforeProviderClear.version }, beforeProviderClear, 'Clearing provider text must preserve its explicit enabled value.');
  assert.equal(current.effective.externalSearchProviders.find(item => item.id === providerB).label, 'Later system provider B');
  assert.equal(current.effective.externalSearchProviders.find(item => item.id === providerB).urlTemplate,
    'https://catalog.example.org/later-b?q={query}');

  const beforeAuthorLabelClear = untouchedFormat;
  await page.locator('#settings-nav-patron').click();
  await page.locator(`#format-rules-editor [data-rule-code="${codeB}"] [data-rule-toggle]`).click();
  await page.locator(`#format-rules-editor [data-rule-code="${codeB}"] [data-rule-inheritance="author.label"] .settings-use-system`).click();
  await saveSettings();
  current = await read();
  untouchedFormat = rawFormat(current, formatB);
  assert.equal(untouchedFormat.authorLabel, null);
  assert.deepEqual({ ...untouchedFormat, authorLabel: beforeAuthorLabelClear.authorLabel,
    version: beforeAuthorLabelClear.version }, beforeAuthorLabelClear, 'Clearing a format field label preserves its explicit mode and other format fields.');
  assert.equal(current.effective.formats.find(item => item.id === formatB).author.label, 'Later system creator B');

  assert.deepEqual(rawOwned(current, sparseId), initialSparse,
    'Editing inherited field rules must preserve every untouched active owned nullable field and version.');
  await page.locator(`#format-rules-editor [data-rule-code="${sparseCode}"] [data-rule-toggle]`).click();
  await page.locator(`#format-rules-editor [data-rule-code="${sparseCode}"] [data-format-label="author"]`)
    .fill('Changed sparse creator');
  await saveSettings();
  current = await read();
  const changedSparse = rawOwned(current, sparseId);
  assert.equal(changedSparse.authorLabel, 'Changed sparse creator');
  assert.notEqual(changedSparse.version, initialSparse.version);
  assert.deepEqual({ ...changedSparse, authorLabel: initialSparse.authorLabel, version: initialSparse.version }, initialSparse,
    'An intentional owned label edit must preserve its other nullable raw default fields.');

  const beforePartialProvider = rawProvider(current, providerA);
  let partial = await postVersionWitness({ orgId: args.scopeId, version: current.version,
    providers: [{ id: providerA, isEnabled: false }] });
  assert.equal(partial.status, 200);
  current = await read();
  assert.equal(rawProvider(current, providerA).isEnabled, false);
  assert.equal(rawProvider(current, providerA).label, beforePartialProvider.label, 'Omitted provider fields must preserve existing overrides.');
  partial = await postVersionWitness({ orgId: args.scopeId, version: current.version,
    providers: [{ id: providerA, isEnabled: null }] });
  assert.equal(partial.status, 200);
  current = await read();
  assert.deepEqual({ ...rawProvider(current, providerA), version: beforePartialProvider.version }, beforePartialProvider,
    'A supplied null enabled value must clear that provider field without clearing omitted text fields.');

  const beforeNullRule = untouchedFormat;
  partial = await postVersionWitness({ orgId: args.scopeId, version: current.version,
    formats: [{ id: formatB, code: codeB, ownerOrganizationId: 1, version: beforeNullRule.version,
      author: { mode: null, label: null } }] });
  assert.equal(partial.status, 200);
  current = await read();
  untouchedFormat = rawFormat(current, formatB);
  assert.equal(untouchedFormat.authorMode, null);
  assert.equal(untouchedFormat.authorLabel, null);
  assert.deepEqual({ ...untouchedFormat, authorMode: beforeNullRule.authorMode, version: beforeNullRule.version }, beforeNullRule,
    'Supplied null built-in field values resume inheritance and preserve omitted format fields.');

  for (const corruption of ['provider-override', 'format-owner', 'format-version', 'format-override']) {
    const before = await read();
    let posts = 0;
    const listener = request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/settings') posts++;
    };
    page.on('request', listener);
    await page.route('**/api/asap/staff/settings?*', async route => {
      if (route.request().method() !== 'GET') return route.continue();
      const response = await route.fetch();
      const data = await response.json();
      if (corruption === 'provider-override') delete data.effective.externalSearchProviders.find(item => item.id === providerB).overridden;
      else if (corruption === 'format-owner') data.effective.formats.find(item => item.id === formatB).ownerOrganizationId = args.scopeId;
      else if (corruption === 'format-version') delete data.effective.formats.find(item => item.id === formatB).version;
      else data.effective.formats.find(item => item.id === formatB).overridden = false;
      await route.fulfill({ status: response.status(), contentType: 'application/json', body: JSON.stringify(data) });
    });
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady(args.scopeId);
    await page.locator('#settings-nav-patron').click();
    await page.locator('#patron-login-note').fill(`Blocked ${corruption}`);
    await page.locator('#settings-save').click();
    await page.locator('#settings-message.error').waitFor();
    assert.match(await page.locator('#settings-message').textContent(), /metadata snapshot is malformed/);
    assert.equal(posts, 0, 'Malformed metadata must block the shipped editor before a destructive POST.');
    await page.unroute('**/api/asap/staff/settings?*');
    page.off('request', listener);
    assert.deepEqual(await read(), before, 'Blocked metadata must leave authoritative SQL/audit/version unchanged.');
  }
  await page.reload({ waitUntil: 'networkidle' });
  await waitForSettingsReady(args.scopeId);
  report.libraryOverrideMetadataRoundTrip = true;
}

async function runPatronTextRefinements(page, axeSource, args, report, saveSettings, ready, read, reload, switchScope) {
  const library = args.scopeId;
  if (args.mode !== 'campaign2-library-admin') {
    await switchScope('system');
    await page.locator('#settings-nav-workflow').click();
    const before = await read('system');
    const originalKeys = before.stored.configuredSystem.providers.map(item => [item.id, item.key]);
    assert.equal(await page.getByText('Provider key', { exact: true }).count(), 0);
    await page.locator('#add-provider').click();
    const added = page.locator('#external-search-provider-editor [data-provider-id=""]');
    await added.getByLabel('Label', { exact: true }).fill(`UI refinements provider ${args.formatCode}`);
    await added.getByLabel('URL template', { exact: true }).fill(`${args.baseOrigin}/catalog?q={{title}}&fixture=${args.formatCode}`);
    await added.getByLabel('Enabled', { exact: true }).check();
    const key = await added.getAttribute('data-provider-key');
    assert.ok(key && !originalKeys.some(item => item[1].toLowerCase() === key.toLowerCase()));
    await saveSettings(); await reload('system'); await page.locator('#settings-nav-workflow').click();
    const saved = await read('system');
    const provider = saved.stored.configuredSystem.providers.find(item => item.key === key);
    assert.ok(provider?.id); assert.equal(provider.isEnabled, true);
    for (const [id, original] of originalKeys) assert.equal(saved.stored.configuredSystem.providers.find(item => item.id === id).key, original);
    const savedRow = page.locator(`#external-search-provider-editor [data-provider-id="${provider.id}"]`);
    try {
      await savedRow.getByLabel('Label', { exact: true }).fill(`UI refinements provider ${args.formatCode} edited`);
      await savedRow.getByLabel('Enabled', { exact: true }).uncheck();
      const expectedOrder = saved.stored.configuredSystem.providers.map(item => item.id);
      const position = expectedOrder.indexOf(provider.id);
      assert.ok(position > 0);
      [expectedOrder[position - 1], expectedOrder[position]] = [expectedOrder[position], expectedOrder[position - 1]];
      await savedRow.getByRole('button', { name: 'Move up', exact: true }).click();
      await saveSettings(); await reload('system');
      const editedProviders = (await read('system')).stored.configuredSystem.providers;
      const editedProvider = editedProviders.find(item => item.id === provider.id);
      assert.equal(editedProvider.key, key);
      assert.equal(editedProvider.label, `UI refinements provider ${args.formatCode} edited`);
      assert.equal(editedProvider.isEnabled, false);
      assert.deepEqual(editedProviders.map(item => item.id), expectedOrder, 'Provider reordering must persist without changing identities.');
    } finally {
      // The .NET class shares its fixture: restore exact accepted orders even
      // when a probe fails, before the owning cleanup deletes the added row.
      const current = await read('system');
      await page.evaluate(async body => {
        const { authorizedJson } = await import('/staff/js/http.js');
        await authorizedJson('/api/asap/staff/settings', { method: 'POST', body });
      }, { orgId: 'system', version: current.version, providers: saved.stored.configuredSystem.providers.map(
        ({ id, key, sortOrder }) => ({ id, key, sortOrder })) });
      await reload('system');
      assert.deepEqual((await read('system')).stored.configuredSystem.providers.map(({ id, key, sortOrder }) => ({ id, key, sortOrder })),
        saved.stored.configuredSystem.providers.map(({ id, key, sortOrder }) => ({ id, key, sortOrder })),
        'The shared fixture must retain its exact original provider order and identities.');
    }
    await page.locator('#settings-nav-patron').click();
    const systemMessage = 'System <b> & é\nOne line\n\nLibby help:\nhttps://help.libbyapp.com/en-us/6260.htm\n';
    await page.locator('#patron-success-message').fill(systemMessage);
    await saveSettings(); await reload('system');
    assert.equal((await read('system')).stored.configuredSystem.patron.successMessage, systemMessage);
    await switchScope(library);
    report.providerIdentityRoundTrip = true;
  }
  await page.locator('#settings-nav-patron').click();
  const systemBefore = (await read(library)).stored.configuredSystem.patron.successMessage;
  const successField = page.locator('[data-setting-key="successMessage"]');
  const duplicateField = page.locator('[data-setting-key="alreadySubmittedMessage"]');
  assert.equal(await successField.locator('textarea').isDisabled(), false);
  assert.equal(await successField.locator('.patron-placeholder-trigger').isDisabled(), false);
  const message = 'Library <img src=x onerror="alert(1)"> & é\nCard: selected\n\nLibby help:\nhttps://help.libbyapp.com/en-us/6260.htm\n';
  await successField.locator('textarea').fill(message);
  const start = message.indexOf('selected');
  await successField.locator('textarea').evaluate((element, start) => element.setSelectionRange(start, start + 8), start);
  await successField.locator('.patron-placeholder-trigger').focus(); await page.keyboard.press('Enter');
  assert.deepEqual(await successField.locator('[data-patron-placeholder]').evaluateAll(buttons => buttons.map(button => button.dataset.patronPlaceholder)), ['barcode_label', 'pin_label']);
  await page.keyboard.press('Enter');
  const expected = message.replace('selected', '{{barcode_label}}');
  assert.equal(await successField.locator('textarea').inputValue(), expected);
  assert.equal(await successField.locator('textarea').evaluate(element => document.activeElement === element), true);
  assert.equal(await successField.locator('textarea').evaluate(element => element.selectionStart), start + '{{barcode_label}}'.length);
  await duplicateField.locator('textarea').fill('Previous: ');
  await duplicateField.locator('textarea').evaluate(element => element.setSelectionRange(10, 10));
  await duplicateField.locator('.patron-placeholder-trigger').click();
  await duplicateField.locator('[data-patron-placeholder="duplicate_title"]').click();
  await saveSettings(); await reload(library); await page.locator('#settings-nav-patron').click();
  assert.equal(await successField.locator('textarea').inputValue(), expected);
  assert.equal(await duplicateField.locator('textarea').inputValue(), 'Previous: {{duplicate_title}}');
  assert.equal((await read(library)).stored.configuredSystem.patron.successMessage, systemBefore, 'A library edit cannot replace System copy.');
  const config = await page.evaluate(async library => (await fetch(`/api/asap/config?libraryOrgId=${library}`)).json(), library);
  assert.equal(config.successMessage, expected);
  const patron = await page.context().newPage();
  patron.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(patron, report.pageErrors);
  try {
    await patron.goto(`${args.baseOrigin}/patron/?libraryOrgId=${library}`, { waitUntil: 'networkidle' });
    const rendered = await patron.evaluate(async config => {
      const { applyLoadedUiText } = await import('/patron/js/config.js');
      const { renderSuccessMessage } = await import('/patron/js/form-ui.js');
      const { showSuccessStep } = await import('/patron/js/steps.js');
      applyLoadedUiText(config); renderSuccessMessage();
      showSuccessStep();
      const body = document.getElementById('success-body');
      return { text: body.textContent, tags: body.querySelectorAll('img,script,b').length,
        whiteSpace: getComputedStyle(body).whiteSpace, links: [...body.querySelectorAll('a')].map(link => link.href) };
    }, config);
    assert.equal(rendered.text, expected.replace('{{barcode_label}}', config.barcodeLabel));
    assert.equal(rendered.tags, 0); assert.equal(rendered.whiteSpace, 'pre-wrap');
    assert.deepEqual(rendered.links, ['https://help.libbyapp.com/en-us/6260.htm']);
    await scan(patron, axeSource, args.artifactRoot, report, 'patron-plain-text');
  } finally { await patron.close(); }
  for (const [name, width, height] of [['patron-picker-desktop', 1280, 900], ['patron-picker-mobile', 390, 844]]) {
    await page.setViewportSize({ width, height });
    await duplicateField.locator('.patron-placeholder-trigger').focus(); await page.keyboard.press('Space');
    await scan(page, axeSource, args.artifactRoot, report, name);
    await page.keyboard.press('Escape');
    assert.equal(await duplicateField.locator('.patron-placeholder-trigger').evaluate(element => document.activeElement === element), true);
  }
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.locator('#settings-nav-templates').click();
  report.patronTextRoundTrip = true;
}

async function runCampaign2Editor(page, axeSource, args, report, saveSettings, ready) {
  await verifyEmailTemplateCards({ page, report,
    scan: (name, fullPage = false) => scan(page, axeSource, args.artifactRoot, report, name, false, fullPage) });
  const library = args.scopeId;
  const read = scope => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`);
    if (!response.ok) throw new Error(`Settings GET ${response.status}`);
    return response.json();
  }, scope);
  const reload = async scope => {
    await page.reload({ waitUntil: 'networkidle' });
    await ready(scope);
  };
  const switchScope = async scope => {
    await page.locator('#settings-scope').selectOption(scope);
    await page.waitForFunction(scope => document.getElementById('settings-scope').value === scope &&
      document.getElementById('settings-version').value && !document.getElementById('settings-form').inert, scope);
    await page.waitForLoadState('networkidle');
    await ready(scope);
  };
  // Retained identities locate cards independently of their current labels.
  const rowIndex = key => page.locator('#email-templates-editor [data-domain-row]').evaluateAll((rows, key) =>
    rows.findIndex(row => row.dataset.templateKey === key), key);
  const openCard = async control => {
    const card = control.locator('xpath=ancestor-or-self::*[contains(concat(" ",normalize-space(@class)," ")," settings-template-card ")][1]');
    const toggle = card.locator('[data-template-toggle]');
    if (await toggle.getAttribute('aria-expanded') === 'false') await toggle.click();
  };
  const row = async key => {
    const card = page.locator('#email-templates-editor [data-domain-row]').nth(await rowIndex(key));
    await openCard(card); return card;
  };
  const titleButton = () => page.locator('.template-placeholder-picker:popover-open [data-placeholder-key="title"]');
  const tokenKeys = () => page.locator('.template-placeholder-picker:popover-open [data-placeholder-key]')
    .evaluateAll(buttons => buttons.map(button => button.dataset.placeholderKey));
  const openPicker = async control => {
    await openCard(control);
    const trigger = control.locator('xpath=..').locator('.template-placeholder-trigger');
    await trigger.scrollIntoViewIfNeeded();
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await trigger.click();
    const popupId = await trigger.getAttribute('aria-controls');
    try {
      await page.waitForFunction(id => document.getElementById(id)?.matches(':popover-open') === true,
        popupId, { timeout: 5000 });
    } catch (error) {
      const diagnostic = await trigger.evaluate(node => {
        const card = node.closest('.settings-template-card');
        const popup = document.getElementById(node.getAttribute('aria-controls'));
        const control = node.closest('.settings-template-field').querySelector('input, textarea');
        return { scope: document.getElementById('settings-scope').value, version: document.getElementById('settings-version').value,
          formInert: document.getElementById('settings-form').inert, key: card.dataset.templateKey,
          kind: card.dataset.templateKind, expanded: card.querySelector('[data-template-toggle]').getAttribute('aria-expanded'),
          override: card.querySelector('.settings-template-override')?.checked, triggerDisabled: node.disabled,
          triggerExpanded: node.getAttribute('aria-expanded'), fieldDisabled: control.disabled, fieldReadOnly: control.readOnly,
          nativeOpen: popup.matches(':popover-open'), activeElement: document.activeElement?.id || document.activeElement?.localName,
          viewport: { width: innerWidth, height: innerHeight, scrollY }, bounds: node.getBoundingClientRect().toJSON() };
      });
      throw new Error(`The contextual email picker did not stay open: ${JSON.stringify(diagnostic)}. ${error.message}`);
    }
  };
  const selectRange = async (control, start, end = start) => {
    await openCard(control);
    await control.focus();
    await control.evaluate((element, range) => {
      element.setSelectionRange(range.start, range.end);
      element.dispatchEvent(new Event('select', { bubbles: true }));
    }, { start, end });
    await openPicker(control);
  };
  const assertLibraryContext = async () => {
    assert.match(await page.locator('#settings-system-context').textContent(), /Campaign 2 library/);
    assert.match(await page.locator('#settings-system-context').textContent(), /apply to every library/);
    assert.equal(await page.locator('#settings-nav-polaris .settings-scope-badge').isVisible(), true);
    await page.locator('#settings-nav-polaris').click();
    assert.match(await page.locator('#settings-polaris .settings-scope-explanation').first().textContent(), /Campaign 2 library/);
    assert.equal(await page.locator('#polaris-host').isDisabled(), true);
    await page.locator('#settings-nav-smtp').click();
    assert.equal(await page.locator('#email-postmark-token').isDisabled(), true);
    assert.equal(await page.locator('#email-clear-postmark-token').isDisabled(), true);
    assert.equal(await page.locator('[data-setting-key="postmarkToken"] .settings-override-toggle').count(), 0);
    assert.equal(await page.locator('#settings-nav-smtp .settings-scope-badge').count(), 0);
  };

  if (args.mode === 'campaign2-library-admin') {
    await assertLibraryContext();
    assert.equal(await page.locator('#settings-switch-system').isVisible(), false);
    assert.equal(await page.locator('#settings-scope-field').isVisible(), false);
    await page.locator('#settings-nav-templates').click();
    const inherited = await row('rejection:template_1');
    assert.equal(await inherited.locator('.template-subject').isDisabled(), false);

    await inherited.locator('.template-body').fill('Library admin {{title}}');
    const payload = await saveSettings();
    assert.equal(Object.hasOwn(payload.email, 'postmarkToken'), false);
    assert.equal(Object.hasOwn(payload.email, 'clearPostmarkToken'), false);
    await reload(library);
    await page.locator('#settings-nav-templates').click();
    assert.equal(await (await row('rejection:template_1')).locator('.template-body').inputValue(), 'Library admin {{title}}');
    report.campaign2LibraryAdmin = true;
  } else {
    await page.locator('#settings-nav-templates').click();
    const initial = await read('system');
    const patronKeys = ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName'];
    assert.deepEqual([...initial.templatePlaceholders].sort(), [...patronKeys].sort(),
      'The UI catalog must remain aligned with the actual patron renderer capability contract.');
    assert.equal(await page.locator('#settings-template-editor section[aria-labelledby="patron-email-templates-title"] [data-template-toggle][aria-expanded="false"]').count(), 7,
      'The five Patron built-in and two existing Additional cards start collapsed.');
    await openPicker(page.locator('#email-submit-subject'));
    assert.deepEqual(await tokenKeys(), patronKeys);
    for (const key of ['rejection:template_1', 'rejection:template_3']) {
      assert.equal(await (await row(key)).locator('.template-key').count(), 0);
    }
    await page.locator('#add-email-template').click();
    let added = page.locator('#email-templates-editor [data-template-id=""]');
    const createdKey = await added.getAttribute('data-template-key');
    assert.match(createdKey, /^rejection:template_[a-f0-9]{32}$/);
    assert.equal(await added.locator('.template-key').count(), 0);
    report.generatedSystemTemplateKey = createdKey;
    await added.locator('.template-display-name').fill('Campaign 2 added template');
    await added.locator('.template-subject').fill('É & title: ');
    await added.locator('.template-body').fill('First\nselected & last');
    await selectRange(added.locator('.template-body'), 6, 14);
    assert.deepEqual(await tokenKeys(), ['title']);
    await titleButton().focus();
    report.campaign2InsertionFocus = await page.evaluate(() => ({
      active: document.activeElement?.className,
      expanded: document.querySelector('.template-placeholder-trigger[aria-expanded="true"]')?.getAttribute('aria-label')
    }));
    await page.keyboard.press('Enter');
    assert.equal(await added.locator('.template-body').inputValue(), 'First\n{{title}} & last',
      JSON.stringify(report.campaign2InsertionFocus));
    assert.equal(await added.locator('.template-body').evaluate(element => document.activeElement === element), true);
    assert.equal(await added.locator('.template-body').evaluate(element => element.selectionStart), 15);
    const createdPayload = await saveSettings();
    assert.equal(createdPayload.templates.find(item => item.templateKey === createdKey).body, 'First\n{{title}} & last');
    await reload('system');
    await page.locator('#settings-nav-templates').click();
    added = await row(createdKey);
    assert.equal(await added.getAttribute('data-template-key'), createdKey);
    assert.equal(await added.locator('.template-subject').inputValue(), 'É & title: ');
    const saved = await read('system');
    const created = saved.stored.templates.find(item => item.templateKey === createdKey && item.organizationId === '1');
    assert.equal(typeof created.id, 'string');
    assert.equal(created.body, 'First\n{{title}} & last');
    for (const original of initial.stored.templates) {
      assert.deepEqual(saved.stored.templates.find(item => item.id === original.id), original,
        'Creating an additional template must preserve every existing raw row/version.');
    }
    const systemKeysBefore = await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey));
    const movingSystem = await row(createdKey);
    await movingSystem.locator('[aria-label="Move up"]').click();
    const systemKeysAfter = await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey));
    assert.notDeepEqual(systemKeysAfter, systemKeysBefore);
    const systemOrderPayload = await saveSettings();
    assert.equal(systemOrderPayload.templates.length, 1, 'A representable System move patches only its moved row.');
    assert.deepEqual(Object.keys(systemOrderPayload.templates[0]).sort(), ['sortOrder', 'templateKey']);
    assert.equal(systemOrderPayload.templates[0].templateKey, created.templateKey);
    const systemOrdered = await read('system');
    const orderedCreated = systemOrdered.stored.templates.find(item => item.id === created.id);
    assert.equal(orderedCreated.sortOrder, systemOrderPayload.templates[0].sortOrder);
    assert.deepEqual({ ...orderedCreated, sortOrder: created.sortOrder, version: created.version }, created,
      'System ordering preserves exact identity, ownership, enabled and raw text values.');
    for (const original of saved.stored.templates.filter(item => item.id !== created.id)) {
      assert.deepEqual(systemOrdered.stored.templates.find(item => item.id === original.id), original,
        'System ordering preserves every sibling row, raw CRLF/whitespace value and rowversion.');
    }
    await reload('system'); await page.locator('#settings-nav-templates').click();
    assert.deepEqual(await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey)), systemKeysAfter,
      'The System Move order survives the SQL-backed GET and full reload.');
    report.systemTemplateOrderingRoundTrip = true;

    // Corrupted retained identity cannot upsert an existing source record.
    const beforeRejected = await read('system');
    await page.locator('#add-email-template').click();
    const invalid = page.locator('#email-templates-editor [data-template-id=""]');
    await invalid.evaluate(node => { node.dataset.templateKey = 'rejection:template_1'; });
    await invalid.locator('.template-subject').fill('Duplicate');
    await invalid.locator('.template-body').fill('Duplicate body');
    await page.locator('#email-template-from-name').fill('Must roll back');
    await invalid.locator('[data-template-toggle]').click();
    await page.locator('#settings-nav-workflow').click();
    const duplicatePosts = [];
    const trackDuplicatePost = request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/settings') duplicatePosts.push(request.url());
    };
    page.on('request', trackDuplicatePost);
    try {
      await page.locator('#settings-save').click();
      await invalid.locator('[role="alert"]').filter({ hasText: /already used/ }).waitFor();
      assert.equal(await page.locator('#settings-templates').isVisible(), true);
      assert.equal(await invalid.locator('.template-display-name').evaluate(node => node === document.activeElement), true);
      assert.equal(await page.locator('#email-template-from-name').inputValue(), 'Must roll back', 'Validation preserves the sender draft.');
      assert.deepEqual(duplicatePosts, [], 'Duplicate UI validation runs before any mutation request.');
    } finally { page.off('request', trackDuplicatePost); }
    assert.deepEqual(await read('system'), beforeRejected);
    report.duplicateTemplateKeyBlocked = true;
    await reload('system');
    await page.locator('#settings-nav-templates').click();

    // Keep the backend's repeated-target atomic rollback contract independent
    // of the UI's new-row protection. A single-key API operation remains an upsert.
    const rejectedResponse = await page.evaluate(async ({ version, key }) => {
      const session = await (await fetch('/api/asap/staff/session')).json();
      const response = await fetch('/api/asap/staff/settings', { method: 'POST', headers: {
        'Content-Type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken
      }, body: JSON.stringify({ orgId: 'system', version, email: { fromName: 'Must roll back' }, templates: [
        { templateKey: key, subject: 'First repeated target', body: 'First body' },
        { templateKey: key, subject: 'Second repeated target', body: 'Second body' }
      ] }) });
      return { status: response.status, body: await response.text() };
    }, { version: beforeRejected.version, key: 'rejection:template_1' });
    assert.equal(rejectedResponse.status, 400, rejectedResponse.body);
    assert.deepEqual(await read('system'), beforeRejected, 'Repeated HTTP targets roll back source and sibling sender changes atomically.');
    report.duplicateTemplateHttpRollback = true;

    const submission = page.locator('#email-submit-subject');
    const purchase = page.locator('#email-purchase-approved-body');
    const submissionBefore = await submission.inputValue();
    const purchaseBefore = await purchase.inputValue();
    await selectRange(submission, 0);
    await selectRange(purchase, 0);
    await submission.dispatchEvent('select');
    await submission.dispatchEvent('keyup');
    await titleButton().focus();
    const staleTitle = await titleButton().elementHandle();
    await page.keyboard.press('Space');
    assert.equal(await submission.inputValue(), submissionBefore, 'Late events cannot reclaim the prior editor.');
    assert.equal(await purchase.inputValue(), `{{title}}${purchaseBefore}`);
    await page.locator('#email-template-from-name').focus();
    await staleTitle.evaluate(button => button.click());
    assert.equal(await purchase.inputValue(), `{{title}}${purchaseBefore}`, 'An unrelated field clears insertion ownership.');
    await openCard(page.locator('#email-hold-subject'));
    assert.equal(await page.locator('#email-hold-subject').locator('xpath=..').locator('.template-placeholder-trigger').isDisabled(), true);
    assert.match(await page.locator('[data-template-key="hold_placed"] .settings-template-preview-note').textContent(), /not verified/);
    await purchase.fill(Array.from({ length: 50 }, (_, index) => `Line ${index} & é`).join('\n'));
    await selectRange(purchase, 0);
    const purchaseTrigger = purchase.locator('xpath=..').locator('.template-placeholder-trigger');
    const purchasePopupId = await purchaseTrigger.getAttribute('aria-controls');
    const draftBeforeScroll = await purchase.inputValue();
    await purchase.evaluate(element => { element.scrollTop = 80; });
    await page.waitForFunction(id => document.getElementById(id)?.matches(':popover-open') === false, purchasePopupId);
    assert.equal(await purchaseTrigger.getAttribute('aria-expanded'), 'false', 'Scrolling the field dismisses its anchored picker.');
    assert.equal(await purchaseTrigger.evaluate(element => element === document.activeElement), true,
      'Field-scroll dismissal restores the trigger focus.');
    assert.equal(await purchase.inputValue(), draftBeforeScroll, 'Field scrolling never inserts into the draft.');
    await openPicker(purchase);
    await titleButton().focus();
    const scrollBefore = await purchase.evaluate(element => ({ page: scrollY, field: element.scrollTop }));
    assert.equal(scrollBefore.field, 80, 'Reopening the settled picker preserves the field scroll position.');
    await page.keyboard.press('Enter');
    assert.deepEqual(await purchase.evaluate(element => ({ page: scrollY, field: element.scrollTop })), scrollBefore,
      'Insertion restores focus without scrolling the page or multiline editor.');
    await reload('system');

    await page.locator('#settings-nav-patron').click();
    const rule = page.locator('#format-rules-editor [data-rule-code="book"]');
    await rule.locator('[data-rule-toggle]').click();
    const message = rule.locator('[data-rule-property="message"]');
    const behavior = rule.locator('[data-rule-property="messageBehavior"]');
    await behavior.selectOption('none');
    assert.equal(await message.count(), 1, 'The complete snapshot retains the hidden textarea.');
    assert.equal(await message.isVisible(), false);
    assert.equal(await message.isDisabled(), true);
    await behavior.selectOption('message');
    await message.fill('Draft conditional text & é');
    await behavior.selectOption('none');
    assert.equal(await message.isVisible(), false);
    await behavior.selectOption('ebookMessage');
    assert.equal(await message.inputValue(), 'Draft conditional text & é');
    await behavior.selectOption('none');
    const rulePayload = await saveSettings();
    assert.equal(rulePayload.formatRules.find(item => item.code === 'book').message, 'Draft conditional text & é');
    await reload('system');
    await page.locator('#settings-nav-patron').click();
    assert.equal(await message.isVisible(), false);
    assert.equal(await message.inputValue(), 'Draft conditional text & é');
    await rule.locator('[data-rule-toggle]').click();
    await behavior.selectOption('message');
    assert.equal(await message.isVisible(), true);
    await reload('system');

    await switchScope(library);
    await assertLibraryContext();
    await page.locator('#settings-nav-templates').click();
    const inherited = await row(createdKey);
    assert.equal(await inherited.locator('.template-subject').isDisabled(), false);

    await inherited.locator('.template-body').fill('Library body: ');
    await selectRange(inherited.locator('.template-body'), 14);
    await titleButton().click();
    const overridePayload = await saveSettings();
    const submitted = overridePayload.templates.find(item => item.sourceTemplateId === created.id);
    assert.equal(Object.hasOwn(submitted, 'subject'), false, 'An unchanged inherited subject must be omitted from the sparse override.');
    assert.equal(submitted.body, 'Library body: {{title}}');
    const beforeSystemEdit = await read(library);
    const sparse = beforeSystemEdit.stored.templates.find(item => item.organizationId === library && item.sourceTemplateId === created.id);
    assert.equal(sparse.subject, null);
    assert.equal(sparse.body, 'Library body: {{title}}');

    // The shortcut shares normal draft consent; cancellation leaves scope and
    // draft untouched, acceptance switches and discards only after consent.
    await openCard(inherited);
    await inherited.locator('.template-subject').fill('Unsaved library draft');
    await decideConfirmation(page, () => page.locator('#settings-switch-system').click(), dialog => dialog.dismiss());
    assert.equal(await page.locator('#settings-scope').inputValue(), library);
    assert.equal(await inherited.locator('.template-subject').inputValue(), 'Unsaved library draft');
    await decideConfirmation(page, () => page.locator('#settings-switch-system').click(), dialog => dialog.accept());
    await page.waitForFunction(() => document.getElementById('settings-scope').value === 'system' &&
      !document.getElementById('settings-form').inert);
    await page.waitForLoadState('networkidle');
    await ready('system');
    await page.locator('#settings-nav-templates').click();
    await openCard(page.locator('#email-submit-subject'));
    assert.equal(await page.locator('#email-submit-subject').isEditable(), true,
      'Returning from library inheritance must restore System built-in template editability.');
    await (await row(createdKey)).locator('.template-subject').fill('Updated System subject {{title}}');
    await saveSettings();
    const afterSystemEdit = await read(library);
    assert.deepEqual(afterSystemEdit.stored.templates.find(item => item.id === sparse.id), sparse,
      'System edits must preserve the exact sparse library row/version.');
    await switchScope(library);
    await page.locator('#settings-nav-templates').click();
    assert.equal(await (await row(createdKey)).locator('.template-subject').inputValue(), 'Updated System subject {{title}}');
    assert.equal(await (await row(createdKey)).locator('.template-body').inputValue(), 'Library body: {{title}}');
    const badgeCounts = await page.locator('.settings-scope-badge').count();
    await switchScope('system'); await switchScope(library);
    assert.equal(await page.locator('.settings-scope-badge').count(), badgeCounts, 'Repeated scope changes must not duplicate indicators.');
    report.campaign2SystemAndInheritance = true;
  }
  await page.locator('#settings-nav-templates').click();
  // Existing API identities can resemble built-in keys. Create those fixtures
  // through the owning API; routine editors allocate their own immutable keys.
  const compatibilityBaseline = await read(library);
  const compatibilityTemplates = ['purchase_approved', 'suggestion_submitted', 'rejected'].map((key, index) => ({
    sortOrder: Math.max(0, ...compatibilityBaseline.stored.templates.map(template => template.sortOrder || 0)) + (index + 1) * 10,
    templateKey: key, isCustom: true, displayName: `Custom ${key}`, subject: `Manual ${key}`,
    body: `Unchanged manual {{name}} content for ${key} & é`
  }));
  const compatibilityResponse = await page.evaluate(async ({ library, version, templates }) => {
    const session = await (await fetch('/api/asap/staff/session')).json();
    const response = await fetch('/api/asap/staff/settings', { method: 'POST', headers: {
      'Content-Type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken
    }, body: JSON.stringify({ orgId: library, version, templates }) });
    return { status: response.status, body: await response.text() };
  }, { library, version: compatibilityBaseline.version, templates: compatibilityTemplates });
  assert.equal(compatibilityResponse.status, 200, compatibilityResponse.body);
  await reload(library); await page.locator('#settings-nav-templates').click();
  const beforeCustomSave = await read(library);
  const builtinBeforeCustomSave = await page.locator('#settings-template-editor').evaluate(container =>
    [...container.querySelectorAll('.settings-template[data-template-audience] [data-template-field]')]
      .map(input => ({ id: input.id, value: input.value, disabled: input.disabled })));
  const customBodies = new Map();
  for (const key of ['purchase_approved', 'suggestion_submitted', 'rejected']) {
    const custom = await row(key);
    const body = `Unchanged manual {{name}} content for ${key} & é`;
    customBodies.set(key, body);
    await custom.locator('.template-body').focus();
    assert.equal(await custom.getAttribute('data-template-kind'), 'custom');
    assert.equal(await custom.locator('.template-placeholder-trigger').first().isDisabled(), true,
      `A library-custom ${key} key cannot claim its built-in sending capabilities.`);
    assert.match(await custom.locator('.settings-template-preview-note').textContent(), /not verified/);
    assert.equal(await custom.locator('.template-body').inputValue(), body);
  }
  await page.locator('#add-email-template').click();
  const upper = page.locator('#email-templates-editor [data-template-id=""]').last();
  const rejectionKey = await upper.getAttribute('data-template-key');
  assert.match(rejectionKey, /^rejection:custom_template_[a-f0-9]{32}$/);
  await upper.locator('.template-display-name').fill('Named rejection');
  await upper.locator('.template-subject').fill('Uppercase rejection {{title}}');
  await upper.locator('.template-body').fill('Upper & ');
  await selectRange(upper.locator('.template-body'), 8);
  assert.deepEqual(await tokenKeys(), ['title']);
  assert.match(await upper.locator('.settings-template-preview-note').textContent(), /including timeouts/);
  await titleButton().focus();
  await page.keyboard.press('Enter');
  customBodies.set(rejectionKey, 'Upper & {{title}}');
  assert.equal(await upper.locator('.template-body').inputValue(), customBodies.get(rejectionKey));
  const customPayload = await saveSettings();
  for (const [key, body] of customBodies) {
    if (key !== rejectionKey) continue;
    const submitted = customPayload.templates.find(item => item.templateKey === key && item.isCustom);
    assert.ok(submitted, `The custom ${key} row is submitted independently of built-in lineage.`);
    assert.equal(submitted.sourceTemplateId ?? null, null);
    assert.equal(submitted.body, body);
  }
  for (const patch of customPayload.templates.filter(item => !item.isCustom && item.sourceTemplateId)) {
    const original = beforeCustomSave.stored.templates.find(item => item.organizationId === library &&
      item.sourceTemplateId === patch.sourceTemplateId && !item.isCustom);
    assert.ok(original, 'Adding custom rows cannot create an unrelated lineage override.');
    assert.notEqual(patch.reset, true);
    for (const field of ['subject', 'body', 'displayName', 'enabled']) {
      if (!Object.hasOwn(patch, field)) continue;
      assert.notEqual(original[field], null, `An inherited ${field} must not be materialized by adding another row.`);
      assert.equal(patch[field], original[field], `Only the unchanged explicit ${field} may be reasserted.`);
    }
  }
  await reload(library);
  await page.locator('#settings-nav-templates').click();
  let customSaved = await read(library);
  for (const original of beforeCustomSave.stored.templates) {
    assert.deepEqual(customSaved.stored.templates.find(item => item.id === original.id), original,
      'Custom template saves preserve every preexisting raw row, sparse field and rowversion.');
  }
  assert.deepEqual(await page.locator('#settings-template-editor').evaluate(container =>
    [...container.querySelectorAll('.settings-template[data-template-audience] [data-template-field]')]
      .map(input => ({ id: input.id, value: input.value, disabled: input.disabled }))),
    builtinBeforeCustomSave, 'Custom built-in-looking keys stay independent of the actual built-in editors.');
  for (const [key, body] of customBodies) {
    const stored = customSaved.stored.templates.find(item => item.templateKey === key && item.organizationId === library && item.isCustom);
    assert.ok(stored, `The real saved custom ${key} row survives reload.`);
    assert.equal(stored.sourceTemplateId ?? null, null);
    assert.equal(stored.body, body);
    const custom = await row(key);
    assert.equal(await custom.getAttribute('data-template-kind'), 'custom');
    assert.equal(await custom.locator('.template-key').count(), 0);
    assert.equal(await custom.getAttribute('data-template-key'), key);
    if (key === rejectionKey) {
      await openPicker(custom.locator('.template-body'));
      assert.deepEqual(await tokenKeys(), ['title']);
      await page.keyboard.press('Escape');
    } else {
      assert.equal(await custom.locator('.template-placeholder-trigger').first().isDisabled(), true,
        'Saved custom identity cannot borrow built-in capabilities.');
    }
    assert.equal(await custom.locator('.template-body').inputValue(), body);
  }
  report.campaign2CustomUsageCapabilities = true;
  // Establish exact raw CRLF text through the existing API. The native textarea
  // displays LF, but sibling ordering/deletion must never rewrite this record.
  const rawCustomKey = 'purchase_approved';
  const rawCustomBody = '  Raw line one\r\nRaw line two {{name}} & é  ';
  const rawCustomResponse = await page.evaluate(async ({ library, version, key, body }) => {
    const session = await (await fetch('/api/asap/staff/session')).json();
    const response = await fetch('/api/asap/staff/settings', { method: 'POST', headers: {
      'Content-Type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken
    }, body: JSON.stringify({ orgId: library, version, templates: [{ templateKey: key, isCustom: true, body }] }) });
    return { status: response.status, body: await response.text() };
  }, { library, version: customSaved.version, key: rawCustomKey, body: rawCustomBody });
  assert.equal(rawCustomResponse.status, 200, rawCustomResponse.body);
  customSaved = await read(library);
  assert.equal(customSaved.stored.templates.find(item => item.organizationId === library && item.templateKey === rawCustomKey).body, rawCustomBody);
  await reload(library); await page.locator('#settings-nav-templates').click();
  assert.equal(await (await row(rawCustomKey)).locator('.template-body').inputValue(), rawCustomBody.replace(/\r\n/g, '\n'));
  const libraryOrderBefore = await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey));
  await (await row('rejected')).locator('[aria-label="Move up"]').click();
  const libraryOrderAfter = await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey));
  assert.notDeepEqual(libraryOrderAfter, libraryOrderBefore);
  const libraryOrderPayload = await saveSettings();
  assert.equal(libraryOrderPayload.templates.length, 1, 'A Library move writes only its custom row, without creating lineage overrides.');
  assert.deepEqual(Object.keys(libraryOrderPayload.templates[0]).sort(), ['isCustom', 'sortOrder', 'templateKey']);
  assert.equal(libraryOrderPayload.templates[0].isCustom, true);
  assert.equal(libraryOrderPayload.templates[0].templateKey, 'rejected');
  const orderedLibrary = await read(library);
  for (const original of customSaved.stored.templates) {
    const current = orderedLibrary.stored.templates.find(item => item.id === original.id);
    if (original.organizationId === library && original.templateKey === 'rejected' && original.isCustom) {
      assert.equal(current.sortOrder, libraryOrderPayload.templates[0].sortOrder);
      assert.deepEqual({ ...current, sortOrder: original.sortOrder, version: original.version }, original);
    } else assert.deepEqual(current, original, 'Library ordering preserves all inherited/custom siblings, raw content and rowversions.');
  }
  await reload(library); await page.locator('#settings-nav-templates').click();
  assert.deepEqual(await page.locator('#email-templates-editor [data-domain-row]').evaluateAll(nodes => nodes.map(node => node.dataset.templateKey)), libraryOrderAfter,
    'Library custom ordering survives the SQL-backed GET and full reload.');
  customSaved = orderedLibrary;
  report.customTemplateOrderingRoundTrip = true;
  const deletedCustom = customSaved.stored.templates.find(item =>
    item.templateKey === 'rejected' && item.organizationId === library && item.isCustom);
  await (await row('rejected')).locator('[aria-label="Delete"]').click();
  assert.equal(await page.evaluate(() => document.activeElement?.matches('[data-template-toggle], #add-email-template')), true);
  const deletePayload = await saveSettings();
  assert.deepEqual(deletePayload.templates.find(item => item.templateKey === 'rejected' && item.isCustom),
    { templateKey: 'rejected', isCustom: true, reset: true }, 'Delete submits the existing custom reset operation.');
  await reload(library); await page.locator('#settings-nav-templates').click();
  const afterDelete = await read(library);
  assert.equal(afterDelete.stored.templates.some(item => item.id === deletedCustom.id), false,
    'The saved custom template is deleted from the real SQL-backed settings snapshot.');
  for (const original of customSaved.stored.templates.filter(item => item.id !== deletedCustom.id)) {
    assert.deepEqual(afterDelete.stored.templates.find(item => item.id === original.id), original,
      'Deleting a custom template preserves every sibling row, value and rowversion.');
  }
  report.customTemplateDeleteRoundTrip = true;
  assert.equal(afterDelete.stored.templates.find(item => item.organizationId === library && item.templateKey === rawCustomKey).body, rawCustomBody);
  report.templateRawTextPreserved = true;

  const source = customSaved.stored.templates.find(item =>
    item.organizationId === '1' && item.templateKey === 'rejection:template_3');
  let returning = await row(source.templateKey);

  await returning.locator('.template-display-name').fill('Library-owned display override');
  await returning.locator('.template-subject').fill('Library subject override');
  await returning.locator('.template-body').fill('Library body override');
  await returning.locator('.template-enabled').uncheck();
  await saveSettings();
  await reload(library);
  await page.locator('#settings-nav-templates').click();
  for (const property of ['subject', 'body', 'displayName', 'enabled']) {
    const before = await read(library);
    const priorOverride = before.stored.templates.find(item =>
      item.organizationId === library && item.sourceTemplateId === source.id);
    returning = await row(source.templateKey);
    const controlClass = { subject: 'subject', body: 'body', displayName: 'display-name', enabled: 'enabled' }[property];
    const control = returning.locator(`.template-${controlClass}`);
    if (property === 'enabled') await control.setChecked(source.enabled);
    else await control.fill(source[property]);
    const payload = await saveSettings();
    const submitted = payload.templates.find(item => item.sourceTemplateId === source.id);
    assert.equal(submitted?.[property], source[property], `The ${property} edit back to System must reach the actual POST.`);
    for (const sibling of ['subject', 'body', 'displayName', 'enabled'].filter(key => key !== property)) {
      assert.equal(Object.hasOwn(submitted, sibling), false, `An untouched ${sibling} must remain omitted.`);
    }
    await reload(library);
    await page.locator('#settings-nav-templates').click();
    const after = await read(library);
    const savedOverride = after.stored.templates.find(item => item.id === priorOverride.id);
    assert.equal(savedOverride[property], source[property], `${property} must survive real SQL and editor reload.`);
    for (const sibling of ['subject', 'body', 'displayName', 'enabled'].filter(key => key !== property)) {
      assert.equal(savedOverride[sibling], priorOverride[sibling], `Saving ${property} must preserve saved ${sibling}.`);
    }
  }
  report.namedOverrideEditsBackToSystem = true;
  await runPatronTextRefinements(page, axeSource, args, report, saveSettings, ready, read, reload, switchScope);
  await scan(page, axeSource, args.artifactRoot, report, 'campaign2-desktop');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan(page, axeSource, args.artifactRoot, report, 'campaign2-mobile');
  await page.setViewportSize({ width: 640, height: 450 });
  await scan(page, axeSource, args.artifactRoot, report, 'campaign2-640-reflow');
  await page.emulateMedia({ forcedColors: 'active' });
  await scan(page, axeSource, args.artifactRoot, report, 'campaign2-forced-colors');
}

async function runCommonAuthorsOptions(page, axeSource, args, report, saveSettings, ready) {
  const libraryScope = '2';
  const readSettings = scope => page.evaluate(async scope => {
    const response = await fetch(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`);
    if (!response.ok) throw new Error(`Settings GET ${response.status}`);
    return response.json();
  }, scope);
  const readEditor = () => page.evaluate(() => {
    const enabled = document.getElementById('common-authors-enabled');
    const override = enabled.closest('[data-setting-section][data-setting-key]')?.querySelector('.settings-inheritance-status');
    return {
      enabled: enabled.checked,
      enabledOverridden: override ? override.textContent.startsWith('Library setting') : null,
      label: document.getElementById('common-authors-label').value,
      help: document.getElementById('common-authors-help').value,
      message: document.getElementById('common-authors-message').value,
      creatorsUseSystem: document.getElementById('settings-scope').value === 'system' || document.getElementById('common-creators-inherit-field').textContent.includes('Using system value'),
      creators: [...document.querySelectorAll('#common-creators-editor [data-domain-row] input')].map(input => input.value)
    };
  });
  const enabled = page.locator('#common-authors-enabled');
  const options = page.locator('#common-authors-options');
  const enabledOverride = page.locator(
    '[data-setting-section="workflow"][data-setting-key="commonAuthorsEnabled"]');
  const creatorStatus = page.locator('#common-creators-inherit-field');
  const creatorsInherited = async () => !await librarySetting(creatorStatus);
  const resetCreators = () => useSystemValue(page, creatorStatus, true);
  const pinCreators = async () => {
    const input = rows.first().locator('input');
    if (await rows.count()) { const value = await input.inputValue(); await input.fill(value + ' draft'); await input.fill(value); }
    else { await page.locator('#add-common-creator').click(); await rows.last().locator('[data-row-action="delete"]').click(); }
  };
  const rows = page.locator('#common-creators-editor [data-domain-row]');
  const creatorValues = () => rows.locator('input').evaluateAll(inputs => inputs.map(input => input.value));
  const creatorGeometry = () => page.evaluate(() => {
    const rect = node => {
      const bounds = node.getBoundingClientRect();
      return { left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom,
        width: bounds.width, height: bounds.height };
    };
    const editor = document.getElementById('common-creators-editor');
    return { viewport: { width: innerWidth, height: innerHeight }, scrollWidth: document.documentElement.scrollWidth,
      editor: rect(editor), rows: [...editor.querySelectorAll('.settings-creator-row')].map(row => ({
        row: rect(row), field: rect(row.querySelector('.settings-domain-field')),
        label: rect(row.querySelector('.settings-domain-field span')), input: rect(row.querySelector('input')),
        actions: rect(row.querySelector('.settings-row-actions')),
        buttons: [...row.querySelectorAll('.settings-row-actions button')].map(button => ({
          label: button.getAttribute('aria-label'), disabled: button.disabled, ...rect(button)
        }))
      })) };
  });
  const assertCreatorContainment = (geometry, name) => {
    assert.ok(geometry.scrollWidth <= geometry.viewport.width, `${name}: no page-level horizontal overflow: ${JSON.stringify(geometry)}`);
    for (const row of geometry.rows) {
      assert.ok(row.label.bottom <= row.input.top + 1 && Math.abs(row.label.left - row.input.left) < 1 &&
        Math.abs(row.label.right - row.input.right) < 1,
      `${name}: Creator label sits above and aligns with its input: ${JSON.stringify({ row })}`);
      for (const part of [row.field, row.input, row.actions, ...row.buttons]) {
        assert.ok(part.left >= row.row.left - 1 && part.right <= row.row.right + 1,
          `${name}: creator content stays inside its row: ${JSON.stringify({ row, part })}`);
      }
      assert.ok(row.input.width > 0 && row.actions.width > 0, `${name}: creator input and actions remain visible.`);
    }
  };
  const setEnabledByKeyboard = async value => {
    if (await enabled.isChecked() !== value) {
      await enabled.focus();
      await enabled.press('Space');
    }
  };
  const setCreatorRows = async values => {
    while (await rows.count() > values.length) {
      await rows.last().locator('.settings-row-actions button[aria-label="Delete"]').click();
    }
    while (await rows.count() < values.length) await page.locator('#add-common-creator').click();
    for (const [index, value] of values.entries()) await rows.nth(index).locator('input').fill(value);
  };
  const waitForScopeLoad = async (scope, responsePromise) => {
    await ready(scope);
    const response = await responsePromise;
    assert.equal(response.status(), 200, `Settings load for ${scope} succeeds before creator interactions.`);
    await page.waitForFunction(expectedScope => {
      const refresh = document.getElementById('settings-refresh');
      const message = document.getElementById('settings-message');
      return document.getElementById('settings-scope')?.value === expectedScope && refresh && !refresh.disabled &&
        !message?.classList.contains('error');
    }, scope);
  };
  const switchScope = async scope => {
    const selectedScope = page.locator('#settings-scope');
    if (await selectedScope.inputValue() !== scope) {
      const settingsReload = page.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' && url.pathname === '/api/asap/staff/settings' &&
          url.searchParams.get('orgId') === scope;
      });
      if (await page.locator('#settings-save').isEnabled()) {
        await decideConfirmation(page, () => selectedScope.selectOption(scope), dialog => dialog.accept());
      } else {
        await selectedScope.selectOption(scope);
      }
      await waitForScopeLoad(scope, settingsReload);
    }
    await page.locator('#settings-nav-workflow').click();
    await page.locator('#settings-workflow').waitFor({ state: 'visible' });
  };
  const ensureLibraryParentEditable = async () => {
    assert.equal(await enabled.isEnabled(), true);
  };
  const saveIfDirty = async () => {
    if (await page.locator('#settings-save').isEnabled()) return saveSettings();
    return null;
  };
  const assertOptionsDisabled = async label => {
    assert.equal(await options.evaluate(node => node.disabled), true, `${label}: the native fieldset is disabled.`);
    assert.equal(await options.evaluate(node => node.matches(':disabled')), true, `${label}: the fieldset matches :disabled.`);
    assert.equal(await page.locator('#common-authors-label').isDisabled(), true,
      `${label}: scalar controls inherit the fieldset disabled state.`);
    assert.equal(await page.locator('#add-common-creator').isDisabled(), true,
      `${label}: creator actions inherit the fieldset disabled state.`);
  };
  const reloadScope = async scope => {
    const settingsReload = page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === 'GET' && url.pathname === '/api/asap/staff/settings' &&
        url.searchParams.get('orgId') === scope;
    });
    await page.reload({ waitUntil: 'networkidle' });
    await waitForScopeLoad(scope, settingsReload);
    await page.locator('#settings-nav-workflow').click();
    await page.locator('#settings-workflow').waitFor({ state: 'visible' });
  };
  const restoreSystem = async snapshot => {
    await switchScope('system');
    await setEnabledByKeyboard(true);
    for (const [id, value] of [
      ['common-authors-label', snapshot.label], ['common-authors-help', snapshot.help],
      ['common-authors-message', snapshot.message]
    ]) await page.locator(`#${id}`).fill(value);
    await setCreatorRows(snapshot.creators);
    await setEnabledByKeyboard(snapshot.enabled);
    await saveIfDirty();
    await reloadScope('system');
    assert.deepEqual(await readEditor(), snapshot, 'System common creators are restored after the browser journey.');
  };
  const restoreLibrary = async snapshot => {
    await switchScope(libraryScope);
    await ensureLibraryParentEditable();
    await setEnabledByKeyboard(true);
    if (snapshot.creatorsUseSystem) {
      if (!await creatorsInherited()) await resetCreators();
    } else {
      if (await creatorsInherited()) await pinCreators();
      await setCreatorRows(snapshot.creators);
    }
    if (snapshot.enabledOverridden) {
      if (!await librarySetting(enabledOverride)) { await setEnabledByKeyboard(!snapshot.enabled); }
      await setEnabledByKeyboard(snapshot.enabled);
    } else if (await librarySetting(enabledOverride)) {
      await useSystemValue(page, enabledOverride);
    }
    await saveIfDirty();
    await reloadScope(libraryScope);
    assert.deepEqual(await readEditor(), snapshot, 'Library common creators and their list ownership are restored.');
  };

  await switchScope(libraryScope);
  const librarySnapshot = await readEditor();
  await switchScope('system');
  const systemSnapshot = await readEditor();
  await switchScope(libraryScope);
  let journeyError = null;
  try {
    await switchScope('system');
    const systemInitially = await readEditor();
    assert.equal(await options.evaluate(node => node.hidden), !systemInitially.enabled);
    if (!systemInitially.enabled) await setEnabledByKeyboard(true);
    assert.equal(await options.isVisible(), true, 'Enabling Show common creators reveals the complete options region.');
    const systemDraft = {
      label: 'Collapsed feature label browser draft',
      help: 'Collapsed feature help browser draft',
      message: 'Collapsed feature message browser draft'
    };
    await page.locator('#common-authors-label').fill(systemDraft.label);
    await page.locator('#common-authors-help').fill(systemDraft.help);
    await page.locator('#common-authors-message').fill(systemDraft.message);
    const creatorBaseRows = [...systemInitially.creators];
    while (creatorBaseRows.length < 2) creatorBaseRows.push(`Existing creator ${creatorBaseRows.length + 1} ${args.identity.staffId}`);
    await setCreatorRows(creatorBaseRows);
    const systemCreator = `Long common creator name ${args.identity.staffId}: ${'extended catalog label '.repeat(8).trimEnd()}`;
    const removableCreator = `Movable creator draft ${args.identity.staffId}`;
    await page.locator('#add-common-creator').click();
    let addedRow = rows.last();
    assert.equal(await addedRow.locator('input').evaluate(input => input === document.activeElement), true,
      'Adding a creator moves focus to its new Creator input.');
    assert.equal(await addedRow.getByLabel('Creator', { exact: true }).count(), 1);
    await addedRow.locator('input').fill(systemCreator);
    await page.locator('#add-common-creator').click();
    addedRow = rows.last();
    assert.equal(await addedRow.locator('input').evaluate(input => input === document.activeElement), true);
    await addedRow.locator('input').fill(removableCreator);
    const creatorOrderBeforeMoveDown = [...creatorBaseRows, systemCreator, removableCreator];
    await rows.nth(1).getByRole('button', { name: 'Move down', exact: true }).click();
    assert.deepEqual(await creatorValues(), [creatorBaseRows[0], systemCreator, creatorBaseRows[1], removableCreator],
      'Move down reorders an interior creator row in the complete list draft.');
    assert.equal(await rows.nth(2).locator('[data-row-action="down"]')
      .evaluate(button => button === document.activeElement), true,
    'Move down retains focus on the moved row action at its interior destination.');
    await rows.nth(2).getByRole('button', { name: 'Move up', exact: true }).click();
    assert.deepEqual(await creatorValues(), creatorOrderBeforeMoveDown,
      'Moving the creator back restores the list order before the remaining action checks.');
    const moveFrom = await rows.count() - 1;
    await rows.nth(moveFrom).getByRole('button', { name: 'Move up', exact: true }).click();
    const movedValues = await creatorValues();
    assert.equal(movedValues[moveFrom - 1], removableCreator, 'Move up reorders the complete list draft.');
    assert.equal(await rows.nth(moveFrom - 1).getByRole('button', { name: 'Move up', exact: true })
      .evaluate(button => button === document.activeElement), true, 'Move up retains focus on its moved row action.');
    await rows.nth(moveFrom - 1).getByRole('button', { name: 'Delete', exact: true }).click();
    const expectedSystemRows = [...creatorBaseRows, systemCreator];
    assert.deepEqual(await creatorValues(), expectedSystemRows, 'Delete removes only its target from the whole-list draft.');
    assert.equal(await rows.nth(moveFrom - 1).locator('input').evaluate(input => input === document.activeElement), true,
      'Delete moves focus to the next surviving Creator input.');
    assert.equal(await rows.first().getByRole('button', { name: 'Move up', exact: true }).isDisabled(), true,
      'The first creator cannot move up.');
    assert.equal(await rows.last().getByRole('button', { name: 'Move down', exact: true }).isDisabled(), true,
      'The last creator cannot move down.');
    const firstCreatorInput = rows.first().getByLabel('Creator', { exact: true });
    await firstCreatorInput.focus(); await page.keyboard.press('Tab');
    assert.equal(await rows.first().locator('[data-row-action="down"]').evaluate(button => button === document.activeElement), true,
      'Keyboard order skips the disabled first-row action and reaches Move down.');
    assert.equal(await rows.first().locator('[data-row-action="down"]').evaluate(button => button.matches(':focus-visible')), true,
      'Keyboard navigation keeps the row action visibly focused.');
    await page.keyboard.press('Tab');
    assert.equal(await rows.first().locator('[data-row-action="delete"]').evaluate(button => button === document.activeElement), true);
    await page.keyboard.press('Tab');
    assert.equal(await rows.nth(1).locator('input').evaluate(input => input === document.activeElement), true,
      'Tab follows row actions to the next Creator input.');
    report.commonCreatorInteractions = true;

    await page.setViewportSize({ width: 1280, height: 900 });
    await rows.last().scrollIntoViewIfNeeded();
    const systemDesktopGeometry = await creatorGeometry();
    assertCreatorContainment(systemDesktopGeometry, 'System desktop');
    assert.ok(systemDesktopGeometry.rows.every(row => row.actions.left > row.input.right &&
      Math.abs(row.actions.top - row.input.top) < 1 && Math.abs(row.actions.bottom - row.input.bottom) < 1),
    `At desktop width, creator actions share the input row: ${JSON.stringify(systemDesktopGeometry)}`);
    report.commonCreatorLayouts.push({ name: 'system-desktop', ...systemDesktopGeometry });
    await page.screenshot({ path: path.join(args.artifactRoot, 'common-creators-system-desktop.png'), fullPage: true });
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-system-desktop');
    await rows.last().locator('input').focus();
    await enabled.evaluate(node => {
      node.checked = false;
      node.dispatchEvent(new Event('change', { bubbles: true }));
    });
    assert.equal(await options.isHidden(), true);
    await assertOptionsDisabled('System hidden draft');
    assert.equal(await enabled.evaluate(node => document.activeElement === node), true,
      'Hiding focused editable options returns focus to Show common creators.');
    const retainedAdd = page.locator('#add-common-creator');
    const retainedDelete = rows.last().locator('.settings-row-actions button[aria-label="Delete"]');
    await retainedAdd.evaluate(node => node.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    await retainedDelete.evaluate(node => node.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    assert.deepEqual(await rows.locator('input').evaluateAll(inputs => inputs.map(input => input.value)), expectedSystemRows,
      'Retained hidden Add/Delete handlers cannot mutate the invisible System list.');
    const systemOffPayload = await saveSettings();
    assert.equal(systemOffPayload.workflow.commonAuthorsEnabled, systemSnapshot.enabled ? false : undefined,
      'A changed System flag is submitted; an accepted false/null presentation is preserved by omission.');
    assert.equal(systemOffPayload.workflow.commonAuthorsLabel, systemDraft.label);
    assert.equal(systemOffPayload.workflow.commonAuthorsHelp, systemDraft.help);
    assert.equal(systemOffPayload.workflow.commonAuthorsMessage, systemDraft.message);
    assert.deepEqual(systemOffPayload.workflow.commonAuthorsList, expectedSystemRows);
    await reloadScope('system');
    assert.equal(await enabled.isChecked(), false);
    assert.equal(await options.isHidden(), true);
    await assertOptionsDisabled('System reloaded off state');
    assert.deepEqual(await page.locator('#common-creators-editor [data-domain-row] input').evaluateAll(inputs => inputs.map(input => input.value)), expectedSystemRows,
      'The hidden System scalar and list drafts survive accepted save and full reload.');
    await setEnabledByKeyboard(true);
    await rows.first().locator('input').fill(`Discarded creator draft ${args.identity.staffId}`);
    await page.locator('#add-common-creator').click();
    await rows.last().locator('input').fill(`Discarded added creator ${args.identity.staffId}`);
    assert.equal(await page.locator('#settings-save').isEnabled(), true,
      'Creator edits and additions reach the existing global Save action.');
    const discardReload = page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === 'GET' && url.pathname === '/api/asap/staff/settings' &&
        url.searchParams.get('orgId') === 'system';
    });
    await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
    const discardResponse = await discardReload;
    assert.equal(discardResponse.status(), 200, 'Global Discard reloads accepted System settings successfully.');
    await page.waitForFunction(expectedRows => {
      const refresh = document.getElementById('settings-refresh');
      const save = document.getElementById('settings-save');
      const message = document.getElementById('settings-message');
      const creators = [...document.querySelectorAll('#common-creators-editor [data-domain-row] input')]
        .map(input => input.value);
      return refresh && !refresh.disabled && save?.disabled && !message?.classList.contains('error') &&
        JSON.stringify(creators) === JSON.stringify(expectedRows);
    }, expectedSystemRows);
    assert.deepEqual(await creatorValues(), expectedSystemRows,
      'Global Discard restores the accepted whole creator list and removes the unsaved row.');
    report.commonCreatorDiscard = true;
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-system-mobile-off');
    await setEnabledByKeyboard(true);
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-system-mobile-on');
    const mobileGeometry = await creatorGeometry();
    assertCreatorContainment(mobileGeometry, 'System mobile');
    assert.ok(mobileGeometry.rows.every(row => row.actions.top >= row.input.top - 1),
      `Mobile rows may reflow below their flexible Creator input: ${JSON.stringify(mobileGeometry)}`);
    report.commonCreatorLayouts.push({ name: 'system-mobile', ...mobileGeometry });
    await page.screenshot({ path: path.join(args.artifactRoot, 'common-creators-system-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1280, height: 900 });

    await switchScope(libraryScope);
    const libraryInitially = await readEditor();
    const systemConfig = await readSettings('system');
    const systemEnabled = systemConfig.stored?.configuredSystem?.workflow?.commonAuthorsEnabled === true;
    assert.equal(await enabled.isEnabled(), true);
    await setEnabledByKeyboard(true);
    assert.equal(await options.isVisible(), true);
    if (!await creatorsInherited()) await resetCreators();
    const inheritedSystemCreators = (systemConfig.stored?.configuredSystem?.commonCreators?.values || [])
      .map(creator => typeof creator === 'string' ? creator : creator.value);
    assert.deepEqual(await creatorValues(), inheritedSystemCreators,
      'Use system renders the whole accepted System creator list in Library context.');
    assert.equal(await rows.first().locator('input').isDisabled(), false,
      'Inherited creator inputs remain disabled.');
    assert.equal(await rows.first().getByRole('button', { name: 'Move down', exact: true }).isDisabled(), false,
      'Inherited row actions remain disabled.');
    assert.equal(await page.locator('#add-common-creator').isDisabled(), false,
      'The inherited Library list can be edited directly.');
    await page.setViewportSize({ width: 1280, height: 900 });
    const inheritedGeometry = await creatorGeometry();
    assertCreatorContainment(inheritedGeometry, 'Library inherited desktop');
    assert.ok(inheritedGeometry.rows.every(row => row.actions.left > row.input.right &&
      Math.abs(row.actions.top - row.input.top) < 1),
    `An inherited list keeps the compact row layout: ${JSON.stringify(inheritedGeometry)}`);
    report.commonCreatorLayouts.push({ name: 'library-inherited-desktop', ...inheritedGeometry });
    await page.screenshot({ path: path.join(args.artifactRoot, 'common-creators-library-inherited-desktop.png'), fullPage: true });
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-library-inherited-desktop');
    assert.equal(await page.locator('#add-common-creator').isEnabled(), true);
    const libraryCreator = `Collapsed Library creator ${args.identity.staffId}`;
    await page.locator('#add-common-creator').click();
    await rows.last().locator('input').fill(libraryCreator);
    const libraryDraftRows = await rows.locator('input').evaluateAll(inputs => inputs.map(input => input.value));
    const libraryOwnedGeometry = await creatorGeometry();
    assertCreatorContainment(libraryOwnedGeometry, 'Library owned desktop');
    assert.ok(libraryOwnedGeometry.rows.every(row => row.actions.left > row.input.right &&
      Math.abs(row.actions.top - row.input.top) < 1),
    `A library-owned list keeps the compact row layout: ${JSON.stringify(libraryOwnedGeometry)}`);
    report.commonCreatorLayouts.push({ name: 'library-owned-desktop', ...libraryOwnedGeometry });
    await page.screenshot({ path: path.join(args.artifactRoot, 'common-creators-library-owned-desktop.png'), fullPage: true });
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-library-owned-desktop');
    await rows.last().locator('input').focus();
    await enabled.evaluate(node => {
      node.checked = false;
      node.dispatchEvent(new Event('change', { bubbles: true }));
    });
    assert.equal(await options.isHidden(), true);
    await assertOptionsDisabled('Library hidden draft');
    assert.equal(await enabled.evaluate(node => document.activeElement === node), true);
    const useSystemBefore = await creatorsInherited();
    await creatorStatus.locator('.settings-use-system').evaluate(node => node.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    assert.equal(await creatorsInherited(), useSystemBefore,
      'A hidden Use system list event cannot change Library list ownership.');
    const libraryOffPayload = await saveSettings();
    assert.equal(libraryOffPayload.workflow.commonAuthorsEnabled, false);
    assert.deepEqual(libraryOffPayload.workflow.commonAuthorsList, libraryDraftRows);
    await reloadScope(libraryScope);
    assert.equal(await enabled.isChecked(), false);
    assert.equal(await librarySetting(enabledOverride), true);
    assert.equal(await options.isHidden(), true);
    assert.equal(await creatorsInherited(), false);
    assert.deepEqual(await rows.locator('input').evaluateAll(inputs => inputs.map(input => input.value)), libraryDraftRows,
      'The hidden Library creator list and its override survive accepted save and reload.');
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-library-mobile-off');

    await useSystemValue(page, enabledOverride);
    assert.equal(await enabled.isChecked(), systemEnabled,
      'Resetting the Library feature override displays the authoritative System flag.');
    assert.equal(await enabled.isDisabled(), false);
    assert.equal(await options.isHidden(), !systemEnabled,
      'Inherited true reveals options, while inherited false keeps them collapsed.');
    await setEnabledByKeyboard(false);
    assert.equal(await enabled.isChecked(), false, 'A direct false edit prepares the local feature value.');
    assert.equal(await options.isHidden(), true);
    await setEnabledByKeyboard(true);
    await scan(page, axeSource, args.artifactRoot, report, 'common-creators-library-mobile-on');
    await page.setViewportSize({ width: 1280, height: 900 });
    await restoreSystem(systemSnapshot);
    report.systemCommonAuthorsRoundTrip = true;
    await restoreLibrary(librarySnapshot);
    report.libraryCommonAuthorsRoundTrip = true;
  } catch (error) {
    journeyError = error;
  }
  const cleanupErrors = [];
  try { await restoreSystem(systemSnapshot); } catch (error) { cleanupErrors.push(error); }
  try { await restoreLibrary(librarySnapshot); } catch (error) { cleanupErrors.push(error); }
  const errors = [journeyError, ...cleanupErrors].filter(Boolean);
  if (errors.length === 1) throw errors[0];
  if (errors.length > 1) throw new AggregateError(errors, 'Common creator journey and restoration failed.');
}

async function runSuggestionLimitPlaceholder(page, axeSource, args, report, saveSettings, ready, postedPayloads) {
  const message = page.locator('#suggestion-limit-message');
  const trigger = page.locator('#suggestion-limit-placeholder-trigger');
  const picker = page.locator('#suggestion-limit-placeholder-picker');
  const option = page.getByRole('button', {
    name: 'Insert Next available submission date/time — {{next_available_date}}', exact: true
  });
  const retainedOption = picker.locator('[data-suggestion-placeholder="next_available_date"]');
  const override = page.locator(
    '[data-setting-section="workflow"][data-setting-key="suggestionLimitMessage"]');
  const scope = page.locator('#settings-scope');
  let librarySnapshot = null;
  const readEditor = async () => ({
    value: await message.inputValue(),
    overridden: await librarySetting(override)
  });
  const switchScope = async target => {
    if (await scope.inputValue() !== target) {
      if (await page.locator('#settings-save').isEnabled()) {
        await decideConfirmation(page, () => scope.selectOption(target), dialog => dialog.accept());
      } else {
        await scope.selectOption(target);
      }
      await ready(target);
    }
    await page.locator('#settings-nav-workflow').click();
    await page.locator('#settings-workflow').waitFor({ state: 'visible' });
  };
  const reloadScope = async target => {
    await page.reload({ waitUntil: 'networkidle' });
    await ready(target);
    await page.locator('#settings-nav-workflow').click();
    await page.locator('#settings-workflow').waitFor({ state: 'visible' });
  };
  const saveMessage = async value => {
    await message.fill(value);
    const payload = await saveSettings();
    assert.equal(payload.workflow.suggestionLimitMessage, value,
      'The current scoped Workflow message is submitted through the ordinary settings payload.');
    await reloadScope(await scope.inputValue());
    assert.equal(await message.inputValue(), value, 'The accepted Workflow message survives a full reload.');
  };
  const trackInputEvents = () => message.evaluate(node => {
    window.__suggestionLimitInputTypes = [];
    node.addEventListener('input', event => window.__suggestionLimitInputTypes.push(event.inputType));
  });

  await switchScope('system');
  const systemSnapshot = await readEditor();
  await switchScope('2');
  librarySnapshot = await readEditor();
  await switchScope('system');
  const postsBeforeOpen = postedPayloads.length;
  let testError = null;
  try {
    assert.equal(await page.locator('#settings-save').isDisabled(), true,
      'The accepted System message starts with a clean save bar.');
    await trigger.focus();
    await trigger.press('Enter');
    await option.waitFor({ state: 'visible' });
    assert.equal(await trigger.getAttribute('aria-expanded'), 'true');
    assert.equal(await picker.locator('[data-suggestion-placeholder]').count(), 1);
    assert.equal(await page.locator('#settings-save').isDisabled(), true,
      'Opening the placeholder picker does not dirty the Workflow setting.');
    assert.equal(postedPayloads.length, postsBeforeOpen, 'Opening the picker does not submit a POST.');
    await page.keyboard.press('Escape');
    assert.equal(await trigger.getAttribute('aria-expanded'), 'false');
    assert.equal(await trigger.evaluate(node => document.activeElement === node), true,
      'Escape closes the picker and returns focus to its trigger.');

    await trackInputEvents();
    const systemSource = `Prefix selected suffix ${args.identity.staffId}`;
    const inserted = `Prefix {{next_available_date}} suffix ${args.identity.staffId}`;
    assert.notEqual(inserted, systemSnapshot.value, 'The System round trip uses a distinct message draft.');
    await message.fill(systemSource);
    await message.evaluate(node => node.setSelectionRange(7, 15));
    await trigger.focus();
    await trigger.press('Enter');
    await option.waitFor({ state: 'visible' });
    await option.press('Enter');
    assert.equal(await message.inputValue(), inserted,
      'Keyboard insertion replaces only the remembered selection and preserves adjacent text.');
    assert.equal(await message.evaluate(node => document.activeElement === node), true,
      'Insertion returns focus to the message editor.');
    const adjacentText = ' adjacent';
    const adjacentSuffix = ` suffix ${args.identity.staffId}`;
    const adjacentPrefix = inserted.slice(0, -adjacentSuffix.length);
    const withAdjacentText = `${adjacentPrefix}${adjacentText}${adjacentSuffix}`;
    const readRemainingAdjacentText = async () => {
      const current = await message.inputValue();
      assert.ok(current.startsWith(adjacentPrefix) && current.endsWith(adjacentSuffix),
        `Native history preserves the inserted token and surrounding text: ${current}`);
      const remaining = current.slice(adjacentPrefix.length, current.length - adjacentSuffix.length);
      assert.ok(adjacentText.startsWith(remaining),
        `Native history retains only a leading prefix of adjacent typing: ${current}`);
      return remaining;
    };
    await page.keyboard.type(adjacentText);
    assert.equal(await message.inputValue(), withAdjacentText);
    let remainingAdjacent = await readRemainingAdjacentText();
    let undoSteps = 0;
    while (remainingAdjacent.length > 0) {
      assert.ok(undoSteps < adjacentText.length, 'Native Undo converges within the adjacent typing length.');
      await page.keyboard.press('Control+Z');
      const previousAdjacent = await readRemainingAdjacentText();
      assert.ok(previousAdjacent.length < remainingAdjacent.length,
        'Each native Undo removes adjacent text without removing or altering the token.');
      remainingAdjacent = previousAdjacent;
      undoSteps++;
    }
    assert.equal(await message.inputValue(), inserted,
      'Native Undo removes all adjacent typing before undoing the placeholder insertion.');
    await page.keyboard.press('Control+Z');
    assert.equal(await message.inputValue(), systemSource, 'Native Undo restores the replaced selection.');
    assert.deepEqual(await message.evaluate(node => [node.selectionStart, node.selectionEnd]), [7, 15],
      'Undo restores the exact selection replaced by the token.');
    await page.keyboard.press('Control+Shift+Z');
    assert.equal(await message.inputValue(), inserted, 'Native Redo restores the token and surrounding text.');
    remainingAdjacent = await readRemainingAdjacentText();
    let redoSteps = 0;
    while (remainingAdjacent.length < adjacentText.length) {
      assert.ok(redoSteps < adjacentText.length, 'Native Redo converges within the adjacent typing length.');
      await page.keyboard.press('Control+Shift+Z');
      const nextAdjacent = await readRemainingAdjacentText();
      assert.ok(nextAdjacent.length > remainingAdjacent.length,
        'Each native Redo restores adjacent text without altering the token.');
      remainingAdjacent = nextAdjacent;
      redoSteps++;
    }
    assert.equal(await message.inputValue(), withAdjacentText,
      'Native Redo restores all adjacent typing after restoring the placeholder insertion.');
    const historyInputTypes = await message.evaluate(() => window.__suggestionLimitInputTypes);
    assert.ok(historyInputTypes.includes('historyUndo') && historyInputTypes.includes('historyRedo'),
      `Native history events were observed: ${JSON.stringify(historyInputTypes)}`);
    assert.equal(postedPayloads.length, postsBeforeOpen, 'Editing, Undo and Redo do not autosave.');
    await saveMessage(inserted);

    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => new Promise(resolve => {
      requestAnimationFrame(() => requestAnimationFrame(resolve));
    }));
    const mobileTriggerBeforeOpen = await trigger.evaluate(node => ({
      disabled: node.disabled,
      ariaExpanded: node.getAttribute('aria-expanded'),
      activeElement: document.activeElement?.id || document.activeElement?.localName || null,
      viewport: { width: innerWidth, height: innerHeight }
    }));
    assert.equal(mobileTriggerBeforeOpen.viewport.width, 390,
      `The mobile viewport is ready before opening the picker: ${JSON.stringify(mobileTriggerBeforeOpen)}`);
    assert.equal(mobileTriggerBeforeOpen.disabled, false,
      `The placeholder trigger remains enabled after the mobile resize: ${JSON.stringify(mobileTriggerBeforeOpen)}`);
    await trigger.focus();
    await trigger.press('Enter');
    try {
      await page.waitForFunction(() => {
        const triggerNode = document.getElementById('suggestion-limit-placeholder-trigger');
        const popupNode = document.getElementById('suggestion-limit-placeholder-picker');
        return triggerNode?.getAttribute('aria-expanded') === 'true' &&
          (popupNode?.matches(':popover-open') || popupNode?.hidden === false);
      }, undefined, { timeout: 5000 });
    } catch (error) {
      const mobileOpenDiagnostic = await page.evaluate(() => {
        const triggerNode = document.getElementById('suggestion-limit-placeholder-trigger');
        const popupNode = document.getElementById('suggestion-limit-placeholder-picker');
        return {
          triggerDisabled: triggerNode?.disabled ?? null,
          ariaExpanded: triggerNode?.getAttribute('aria-expanded') ?? null,
          popoverOpen: popupNode?.matches(':popover-open') ?? false,
          popupHidden: popupNode?.hidden ?? null,
          activeElement: document.activeElement?.id || document.activeElement?.localName || null,
          viewport: { width: innerWidth, height: innerHeight }
        };
      });
      throw new Error(`The mobile placeholder popup did not open: ${JSON.stringify(mobileOpenDiagnostic)}`, { cause: error });
    }
    await option.waitFor({ state: 'visible' });
    const popupGeometry = await page.evaluate(() => {
      const picker = document.getElementById('suggestion-limit-placeholder-picker').getBoundingClientRect();
      const button = document.querySelector('#suggestion-limit-placeholder-picker [data-suggestion-placeholder]').getBoundingClientRect();
      return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
        picker: { left: picker.left, right: picker.right, width: picker.width },
        option: { left: button.left, right: button.right, width: button.width } };
    });
    assert.ok(popupGeometry.picker.width > 0 && popupGeometry.picker.left >= -1 &&
      popupGeometry.picker.right <= popupGeometry.width + 1,
    `The mobile picker fits inside the viewport: ${JSON.stringify(popupGeometry)}`);
    assert.ok(popupGeometry.option.left >= popupGeometry.picker.left - 1 &&
      popupGeometry.option.right <= popupGeometry.picker.right + 1,
    `The mobile option fits inside the picker: ${JSON.stringify(popupGeometry)}`);
    assert.ok(popupGeometry.scrollWidth <= popupGeometry.width);
    const assertMobilePickerOpen = async phase => {
      const state = await page.evaluate(() => {
        const triggerNode = document.getElementById('suggestion-limit-placeholder-trigger');
        const popupNode = document.getElementById('suggestion-limit-placeholder-picker');
        const optionNode = popupNode?.querySelector('[data-suggestion-placeholder]');
        const bounds = optionNode?.getBoundingClientRect();
        return {
          ariaExpanded: triggerNode?.getAttribute('aria-expanded') ?? null,
          popoverOpen: popupNode?.matches(':popover-open') ?? false,
          popupHidden: popupNode?.hidden ?? null,
          optionVisible: Boolean(bounds && bounds.width > 0 && bounds.height > 0),
          viewport: { width: innerWidth, height: innerHeight }
        };
      });
      assert.equal(state.ariaExpanded, 'true', `The mobile picker trigger remains expanded ${phase}: ${JSON.stringify(state)}`);
      assert.equal(state.popoverOpen, true, `The native mobile popover remains open ${phase}: ${JSON.stringify(state)}`);
      assert.equal(state.optionVisible, true, `The mobile option remains rendered ${phase}: ${JSON.stringify(state)}`);
    };
    report.suggestionLimitPlaceholderGeometry = popupGeometry;
    await assertMobilePickerOpen('before screenshot capture');
    await scan(page, axeSource, args.artifactRoot, report, 'suggestion-placeholder-mobile', false, false);
    await assertMobilePickerOpen('after screenshot capture');
    await page.keyboard.press('Escape');
    assert.equal(await trigger.evaluate(node => document.activeElement === node), true);
    await page.setViewportSize({ width: 1280, height: 900 });
    if ((await message.inputValue()) !== systemSnapshot.value) await saveMessage(systemSnapshot.value);
    else await reloadScope('system');

    await switchScope('2');
    assert.deepEqual(await readEditor(), librarySnapshot,
      'The accepted Library message still reflects the initial System fallback before Library editing.');
    await trackInputEvents();
    assert.equal(await override.count(), 1);
    assert.equal(await message.isEditable(), true);
    const librarySource = `Library selected suffix ${args.identity.staffId}`;
    const libraryInserted = `Library {{next_available_date}} suffix ${args.identity.staffId}`;
    assert.notEqual(libraryInserted, librarySnapshot.value, 'The Library round trip uses a distinct message draft.');
    await message.fill(librarySource);
    await message.evaluate(node => node.setSelectionRange(8, 16));
    await trigger.focus();
    await trigger.press('Enter');
    await option.waitFor({ state: 'visible' });
    await option.press('Enter');
    assert.equal(await message.inputValue(), libraryInserted,
      'The Library picker inserts the token into the local override message.');
    const libraryPayload = await saveSettings();
    assert.equal(libraryPayload.workflow.suggestionLimitMessage, libraryInserted,
      'The Library token is saved through the existing Workflow override payload.');
    await reloadScope('2');
    assert.equal(await librarySetting(override), true);
    assert.equal(await message.inputValue(), libraryInserted,
      'The explicit Library message survives an accepted save and reload.');
    if (librarySnapshot.overridden) {
      await message.fill(librarySnapshot.value);
      const restoreLibraryValue = await saveSettings();
      assert.equal(restoreLibraryValue.workflow.suggestionLimitMessage, librarySnapshot.value);
    } else {
      await useSystemValue(page, override);
      const resetLibraryOverride = await saveSettings();
      assert.equal(resetLibraryOverride.workflow.suggestionLimitMessage, null,
        'Restoring an initially inherited message removes only the temporary Library override.');
    }
    await reloadScope('2');
    assert.deepEqual(await readEditor(), librarySnapshot,
      'The Library message and Override ownership are restored after the insertion round trip.');

    await trackInputEvents();
    assert.equal(await message.isEditable(), true);
    await message.fill('Library override draft for stale picker');
    await message.evaluate(node => node.setSelectionRange(8, 20));
    await trigger.focus();
    await trigger.press('Enter');
    await option.waitFor({ state: 'visible' });
    await useSystemValue(page, override);
    assert.equal(await trigger.isDisabled(), false, 'The inherited Library editor keeps its picker editable.');
    const overrideDraft = await message.inputValue();
    const inputEventsBeforeOverrideClick = await message.evaluate(node => window.__suggestionLimitInputTypes.length);
    await retainedOption.evaluate(node => node.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    assert.equal(await message.inputValue(), overrideDraft, 'A stale Override-value option cannot insert text.');
    assert.equal(await message.evaluate(node => window.__suggestionLimitInputTypes.length), inputEventsBeforeOverrideClick,
      'Rejecting an Override-stale action dispatches no editor input.');
    await reloadScope('2');
    assert.deepEqual(await readEditor(), librarySnapshot,
      'The Library message and Override ownership are restored after the stale-value check.');

    await trackInputEvents();
    assert.equal(await message.isEditable(), true);
    await message.fill('Library scope draft before switch');
    await message.evaluate(node => node.setSelectionRange(0, 7));
    await trigger.focus();
    await trigger.press('Enter');
    await option.waitFor({ state: 'visible' });
    const inputEventsBeforeScopeSwitch = await message.evaluate(node => window.__suggestionLimitInputTypes.length);
    await switchScope('system');
    const systemValueAfterSwitch = await message.inputValue();
    const inputEventsAfterScopeSwitch = await message.evaluate(node => window.__suggestionLimitInputTypes.length);
    assert.equal(systemValueAfterSwitch, systemSnapshot.value,
      'The accepted System context replaces the pending Library message.');
    await retainedOption.evaluate(node => node.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    assert.equal(await message.inputValue(), systemSnapshot.value,
      'A retained Library option cannot insert into the accepted System context.');
    assert.equal(await message.evaluate(node => window.__suggestionLimitInputTypes.length), inputEventsAfterScopeSwitch,
      'A scope-retired picker dispatches no editor input.');
    assert.ok(inputEventsAfterScopeSwitch >= inputEventsBeforeScopeSwitch);
    await switchScope('2');
    assert.deepEqual(await readEditor(), librarySnapshot,
      'The Library scope switch leaves its persisted message and ownership unchanged.');
    report.suggestionLimitPlaceholderRoundTrip = true;
  } catch (error) {
    testError = error;
  }

  let cleanupError = null;
  try {
    await page.setViewportSize({ width: 1280, height: 900 });
    await switchScope('system');
    if ((await message.inputValue()) !== systemSnapshot.value) {
      await message.fill(systemSnapshot.value);
    }
    if (await page.locator('#settings-save').isEnabled()) {
      const payload = await saveSettings();
      assert.equal(payload.workflow.suggestionLimitMessage, systemSnapshot.value,
        'Only a dirty, accepted System replacement is saved during restoration.');
    }
    await reloadScope('system');
    assert.equal(await message.inputValue(), systemSnapshot.value,
      'The accepted System message is restored after the placeholder journey.');
    await switchScope('2');
    const currentLibrary = await readEditor();
    if (librarySnapshot.overridden) {
      if (!currentLibrary.overridden) { const value = await message.inputValue(); await message.fill(value + ' draft'); }
      if (await message.inputValue() !== librarySnapshot.value) await message.fill(librarySnapshot.value);
    } else if (currentLibrary.overridden) {
      await useSystemValue(page, override);
    }
    if (await page.locator('#settings-save').isEnabled()) {
      const payload = await saveSettings();
      assert.equal(payload.workflow.suggestionLimitMessage,
        librarySnapshot.overridden ? librarySnapshot.value : null);
    }
    await reloadScope('2');
    if (librarySnapshot) {
      assert.deepEqual(await readEditor(), librarySnapshot, 'The final Library state matches its accepted snapshot.');
    }
  } catch (error) {
    cleanupError = error;
  }
  if (testError && cleanupError) {
    throw new AggregateError([testError, cleanupError],
      'The suggestion placeholder journey failed and its settings snapshots could not both be restored.');
  }
  if (testError) throw testError;
  if (cleanupError) throw cleanupError;
}

async function runLibraryParticipation(page, axeSource, args, report, ready, postedPayloads) {
  const writes = [];
  const track = request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.startsWith('/api/asap/staff/')) writes.push(request);
  };
  page.on('request', track);
  const readCatalog = () => page.evaluate(async () => {
    const response = await fetch('/api/asap/staff/organizations');
    if (!response.ok) throw new Error(`Organization catalog GET ${response.status}`);
    const body = await response.json(); return body.data;
  });
  const visibleCatalog = () => page.locator('#settings-organization-catalog').evaluate(catalog => ({
    libraries: [...catalog.querySelectorAll('#settings-organizations-list [data-organization-id]')]
      .map(row => ({ id: Number(row.dataset.organizationId), name: row.querySelector('.settings-organization-name').textContent,
        status: row.querySelector('.status-badge').textContent, actions: row.querySelectorAll('button').length })),
    groups: [...catalog.querySelectorAll('[data-library-organization-id]')].map(group => ({
      id: group.dataset.libraryOrganizationId, name: group.querySelector('h4').textContent,
      children: [...group.querySelectorAll('[data-organization-id]')].map(row => Number(row.dataset.organizationId))
    })),
    recordIds: [...catalog.querySelectorAll('[data-organization-id]')].map(row => Number(row.dataset.organizationId)),
    referenceActions: catalog.querySelectorAll('#settings-reference-organizations button').length
  }));
  const settled = () => page.waitForFunction(() => {
    const form = document.getElementById('settings-form'); return !form.inert && !form.hasAttribute('aria-busy');
  });
  const action = async (id, active, expectedStatus = 200, version = null) => {
    const current = (await readCatalog()).find(row => row.id === id);
    const path = `/api/asap/staff/organizations/${id}/${active ? 'activate' : 'deactivate'}`;
    const responsePromise = page.waitForResponse(candidate => candidate.request().method() === 'POST' &&
      new URL(candidate.url()).pathname === path);
    await decideConfirmation(page, () => page.locator(`#settings-organizations-list [data-organization-id="${id}"] button`).click(),
      async dialog => {
        assert.match(dialog.message(), /participation/);
        await dialog.accept();
      });
    const response = await responsePromise;
    assert.equal(response.status(), expectedStatus, await response.text());
    assert.deepEqual(response.request().postDataJSON(), { version: version || current.version });
    await settled();
    assert.equal(await page.locator(`#settings-organizations-list [data-organization-id="${id}"] button`)
      .evaluate(button => button === document.activeElement), true,
    `The updated library ${id} participation action retains focus after HTTP ${expectedStatus}.`);
    return response;
  };
  const label = row => `${row.name}${row.abbreviation ? ` (${row.abbreviation})` : ''}`;
  const isLibrary = row => row.id > 1 && row.organizationCodeId === 2;
  const order = (left, right) => left.localeCompare(right, undefined, { sensitivity: 'base', numeric: true });
  const catalogOrder = (left, right) => order(label(left), label(right)) || left.id - right.id;
  const ownedLibraries = new Set([2, 3, 4, 5, 6, 7]);
  const ownedReferences = new Set([1, 101, ...Array.from({ length: 11 }, (_, index) => 81001 + index)]);
  const assertCatalog = (displayed, accepted) => {
    const byId = new Map(accepted.map(row => [row.id, row]));
    assert.deepEqual([...displayed.recordIds].sort((a, b) => a - b), accepted.map(row => row.id).sort((a, b) => a - b),
      'The Polaris catalog renders every SQL record exactly once.');
    assert.deepEqual(displayed.libraries, accepted.filter(isLibrary).sort(catalogOrder).map(row => ({
      id: row.id, name: label(row), status: row.isActive ? 'Active' : 'Inactive', actions: 1
    })));
    assert.equal(displayed.referenceActions, 0);
    assert.equal(new Set(displayed.groups.map(group => group.id)).size, displayed.groups.length);
    assert.equal(displayed.groups.at(-1).id, 'other', 'Unassociated records appear in the final group.');
    assert.equal(displayed.groups.at(-1).name, 'Other / unassociated organizations');
    const associated = displayed.groups.filter(group => group.id !== 'other');
    assert.deepEqual(associated.map(group => group.id), [...associated]
      .sort((left, right) => catalogOrder(byId.get(Number(left.id)), byId.get(Number(right.id))))
      .map(group => group.id), 'All parent-library groups are alphabetical, including records retained by earlier journeys.');
    for (const group of displayed.groups) {
      if (group.id !== 'other') {
        const library = byId.get(Number(group.id));
        assert.equal(isLibrary(library), true);
        assert.equal(group.name, label(library));
      }
      assert.ok(group.children.every(id => !isLibrary(byId.get(id))), 'Reference groups contain only reference records.');
      assert.deepEqual(group.children, [...group.children].sort((left, right) => catalogOrder(byId.get(left), byId.get(right))),
        `Every record in ${group.name} is alphabetical.`);
    }
  };
  const assertOwnedGroups = (displayed, expected) => {
    for (const [libraryId, ids] of expected) {
      assert.deepEqual(displayed.groups.find(group => group.id === libraryId)?.children.filter(id => ownedReferences.has(id)), ids,
        `Issue 433 fixture records retain their exact membership and ordering in group ${libraryId}.`);
    }
  };
  const assertSummary = async accepted => {
    const libraries = accepted.filter(isLibrary), active = libraries.filter(row => row.isActive).length;
    assert.equal(await page.locator('#organizations-status-message').textContent(),
      `${active} of ${libraries.length} synchronized libraries active in ASAP; ${libraries.length - active} inactive.`);
  };
  try {
    await page.locator('#settings-nav-start').click();
    assert.equal(await page.locator('#enabled-libraries-checkbox-container').count(), 0);
    assert.equal(await page.locator('#manage-library-participation').evaluate(button =>
      button.closest('fieldset').querySelectorAll('input').length), 0);
    assert.equal(await page.locator('#settings-staff #settings-organizations-list').count(), 0);
    assert.equal(await page.locator('#settings-organizations-list').count(), 1);
    await assertSummary(await readCatalog());
    const systemSummaryWrites = writes.length;
    await page.setViewportSize({ width: 1280, height: 900 });
    await scan(page, axeSource, args.artifactRoot, report, 'participation-summary-system-desktop');
    assert.equal(writes.length, systemSummaryWrites, 'Capturing the system summary does not submit any changes.');
    assert.equal(await page.locator('#settings-save').isDisabled(), true);
    const originalUrl = await page.locator('#system-staff-url').inputValue();
    const draftUrl = 'https://draft.example.org/staff/participation';
    await page.locator('#system-staff-url').fill(draftUrl);
    const beforeWrites = writes.length, beforeSettingsPosts = postedPayloads.length;
    await page.getByRole('button', { name: 'Manage library participation', exact: true }).focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.locator('#settings-polaris').isVisible(), true);
    assert.equal(await page.locator('#settings-organization-catalog').evaluate(node => node === document.activeElement), true);
    assert.equal(await page.locator('#system-staff-url').inputValue(), draftUrl);
    assert.equal(await page.locator('#settings-save').isEnabled(), true);
    assert.equal(await page.locator('#settings-scope').inputValue(), 'system');
    assert.equal(await page.locator('#confirmation-dialog[open]').count(), 0);
    assert.equal(writes.length, beforeWrites);
    assert.equal(postedPayloads.length, beforeSettingsPosts);
    await page.locator('#settings-nav-start').click();
    await page.locator('#system-staff-url').fill(originalUrl);
    assert.equal(await page.locator('#settings-save').isDisabled(), true);
    report.participationNavigationPreservedDraft = true;

    await page.locator('#settings-nav-polaris').click();
    const catalog = await readCatalog(), initial = await visibleCatalog();
    assertCatalog(initial, catalog);
    assertOwnedGroups(initial, [
      ['5', [81002, 81003, 81001]], ['6', [81005, 81004]], ['7', [81006]], ['2', [101]],
      ['other', [81008, 81009, 81011, 81007, 81010, 1]]
    ]);
    assert.deepEqual(['5', '6', '7'].map(id => initial.groups.find(group => group.id === id).name),
      ['Alexandria (ALEX)', 'Bexley (BEX)', 'Columbus (COL)']);
    for (const id of [5, 6, 7]) assert.equal(initial.libraries.find(row => row.id === id).status, 'Inactive');
    assert.equal(await page.locator('[data-organization-id="81003"] .settings-organization-parent').textContent(),
      'Within: Alexandria services (SERV)');
    const descriptions = await page.locator('#settings-organization-catalog').textContent();
    assert.match(descriptions, /ASAP only; it does not activate or deactivate the organization in Polaris/);
    assert.match(descriptions, /not necessarily available pickup locations/);
    await page.setViewportSize({ width: 1280, height: 900 });
    await scan(page, axeSource, args.artifactRoot, report, 'organizations-system-desktop');
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'organizations-system-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    report.organizationParentGrouping = true;

    const failSync = route => route.fulfill({ status: 502, contentType: 'application/json',
      body: JSON.stringify({ code: 'polaris_unavailable', message: 'Deterministic organization provider unavailable.' }) });
    await page.route('**/api/asap/staff/organizations/sync', failSync);
    await page.locator('#btn-sync-organizations').click();
    await page.locator('#organizations-sync-result').filter({ hasText: /outcome is uncertain/ }).waitFor();
    assert.deepEqual(await visibleCatalog(), initial, 'A failed sync preserves the accepted catalog while review is required.');
    await page.unroute('**/api/asap/staff/organizations/sync', failSync);
    await page.locator('#settings-refresh').click(); await settled();
    const syncResponse = page.waitForResponse(candidate => candidate.request().method() === 'POST' &&
      new URL(candidate.url()).pathname === '/api/asap/staff/organizations/sync');
    await page.locator('#btn-sync-organizations').click();
    assert.equal((await syncResponse).status(), 200);
    await page.locator('#organizations-sync-result').filter({ hasText: /^Synchronized 19 organizations\.$/ }).waitFor();
    await settled();
    const synchronized = await visibleCatalog();
    const synchronizedCatalog = await readCatalog();
    assertCatalog(synchronized, synchronizedCatalog);
    assertOwnedGroups(synchronized, [
      ['5', [81001]], ['6', [81005, 81004]], ['7', [81002, 81006, 81003]], ['2', [101]],
      ['other', [81008, 81009, 81011, 81007, 81010, 1]]
    ]);
    assert.deepEqual([...synchronized.recordIds].sort((a, b) => a - b), [...initial.recordIds].sort((a, b) => a - b));
    assert.ok(synchronizedCatalog.filter(row => ownedLibraries.has(row.id)).every(row => !row.isActive));
    await assertSummary(synchronizedCatalog);
    report.organizationSyncRegrouped = true;

    const canceledCount = writes.length;
    await decideConfirmation(page, () => page.locator('[data-organization-id="3"] button').click(), dialog => dialog.dismiss());
    await settled();
    assert.equal(writes.length, canceledCount, 'Cancel does not send a participation POST.');
    assert.equal(await page.locator('[data-organization-id="3"] button').evaluate(button => button === document.activeElement), true,
      'Cancel returns focus to the unchanged participation action.');
    await action(2, true);
    const stale = (await readCatalog()).find(row => row.id === 3);
    const external = await page.evaluate(async ({ id, version }) => {
      const session = await (await fetch('/api/asap/staff/session')).json();
      const response = await fetch(`/api/asap/staff/organizations/${id}/activate`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'X-ASAP-Antiforgery': session.antiforgeryToken },
        body: JSON.stringify({ version })
      });
      return { status: response.status, body: await response.json() };
    }, { id: 3, version: stale.version });
    assert.equal(external.status, 200);
    const conflict = await action(3, true, 409, stale.version);
    assert.equal((await conflict.json()).code, 'stale_version');
    assert.equal(await page.locator('[data-organization-id="3"] .status-badge').textContent(), 'Active');
    await action(3, false);
    assert.equal(await page.locator('[data-organization-id="3"] .status-badge').textContent(), 'Inactive');
    await action(3, true);
    const current = await readCatalog();
    assert.deepEqual(current.filter(row => ownedLibraries.has(row.id) && row.isActive).map(row => row.id).sort(), [2, 3]);
    assert.deepEqual(current.filter(row => isLibrary(row) && !ownedLibraries.has(row.id)).map(row => [row.id, row.isActive]),
      catalog.filter(row => isLibrary(row) && !ownedLibraries.has(row.id)).map(row => [row.id, row.isActive]),
      'Participation actions preserve other legitimate libraries in the shared catalog.');
    await assertSummary(current);
    assert.equal(postedPayloads.length, beforeSettingsPosts, 'Participation changes never submit the global Settings form.');
    report.systemParticipationRoundTrip = true;

    await page.locator('#settings-scope').selectOption('2'); await ready('2');
    await page.locator('#settings-nav-start').click();
    assert.equal(await page.locator('#manage-library-participation').isDisabled(), true);
    await page.locator('#manage-library-participation').evaluate(button => button.click());
    assert.equal(await page.locator('#settings-scope').inputValue(), '2');
    assert.equal(await page.locator('#settings-start').isVisible(), true);
    await assertSummary(await readCatalog());
    const librarySummaryWrites = writes.length;
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'participation-summary-library-mobile');
    assert.equal(writes.length, librarySummaryWrites, 'Capturing the library summary does not submit any changes.');
    assert.equal(await page.locator('#settings-save').isDisabled(), true);
    await page.locator('#settings-nav-polaris').click();
    for (const button of await page.locator('#settings-organizations-list button').all()) assert.equal(await button.isDisabled(), true);
    await scan(page, axeSource, args.artifactRoot, report, 'organizations-library-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#settings-scope').selectOption('system'); await ready('system');
    report.participationLibraryControlsDisabled = true;
  } finally { page.removeListener('request', track); }
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  if (!['full', 'system-format-rule', 'library-rule-partial-snapshot', 'library-rule-missing-row', 'library-format-rule-drafts', 'library-metadata', 'campaign2', 'campaign2-library-admin'].includes(args.mode)) {
    throw new Error(`Unsupported settings browser mode: ${args.mode}`);
  }
  if (args.mode.startsWith('library-rule-') && (!args.formatCode || args.scopeId === 'system')) {
    throw new Error('Library rule-completeness modes require a library scope and a seeded format code.');
  }
  await fs.mkdir(args.artifactRoot, { recursive: true });
  let chromium;
  let axeSource;
  try {
    ({ chromium } = require('playwright'));
    axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  } catch (error) {
    throw new Error(`Settings browser acceptance requires local Playwright and axe-core dependencies: ${error.message}`);
  }

  const browser = await chromium.launch({ headless: true });
  const traffic = { externalRequests: 0 };
  const pageErrors = [];
  const report = { states: [], externalRequests: 0, pageErrors, postedEditorPayload: false,
    systemParticipationRoundTrip: false, unrelatedSystemSavePreservedParticipation: false,
    participationNavigationPreservedDraft: false, organizationParentGrouping: false,
    organizationSyncRegrouped: false, participationLibraryControlsDisabled: false,
    systemPatronCodesRoundTrip: false, systemUnrelatedSavePreservedPatronCodes: false,
    libraryPatronCodesRoundTrip: false, libraryUnrelatedSavePreservedPatronCodes: false,
    libraryAutoClaimRuleBigintRoundTrip: false, libraryUnrelatedSavePreservedAutoClaimRules: false,
    libraryInitialScalarSavePreservedAutoClaimRules: false,
    libraryEmptyPatronCodeReplacementRoundTrip: false, libraryPatronCodeResetRoundTrip: false,
    systemTimeoutDependenciesRoundTrip: false, libraryTimeoutDependenciesRoundTrip: false,
    systemCommonAuthorsRoundTrip: false, libraryCommonAuthorsRoundTrip: false,
    commonCreatorDiscard: false, commonCreatorLayouts: [],
    suggestionLimitPlaceholderRoundTrip: false,
    wildcardOriginNormalized: false, systemFormatRuleRoundTrip: false, systemFormatRulePost: null,
    systemFormatRulePostCount: 0,
    libraryFormatRuleWitness: {
      postCount: 0,
      requestBody: null,
      responseStatus: null,
      responseBody: null,
      partialSnapshotProjectionApplied: false,
      storedTargetRulePresent: false,
      targetRowInitiallyPresent: false,
      targetRowRemoved: false,
      blocked: false,
      blockedMessage: null
    } };
  let postedPayload = null;
  const postedPayloads = [];
  try {
    const context = await browser.newContext({
      viewport: { width: 1280, height: 900 },
      extraHTTPHeaders: {
        'X-ASAP-Test-Staff-Id': args.identity.staffId,
        'X-ASAP-Test-Tenant-Id': args.identity.tenantId,
        'X-ASAP-Test-Staff-Email': args.identity.email
      }
    });
    await context.route('**/*', route => {
      if (sameOrigin(route.request().url(), args.baseOrigin)) {
        return route.continue().catch(error => {
          if (!/Route is already handled|closed/i.test(error.message)) throw error;
        });
      }
      traffic.externalRequests += 1;
      return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    report.recordSpacing = settingsSpacingRecorder(page, args.artifactRoot);
    page.on('pageerror', error => pageErrors.push(error.message)); rejectNativeDialogs(page, pageErrors);
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/settings') {
        postedPayload = request.postDataJSON();
        postedPayloads.push(postedPayload);
        if (args.mode === 'system-format-rule') report.systemFormatRulePostCount = postedPayloads.length;
        if (args.mode.startsWith('library-rule-')) {
          report.libraryFormatRuleWitness.postCount = postedPayloads.length;
          report.libraryFormatRuleWitness.requestBody = request.postData();
        }
      }
    });

    if (args.mode === 'library-rule-partial-snapshot') {
      await page.route('**/api/asap/staff/settings*', async route => {
        const requestUrl = new URL(route.request().url());
        if (route.request().method() !== 'GET' || requestUrl.pathname !== '/api/asap/staff/settings' ||
            requestUrl.searchParams.get('orgId') !== args.scopeId) {
          await route.continue();
          return;
        }
        const response = await route.fetch();
        const data = await response.json();
        const storedRules = data.stored?.formatRules;
        report.libraryFormatRuleWitness.storedTargetRulePresent = Array.isArray(storedRules) &&
          storedRules.some(item => item.code === args.formatCode);
        const effectiveFormats = data.effective?.formats;
        assert.ok(Array.isArray(effectiveFormats), 'The settings endpoint must return its effective format snapshot.');
        assert.ok(effectiveFormats.some(item => item.code === args.formatCode),
          'The unmodified settings endpoint must include the seeded format before the test projection is made.');
        data.effective.formats = effectiveFormats.filter(item => item.code !== args.formatCode);
        report.libraryFormatRuleWitness.partialSnapshotProjectionApplied = true;
        await route.fulfill({
          status: response.status(),
          headers: { 'content-type': 'application/json; charset=utf-8' },
          body: JSON.stringify(data)
        });
      });
    }

    const saveSettings = async () => {
      const responsePromise = page.waitForResponse(candidate => candidate.request().method() === 'POST' &&
        new URL(candidate.url()).pathname === '/api/asap/staff/settings');
      let saved;
      try {
        [, saved] = await Promise.all([page.locator('#settings-save').click(), responsePromise]);
      } catch (error) {
        const settingsMessage = (await page.locator('#settings-message').textContent().catch(() => ''))?.trim();
        throw new Error(`Settings save did not receive an HTTP response; settings message: ${settingsMessage || '(empty)'}. ${error.message}`);
      }
      assert.equal(saved.status(), 200, await saved.text());
      await page.locator('#settings-message').filter({
        hasText: /^Settings saved\.(?: Format deletions confirmed: \d+ of \d+\.)?$/
      }).waitFor();
      return postedPayloads.at(-1);
    };

    const waitForSettingsReady = async expectedScope => {
      await page.waitForFunction(expectedScope => {
        const form = document.getElementById('settings-form');
        const message = document.getElementById('settings-message');
        const scope = document.getElementById('settings-scope');
        const acceptedScope = Boolean(form && !form.hidden && scope?.value === expectedScope);
        return acceptedScope ||
          Boolean(message?.classList.contains('error') && message.textContent.trim());
      }, expectedScope);
      const message = page.locator('#settings-message');
      if (await message.evaluate(element => element.classList.contains('error'))) {
        throw new Error(`Settings load failed: ${await message.textContent()}`);
      }
      await page.locator('#settings-form').waitFor({ state: 'visible' });
      assert.equal(await page.locator('#settings-scope').inputValue(), expectedScope,
        'The settings editor became ready for the wrong scope.');
    };

    const initialScope = args.mode.startsWith('library-') || args.mode === 'campaign2-library-admin' ? args.scopeId : 'system';
    await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=${encodeURIComponent(initialScope)}#settings-start`, { waitUntil: 'networkidle' });
    await waitForSettingsReady(initialScope);
    if (args.mode.startsWith('campaign2')) {
      await runCampaign2Editor(page, axeSource, args, report, saveSettings, waitForSettingsReady);
      assert.deepEqual(pageErrors, [], 'The Campaign 2 Settings journey raised browser errors.');
      assert.equal(traffic.externalRequests, 0);
      await page.close(); await context.close();
      return;
    }
    if (args.mode === 'library-metadata') {
      await verifySettingsDraftBlockers(page, args, report, waitForSettingsReady,
        name => scan(page, axeSource, args.artifactRoot, report, name, false, false));
      await verifyInheritanceInteractions(page, args, report, waitForSettingsReady,
        name => scan(page, axeSource, args.artifactRoot, report, name, false, false));
      await runLibraryOverrideMetadataEditor(page, args, report, saveSettings, waitForSettingsReady);
      await scan(page, axeSource, args.artifactRoot, report, 'desktop');
      await page.setViewportSize({ width: 390, height: 844 });
      await scan(page, axeSource, args.artifactRoot, report, 'mobile');
      assert.deepEqual(pageErrors, [], 'The library metadata journey raised browser errors.');
      assert.equal(traffic.externalRequests, 0, 'The library metadata journey requested external assets.');
      await page.close();
      await context.close();
      return;
    }
    if (args.mode === 'library-format-rule-drafts') {
      await runLibraryFormatRuleDrafts(page, args, report, waitForSettingsReady);
      await scan(page, axeSource, args.artifactRoot, report, 'desktop');
      await page.setViewportSize({ width: 390, height: 844 });
      await scan(page, axeSource, args.artifactRoot, report, 'mobile');
      assert.equal(postedPayloads.length, 3, 'Two actual rejected attempts precede exactly one combined committed POST.');
      assert.deepEqual(pageErrors, []);
      assert.equal(traffic.externalRequests, 0);
      await page.close(); await context.close();
      return;
    }
    if (args.mode === 'system-format-rule') {
      await runSystemFormatRuleEditor(page, report, waitForSettingsReady);
      await scan(page, axeSource, args.artifactRoot, report, 'desktop');
      await page.setViewportSize({ width: 390, height: 844 });
      await scan(page, axeSource, args.artifactRoot, report, 'mobile');
      await page.close();
      await context.close();
      assert.deepEqual(pageErrors, [], 'The System format-rule journey raised browser errors.');
      assert.equal(traffic.externalRequests, 0, 'The System format-rule journey requested external assets.');
      assert.equal(report.systemFormatRuleRoundTrip, true);
      return;
    }
    if (args.mode.startsWith('library-rule-')) {
      await runLibraryRuleCompletenessEditor(page, axeSource, args, report);
      await page.close();
      await context.close();
      assert.deepEqual(pageErrors, [], 'The library rule-completeness journey raised browser errors.');
      assert.equal(traffic.externalRequests, 0, 'The library rule-completeness journey requested external assets.');
      assert.equal(report.libraryFormatRuleWitness.postCount, 0,
        `An incomplete rule projection must block the save before HTTP. Body: ${report.libraryFormatRuleWitness.responseBody || ''}`);
      assert.equal(report.libraryFormatRuleWitness.blocked, true,
        'The editor must explain that an incomplete rule projection cannot be saved.');
      if (args.mode === 'library-rule-partial-snapshot') {
        assert.equal(report.libraryFormatRuleWitness.partialSnapshotProjectionApplied, true);
        assert.equal(report.libraryFormatRuleWitness.storedTargetRulePresent, true);
      } else {
        assert.equal(report.libraryFormatRuleWitness.targetRowInitiallyPresent, true);
        assert.equal(report.libraryFormatRuleWitness.targetRowRemoved, true);
      }
      return;
    }
    const systemCredentialFlags = await page.evaluate(async () => {
      const response = await fetch('/api/asap/staff/settings?orgId=system');
      if (!response.ok) throw new Error(`Settings credential flags GET ${response.status}`);
      const data = await response.json();
      const configured = data.stored?.configuredSystem || {};
      return {
        apiKey: configured.polaris?.hasApiKey === true,
        adminPassword: configured.polaris?.hasAdminPassword === true,
        postmarkToken: configured.email?.hasPostmarkToken === true
      };
    });
    await page.locator('#settings-nav-polaris').click();
    for (const [id, saved] of [
      ['polaris-api-key', systemCredentialFlags.apiKey],
      ['polaris-admin-pass', systemCredentialFlags.adminPassword]
    ]) {
      const input = page.locator(`#${id}`);
      assert.equal(await input.inputValue(), '', `${id}: the stored secret is never placed in the input value`);
      assert.equal(await input.getAttribute('placeholder') || '', saved ? '********' : '',
        `${id}: the fixed mask follows only the accepted saved-credential flag`);
      await input.focus(); await input.blur();
    }
    assert.equal(await page.locator('#settings-save').isDisabled(), true,
      'Credential focus and blur do not create a settings draft.');
    assert.equal(await page.locator('#polaris-api-key-status').textContent(), systemCredentialFlags.apiKey
      ? 'API key saved. Leave blank to keep the current key; enter a new key to replace it.' : 'No API key saved.');
    assert.equal(await page.locator('#polaris-admin-pass-status').textContent(), systemCredentialFlags.adminPassword
      ? 'System staff password saved. Leave blank to keep the current password; enter a new password to replace it.'
      : 'No system staff password saved.');

    await page.locator('#settings-nav-smtp').click();
    const postmarkInput = page.locator('#email-postmark-token');
    const clearPostmark = page.locator('#email-clear-postmark-token');
    const postmarkStatus = page.locator('#email-token-status');
    assert.equal(await postmarkInput.inputValue(), '', 'The stored Postmark token is never placed in the input value.');
    assert.equal(await postmarkInput.getAttribute('placeholder') || '', systemCredentialFlags.postmarkToken ? '********' : '',
      'The Postmark mask follows only the accepted saved-token flag.');
    assert.equal(await postmarkInput.getAttribute('aria-describedby'), 'email-token-status');
    assert.equal(await postmarkStatus.getAttribute('role'), 'status');
    assert.equal(await postmarkStatus.getAttribute('aria-live'), 'polite');
    assert.equal(await postmarkStatus.textContent(), systemCredentialFlags.postmarkToken
      ? 'A system Postmark token is stored. Leave blank to keep it; enter a new token to replace it.'
      : 'No system Postmark token is stored.');
    await postmarkInput.focus(); await postmarkInput.blur();
    assert.equal(await page.locator('#settings-save').isDisabled(), true,
      'Postmark focus and blur do not create a settings draft.');
    await clearPostmark.check();
    assert.equal(await postmarkInput.getAttribute('placeholder') || '', '', 'Selecting clear hides the retain-current mask.');
    assert.equal(await postmarkStatus.textContent(), systemCredentialFlags.postmarkToken
      ? 'Postmark token removal is pending. Save settings to remove the stored token.'
      : 'Postmark token clear is selected. No token is currently stored; saving keeps it unconfigured.');
    if (!systemCredentialFlags.postmarkToken) {
      await clearPostmark.uncheck();
      await postmarkInput.fill('unsaved-postmark-draft');
      await clearPostmark.check();
      assert.equal(await postmarkInput.inputValue(), '', 'Selecting clear discards a first unsaved replacement draft.');
      assert.equal(await postmarkStatus.textContent(),
        'Postmark token clear is selected. No token is currently stored; saving keeps it unconfigured.');
    }
    await clearPostmark.uncheck();
    assert.equal(await postmarkInput.getAttribute('placeholder') || '', systemCredentialFlags.postmarkToken ? '********' : '');
    assert.equal(await postmarkStatus.textContent(), systemCredentialFlags.postmarkToken
      ? 'A system Postmark token is stored. Leave blank to keep it; enter a new token to replace it.'
      : 'No system Postmark token is stored.');
    assert.equal(await page.locator('#settings-save').isDisabled(), true,
      'Canceling clear restores the accepted presentation without saving.');
    assert.equal(postedPayloads.length, 0, 'Placeholder and clear-preview checks send no settings POST.');

    const timeoutFieldDefinitions = [
      { key: 'outstandingTimeoutEnabled', id: 'outstanding-timeout-enabled', kind: 'checkbox' },
      { key: 'outstandingTimeoutDays', id: 'outstanding-timeout-days', kind: 'number' },
      { key: 'outstandingTimeoutSendEmail', id: 'outstanding-timeout-send-email', kind: 'checkbox' },
      { key: 'outstandingTimeoutRejectionTemplateId', id: 'outstanding-timeout-rejection-template-id', kind: 'select' },
      { key: 'holdPickupTimeoutEnabled', id: 'hold-pickup-timeout-enabled', kind: 'checkbox' },
      { key: 'holdPickupTimeoutDays', id: 'hold-pickup-timeout-days', kind: 'number' },
      { key: 'pendingHoldTimeoutEnabled', id: 'pending-hold-timeout-enabled', kind: 'checkbox' },
      { key: 'pendingHoldTimeoutDays', id: 'pending-hold-timeout-days', kind: 'number' },
      { key: 'additionalCopyTimeoutEnabled', id: 'additional-copy-timeout-enabled', kind: 'checkbox' },
      { key: 'additionalCopyTimeoutDays', id: 'additional-copy-timeout-days', kind: 'number' }
    ];
    const timeoutParentIds = [
      'outstanding-timeout-enabled', 'hold-pickup-timeout-enabled',
      'pending-hold-timeout-enabled', 'additional-copy-timeout-enabled'
    ];
    const timeoutSnapshot = async () => page.evaluate(definitions => Object.fromEntries(definitions.map(({ key, id, kind }) => {
      const control = document.getElementById(id);
      return [key, kind === 'checkbox' ? control.checked : control.value];
    })), timeoutFieldDefinitions);
    const setTimeoutSnapshot = async values => {
      for (const id of timeoutParentIds) await page.locator(`#${id}`).check();
      await page.locator('#outstanding-timeout-send-email').check();
      for (const { key, id, kind } of timeoutFieldDefinitions) {
        if (kind === 'checkbox') continue;
        const control = page.locator(`#${id}`);
        const value = values[key] ?? '';
        if (kind === 'select') await control.selectOption(String(value));
        else await control.fill(String(value));
      }
      await page.locator('#outstanding-timeout-send-email').setChecked(Boolean(values.outstandingTimeoutSendEmail));
      for (const { key, id } of timeoutFieldDefinitions.filter(field => timeoutParentIds.includes(field.id))) {
        await page.locator(`#${id}`).setChecked(Boolean(values[key]));
      }
    };
    const systemTimeoutSnapshot = await timeoutSnapshot();
    await page.locator('#settings-nav-workflow').click();
    const testTemplateId = await page.locator('#outstanding-timeout-rejection-template-id option')
      .evaluateAll(options => options.find(option => option.textContent.trim() === 'Timeout dependency browser template')?.value || '');
    assert.notEqual(testTemplateId, '', 'The SQL fixture must seed a selectable rejection template for timeout retention.');
    const testTimeouts = {
      outstandingTimeoutEnabled: true,
      outstandingTimeoutDays: '43',
      outstandingTimeoutSendEmail: true,
      outstandingTimeoutRejectionTemplateId: testTemplateId,
      holdPickupTimeoutEnabled: true,
      holdPickupTimeoutDays: '9',
      pendingHoldTimeoutEnabled: true,
      pendingHoldTimeoutDays: '17',
      additionalCopyTimeoutEnabled: true,
      additionalCopyTimeoutDays: '24'
    };
    await setTimeoutSnapshot(testTimeouts);
    const configuredTimeoutPayload = await saveSettings();
    for (const { key, kind } of timeoutFieldDefinitions) {
      const expected = kind === 'checkbox' ? testTimeouts[key] : kind === 'number'
        ? Number(testTimeouts[key]) : testTimeouts[key] || null;
      assert.equal(configuredTimeoutPayload.workflow[key], expected, `${key} test value was submitted.`);
    }
    assert.equal(await page.getByRole('group', { name: 'Auto-reject unreviewed suggestions' }).count(), 1);
    assert.equal(await page.getByRole('group', { name: 'Send auto-rejection email' }).count(), 1);
    const outstandingDaysInput = page.locator('#outstanding-timeout-days');
    const outstandingEmailInput = page.locator('#outstanding-timeout-send-email');
    const rejectionTemplateInput = page.locator('#outstanding-timeout-rejection-template-id');
    const holdDaysInput = page.locator('#hold-pickup-timeout-days');
    const pendingDaysInput = page.locator('#pending-hold-timeout-days');
    const additionalDaysInput = page.locator('#additional-copy-timeout-days');
    const keyboardToggle = async locator => { await locator.focus(); await locator.press('Space'); };
    await keyboardToggle(page.locator('#outstanding-timeout-enabled'));
    assert.equal(await page.locator('#outstanding-timeout-enabled').isChecked(), false);
    assert.equal(await outstandingDaysInput.isDisabled(), true);
    assert.equal(await outstandingEmailInput.isDisabled(), true);
    assert.equal(await rejectionTemplateInput.isDisabled(), true);
    assert.equal(await outstandingDaysInput.inputValue(), '43');
    assert.equal(await outstandingEmailInput.isChecked(), true);
    assert.equal(await rejectionTemplateInput.inputValue(), testTemplateId);
    await keyboardToggle(page.locator('#outstanding-timeout-enabled'));
    assert.equal(await outstandingDaysInput.isDisabled(), false);
    assert.equal(await outstandingEmailInput.isDisabled(), false);
    await keyboardToggle(outstandingEmailInput);
    assert.equal(await rejectionTemplateInput.isDisabled(), true);
    assert.equal(await rejectionTemplateInput.inputValue(), testTemplateId);
    const emailOffTimeoutPayload = await saveSettings();
    assert.equal(emailOffTimeoutPayload.workflow.outstandingTimeoutEnabled, undefined, 'The unchanged main flag is preserved by omission.');
    assert.equal(emailOffTimeoutPayload.workflow.outstandingTimeoutSendEmail, false);
    assert.equal(emailOffTimeoutPayload.workflow.outstandingTimeoutRejectionTemplateId, undefined,
      'Saving with only the email option off retains the nonblank template choice.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await page.locator('#outstanding-timeout-enabled').isChecked(), true);
    assert.equal(await outstandingDaysInput.isDisabled(), false,
      'Days stay enabled while the main rule is on, independent of the email option.');
    assert.equal(await outstandingEmailInput.isChecked(), false);
    assert.equal(await outstandingEmailInput.isDisabled(), false);
    assert.equal(await rejectionTemplateInput.inputValue(), testTemplateId,
      'The nonblank template selection survives an accepted save and full page reload.');
    assert.equal(await rejectionTemplateInput.isDisabled(), true,
      'The template remains disabled while its email option is off.');
    await keyboardToggle(outstandingEmailInput);
    assert.equal(await rejectionTemplateInput.isDisabled(), false);
    for (const [parentId, daysInput, expected] of [
      ['hold-pickup-timeout-enabled', holdDaysInput, '9'],
      ['pending-hold-timeout-enabled', pendingDaysInput, '17'],
      ['additional-copy-timeout-enabled', additionalDaysInput, '24']
    ]) {
      await keyboardToggle(page.locator(`#${parentId}`));
      assert.equal(await daysInput.isDisabled(), true, `${parentId} disables its days input.`);
      assert.equal(await daysInput.inputValue(), expected, `${parentId} retains its disabled days value.`);
      await keyboardToggle(page.locator(`#${parentId}`));
      assert.equal(await daysInput.isDisabled(), false, `${parentId} re-enables its days input.`);
    }
    for (const id of timeoutParentIds) await page.locator(`#${id}`).uncheck();
    assert.equal(await outstandingDaysInput.isDisabled(), true);
    assert.equal(await outstandingEmailInput.isDisabled(), true);
    assert.equal(await rejectionTemplateInput.isDisabled(), true);
    assert.equal(await holdDaysInput.isDisabled(), true);
    assert.equal(await pendingDaysInput.isDisabled(), true);
    assert.equal(await additionalDaysInput.isDisabled(), true);
    const disabledTimeoutPayload = await saveSettings();
    for (const [key, expected] of Object.entries({
      outstandingTimeoutEnabled: false,
      outstandingTimeoutDays: undefined,
      outstandingTimeoutSendEmail: true,
      outstandingTimeoutRejectionTemplateId: undefined,
      holdPickupTimeoutEnabled: false,
      holdPickupTimeoutDays: undefined,
      pendingHoldTimeoutEnabled: false,
      pendingHoldTimeoutDays: undefined,
      additionalCopyTimeoutEnabled: false,
      additionalCopyTimeoutDays: undefined
    })) assert.equal(disabledTimeoutPayload.workflow[key], expected, `${key} is changed explicitly or preserved by omission while disabled.`);
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    await page.locator('#settings-nav-workflow').click();
    for (const [id, value] of [
      ['outstanding-timeout-days', '43'], ['outstanding-timeout-send-email', true],
      ['outstanding-timeout-rejection-template-id', testTemplateId], ['hold-pickup-timeout-days', '9'],
      ['pending-hold-timeout-days', '17'], ['additional-copy-timeout-days', '24']
    ]) {
      const input = page.locator(`#${id}`);
      assert.equal(id === 'outstanding-timeout-send-email' ? await input.isChecked() : await input.inputValue(), value,
        `${id} round-trips while its parent is off.`);
    }
    assert.equal(await outstandingDaysInput.isDisabled(), true);
    assert.equal(await outstandingEmailInput.isDisabled(), true);
    assert.equal(await rejectionTemplateInput.isDisabled(), true);
    assert.equal(await holdDaysInput.isDisabled(), true);
    assert.equal(await pendingDaysInput.isDisabled(), true);
    assert.equal(await additionalDaysInput.isDisabled(), true);
    report.systemTimeoutDependenciesRoundTrip = true;
    const preservedSystemTimeouts = {
      ...systemTimeoutSnapshot,
      outstandingTimeoutEnabled: true,
      holdPickupTimeoutEnabled: true,
      pendingHoldTimeoutEnabled: true,
      additionalCopyTimeoutEnabled: true
    };
    await setTimeoutSnapshot(preservedSystemTimeouts);
    await saveSettings();
    await page.setViewportSize({ width: 1280, height: 900 });
    await scan(page, axeSource, args.artifactRoot, report, 'system-desktop', true);
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'system-mobile', true);
    await page.setViewportSize({ width: 1280, height: 900 });

    await runLibraryParticipation(page, axeSource, args, report, waitForSettingsReady, postedPayloads);

    await page.locator('#settings-nav-workflow').click();
    const systemCodeRows = page.locator('#patron-codes-editor [data-code-id]');
    assert.deepEqual(await systemCodeRows.evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2'],
      'The system patron-code snapshot must begin with numeric IDs 1 and 2.');
    const codePicker = page.locator('#patron-code-picker');
    await codePicker.fill('3');
    await codePicker.press('ArrowDown');
    await codePicker.press('Enter');
    const systemCodesPayload = await saveSettings();
    assert.deepEqual(systemCodesPayload.workflow.allowedPatronCodeIds, [1, 2, 3],
      'Adding a system patron-code chip must preserve existing numeric IDs and submit the new ID.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    await page.locator('#settings-nav-workflow').click();
    const reloadedSystemCodes = page.locator('#patron-codes-editor [data-code-id]');
    assert.deepEqual(await reloadedSystemCodes.evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2', '3']);
    report.systemPatronCodesRoundTrip = true;

    await page.locator('#settings-nav-start').click();
    await page.locator('#patron-embed-allowed-origins').fill('https://*.DOMAIN.EXAMPLE');
    const unrelatedPayload = await saveSettings();
    assert.deepEqual(unrelatedPayload.systemSettings.patronEmbedAllowedOrigins, ['https://*.DOMAIN.EXAMPLE']);
    assert.equal(Object.hasOwn(unrelatedPayload.systemSettings, 'enabledLibraryOrgIds'), false,
      'An unrelated system edit must omit the participation replacement key.');
    assert.equal(Object.hasOwn(unrelatedPayload.workflow, 'allowedPatronCodeIds'), false,
      'An unrelated system edit must omit the patron-code replacement key.');
    assert.equal(Object.hasOwn(unrelatedPayload.polaris, 'apiKey'), false);
    assert.equal(Object.hasOwn(unrelatedPayload.polaris, 'adminPassword'), false);
    assert.equal(Object.hasOwn(unrelatedPayload.email, 'postmarkToken'), false);
    assert.equal(Object.hasOwn(unrelatedPayload.email, 'clearPostmarkToken'), false);
    assert.equal(JSON.stringify(unrelatedPayload).includes('********'), false,
      'Unrelated system saves never transmit the visual credential masks.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    assert.equal(await page.locator('#patron-embed-allowed-origins').inputValue(), 'https://*.domain.example');
    for (const id of ['2', '3', '4']) {
      assert.equal(await page.locator(`#settings-organizations-list [data-organization-id="${id}"] .status-badge`).textContent(),
        id === '4' ? 'Inactive' : 'Active',
        `The unrelated save changed library ${id} participation.`);
    }
    await page.locator('#settings-nav-workflow').click();
    assert.deepEqual(await page.locator('#patron-codes-editor [data-code-id]')
      .evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2', '3'],
    'The unrelated system edit must preserve all saved numeric patron-code IDs.');
    report.unrelatedSystemSavePreservedParticipation = true;
    report.systemUnrelatedSavePreservedPatronCodes = true;
    report.wildcardOriginNormalized = true;

    const autoClaimStaffId = '9007199254740993';
    const librarySettingsResponse = page.waitForResponse(candidate => {
      const url = new URL(candidate.url());
      return candidate.request().method() === 'GET' && url.pathname === '/api/asap/staff/settings' &&
        url.searchParams.get('orgId') === '2';
    });
    await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=2#settings-patron`, { waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    const initialLibrarySettings = await (await librarySettingsResponse).json();
    assert.equal(initialLibrarySettings.autoClaimStaff.some(staff => staff.id === autoClaimStaffId), true,
      'The actual library settings snapshot must load the exact eligible SQL bigint staff identity before any save.');
    const originalAutoClaimRules = initialLibrarySettings.stored.autoClaimRules
      .filter(rule => rule.active !== false)
      .map(rule => ({ materialFormatId: rule.materialFormatId, staffUserId: rule.staffUserId }));

    await page.locator('#settings-nav-patron').click();
    assert.equal(await page.locator('#patron-login-note').isEditable(), true);
    await page.locator('#patron-login-note').fill('Initial unrelated library scalar edit');
    const initialScalarPayload = await saveSettings();
    assert.equal(initialScalarPayload.patron.loginNote, 'Initial unrelated library scalar edit');
    for (const collection of ['autoClaimRules', 'customFields', 'formatRules', 'formats', 'providers', 'templates']) {
      assert.equal(Object.hasOwn(initialScalarPayload, collection), false,
        `An unrelated scalar save must omit the unchanged ${collection} replacement collection.`);
    }
    assert.equal(Object.hasOwn(initialScalarPayload.workflow, 'allowedPatronCodeIds'), false,
      'An unrelated scalar save must omit the unchanged patron-code replacement collection.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-patron').click();
    assert.equal(await page.locator('#patron-login-note').inputValue(), 'Initial unrelated library scalar edit',
      'The unrelated library scalar must remain authoritative after reload.');
    const initialAutoClaimRulesAfterScalar = await page.locator('#format-claim-rules-editor [data-domain-row]').evaluateAll(rows => rows.map(row => {
      const selects = row.querySelectorAll('select');
      return { materialFormatId: selects[0]?.value || '', staffUserId: selects[1]?.value || '' };
    }));
    assert.deepEqual(initialAutoClaimRulesAfterScalar, originalAutoClaimRules,
      'An unrelated scalar save and authoritative reload must preserve every original active auto-claim rule.');
    report.libraryInitialScalarSavePreservedAutoClaimRules = true;

    // Complete this presentation probe before creating the unrelated code and
    // auto-claim drafts: Library edits now deliberately retain pin/reset intent.
    await page.locator('#settings-nav-patron').click();
    await verifyFormatRuleDraftNames(page);

    await page.locator('#settings-nav-workflow').click();
    await page.waitForFunction(() => document.getElementById('settings-scope')?.value === '2');

    assert.equal(await codePicker.isEnabled(), true);
    const libraryCodeRows = page.locator('#patron-codes-editor [data-code-id]');
    assert.deepEqual(await libraryCodeRows.evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2'],
      'The library override must begin with numeric IDs 1 and 2.');
    await codePicker.fill('3');
    await codePicker.press('ArrowDown');
    await codePicker.press('Enter');

    await page.locator('#settings-nav-patron').click();
    const autoClaimEditor = page.locator('#format-claim-rules-editor');
    const existingAutoClaimRules = await autoClaimEditor.locator('[data-domain-row]').evaluateAll(rows => rows.map(row => {
      const selects = row.querySelectorAll('select');
      return { materialFormatId: selects[0]?.value || '', staffUserId: selects[1]?.value || '' };
    }));
    const existingAutoClaimFormatIds = new Set(existingAutoClaimRules.map(rule => rule.materialFormatId));
    await page.locator('#add-auto-claim-rule').click();
    const autoClaimRow = page.locator('#format-claim-rules-editor [data-domain-row]').last();
    const autoClaimSelects = autoClaimRow.locator('select');
    const availableAutoClaimFormats = await autoClaimSelects.nth(0).locator('option').evaluateAll(options =>
      options.filter(option => option.value).map(option => ({
        id: option.value,
        code: [...document.querySelectorAll('#material-formats-editor [data-domain-row]')]
          .find(row => row.dataset.formatId === option.value)?.dataset.formatCode || ''
      })));
    const systemAutoClaimCodes = new Set(['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook']);
    const autoClaimFormat = availableAutoClaimFormats.find(option => systemAutoClaimCodes.has(option.code) &&
      !existingAutoClaimFormatIds.has(option.id));
    assert.ok(autoClaimFormat,
      'The fixture must expose an unused System format so the new library rule does not replace an existing rule.');
    const autoClaimFormatId = autoClaimFormat.id;
    const autoClaimFormatCode = autoClaimFormat.code;
    await autoClaimSelects.nth(0).selectOption(autoClaimFormatId);
    const exactStaffOption = await autoClaimSelects.nth(1).locator('option').evaluateAll((options, staffId) =>
      options.some(option => option.value === staffId), autoClaimStaffId);
    assert.equal(exactStaffOption, true,
      'The SQL bigint catalog identity must be present as an exact staff-option string.');
    await autoClaimSelects.nth(1).selectOption(autoClaimStaffId);
    assert.equal(await autoClaimSelects.nth(1).inputValue(), autoClaimStaffId,
      'The editor must retain the exact decimal SQL bigint staff identity.');

    await page.locator('#settings-nav-patron').click();
    const fields = page.locator('#additional-fields-editor [data-domain-row]');
    await fields.first().waitFor({ state: 'visible' });
    const originalKeys = await fields.evaluateAll(rows => rows.map(row => row.dataset.fieldKey));
    assert.ok(originalKeys.includes('authority_retired'), 'The seeded retired field should be present before adding a dynamic editor row.');
    await page.locator('#add-custom-field').click();
    const added = fields.last();
    const dynamicKey = await added.getAttribute('data-field-key');
    await added.locator('[data-custom-field="label"]').fill('Browser choice');
    await added.locator('select').selectOption('select');
    const options = added.locator('[data-options-editor]');
    await options.getByRole('button', { name: /Add option/i }).click();
    await options.locator('[data-option-row]').nth(0).locator('[data-option-field="label"]').fill('Zeta');
    await options.getByRole('button', { name: /Add option/i }).click();
    await options.locator('[data-option-row]').nth(1).locator('[data-option-field="label"]').fill('Alpha');
    const dynamicOptionIds = await options.locator('[data-option-row]').evaluateAll(rows => rows.map(row => row.dataset.optionId));
    report.dynamicFieldIdentity = { key: dynamicKey, optionIds: dynamicOptionIds };

    const retiredRule = page.locator('#format-rules-editor [data-rule-code="book"] [data-custom-rule-key="authority_retired"]');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'optional');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Historical label');
    const addedRule = page.locator(`#format-rules-editor [data-rule-code="book"] [data-custom-rule-key="${dynamicKey}"]`);
    await page.locator('#format-rules-editor [data-rule-code="book"] [data-rule-toggle]').click();
    await addedRule.locator('[data-custom-rule-property="mode"]').selectOption('required');
    await addedRule.locator('[data-custom-rule-property="labelOverride"]').fill('Browser choice');
    await page.locator('#format-rules-editor [data-rule-code="book"] [data-rule-toggle]').click();

    const submitted = await saveSettings();
    postedPayload = submitted;
    assert.deepEqual(postedPayload.workflow.allowedPatronCodeIds, [1, 2, 3]);
    assert.equal(postedPayload.autoClaimRules.length, existingAutoClaimRules.length + 1,
      'Adding a rule must retain every pre-existing active library rule.');
    for (const existingRule of existingAutoClaimRules) {
      assert.ok(postedPayload.autoClaimRules.some(rule => rule.materialFormatId === existingRule.materialFormatId &&
        rule.staffUserId === existingRule.staffUserId && rule.active === true),
      `Adding a rule must preserve existing auto-claim format ${existingRule.materialFormatId}.`);
    }
    assert.ok(postedPayload.autoClaimRules.some(rule => rule.materialFormatId === autoClaimFormatId &&
      rule.staffUserId === autoClaimStaffId && rule.active === true),
    'Saving the library auto-claim rule must submit the exact SQL bigint staff ID.');
    const submittedField = postedPayload.customFields.find(field => field.key === dynamicKey);
    assert.ok(submittedField, 'The dynamically added custom field was missing from the submitted payload.');
    assert.deepEqual(submittedField.options.map(option => option.id), dynamicOptionIds);
    const submittedRule = postedPayload.formatRules.find(rule => rule.code === 'book');
    assert.equal(submittedRule.customFields.authority_retired.mode, 'optional');
    assert.equal(submittedRule.customFields.authority_retired.labelOverride, 'Historical label');
    assert.equal(submittedRule.customFields[dynamicKey].mode, 'required');
    assert.equal(submittedRule.customFields[dynamicKey].labelOverride, 'Browser choice');
    await page.locator('#settings-message').filter({ hasText: 'Settings saved.' }).waitFor();
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(!await librarySetting(page.locator('#patron-codes-inherit-field')), false);
    assert.deepEqual(await page.locator('#patron-codes-editor [data-code-id]')
      .evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2', '3']);
    await page.locator('#settings-nav-patron').click();
    const reloadedAutoClaimRules = await autoClaimEditor.locator('[data-domain-row]').evaluateAll(rows => rows.map(row => {
      const selects = row.querySelectorAll('select');
      return { materialFormatId: selects[0]?.value || '', staffUserId: selects[1]?.value || '' };
    }));
    assert.equal(reloadedAutoClaimRules.length, existingAutoClaimRules.length + 1,
      'The authoritative reload must retain the pre-existing active rules and the new rule.');
    for (const existingRule of existingAutoClaimRules) {
      assert.ok(reloadedAutoClaimRules.some(rule => rule.materialFormatId === existingRule.materialFormatId &&
        rule.staffUserId === existingRule.staffUserId),
      `Reloading must preserve existing auto-claim format ${existingRule.materialFormatId}.`);
    }
    assert.ok(reloadedAutoClaimRules.some(rule => rule.materialFormatId === autoClaimFormatId &&
      rule.staffUserId === autoClaimStaffId),
      'Reloading the real settings endpoint must preserve the exact SQL bigint staff ID.');
    report.libraryAutoClaimRuleBigintRoundTrip = true;
    report.libraryPatronCodesRoundTrip = true;
    await page.locator('#settings-nav-patron').click();
    const reloadedFields = page.locator('#additional-fields-editor [data-domain-row]');
    const reloadedKeys = await reloadedFields.evaluateAll(rows => rows.map(row => row.dataset.fieldKey));
    assert.ok(reloadedKeys.indexOf('authority_retired') < reloadedKeys.indexOf(dynamicKey));
    const reloadedDynamicField = reloadedFields.nth(reloadedKeys.indexOf(dynamicKey));
    assert.deepEqual(await reloadedDynamicField
      .locator('[data-option-row]').evaluateAll(rows => rows.map(row => row.dataset.optionId)), dynamicOptionIds);
    assert.equal(await retiredRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'optional');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Historical label');
    assert.equal(await addedRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'required');
    assert.equal(await addedRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Browser choice');

    await page.locator('#settings-nav-patron').click();
    await page.locator('#patron-login-note').fill('Unrelated library setting edit');
    const unrelatedLibraryPayload = await saveSettings();
    assert.equal(unrelatedLibraryPayload.patron.loginNote, 'Unrelated library setting edit',
      'The unrelated edit must intentionally override the inherited login note.');
    assert.equal(Object.hasOwn(unrelatedLibraryPayload, 'autoClaimRules'), false,
      'An unrelated scalar edit must omit the auto-claim replacement collection.');
    assert.equal(Object.hasOwn(unrelatedLibraryPayload.workflow, 'allowedPatronCodeIds'), false,
      'An unrelated library edit must omit the patron-code replacement key.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.deepEqual(await page.locator('#patron-codes-editor [data-code-id]')
      .evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2', '3'],
    'The unrelated library edit must preserve its complete numeric patron-code override.');
    await page.locator('#settings-nav-patron').click();
    const preservedAutoClaimRules = await autoClaimEditor.locator('[data-domain-row]').evaluateAll(rows => rows.map(row => {
      const selects = row.querySelectorAll('select');
      return { materialFormatId: selects[0]?.value || '', staffUserId: selects[1]?.value || '' };
    }));
    assert.equal(preservedAutoClaimRules.length, existingAutoClaimRules.length + 1,
      'An unrelated save and authoritative reload must preserve the complete active auto-claim rule set.');
    for (const expectedRule of [...existingAutoClaimRules, { materialFormatId: autoClaimFormatId, staffUserId: autoClaimStaffId }]) {
      assert.ok(preservedAutoClaimRules.some(rule => rule.materialFormatId === expectedRule.materialFormatId &&
        rule.staffUserId === expectedRule.staffUserId),
      `An unrelated save must preserve auto-claim format ${expectedRule.materialFormatId}.`);
    }
    report.libraryUnrelatedSavePreservedAutoClaimRules = true;
    report.libraryAutoClaimFormatCode = autoClaimFormatCode;
    report.libraryUnrelatedSavePreservedPatronCodes = true;

    await page.locator('#settings-nav-workflow').click();
    while (await page.locator('#patron-codes-editor button').count()) await page.locator('#patron-codes-editor button').first().click();
    const emptyLibraryCodesPayload = await saveSettings();
    assert.deepEqual(emptyLibraryCodesPayload.workflow.allowedPatronCodeIds, [],
      'Removing the library code chips must submit an intentional empty replacement set.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(!await librarySetting(page.locator('#patron-codes-inherit-field')), false,
      'An empty replacement set must remain a library override.');
    assert.equal(await page.locator('#patron-codes-editor [data-code-id]').count(), 0,
      'An empty replacement set must round-trip as an empty editor.');
    report.libraryEmptyPatronCodeReplacementRoundTrip = true;

    await useSystemValue(page, page.locator('#patron-codes-inherit-field'), true);
    const resetLibraryCodesPayload = await saveSettings();
    assert.equal(resetLibraryCodesPayload.workflow.allowedPatronCodeIds, null,
      'Use system must send an explicit reset, not an empty replacement.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(!await librarySetting(page.locator('#patron-codes-inherit-field')), true);
    assert.deepEqual(await page.locator('#patron-codes-editor [data-code-id]')
      .evaluateAll(chips => chips.map(chip => chip.dataset.codeId)), ['1', '2', '3']);
    report.libraryPatronCodeResetRoundTrip = true;

    const libraryTimeoutSnapshot = await page.evaluate(definitions => Object.fromEntries(definitions.map(({ key, id, kind }) => {
      const control = document.getElementById(id);
      const override = control.closest('[data-setting-section][data-setting-key]')?.querySelector('.settings-inheritance-status');
      return [key, { overridden: override?.textContent.startsWith('Library setting') === true, value: kind === 'checkbox' ? control.checked : control.value }];
    })), timeoutFieldDefinitions);
    const libraryTimeoutOverride = key => page.locator(
      `[data-setting-section="workflow"][data-setting-key="${key}"]`);
    const libraryTimeoutControl = key => page.locator(`#${timeoutFieldDefinitions.find(field => field.key === key).id}`);
    const setLibraryTimeoutOverride = async (key, checked) => {
      if (!checked) await useSystemValue(page, libraryTimeoutOverride(key));
    };
    const setLibraryTimeoutValue = async (key, value) => {
      const definition = timeoutFieldDefinitions.find(field => field.key === key);
      const control = libraryTimeoutControl(key);
      const normalized = value ?? '';
      if (definition.kind === 'checkbox') { if (await control.isChecked() === Boolean(normalized)) await control.setChecked(!Boolean(normalized)); await control.setChecked(Boolean(normalized)); }
      else if (definition.kind === 'select') {
        if (await control.inputValue() === String(normalized)) {
          const alternate = await control.locator('option').evaluateAll((options, desired) => options.find(option => option.value !== desired)?.value, String(normalized));
          if (alternate !== undefined) await control.selectOption(alternate);
        }
        await control.selectOption(String(normalized));
      }
      else { if (await control.inputValue() === String(normalized)) await control.fill(String(Number(normalized) + 1)); await control.fill(String(normalized)); }
    };
    assert.equal(await page.getByRole('group', { name: 'Auto-reject unreviewed suggestions' }).count(), 1);
    assert.equal(await page.getByRole('group', { name: 'Send auto-rejection email' }).count(), 1);
    await setLibraryTimeoutOverride('outstandingTimeoutEnabled', true);
    await setLibraryTimeoutValue('outstandingTimeoutEnabled', true);
    await setLibraryTimeoutOverride('outstandingTimeoutDays', true);
    await setLibraryTimeoutValue('outstandingTimeoutDays', '47');
    await setLibraryTimeoutOverride('outstandingTimeoutSendEmail', true);
    await setLibraryTimeoutValue('outstandingTimeoutSendEmail', true);
    await setLibraryTimeoutOverride('outstandingTimeoutRejectionTemplateId', true);
    await setLibraryTimeoutValue('outstandingTimeoutRejectionTemplateId', testTemplateId);
    await setLibraryTimeoutValue('outstandingTimeoutEnabled', false);
    await setLibraryTimeoutOverride('holdPickupTimeoutEnabled', false);
    await setLibraryTimeoutOverride('holdPickupTimeoutDays', false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), false);
    assert.equal(await librarySetting(libraryTimeoutOverride('outstandingTimeoutEnabled')), true,
      'The test begins from an explicit false Library override over a true System parent.');
    assert.equal(await libraryTimeoutControl('holdPickupTimeoutEnabled').isChecked(), true);
    assert.equal(await librarySetting(libraryTimeoutOverride('holdPickupTimeoutDays')), false);
    assert.equal(await libraryTimeoutControl('holdPickupTimeoutDays').isDisabled(), false,
      'An inherited true parent enables a directly editable dependent.');
    const explicitLibraryTimeoutPayload = await saveSettings();
    assert.equal(explicitLibraryTimeoutPayload.workflow.outstandingTimeoutEnabled, false);
    assert.equal(explicitLibraryTimeoutPayload.workflow.outstandingTimeoutDays, 47);
    assert.equal(explicitLibraryTimeoutPayload.workflow.outstandingTimeoutSendEmail, true);
    assert.equal(explicitLibraryTimeoutPayload.workflow.outstandingTimeoutRejectionTemplateId, testTemplateId);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').isDisabled(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutSendEmail').isDisabled(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').isDisabled(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').inputValue(), '47');
    assert.equal(await libraryTimeoutControl('outstandingTimeoutSendEmail').isChecked(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').inputValue(), testTemplateId);

    await useSystemValue(page, libraryTimeoutOverride('outstandingTimeoutEnabled'));
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true,
      'Removing the explicit false override displays the inherited true System parent.');
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutSendEmail').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').inputValue(), '47');
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').inputValue(), testTemplateId);
    await libraryTimeoutControl('outstandingTimeoutEnabled').uncheck();
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), false,
      'A direct false edit prepares a Library draft.');
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').inputValue(), '47');
    await useSystemValue(page, libraryTimeoutOverride('outstandingTimeoutEnabled'));
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true);
    const resetLibraryTimeoutPayload = await saveSettings();
    assert.equal(resetLibraryTimeoutPayload.workflow.outstandingTimeoutEnabled, null,
      'The explicit false parent reset is saved as inheritance.');
    assert.equal(resetLibraryTimeoutPayload.workflow.outstandingTimeoutDays, undefined,
      'Resetting the parent keeps the independently owned days override.');
    assert.equal(resetLibraryTimeoutPayload.workflow.outstandingTimeoutSendEmail, undefined);
    assert.equal(resetLibraryTimeoutPayload.workflow.outstandingTimeoutRejectionTemplateId, undefined,
      'Resetting the parent keeps the independently owned nonblank template choice.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await librarySetting(libraryTimeoutOverride('outstandingTimeoutEnabled')), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').inputValue(), '47');
    assert.equal(await libraryTimeoutControl('outstandingTimeoutDays').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutSendEmail').isChecked(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutSendEmail').isDisabled(), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').inputValue(), testTemplateId);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutRejectionTemplateId').isDisabled(), false);

    await libraryTimeoutControl('outstandingTimeoutEnabled').uncheck();
    await useSystemValue(page, libraryTimeoutOverride('outstandingTimeoutEnabled'));
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true);
    await decideConfirmation(page, () => page.locator('#settings-scope').selectOption('system'), dialog => dialog.dismiss());
    assert.equal(await page.locator('#settings-scope').inputValue(), '2', 'Cancelling departure preserves the pending reset owner.');
    assert.match(await libraryTimeoutOverride('outstandingTimeoutEnabled').textContent(), /Using system value.*Unsaved/);
    await decideConfirmation(page, () => page.locator('#settings-scope').selectOption('system'), dialog => dialog.accept());
    await waitForSettingsReady('system');
    await page.locator('#settings-scope').selectOption('2');
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await librarySetting(libraryTimeoutOverride('outstandingTimeoutEnabled')), false);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true);
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true,
      'Scope population retires the previous unsaved timeout override draft.');
    await useSystemValue(page, libraryTimeoutOverride('outstandingTimeoutEnabled'));
    assert.equal(await libraryTimeoutControl('outstandingTimeoutEnabled').isChecked(), true);
    await page.setViewportSize({ width: 1280, height: 900 });
    await scan(page, axeSource, args.artifactRoot, report, 'library-desktop', true);
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'library-mobile', true);
    await page.setViewportSize({ width: 1280, height: 900 });

    for (const key of timeoutParentIds.map(id => timeoutFieldDefinitions.find(field => field.id === id).key)) {
      await setLibraryTimeoutOverride(key, true);
      await setLibraryTimeoutValue(key, true);
    }
    await setLibraryTimeoutOverride('outstandingTimeoutSendEmail', true);
    await setLibraryTimeoutValue('outstandingTimeoutSendEmail', true);
    for (const field of timeoutFieldDefinitions.filter(item =>
      !timeoutParentIds.includes(item.id) && item.key !== 'outstandingTimeoutSendEmail')) {
      await setLibraryTimeoutOverride(field.key, libraryTimeoutSnapshot[field.key].overridden);
      if (libraryTimeoutSnapshot[field.key].overridden) {
        await setLibraryTimeoutValue(field.key, libraryTimeoutSnapshot[field.key].value);
      }
    }
    await setLibraryTimeoutOverride('outstandingTimeoutSendEmail', libraryTimeoutSnapshot.outstandingTimeoutSendEmail.overridden);
    if (libraryTimeoutSnapshot.outstandingTimeoutSendEmail.overridden) {
      await setLibraryTimeoutValue('outstandingTimeoutSendEmail', libraryTimeoutSnapshot.outstandingTimeoutSendEmail.value);
    }
    for (const id of timeoutParentIds) {
      const key = timeoutFieldDefinitions.find(field => field.id === id).key;
      if (libraryTimeoutSnapshot[key].overridden) {
        await setLibraryTimeoutValue(key, libraryTimeoutSnapshot[key].value);
      } else {
        await setLibraryTimeoutOverride(key, false);
      }
    }
    if (!await page.locator('#settings-save').isDisabled()) await saveSettings();
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    const libraryTimeoutAfterRestore = await page.evaluate(definitions => Object.fromEntries(definitions.map(({ key, id, kind }) => {
      const control = document.getElementById(id);
      const override = control.closest('[data-setting-section][data-setting-key]')?.querySelector('.settings-inheritance-status');
      return [key, { overridden: override?.textContent.startsWith('Library setting') === true, value: kind === 'checkbox' ? control.checked : control.value }];
    })), timeoutFieldDefinitions);
    for (const { key } of timeoutFieldDefinitions) {
      assert.equal(libraryTimeoutAfterRestore[key].overridden, libraryTimeoutSnapshot[key].overridden,
        `${key} Library override ownership is restored after the browser journey.`);
      if (libraryTimeoutSnapshot[key].overridden) {
        assert.equal(libraryTimeoutAfterRestore[key].value, libraryTimeoutSnapshot[key].value,
          `${key} Library value is restored after the browser journey.`);
      }
    }

    await page.locator('#settings-scope').selectOption('system');
    await waitForSettingsReady('system');
    await page.locator('#settings-nav-workflow').click();
    await setTimeoutSnapshot(systemTimeoutSnapshot);
    if (!await page.locator('#settings-save').isDisabled()) await saveSettings();
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    await page.locator('#settings-scope').selectOption('2');
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    report.libraryTimeoutDependenciesRoundTrip = true;
    await runCommonAuthorsOptions(page, axeSource, args, report, saveSettings, waitForSettingsReady);
    await runSuggestionLimitPlaceholder(page, axeSource, args, report, saveSettings, waitForSettingsReady, postedPayloads);

    await page.close();
    await context.close();
  } finally {
    report.externalRequests = traffic.externalRequests;
    report.postedEditorPayload = postedPayload !== null;
    await fs.writeFile(path.join(args.artifactRoot, 'settings-browser-results.json'), JSON.stringify(report, null, 2), 'utf8');
    await browser.close();
  }

  assert.deepEqual(pageErrors, [], 'The settings journey raised browser errors.');
  assert.equal(traffic.externalRequests, 0, 'The settings journey requested external assets.');
  assert.equal(report.systemParticipationRoundTrip, true);
  assert.equal(report.unrelatedSystemSavePreservedParticipation, true);
  assert.equal(report.systemPatronCodesRoundTrip, true);
  assert.equal(report.systemUnrelatedSavePreservedPatronCodes, true);
  assert.equal(report.libraryPatronCodesRoundTrip, true);
  assert.equal(report.libraryUnrelatedSavePreservedPatronCodes, true);
  assert.equal(report.libraryAutoClaimRuleBigintRoundTrip, true);
  assert.equal(report.libraryUnrelatedSavePreservedAutoClaimRules, true);
  assert.equal(report.libraryInitialScalarSavePreservedAutoClaimRules, true);
  assert.equal(report.libraryEmptyPatronCodeReplacementRoundTrip, true);
  assert.equal(report.libraryPatronCodeResetRoundTrip, true);
  assert.equal(report.systemTimeoutDependenciesRoundTrip, true);
  assert.equal(report.libraryTimeoutDependenciesRoundTrip, true);
  assert.equal(report.systemCommonAuthorsRoundTrip, true);
  assert.equal(report.libraryCommonAuthorsRoundTrip, true);
  assert.equal(report.commonCreatorInteractions, true);
  assert.equal(report.commonCreatorDiscard, true);
  assert.ok(report.commonCreatorLayouts.some(layout => layout.name === 'system-desktop'));
  assert.ok(report.commonCreatorLayouts.some(layout => layout.name === 'system-mobile'));
  assert.ok(report.commonCreatorLayouts.some(layout => layout.name === 'library-inherited-desktop'));
  assert.ok(report.commonCreatorLayouts.some(layout => layout.name === 'library-owned-desktop'));
  assert.equal(report.suggestionLimitPlaceholderRoundTrip, true);
  assert.equal(report.wildcardOriginNormalized, true);
  console.log('Settings editor browser journey passed.');
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
