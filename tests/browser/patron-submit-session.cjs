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

async function runScopedConfigurationGuards(browser, baseOrigin, artifactRoot, axeSource, report) {
  const scoped = JSON.parse(await fs.readFile(path.join(artifactRoot, 'patron-session-scope.json'), 'utf8'));
  const { context, page, traffic, pageErrors } = await openPage(browser, baseOrigin);
  let malformedLoginResponses = 0;
  let serverIssuedTokenPresent = false;
  let suggestionPostCount = 0;

  const malformedLoginRoute = async route => {
    const request = route.request();
    const payload = request.postDataJSON();
    if (payload?.username !== scoped.barcode ||
        String(payload?.libraryOrgId) !== String(scoped.libraryOrgId) ||
        malformedLoginResponses > 0) {
      return route.continue();
    }

    malformedLoginResponses += 1;
    const response = await route.fetch();
    const session = await response.json();
    assert.equal(response.status(), 200, 'The actual B login endpoint should issue its normal HTTP session response.');
    assert.equal(session.effectiveLibraryOrgId, scoped.libraryOrgId);
    assert.equal(session.ui_text.library, scoped.libraryName);
    assert.equal(session.ui_text.pageTitle, scoped.pageTitle);
    assert.deepEqual(session.ui_text.availableFormats, [],
      'The real producer should return the Settings-owned empty format list.');
    serverIssuedTokenPresent = typeof session.token === 'string' && session.token.trim().length > 0;
    assert.equal(serverIssuedTokenPresent, true,
      'route.fetch reaches the real server, which may and does issue a token before the browser rejects this response.');

    const incomplete = { ...session };
    delete incomplete.ui_text;
    await route.fulfill({ response, body: JSON.stringify(incomplete) });
  };
  await page.route('**/api/asap/patron/login', malformedLoginRoute);
  await page.route('**/api/asap/patron/suggestions', async route => {
    suggestionPostCount += 1;
    return route.continue();
  });

  try {
    const barcodeA = '20000000000901';
    await login(page, barcodeA);
    const uiA = await readVisibleLibraryUi(page);
    assert.ok(uiA.formatOptions.length > 0, 'A must have real available formats before switching scope.');

    await page.locator('#step-form .btn-logout').click();
    await page.locator('#step-login:not(.hidden)').waitFor();
    assert.ok(!(await page.evaluate(() => sessionStorage.getItem('asap_patron_token'))),
      'A logout must clear the browser token before testing B login acceptance.');
    await page.evaluate(libraryOrgId => {
      const url = new URL(location.href);
      url.searchParams.set('libraryOrgId', String(libraryOrgId));
      history.replaceState(history.state, '', url.toString());
    }, scoped.libraryOrgId);

    await page.locator('#barcode').fill(scoped.barcode);
    await page.locator('#pin').fill('1234');
    await page.locator('#pin').press('Enter');
    await page.locator('#login-error').waitFor({ state: 'visible' });
    assert.equal(malformedLoginResponses, 1, 'Exactly one real B login response should have ui_text removed.');

    const malformedState = await page.evaluate(() => ({
      loginVisible: !document.querySelector('#step-login').classList.contains('hidden'),
      formVisible: !document.querySelector('#step-form').classList.contains('hidden'),
      loginError: document.querySelector('#login-error').textContent.trim(),
      displayBarcode: document.querySelector('#display-barcode').textContent.trim(),
      token: sessionStorage.getItem('asap_patron_token'),
      storedLibraryId: localStorage.getItem('asap_patron_library_org_id'),
      pageTitle: document.title,
      heading: document.querySelector('#main-title').textContent.trim(),
      barcodeLabel: document.querySelector('#lbl-barcode-login').textContent.trim(),
      formatOptions: [...document.querySelector('#format').options].map(option => ({
        value: option.value,
        label: option.textContent.trim()
      }))
    }));
    assert.equal(malformedState.loginVisible, true, 'An incomplete session response must leave B on the login step.');
    assert.equal(malformedState.formVisible, false, 'An incomplete session response must not establish B identity.');
    assert.equal(malformedState.loginError, 'The server returned an invalid response body.',
      'Session shape rejection should use the established shared-helper public error contract.');
    assert.ok(!malformedState.token, 'The server-issued token must not be stored by the client.');
    assert.equal(malformedState.displayBarcode, barcodeA, 'B identity must not replace the last established patron display.');
    assert.equal(malformedState.storedLibraryId, '2', 'B scope must not replace the client library identity on rejection.');
    assert.deepEqual({
      pageTitle: malformedState.pageTitle,
      heading: malformedState.heading,
      barcodeLabel: malformedState.barcodeLabel,
      formatOptions: malformedState.formatOptions
    }, uiA, 'A UI configuration must remain intact when the malformed B response is rejected.');
    await scan(page, axeSource, artifactRoot, 'missing-session-ui-text-retains-a-ui', report);
    report.configurationGuards.push({
      scenario: 'real-session-missing-ui-text',
      serverIssuedTokenPresent,
      clientTokenStored: Boolean(malformedState.token),
      bIdentityEstablished: malformedState.formVisible || malformedState.displayBarcode === scoped.barcode ||
        malformedState.storedLibraryId === String(scoped.libraryOrgId),
      aUiRetained: true
    });

    const fullLoginResponsePromise = page.waitForResponse(response => {
      const request = response.request();
      if (request.method() !== 'POST' || new URL(response.url()).pathname !== '/api/asap/patron/login') {
        return false;
      }
      const payload = request.postDataJSON();
      return payload?.username === scoped.barcode &&
        String(payload?.libraryOrgId) === String(scoped.libraryOrgId);
    });
    // If an earlier assertion fails, context teardown must not replace it with an unhandled waiter rejection.
    void fullLoginResponsePromise.catch(() => {});
    await login(page, scoped.barcode);
    const fullLoginResponse = await fullLoginResponsePromise;
    assert.equal(fullLoginResponse.status(), 200, 'The unmodified B login should use the actual HTTP endpoint.');
    const fullSession = await fullLoginResponse.json();
    assert.equal(fullSession.effectiveLibraryOrgId, scoped.libraryOrgId);
    assert.equal(fullSession.ui_text.pageTitle, scoped.pageTitle);
    assert.deepEqual(fullSession.ui_text.availableFormats, [],
      'The complete producer response should preserve the saved empty format list.');

    const emptyFormatState = await page.evaluate(() => ({
      formVisible: !document.querySelector('#step-form').classList.contains('hidden'),
      displayBarcode: document.querySelector('#display-barcode').textContent.trim(),
      pageTitle: document.title,
      heading: document.querySelector('#main-title').textContent.trim(),
      token: sessionStorage.getItem('asap_patron_token'),
      storedLibraryId: localStorage.getItem('asap_patron_library_org_id'),
      pickupBranch: document.querySelector('#preferred-pickup-branch').value,
      optionCount: document.querySelector('#format').options.length,
      formatValid: document.querySelector('#format').checkValidity()
    }));
    assert.equal(emptyFormatState.formVisible, true);
    assert.equal(emptyFormatState.displayBarcode, scoped.barcode);
    assert.equal(emptyFormatState.token, fullSession.token,
      'The complete producer token should be the token established by the client.');
    assert.equal(emptyFormatState.storedLibraryId, String(scoped.libraryOrgId));
    assert.equal(emptyFormatState.pageTitle, scoped.pageTitle);
    assert.equal(emptyFormatState.heading, scoped.pageTitle,
      'The scoped B heading should replace A UI from the complete session snapshot.');
    assert.equal(emptyFormatState.optionCount, 0, 'An authoritative empty B format list must clear A options.');
    assert.equal(emptyFormatState.formatValid, false,
      'The required format select must be invalid when the complete B configuration has no formats.');
    assert.equal(emptyFormatState.pickupBranch, '101', 'The empty-format submit control needs a valid pickup branch.');

    await page.locator('#suggestion-form').evaluate(form =>
      form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })));
    await page.locator('#submit-error').waitFor({ state: 'visible' });
    assert.match(await page.locator('#submit-error').textContent(), /No suggestion formats are currently available/i);
    assert.equal(suggestionPostCount, 0,
      'Directly invoking the submit handler with no currently enabled format must not dispatch a suggestion POST.');
    await scan(page, axeSource, artifactRoot, 'settings-empty-formats-block-submit', report);
    report.configurationGuards.push({
      scenario: 'settings-empty-formats',
      effectiveLibraryOrgId: fullSession.effectiveLibraryOrgId,
      availableFormats: fullSession.ui_text.availableFormats,
      optionCount: emptyFormatState.optionCount,
      suggestionPostCount
    });

    await page.locator('#step-form .btn-logout').click();
    await page.locator('#step-login:not(.hidden)').waitFor();
    assert.deepEqual(pageErrors, [], 'Scoped session configuration controls raised an uncaught browser error.');
    assert.equal(traffic.externalRequests, 0,
      `Scoped session configuration controls requested external resources: ${traffic.externalUrls.join(', ')}`);
  } finally {
    await context.close();
  }
}

