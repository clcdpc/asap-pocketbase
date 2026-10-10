'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 11) {
    throw new Error('Usage: node tests/browser/staff-request-contracts.cjs <baseURL> <artifactDirectory> <staffId> <tenantId> <email> <requestId> <fieldKey> <retiredSelectKey> <corruptRequestId> <fieldLabel> <retiredSelectLabel>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password ||
      parsed.pathname !== '/' || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    identity: { staffId: argv[2], tenantId: argv[3], email: argv[4] },
    requestId: argv[5],
    fieldKey: argv[6],
    retiredSelectKey: argv[7],
    corruptRequestId: argv[8],
    fieldLabel: argv[9],
    retiredSelectLabel: argv[10]
  };
}

function allowedOrigin(request, baseOrigin) {
  try { return new URL(request.url()).origin === baseOrigin; } catch { return false; }
}

async function openContext(browser, viewport, baseOrigin, identity) {
  const context = await browser.newContext({
    viewport,
    extraHTTPHeaders: {
      'X-ASAP-Test-Staff-Id': identity.staffId,
      'X-ASAP-Test-Tenant-Id': identity.tenantId,
      'X-ASAP-Test-Staff-Email': identity.email
    }
  });
  const traffic = { externalRequests: 0 };
  await context.route('**/*', route => {
    if (allowedOrigin(route.request(), baseOrigin)) return route.continue();
    traffic.externalRequests += 1;
    return route.abort('blockedbyclient');
  });
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  return { context, page, traffic, pageErrors };
}

