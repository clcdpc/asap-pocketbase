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
  const report = {
    states: [], nativeZoom: [], external: [], errors: [], reads: [], mutation: false, ordinaryScope: false, modalReturn: null,
    controlledClientFilterProjection: { taggedRequestId: fixture.mutationId, similarRequestId: fixture.mutationId, databaseWrites: false }
  };
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
  const requestsNav = (page, total) => page.getByRole('button', { name: `Requests (${total})`, exact: true });
  const counts = async (page, totals, suggestions = totals[0]) => {
    const expected = names(totals);
    await page.waitForFunction(expected => JSON.stringify([...document.querySelectorAll('#status-tabs [data-status]')]
      .map(tab => tab.getAttribute('aria-label'))) === JSON.stringify(expected), expected);
    assert.deepEqual(await countNames(page), expected);
    await requestsNav(page, suggestions).waitFor();
    assert.deepEqual(await page.locator('#status-tabs .status-count').allTextContents(), totals.map(total => `(${total})`));
    for (let index = 0; index < totals.length; index++) {
      assert.equal(await page.getByRole('tab', { name: expected[index], exact: true }).count(), 1);
      assert.equal(await page.locator(`#status-tabs [data-status="${stages[index]}"] .status-count`).evaluate(node => node.classList.contains('quiet')), totals[index] === 0);
    }
    assert.deepEqual(await page.locator('#status-count-state').evaluate(node => ({
      text: node.textContent,
      role: node.getAttribute('role'),
      live: node.getAttribute('aria-live'),
      display: getComputedStyle(node).display,
      height: node.getBoundingClientRect().height
    })), { text: '', role: 'status', live: 'polite', display: 'none', height: 0 },
    'Ready feedback is empty and reserves no helper spacing.');
  };
  const unknown = async (page, loading) => {
    const expected = labels.map(label => `${label} (count ${loading ? 'loading' : 'unavailable'})`);
    await page.waitForFunction(expected => JSON.stringify([...document.querySelectorAll('#status-tabs [data-status]')]
      .map(tab => tab.getAttribute('aria-label'))) === JSON.stringify(expected), expected);
    assert.deepEqual(await countNames(page), expected);
    assert.match(await page.locator('#status-count-state').textContent(), loading ? /Counts loading/ : /requests unavailable/i);
    await page.getByRole('button', { name: 'Requests', exact: true }).waitFor();
    assert.match(await page.locator('#requests-count-state').textContent(), loading ? /loading/ : /unavailable/);
    assert.equal(await page.locator('#queue-empty').isHidden(), true);
    assert.ok(await page.locator('#status-count-state').evaluate(node => node.getBoundingClientRect().height > 0),
      'Loading and unavailable feedback remains visible.');
  };
  const refreshFocus = async (page, expected, label) => {
    assert.deepEqual(await page.locator('#refresh-queue').evaluate(node => ({
      active: node === document.activeElement,
      visible: node.matches(':focus-visible'),
      disabled: node.disabled
    })), { active: expected, visible: expected, disabled: false }, label);
  };
  const focusRefreshByKeyboard = async page => {
    await page.locator('#request-search').focus();
    for (let index = 0; index < 4; index++) await page.keyboard.press('Tab');
    await refreshFocus(page, true, 'Refresh is reached with visible keyboard focus.');
  };
  const scan = async (page, name, visible = null) => {
    await page.evaluate(axe);
    const result = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      overflow: document.documentElement.scrollWidth > innerWidth,
      countStateMatches: (() => {
        const node = document.querySelector('#status-count-state'), state = node.textContent.trim();
        if (document.querySelector('#queue-view').hidden) return node.getClientRects().length === 0;
        return state ? node.getClientRects().length > 0 : getComputedStyle(node).display === 'none' && node.getBoundingClientRect().height === 0;
      })(),
      danglingDescriptions: [...document.querySelectorAll('[aria-describedby]')].flatMap(node =>
        (node.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean)
          .filter(id => !document.getElementById(id)).map(id => ({ node: node.id || node.tagName, id }))),
      images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
    }));
    assert.deepEqual(result.violations, [], `${name}: axe`); assert.equal(result.overflow, false, `${name}: document overflow`);
    assert.equal(result.countStateMatches, true, `${name}: count feedback matches its state without empty spacing`);
    assert.deepEqual(result.danglingDescriptions, [], `${name}: no aria-describedby reference is dangling`);
    assert.equal(result.images, true, `${name}: images`); report.states.push({ name, ...result });
    // Axe can scroll the tab strip while inspecting its other buttons.
    if (visible) await visible.scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
  };
  async function checkHeaders(page, selector, name) {
    const headers = page.locator(selector + ' th[aria-sort]');
    await headers.first().waitFor();
    const geometry = await headers.evaluateAll(nodes => nodes.map(node => {
      const label = node.querySelector('.gridjs-th-content'), sort = node.querySelector('.gridjs-sort');
      const cell = node.getBoundingClientRect(), text = label.getBoundingClientRect(), button = sort.getBoundingClientRect();
      return { label: label.textContent, height: cell.height, textHeight: text.height, line: parseFloat(getComputedStyle(label).lineHeight), buttonHeight: button.height,
        buttonWidth: button.width, centers: Math.abs((text.top + text.bottom - button.top - button.bottom) / 2),
        contained: text.left >= cell.left && button.right <= cell.right + 1 && text.right <= button.left + 1 &&
          text.top >= cell.top && text.bottom <= cell.bottom && button.top >= cell.top && button.bottom <= cell.bottom &&
          label.scrollHeight <= label.clientHeight + 1 && label.scrollWidth <= label.clientWidth + 1 };
    }));
    await fs.writeFile(path.join(artifacts, `${name}-headers.json`), JSON.stringify(geometry));
    const contentHeight = Math.max(28, ...geometry.map(cell => cell.textHeight));
    for (const cell of geometry) {
      assert.ok(cell.contained && cell.centers <= 1 && cell.height <= contentHeight + 11,
        `${name}: aligned labels, no clipping and compact padding: ${JSON.stringify(cell)}`);
      assert.ok(cell.buttonHeight >= 24 && cell.buttonWidth >= 24);
    }
    if (name === 'desktop-copies') assert.ok(geometry.some(cell => cell.textHeight >= cell.line * 1.9), 'Long label wraps at desktop grid width.');
    const sorted = headers.first(), button = sorted.locator('button');
    const direction = await sorted.getAttribute('aria-sort') === 'ascending' ? 'descending' : 'ascending';
    await button.focus(); await page.keyboard.press('Space');
    await page.waitForFunction(({ selector, direction }) => document.querySelector(selector + ' th[aria-sort]').getAttribute('aria-sort') === direction,
      { selector, direction });
    assert.ok((await button.getAttribute('aria-label')).endsWith(direction === 'ascending' ? 'descending' : 'ascending'));
    assert.equal(await button.evaluate(node => node === document.activeElement && node.matches(':focus-visible') &&
      getComputedStyle(node).outlineStyle !== 'none'), true);
  }
  async function checkCopyHeading(page, status) {
    const original = page.viewportSize();
    for (const [width, height] of [[390, 844], [901, 862], [1280, 900], [1920, 1080], [3000, 1000]]) {
      await page.setViewportSize({ width, height });
      const layout = await page.locator('#additional-copy-view').evaluate(view => {
        const bounds = selector => view.querySelector(selector).getBoundingClientRect();
        const title = bounds('h1'), scope = bounds('#additional-copy-scope-field'), deletion = bounds('#bulk-delete-closed-copies');
        return { summaryText: view.querySelector('#additional-copy-summary').textContent,
          summaryHeight: bounds('#additional-copy-summary').height, titleBottom: title.bottom,
          scopeTop: scope.top, scopeBottom: scope.bottom, deleteTop: deletion.top, deleteBottom: deletion.bottom,
          tabsTop: bounds('[role="tablist"]').top, overflow: document.documentElement.scrollWidth > innerWidth };
      });
      assert.equal(layout.summaryText, '');
      assert.equal(layout.summaryHeight, 0);
      assert.equal(layout.overflow, false);
      assert.equal(layout.deleteBottom > 0, status === 'closed');
      assert.ok(layout.tabsTop > Math.max(layout.scopeBottom, layout.deleteBottom));
      if (width === 390) {
        assert.ok(layout.scopeTop > layout.titleBottom);
        if (status === 'closed') assert.ok(layout.deleteTop > layout.scopeBottom);
      } else {
        assert.ok(Math.abs(layout.scopeBottom - layout.titleBottom) <= 1);
        if (status === 'closed') assert.ok(Math.abs(layout.scopeBottom - layout.deleteBottom) <= 1);
      }
      (report.copyHeadings ||= []).push({ status, width, height, ...layout });
    }
    await page.setViewportSize(original);
  }
  async function checkCopyWorkspace(page, name) {
    const countBeforeSort = await page.locator('#requests-count').textContent();
    await checkHeaders(page, '#request-grid', name + '-requests');
    assert.equal(await page.locator('#requests-count').textContent(), countBeforeSort);
    const nav = page.getByRole('button', { name: 'Additional copies (26)', exact: true });
    await nav.click();
    const open = page.getByRole('tab', { name: 'Open (26)', exact: true });
    const closed = page.getByRole('tab', { name: 'Closed (3)', exact: true });
    await open.waitFor(); await closed.waitFor();
    const filterReads = [];
    const observeCount = req => {
      if (new URL(req.url()).pathname.endsWith('/additional-copies/counts')) filterReads.push(req.url());
    };
    page.on('request', observeCount);
    await checkHeaders(page, '#additional-copy-grid', name + '-copies');
    if (name === 'desktop') {
      await checkCopyHeading(page, 'open');
      await page.locator('#additional-copy-grid').getByRole('button', { name: 'Next', exact: true }).click();
      await page.waitForFunction(() => document.querySelectorAll('#additional-copy-grid .additional-copy-open').length === 1);
      await page.locator('#additional-copy-search').fill('Counted copy 01');
      await page.locator('#additional-copy-grid .gridjs-summary').filter({ hasText: 'Showing 1 to 1 of 1 results' }).waitFor();
      assert.equal(await page.locator('#additional-copy-summary').isHidden(), true);
      await open.waitFor(); await closed.waitFor(); await nav.waitFor();
      await page.locator('#additional-copy-search').fill('');
      await page.locator('#additional-copy-claim-filter').selectOption('mine');
      await requestsNav(page, 0).waitFor();
      await page.locator('#additional-copy-empty').waitFor();
      assert.equal(await page.locator('#additional-copy-summary').isHidden(), true);
      await open.waitFor(); await closed.waitFor();
      await page.locator('#additional-copy-claim-filter').selectOption('all');
      await requestsNav(page, fixture.libraryCounts[0]).waitFor();
      assert.deepEqual(filterReads, [], 'Sort, pagination, search and claims operate on the list without fetching totals.');
      await page.locator('#additional-copy-library-scope').selectOption(fixture.otherLibrary);
      await requestsNav(page, fixture.otherCounts[0]).waitFor();
      await page.getByRole('button', { name: 'Additional copies (0)', exact: true }).waitFor();
      await page.getByRole('tab', { name: 'Open (0)', exact: true }).waitFor();
      await page.getByRole('tab', { name: 'Closed (0)', exact: true }).waitFor();
      await page.locator('#additional-copy-library-scope').selectOption(fixture.library);
    }
    page.off('request', observeCount);
    await open.focus(); await page.keyboard.press('End');
    await page.locator('[data-copy-status="closed"][aria-selected="true"]').waitFor();
    await page.locator('#additional-copy-grid .gridjs-summary').filter({ hasText: 'Showing 1 to 3 of 3 results' }).waitFor();
    if (name === 'desktop') await checkCopyHeading(page, 'closed');
    await nav.waitFor();
    assert.equal(await closed.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    await scan(page, name + '-copy-totals', closed);
    const countPath = '**/api/asap/staff/additional-copies/counts?*';
    await page.route(countPath, route => route.fulfill({ json: { scope: fixture.library, openCount: 123456, closedCount: 987654 } }));
    await page.locator('#refresh-additional-copies').click();
    for (const [role, label] of [['button', 'Additional copies (123456)'], ['tab', 'Open (123456)'], ['tab', 'Closed (987654)']]) {
      const control = page.getByRole(role, { name: label, exact: true });
      await control.scrollIntoViewIfNeeded();
      assert.ok(await control.evaluate(node => {
        const box = node.getBoundingClientRect(), count = node.querySelector('[data-copy-count]').getBoundingClientRect();
        return node.scrollWidth <= node.clientWidth + 1 && count.left >= box.left && count.right <= box.right &&
          count.top >= box.top && count.bottom <= box.bottom && document.documentElement.scrollWidth <= innerWidth;
      }), `${name}: complete large count fits within ${label}`);
    }
    await page.unroute(countPath);
    await page.locator('#refresh-additional-copies').click(); await nav.waitFor();
    await page.locator('[data-view="queue"]').click();
    await page.locator('#queue-view').waitFor({ state: 'visible' });
  }
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers(fixture.super) });
    await guard(context);
    await context.route('**/api/asap/staff/title-requests?*', async route => {
      report.reads.push(route.request().url());
      const control = nextRead; nextRead = null;
      if (control?.mode === 'failed') return route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"Deterministic count read failure"}' });
      if (control?.mode === 'malformed') return route.fulfill({ status: 200, contentType: 'application/json', body: '{"scope":"44201"}' });
      const response = await route.fetch();
      const body = await response.json();
      if (!response.ok() || !Array.isArray(body.items)) return route.fulfill({ response });
      // Deterministic UI-only projection: the authorized SQL row exercises Tag and Similarity filters.
      const tagged = { ...body, items: body.items.map(item => {
        const target = String(item.id) === fixture.mutationId;
        return { ...item, workflowTags: target ? ['Browser tag'] : [],
          relatedRequests: { ...(item.relatedRequests || {}), count: target ? 1 : 0 } };
      }) };
      if (control) {
        control.started.resolve();
        await control.release.promise;
      }
      try { await route.fulfill({ response, json: tagged }); }
      catch (error) { if (!/closed|canceled|cancelled|invalid interception/i.test(error.message)) throw error; }
      finally { control?.completed.resolve(); }
    });
    const holdRead = mode => (nextRead = { mode, started: deferred(), release: deferred(), completed: deferred() });
    const page = await context.newPage();
    let copyReads = 0;
    page.on('request', request => {
      if (new URL(request.url()).pathname === '/api/asap/staff/additional-copies/counts') copyReads++;
    });
    const initial = holdRead('hold');
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`);
    await initial.started.promise;
    assert.equal(await page.locator('#queue-empty').isHidden(), true);
    assert.equal(await page.locator('#request-grid').isHidden(), true);
    initial.release.resolve(); await initial.completed.promise;
    await counts(page, fixture.libraryCounts);
    await page.getByRole('button', { name: 'Additional copies (26)', exact: true }).waitFor();
    assert.equal(await page.locator('#additional-copy-grid table').count(), 0, 'Navigation totals load independently of the copy list.');
    assert.equal(await page.locator('#queue-title').evaluate(node => document.activeElement === node), false, 'Startup counts preserve native initial focus.');
    const search = page.locator('#request-search'), claim = page.locator('#claim-filter');
    const tag = page.locator('#tag-filter'), similarity = page.locator('#similar-filter');
    const visibleIds = locator => locator.evaluateAll(nodes => nodes.map(node => node.dataset.requestId));
    await search.focus(); await page.keyboard.press('Tab');
    const claimFocus = await claim.evaluate(node => ({ active: node === document.activeElement, visible: node.matches(':focus-visible') }));
    assert.deepEqual(claimFocus, { active: true, visible: true }, 'Claims receives visible keyboard focus.');
    await page.keyboard.press('Home');
    await page.waitForFunction(() => document.querySelector('#claim-filter').value === 'all' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 25 of 28 results' &&
      document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    const firstPageIds = await visibleIds(page.locator('#request-grid tbody .request-title-link'));
    assert.equal(firstPageIds.length, 25);
    assert.equal(new Set(firstPageIds).size, 25, 'All shows 25 distinct first-page Suggestions.');
    await page.keyboard.press('End');
    await page.waitForFunction(() => document.querySelector('#claim-filter').value === 'unclaimed' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 25 of 28 results' &&
      document.querySelectorAll('#request-grid tbody .request-title-link').length === 25 &&
      [...document.querySelectorAll('#request-grid tbody .claim-label')].every(node => node.textContent.trim() === 'Unclaimed'));
    assert.equal(await claim.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    assert.deepEqual(await visibleIds(page.locator('#request-grid tbody .request-title-link')), firstPageIds,
      'Unclaimed matches the same 25 visible IDs as All; 28 total Suggestions are available.');
    await counts(page, fixture.libraryCounts);
    await page.keyboard.press('ArrowUp');
    await page.waitForFunction(() => document.querySelector('#claim-filter').value === 'mine' &&
      !document.querySelector('#queue-empty').hidden);
    assert.equal(await claim.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    await page.locator('#queue-empty').waitFor();
    assert.deepEqual(await visibleIds(page.locator('#request-grid tbody .request-title-link')), [],
      'Mine has no matching rows in the unclaimed SQL fixture.');
    await counts(page, fixture.libraryCounts, 0);
    await page.keyboard.press('Home');
    await page.waitForFunction(() => document.querySelector('#claim-filter').value === 'all' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 25 of 28 results' &&
      document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    assert.deepEqual(await visibleIds(page.locator('#request-grid tbody .request-title-link')), firstPageIds,
      'All restores the exact first-page ID set.');
    await counts(page, fixture.libraryCounts);
    await page.keyboard.press('Tab');
    const tagFocus = await tag.evaluate(node => ({ active: node === document.activeElement, visible: node.matches(':focus-visible') }));
    assert.deepEqual(tagFocus, { active: true, visible: true }, 'Tag receives visible keyboard focus.');
    await page.keyboard.press('ArrowDown');
    await page.waitForFunction(id => document.querySelector('#tag-filter').value === 'Browser tag' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 1 of 1 results' &&
      document.querySelectorAll('#request-grid .request-title-link').length === 1 &&
      document.querySelector('#request-grid .request-title-link')?.dataset.requestId === id, fixture.mutationId);
    assert.equal(await tag.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    await counts(page, fixture.libraryCounts, 1);
    await page.keyboard.press('Home');
    await page.waitForFunction(() => document.querySelector('#tag-filter').value === 'all' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 25 of 28 results' &&
      document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    assert.deepEqual(await visibleIds(page.locator('#request-grid tbody .request-title-link')), firstPageIds,
      'All tags restores the exact first-page ID set.');
    await counts(page, fixture.libraryCounts);
    await page.keyboard.press('Tab');
    const similarityFocus = await similarity.evaluate(node => ({ active: node === document.activeElement, visible: node.matches(':focus-visible') }));
    assert.deepEqual(similarityFocus, { active: true, visible: true }, 'Similarity receives visible keyboard focus.');
    await page.keyboard.press('Home'); await page.keyboard.press('ArrowDown');
    await page.waitForFunction(id => document.querySelector('#similar-filter').value === 'similar' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 1 of 1 results' &&
      document.querySelectorAll('#request-grid .request-title-link').length === 1 &&
      document.querySelector('#request-grid .request-title-link')?.dataset.requestId === id, fixture.mutationId);
    assert.equal(await similarity.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    await counts(page, fixture.libraryCounts, 1);
    await page.keyboard.press('Home');
    await page.waitForFunction(() => document.querySelector('#similar-filter').value === 'all' &&
      document.querySelector('#request-grid .gridjs-summary')?.textContent === 'Showing 1 to 25 of 28 results' &&
      document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    assert.deepEqual(await visibleIds(page.locator('#request-grid tbody .request-title-link')), firstPageIds,
      'All similarity restores the exact first-page ID set.');
    await counts(page, fixture.libraryCounts);
    await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody .request-title-link').length === 25);
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody .request-title-link').length === 3);
    assert.equal(await page.locator('#request-grid .gridjs-summary').textContent(), 'Showing 26 to 28 of 28 results');
    await counts(page, fixture.libraryCounts);
    await page.locator('#request-search').fill('no matching requests');
    await page.locator('#queue-empty').waitFor();
    await counts(page, fixture.libraryCounts, 0);
    await page.locator('#request-search').fill('');
    await page.locator('#request-grid .gridjs-summary').filter({ hasText: 'Showing 1 to 25 of 28 results' }).waitFor();
    await scan(page, 'counts-desktop');
    await checkCopyWorkspace(page, 'desktop');
    await counts(page, fixture.libraryCounts);

    const beforeTabs = report.reads.length, beforeCopyReads = copyReads;
    const suggestion = page.locator('#status-tabs [data-status="suggestion"]');
    await suggestion.focus(); await page.keyboard.press('End');
    const closed = page.getByRole('tab', { name: 'Closed (3)', exact: true });
    await page.waitForFunction(() => document.querySelector('#status-tabs [data-status="closed"]').getAttribute('aria-selected') === 'true');
    assert.equal(await closed.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true);
    for (const stage of ['hold_placed', 'pending_hold', 'outstanding_purchase', 'suggestion']) {
      await page.keyboard.press('ArrowLeft');
      await page.waitForFunction(stage => document.querySelector(`#status-tabs [data-status="${stage}"]`)?.getAttribute('aria-selected') === 'true', stage);
      const tab = page.locator(`#status-tabs [data-status="${stage}"]`);
      assert.equal(await tab.evaluate(node => node === document.activeElement && node.matches(':focus-visible')), true,
        `${stage}: keyboard status selection retains visible focus.`);
      await counts(page, fixture.libraryCounts);
    }
    assert.deepEqual(await page.locator('#status-tabs [data-status]').evaluateAll(tabs => tabs.map(tab => tab.tabIndex)), [0, -1, -1, -1, -1]);
    assert.equal(report.reads.length, beforeTabs, 'Tab selection uses the already complete dataset.');
    assert.equal(copyReads, beforeCopyReads, 'Title stage changes leave the copy totals alone.');

    const held = holdRead('hold');
    await page.locator('#refresh-queue').click(); await held.started.promise; await unknown(page, true);
    // Refresh disables its native button. Keep a deliberate tab focus while
    // the read completes and changes that tab's accessible count name.
    await suggestion.focus(); await scan(page, 'counts-loading');
    held.release.resolve(); await held.completed.promise; await counts(page, fixture.libraryCounts);
    assert.equal(await suggestion.evaluate(node => node === document.activeElement), true, 'Counts refresh preserves deliberate tab focus.');
    for (const mode of ['failed', 'malformed']) {
      await focusRefreshByKeyboard(page);
      holdRead(mode); await page.keyboard.press('Enter'); await unknown(page, false); await scan(page, `counts-${mode}`);
      await refreshFocus(page, true, `${mode} failure returns visible focus to Refresh.`);
      const retry = holdRead('hold');
      await page.keyboard.press('Enter'); await retry.started.promise; await unknown(page, true);
      assert.equal(await page.locator('#queue-title').evaluate(node => node === document.activeElement), true,
        'A keyboard retry hands focus to the queue heading while Refresh is disabled.');
      retry.release.resolve(); await retry.completed.promise; await counts(page, fixture.libraryCounts);
      await refreshFocus(page, true, 'A successful keyboard retry returns visible focus to Refresh.');
    }
    const obsolete = holdRead('hold');
    await page.locator('#refresh-queue').click(); await obsolete.started.promise;
    await page.locator('#library-scope').selectOption(fixture.otherLibrary); await counts(page, fixture.otherCounts);
    await page.getByRole('button', { name: 'Additional copies (0)', exact: true }).waitFor();
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
    await page.emulateMedia({ colorScheme: 'dark' });
    await checkCopyWorkspace(page, 'mobile-dark');
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
        await checkCopyWorkspace(zoomPage, `native-200-${width}`);
        if (width === 1440) {
          await zoomPage.locator('[data-view="additional-copies"]').click();
          await zoomPage.getByRole('tab', { name: 'Closed (3)', exact: true }).waitFor();
          await zoomPage.locator('#bulk-delete-closed-copies').click();
          await zoomPage.locator('#bulk-delete-scope').selectOption(fixture.library);
          await zoomPage.locator('#bulk-delete-preview').click();
          await zoomPage.locator('#bulk-delete-items').getByText(/Additional-copy task/).first().waitFor();
          await zoomPage.locator('#bulk-delete-confirmation').fill('DELETE');
          await zoomPage.locator('#bulk-delete-execute').click();
          await zoomPage.locator('#bulk-delete-summary').filter({ hasText: 'Both Closed views were refreshed' }).waitFor();
          await zoomPage.keyboard.press('Escape');
          await zoomPage.getByRole('tab', { name: 'Closed (0)', exact: true }).waitFor();
          await zoomPage.locator('#additional-copy-empty').waitFor();
          assert.equal(await zoomPage.locator('#additional-copy-summary').isHidden(), true);
          await zoomPage.getByRole('button', { name: 'Additional copies (26)', exact: true }).waitFor();
        }
      } finally { await zoomContext.close(); }
    }
    assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