async function readVisibleLibraryUi(page) {
  return page.evaluate(() => ({
    pageTitle: document.title,
    heading: document.querySelector('#main-title').textContent.trim(),
    barcodeLabel: document.querySelector('#lbl-barcode-login').textContent.trim(),
    formatOptions: [...document.querySelector('#format').options].map(option => ({
      value: option.value,
      label: option.textContent.trim()
    }))
  }));
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
  const report = { races: [], unknownOutcomes: [], configurationGuards: [], states: [] };
  try {
    for (const [status, scenario] of [[201, 'accepted'], [409, 'conflict'], [400, 'validation'], [500, 'server-error'], ['abort', 'cancelled']]) {
      await runStaleResponse(browser, baseOrigin, artifactRoot, axeSource, status, scenario, report);
    }
    await runUnknownOutcome(browser, baseOrigin, artifactRoot, axeSource, report);
    await runScopedConfigurationGuards(browser, baseOrigin, artifactRoot, axeSource, report);
    assert.equal(report.races.length, 5, 'Expected all stale submission completion paths.');
    assert.equal(report.unknownOutcomes.length, 1, 'Expected the malformed accepted response guard.');
    assert.equal(report.configurationGuards.length, 2, 'Expected both scoped session configuration controls.');
    await fs.writeFile(path.join(artifactRoot, 'browser-results.json'), JSON.stringify(report, null, 2), 'utf8');
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Patron submit session journey failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
