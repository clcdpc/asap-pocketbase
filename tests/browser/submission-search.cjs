'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts] = process.argv.slice(2);
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

(async () => {
  const fixture = JSON.parse(await fs.readFile(path.join(artifacts, 'fixture.json'), 'utf8'));
  const headers = actor => ({ 'X-ASAP-Test-Staff-Id': actor.id, 'X-ASAP-Test-Tenant-Id': actor.tenantId, 'X-ASAP-Test-Staff-Email': actor.email });
  const report = { states: [], errors: [], external: [], mutations: [], referenceReads: [], searchDetailReads: [], timings: [], nativeZoom: [], dirtySettings: false, dirtyTitle: false, dirtyCopy: false, staleDetails: false, pendingSettingsRetired: false };
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true });
  let injection = null, settingsPreparation = null, page = null;
  const guard = async context => {
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
      report.external.push(route.request().url()); return route.abort();
    });
    context.on('page', page => { page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors); });
    context.on('request', request => {
      const url = new URL(request.url());
      if (url.searchParams.get('searchResult') === 'true') report.searchDetailReads.push({ path: url.pathname, scope: url.searchParams.get('scope') });
      if (new URL(request.url()).pathname === '/api/asap/staff/polaris/patron-codes') report.referenceReads.push(request.url());
      if (!['GET', 'HEAD'].includes(request.method()) && !['/api/asap/staff/search'].includes(new URL(request.url()).pathname)) {
        report.mutations.push({ path: new URL(request.url()).pathname, method: request.method() });
      }
      assert.ok(!new URL(request.url()).searchParams.has('query'), 'Private terms stay out of URLs');
    });
  };
  const command = async kind => {
    await fs.rm(path.join(artifacts, 'command-done'), { force: true });
    await fs.writeFile(path.join(artifacts, 'command.tmp'), JSON.stringify({ kind }));
    await fs.rename(path.join(artifacts, 'command.tmp'), path.join(artifacts, 'command.json'));
    const until = Date.now() + 15000;
    while (Date.now() < until) {
      try { await fs.access(path.join(artifacts, 'command-done')); return; } catch { await new Promise(done => setTimeout(done, 20)); }
    }
    throw new Error(`Fixture command ${kind} did not complete`);
  };
  const scan = async (page, name) => {
    await page.evaluate(axe);
    const state = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      overflow: document.documentElement.scrollWidth > innerWidth,
      images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0),
      dialogs: [...document.querySelectorAll('dialog[open]')].map(node => node.id),
      focus: document.activeElement?.id,
      geometry: [...document.querySelectorAll('#search-dialog[open] [data-search="close"], #search-dialog[open] input, #search-dialog[open] select, #search-dialog[open] [data-search="all"]:not([hidden])')].filter(node => node.getClientRects().length).map(node => {
        const bounds = node.getBoundingClientRect(); return { id: node.id || node.dataset.search, left: bounds.left, right: bounds.right, top: bounds.top, bottom: bounds.bottom, width: innerWidth, height: innerHeight };
      })
    }));
    report.states.push({ name, ...state });
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: !await page.locator('#search-dialog').isVisible() });
    assert.deepEqual(state.violations, [], `${name}: axe`); assert.equal(state.overflow, false, `${name}: overflow`); assert.equal(state.images, true);
    assert.ok(state.dialogs.length <= 1, `${name}: no stacked search/detail/confirmation`);
    for (const rect of state.geometry) assert.ok(rect.left >= 0 && rect.top >= 0 && rect.right <= rect.width + 1 && rect.bottom <= rect.height + 1, `${name}: controls fit ${JSON.stringify(rect)}`);
  };
  const dialog = page => page.locator('#search-dialog');
  const summary = page => page.locator('#search-dialog [data-search="summary"]');
  const open = async (page, detail = false) => {
    await page.locator(detail ? '#detail-search-asap' : '#search-asap').click();
    await dialog(page).waitFor();
    assert.equal(await page.locator('#search-quick-input').evaluate(node => document.activeElement === node), true);
  };
  const search = async (page, query) => {
    const start = Date.now();
    await page.locator('#search-quick-input').fill(query);
    await page.waitForFunction(() => /matching submission|No matching/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    report.timings.push({ query, elapsedMs: Date.now() - start });
  };
  const close = async page => { await page.keyboard.press('Escape'); await dialog(page).waitFor({ state: 'hidden' }); };
  const selectedTitleVisible = async page => {
    const geometry = await page.evaluate(() => {
      const list = document.querySelector('#search-suggestions'), bounds = list.getBoundingClientRect();
      const id = document.querySelector('#search-quick-input').getAttribute('aria-activedescendant');
      const title = document.getElementById(id).querySelector('strong').getBoundingClientRect();
      return { id, listHeight: list.clientHeight, titleHeight: title.height,
        visibleTitle: Math.max(0, Math.min(title.bottom, bounds.bottom) - Math.max(title.top, bounds.top)) };
    });
    report.states.at(-1).activeResult = geometry;
    assert.ok(geometry.listHeight >= 64, `Meaningful short-screen preview: ${JSON.stringify(geometry)}`);
    assert.ok(geometry.visibleTitle >= geometry.titleHeight - 1, `Selected title is readable: ${JSON.stringify(geometry)}`);
    return geometry.id;
  };
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers(fixture.super) });
    await guard(context);
    await context.route('**/api/asap/staff/search', async route => {
      const control = injection; injection = null;
      if (!control) return route.continue();
      if (control.kind === 'failure') return route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"Deterministic search failure"}' });
      const response = await route.fetch(); control.started.resolve(); await control.release.promise;
      try { await route.fulfill({ response }); } catch (error) { if (!/closed|cancelled|canceled|invalid interception/i.test(error.message)) throw error; }
      finally { control.completed.resolve(); }
    });
    await context.route('**/api/asap/staff/settings?*', async route => {
      const control = settingsPreparation; settingsPreparation = null;
      if (!control) return route.continue();
      const response = await route.fetch(); control.started.resolve(); await control.release.promise;
      await route.fulfill({ response });
    });
    page = await context.newPage();
    await page.goto(`${baseURL}/staff/?scope=${fixture.library}`);
    await page.locator('#request-grid tbody tr').first().waitFor();
    await page.keyboard.press('Control+k'); await dialog(page).waitFor();
    assert.equal(await page.locator('#search-quick-input').evaluate(node => node === document.activeElement), true);
    await search(page, 'needle'); assert.match(await summary(page).textContent(), /36 matching/);
    assert.equal(await page.locator('#search-suggestions [role="option"]').count(), 12);
    assert.equal(await page.locator('#search-suggestions h3').count(), 7);
    assert.match(await page.locator('#search-dialog [data-search="scope"]').textContent(), /Search Library/);
    await scan(page, 'search-desktop-light');
    await page.locator('#search-quick-scope').selectOption('all');
    await page.waitForFunction(() => /37 matching/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    await page.locator('#search-quick-scope').selectOption(fixture.otherLibrary);
    await page.waitForFunction(() => /1 matching/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    await page.locator('#search-quick-scope').selectOption(fixture.library);
    await page.waitForFunction(() => /36 matching/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
    await page.locator('#request-dialog[open]').waitFor();
    assert.match(await page.locator('#request-dialog-kicker').textContent(), /Additional copy/);
    await page.keyboard.press('Escape'); await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await open(page); await search(page, 'EXACT-BARCODE');
    await page.locator('#search-option-0').click();
    await page.locator('#request-dialog-kicker').filter({ hasText: `Request ${fixture.bigId}` }).waitFor();
    await page.keyboard.press('Escape'); await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await command('readOnly');
    assert.equal(report.searchDetailReads.length, 2, 'Both initial selections explicitly request search detail eligibility.');
    assert.ok(report.searchDetailReads.some(item => item.path.includes('/additional-copies/')));
    assert.ok(report.searchDetailReads.some(item => item.path.includes('/title-requests/')));
    assert.ok(report.searchDetailReads.every(item => item.scope === fixture.library));
    for (const name of ['analytics', 'operations', 'settings', 'profile', 'additional-copies', 'queue']) {
      const preparing = name === 'settings' ? { started: deferred(), release: deferred() } : null;
      if (preparing) settingsPreparation = preparing;
      await page.locator(name === 'profile' ? '#profile-button' : `.view-tab[data-view="${name}"]`).click();
      if (preparing) {
        await preparing.started.promise;
        assert.equal(await page.locator('#settings-view').isVisible(), false, 'Settings remains unaccepted while its target is preparing.');
        await open(page); await search(page, 'Ada   O\'Brien');
        await scan(page, 'search-during-settings-preparation');
        preparing.release.resolve();
        await page.locator('#settings-view').waitFor();
        await dialog(page).waitFor({ state: 'hidden' });
        assert.equal(await page.locator('#search-suggestions [role="option"]').count(), 0, 'Accepted context replacement retires the previous search.');
        report.pendingSettingsRetired = true;
      }
      // A tab click starts guarded target preparation. Search from the destination
      // only after that destination is visible, rather than racing its commit.
      await page.locator(`#${name === 'additional-copies' ? 'additional-copy' : name}-view`).waitFor();
      if (name === 'operations') { await page.locator('#operations-view').waitFor(); await page.emulateMedia({ colorScheme: 'dark' }); await scan(page, 'search-launcher-dark-operations'); }
      await open(page); await search(page, 'Ada   O\'Brien'); assert.match(await summary(page).textContent(), /2 matching/);
      await close(page); assert.equal(await page.locator('#search-asap').evaluate(node => node === document.activeElement), true);
      if (name === 'operations') await page.emulateMedia({ colorScheme: 'light' });
    }
    await open(page); await search(page, 'Éloïse author'); assert.match(await summary(page).textContent(), /5 matching/); await close(page);
    await open(page); await search(page, 'EXACT-BARCODE');
    await page.locator('#search-quick-input').press('ArrowDown');
    await page.locator('#search-quick-input').evaluate(node => node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true, cancelable: true })));
    assert.equal(await dialog(page).isVisible(), true);
    await page.keyboard.press('Enter'); await page.locator('#request-dialog[open]').waitFor();
    assert.equal(await page.locator('#status-tabs [data-status="closed"]').getAttribute('aria-selected'), 'true');
    assert.ok(page.url().includes(fixture.bigId));
    await open(page, true); assert.equal(await page.locator('#request-dialog').isVisible(), false);
    await close(page); assert.equal(await page.locator('#request-dialog[open]').isVisible(), true);
    await page.keyboard.press('Escape');

    await page.locator('.view-tab[data-view="settings"]').click();
    await page.locator('#settings-nav-patron').click();
    // Make Back a real departure from Settings rather than a safe, same-owner
    // Settings-section traversal, which correctly preserves drafts without consent.
    await page.locator('.view-tab[data-view="queue"]').click();
    await page.locator('.view-tab[data-view="settings"]').click();
    const note = page.locator('#patron-login-note'); await note.fill('Private unsaved search draft');
    await open(page); await search(page, 'EXACT-BARCODE');
    await decideConfirmation(page, () => page.goBack(), async decision => {
      assert.equal(await dialog(page).isVisible(), false, 'history retires quick search before consent');
      assert.equal(await page.locator('dialog[open]').count(), 1, 'only the discard decision owns the top layer');
      await decision.dismiss();
    });
    await page.waitForFunction(() => new URL(location.href).searchParams.get('stage') === 'settings');
    assert.equal(await note.inputValue(), 'Private unsaved search draft');
    assert.equal(await page.locator('#search-asap').evaluate(node => node === document.activeElement), true);
    await open(page); await search(page, 'EXACT-BARCODE');
    await decideConfirmation(page, () => page.locator('#search-option-0').click(), decision => decision.dismiss());
    assert.equal(await note.inputValue(), 'Private unsaved search draft');
    assert.equal(await page.locator('#settings-view').isVisible(), true); assert.equal(await page.locator('#search-dialog').isVisible(), false);
    await open(page); await search(page, 'needle');
    await decideConfirmation(page, () => page.locator('#search-dialog [data-search="all"]').click(), decision => decision.dismiss());
    assert.equal(await note.inputValue(), 'Private unsaved search draft');
    await open(page); await search(page, 'needle');
    await decideConfirmation(page, () => page.locator('#search-dialog [data-search="all"]').click(), decision => decision.accept());
    await page.locator('#search-view').waitFor();
    await page.waitForFunction(() => /36 matching/.test(document.querySelector('#search-view [data-search="summary"]').textContent));
    assert.ok(page.url().includes('stage=search')); assert.ok(!page.url().includes('needle'));
    assert.equal(await page.locator('#search-results-input').inputValue(), 'needle');
    await page.locator('#search-view [data-search="next"]').click();
    await page.waitForFunction(() => document.querySelector('#search-view [data-search="page"]').textContent === 'Page 2 of 2');
    await page.locator('#search-results-scope').selectOption(fixture.otherLibrary);
    await page.waitForFunction(() => /1 matching/.test(document.querySelector('#search-view [data-search="summary"]').textContent));
    await open(page); await search(page, 'needle');
    assert.equal(await page.locator('#search-quick-scope').inputValue(), fixture.otherLibrary);
    await close(page);
    await page.waitForFunction(() => /1 matching/.test(document.querySelector('#search-view [data-search="summary"]').textContent));
    assert.equal(await page.locator('#search-results-scope').inputValue(), fixture.otherLibrary);
    await page.locator('#search-results-scope').selectOption(fixture.library);
    await page.waitForFunction(() => /36 matching/.test(document.querySelector('#search-view [data-search="summary"]').textContent));
    await page.locator('#search-result-filter').selectOption('copy:closed');
    await page.waitForFunction(() => document.querySelector('#search-view [data-search="list"]').children.length === 1);
    assert.match(await page.locator('#search-view [data-search="summary"]').textContent(), /1 matching/);
    assert.match(await page.locator('#search-result-filter option[value="title:suggestion"]').textContent(), /28/);
    await scan(page, 'search-full-results'); report.dirtySettings = true;
    await page.locator('#search-view .search-result').click(); await page.locator('#request-dialog[open]').waitFor();
    assert.equal(await page.locator('#additional-copy-status-tabs [data-copy-status="closed"]').getAttribute('aria-selected'), 'true');
    await page.goBack(); await page.locator('#search-view').waitFor();
    assert.equal(await page.locator('#search-results-input').inputValue(), 'needle');
    await page.goForward(); await page.locator('#request-dialog[open]').waitFor(); await page.keyboard.press('Escape'); await page.locator('#search-view').waitFor();
    await page.goto(`${baseURL}/staff/?stage=search`);
    await page.locator('#search-view').waitFor(); assert.equal(await page.locator('#search-results-input').inputValue(), '');
    assert.match(await page.locator('#search-view [data-search="summary"]').textContent(), /Enter a title/);

    await page.locator('.view-tab[data-view="queue"]').click(); await open(page); await search(page, 'needle-suggestion');
    await page.locator('#search-option-0').click(); await page.locator('#request-dialog[open]').waitFor();
    const title = page.locator('#request-dialog').getByLabel('Title', { exact: true }); await title.fill('Unsaved title search source');
    await open(page, true); await search(page, 'EXACT-BARCODE');
    await decideConfirmation(page, () => page.locator('#search-option-0').click(), decision => decision.dismiss());
    assert.equal(await title.inputValue(), 'Unsaved title search source');
    assert.equal(await page.locator('#request-dialog[open]').count(), 1); report.dirtyTitle = true;
    await open(page, true); await search(page, '467222');
    await decideConfirmation(page, () => page.locator('#search-option-0').click(), decision => decision.accept());
    await page.locator('#request-dialog[open]').waitFor();
    await page.locator('#request-dialog-kicker').filter({ hasText: 'Additional copy' }).waitFor();
    await page.locator('#request-dialog').getByRole('button', { name: 'Assign', exact: true }).click();
    const assign = page.getByLabel('Assign additional-copy task'); await assign.waitFor();
    const candidates = await assign.locator('option').evaluateAll(options => options.map(option => option.value));
    if (candidates.length > 1) await assign.selectOption(candidates[1]);
    await open(page, true); await search(page, 'EXACT-BARCODE');
    await decideConfirmation(page, () => page.locator('#search-option-0').click(), decision => decision.dismiss());
    assert.equal(await assign.isVisible(), true); report.dirtyCopy = true;
    await open(page, true); await search(page, 'EXACT-BARCODE');
    await decideConfirmation(page, () => page.locator('#search-option-0').click(), decision => decision.accept());
    await page.locator('#request-dialog-kicker').filter({ hasText: `Request ${fixture.bigId}` }).waitFor(); await page.keyboard.press('Escape');

    await open(page); await search(page, 'none-found-anywhere'); assert.match(await summary(page).textContent(), /No matching/);
    injection = { kind: 'failure' }; await page.locator('#search-quick-input').fill('needle');
    await page.waitForFunction(() => /Search unavailable/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    await page.locator('#search-dialog [data-search="retry"]').click();
    await page.waitForFunction(() => /matching submission/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
    const delayed = injection = { kind: 'delay', started: deferred(), release: deferred(), completed: deferred() };
    await page.locator('#search-quick-input').fill('copy'); await delayed.started.promise;
    await search(page, 'EXACT-BARCODE'); delayed.release.resolve(); await delayed.completed.promise;
    assert.ok((await page.locator('#search-suggestions').textContent()).includes(fixture.bigId)); assert.doesNotMatch(await page.locator('#search-suggestions').textContent(), /Needle copy/);
    await search(page, 'literal%_[~'); assert.equal(await page.locator('#search-suggestions img').count(), 0);
    await close(page);
    await command('readOnly');
    await open(page); await search(page, 'EXACT-BARCODE'); await command('changeTitle');
    await page.locator('#search-option-0').click(); await page.locator('#request-dialog[open]').waitFor();
    assert.equal(await page.locator('#status-tabs [data-status="suggestion"]').getAttribute('aria-selected'), 'true');
    await page.keyboard.press('Escape');
    await open(page); await search(page, 'literal%_[~'); await command('deleteTitle');
    await page.locator('#search-option-0').click();
    await page.locator('#app-status').filter({ hasText: 'no longer available' }).waitFor();
    assert.equal(await page.locator('#request-dialog[open]').count(), 0); assert.ok(!page.url().includes('literal'));
    await open(page); await search(page, 'literal%_[~'); assert.match(await summary(page).textContent(), /No matching/); report.staleDetails = true;
    await search(page, 'needle'); await page.emulateMedia({ colorScheme: 'dark' }); await scan(page, 'search-desktop-dark');
    await page.setViewportSize({ width: 390, height: 844 }); await scan(page, 'search-mobile-dark');
    await page.emulateMedia({ colorScheme: 'light' }); await scan(page, 'search-mobile-light');
    await page.setViewportSize({ width: 390, height: 360 });
    assert.equal(await page.locator('#search-suggestions [role="option"]').count(), 12);
    assert.equal(await page.locator('#search-dialog [data-search="all"]').isVisible(), true);
    await page.keyboard.press('ArrowUp'); await scan(page, 'search-mobile-short-last');
    assert.equal(await selectedTitleVisible(page), 'search-option-11');
    await page.keyboard.press('ArrowDown'); await scan(page, 'search-mobile-short-first');
    assert.equal(await selectedTitleVisible(page), 'search-option-0');
    await page.keyboard.press('Tab'); assert.equal(await page.locator('#search-quick-scope').evaluate(node => node === document.activeElement), true);
    await page.keyboard.press('Shift+Tab'); assert.equal(await page.locator('#search-quick-input').evaluate(node => node === document.activeElement), true);
    await page.setViewportSize({ width: 760, height: 360 }); await scan(page, 'search-short'); await close(page);
    const ordinaryContext = await browser.newContext({ viewport: { width: 390, height: 844 }, extraHTTPHeaders: headers(fixture.ordinary) });
    await guard(ordinaryContext); const ordinary = await ordinaryContext.newPage();
    await ordinary.goto(`${baseURL}/staff/?scope=${fixture.otherLibrary}`); await ordinary.locator('#request-grid tbody tr').first().waitFor();
    await open(ordinary); await search(ordinary, 'needle');
    assert.equal(await ordinary.locator('#search-quick-scope').isVisible(), false);
    assert.match(await ordinary.locator('#search-dialog [data-search="scope"]').textContent(), /Search Library/);
    assert.doesNotMatch(await ordinary.locator('#search-suggestions').textContent(), /Other Search Library/);
    await scan(ordinary, 'search-ordinary-mobile'); await ordinaryContext.close(); await context.close();
    for (const width of [1280, 1440]) {
      const extension = path.join(__dirname, 'native-zoom-extension');
      const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, `zoom-${width}`), { channel: 'chromium', headless: true, viewport: null,
        extraHTTPHeaders: headers(fixture.super), args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`] });
      try {
        await guard(zoomContext); const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
        const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
        zoomPage.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(zoomPage, report.errors);
        await zoomPage.goto(`${baseURL}/staff/?scope=${fixture.library}`); await zoomPage.locator('#request-grid tbody tr').first().waitFor();
        const ratio = await zoomPage.evaluate(() => devicePixelRatio);
        assert.equal(await worker.evaluate(async origin => { const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(origin));
          await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' }); await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id); }, new URL(baseURL).origin), 2);
        await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < .01, ratio);
        await zoomPage.keyboard.press('Control+k'); await dialog(zoomPage).waitFor(); await search(zoomPage, 'needle');
        await zoomPage.keyboard.press('ArrowDown'); await zoomPage.keyboard.press('Tab'); await zoomPage.keyboard.press('Shift+Tab');
        await scan(zoomPage, `search-native200-${width}`);
        const cdp = await zoomContext.newCDPSession(zoomPage), metrics = await cdp.send('Page.getLayoutMetrics');
        assert.equal(metrics.visualViewport.zoom, 2); assert.equal(await zoomPage.evaluate(() => getComputedStyle(document.documentElement).zoom), '1');
        const capture = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `search-native200-${width}-viewport.png`), Buffer.from(capture.data, 'base64'));
        report.nativeZoom.push({ width, zoom: metrics.visualViewport.zoom }); await cdp.detach();
      } finally { await zoomContext.close(); }
    }
    assert.deepEqual(report.mutations, []); assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
  } catch (error) {
    report.failure = { message: error.message };
    if (page && !page.isClosed()) {
      try {
        report.failure.state = await page.evaluate(() => ({ url: location.href, dialogOpen: document.querySelector('#search-dialog').open,
          summary: document.querySelector('#search-dialog [data-search="summary"]').textContent, focus: document.activeElement?.id,
          visibleViews: ['queue', 'additional-copy', 'analytics', 'operations', 'settings', 'profile', 'search'].filter(name => !document.getElementById(`${name}-view`).hidden) }));
        await page.screenshot({ path: path.join(artifacts, 'search-failure.png'), fullPage: true });
      } catch (diagnosticError) { report.failure.diagnosticError = diagnosticError.message; }
    }
    throw error;
  } finally { await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
