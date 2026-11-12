'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { rejectNativeDialogs } = require('./confirmation.cjs');

const [baseURL, artifactDirectory, staffId, tenantId, email, library, barcode, raceFormat, correctedFormat, title] = process.argv.slice(2);
const origin = new URL(baseURL).origin;
const headers = {
  'X-ASAP-Test-Staff-Id': staffId,
  'X-ASAP-Test-Tenant-Id': tenantId,
  'X-ASAP-Test-Staff-Email': email
};
const report = { externalRequests: [], errors: [], states: [], requests: [] };
let axe = '';

function isSuggestionConfiguration(request) {
  const url = new URL(request.url());
  return request.method() === 'GET' && url.pathname === '/api/asap/staff/suggestion-configuration' &&
    url.searchParams.get('libraryOrgId') === library;
}

function emptyConfiguration(libraryId) {
  return {
    libraryOrgId: Number(libraryId),
    libraryOrgName: 'Test Library',
    configuration: {
      availableFormats: ['book'],
      formatLabels: { book: 'Book' },
      formatRules: { book: { messageBehavior: 'message' } },
      publicationOptions: ['Coming soon'],
      additionalFieldDefinitions: []
    }
  };
}

async function installGuards(context, page) {
  await context.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    report.externalRequests.push(route.request().url());
    return route.abort('blockedbyclient');
  });
  page.on('pageerror', error => report.errors.push(error.message));
  rejectNativeDialogs(page, report.errors);
}

async function scan(page, name, viewport) {
  await page.evaluate(axe);
  const state = await page.evaluate(async () => ({
    violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
    width: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    overflow: document.documentElement.scrollWidth > innerWidth,
    images: [...document.images].filter(image => image.getClientRects().length)
      .every(image => image.complete && image.naturalWidth > 0),
    focused: document.activeElement?.textContent?.trim() || document.activeElement?.getAttribute('aria-label') || ''
  }));
  assert.deepEqual(state.violations, [], `${name}: serious or critical axe findings`);
  assert.equal(state.overflow, false, `${name}: horizontal overflow`);
  assert.equal(state.images, true, `${name}: visible image failed to load`);
  report.states.push({ name, viewport, ...state });
  await page.screenshot({ path: path.join(artifactDirectory, `${name}.png`), fullPage: true });
}

async function searchPatron(page, dialog, query) {
  const scope = dialog.getByRole('combobox', { name: 'Servicing library', exact: true });
  if (await scope.inputValue() !== library) await scope.selectOption(library);
  await dialog.getByRole('searchbox', { name: 'Patron barcode or name', exact: true }).fill(query);
  await dialog.getByRole('button', { name: 'Look up patron', exact: true }).click();
  await dialog.locator('.staff-suggestion-fields').waitFor();
}

async function scanRecoveryAtNativeZoom() {
  const extension = path.join(__dirname, 'native-zoom-extension');
  const context = await chromium.launchPersistentContext(path.join(artifactDirectory, 'recovery-zoom-profile'), {
    channel: 'chromium',
    headless: true,
    viewport: null,
    extraHTTPHeaders: headers,
    args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900']
  });
  try {
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
    const page = context.pages()[0] || await context.newPage();
    await installGuards(context, page);
    let mockedFirstConfig = false;
    await context.route('**/api/asap/staff/suggestion-configuration?*', route => {
      if (!mockedFirstConfig && isSuggestionConfiguration(route.request())) {
        mockedFirstConfig = true;
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(emptyConfiguration(library)) });
      }
      return route.continue();
    });
    await context.route('**/api/asap/staff/suggestions', route => {
      if (route.request().method() !== 'POST') return route.continue();
      report.requests.push({ method: 'POST', source: 'native-zoom' });
      return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({
        code: 'request_not_created_pickup_changed',
        pickupPreferenceChanged: true,
        reasonCode: 'submission_configuration_changed',
        message: 'The suggestion was not created, but the preferred pickup location changed successfully. Submission settings changed.'
      }) });
    });
    await page.goto(`${baseURL}/staff/?stage=queue&scope=${library}&status=suggestion`, { waitUntil: 'networkidle' });
    const baseDpr = await page.evaluate(() => devicePixelRatio);
    const zoom = await worker.evaluate(async siteOrigin => {
      const tab = (await chrome.tabs.query({})).find(item => item.url.startsWith(siteOrigin));
      if (!tab) throw new Error('The staff application tab must exist before changing zoom.');
      await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
      await chrome.tabs.setZoom(tab.id, 2);
      return chrome.tabs.getZoom(tab.id);
    }, origin);
    assert.equal(zoom, 2);
    await page.waitForFunction(prior => Math.abs(devicePixelRatio / prior - 2) < 0.01, baseDpr);

    const dialog = page.locator('#staff-suggestion-dialog');
    await page.getByRole('button', { name: 'New suggestion', exact: true }).click();
    await dialog.waitFor({ state: 'visible' });
    await searchPatron(page, dialog, barcode);
    const refresh = dialog.getByRole('button', { name: 'Refresh eligible formats', exact: true });
    assert.equal(await refresh.evaluate(node => node === document.activeElement), true,
      'At actual 200% browser zoom, zero eligible formats focus the refresh action.');
    await scan(page, 'suggestion-zero-eligible-native-200', 'native-200%');

    const refreshed = page.waitForResponse(response => isSuggestionConfiguration(response.request()));
    await page.keyboard.press('Enter');
    const refreshResponse = await refreshed;
    assert.equal(refreshResponse.status(), 200);
    const format = dialog.getByLabel('Material format', { exact: true });
    await page.waitForFunction(() => {
      const select = document.querySelector('#staff-suggestion-dialog [aria-label="Material format"]');
      return select && !select.disabled;
    });
    await format.selectOption(correctedFormat);
    await dialog.getByLabel(/^Title/).fill('Native zoom partial result');
    await dialog.getByLabel(/^Refreshed format author/).fill('Native zoom author');
    await dialog.getByLabel('Preferred pickup location', { exact: true }).selectOption('102');
    const create = dialog.getByRole('button', { name: 'Create suggestion', exact: true });
    await create.click();
    await page.waitForFunction(() => document.querySelector('#staff-suggestion-status')?.textContent
      .includes('pickup preference changed successfully'));
    assert.equal(await refresh.evaluate(node => node === document.activeElement), true,
      'The configuration rejection returns focus to Refresh at actual 200% browser zoom.');
    assert.equal(await create.isDisabled(), true);
    const metricsSession = await context.newCDPSession(page);
    const metrics = await metricsSession.send('Page.getLayoutMetrics');
    assert.equal(metrics.visualViewport.zoom, 2);
    await metricsSession.detach();
    await scan(page, 'suggestion-partial-recovery-native-200', 'native-200%');
    report.nativeZoom = { actualBrowserZoom: zoom, cdpZoom: metrics.visualViewport.zoom,
      devicePixelRatio: await page.evaluate(() => devicePixelRatio) };
  } finally {
    await context.close();
  }
}

