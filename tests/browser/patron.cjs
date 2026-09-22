'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

const JOURNEY_FIXTURES = [
  ['desktop', { width: 1280, height: 900 }, '20000000000801'],
  ['mobile', { width: 390, height: 844 }, '20000000000802']
];

function parseArguments(argv) {
  if (argv.length !== 2) {
    throw new Error('Usage: node tests/browser/patron.cjs <baseURL> <artifactDirectory>');
  }

  let parsed;
  try {
    parsed = new URL(argv[0]);
  } catch {
    throw new Error('baseURL must be an absolute http:// or https:// origin.');
  }

  if (!['http:', 'https:'].includes(parsed.protocol) ||
      parsed.username || parsed.password || parsed.pathname !== '/' ||
      parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute http:// or https:// origin without a path or credentials.');
  }

  return { baseOrigin: parsed.origin, artifactRoot: path.resolve(argv[1]) };
}

function routeOriginIsAllowed(request, baseOrigin) {
  try {
    return new URL(request.url()).origin === baseOrigin;
  } catch {
    return false;
  }
}

async function createOriginGatedContext(browser, viewport, baseOrigin) {
  const context = await browser.newContext({ viewport });
  const traffic = { externalRequests: 0 };
  await context.route('**/*', route => {
    if (routeOriginIsAllowed(route.request(), baseOrigin)) return route.continue();
    traffic.externalRequests += 1;
    return route.abort('blockedbyclient');
  });
  return { context, traffic };
}

async function writeReport(artifactRoot, report) {
  await fs.writeFile(
    path.join(artifactRoot, 'browser-results.json'),
    JSON.stringify(report, null, 2),
    'utf8'
  );
}

