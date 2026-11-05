'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts, staffId, tenantId, email, missing, invalid, stale, unknown, optout, existing, duplicate] = process.argv.slice(2);

(async () => {
  await fs.mkdir(artifacts, { recursive: true });
  const headers = { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email };
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
  const report = { states: [], external: [], errors: [], staleRetry: false, unknownRecovered: false, reloadStayedReadOnly: false,
    duplicateDraftsPreserved: false, latestReloadDraftPreserved: false, canceledReloadStayedClosed: false, actions: [] };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const guard = async ctx => ctx.route('**/*', route => {
    if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
    report.external.push(route.request().url()); return route.abort();
  });
  await guard(context);
  const page = await context.newPage();
  page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors);
  page.on('request', request => { if (/\/title-requests\/\d+\/action$/.test(request.url())) report.actions.push(request.postDataJSON()); });
  const requestModal = target => target.locator('#request-dialog');
  const panel = target => requestModal(target).locator('.bib-link-panel');
  const status = target => target.locator('#request-dialog-status');
  const open = async (id, target = page) => {
    await target.goto(`${baseURL}/staff/?scope=all&request=${id}`, { waitUntil: 'networkidle' });
    await requestModal(target).waitFor({ state: 'visible' });
  };
  const link = async (target = page) => {
    await requestModal(target).getByRole('tab', { name: 'Related requests', exact: true }).click();
    await requestModal(target).getByRole('button', { name: 'Already own', exact: true }).click();
    await panel(target).getByRole('heading', { name: 'Link a catalog record to continue', exact: true }).waitFor();
    assert.equal(await requestModal(target).getByRole('tab', { name: 'Details', exact: true }).getAttribute('aria-selected'), 'true');
    assert.equal(await requestModal(target).getByRole('button', { name: 'Search Polaris catalog', exact: true }).evaluate(n => n === document.activeElement), true);
    assert.equal(await target.locator('#request-dialog[open]').count(), 1);
  };
  const select = async (target = page) => {
    await requestModal(target).getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    await target.locator('#polaris-mode').selectOption('bib');
    await target.locator('#polaris-query').fill('9001');
    await target.locator('#polaris-form button[type=submit]').click();
    await target.locator('#polaris-results').getByRole('button', { name: 'Use BIB 9001 in form', exact: true }).click();
    await target.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    assert.equal(await requestModal(target).getByLabel('BIB ID', { exact: true }).inputValue(), '9001');
    assert.equal(await status(target).textContent(), 'Suggestion');
  };
  const final = (target = page) => panel(target).getByRole('button', { name: 'Save Bib ID and move to Pending hold', exact: true });
  const getDetail = async id => {
    const response = await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`);
    assert.equal(response.status(), 200); return response.json();
  };
  async function scan(name, target = page, nativeZoom = false) {
    await panel(target).scrollIntoViewIfNeeded();
    await target.evaluate(axe);
    const state = await target.evaluate(async () => {
      const dialog = document.querySelector('#request-dialog'), panel = dialog.querySelector('.bib-link-panel');
      const rect = dialog.getBoundingClientRect();
      return { violations: (await axe.run()).violations.filter(v => ['serious', 'critical'].includes(v.impact)),
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        overflow: panel.scrollWidth > panel.clientWidth + 1 || dialog.querySelector('.request-middle').scrollWidth > dialog.querySelector('.request-middle').clientWidth + 1,
        modal: { left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, height: innerHeight },
        images: [...document.images].filter(n => n.getClientRects().length).every(n => n.complete && n.naturalWidth > 0),
        error: panel.querySelector('[role=alert]')?.textContent,
        focus: { tag: document.activeElement?.tagName, id: document.activeElement?.id, text: document.activeElement?.textContent.slice(0, 120) },
        htmlZoom: getComputedStyle(document.documentElement).zoom };
    });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width && !state.overflow, `${name}: horizontal overflow`);
    assert.equal(state.images, true, `${name}: missing image`);
    assert.ok(state.modal.left >= 0 && state.modal.right <= state.width + 1 && state.modal.top >= 0 && state.modal.bottom <= state.modal.height + 1);
    if (nativeZoom) {
      const cdp = await target.context().newCDPSession(target);
      const metrics = await cdp.send('Page.getLayoutMetrics');
      assert.equal(metrics.visualViewport.zoom, 2); assert.equal(state.htmlZoom, '1');
      state.browserZoom = metrics.visualViewport.zoom;
      const screenshot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await fs.writeFile(path.join(artifacts, `${name}.png`), Buffer.from(screenshot.data, 'base64')); await cdp.detach();
    } else await target.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, ...state });
  }
  try {
    await open(missing);
    await requestModal(page).getByLabel('Title', { exact: true }).fill('Unrelated title draft');
    await requestModal(page).getByLabel('Notes', { exact: true }).fill('Unrelated note draft');
    await link(); await final().click();
    await panel(page).getByRole('alert').filter({ hasText: /Enter a positive Polaris/ }).waitFor();
    await scan('already-own-desktop-validation');
    await page.setViewportSize({ width: 390, height: 844 }); await scan('already-own-mobile-validation');
    await page.setViewportSize({ width: 1280, height: 900 });
    assert.equal(report.actions.length, 0);
    // Lookup errors stay visible on returning from the existing catalog dialog.
    await page.route('**/staff/bib-lookup', route => route.fulfill({ status: 503, json: { message: 'Deterministic catalog lookup failure' } }));
    await requestModal(page).getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    await page.locator('#polaris-form button[type=submit]').click();
    await page.locator('#polaris-status').filter({ hasText: 'Deterministic catalog lookup failure' }).waitFor();
    await page.keyboard.press('Escape');
    await panel(page).getByRole('alert').filter({ hasText: 'Deterministic catalog lookup failure' }).waitFor();
    await scan('already-own-lookup-failure'); await page.unroute('**/staff/bib-lookup');
    await select(); assert.equal(report.actions.length, 0, 'Selection is never a workflow mutation');
    assert.equal(await requestModal(page).getByLabel('Title', { exact: true }).inputValue(), 'Unrelated title draft');
    // Definite pre-commit failure leaves the form, selection and drafts intact.
    const missingPath = `**/staff/title-requests/${missing}/action`;
    await page.route(missingPath, route => route.fulfill({ status: 503, json: { code: 'notification_dependency_unavailable', message: 'Deterministic save failure; retry' } }));
    await final().click();
    await panel(page).getByRole('alert').filter({ hasText: 'Deterministic save failure; retry' }).waitFor();
    assert.equal(await final().evaluate(n => n === document.activeElement), true, 'Failed save returns focus to the retry action');
    assert.equal((await getDetail(missing)).status, 'suggestion');
    await scan('already-own-save-failure'); await page.unroute(missingPath);
    let release, started;
    const entered = new Promise(resolve => { started = resolve; }), held = new Promise(resolve => { release = resolve; });
    await page.route(missingPath, async route => { started(); await held; await route.continue(); });
    await final().focus(); await page.keyboard.press('Enter'); await entered;
    await page.keyboard.press('Enter'); await page.keyboard.press('Escape');
    assert.equal(await requestModal(page).isVisible(), true);
    assert.equal(await final().isDisabled(), true);
    release(); await status(page).filter({ hasText: 'Pending hold' }).waitFor();
    await page.unroute(missingPath);
    assert.equal(await requestModal(page).getByLabel('Title', { exact: true }).inputValue(), 'Unrelated title draft');
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Unrelated note draft');
    assert.equal(await requestModal(page).locator('.request-save-state').textContent(), 'Unsaved changes');
    assert.equal((await getDetail(missing)).title, 'Queue fixture');

    await open(invalid); await link();
    await page.keyboard.press('Escape');
    assert.equal(await panel(page).isVisible(), false);
    assert.equal(await requestModal(page).getByRole('button', { name: 'Already own', exact: true }).evaluate(n => n === document.activeElement), true);
    assert.equal((await getDetail(invalid)).status, 'suggestion');

    // Native browser zoom checks the complete linking action using the unmodified fixture.
    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900'] });
    try {
      await guard(zoomContext);
      const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
      const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
      zoomPage.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(zoomPage, report.errors);
      await open(invalid, zoomPage);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin), 2);
      await link(zoomPage); await final(zoomPage).click();
      await scan('already-own-native-200-validation', zoomPage, true);
    } finally { await zoomContext.close(); }

    // A real same-patron/BIB conflict must retain the selected BIB and every
    // unrelated draft, both when its follow-up read succeeds and when it fails.
    await open(invalid);
    const savedInvalid = await getDetail(invalid);
    await requestModal(page).getByLabel('Title', { exact: true }).fill('Duplicate recovery title draft');
    await requestModal(page).getByLabel('Notes', { exact: true }).fill('Duplicate recovery note draft');
    await link(); await select();
    await final().click();
    const duplicateRecovery = requestModal(page).locator('.duplicate-recovery');
    await duplicateRecovery.waitFor();
    assert.match(await duplicateRecovery.textContent(), new RegExp(`Request ${duplicate}:`));
    assert.equal(await requestModal(page).getByLabel('Title', { exact: true }).inputValue(), 'Duplicate recovery title draft');
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Duplicate recovery note draft');
    assert.equal(await requestModal(page).getByLabel('BIB ID', { exact: true }).inputValue(), '9001');
    await panel(page).getByRole('alert').filter({ hasText: 'were not saved' }).waitFor();
    assert.deepEqual(await getDetail(invalid), savedInvalid, 'Duplicate rejection leaves authoritative data and version unchanged');
    await scan('already-own-duplicate-recovery');
    await duplicateRecovery.getByRole('button', { name: 'Continue editing', exact: true }).click();
    assert.equal(await requestModal(page).getByLabel('BIB ID', { exact: true }).evaluate(n => n === document.activeElement), true);
    const invalidDetailPath = new RegExp(`/api/asap/staff/title-requests/${invalid}(?:\\?.*)?$`);
    await page.route(invalidDetailPath, route => route.fulfill({ status: 503, json: { message: 'Duplicate follow-up read failed' } }));
    await final().click();
    const duplicateReload = panel(page).getByRole('button', { name: 'Reload current request', exact: true });
    await duplicateReload.waitFor();
    await panel(page).getByRole('alert').filter({ hasText: 'not saved because another open request' }).waitFor();
    assert.equal(await requestModal(page).getByLabel('Title', { exact: true }).inputValue(), 'Duplicate recovery title draft');
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Duplicate recovery note draft');
    assert.equal(await requestModal(page).getByLabel('BIB ID', { exact: true }).inputValue(), '9001');
    await scan('already-own-duplicate-read-failure');
    await page.unroute(invalidDetailPath);
    const postsBeforeDuplicateReload = report.actions.length;
    await duplicateReload.click(); await final().waitFor();
    assert.equal(report.actions.length, postsBeforeDuplicateReload);
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Duplicate recovery note draft');
    assert.deepEqual(await getDetail(invalid), savedInvalid);
    report.duplicateDraftsPreserved = true;

    await open(stale); await link(); await select();
    await requestModal(page).getByLabel('Notes', { exact: true }).fill('Stale action draft');
    const before = await getDetail(stale);
    const session = await (await context.request.get(`${baseURL}/api/asap/staff/session`)).json();
    const concurrent = await context.request.post(`${baseURL}/api/asap/staff/title-requests/${stale}/action`, {
      headers: { 'X-ASAP-Antiforgery': session.antiforgeryToken }, data: { version: before.version, action: 'edit', author: 'Concurrent author' } });
    assert.equal(concurrent.status(), 200, await concurrent.text());
    await final().click();
    await panel(page).getByRole('alert').waitFor();
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Stale action draft');
    assert.equal(await requestModal(page).getByLabel('Author', { exact: true }).inputValue(), 'Concurrent author');
    assert.equal(await requestModal(page).getByLabel('BIB ID', { exact: true }).inputValue(), '9001');
    await final().click(); await status(page).filter({ hasText: 'Pending hold' }).waitFor(); report.staleRetry = true;

    await open(unknown); await link(); await select();
    await requestModal(page).getByLabel('Notes', { exact: true }).fill('Unknown response draft');
    const unknownPath = `**/staff/title-requests/${unknown}/action`;
    const unknownDetailPath = new RegExp(`/api/asap/staff/title-requests/${unknown}(?:\\?.*)?$`);
    let recoveryReads = 0;
    page.on('request', request => {
      if (request.method() === 'GET' && unknownDetailPath.test(request.url())) recoveryReads++;
    });
    // The first attempt is uncommitted and its state cannot refresh. A later
    // lookup error must not turn the button labeled Reload into another write.
    await page.route(unknownPath, route => route.fulfill({ status: 503, json: { code: 'request_outcome_unconfirmed', message: 'Uncommitted action could not be confirmed' } }));
    await page.route(unknownDetailPath, route => route.fulfill({ status: 503, json: { message: 'Current request read failed' } }));
    await final().click();
    const reload = panel(page).getByRole('button', { name: 'Reload current request', exact: true });
    await reload.waitFor();
    await page.route('**/staff/bib-lookup', route => route.fulfill({ status: 503, json: { message: 'Later catalog lookup failed' } }));
    await requestModal(page).getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    await page.locator('#polaris-form button[type=submit]').click();
    await page.locator('#polaris-status').filter({ hasText: 'Later catalog lookup failed' }).waitFor();
    await page.keyboard.press('Escape');
    const postsBeforeReload = report.actions.length, readsBeforeReload = recoveryReads;
    await reload.click();
    await panel(page).getByRole('alert').filter({ hasText: 'Current request details could not be loaded' }).waitFor();
    assert.equal(report.actions.length, postsBeforeReload, 'Reload after a lookup error cannot resend the uncertain action');
    assert.equal(recoveryReads, readsBeforeReload + 1, 'Reload still reads current details');
    await scan('already-own-reload-after-lookup-failure');
    await page.unroute('**/staff/bib-lookup'); await page.unroute(unknownDetailPath); await page.unroute(unknownPath);
    let releaseReload, reloadStarted;
    const reloadEntered = new Promise(resolve => { reloadStarted = resolve; });
    const reloadHeld = new Promise(resolve => { releaseReload = resolve; });
    await page.route(unknownDetailPath, async route => { reloadStarted(); await reloadHeld; await route.continue(); });
    await reload.click(); await reloadEntered;
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).isEnabled(), true);
    await requestModal(page).getByLabel('Notes', { exact: true }).fill('Latest typing while reload is pending');
    await panel(page).getByRole('button', { name: 'Cancel linking', exact: true }).click();
    const reloadedResponse = page.waitForResponse(response => response.request().method() === 'GET' && unknownDetailPath.test(response.url()) && response.status() === 200);
    releaseReload();
    await reloadedResponse;
    await page.waitForFunction(() => document.querySelector('.bib-link-panel')?.hidden === true && document.activeElement?.id === 'close-request');
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Latest typing while reload is pending');
    assert.equal(await panel(page).isVisible(), false, 'A late reload cannot reopen canceled linking');
    report.latestReloadDraftPreserved = true; report.canceledReloadStayedClosed = true;
    await page.unroute(unknownDetailPath);
    await requestModal(page).getByRole('button', { name: 'Already own', exact: true }).click(); await final().waitFor();
    assert.equal(report.actions.length, postsBeforeReload);
    assert.equal((await getDetail(unknown)).status, 'suggestion', 'An uncommitted first attempt stays unchanged after recovery');
    report.reloadStayedReadOnly = true;
    const attemptsBeforeCommittedUnknown = report.actions.length;
    await page.route(unknownPath, async route => {
      const response = await route.fetch(); assert.equal(response.status(), 200, await response.text());
      await route.fulfill({ status: 503, json: { code: 'request_outcome_unconfirmed', message: 'Response lost after commit' } });
    });
    await final().click(); await status(page).filter({ hasText: 'Pending hold' }).waitFor();
    await requestModal(page).locator('.bib-link-outcome[role=alert]').waitFor();
    assert.equal(await panel(page).isVisible(), false);
    assert.equal(await requestModal(page).getByLabel('Notes', { exact: true }).inputValue(), 'Latest typing while reload is pending');
    assert.equal(report.actions.length, attemptsBeforeCommittedUnknown + 1);
    report.unknownRecovered = true; await page.unroute(unknownPath);

    await open(optout); await link(); await select();
    await panel(page).getByRole('button', { name: 'Save Bib ID and close without hold', exact: true }).click();
    await status(page).filter({ hasText: 'Closed' }).waitFor();
    assert.equal((await getDetail(optout)).autohold, false);

    await open(existing);
    await decideConfirmation(page, () => requestModal(page).getByRole('button', { name: 'Already own', exact: true }).click(), async decision => {
      assert.match(decision.message(), /move the verified BIB to Pending hold/); await decision.accept();
    });
    await status(page).filter({ hasText: 'Pending hold' }).waitFor();
    assert.equal(report.actions.every(a => !Object.hasOwn(a, 'notes') && !Object.hasOwn(a, 'title') && !Object.hasOwn(a, 'autohold')), true);
    assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    console.log(`Already own browser linking passed: ${report.states.length} accessibility/layout states, ${report.actions.length} action attempts`);
  } finally { await context.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
