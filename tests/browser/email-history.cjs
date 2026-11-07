'use strict';
const { rejectNativeDialogs } = require('./confirmation.cjs');

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

async function main() {
  const [origin, artifacts, library, otherLibrary, staffId, tenantId, email, adminId, adminTenantId, adminEmail] = process.argv.slice(2);
  assert.ok(adminEmail, 'Usage: email-history.cjs origin artifacts library other-library staff tenant email admin-id admin-tenant email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], writes: 0, emailReads: [], states: [], focus: [], races: {} };
  const contexts = [];
  const pendingGateReleases = new Set();
  const listResponse = response => response.request().method() === 'GET' &&
    new URL(response.url()).pathname === '/api/asap/staff/email-operations';
  const requestHeaders = actor => ({
    'X-ASAP-Test-Staff-Id': actor.id,
    'X-ASAP-Test-Tenant-Id': actor.tenant,
    'X-ASAP-Test-Staff-Email': actor.email
  });
  const superActor = { id: staffId, tenant: tenantId, email };
  const libraryActor = { id: adminId, tenant: adminTenantId, email: adminEmail };

  async function newPage({ actor = superActor, width = 1280, height = 900, colorScheme = 'light' } = {}) {
    const context = await browser.newContext({
      timezoneId: 'America/New_York', locale: 'en-US', colorScheme,
      viewport: { width, height }, extraHTTPHeaders: requestHeaders(actor)
    });
    contexts.push(context);
    context.setDefaultTimeout(15000);
    context.setDefaultNavigationTimeout(20000);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++;
      return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.pageErrors.push(error.message));
    rejectNativeDialogs(page, report.pageErrors);
    page.on('request', request => {
      if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) report.writes++;
    });
    page.on('response', response => {
      if (!listResponse(response)) return;
      const url = new URL(response.url());
      report.emailReads.push({
        scope: url.searchParams.get('scope'), search: url.searchParams.get('search'),
        status: url.searchParams.get('status'), type: url.searchParams.get('type'),
        fromUtc: url.searchParams.get('fromUtc'), beforeUtc: url.searchParams.get('beforeUtc'),
        sort: url.searchParams.get('sort'), page: url.searchParams.get('page'),
        statusCode: response.status()
      });
    });
    return { context, page };
  }

  function historyUrl({ scope = library, testScope = otherLibrary, page = 1, search = '', status = '', type = '', from = '', through = '', sort = 'newest', reference = '' } = {}) {
    const url = new URL('/staff/', origin);
    url.searchParams.set('stage', 'operations');
    url.searchParams.set('opSection', 'email');
    url.searchParams.set('emailScope', scope);
    url.searchParams.set('emailTestScope', testScope);
    url.searchParams.set('emailPage', String(page));
    url.searchParams.set('emailSort', sort);
    if (search) url.searchParams.set('emailSearch', search);
    if (status) url.searchParams.set('emailStatus', status);
    if (type) url.searchParams.set('emailType', type);
    if (from) url.searchParams.set('emailFrom', from);
    if (through) url.searchParams.set('emailThrough', through);
    if (reference) url.searchParams.set('emailReference', reference);
    return url.toString();
  }

  async function open(page, route = {}) {
    const expectedScope = route.scope || library;
    const response = page.waitForResponse(item => listResponse(item) &&
      new URL(item.url()).searchParams.get('scope') === expectedScope);
    await page.goto(historyUrl(route), { waitUntil: 'networkidle' });
    await page.locator('#operations-email-panel').waitFor({ state: 'visible' });
    const listed = await response;
    const data = await listed.json();
    assert.equal(listed.status(), 200, JSON.stringify(data));
    await waitSettled(page);
    return data;
  }

  async function waitSettled(page) {
    await page.waitForFunction(() => {
      const table = document.querySelector('#email-operations-table');
      return table && !table.hasAttribute('aria-busy');
    });
  }

  async function waitForGate(promise, name) {
    let timeout;
    try {
      await Promise.race([promise, new Promise((_, reject) => {
        timeout = setTimeout(() => reject(new Error(`${name} response was never held.`)), 10000);
      })]);
    } finally {
      clearTimeout(timeout);
    }
  }

  function createGate() {
    let resolve;
    let released = false;
    const promise = new Promise(complete => { resolve = complete; });
    const release = () => {
      if (released) return;
      released = true;
      pendingGateReleases.delete(release);
      resolve();
    };
    pendingGateReleases.add(release);
    return { promise, release };
  }

  async function readAfter(page, action, predicate = () => true, expectedStatus = 200) {
    const responsePromise = page.waitForResponse(response => listResponse(response) && predicate(new URL(response.url())));
    await action();
    const response = await responsePromise;
    const data = await response.json();
    assert.equal(response.status(), expectedStatus, JSON.stringify(data));
    await waitSettled(page);
    return { data, response, query: new URL(response.url()).searchParams };
  }

  async function nextPage(page, expectedPage) {
    const { data, query } = await readAfter(page, () => page.locator('#email-next').click(),
      url => url.searchParams.get('page') === String(expectedPage));
    assert.equal(data.page, expectedPage);
    return data;
  }

  async function scan(page, name, { nativeViewport = false } = {}) {
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)));
    const layout = await page.evaluate(() => ({
      width: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth,
      tableWidth: document.querySelector('#email-operations-table').scrollWidth,
      tableClientWidth: document.querySelector('#email-operations-table').clientWidth,
      imagesLoaded: [...document.images].filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      }).every(image => image.complete && image.naturalWidth > 0),
      statusLabel: (() => {
        const select = document.querySelector('#email-status');
        const style = getComputedStyle(select);
        const text = document.createElement('span');
        text.textContent = select.selectedOptions[0].textContent;
        Object.assign(text.style, { position: 'fixed', visibility: 'hidden', whiteSpace: 'pre',
          font: style.font, letterSpacing: style.letterSpacing });
        document.body.append(text);
        const textWidth = text.getBoundingClientRect().width;
        text.remove();
        // Native Chromium reserves an arrow slot beyond the computed padding.
        const availableWidth = select.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight) - 24;
        return { value: select.value, text: select.selectedOptions[0].textContent, textWidth,
          availableWidth, controlWidth: select.getBoundingClientRect().width };
      })(),
      colorScheme: getComputedStyle(document.querySelector('#operations-view')).colorScheme,
      bodyBackground: getComputedStyle(document.body).backgroundColor
    }));
    let capture = null;
    if (nativeViewport) {
      const cdp = await page.context().newCDPSession(page);
      try {
        assert.equal((await cdp.send('Page.getLayoutMetrics')).visualViewport.zoom, 2);
        const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight,
          ratio: devicePixelRatio, htmlZoom: getComputedStyle(document.documentElement).zoom }));
        assert.equal(viewport.htmlZoom, '1', 'Native evidence must use browser zoom, not CSS scaling.');
        // Playwright's full-page clip is incorrect at native browser zoom.
        // Capture the compositor viewport directly, following the existing
        // acceptance and Polaris-search evidence contract.
        const result = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        const buffer = Buffer.from(result.data, 'base64');
        assert.equal(buffer.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
        const width = buffer.readUInt32BE(16), height = buffer.readUInt32BE(20);
        const expectedWidth = Math.round(viewport.width * viewport.ratio), expectedHeight = Math.round(viewport.height * viewport.ratio);
        assert.ok(Math.abs(width - expectedWidth) <= 1 && Math.abs(height - expectedHeight) <= 1,
          `Native capture ${width}x${height} must match the physical viewport ${expectedWidth}x${expectedHeight}.`);
        capture = { method: 'cdp-viewport', width, height, expectedWidth, expectedHeight,
          cssWidth: viewport.width, cssHeight: viewport.height, devicePixelRatio: viewport.ratio, htmlZoom: viewport.htmlZoom };
        await fs.writeFile(path.join(artifacts, `${name}.png`), buffer);
      } finally { await cdp.detach(); }
    } else await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, violations, layout, capture });
    assert.deepEqual(violations, [], name);
    assert.ok(layout.scrollWidth <= layout.width, `Page horizontal overflow: ${name}`);
    assert.equal(layout.imagesLoaded, true, `Unexpected missing image: ${name}`);
    assert.equal(layout.statusLabel.value, 'failed', `The failed filter must be selected in ${name}.`);
    assert.equal(layout.statusLabel.text, 'Failed or unconfirmed');
    assert.ok(layout.statusLabel.textWidth <= layout.statusLabel.availableWidth,
      `Selected status text clips in ${name}: ${JSON.stringify(layout.statusLabel)}`);
  }

  async function assertPageState(page, data, expectedPage) {
    assert.equal(await page.locator('#email-count').textContent(), `${data.count.toLocaleString()} matching ${data.count === 1 ? 'operation' : 'operations'}`);
    assert.match(await page.locator('#email-page').textContent(), new RegExp(`Page ${expectedPage}(?:\\s|$)`));
    assert.equal(await page.locator('#email-operations-table tbody tr').count(), Math.max(data.items.length, 1));
    assert.equal(await page.locator('#email-operations-table tbody tr').first().locator('td').count(), 5);
  }

  try {
    const { context, page } = await newPage();
    let first = await open(page);
    assert.equal(first.scope, library);
    assert.equal(first.count, 528);
    assert.equal(first.page, 1);
    assert.equal(first.pageSize, 25);
    assert.equal(first.items.length, 25);
    await assertPageState(page, first, 1);
    assert.deepEqual(await page.locator('#email-operations-table thead th').allTextContents(),
      ['Reference', 'Created', 'Status', 'Type / detail', 'Action']);
    assert.equal(first.items[0].id, '9007199254741994');
    assert.equal(first.items[1].id, '9007199254741993');
    assert.equal(await page.locator('#email-operations-table tbody tr').nth(0).locator('td').nth(0).textContent(), '9007199254741994');
    assert.equal(await page.locator('#email-operations-table tbody tr').nth(1).locator('td').nth(0).textContent(), '9007199254741993');
    assert.equal(first.items[0].createdUtc, first.items[1].createdUtc, 'The equal SQL timestamp tie uses a numeric ID order.');
    assert.match(await page.locator('#email-history-status').textContent(), /Some library choices are omitted|^$/);
    assert.match(await page.locator('#email-timezone').textContent(), /America\/New_York.*whole local day/);
    assert.equal(await page.locator('#email-test-scope').inputValue(), otherLibrary,
      'Test settings scope is independently initialized from the history filter.');
    assert.equal(await page.locator('#operations-email-panel button').filter({ hasText: /Refresh history/i }).count(), 0,
      'The main Operations Refresh remains the only explicit refresh control.');
    assert.equal(await page.locator('#email-reference-context').isHidden(), true);

    let observedIds = first.items.map(item => item.id);
    const second = await nextPage(page, 2);
    assert.equal(second.count, 528);
    observedIds.push(...second.items.map(item => item.id));
    let middle;
    for (let expected = 3; expected <= 11; expected++) {
      middle = await nextPage(page, expected);
      observedIds.push(...middle.items.map(item => item.id));
    }
    assert.equal(middle.page, 11, 'A middle page remains directly reachable through the pager.');
    assert.equal(middle.items.length, 25);
    let last;
    for (let expected = 12; expected <= 22; expected++) {
      last = await nextPage(page, expected);
      observedIds.push(...last.items.map(item => item.id));
    }
    assert.equal(last.count, 528);
    assert.equal(last.page, 22);
    assert.equal(last.items.length, 3);
    assert.equal(await page.locator('#email-prev').isDisabled(), false);
    assert.equal(await page.locator('#email-next').isDisabled(), true);
    assert.match(await page.locator('#email-page').textContent(), /Showing 526–528 of 528 · Page 22/);
    assert.equal(new Set(observedIds).size, 528, 'Every full-history row appears once across the bounded pages.');

    const clamped = await open(page, { scope: library, page: 999 });
    assert.equal(clamped.page, 22, 'Out-of-range route pages clamp to the final real page.');
    assert.equal(new URL(page.url()).searchParams.get('emailPage'), '22', 'The route records the accepted page after clamping.');
    assert.equal(await page.locator('#email-next').isDisabled(), true);

    let read = await readAfter(page, () => page.locator('#email-sort').selectOption('oldest'),
      url => url.searchParams.get('sort') === 'oldest' && url.searchParams.get('page') === '1');
    const tiedIndex = read.data.items.findIndex((item, index) => index > 0 && item.createdUtc === read.data.items[index - 1].createdUtc);
    assert.ok(tiedIndex > 0, 'Oldest-first page includes a deterministic equal-timestamp pair.');
    assert.ok(BigInt(read.data.items[tiedIndex - 1].id) < BigInt(read.data.items[tiedIndex].id));
    assert.equal(await page.locator('#email-page').textContent(), 'Showing 1–25 of 528 · Page 1');

    read = await readAfter(page, () => page.locator('#email-status').selectOption('failed'),
      url => url.searchParams.get('status') === 'failed' && url.searchParams.get('page') === '1');
    assert.equal(read.data.count, 105);
    assert.ok(read.data.items.every(item => item.status === 'failed'));
    assert.equal(await page.locator('#email-operations-table tbody tr').first().locator('td').nth(2).textContent(), 'Failed');
    read = await readAfter(page, () => page.locator('#email-type').selectOption('business_event'),
      url => url.searchParams.get('status') === 'failed' && url.searchParams.get('type') === 'business_event');
    assert.ok(read.data.count > 0);
    assert.ok(read.data.items.every(item => item.status === 'failed' && item.deliveryClass === 'business_event'));
    read = await readAfter(page, () => page.locator('#email-clear').click(), url =>
      url.searchParams.get('scope') === 'all' && !url.searchParams.has('status') && !url.searchParams.has('type'));
    assert.equal(read.data.count, 596, 'Clear filters restores the authorized all-libraries-and-system scope.');
    assert.equal(await page.locator('#email-status').inputValue(), '');
    assert.equal(await page.locator('#email-type').inputValue(), '');
    assert.equal(await page.locator('#email-sort').inputValue(), 'newest');
    assert.equal(await page.locator('#email-view-scope').inputValue(), 'all');

    read = await readAfter(page, () => page.locator('#email-view-scope').selectOption('system'),
      url => url.searchParams.get('scope') === 'system');
    assert.equal(read.data.count, 8);
    assert.ok(read.data.items.every(item => item.organizationId === 1));
    read = await readAfter(page, () => page.locator('#email-view-scope').selectOption(library),
      url => url.searchParams.get('scope') === library);
    assert.equal(read.data.count, 528);
    read = await readAfter(page, () => page.locator('#email-view-scope').selectOption('all'),
      url => url.searchParams.get('scope') === 'all');
    assert.equal(read.data.count, 596);
    await page.locator('#email-test-scope').selectOption('1');
    assert.equal(await page.locator('#email-test-scope').inputValue(), '1');
    await readAfter(page, () => page.locator('#email-view-scope').selectOption('system'), url => url.searchParams.get('scope') === 'system');
    assert.equal(await page.locator('#email-test-scope').inputValue(), '1',
      'Changing history scope never rebinds the test-email settings scope.');

    await readAfter(page, () => page.locator('#email-view-scope').selectOption(library), url => url.searchParams.get('scope') === library);
    read = await readAfter(page, () => page.locator('#email-search').fill('business_event'),
      url => url.searchParams.get('search') === 'business_event');
    assert.equal(read.data.count, 176);
    assert.ok(read.data.items.every(item => item.typeLabel === 'Business event'));
    read = await readAfter(page, () => page.locator('#email-search').fill('pre_send_cancelled'),
      url => url.searchParams.get('search') === 'pre_send_cancelled');
    assert.equal(read.data.count, 1);
    assert.equal(read.data.items[0].status, 'pending');
    assert.equal(read.data.items[0].canRetry, false);
    assert.match(await page.locator('#email-operations-table').textContent(), /Queued.*Pre-send cancelled; send not started/);
    assert.equal(await page.locator('#email-operations-table button').count(), 0);
    read = await readAfter(page, () => page.locator('#email-search').fill('send not started'),
      url => url.searchParams.get('search') === 'send not started');
    assert.equal(read.data.count, 1, 'The safe pre-send cancellation label is searchable over full history.');
    read = await readAfter(page, () => page.locator('#email-search').fill('Recipient domain not allowed'),
      url => url.searchParams.get('search') === 'Recipient domain not allowed');
    assert.ok(read.data.count > 0);
    assert.ok(read.data.items.every(item => item.detailLabel === 'Recipient domain not allowed'));
    await readAfter(page, () => page.locator('#email-search').fill('hidden-subject'),
      url => url.searchParams.get('search') === 'hidden-subject');
    assert.match(await page.locator('#email-count').textContent(), /^0 matching operations$/,
      'Subjects and message payloads are not searchable.');
    assert.match(await page.locator('#email-operations-table').textContent(), /No email operations match these filters/);
    assert.equal(await page.locator('#email-operations-table img').count(), 0);

    read = await readAfter(page, () => page.locator('#email-clear').click(),
      url => url.searchParams.get('scope') === 'all' && !url.searchParams.has('search'));
    assert.equal(read.data.count, 596);
    read = await readAfter(page, () => page.locator('#email-view-scope').selectOption(library), url => url.searchParams.get('scope') === library);
    read = await readAfter(page, () => page.locator('#email-search').fill('9007199254741993'),
      url => url.searchParams.get('search') === '9007199254741993');
    assert.equal(read.data.count, 1);
    assert.equal(read.data.items[0].id, '9007199254741993', 'Reference search preserves the exact value above JavaScript safe-integer range.');
    assert.deepEqual((await page.locator('#email-operations-table tbody tr').first().locator('td').allTextContents()).slice(0, 2),
      ['9007199254741993', new Date(read.data.items[0].createdUtc).toLocaleString('en-US', { timeZone: 'America/New_York' })]);
    await open(page, { scope: library, reference: '9007199254741993' });
    assert.equal(await page.locator('#email-reference-context').isVisible(), true);
    assert.match(await page.locator('#email-reference-context').textContent(), /Showing exact email operation 9007199254741993/);
    assert.deepEqual(await page.locator('#email-operations-table tbody tr').first().locator('td').allTextContents()
      .then(cells => cells.slice(0, 2)), ['9007199254741993', new Date(read.data.items[0].createdUtc).toLocaleString('en-US', { timeZone: 'America/New_York' })]);
    read = await readAfter(page, () => page.locator('#email-reference-clear').click(), url => !url.searchParams.has('reference'));
    assert.equal(read.data.count, 528);

    await readAfter(page, () => page.locator('#email-clear').click(), url => url.searchParams.get('scope') === 'all');
    read = await readAfter(page, () => page.locator('#email-view-scope').selectOption(library), url => url.searchParams.get('scope') === library);
    await readAfter(page, () => page.locator('#email-from').fill('2026-03-08'), url =>
      url.searchParams.get('fromUtc') === '2026-03-08T05:00:00.000Z');
    read = await readAfter(page, () => page.locator('#email-through').fill('2026-03-08'), url =>
      url.searchParams.get('fromUtc') === '2026-03-08T05:00:00.000Z' && url.searchParams.get('beforeUtc') === '2026-03-09T04:00:00.000Z');
    assert.equal(read.data.count, 2, 'The spring DST local day spans 23 hours.');
    read = await readAfter(page, () => page.locator('#email-clear').click(), url => url.searchParams.get('scope') === 'all');
    await readAfter(page, () => page.locator('#email-view-scope').selectOption(library), url => url.searchParams.get('scope') === library);
    await readAfter(page, () => page.locator('#email-from').fill('2026-11-01'), url =>
      url.searchParams.get('fromUtc') === '2026-11-01T04:00:00.000Z');
    read = await readAfter(page, () => page.locator('#email-through').fill('2026-11-01'), url =>
      url.searchParams.get('fromUtc') === '2026-11-01T04:00:00.000Z' && url.searchParams.get('beforeUtc') === '2026-11-02T05:00:00.000Z');
    assert.equal(read.data.count, 2, 'The fall DST local day spans 25 hours.');
    assert.ok(read.data.items.every(item => item.createdUtc >= '2026-11-01T04:00:00.000Z' && item.createdUtc < '2026-11-02T05:00:00.000Z'));

    read = await readAfter(page, () => page.locator('#email-clear').click(), url => url.searchParams.get('scope') === 'all');
    assert.equal(read.data.count, 596);
    let began;
    await readAfter(page, () => page.locator('#email-view-scope').selectOption(library), url =>
      url.searchParams.get('scope') === library && url.searchParams.get('page') === '1');
    const heldStarted = new Promise(resolve => { began = resolve; });
    const held = createGate();
    await page.route('**/api/asap/staff/email-operations?*', async route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.searchParams.get('scope') !== library ||
          url.searchParams.get('page') !== '2' || url.searchParams.has('search')) return route.continue();
      const response = await route.fetch();
      began();
      await held.promise;
      try { await route.fulfill({ response }); } catch { /* A superseding scope/session retires this response. */ }
    });
    await page.locator('#email-next').focus();
    await page.locator('#email-next').press('Enter');
    await waitForGate(heldStarted, 'Keyboard page');
    await page.locator('#email-history-status').filter({ hasText: 'Loading email history' }).waitFor();
    assert.equal(await page.locator('#email-operations-table').getAttribute('aria-busy'), 'true');
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'email-operations-table',
      'Keyboard paging moves focus to the stable results region while rows are replaced.');
    held.release();
    await page.locator('#email-page').filter({ hasText: 'Page 2' }).waitFor();
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'email-next',
      'Successful keyboard paging restores focus when the user has not moved it.');
    await page.unroute('**/api/asap/staff/email-operations?*');
    report.focus.push({ scenario: 'pager-loading-success', focusedDuring: 'email-operations-table', focusedAfter: 'email-next' });

    let failOnce = true;
    await page.route('**/api/asap/staff/email-operations?*', async route => {
      const url = new URL(route.request().url());
      if (failOnce && route.request().method() === 'GET' && url.searchParams.get('search') === 'i459-forced-read-failure') {
        failOnce = false;
        const real = await route.fetch();
        report.races.failureBackedByRealRead = real.status() === 200;
        return route.fulfill({ response: real, status: 503, body: JSON.stringify({ message: 'Synthetic history read failure.' }) });
      }
      return route.continue();
    });
    const failedResponse = page.waitForResponse(response => listResponse(response) &&
      new URL(response.url()).searchParams.get('search') === 'i459-forced-read-failure');
    await page.locator('#email-search').fill('i459-forced-read-failure');
    assert.equal((await failedResponse).status(), 503);
    await page.locator('#email-history-status').filter({ hasText: 'Synthetic history read failure.' }).waitFor();
    assert.match(await page.locator('#email-operations-table').textContent(), /Email history is unavailable/);
    assert.equal(await page.locator('#email-history-status').getAttribute('class').then(value => value.includes('error')), true);
    await page.unroute('**/api/asap/staff/email-operations?*');
    const retryHistory = page.waitForResponse(response => listResponse(response) &&
      new URL(response.url()).searchParams.get('search') === 'i459-forced-read-failure' && response.status() === 200);
    const retryQueues = page.waitForResponse(response => response.request().method() === 'GET' &&
      new URL(response.url()).pathname === '/api/asap/staff/workflow/queues');
    await page.locator('#refresh-operations').click();
    assert.equal((await retryHistory).status(), 200);
    await retryQueues;
    await waitSettled(page);
    assert.equal(await page.locator('#email-history-status').getAttribute('class').then(value => value.includes('error')), false,
      'The single main Refresh action retries and clears the history error.');
    report.races.failureRetryViaMainRefresh = true;

    let oldStarted;
    const oldStartedPromise = new Promise(resolve => { oldStarted = resolve; });
    const delayedScope = createGate();
    await page.route('**/api/asap/staff/email-operations?*', async route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.searchParams.get('scope') !== library ||
          url.searchParams.get('search') !== 'business_event') return route.continue();
      const response = await route.fetch();
      oldStarted();
      await delayedScope.promise;
      try { await route.fulfill({ response }); } catch { /* The new scope owns the visible result. */ }
    });
    await page.locator('#email-search').fill('business_event');
    await waitForGate(oldStartedPromise, 'Delayed scope');
    const systemRead = page.waitForResponse(response => listResponse(response) &&
      new URL(response.url()).searchParams.get('scope') === 'system' &&
      new URL(response.url()).searchParams.get('search') === 'business_event');
    await page.locator('#email-view-scope').selectOption('system');
    const systemResponse = await systemRead;
    const systemData = await systemResponse.json();
    assert.equal(systemData.count, 3);
    delayedScope.release();
    await page.waitForTimeout(80);
    assert.equal(await page.locator('#email-view-scope').inputValue(), 'system');
    assert.equal(await page.locator('#email-count').textContent(), '3 matching operations',
      'A delayed real library response cannot overwrite the newer System filter.');
    assert.ok((await page.locator('#email-operations-table tbody tr').allTextContents()).every(text => !text.includes(library)));
    await page.unroute('**/api/asap/staff/email-operations?*');
    report.races.delayedScopeResponseRetired = true;

    await page.goto(historyUrl({ scope: 'all' }), { waitUntil: 'networkidle' });
    await waitSettled(page);
    const staleStarted = new Promise(resolve => { oldStarted = resolve; });
    const delayedIdentity = createGate();
    await page.route('**/api/asap/staff/email-operations?*', async route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.searchParams.get('scope') !== 'all' ||
          url.searchParams.get('search') !== 'Recipient domain not allowed') return route.continue();
      const response = await route.fetch();
      oldStarted();
      await delayedIdentity.promise;
      try { await route.fulfill({ response }); } catch { /* Session invalidation may abort the old identity's read. */ }
    });
    await page.locator('#email-search').fill('Recipient domain not allowed');
    await waitForGate(staleStarted, 'Delayed identity');
    await context.setExtraHTTPHeaders(requestHeaders(libraryActor));
    await page.locator('#email-sort').selectOption('oldest');
    await page.waitForFunction(() => !document.querySelector('#operations-view') ||
      document.querySelector('#operations-view').hidden ||
      document.querySelector('#staff-session-error')?.textContent?.length > 0, null, { timeout: 10000 }).catch(() => {});
    delayedIdentity.release();
    await page.waitForTimeout(100);
    const oldIdentityText = await page.locator('body').textContent();
    assert.doesNotMatch(oldIdentityText, /Recipient domain not allowed/,
      'A response captured under the previous session never remains visible after identity replacement.');
    assert.equal(await page.locator('#email-count').textContent().catch(() => ''), '');
    await page.unroute('**/api/asap/staff/email-operations?*');
    report.races.delayedIdentityResponseRetired = true;
    await context.close();

    const { page: adminPage } = await newPage({ actor: libraryActor });
    const adminData = await open(adminPage, { scope: library, testScope: library });
    assert.equal(adminData.count, 528);
    assert.equal(await adminPage.locator('#email-view-scope').isDisabled(), true);
    assert.equal(await adminPage.locator('#email-view-scope option').count(), 1);
    assert.equal(await adminPage.locator('#email-view-scope').inputValue(), library,
      'A library administrator is bound to the authorized library scope.');
    const forbiddenScopes = await adminPage.evaluate(async scopes => Promise.all(scopes.map(async scope => {
      const response = await fetch(`/api/asap/staff/email-operations?scope=${encodeURIComponent(scope)}`);
      const body = await response.json();
      return { scope, status: response.status, body };
    })), ['all', 'system']);
    assert.deepEqual(forbiddenScopes, [
      { scope: 'all', status: 403, body: { code: 'staff_scope_forbidden' } },
      { scope: 'system', status: 403, body: { code: 'staff_scope_forbidden' } }
    ], 'Direct API requests cannot expand a library administrator beyond their own scope.');
    report.races.directScopeDenied = true;
    await readAfter(adminPage, () => adminPage.locator('#email-status').selectOption('failed'),
      url => url.searchParams.get('status') === 'failed');
    await scan(adminPage, 'email-history-light-desktop');
    await adminPage.emulateMedia({ colorScheme: 'dark' });
    await scan(adminPage, 'email-history-dark-desktop');
    await adminPage.setViewportSize({ width: 390, height: 844 });
    await adminPage.emulateMedia({ colorScheme: 'light' });
    await scan(adminPage, 'email-history-light-mobile');
    await adminPage.emulateMedia({ colorScheme: 'dark' });
    await scan(adminPage, 'email-history-dark-mobile');

    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'native-zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null,
      extraHTTPHeaders: requestHeaders(libraryActor),
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900']
    });
    contexts.push(zoomContext);
    await zoomContext.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++;
      return route.abort('blockedbyclient');
    });
    const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
    zoomContext.setDefaultTimeout(15000);
    zoomContext.setDefaultNavigationTimeout(20000);
    zoomPage.on('pageerror', error => report.pageErrors.push(error.message));
    rejectNativeDialogs(zoomPage, report.pageErrors);
    zoomPage.on('request', request => { if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) report.writes++; });
    zoomPage.on('response', response => {
      if (!listResponse(response)) return;
      const url = new URL(response.url());
      report.emailReads.push({ scope: url.searchParams.get('scope'), search: url.searchParams.get('search'),
        status: url.searchParams.get('status'), type: url.searchParams.get('type'), fromUtc: url.searchParams.get('fromUtc'),
        beforeUtc: url.searchParams.get('beforeUtc'), sort: url.searchParams.get('sort'), page: url.searchParams.get('page'),
        statusCode: response.status() });
    });
    await zoomPage.goto(historyUrl({ scope: library, testScope: library, status: 'failed' }), { waitUntil: 'networkidle' });
    await waitSettled(zoomPage);
    const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker', { timeout: 10000 });
    const initialRatio = await zoomPage.evaluate(() => devicePixelRatio);
    const zoom = await worker.evaluate(async appOrigin => {
      const tab = (await chrome.tabs.query({})).find(item => item.url.startsWith(appOrigin));
      await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
      await chrome.tabs.setZoom(tab.id, 2);
      return chrome.tabs.getZoom(tab.id);
    }, origin);
    assert.equal(zoom, 2);
    await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, initialRatio);
    const zoomSession = await zoomContext.newCDPSession(zoomPage);
    const zoomMetrics = await zoomSession.send('Page.getLayoutMetrics');
    assert.equal(zoomMetrics.visualViewport.zoom, 2);
    await zoomPage.locator('#email-filters').scrollIntoViewIfNeeded();
    await scan(zoomPage, 'email-history-native-200', { nativeViewport: true });
    const zoomGeometry = await zoomPage.evaluate(() => ({
      viewport: document.documentElement.clientWidth, scrollWidth: document.documentElement.scrollWidth,
      tableClientWidth: document.querySelector('#email-operations-table').clientWidth,
      tableScrollWidth: document.querySelector('#email-operations-table').scrollWidth,
      searchBounds: (() => { const bounds = document.querySelector('#email-search').getBoundingClientRect(); return { left: bounds.left, right: bounds.right }; })()
    }));
    assert.ok(zoomGeometry.scrollWidth <= zoomGeometry.viewport, 'Native 200% zoom has no page-level horizontal overflow.');
    assert.ok(zoomGeometry.searchBounds.left >= 0 && zoomGeometry.searchBounds.right <= zoomGeometry.viewport,
      'The focused history controls remain in the visual viewport at native 200%.');
    report.nativeZoom = { zoom, cdpZoom: zoomMetrics.visualViewport.zoom, geometry: zoomGeometry };
    await fs.writeFile(path.join(artifacts, 'native-200-layout.json'), JSON.stringify(zoomGeometry, null, 2));
    await zoomSession.detach();

    assert.equal(report.externalRequests, 0);
    assert.deepEqual(report.pageErrors, []);
    assert.equal(report.writes, 0, 'Browsing only issues read requests; there are no HTTP writes.');
    assert.ok(report.emailReads.some(item => item.scope === 'system'));
    assert.ok(report.emailReads.some(item => item.scope === 'all'));
    assert.ok(report.emailReads.some(item => item.fromUtc === '2026-03-08T05:00:00.000Z' && item.beforeUtc === '2026-03-09T04:00:00.000Z'));
    assert.ok(report.emailReads.some(item => item.fromUtc === '2026-11-01T04:00:00.000Z' && item.beforeUtc === '2026-11-02T05:00:00.000Z'));
    console.log('Email history real SQL paging, scoped filters, local DST, read-only browsing, stale-response, keyboard and layout checks passed.');
  } finally {
    for (const release of [...pendingGateReleases]) release();
    await Promise.resolve();
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