async function scanMajorState(page, axeSource, artifactRoot, report, viewport, state) {
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

  await page.screenshot({
    path: path.join(artifactRoot, `${viewport}-${state}.png`),
    fullPage: true
  });
  report.majorStates.push({ viewport, state, layout, accessibility });
  await writeReport(artifactRoot, report);

  assert.deepEqual(accessibility, [], `Serious or critical accessibility violation in ${viewport}/${state}`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow in ${viewport}/${state}`);
  assert.equal(layout.imagesLoaded, true, `Visible image failed to load in ${viewport}/${state}`);
}

function assertCleanBrowser(traffic, pageErrors, scenario) {
  assert.deepEqual(pageErrors, [], `Uncaught browser error in ${scenario}`);
  assert.equal(traffic.externalRequests, 0, `Unexpected external browser request in ${scenario}`);
}

async function gotoPatron(page, baseOrigin) {
  await page.goto(`${baseOrigin}/patron/?libraryOrgId=2`, { waitUntil: 'networkidle' });
  await page.locator('#barcode').waitFor({ state: 'visible' });
}

async function fillSuggestion(page, viewport) {
  await page.locator('#format').selectOption('book');
  await page.locator('#title').fill(`Browser Journey ${viewport}`);
  await page.locator('#author').fill('Ada Example');
  await page.locator('#isbn').fill('9780000000001');
  await page.locator('#publication').selectOption({ label: 'Coming soon' });
  await page.locator('#preferred-pickup-branch').selectOption('101');
  await page.locator('#submit-btn').focus();
  await page.keyboard.press('Enter');
}

async function runJourney(browser, baseOrigin, artifactRoot, axeSource, report) {
  for (const [viewport, size, barcode] of JOURNEY_FIXTURES) {
    const { context, traffic } = await createOriginGatedContext(browser, size, baseOrigin);
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', () => pageErrors.push(true));
    try {
      await gotoPatron(page, baseOrigin);
      await scanMajorState(page, axeSource, artifactRoot, report, viewport, 'login');

      await page.locator('#barcode').fill(barcode);
      await page.locator('#pin').fill('wrong');
      await page.locator('#pin').press('Enter');
      await page.locator('#login-error:not(.hidden)').waitFor();
      assert.equal(
        await page.evaluate(() => document.activeElement.id),
        'login-error',
        'Wrong-PIN feedback did not receive focus'
      );
      await scanMajorState(page, axeSource, artifactRoot, report, viewport, 'invalid-login');

      await page.locator('#pin').fill('1234');
      await page.locator('#pin').press('Enter');
      await page.locator('#step-form:not(.hidden)').waitFor();
      assert.equal(
        await page.evaluate(() => document.activeElement === document.querySelector('#step-form h2')),
        true,
        'Suggestion heading did not receive focus after login'
      );
      const token = await page.evaluate(() => sessionStorage.getItem('asap_patron_token'));
      assert.ok(token, 'Bearer token was not stored in sessionStorage');

      await page.reload({ waitUntil: 'networkidle' });
      await page.locator('#step-form:not(.hidden)').waitFor();
      await scanMajorState(page, axeSource, artifactRoot, report, viewport, 'form');

      await fillSuggestion(page, viewport);
      await page.locator('#step-success:not(.hidden)').waitFor();
      assert.equal(await page.evaluate(() => document.activeElement.id), 'success-title');
      await scanMajorState(page, axeSource, artifactRoot, report, viewport, 'success');

      await page.locator('#step-success .btn-submit-another').click();
      await fillSuggestion(page, viewport);
      await page.locator('#step-conflict:not(.hidden)').waitFor();
      assert.equal(await page.evaluate(() => document.activeElement.id), 'conflict-title');
      await scanMajorState(page, axeSource, artifactRoot, report, viewport, 'duplicate');

      await page.locator('#step-conflict .btn-logout').click();
      await page.locator('#step-login:not(.hidden)').waitFor();
      assert.equal(await page.evaluate(() => document.activeElement.id), 'barcode');
      assert.equal(
        await page.evaluate(() => sessionStorage.getItem('asap_patron_token')),
        null,
        'Logout did not clear the browser session token'
      );

      const revoked = await context.request.get(`${baseOrigin}/api/asap/patron/session`, {
        headers: { Authorization: `Bearer ${token}` }
      });
      assert.equal(revoked.status(), 401, 'Logout did not revoke the server session');
      assertCleanBrowser(traffic, pageErrors, `${viewport} journey`);
    } finally {
      await context.close();
    }
  }
}

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

async function loginForRace(context, baseOrigin, barcode) {
  const response = await context.request.post(`${baseOrigin}/api/asap/patron/login`, {
    data: { barcode, pin: '1234', libraryOrgId: 2 }
  });
  assert.equal(response.status(), 200, 'Race fixture login failed');
  return (await response.json()).token;
}

async function runSessionRestoreRace(browser, baseOrigin, artifactRoot, report, revokedOldSession) {
  const scenario = revokedOldSession ? 'restore-401-after-revocation' : 'restore-200';
  const oldBarcode = '20000000000901';
  const newBarcode = '20000000000902';
  const { context, traffic } = await createOriginGatedContext(
    browser,
    { width: 1280, height: 900 },
    baseOrigin
  );
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', () => pageErrors.push(true));
  const releaseRestore = deferred();
  const restoreFetched = deferred();
  const restoreReleased = deferred();
  try {
    const oldToken = await loginForRace(context, baseOrigin, oldBarcode);
    await gotoPatron(page, baseOrigin);
    await page.evaluate(token => sessionStorage.setItem('asap_patron_token', token), oldToken);

    if (revokedOldSession) {
      const logout = await context.request.post(`${baseOrigin}/api/asap/patron/logout`, {
        headers: { Authorization: `Bearer ${oldToken}` }
      });
      assert.equal(logout.status(), 204, 'Could not revoke the old race session');
    }

    await page.route('**/api/asap/patron/session', async route => {
      const response = await route.fetch();
      const restoreStatus = response.status();
      restoreFetched.resolve();
      await releaseRestore.promise;
      await route.fulfill({ response });
      restoreReleased.resolve();
      assert.equal(restoreStatus, revokedOldSession ? 401 : 200, 'Unexpected restore fixture status');
    });

    await page.reload({ waitUntil: 'domcontentloaded' });
    await restoreFetched.promise;
    await page.locator('#barcode').fill(newBarcode);
    await page.locator('#pin').fill('1234');
    await page.locator('#pin').press('Enter');
    await page.locator('#step-form:not(.hidden)').waitFor();
    assert.equal(
      await page.locator('#display-barcode').textContent(),
      newBarcode,
      'New login did not replace the displayed patron'
    );
    const newToken = await page.evaluate(() => sessionStorage.getItem('asap_patron_token'));
    assert.notEqual(newToken, oldToken, 'New login reused the old session token');

    releaseRestore.resolve();
    await restoreReleased.promise;
    await page.waitForLoadState('networkidle');
    await page.screenshot({
      path: path.join(artifactRoot, `auth-restore-race-${revokedOldSession ? 'revoked' : 'normal'}.png`),
      fullPage: true
    });
    const state = {
      displayedNewPatron: (await page.locator('#display-barcode').textContent()) === newBarcode,
      formVisible: await page.locator('#step-form').isVisible(),
      tokenUnchangedAfterRestore: await page.evaluate(
        token => sessionStorage.getItem('asap_patron_token') === token,
        newToken
      )
    };
    assert.equal(state.displayedNewPatron, true, 'Late session restore overwrote the newer login');
    assert.equal(state.formVisible, true, 'Late session restore hid the newer login form');
    assert.equal(state.tokenUnchangedAfterRestore, true, 'Late session restore changed the newer token');
    report.authRaces.push({ scenario, ...state });
    await writeReport(artifactRoot, report);
    assertCleanBrowser(traffic, pageErrors, scenario);
  } finally {
    releaseRestore.resolve();
    await context.close();
  }
}

async function runStartupRace(browser, baseOrigin, artifactRoot, report) {
  const oldBarcode = '20000000000911';
  const newBarcode = '20000000000912';
  const { context, traffic } = await createOriginGatedContext(
    browser,
    { width: 1280, height: 900 },
    baseOrigin
  );
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', () => pageErrors.push(true));
  const releaseConfig = deferred();
  const releaseLogin = deferred();
  const configFetched = deferred();
  const loginFetched = deferred();
  const loginReleased = deferred();
  try {
    const oldToken = await loginForRace(context, baseOrigin, oldBarcode);
    await gotoPatron(page, baseOrigin);
    await page.evaluate(token => sessionStorage.setItem('asap_patron_token', token), oldToken);

    await page.route('**/api/asap/config?*', async route => {
      const response = await route.fetch();
      configFetched.resolve();
      await releaseConfig.promise;
      await route.fulfill({ response });
    });
    await page.route('**/api/asap/patron/login', async route => {
      const response = await route.fetch();
      loginFetched.resolve();
      await releaseLogin.promise;
      await route.fulfill({ response });
      loginReleased.resolve();
    });

    await page.reload({ waitUntil: 'domcontentloaded' });
    await configFetched.promise;
    await page.locator('#barcode').fill(newBarcode);
    await page.locator('#pin').fill('1234');
    await page.locator('#pin').press('Enter');
    await loginFetched.promise;

    const configDelivered = page.waitForResponse(response =>
      routeOriginIsAllowed(response.request(), baseOrigin) &&
      response.url().includes('/api/asap/config?')
    );
    releaseConfig.resolve();
    await configDelivered;
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    releaseLogin.resolve();
    await loginReleased.promise;
    await page.locator('#step-form:not(.hidden)').waitFor();
    await page.waitForLoadState('networkidle');
    await page.screenshot({ path: path.join(artifactRoot, 'auth-startup-race.png'), fullPage: true });

    const state = {
      displayedNewPatron: (await page.locator('#display-barcode').textContent()) === newBarcode,
      oldTokenReplaced: await page.evaluate(token => sessionStorage.getItem('asap_patron_token') !== token, oldToken)
    };
    assert.equal(state.displayedNewPatron, true, 'Startup restore superseded the in-flight user login');
    assert.equal(state.oldTokenReplaced, true, 'Startup race left the old session token active');
    report.authRaces.push({ scenario: 'startup-config-held-vs-new-login', ...state });
    await writeReport(artifactRoot, report);
    assertCleanBrowser(traffic, pageErrors, 'startup-config-held-vs-new-login');
  } finally {
    releaseConfig.resolve();
    releaseLogin.resolve();
    await context.close();
  }
}

async function loadDependencies() {
  let chromium;
  let axePath;
  try {
    ({ chromium } = require('playwright'));
  } catch {
    throw new Error('Missing local Playwright. Run npm install with playwright@1.62.1.');
  }
  try {
    axePath = require.resolve('axe-core/axe.min.js');
  } catch {
    throw new Error('Missing local axe-core. Run npm install with axe-core@4.10.3.');
  }
  return { chromium, axeSource: await fs.readFile(axePath, 'utf8') };
}

async function launchBrowser(chromium) {
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  if (executablePath) {
    try {
      await fs.access(executablePath);
    } catch {
      throw new Error('ASAP_TEST_CHROMIUM_EXECUTABLE_PATH does not point to a readable browser executable.');
    }
  }

  try {
    return await chromium.launch({
      headless: true,
      ...(executablePath ? { executablePath } : {})
    });
  } catch {
    throw new Error(
      executablePath
        ? 'Configured Chromium could not be launched.'
        : 'The Playwright Chromium browser is unavailable. Run npx playwright install chromium or set ASAP_TEST_CHROMIUM_EXECUTABLE_PATH.'
    );
  }
}

async function main() {
  const { baseOrigin, artifactRoot } = parseArguments(process.argv.slice(2));
  await fs.mkdir(artifactRoot, { recursive: true });
  const { chromium, axeSource } = await loadDependencies();
  const browser = await launchBrowser(chromium);
  const report = { majorStates: [], authRaces: [] };
  try {
    await runJourney(browser, baseOrigin, artifactRoot, axeSource, report);
    await runSessionRestoreRace(browser, baseOrigin, artifactRoot, report, false);
    await runSessionRestoreRace(browser, baseOrigin, artifactRoot, report, true);
    await runStartupRace(browser, baseOrigin, artifactRoot, report);
    await writeReport(artifactRoot, report);
    assert.equal(report.majorStates.length, 10, 'Expected ten desktop/mobile journey states');
    assert.equal(report.authRaces.length, 3, 'Expected three authentication ordering cases');
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Patron browser acceptance failed: ${error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
