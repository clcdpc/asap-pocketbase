'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

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

async function scan(page, axeSource, artifactRoot, report, viewport) {
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(() => ({
    width: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    imagesLoaded: [...document.images]
      .filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      })
      .every(image => image.complete && image.naturalWidth > 0)
  }));
  await page.screenshot({ path: path.join(artifactRoot, `${viewport}-settings.png`), fullPage: true });
  report.states.push({ viewport, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility violation in ${viewport} settings`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow in ${viewport} settings`);
  assert.equal(layout.imagesLoaded, true, `Visible image failed to load in ${viewport} settings`);
}

async function runSystemFormatRuleEditor(page, report, waitForSettingsReady) {
  await page.locator('#settings-nav-patron').click();
  const rule = page.locator('#format-rules-editor [data-rule-code="book"]');
  await rule.locator('[data-rule-property="messageBehavior"]').selectOption('message');
  await rule.locator('[data-rule-property="message"]').fill('System format-rule integration witness');

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

async function runLibraryOverrideMetadataEditor(page, args, report, saveSettings, waitForSettingsReady) {
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
  await page.locator(`#external-search-provider-editor [data-provider-id="${providerA}"] input`).nth(1)
    .fill('Changed provider A');
  let payload = await saveSettings();
  assert.equal(payload.providers.find(item => item.id === providerB).reset, false);
  let current = await read();
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider, 'Untouched provider SQL row/version must survive sibling save.');

  await page.locator('#settings-nav-patron').click();
  await page.locator(`#material-formats-editor [data-format-id="${formatA}"] input`).nth(1)
    .fill('Changed format A');
  payload = await saveSettings();
  assert.equal(payload.formats.find(item => item.id === formatB).reset, false);
  current = await read();
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat, 'Untouched format SQL row/version must survive sibling save.');
  assert.deepEqual(rawOwned(current, sparseId), initialSparse, 'Untouched active owned SQL row/version must survive sibling save.');

  await page.locator(`#material-formats-editor [data-format-id="${customId}"] input`).nth(1)
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

  await page.locator(`#material-formats-editor [data-format-id="${formatA}"] .settings-domain-override-toggle`).uncheck();
  payload = await saveSettings();
  assert.equal(payload.formats.find(item => item.id === formatA).reset, true);
  current = await read();
  assert.equal(rawFormat(current, formatA), undefined);
  assert.deepEqual(rawFormat(current, formatB), untouchedFormat);
  assert.deepEqual(rawProvider(current, providerB), untouchedProvider);

  await page.locator('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle').check();
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
  await page.locator(`#external-search-provider-editor [data-provider-id="${providerB}"] input`).nth(1)
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
  await page.locator(`#material-formats-editor [data-format-id="${formatB}"] input`).nth(1)
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
  await providerRow.locator('input').nth(1).fill('');
  await providerRow.locator('input').nth(2).fill('');
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
  await page.locator(`#format-rules-editor [data-rule-code="${codeB}"] [data-format-label="author"]`).fill('');
  await saveSettings();
  current = await read();
  untouchedFormat = rawFormat(current, formatB);
  assert.equal(untouchedFormat.authorLabel, null);
  assert.deepEqual({ ...untouchedFormat, authorLabel: beforeAuthorLabelClear.authorLabel,
    version: beforeAuthorLabelClear.version }, beforeAuthorLabelClear, 'Clearing a format field label preserves its explicit mode and other format fields.');
  assert.equal(current.effective.formats.find(item => item.id === formatB).author.label, 'Later system creator B');

  assert.deepEqual(rawOwned(current, sparseId), initialSparse,
    'Editing inherited field rules must preserve every untouched active owned nullable field and version.');
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

