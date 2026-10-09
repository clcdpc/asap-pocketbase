'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 5) {
    throw new Error('Usage: node tests/browser/settings.cjs <baseURL> <artifactDirectory> <staffId> <tenantId> <email>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    identity: { staffId: argv[2], tenantId: argv[3], email: argv[4] }
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

async function main() {
  const args = parseArguments(process.argv.slice(2));
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
    libraryEmptyPatronCodeReplacementRoundTrip: false, libraryPatronCodeResetRoundTrip: false,
    wildcardOriginNormalized: false };
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
      }
    });

    const saveSettings = async () => {
      const response = page.waitForResponse(candidate => candidate.request().method() === 'POST' &&
        new URL(candidate.url()).pathname === '/api/asap/staff/settings');
      await page.locator('#settings-save').click();
      const saved = await response;
      assert.equal(saved.status(), 200, await saved.text());
      await page.locator('#settings-message').filter({ hasText: 'Settings saved.' }).waitFor();
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

    await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=system#settings-start`, { waitUntil: 'networkidle' });
    await waitForSettingsReady('system');
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

    await page.goto(`${args.baseOrigin}/staff/?stage=settings&settingsScope=2#settings-patron`, { waitUntil: 'networkidle' });
    await waitForSettingsReady('2');

    await page.waitForFunction(() => document.getElementById('settings-scope')?.value === '2');

    await page.locator('#settings-nav-workflow').click();
    const useSystemCodes = page.locator('#patron-codes-use-system');
    if (await useSystemCodes.isChecked()) await useSystemCodes.uncheck();
    const libraryCodeRows = page.locator('#patron-codes-editor [data-domain-row] select');
    assert.deepEqual(await libraryCodeRows.evaluateAll(selects => selects.map(select => select.value)), ['1', '2'],
      'The library override must begin with numeric IDs 1 and 2.');
    await page.locator('#add-patron-code').click();
    await libraryCodeRows.last().selectOption('3');

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
    assert.equal(Object.hasOwn(unrelatedLibraryPayload.workflow, 'allowedPatronCodeIds'), false,
      'An unrelated library edit must omit the patron-code replacement key.');
    await page.reload({ waitUntil: 'networkidle' });
    await waitForSettingsReady('2');
    await page.locator('#settings-nav-workflow').click();
    assert.deepEqual(await page.locator('#patron-codes-editor [data-domain-row] select')
      .evaluateAll(selects => selects.map(select => select.value)), ['1', '2', '3'],
    'The unrelated library edit must preserve its complete numeric patron-code override.');
    report.libraryUnrelatedSavePreservedPatronCodes = true;

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
  assert.equal(report.libraryEmptyPatronCodeReplacementRoundTrip, true);
  assert.equal(report.libraryPatronCodeResetRoundTrip, true);
  assert.equal(report.wildcardOriginNormalized, true);
  console.log('Settings editor browser journey passed.');
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