async function main() {
  axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true });
  try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
  const page = await context.newPage();
  await installGuards(context, page);
  let mockedFirstConfig = false;
  await context.route('**/api/asap/staff/suggestion-configuration?*', route => {
    if (!mockedFirstConfig && isSuggestionConfiguration(route.request())) {
      mockedFirstConfig = true;
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(emptyConfiguration(library)) });
    }
    return route.continue();
  });
  const createRequests = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (request.method() === 'POST' && url.pathname === '/api/asap/staff/suggestions') {
      createRequests.push(request.postDataJSON());
      report.requests.push({ method: 'POST', format: request.postDataJSON().format });
    }
  });
  await page.goto(`${baseURL}/staff/?stage=queue&scope=${library}&status=suggestion`, { waitUntil: 'networkidle' });
  const dialog = page.locator('#staff-suggestion-dialog');
  const opener = page.getByRole('button', { name: 'New suggestion', exact: true });
  await opener.click();
  await dialog.waitFor({ state: 'visible' });
  await searchPatron(page, dialog, barcode);

  const format = dialog.getByLabel('Material format', { exact: true });
  const refresh = dialog.getByRole('button', { name: 'Refresh eligible formats', exact: true });
  assert.equal(await format.isDisabled(), true);
  assert.equal(await refresh.isVisible(), true);
  assert.match(await dialog.locator('#staff-suggestion-format-empty').textContent(), /referral route|enable a format/i);
  assert.equal(await refresh.evaluate(node => node === document.activeElement), true,
    'Zero eligible formats move focus to Refresh.');
  await scan(page, 'suggestion-zero-eligible-desktop', '1280px');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan(page, 'suggestion-zero-eligible-mobile', '390px');
  const refreshResponse = page.waitForResponse(response => isSuggestionConfiguration(response.request()));
  await page.keyboard.press('Enter');
  assert.equal((await refreshResponse).status(), 200);
  await page.waitForFunction(() => {
    const select = document.querySelector('#staff-suggestion-dialog [aria-label="Material format"]');
    return select && !select.disabled;
  });
  assert.equal(await format.isDisabled(), false);
  assert.equal(await format.inputValue(), '', 'Refresh requires an explicit format choice.');
  await page.keyboard.press('Escape');
  await dialog.waitFor({ state: 'hidden' });
  assert.equal(await opener.evaluate(node => node === document.activeElement), true,
    'Escape returns focus to the action that opened the dialog.');

  await page.setViewportSize({ width: 1280, height: 900 });
  await opener.click();
  await searchPatron(page, dialog, barcode);
  await format.selectOption(raceFormat);
  const titleInput = dialog.getByLabel(/^Title/);
  const authorInput = dialog.getByLabel(/^Author/);
  const notesInput = dialog.getByLabel('Staff notes', { exact: true });
  await titleInput.fill(title);
  await authorInput.fill('Draft author');
  await notesInput.fill('Retain these staff notes');
  await dialog.getByLabel('Preferred pickup location', { exact: true }).selectOption('102');
  await scan(page, 'suggestion-before-partial-desktop', '1280px');

  const partialResponse = page.waitForResponse(response => response.request().method() === 'POST' &&
    new URL(response.url()).pathname === '/api/asap/staff/suggestions');
  await dialog.getByRole('button', { name: 'Create suggestion', exact: true }).click();
  const rejected = await partialResponse;
  assert.equal(rejected.status(), 409, await rejected.text());
  const rejection = await rejected.json();
  assert.equal(rejection.code, 'request_not_created_pickup_changed');
  assert.equal(rejection.pickupPreferenceChanged, true);
  assert.equal(rejection.reasonCode, 'submission_configuration_changed');
  await page.waitForFunction(() => document.querySelector('#staff-suggestion-status')?.textContent
    .includes('pickup preference changed successfully'));
  assert.match(await dialog.locator('#staff-suggestion-pickup-update-notice').textContent(),
    /changed to North Branch.*not created.*will not repeat or reverse/i);
  assert.equal(await titleInput.inputValue(), title);
  assert.equal(await authorInput.inputValue(), 'Draft author');
  assert.equal(await notesInput.inputValue(), 'Retain these staff notes');
  assert.equal(await dialog.getByRole('button', { name: 'Create suggestion', exact: true }).isDisabled(), true,
    'The first configuration rejection prevents another stale submission.');
  assert.equal(await refresh.evaluate(node => node === document.activeElement), true,
    `Configuration rejection focus: ${await page.evaluate(() => `${document.activeElement?.tagName} ${document.activeElement?.textContent?.trim()}`)}`);
  assert.equal(createRequests.length, 1);
  await dialog.locator('form').evaluate(form => form.requestSubmit());
  await page.waitForTimeout(100);
  assert.equal(createRequests.length, 1, 'Submitting the stale draft again sends no second request.');
  await scan(page, 'suggestion-partial-recovery-desktop', '1280px');
  await page.setViewportSize({ width: 390, height: 844 });
  await scan(page, 'suggestion-partial-recovery-mobile', '390px');

  const refreshedConfiguration = page.waitForResponse(response => isSuggestionConfiguration(response.request()));
  await page.keyboard.press('Enter');
  assert.equal((await refreshedConfiguration).status(), 200);
  await page.waitForFunction(({ currentCode, staleCode }) => {
    const select = document.querySelector('[aria-label="Material format"]');
    return select && select.value === '' && [...select.options].some(option => option.value === currentCode) &&
      ![...select.options].some(option => option.value === staleCode);
  }, { currentCode: correctedFormat, staleCode: raceFormat });
  assert.equal(await format.inputValue(), '', 'A removed format is cleared and requires explicit reselection.');
  assert.equal(await format.locator(`option[value="${raceFormat}"]`).count(), 0);
  assert.equal(await titleInput.inputValue(), title);
  assert.equal(await authorInput.inputValue(), 'Draft author');
  assert.equal(await notesInput.inputValue(), 'Retain these staff notes');
  await page.setViewportSize({ width: 1280, height: 900 });

  await format.focus();
  const correctedIndex = await format.locator('option').evaluateAll((options, code) => options.findIndex(option => option.value === code), correctedFormat);
  assert.ok(correctedIndex > 0);
  for (let index = 0; index < correctedIndex; index += 1) await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  assert.equal(await format.inputValue(), correctedFormat, 'Keyboard navigation explicitly selects a current format.');
  const refreshedAuthor = dialog.getByLabel(/^Refreshed format author/);
  assert.equal(await refreshedAuthor.count(), 1, 'The current field label is applied after selecting the refreshed format.');
  assert.equal(await refreshedAuthor.evaluate(node => node.required), true,
    'The current required field mode is applied after selecting the refreshed format.');
  const createdResponse = page.waitForResponse(response => response.request().method() === 'POST' &&
    new URL(response.url()).pathname === '/api/asap/staff/suggestions');
  await dialog.getByRole('button', { name: 'Create suggestion', exact: true }).focus();
  await page.keyboard.press('Enter');
  const created = await createdResponse;
  assert.equal(created.status(), 201, await created.text());
  assert.equal(createRequests.length, 2);
  assert.equal(createRequests[1].format, correctedFormat);
  assert.equal(createRequests[1].preferredPickupBranchId, 102);
  assert.equal(createRequests[1].currentPreferredPickupBranchIdAtLoad, 102,
    'Corrected creation uses the pickup preference confirmed by the partial-success response.');
  await dialog.waitFor({ state: 'hidden' });
  assert.deepEqual(report.errors, [], 'Browser page errors');
  assert.deepEqual(report.externalRequests, [], 'Unexpected external requests');
  await scanRecoveryAtNativeZoom();
  assert.deepEqual(report.errors, [], 'Browser page errors including native zoom');
  assert.deepEqual(report.externalRequests, [], 'Unexpected external requests including native zoom');
  await fs.writeFile(path.join(artifactDirectory, 'browser-results.json'), JSON.stringify(report, null, 2));
  console.log(`Staff suggestion recovery passed: ${report.states.length} scanned states; ${createRequests.length} real create requests.`);
  } finally {
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