async function main() {
  const args = parseArguments(process.argv.slice(2));
  if (!['full', 'system-format-rule', 'library-rule-partial-snapshot', 'library-rule-missing-row', 'library-metadata'].includes(args.mode)) {
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
    systemPatronCodesRoundTrip: false, systemUnrelatedSavePreservedPatronCodes: false,
    libraryPatronCodesRoundTrip: false, libraryUnrelatedSavePreservedPatronCodes: false,
    libraryAutoClaimRuleBigintRoundTrip: false, libraryUnrelatedSavePreservedAutoClaimRules: false,
    libraryInitialScalarSavePreservedAutoClaimRules: false,
    libraryEmptyPatronCodeReplacementRoundTrip: false, libraryPatronCodeResetRoundTrip: false,
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
    page.on('pageerror', error => pageErrors.push(error.message));
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
      await page.waitForFunction(() => {
        const form = document.getElementById('settings-form');
        const message = document.getElementById('settings-message');
        return Boolean(form && !form.hidden) ||
          Boolean(message?.classList.contains('error') && message.textContent.trim());
      });
      const message = page.locator('#settings-message');
      if (await message.evaluate(element => element.classList.contains('error'))) {
        throw new Error(`Settings load failed: ${await message.textContent()}`);
      }
      await page.locator('#settings-form').waitFor({ state: 'visible' });
      assert.equal(await page.locator('#settings-scope').inputValue(), expectedScope,
        'The settings editor became ready for the wrong scope.');
    };

    const initialScope = args.mode.startsWith('library-') ? args.scopeId : 'system';
    await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=${encodeURIComponent(initialScope)}#settings-start`, { waitUntil: 'networkidle' });
    await waitForSettingsReady(initialScope);
    if (args.mode === 'library-metadata') {
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
    const participationRows = page.locator('#enabled-libraries-checkbox-container input[type="checkbox"]');
    const participationIds = await participationRows.evaluateAll(inputs => inputs.map(input => input.value));
    assert.ok(participationIds.includes('2') && participationIds.includes('3') && participationIds.includes('4'),
      'The system participation editor must expose the seeded active, new, and inactive libraries.');
    for (const checkbox of await participationRows.all()) {
      const id = await checkbox.inputValue();
      await checkbox.setChecked(id === '2' || id === '3');
    }
    const participationPayload = await saveSettings();
    assert.deepEqual(participationPayload.systemSettings.enabledLibraryOrgIds, [2, 3],
      'The browser must submit exact numeric native library IDs for an intentional participation change.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    for (const id of ['2', '3', '4']) {
      assert.equal(await page.locator(`#enabled-libraries-checkbox-container input[value="${id}"]`).isChecked(), id !== '4',
        `Library ${id} participation did not round-trip from SQL.`);
    }
    report.systemParticipationRoundTrip = true;

    await page.locator('#settings-nav-workflow').click();
    const systemCodeRows = page.locator('#patron-codes-editor [data-domain-row] select');
    assert.deepEqual(await systemCodeRows.evaluateAll(selects => selects.map(select => select.value)), ['1', '2'],
      'The system patron-code snapshot must begin with numeric IDs 1 and 2.');
    await page.locator('#add-patron-code').click();
    await systemCodeRows.last().selectOption('3');
    const systemCodesPayload = await saveSettings();
    assert.deepEqual(systemCodesPayload.workflow.allowedPatronCodeIds, [1, 2, 3],
      'Adding a system patron-code row must preserve existing numeric IDs and submit the new ID.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    await page.locator('#settings-nav-workflow').click();
    const reloadedSystemCodes = page.locator('#patron-codes-editor [data-domain-row] select');
    assert.deepEqual(await reloadedSystemCodes.evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3']);
    report.systemPatronCodesRoundTrip = true;

    await page.locator('#settings-nav-start').click();
    await page.locator('#patron-embed-allowed-origins').fill('https://*.DOMAIN.EXAMPLE');
    const unrelatedPayload = await saveSettings();
    assert.deepEqual(unrelatedPayload.systemSettings.patronEmbedAllowedOrigins, ['https://*.DOMAIN.EXAMPLE']);
    assert.equal(Object.hasOwn(unrelatedPayload.systemSettings, 'enabledLibraryOrgIds'), false,
      'An unrelated system edit must omit the participation replacement key.');
    assert.equal(Object.hasOwn(unrelatedPayload.workflow, 'allowedPatronCodeIds'), false,
      'An unrelated system edit must omit the patron-code replacement key.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
    assert.equal(await page.locator('#patron-embed-allowed-origins').inputValue(), 'https://*.domain.example');
    for (const id of ['2', '3', '4']) {
      assert.equal(await page.locator(`#enabled-libraries-checkbox-container input[value="${id}"]`).isChecked(), id !== '4',
        `The unrelated save changed library ${id} participation.`);
    }
    await page.locator('#settings-nav-workflow').click();
    assert.deepEqual(await page.locator('#patron-codes-editor [data-domain-row] select')
      .evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3'],
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
    const initialLoginNoteToggle = page.locator('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
    if (!await initialLoginNoteToggle.isChecked()) await initialLoginNoteToggle.check();
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

    await page.locator('#settings-nav-workflow').click();
    await page.waitForFunction(() => document.getElementById('settings-scope')?.value === '2');

    const useSystemCodes = page.locator('#patron-codes-use-system');
    if (await useSystemCodes.isChecked()) await useSystemCodes.uncheck();
    const libraryCodeRows = page.locator('#patron-codes-editor [data-domain-row] select');
    assert.deepEqual(await libraryCodeRows.evaluateAll(selects => selects.map(select => select.value)), ['1', '2'],
      'The library override must begin with numeric IDs 1 and 2.');
    await page.locator('#add-patron-code').click();
    await libraryCodeRows.last().selectOption('3');

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
        code: option.textContent.trim().match(/\(([^()]*)\)$/)?.[1] || ''
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
    const originalKeys = await fields.evaluateAll(rows => rows.map(row => row.querySelector('input')?.value));
    assert.ok(originalKeys.includes('authority_retired'), 'The seeded retired field should be present before adding a dynamic editor row.');
    await page.locator('#add-custom-field').click();
    const added = fields.last();
    await added.locator('input').nth(0).fill('browser_select');
    await added.locator('input').nth(1).fill('Browser choice');
    await added.locator('select').selectOption('select');
    const options = added.locator('[data-options-editor]');
    await options.getByRole('button', { name: /Add option/i }).click();
    await options.locator('[data-option-row]').nth(0).locator('input').nth(0).fill('zeta');
    await options.locator('[data-option-row]').nth(0).locator('input').nth(1).fill('Zeta');
    await options.getByRole('button', { name: /Add option/i }).click();
    await options.locator('[data-option-row]').nth(1).locator('input').nth(0).fill('alpha');
    await options.locator('[data-option-row]').nth(1).locator('input').nth(1).fill('Alpha');

    const retiredRule = page.locator('#format-rules-editor [data-rule-code="book"] [data-custom-rule-key="authority_retired"]');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'optional');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Historical label');
    const addedRule = page.locator('#format-rules-editor [data-rule-code="book"] [data-custom-rule-key="browser_select"]');
    await addedRule.locator('[data-custom-rule-property="mode"]').selectOption('required');
    await addedRule.locator('[data-custom-rule-property="labelOverride"]').fill('Browser choice');

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
    const submittedField = postedPayload.customFields.find(field => field.key === 'browser_select');
    assert.ok(submittedField, 'The dynamically added custom field was missing from the submitted payload.');
    assert.deepEqual(submittedField.options.map(option => option.id), ['zeta', 'alpha']);
    const submittedRule = postedPayload.formatRules.find(rule => rule.code === 'book');
    assert.equal(submittedRule.customFields.authority_retired.mode, 'optional');
    assert.equal(submittedRule.customFields.authority_retired.labelOverride, 'Historical label');
    assert.equal(submittedRule.customFields.browser_select.mode, 'required');
    assert.equal(submittedRule.customFields.browser_select.labelOverride, 'Browser choice');
    await page.locator('#settings-message').filter({ hasText: 'Settings saved.' }).waitFor();
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await page.locator('#patron-codes-use-system').isChecked(), false);
    assert.deepEqual(await page.locator('#patron-codes-editor [data-domain-row] select')
      .evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3']);
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
    const reloadedKeys = await reloadedFields.evaluateAll(rows => rows.map(row => row.querySelector('input')?.value));
    assert.ok(reloadedKeys.indexOf('authority_retired') < reloadedKeys.indexOf('browser_select'));
    const reloadedDynamicField = reloadedFields.nth(reloadedKeys.indexOf('browser_select'));
    assert.deepEqual(await reloadedDynamicField
      .locator('[data-option-row] input[aria-label="Option stable ID"]').evaluateAll(inputs => inputs.map(input => input.value)),
    ['zeta', 'alpha']);
    assert.equal(await retiredRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'optional');
    assert.equal(await retiredRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Historical label');
    assert.equal(await addedRule.locator('[data-custom-rule-property="mode"]').inputValue(), 'required');
    assert.equal(await addedRule.locator('[data-custom-rule-property="labelOverride"]').inputValue(), 'Browser choice');

    await page.locator('#settings-nav-patron').click();
    await page.locator('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle').check();
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
    assert.deepEqual(await page.locator('#patron-codes-editor [data-domain-row] select')
      .evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3'],
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
    await page.locator('#patron-codes-clear-all').click();
    const emptyLibraryCodesPayload = await saveSettings();
    assert.deepEqual(emptyLibraryCodesPayload.workflow.allowedPatronCodeIds, [],
      'Clearing the library code rows must submit an intentional empty replacement set.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await page.locator('#patron-codes-use-system').isChecked(), false,
      'An empty replacement set must remain a library override.');
    assert.equal(await page.locator('#patron-codes-editor [data-domain-row] select').count(), 0,
      'An empty replacement set must round-trip as an empty editor.');
    report.libraryEmptyPatronCodeReplacementRoundTrip = true;

    await page.locator('#patron-codes-use-system').check();
    const resetLibraryCodesPayload = await saveSettings();
    assert.equal(resetLibraryCodesPayload.workflow.allowedPatronCodeIds, null,
      'Use system must send an explicit reset, not an empty replacement.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.equal(await page.locator('#patron-codes-use-system').isChecked(), true);
    assert.deepEqual(await page.locator('#patron-codes-editor [data-domain-row] select')
      .evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3']);
    report.libraryPatronCodeResetRoundTrip = true;

    await scan(page, axeSource, args.artifactRoot, report, 'desktop');
    await page.setViewportSize({ width: 390, height: 844 });
    await scan(page, axeSource, args.artifactRoot, report, 'mobile');
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
  assert.equal(report.wildcardOriginNormalized, true);
  console.log('Settings editor browser journey passed.');
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
