'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 2) throw new Error('Usage: node tests/browser/patron-submit-session.cjs <baseURL> <artifactDirectory>');
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.username || parsed.password ||
      parsed.pathname !== '/' || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return { baseOrigin: parsed.origin, artifactRoot: path.resolve(argv[1]) };
}

function allowedOrigin(request, baseOrigin) {
  try { return new URL(request.url()).origin === baseOrigin; } catch { return false; }
}

async function openPage(browser, baseOrigin) {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const traffic = { externalRequests: 0, externalUrls: [] };
  await context.route('**/*', route => {
    if (allowedOrigin(route.request(), baseOrigin)) return route.continue();
    traffic.externalRequests += 1;
    traffic.externalUrls.push(route.request().url());
    return route.abort('blockedbyclient');
  });
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.goto(`${baseOrigin}/patron/?libraryOrgId=2`, { waitUntil: 'networkidle' });
  await page.locator('#barcode').waitFor({ state: 'visible' });
  return { context, page, traffic, pageErrors };
}

async function login(page, barcode) {
  await page.locator('#barcode').fill(barcode);
  await page.locator('#pin').fill('1234');
  await page.locator('#pin').press('Enter');
  await page.locator('#step-form:not(.hidden)').waitFor();
  assert.equal((await page.locator('#display-barcode').textContent()).trim(), barcode);
}

async function fillSuggestion(page, title, author) {
  await page.locator('#format').selectOption('book');
  await page.locator('#title').fill(title);
  await page.locator('#author').fill(author);
  await page.locator('#isbn').fill('');
  await page.locator('#publication').selectOption({ label: 'Coming soon' });
  await page.locator('#preferred-pickup-branch').selectOption('101');
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

async function scan(page, axeSource, artifactRoot, name, report) {
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(() => {
    const visibleImages = [...document.images].filter(image => {
      const bounds = image.getBoundingClientRect();
      return bounds.width > 0 && bounds.height > 0;
    });
    return {
      width: innerWidth,
      scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: visibleImages.every(image => image.complete && image.naturalWidth > 0)
    };
  });
  await page.screenshot({ path: path.join(artifactRoot, `${name}.png`), fullPage: true });
  report.states.push({ name, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility findings in ${name}`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow in ${name}`);
  assert.equal(layout.imagesLoaded, true, `A visible image did not load in ${name}`);
}

async function runStaleResponse(browser, baseOrigin, artifactRoot, axeSource, status, scenario, report) {
  const { context, page, traffic, pageErrors } = await openPage(browser, baseOrigin);
  const bodyRead = deferred();
  const release = deferred();
  const released = deferred();
  let requests = 0;
  await page.route('**/api/asap/patron/suggestions', async route => {
    requests += 1;
    bodyRead.resolve();
    await release.promise;
    if (status === 'abort') {
      await route.abort('failed');
      released.resolve();
      return;
    }
    const responseBody = status === 201
      ? { id: '9007199254741993', successTitle: 'A accepted', successMessage: 'A accepted' }
      : status === 409
        ? { code: 'duplicate', conflictTitle: 'A duplicate', conflictMessage: 'A duplicate message' }
        : status === 400
          ? { message: 'A validation error' }
          : { message: 'A server error' };
    await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(responseBody) });
    released.resolve();
  });

  try {
    await login(page, '20000000000901');
    await fillSuggestion(page, `A pending ${scenario}`, 'A Author');
    const responseReceived = status === 'abort'
      ? page.waitForEvent('requestfailed', request =>
          allowedOrigin(request, baseOrigin) && new URL(request.url()).pathname === '/api/asap/patron/suggestions')
      : page.waitForResponse(response =>
          allowedOrigin(response.request(), baseOrigin) && new URL(response.url()).pathname === '/api/asap/patron/suggestions',
          { timeout: 10000 });
    // If an earlier assertion fails, context teardown must not replace it with an unhandled waiter rejection.
    void responseReceived.catch(() => {});
    await page.locator('#submit-btn').click();
    await bodyRead.promise;
    assert.equal(requests, 1, `${scenario}: patron A should submit exactly once`);

    await page.locator('#step-form .btn-logout').click();
    await page.locator('#step-login:not(.hidden)').waitFor();
    await login(page, '20000000000902');
    const draftTitle = `B draft survives ${scenario}`;
    const draftAuthor = 'B Author';
    await fillSuggestion(page, draftTitle, draftAuthor);
    const newToken = await page.evaluate(() => sessionStorage.getItem('asap_patron_token'));
    assert.ok(newToken, `${scenario}: B should own a live token before A responds`);

    release.resolve();
    await released.promise;
    const completedRequest = await responseReceived;
    if (status !== 'abort') {
      assert.equal(completedRequest.status(), status, `${scenario}: the deferred POST returned the expected HTTP status`);
    }
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

    const state = await page.evaluate(() => ({
      formVisible: !document.querySelector('#step-form').classList.contains('hidden'),
      successVisible: !document.querySelector('#step-success').classList.contains('hidden'),
      conflictVisible: !document.querySelector('#step-conflict').classList.contains('hidden'),
      loginVisible: !document.querySelector('#step-login').classList.contains('hidden'),
      barcode: document.querySelector('#display-barcode').textContent.trim(),
      title: document.querySelector('#title').value,
      author: document.querySelector('#author').value,
      token: sessionStorage.getItem('asap_patron_token'),
      submitDisabled: document.querySelector('#submit-btn').disabled,
      submitError: document.querySelector('#submit-error').textContent,
      unknownMessageVisible: document.querySelector('#submit-error').textContent.includes('Please do not submit again')
    }));
    assert.equal(state.formVisible, true, `${scenario}: A response replaced B's form`);
    assert.equal(state.loginVisible, false, `${scenario}: A response logged B out`);
    assert.equal(state.successVisible, false, `${scenario}: A success rendered in B's session`);
    assert.equal(state.conflictVisible, false, `${scenario}: A conflict rendered in B's session`);
    assert.equal(state.barcode, '20000000000902', `${scenario}: A response changed B's displayed identity`);
    assert.equal(state.title, draftTitle, `${scenario}: A response replaced B's title draft`);
    assert.equal(state.author, draftAuthor, `${scenario}: A response replaced B's author draft`);
    assert.equal(state.token, newToken, `${scenario}: A response replaced B's token`);
    assert.equal(state.submitDisabled, false, `${scenario}: A response changed B's busy state`);
    assert.equal(state.unknownMessageVisible, false, `${scenario}: stale A response marked B's outcome unknown`);
    await scan(page, axeSource, artifactRoot, `stale-${scenario}`, report);
    assert.deepEqual(pageErrors, [], `${scenario}: browser raised an uncaught error`);
    assert.equal(traffic.externalRequests, 0,
      `${scenario}: browser requested external resources: ${traffic.externalUrls.join(', ')}`);
    report.races.push({ scenario, status, ...state, requestCount: requests });
  } finally {
    release.resolve();
    await context.close();
  }
}