async function scan(page, axeSource, artifactRoot, fileName, report) {
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(() => {
    const images = [...document.images].filter(image => {
      const bounds = image.getBoundingClientRect();
      return bounds.width > 0 && bounds.height > 0;
    });
    return {
      width: innerWidth,
      scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: images.every(image => image.complete && image.naturalWidth > 0)
    };
  });
  await page.screenshot({ path: path.join(artifactRoot, `${fileName}.png`), fullPage: true });
  report.states.push({ fileName, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility findings in ${fileName}`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow in ${fileName}`);
  assert.equal(layout.imagesLoaded, true, `A visible image failed to load in ${fileName}`);
}

async function requestDetail(context, baseOrigin, requestId) {
  const response = await context.request.get(`${baseOrigin}/api/asap/staff/title-requests/${requestId}?scope=all`);
  assert.equal(response.status(), 200, await response.text());
  return response.json();
}

async function runDesktop(browser, args, axeSource, report) {
  const { context, page, traffic, pageErrors } = await openContext(
    browser, { width: 1280, height: 900 }, args.baseOrigin, args.identity);
  const actionBodies = [];
  page.on('request', request => {
    if (request.method() === 'POST' &&
        new URL(request.url()).pathname === `/api/asap/staff/title-requests/${args.requestId}/action`) {
      actionBodies.push(JSON.parse(request.postData() || '{}'));
    }
  });
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${encodeURIComponent(args.requestId)}`, { waitUntil: 'networkidle' });
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#request-dialog[open]').waitFor();
    const original = await requestDetail(context, args.baseOrigin, args.requestId);
    const originalVersion = original.version;
    const originalRetiredChoice = original.customFields[args.retiredSelectKey];
    const originalRetiredHistory = original.customFields.retired_history;
    const clearInput = page.getByLabel(args.fieldLabel, { exact: true });
    await clearInput.waitFor();
    const retiredChoice = page.getByLabel(args.retiredSelectLabel, { exact: true });
    await retiredChoice.waitFor();
    assert.equal(await retiredChoice.inputValue(), originalRetiredChoice.value);
    assert.equal(await retiredChoice.locator('option:checked').textContent(), originalRetiredChoice.displayValue,
      'The editor must render the historical display value for an option that is no longer configured.');

    await page.getByLabel('Title', { exact: true }).fill('   ');
    const invalidResponsePromise = page.waitForResponse(response =>
      allowedOrigin(response.request(), args.baseOrigin) && response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/asap/staff/title-requests/${args.requestId}/action`);
    await page.getByRole('button', { name: 'Save changes' }).click();
    const invalidResponse = await invalidResponsePromise;
    assert.equal(invalidResponse.status(), 400, await invalidResponse.text());
    const invalidBody = await invalidResponse.json();
    assert.equal(invalidBody.code, 'invalid_title', 'Whitespace-only title must be rejected by the mutation endpoint.');
    assert.equal(actionBodies.at(-1).title, '   ', 'Browser did not send the invalid whitespace title to the real API.');
    const unchanged = await requestDetail(context, args.baseOrigin, args.requestId);
    assert.equal(unchanged.title, original.title, 'Rejected title changed the request title.');
    assert.equal(unchanged.version, originalVersion, 'Rejected title changed the SQL rowversion.');

    await page.getByLabel('Title', { exact: true }).fill('Browser saved after clear');
    await clearInput.fill('');
    const acceptedResponsePromise = page.waitForResponse(response =>
      allowedOrigin(response.request(), args.baseOrigin) && response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/asap/staff/title-requests/${args.requestId}/action`);
    await page.getByRole('button', { name: 'Save changes' }).click();
    const acceptedResponse = await acceptedResponsePromise;
    assert.equal(acceptedResponse.status(), 200, await acceptedResponse.text());
    await page.getByText('Request changes saved.').waitFor();
    const clearBody = actionBodies.at(-1);
    assert.equal(clearBody.title, 'Browser saved after clear');
    assert.equal(clearBody.customFields[args.fieldKey], null,
      'Clearing the optional input must send an explicit clear value rather than omit it.');
    assert.equal(clearBody.customFields[args.retiredSelectKey].value, 'retired-choice');

    const updated = await requestDetail(context, args.baseOrigin, args.requestId);
    assert.equal(updated.title, 'Browser saved after clear');
    assert.equal(Object.hasOwn(updated.customFields, args.fieldKey), false,
      'The accepted clear must remove the optional field value from the stored snapshot.');
    assert.deepEqual(updated.customFields.retired_history, originalRetiredHistory,
      'Clearing a current field must preserve an unknown/retired historical snapshot.');
    assert.deepEqual(updated.customFields[args.retiredSelectKey], originalRetiredChoice,
      'An unchanged retired select value must preserve its exact stored historical snapshot.');
    await scan(page, axeSource, args.artifactRoot, 'staff-request-editor-after-clear', report);

    const corruptDetail = await requestDetail(context, args.baseOrigin, args.corruptRequestId);
    assert.equal(corruptDetail.customFieldsValid, false);
    assert.equal(corruptDetail.customFields, null,
      'Corrupt stored custom-field history must be surfaced as unavailable rather than as an empty snapshot.');
    await page.goto(`${args.baseOrigin}/staff/?request=${encodeURIComponent(args.corruptRequestId)}`, { waitUntil: 'networkidle' });
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#request-dialog[open]').waitFor();
    await page.getByText('Saved custom-field history is invalid. Request edits are blocked to protect the stored values; contact an administrator.',
      { exact: true }).waitFor();
    assert.equal(await page.locator('.edit-form').count(), 0,
      'The editor must not allow unrelated edits to proceed from a corrupt field snapshot.');
    await scan(page, axeSource, args.artifactRoot, 'staff-request-corrupt-history-blocked', report);
    assert.deepEqual(pageErrors, [], `Staff request editor raised an uncaught error: ${pageErrors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Staff request browser journey made an external request.');
    report.requestEdit = {
      invalidTitleCode: invalidBody.code,
      invalidTitlePreservedVersion: unchanged.version === originalVersion,
      outgoingClearValue: clearBody.customFields[args.fieldKey],
      retiredSnapshotPreserved: JSON.stringify(updated.customFields.retired_history) === JSON.stringify(originalRetiredHistory),
      retiredSelectPreserved: JSON.stringify(updated.customFields[args.retiredSelectKey]) === JSON.stringify(originalRetiredChoice),
      corruptHistoryBlocked: true,
      editPostCount: actionBodies.length
    };
  } finally {
    await context.close();
  }
}

async function runMobile(browser, args, axeSource, report) {
  const { context, page, traffic, pageErrors } = await openContext(
    browser, { width: 390, height: 844 }, args.baseOrigin, args.identity);
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${encodeURIComponent(args.requestId)}`, { waitUntil: 'networkidle' });
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#request-dialog[open]').waitFor();
    const current = await requestDetail(context, args.baseOrigin, args.requestId);
    const retiredChoiceSnapshot = current.customFields[args.retiredSelectKey];
    await page.getByLabel(args.fieldLabel, { exact: true }).waitFor();
    assert.equal(await page.getByLabel('Title', { exact: true }).inputValue(), 'Browser saved after clear');
    assert.equal(await page.getByLabel(args.fieldLabel, { exact: true }).inputValue(), '');
    const retiredChoice = page.getByLabel(args.retiredSelectLabel, { exact: true });
    assert.equal(await retiredChoice.inputValue(), retiredChoiceSnapshot.value);
    assert.equal(await retiredChoice.locator('option:checked').textContent(), retiredChoiceSnapshot.displayValue);
    await scan(page, axeSource, args.artifactRoot, 'staff-request-editor-mobile', report);
    assert.deepEqual(pageErrors, [], 'Mobile staff request editor raised an uncaught error');
    assert.equal(traffic.externalRequests, 0, 'Mobile staff request browser journey made an external request.');
  } finally {
    await context.close();
  }
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  await fs.mkdir(args.artifactRoot, { recursive: true });
  const { chromium } = require('playwright');
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
  const report = { requestEdit: null, states: [] };
  try {
    await runDesktop(browser, args, axeSource, report);
    await runMobile(browser, args, axeSource, report);
    assert.equal(report.states.length, 3, 'Expected desktop, corrupt-history, and mobile editor states.');
    await fs.writeFile(path.join(args.artifactRoot, 'browser-results.json'), JSON.stringify(report, null, 2), 'utf8');
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Staff request contract journey failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
