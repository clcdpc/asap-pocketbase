'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { activateRowAction } = require('../acceptance/helpers/request-actions.cjs');
const { rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts] = process.argv.slice(2);
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const labels = ['Suggestions', 'Purchases', 'Pending holds', 'Placed holds', 'Closed'];
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

(async () => {
  const fixture = JSON.parse(await fs.readFile(path.join(artifacts, 'fixture.json'), 'utf8'));
  const headers = actor => ({ 'X-ASAP-Test-Staff-Id': actor.id, 'X-ASAP-Test-Tenant-Id': actor.tenantId, 'X-ASAP-Test-Staff-Email': actor.email });
  const report = { states: [], nativeZoom: [], external: [], errors: [], reads: [], mutation: false, ordinaryScope: false, modalReturn: null };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true });
  let nextRead = null;
  const guard = async context => {
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
      report.external.push(route.request().url()); return route.abort();
    });
    context.on('page', page => { page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors); });
  };
  const names = totals => totals.map((total, i) => `${labels[i]} (${total})`);
  const countNames = page => page.locator('#status-tabs [data-status]').evaluateAll(tabs => tabs.map(tab => tab.getAttribute('aria-label')));
  const counts = async (page, totals) => {
    const expected = names(totals);
    await page.waitForFunction(expected => JSON.stringify([...document.querySelectorAll('#status-tabs [data-status]')]
      .map(tab => tab.getAttribute('aria-label'))) === JSON.stringify(expected), expected);
    assert.deepEqual(await countNames(page), expected);
    assert.deepEqual(await page.locator('#status-tabs .status-count').allTextContents(), totals.map(total => `(${total})`));
    for (let index = 0; index < totals.length; index++) {
      assert.equal(await page.getByRole('tab', { name: expected[index], exact: true }).count(), 1);
      assert.equal(await page.locator(`#status-tabs [data-status="${stages[index]}"] .status-count`).evaluate(node => node.classList.contains('quiet')), totals[index] === 0);
    }
  };
  const unknown = async (page, loading) => {
    const expected = labels.map(label => `${label} (count ${loading ? 'loading' : 'unavailable'})`);
    await page.waitForFunction(expected => JSON.stringify([...document.querySelectorAll('#status-tabs [data-status]')]
      .map(tab => tab.getAttribute('aria-label'))) === JSON.stringify(expected), expected);
    assert.deepEqual(await countNames(page), expected);
    assert.match(await page.locator('#status-count-state').textContent(), loading ? /Counts loading/ : /Counts unavailable/);
  };
  const scan = async (page, name, visible = null) => {
    await page.evaluate(axe);
    const result = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      overflow: document.documentElement.scrollWidth > innerWidth,
      countHelpVisible: document.querySelector('#status-count-help').getClientRects().length > 0,
      images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
    }));
    assert.deepEqual(result.violations, [], `${name}: axe`); assert.equal(result.overflow, false, `${name}: document overflow`);
    assert.equal(result.countHelpVisible, true, `${name}: count/filter help is visible`);
    assert.equal(result.images, true, `${name}: images`); report.states.push({ name, ...result });
    // Axe can scroll the tab strip while inspecting its other buttons.
    if (visible) await visible.scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
  };
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers(fixture.super) });
    await guard(context);
    await context.route('**/api/asap/staff/title-requests?*', async route => {
      report.reads.push(route.request().url());
      const control = nextRead; nextRead = null;
      if (!control) return route.continue();
      if (control.mode === 'failed') return route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"Deterministic count read failure"}' });
      if (control.mode === 'malformed') return route.fulfill({ status: 200, contentType: 'application/json', body: '{"scope":"44201"}' });
      const response = await route.fetch();
      control.started.resolve();
      await control.release.promise;
      try { await route.fulfill({ response }); }
      catch (error) { if (!/closed|canceled|cancelled|invalid interception/i.test(error.message)) throw error; }
      finally { control.completed.resolve(); }
    });
    const holdRead = mode => (nextRead = { mode, started: deferred(), release: deferred(), completed: deferred() });
    const page = await context.newPage();
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`);
    await counts(page, fixture.libraryCounts);
    assert.equal(await page.locator('#queue-title').evaluate(node => document.activeElement === node), false, 'Startup counts preserve native initial focus.');
    await page.locator('#claim-filter').selectOption('all');
    await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody .request-title-link').length === 3);
    await counts(page, fixture.libraryCounts);
    await page.locator('#request-search').fill('no matching requests');
    await page.waitForFunction(() => document.querySelector('#queue-summary').textContent === '0 suggestion requests');
    await counts(page, fixture.libraryCounts);
    await page.locator('#request-search').fill('');
    await page.locator('#claim-filter').selectOption('mine'); await counts(page, fixture.libraryCounts);
    await page.locator('#claim-filter').selectOption('all');
    await page.locator('#similar-filter').selectOption('similar'); await counts(page, fixture.libraryCounts);
    await page.locator('#similar-filter').selectOption('all');
    assert.match(await page.locator('#status-count-help').textContent(), /all requests in the selected library scope/);
    await scan(page, 'counts-desktop');

    const beforeTabs = report.reads.length;
    const suggestion = page.locator('#status-tabs [data-status="suggestion"]');
    await suggestion.focus(); await page.keyboard.press('End');
    const closed = page.getByRole('tab', { name: 'Closed (3)', exact: true });
    await page.waitForFunction(() => document.querySelector('#status-tabs [data-status="closed"]').getAttribute('aria-selected') === 'true');
    assert.equal(await closed.evaluate(node => document.activeElement === node), true);
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(() => document.querySelector('#status-tabs [data-status="suggestion"]').getAttribute('aria-selected') === 'true');
    assert.equal(await suggestion.evaluate(node => document.activeElement === node), true);
    assert.deepEqual(await page.locator('#status-tabs [data-status]').evaluateAll(tabs => tabs.map(tab => tab.tabIndex)), [0, -1, -1, -1, -1]);
    assert.equal(report.reads.length, beforeTabs, 'Tab selection uses the already complete dataset.');

    const held = holdRead('hold');
    await page.locator('#refresh-queue').click(); await held.started.promise; await unknown(page, true);
    // Refresh disables its native button. Keep a deliberate tab focus while
    // the read completes and changes that tab's accessible count name.
    await suggestion.focus(); await scan(page, 'counts-loading');
    held.release.resolve(); await held.completed.promise; await counts(page, fixture.libraryCounts);
    assert.equal(await suggestion.evaluate(node => node === document.activeElement), true, 'Counts refresh preserves deliberate tab focus.');
    for (const mode of ['failed', 'malformed']) {
      holdRead(mode); await page.locator('#refresh-queue').click(); await unknown(page, false); await scan(page, `counts-${mode}`);
      await page.locator('#refresh-queue').click(); await counts(page, fixture.libraryCounts);
    }
    const obsolete = holdRead('hold');
    await page.locator('#refresh-queue').click(); await obsolete.started.promise;
    await page.locator('#library-scope').selectOption(fixture.otherLibrary); await counts(page, fixture.otherCounts);
    obsolete.release.resolve(); await obsolete.completed.promise; await counts(page, fixture.otherCounts);
    const target = holdRead('hold');
    await page.locator('#library-scope').selectOption(fixture.library); await target.started.promise; await unknown(page, true);
    target.release.resolve(); await target.completed.promise; await counts(page, fixture.libraryCounts);
    await page.locator('#library-scope').selectOption('all'); await counts(page, fixture.allCounts);
    await page.locator('#library-scope').selectOption(fixture.library); await counts(page, fixture.libraryCounts);

    await page.locator('#request-search').fill(`Tab counts ${fixture.library} suggestion 00`);
    const row = page.locator(`#request-grid a[data-request-id="${fixture.mutationId}"]`).locator('xpath=ancestor::tr');
    await activateRowAction(row, 'purchase');
    const detail = page.locator('#request-dialog[open]');
    await detail.locator('.action-choice button[type="submit"]:not([disabled])').waitFor();
    const reminder = detail.getByLabel('Send purchase reminder', { exact: true });
    if (await reminder.count()) await reminder.uncheck();
    const committed = page.waitForResponse(response => response.url().endsWith(`/title-requests/${fixture.mutationId}/action`));
    await detail.locator('.action-choice button[type="submit"]').click();
    const saved = await committed; assert.equal(saved.status(), 200, await saved.text());
    const updated = [...fixture.libraryCounts]; updated[0]--; updated[1]++;
    await counts(page, updated); report.mutation = true;
    assert.equal(await detail.evaluate(node => node.contains(document.activeElement)), true,
      'Count refresh retains focus inside the open request dialog.');
    report.dialogFocusAtCountRefresh = await page.evaluate(() => document.activeElement?.id);
    await page.keyboard.press('Escape'); await detail.waitFor({ state: 'hidden' });
    await page.waitForFunction(id => document.activeElement?.dataset.requestId === id &&
      document.activeElement.classList.contains('grid-open'), fixture.mutationId);
    report.modalReturn = await page.evaluate(() => document.activeElement.getAttribute('aria-label'));
    await page.locator('#status-tabs [data-status="suggestion"]').click();
    await page.setViewportSize({ width: 390, height: 844 }); await scan(page, 'counts-mobile');
    await counts(page, updated);
    const ordinaryContext = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers(fixture.ordinary) });
    await guard(ordinaryContext);
    const ordinaryPage = await ordinaryContext.newPage();
    await ordinaryPage.goto(`${baseURL}/staff/?scope=${fixture.otherLibrary}`); await counts(ordinaryPage, updated);
    assert.equal(await ordinaryPage.locator('#scope-field').isVisible(), false);
    for (const scope of ['all', fixture.otherLibrary]) {
      const response = await ordinaryContext.request.get(`${baseURL}/api/asap/staff/title-requests?scope=${scope}`);
      assert.equal(response.status(), 200); const data = await response.json();
      assert.equal(data.scope, fixture.library); assert.ok(data.items.every(item => String(item.libraryOrgId) === fixture.library));
      assert.deepEqual(stages.map(stage => data.items.filter(item => item.status === stage).length), updated);
    }
    await scan(ordinaryPage, 'counts-ordinary-authorized'); report.ordinaryScope = true;
    await ordinaryContext.close(); await context.close();

    for (const width of [1280, 1440]) {
      const extension = path.join(__dirname, 'native-zoom-extension');
      const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, `zoom-${width}`), {
        channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers(fixture.super),
        args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`]
      });
      try {
        await guard(zoomContext);
        const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
        const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
        // The persistent context may already own its initial page before guard().
        zoomPage.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(zoomPage, report.errors);
        await zoomPage.goto(`${baseURL}/staff/?scope=${fixture.library}`); await counts(zoomPage, updated);
        const ratio = await zoomPage.evaluate(() => devicePixelRatio);
        assert.equal(await worker.evaluate(async origin => {
          const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
          await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
          await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
        }, new URL(baseURL).origin), 2);
        await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, ratio);
        await zoomPage.locator('#status-tabs [data-status="suggestion"]').focus(); await zoomPage.keyboard.press('End');
        await zoomPage.waitForFunction(() => document.querySelector('#status-tabs [data-status="closed"]').getAttribute('aria-selected') === 'true');
        await counts(zoomPage, updated);
        const selected = zoomPage.locator('#status-tabs [aria-selected="true"]');
        const bounds = await selected.boundingBox(), viewportWidth = await zoomPage.evaluate(() => innerWidth);
        const keyboardTab = { bounds, viewportWidth, status: await selected.getAttribute('data-status'),
          focused: await selected.evaluate(node => node === document.activeElement) };
        assert.equal(keyboardTab.focused, true);
        assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= viewportWidth + 1,
          `${width}: keyboard selection brings the complete focused tab into view before axe: ${JSON.stringify(keyboardTab)}`);
        await scan(zoomPage, `counts-native-200-${width}`, selected);
        await selected.scrollIntoViewIfNeeded();
        const cdp = await zoomContext.newCDPSession(zoomPage), metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2);
        assert.equal(await zoomPage.evaluate(() => getComputedStyle(document.documentElement).zoom), '1');
        const png = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `counts-native-200-${width}-viewport.png`), Buffer.from(png.data, 'base64'));
        report.nativeZoom.push({ physicalWidth: width, zoom: metrics.visualViewport.zoom, keyboardTab }); await cdp.detach();
      } finally { await zoomContext.close(); }
    }
    assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