async function runUnknownOutcome(browser, baseOrigin, artifactRoot, axeSource, report) {
  const { context, page, traffic, pageErrors } = await openPage(browser, baseOrigin);
  let requests = 0;
  await page.route('**/api/asap/patron/suggestions', async route => {
    requests += 1;
    await route.fulfill({ status: 201, contentType: 'application/json', body: '{"id":"9007199254741993"' });
  });
  try {
    await login(page, '20000000000901');
    await fillSuggestion(page, 'Unknown outcome contract', 'Ada Example');
    await page.locator('#submit-btn').click();
    await page.getByText(/could not confirm whether your suggestion was saved/i).waitFor();
    assert.equal(await page.locator('#submit-btn').isDisabled(), true,
      'A malformed success response must leave resubmission disabled until the user changes session.');
    await page.locator('#suggestion-form').evaluate(form =>
      form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })));
    assert.equal(requests, 1, 'An uncertain accepted outcome must not trigger a blind duplicate POST.');
    await page.locator('#step-form .btn-logout').click();
    await page.locator('#step-login:not(.hidden)').waitFor();
    await login(page, '20000000000902');
    assert.equal(await page.locator('#submit-btn').isDisabled(), false,
      'A new identity should clear the previous identity’s uncertain-outcome lock.');
    await scan(page, axeSource, artifactRoot, 'malformed-success-new-session', report);
    assert.deepEqual(pageErrors, [], 'Malformed success flow raised an uncaught browser error');
    assert.equal(traffic.externalRequests, 0,
      `Malformed success flow requested external resources: ${traffic.externalUrls.join(', ')}`);
    report.unknownOutcomes.push({ scenario: 'truncated-201-blocks-resubmit', requestCount: requests });
  } finally {
    await context.close();
  }
}

async function loadBrowserDependencies() {
  const { chromium } = require('playwright');
  const axePath = require.resolve('axe-core/axe.min.js');
  return { chromium, axeSource: await fs.readFile(axePath, 'utf8') };
}

async function main() {
  const { baseOrigin, artifactRoot } = parseArguments(process.argv.slice(2));
  await fs.mkdir(artifactRoot, { recursive: true });
  const { chromium, axeSource } = await loadBrowserDependencies();
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
  const report = { races: [], unknownOutcomes: [], states: [] };
  try {
    for (const [status, scenario] of [[201, 'accepted'], [409, 'conflict'], [400, 'validation'], [500, 'server-error'], ['abort', 'cancelled']]) {
      await runStaleResponse(browser, baseOrigin, artifactRoot, axeSource, status, scenario, report);
    }
    await runUnknownOutcome(browser, baseOrigin, artifactRoot, axeSource, report);
    assert.equal(report.races.length, 5, 'Expected all stale submission completion paths.');
    assert.equal(report.unknownOutcomes.length, 1, 'Expected the malformed accepted response guard.');
    await fs.writeFile(path.join(artifactRoot, 'browser-results.json'), JSON.stringify(report, null, 2), 'utf8');
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Patron submit session journey failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
