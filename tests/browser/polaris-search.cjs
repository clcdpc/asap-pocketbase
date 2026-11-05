'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts, staffId, tenantId, email, copyOff, copyOn, useOff, useOn, stale, research] = process.argv.slice(2);

(async () => {
  await fs.mkdir(artifacts, { recursive: true });
  const headers = { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const report = { committed: [], states: [], external: [], errors: [], identicalRecovery: false, staleVersionRejected: false };
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers });
  const guard = async context => context.route('**/*', route => {
    if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
    report.external.push(route.request().url()); return route.abort();
  });
  await guard(context);
  const page = await context.newPage();
  page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors);
  const lookup = page.locator('#polaris-dialog'), cards = lookup.locator('.polaris-result');
  const first = () => cards.first();
  const confirmation = () => page.locator('#polaris-confirmation');
  const getDetail = async id => {
    const response = await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`);
    assert.equal(response.status(), 200, await response.text()); return response.json();
  };
  const openSearch = async (id, stage = 'suggestion', targetPage = page) => {
    await targetPage.locator(`#status-tabs [data-status="${stage}"]`).click();
    await targetPage.locator('#refresh-queue:not([disabled])').waitFor();
    await targetPage.locator('#claim-filter').selectOption('all');
    const row = targetPage.locator(`#request-grid a[data-request-id="${id}"]`).locator('xpath=ancestor::tr');
    await row.getByRole('button', { name: 'Search Polaris for title: Queue fixture', exact: true }).click();
    await targetPage.locator('#polaris-query').press('Enter');
    await targetPage.locator('.polaris-result').first().getByText('Owned by you (1)', { exact: true }).waitFor();
  };
  async function scan(name, target = page, nativeZoom = false) {
    await target.evaluate(axe);
    const state = await target.evaluate(async () => {
      const dialog = document.querySelector('#polaris-dialog');
      const frame = dialog.getBoundingClientRect();
      return { violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
        width: innerWidth, height: innerHeight, documentWidth: document.documentElement.scrollWidth,
        frame: { left: frame.left, right: frame.right, top: frame.top, bottom: frame.bottom },
        overflow: [...dialog.querySelectorAll('.polaris-result, .polaris-result-actions, .polaris-confirmation')]
          .filter(node => node.getClientRects().length).some(node => node.scrollWidth > node.clientWidth + 1),
        images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0),
        focused: document.activeElement?.textContent, devicePixelRatio, htmlZoom: getComputedStyle(document.documentElement).zoom };
    });
    assert.deepEqual(state.violations, [], `${name}: serious/critical axe findings`);
    assert.ok(state.documentWidth <= state.width, `${name}: document horizontal overflow`);
    assert.equal(state.overflow, false, `${name}: result/action overflow`);
    assert.equal(state.images, true, `${name}: unexpected missing image`);
    assert.ok(state.frame.left >= 0 && state.frame.right <= state.width + 1 && state.frame.bottom <= state.height + 1 && state.frame.top >= 0,
      `${name}: modal remains within viewport`);
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
    await page.goto(`${baseURL}/staff/?scope=all`, { waitUntil: 'networkidle' });
    assert.equal(await page.locator('#library-scope').inputValue(), 'all');
    await openSearch(copyOff);
    await lookup.getByText('Holdings unavailable', { exact: true }).waitFor();
    assert.equal(await cards.count(), 10);
    assert.equal(await lookup.locator('#polaris-status').textContent(), '10 catalog results shown of 2,951.');
    assert.match(await cards.nth(1).textContent(), /Owned by consortium \(4\)/);
    assert.match(await cards.nth(2).textContent(), /No item records found/);
    assert.match(await cards.nth(3).textContent(), /Not holdable/);
    for (const index of [2, 3, 4]) assert.equal(await cards.nth(index).locator('.polaris-queue-button').isDisabled(), true);
    assert.equal(await first().locator('img').getAttribute('alt'), '');
    assert.equal(new URL(await first().locator('img').getAttribute('src'), baseURL).origin, new URL(baseURL).origin);
    await lookup.locator('#polaris-ownership-status').filter({ hasText: 'Ownership checks complete.' }).waitFor();
    const posts = [];
    page.on('request', request => { if (request.url().endsWith('/staff/bib-lookup')) posts.push(request.postDataJSON()); });
    await lookup.getByRole('button', { name: 'Add author to search', exact: true }).focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.locator('#polaris-mode').inputValue(), 'title_author');
    assert.equal(await page.locator('#polaris-author').inputValue(), 'Staff author'); assert.equal(posts.length, 0);
    await page.locator('#polaris-author').press('Enter');
    await first().getByText('Owned by you (1)', { exact: true }).waitFor();
    assert.ok(posts.filter(body => body.mode === 'bib').every(body => body.libraryOrgId === 2 && body.includePatronHolds === false));
    await scan('polaris-desktop-results');
    await first().locator('.polaris-copy-button').focus(); await page.keyboard.press('Enter');
    assert.match(await confirmation().textContent(), /This action will turn on automatic hold placement for this request\./);
    await scan('polaris-desktop-confirmation');
    await page.keyboard.press('Escape');
    assert.equal(await first().locator('.polaris-copy-button').evaluate(node => node === document.activeElement), true);
    await page.setViewportSize({ width: 390, height: 844 }); await scan('polaris-mobile-results');
    await first().locator('.polaris-copy-button').click(); await scan('polaris-mobile-confirmation');
    await confirmation().getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.keyboard.press('Escape'); await page.setViewportSize({ width: 1280, height: 900 });

    // Use real Chromium tab zoom, rather than CSS/pinch scaling, for native controls.
    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900'] });
    try {
      await guard(zoomContext);
      const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
      const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
      zoomPage.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(zoomPage, report.errors);
      await zoomPage.goto(`${baseURL}/staff/?scope=all`, { waitUntil: 'networkidle' });
      const ratio = await zoomPage.evaluate(() => devicePixelRatio);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
      }, new URL(baseURL).origin), 2);
      await zoomPage.waitForFunction(before => Math.abs(devicePixelRatio / before - 2) < 0.01, ratio);
      await openSearch(copyOff, 'suggestion', zoomPage); await scan('polaris-native-zoom-results', zoomPage, true);
      await zoomPage.locator('.polaris-copy-button').first().click(); await scan('polaris-native-zoom-confirmation', zoomPage, true);
      await zoomPage.keyboard.press('Escape'); await zoomPage.keyboard.press('Escape');
    } finally { await zoomContext.close(); }

    // Pending close locks and a lost acknowledgement after the real SQL commit.
    await openSearch(copyOff);
    const actionPath = `**/staff/title-requests/${copyOff}/action`;
    let release, started;
    const dispatched = new Promise(resolve => { started = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    const commands = [];
    await page.route(actionPath, async route => {
      commands.push(route.request().postDataJSON());
      const committed = await route.fetch(); assert.equal(committed.status(), 200, await committed.text()); started();
      await held;
      await route.fulfill({ status: 503, json: { code: 'request_outcome_unconfirmed', message: 'Fixture acknowledgement lost after SQL commit.' } });
    });
    await first().locator('.polaris-copy-button').click();
    await confirmation().getByLabel('Email me a purchase reminder', { exact: true }).check();
    await confirmation().getByRole('button', { name: 'Confirm additional copy and queue', exact: true }).click();
    await dispatched;
    await page.locator('#close-polaris').click(); await page.keyboard.press('Escape');
    assert.equal(await lookup.isVisible(), true); assert.equal(await confirmation().locator('button:enabled').count(), 0);
    release(); await confirmation().getByText('Catalog action unconfirmed', { exact: true }).waitFor();
    await page.unroute(actionPath);
    page.on('request', request => { if (request.url().endsWith(`/title-requests/${copyOff}/action`)) commands.push(request.postDataJSON()); });
    await page.keyboard.press('Escape');
    assert.equal(await first().locator('.polaris-copy-button').evaluate(node => node === document.activeElement), true,
      'Escape after an uncertain submission returns focus to the original result.');
    await first().locator('.polaris-copy-button').click();
    await confirmation().getByRole('button', { name: 'Close recovery', exact: true }).click();
    assert.equal(await first().locator('.polaris-copy-button').evaluate(node => node === document.activeElement), true,
      'Close recovery returns focus to the original result.');
    await first().locator('.polaris-copy-button').click();
    await scan('polaris-uncertain-recovery');
    await confirmation().getByRole('button', { name: 'Recover identical action', exact: true }).click();
    await lookup.waitFor({ state: 'hidden' });
    assert.equal(commands.length, 2); assert.deepEqual(commands[1], commands[0]); report.identicalRecovery = true;
    report.committed.push({ id: copyOff, ...(await getDetail(copyOff)) });
    assert.equal(report.committed.at(-1).autohold, true);
    await page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled &&
      document.querySelector('#refresh-queue') === document.activeElement);

    for (const [id, stage, copy] of [[copyOn, 'outstanding_purchase', true], [useOff, 'suggestion', false], [useOn, 'outstanding_purchase', false]]) {
      await openSearch(id, stage);
      await first().locator(copy ? '.polaris-copy-button' : '.polaris-queue-button').click();
      if (copy) {
        assert.doesNotMatch(await confirmation().textContent(), /This action will turn on automatic hold placement/);
        await confirmation().getByLabel('Email me a purchase reminder', { exact: true }).uncheck();
      } else if (id === useOff) assert.match(await confirmation().textContent(), /close the request without placing a hold/);
      await confirmation().getByRole('button', { name: copy ? 'Confirm additional copy and queue' : 'Confirm use BIB', exact: true }).click();
      await lookup.waitFor({ state: 'hidden' }); await page.locator('#refresh-queue:not([disabled])').waitFor();
      report.committed.push({ id, ...(await getDetail(id)) });
    }
    assert.equal(report.committed.find(row => row.id === useOff).status, 'closed');

    // An editor lookup never gains direct workflow access or overwrites other drafts.
    await page.locator('#status-tabs [data-status="suggestion"]').click();
    await page.locator(`#request-grid a[data-request-id="${stale}"]`).click();
    const editor = page.locator('#request-dialog');
    await editor.getByLabel('Title', { exact: true }).fill('Queue fixture');
    await editor.getByLabel('Author', { exact: true }).fill('Unsaved staff author');
    await editor.getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    await page.locator('#polaris-query').press('Enter');
    await first().getByRole('button', { name: 'Use BIB 9001 in form', exact: true }).click();
    await lookup.waitFor({ state: 'hidden' });
    assert.equal(await editor.getByLabel('Author', { exact: true }).inputValue(), 'Unsaved staff author');
    assert.equal(await editor.locator('.request-save-state').textContent(), 'Unsaved changes');
    await decideConfirmation(page, () => editor.getByRole('button', { name: 'Revert changes', exact: true }).click(), dialog => dialog.accept());
    await page.keyboard.press('Escape');

    await openSearch(stale);
    const before = await getDetail(stale);
    const session = await (await context.request.get(`${baseURL}/api/asap/staff/session`)).json();
    const changed = await context.request.post(`${baseURL}/api/asap/staff/title-requests/${stale}/action`, {
      headers: { 'X-ASAP-Antiforgery': session.antiforgeryToken }, data: { action: 'edit', version: before.version, notes: 'Concurrent browser fixture update' } });
    assert.equal(changed.status(), 200, await changed.text());
    await first().locator('.polaris-queue-button').click();
    await confirmation().getByRole('button', { name: 'Confirm use BIB', exact: true }).click();
    await confirmation().getByText('Catalog action not completed', { exact: true }).waitFor();
    assert.equal((await getDetail(stale)).status, 'suggestion'); report.staleVersionRejected = true;
    await scan('polaris-stale-rejected');
    await confirmation().getByRole('button', { name: 'Return to search', exact: true }).click();
    assert.equal(await cards.count(), 0);
    assert.equal(await lookup.locator('#polaris-status').textContent(),
      'Search Polaris again to review current results and available actions.');
    assert.equal(await lookup.locator('#polaris-ownership-status').textContent(), '');
    assert.equal(await lookup.getByRole('button', { name: 'Search Polaris', exact: true }).evaluate(node => node === document.activeElement), true);
    await scan('polaris-rejected-return'); await page.locator('#close-polaris').click();
    await openSearch(research, 'closed');
    assert.equal(await lookup.locator('.polaris-queue-button, .polaris-copy-button').count(), 0);
    await page.keyboard.press('Escape');
    assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    console.log('Polaris search desktop/mobile/native 200%, ownership, direct actions, forced autohold, exact retry and stale/read-only protections passed.');
  } catch (error) {
    await page.screenshot({ path: path.join(artifacts, 'failure.png'), fullPage: true }).catch(() => {});
    await fs.writeFile(path.join(artifacts, 'failure.json'), JSON.stringify({ error: error.message, report,
      status: await page.locator('#polaris-status').textContent().catch(() => ''),
      confirmation: await confirmation().textContent().catch(() => '') }, null, 2));
    throw error;
  } finally { await context.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
