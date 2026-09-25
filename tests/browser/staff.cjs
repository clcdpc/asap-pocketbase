'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { URL } = require('node:url');

function parseArguments(argv) {
  if (argv.length !== 25) {
    throw new Error('Usage: node tests/browser/staff.cjs <baseURL> <artifactDirectory> <superId> <tenantId> <superEmail> <staffId> <staffEmail> <legacyRequestId> <primaryRequestId> <blockedRequestId> <resolutionRequestId> <otherRequestId> <copySourceRequestId> <invalidClosedCopyId> <invalidClaimantId> <legacyRuleId> <mobileCopyId> <foreignStaffId> <invalidTenantStaffId> <unboundStaffId> <staleTitleAId> <staleTitleBId> <staleCopyAId> <staleCopyBId> <staleCreateSourceId>');
  }
  const parsed = new URL(argv[0]);
  if (!['http:', 'https:'].includes(parsed.protocol) || parsed.pathname !== '/' ||
      parsed.username || parsed.password || parsed.search || parsed.hash) {
    throw new Error('baseURL must be an absolute origin without a path or credentials.');
  }
  return {
    baseOrigin: parsed.origin,
    artifactRoot: path.resolve(argv[1]),
    superIdentity: { staffId: argv[2], tenantId: argv[3], email: argv[4] },
    staffIdentity: { staffId: argv[5], tenantId: argv[3], email: argv[6] },
    legacyRequestId: argv[7],
    primaryRequestId: argv[8],
    blockedRequestId: argv[9],
    resolutionRequestId: argv[10],
    otherRequestId: argv[11],
    copySourceRequestId: argv[12],
    invalidClosedCopyId: argv[13],
    invalidClaimantId: argv[14],
    legacyRuleId: argv[15],
    mobileCopyId: argv[16],
    foreignStaffId: argv[17],
    invalidTenantStaffId: argv[18],
    unboundStaffId: argv[19],
    staleTitleAId: argv[20],
    staleTitleBId: argv[21],
    staleCopyAId: argv[22],
    staleCopyBId: argv[23],
    staleCreateSourceId: argv[24]
  };
}

function sameOrigin(url, origin) {
  try { return new URL(url).origin === origin; } catch { return false; }
}

async function createContext(browser, viewport, baseOrigin, identity) {
  const context = await browser.newContext({
    viewport,
    extraHTTPHeaders: identity ? {
      'X-ASAP-Test-Staff-Id': identity.staffId,
      'X-ASAP-Test-Tenant-Id': identity.tenantId,
      'X-ASAP-Test-Staff-Email': identity.email
    } : undefined
  });
  const traffic = { externalRequests: 0 };
  await context.route('**/*', route => {
    if (sameOrigin(route.request().url(), baseOrigin)) return route.continue();
    traffic.externalRequests += 1;
    return route.abort('blockedbyclient');
  });
  return { context, traffic };
}

async function scan(page, axeSource, artifactRoot, report, viewport, state) {
  await page.evaluate(axeSource);
  const accessibility = await page.evaluate(async () => {
    const result = await axe.run();
    return result.violations.filter(item => ['serious', 'critical'].includes(item.impact));
  });
  const layout = await page.evaluate(() => ({
    width: innerWidth,
    scrollWidth: document.documentElement.scrollWidth,
    visibleImagesLoaded: [...document.images]
      .filter(image => {
        const bounds = image.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0;
      })
      .every(image => image.complete && image.naturalWidth > 0)
  }));
  await page.screenshot({ path: path.join(artifactRoot, `${viewport}-${state}.png`), fullPage: true });
  report.states.push({ viewport, state, accessibility, layout });
  assert.deepEqual(accessibility, [], `Serious or critical accessibility violation in ${viewport}/${state}`);
  assert.ok(layout.scrollWidth <= layout.width, `Horizontal document overflow in ${viewport}/${state}`);
  assert.equal(layout.visibleImagesLoaded, true, `Visible image failed in ${viewport}/${state}`);
}

async function assertReadableMobileQueue(page, selector = '#request-grid') {
  const geometry = await page.locator(selector).evaluate(root => {
    const wrapper = root.querySelector('.gridjs-wrapper');
    const table = root.querySelector('table.gridjs-table');
    const row = root.querySelector('tbody tr');
    const cells = row ? [...row.querySelectorAll('td')] : [];
    const bounds = cells.map(cell => cell.getBoundingClientRect());
    return {
      wrapperClientWidth: wrapper ? wrapper.clientWidth : 0,
      wrapperScrollWidth: wrapper ? wrapper.scrollWidth : 0,
      wrapperRight: wrapper ? wrapper.getBoundingClientRect().right : 0,
      tableWidth: table ? table.getBoundingClientRect().width : 0,
      viewportWidth: innerWidth,
      cellCount: cells.length,
      overlaps: bounds.slice(1).some((current, index) => current.left < bounds[index].right - 1),
      nonPositiveCells: bounds.some(bounds => bounds.width <= 0)
    };
  });
  assert.equal(geometry.cellCount, 7, 'Mobile queue should render all request columns');
  assert.equal(geometry.overlaps, false, 'Mobile queue cells must not overlap neighboring columns');
  assert.equal(geometry.nonPositiveCells, false, 'Mobile queue cells need stable positive widths');
  assert.ok(geometry.wrapperScrollWidth > geometry.wrapperClientWidth, 'Mobile queue should scroll inside its wrapper');
  assert.ok(geometry.tableWidth > geometry.viewportWidth, 'Mobile queue should retain readable column widths');
  assert.ok(geometry.wrapperRight <= geometry.viewportWidth + 1, 'Queue wrapper must stay inside the viewport');
}

async function session(context, baseOrigin) {
  const response = await context.request.get(`${baseOrigin}/api/asap/staff/session`);
  assert.equal(response.status(), 200, 'Could not load the staff session contract');
  return response.json();
}

async function mutate(context, baseOrigin, route, data) {
  const current = await session(context, baseOrigin);
  assert.equal(current.authenticated, true, 'Mutation identity was not authenticated');
  return context.request.post(`${baseOrigin}${route}`, {
    headers: { 'X-ASAP-Antiforgery': current.antiforgeryToken },
    data
  });
}

async function mutateDelete(context, baseOrigin, route, data) {
  const current = await session(context, baseOrigin);
  assert.equal(current.authenticated, true, 'Mutation identity was not authenticated');
  return context.request.delete(`${baseOrigin}${route}`, {
    headers: { 'X-ASAP-Antiforgery': current.antiforgeryToken },
    data
  });
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((settle, fail) => {
    resolve = settle;
    reject = fail;
  });
  return { promise, resolve, reject };
}

async function delayNextCandidateResponse(page, payload) {
  const pattern = '**/api/asap/staff/assignment-candidates?*';
  const requested = deferred();
  const release = deferred();
  const completed = deferred();
  let intercepted = false;
  const handler = async route => {
    if (intercepted) {
      await route.continue();
      return;
    }
    intercepted = true;
    requested.resolve();
    await release.promise;
    try {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(payload)
      });
    } finally {
      completed.resolve();
    }
  };
  await page.route(pattern, handler);
  return {
    requested: requested.promise,
    completed: completed.promise,
    release: release.resolve,
    dispose: () => page.unroute(pattern, handler)
  };
}

async function delayNextServerResponse(page, method, pathname) {
  const accepted = deferred();
  const release = deferred();
  const completed = deferred();
  let intercepted = false;
  const handler = async route => {
    const request = route.request();
    if (intercepted || request.method() !== method || new URL(request.url()).pathname !== pathname) {
      await route.continue();
      return;
    }
    intercepted = true;
    try {
      const response = await route.fetch();
      const body = await response.body();
      let json = null;
      try { json = JSON.parse(body.toString('utf8')); } catch { /* Not every mutation returns JSON. */ }
      accepted.resolve({ status: response.status(), json });
      await release.promise;
      await route.fulfill({ status: response.status(), headers: response.headers(), body });
      completed.resolve();
    } catch (error) {
      accepted.reject(error);
      completed.reject(error);
      throw error;
    }
  };
  await page.route('**/*', handler);
  return {
    accepted: accepted.promise,
    completed: completed.promise,
    release: release.resolve,
    dispose: () => page.unroute('**/*', handler)
  };
}

async function delayRecoveryErrorResponses(page) {
  const firstRequested = deferred();
  const firstRelease = deferred();
  const firstCompleted = deferred();
  const secondCompleted = deferred();
  let matchingRequests = 0;
  const handler = async route => {
    const request = route.request();
    if (request.method() !== 'POST' ||
        !/\/api\/asap\/staff\/hold-operations\/\d+\/resolve$/.test(new URL(request.url()).pathname)) {
      await route.continue();
      return;
    }
    matchingRequests += 1;
    if (matchingRequests > 2) {
      await route.continue();
      return;
    }
    const first = matchingRequests === 1;
    if (first) {
      firstRequested.resolve();
      await firstRelease.promise;
    }
    try {
      await route.fulfill({
        status: 502,
        contentType: 'application/json',
        body: JSON.stringify({
          code: 'hold_provider_error',
          message: first ? 'Older hold recovery failed.' : 'Current hold recovery failed.'
        })
      });
      (first ? firstCompleted : secondCompleted).resolve();
    } catch (error) {
      (first ? firstCompleted : secondCompleted).reject(error);
      throw error;
    }
  };
  await page.route('**/*', handler);
  return {
    firstRequested: firstRequested.promise,
    firstCompleted: firstCompleted.promise,
    secondCompleted: secondCompleted.promise,
    releaseFirst: firstRelease.resolve,
    dispose: () => page.unroute('**/*', handler)
  };
}

async function runAnonymous(browser, args, axeSource, report) {
  for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const { context, traffic } = await createContext(browser, viewport, args.baseOrigin, null);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      await page.goto(`${args.baseOrigin}/staff/`, { waitUntil: 'networkidle' });
      await page.locator('#signed-out').waitFor({ state: 'visible' });
      assert.equal(await page.locator('#workspace').isVisible(), false);
      assert.equal(
        await page.locator('.sign-in').getAttribute('href'),
        '/api/asap/staff/sign-in?returnUrl=%2Fstaff%2F'
      );
      await scan(page, axeSource, args.artifactRoot, report, name, 'anonymous-sign-in');
      assert.deepEqual(errors, [], `${name} anonymous shell raised a browser error`);
      assert.equal(traffic.externalRequests, 0, `${name} anonymous shell requested an external asset`);
    } finally {
      await context.close();
    }
  }
}

async function runSuperAdmin(browser, args, axeSource, report) {
  assert.equal(args.primaryRequestId, '9007199254740993');
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  const bibLookupBodies = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/bib-lookup') {
      bibLookupBodies.push(JSON.parse(request.postData() || '{}'));
    }
  });
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${encodeURIComponent(args.legacyRequestId)}`, { waitUntil: 'networkidle' });
    await page.locator('#workspace').waitFor({ state: 'visible' });
    await page.locator('#request-dialog[open]').waitFor();
    assert.deepEqual(errors, [], `Request detail raised a browser error: ${errors.join('; ')}`);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'close-request');
    assert.match(page.url(), new RegExp(`[?&]request=${args.primaryRequestId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
    assert.equal(await page.locator('#library-scope').inputValue(), 'all');
    const format = page.getByLabel('Format', { exact: true });
    const publication = page.getByLabel('Publication timing', { exact: true });
    assert.equal(await format.evaluate(node => node.tagName), 'SELECT');
    assert.deepEqual(
      await format.locator('option').allTextContents(),
      ['Book', 'Audiobook (Physical CD)', 'DVD', 'Music CD', 'eBook', 'eAudiobook']
    );
    assert.equal(await format.inputValue(), 'book');
    assert.equal(await publication.evaluate(node => node.tagName), 'SELECT');
    assert.deepEqual(
      await publication.locator('option').evaluateAll(options => options.map(option => option.value)),
      ['Library early release', 'Library backlist', 'Historical browser publication']
    );
    assert.equal(await publication.inputValue(), 'Historical browser publication');
    assert.equal(await page.getByLabel('Audience note', { exact: true }).inputValue(), 'Historic audience');
    const binding = page.getByLabel('Binding *', { exact: true });
    assert.equal(await binding.evaluate(node => node.tagName), 'SELECT');
    assert.equal(await binding.inputValue(), 'historic');
    assert.deepEqual(
      await binding.locator('option').evaluateAll(options => options.map(option => option.value)),
      ['', 'hardback', 'paperback', 'historic']
    );
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'deep-link-detail');

    const storedBibPage = await context.newPage();
    const storedBibErrors = [];
    let storedBibPurchasePosts = 0;
    storedBibPage.on('pageerror', error => storedBibErrors.push(error.message));
    storedBibPage.on('request', request => {
      if (request.method() === 'POST' && request.url().endsWith(`/title-requests/${args.primaryRequestId}/action`)) {
        storedBibPurchasePosts += 1;
      }
    });
    await storedBibPage.route(`**/api/asap/staff/title-requests/${args.primaryRequestId}`, async route => {
      const response = await route.fetch();
      const request = await response.json();
      await route.fulfill({ response, json: { ...request, bibid: '9001',
        capabilities: { ...request.capabilities, canChangeBib: false } } });
    });
    await storedBibPage.goto(`${args.baseOrigin}/staff/?request=${args.primaryRequestId}`, { waitUntil: 'networkidle' });
    await storedBibPage.locator('#request-dialog[open]').waitFor();
    await storedBibPage.getByRole('button', { name: 'Purchase', exact: true }).click();
    await storedBibPage.getByText(/Search Polaris, select the matching BIB/).waitFor();
    assert.equal(storedBibPurchasePosts, 0, 'Purchase bypassed the verified-BIB gate');
    await storedBibPage.getByRole('button', { name: 'Search Polaris catalog' }).click();
    await storedBibPage.locator('#polaris-mode').selectOption('bib');
    await storedBibPage.locator('#polaris-query').fill('9001');
    await storedBibPage.locator('#polaris-form button[type=submit]').click();
    const verifyLockedBib = storedBibPage.getByRole('button', { name: 'Use BIB 9001' });
    await verifyLockedBib.waitFor();
    assert.equal(await verifyLockedBib.isEnabled(), true, 'An unchanged locked BIB could not be verified');
    await verifyLockedBib.click();
    await storedBibPage.getByText(/Polaris BIB 9001 verified for this request/).waitFor();
    assert.deepEqual(storedBibErrors, []);
    await storedBibPage.close();

    await page.getByLabel('Title', { exact: true }).fill('Browser staff title edited');
    await publication.selectOption('Library backlist');
    await page.getByLabel('Audience note', { exact: true }).fill('Edited audience');
    await binding.selectOption('hardback');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await page.getByText('Request changes saved.').waitFor();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();

    const currentRequest = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.primaryRequestId}`
    );
    assert.equal(currentRequest.status(), 200);
    const request = await currentRequest.json();
    const firstUnclaim = await mutate(
      context,
      args.baseOrigin,
      `/api/asap/staff/title-requests/${args.primaryRequestId}/unclaim`,
      { version: request.version }
    );
    assert.equal(firstUnclaim.status(), 200, await firstUnclaim.text());
    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    assert.match(await page.locator('#app-status').textContent(), /changed|blocked|reload/i);

    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();
    await page.getByRole('button', { name: 'Purchase', exact: true }).click();
    await page.getByText('Outstanding purchase', { exact: true }).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'edited-claimed-actioned');

    await page.getByRole('button', { name: 'Close request details' }).click();
    await page.getByRole('button', { name: 'Profile' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'profile-title');
    await page.locator('#weekly-email').fill('browser-weekly@example.org');
    await page.locator('#weekly-enabled').check();
    await page.locator('#mine-default').check();
    await page.getByRole('button', { name: 'Save profile' }).click();
    await page.getByText('Profile saved.').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'profile-saved');

    await page.getByRole('button', { name: 'Requests' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'queue-title');
    const suggestionTab = page.locator('[data-status="suggestion"]');
    await suggestionTab.focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.status), 'outstanding_purchase');
    await page.keyboard.press('Enter');
    await page.locator('#library-scope').selectOption('2');
    await page.getByRole('button', { name: `Open request ${args.primaryRequestId}` }).waitFor();
    assert.equal(await page.getByText('Other browser library title', { exact: true }).count(), 0);
    const open = page.getByRole('button', { name: `Open request ${args.primaryRequestId}` });
    await open.click();
    await page.locator('#request-dialog[open]').waitFor();
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.activeElement?.classList.contains('grid-open'));
    assert.equal(await page.evaluate(() => document.activeElement.classList.contains('grid-open')), true);

    await open.click();
    await page.locator('#request-dialog[open]').waitFor();
    await page.getByRole('link', { name: 'Open patron in LEAP' }).waitFor();
    assert.equal(await page.getByRole('link', { name: 'Open patron in LEAP' }).getAttribute('href'),
      'https://leap.example.test/patron/7001');
    const researchLabels = await page.locator('.research-links a').allTextContents();
    assert.deepEqual(researchLabels, ['Open patron in LEAP', 'Browser vendor', 'Search WorldCat']);
    assert.equal(await page.getByRole('link', { name: 'Search Goodreads' }).count(), 0);
    assert.match(await page.getByRole('link', { name: 'Browser vendor' }).getAttribute('href'),
      /q=Browser%20staff%20title%20edited/);

    await page.getByLabel('BIB ID', { exact: true }).fill('9001');
    await page.getByRole('button', { name: 'Ready for hold' }).click();
    await page.getByText(/Search Polaris, select the matching BIB/).waitFor();
    const searchCatalog = page.getByRole('button', { name: 'Search Polaris catalog' });
    await searchCatalog.focus();
    await page.keyboard.press('Enter');
    await page.locator('#polaris-dialog[open]').waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'polaris-query');
    await page.keyboard.press('Escape');
    await page.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.evaluate(() => document.activeElement.textContent.includes('Search Polaris catalog')), true);

    await searchCatalog.click();
    for (const [mode, value] of [['identifier', '9780000000001'], ['title', 'Catalog title'], ['author', 'Catalog author']]) {
      await page.locator('#polaris-mode').selectOption(mode);
      await page.locator('#polaris-query').fill(value);
      await page.locator('#polaris-form button[type=submit]').click();
      await page.getByRole('button', { name: 'Use BIB 9001' }).waitFor();
    }
    await page.locator('#polaris-mode').selectOption('title_author');
    await page.locator('#polaris-title').fill('Catalog title');
    await page.locator('#polaris-author').fill('Catalog author');
    await page.locator('#polaris-form button[type=submit]').click();
    await page.getByRole('button', { name: 'Use BIB 9001' }).waitFor();
    assert.ok(bibLookupBodies.length > 0, 'Polaris lookup should send a request body');
    for (const body of bibLookupBodies) {
      assert.equal(typeof body.requestId, 'string');
      assert.equal(body.requestId, '9007199254740993');
    }

    const firstSearch = deferred();
    const releaseSearch = deferred();
    const staleSearch = async route => {
      const body = JSON.parse(route.request().postData() || '{}');
      if (body.mode !== 'title' || body.query !== 'Older query') return route.continue();
      firstSearch.resolve();
      await releaseSearch.promise;
      try {
        await route.fulfill({ status: 200, contentType: 'application/json',
          body: JSON.stringify({ status: 'found', totalMatches: 1,
            results: [{ bibId: '9002', title: 'Older catalog result' }] }) });
      } catch {
        // The newer query may have aborted the old browser request.
      }
    };
    await page.route('**/api/asap/staff/bib-lookup', staleSearch);
    await page.locator('#polaris-mode').selectOption('title');
    await page.locator('#polaris-query').fill('Older query');
    await page.locator('#polaris-form button[type=submit]').click();
    await firstSearch.promise;
    await page.locator('#polaris-query').fill('Current query');
    await page.locator('#polaris-form button[type=submit]').click();
    await page.getByRole('button', { name: 'Use BIB 9001' }).waitFor();
    releaseSearch.resolve();
    assert.equal(await page.getByText('Older catalog result').count(), 0);
    await page.unroute('**/api/asap/staff/bib-lookup', staleSearch);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'polaris-search');

    const detailWithoutIdentifier = async route => {
      const body = JSON.parse(route.request().postData() || '{}');
      if (body.mode !== 'bib') return route.continue();
      const response = await route.fetch();
      const detail = await response.json();
      await route.fulfill({ response, json: { ...detail, identifier: null, publication: '2026', format: 'Large Print' } });
    };
    await page.route('**/api/asap/staff/bib-lookup', detailWithoutIdentifier);
    await page.getByRole('button', { name: 'Use BIB 9001' }).click();
    await page.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    await page.unroute('**/api/asap/staff/bib-lookup', detailWithoutIdentifier);
    assert.equal(await page.evaluate(() => document.activeElement.getAttribute('inputmode')), 'numeric');
    assert.equal(await page.getByLabel('Title', { exact: true }).inputValue(),
      'Catalog title 9001 (Browser staff title edited)');
    assert.equal(await page.getByLabel('Author', { exact: true }).inputValue(),
      'Catalog author (Browser Author)');
    assert.equal(await page.getByLabel('Identifier', { exact: true }).inputValue(), '9780000002901');
    assert.equal(await page.getByLabel('Publication timing', { exact: true }).inputValue(), 'Library backlist');
    assert.equal(await page.getByLabel('Format', { exact: true }).inputValue(), 'book');
    const selectedMetadataContext = await page.locator('.polaris-selection-context').textContent();
    assert.match(selectedMetadataContext, /Polaris publication: 2026/);
    assert.match(selectedMetadataContext, /Polaris format: Large Print/);
    await page.getByText(/3 item|1 item\(s\) at this library/).waitFor();
    await page.getByRole('link', { name: 'Open BIB in LEAP' }).waitFor();
    assert.equal(await page.getByRole('link', { name: 'Open BIB in LEAP' }).getAttribute('href'),
      'https://leap.example.test/bib/9001');
    await page.getByRole('button', { name: 'Ready for hold' }).click();
    await page.getByText('Save the current request edits before moving to Pending hold.').waitFor();
    await page.getByLabel('BIB ID', { exact: true }).fill('9002');
    await page.getByLabel('BIB ID', { exact: true }).fill('9001');
    await page.getByRole('button', { name: 'Ready for hold' }).click();
    await page.getByText(/Search Polaris, select the matching BIB/).waitFor();

    await searchCatalog.click();
    await page.locator('#polaris-mode').selectOption('bib');
    await page.locator('#polaris-query').fill('9001');
    await page.locator('#polaris-form button[type=submit]').click();
    await page.getByText(/Publisher: Catalog publisher/).waitFor();
    await page.getByText(/Format: Book/).waitFor();
    await page.getByText(/Identifier: 9780000000001/).waitFor();
    await page.getByRole('button', { name: 'Use BIB 9001' }).click();
    await page.locator('.polaris-selection-context').filter({ hasText: 'Polaris format: Book' }).waitFor();
    assert.equal(await page.getByLabel('Publication timing', { exact: true }).inputValue(), 'Library backlist');
    assert.equal(await page.getByLabel('Format', { exact: true }).inputValue(), 'book');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await page.getByText('Request changes saved.').waitFor();
    const reconciledResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.primaryRequestId}`
    );
    assert.equal(reconciledResponse.status(), 200);
    const reconciledRequest = await reconciledResponse.json();
    assert.equal(reconciledRequest.title, 'Catalog title 9001 (Browser staff title edited)');
    assert.equal(reconciledRequest.author, 'Catalog author (Browser Author)');
    assert.equal(reconciledRequest.identifier, '9780000002901');
    assert.equal(reconciledRequest.publication, 'Library backlist');
    assert.equal(reconciledRequest.format, 'book');
    await page.getByRole('button', { name: 'Ready for hold' }).click();
    await page.locator('#request-dialog .status-badge').filter({ hasText: 'Pending hold' }).waitFor();

    await page.getByRole('button', { name: 'Close request details' }).click();
    const noConfig = async route => route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ externalSearchProviders: [], leapBibUrlPattern: '', leapPatronUrlPattern: '' }) });
    await page.route('**/api/asap/staff/research-configuration?*', noConfig);
    await page.goto(`${args.baseOrigin}/staff/?request=${args.primaryRequestId}`, { waitUntil: 'networkidle' });
    await page.locator('#request-dialog[open]').waitFor();
    assert.equal(await page.locator('.research-links a').count(), 0);
    await page.unroute('**/api/asap/staff/research-configuration?*', noConfig);
    assert.deepEqual(errors, [], `Super-admin browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Super-admin browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runAnalytics(browser, args, axeSource, report) {
  const superContextState = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const superPage = await superContextState.context.newPage();
  const superErrors = [];
  superPage.on('pageerror', error => superErrors.push(error.message));
  try {
    await superPage.goto(`${args.baseOrigin}/staff/?stage=analytics`, { waitUntil: 'networkidle' });
    await superPage.locator('#analytics-container .analytics-shell').waitFor();
    assert.equal(await superPage.evaluate(() => document.activeElement.id), 'analytics-title');
    assert.equal(await superPage.locator('#analytics-scope').inputValue(), 'all');
    assert.equal(await superPage.locator('#analytics-date-range').inputValue(), 'lastMonth');
    await superPage.getByText('Requests by stage', { exact: true }).waitFor();
    await superPage.getByText('New suggestions', { exact: true }).waitFor();

    let invalidScopeInjected = false;
    let allScopeRecoveryRequests = 0;
    await superPage.route('**/api/asap/staff/analytics*', async route => {
      const url = new URL(route.request().url());
      if (!invalidScopeInjected && url.searchParams.get('scope') === '2') {
        invalidScopeInjected = true;
        await route.fulfill({
          status: 400,
          contentType: 'application/json',
          body: JSON.stringify({ code: 'invalid_scope', message: 'The analytics scope is invalid.' })
        });
        return;
      }
      if (url.searchParams.get('scope') === 'all') allScopeRecoveryRequests += 1;
      await route.continue();
    });

    await Promise.all([
      superPage.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' &&
          url.pathname === '/api/asap/staff/analytics' &&
          url.searchParams.get('scope') === '2' &&
          response.status() === 400;
      }),
      superPage.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' &&
          url.pathname === '/api/asap/staff/analytics' &&
          url.searchParams.get('scope') === 'all' &&
          response.status() === 200;
      }),
      superPage.locator('#analytics-scope').selectOption('2')
    ]);
    await superPage.locator('#analytics-container .analytics-shell').waitFor();
    assert.equal(invalidScopeInjected, true);
    assert.equal(allScopeRecoveryRequests, 1, 'Analytics invalid_scope recovery must issue one all-scope request');
    assert.equal(await superPage.locator('#analytics-scope').inputValue(), 'all');
    assert.equal(await superPage.locator('#analytics-scope').isEnabled(), true);
    assert.equal(await superPage.locator('#analytics-date-range').isEnabled(), true);
    assert.equal(await superPage.evaluate(() => document.activeElement.id), 'analytics-title');

    await Promise.all([
      superPage.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' &&
          url.pathname === '/api/asap/staff/analytics' &&
          url.searchParams.get('scope') === 'all' &&
          url.searchParams.get('range') === 'last90';
      }),
      superPage.locator('#analytics-date-range').selectOption('last90')
    ]);
    await superPage.locator('#analytics-container .analytics-shell').waitFor();
    assert.equal(await superPage.locator('#analytics-scope').inputValue(), 'all');
    assert.equal(await superPage.locator('#analytics-date-range').inputValue(), 'last90');
    await scan(superPage, axeSource, args.artifactRoot, report, 'desktop', 'analytics-super-admin');
    assert.deepEqual(superErrors, [], `Super-admin Analytics raised a browser error: ${superErrors.join('; ')}`);
    assert.equal(superContextState.traffic.externalRequests, 0, 'Super-admin Analytics requested an external asset');
  } finally {
    await superContextState.context.close();
  }

  const staffContextState = await createContext(
    browser,
    { width: 390, height: 844 },
    args.baseOrigin,
    args.staffIdentity
  );
  const staffPage = await staffContextState.context.newPage();
  const staffErrors = [];
  staffPage.on('pageerror', error => staffErrors.push(error.message));
  try {
    await staffPage.goto(`${args.baseOrigin}/staff/?stage=analytics`, { waitUntil: 'networkidle' });
    await staffPage.locator('#analytics-container .analytics-shell').waitFor();
    assert.equal(await staffPage.evaluate(() => document.activeElement.id), 'analytics-title');
    assert.equal(await staffPage.locator('#analytics-scope').count(), 0);
    assert.equal(await staffPage.locator('#analytics-date-range').inputValue(), 'lastMonth');
    assert.match(await staffPage.locator('.analytics-range-summary').textContent(), /Test Library/);
    await Promise.all([
      staffPage.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET' &&
          url.pathname === '/api/asap/staff/analytics' &&
          url.searchParams.get('range') === 'last90';
      }),
      staffPage.locator('#analytics-date-range').selectOption('last90')
    ]);
    await staffPage.locator('#analytics-container .analytics-shell').waitFor();
    assert.equal(await staffPage.locator('#analytics-date-range').inputValue(), 'last90');
    assert.match(await staffPage.locator('.analytics-range-summary').textContent(), /Test Library/);
    await scan(staffPage, axeSource, args.artifactRoot, report, 'mobile', 'analytics-library-staff');
    assert.deepEqual(staffErrors, [], `Library-staff Analytics raised a browser error: ${staffErrors.join('; ')}`);
    assert.equal(staffContextState.traffic.externalRequests, 0, 'Library-staff Analytics requested an external asset');
  } finally {
    await staffContextState.context.close();
  }

  report.analytics = {
    desktopSuperAdminScope: 'all',
    desktopRange: 'last90',
    invalidScopeRecovery: true,
    allScopeRecoveryRequests: 1,
    mobileLibraryOnly: true
  };
}

async function runStaleAssignmentCandidates(browser, args, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  const assignmentPosts = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (request.method() === 'POST' && url.pathname.endsWith('/assign')) assignmentPosts.push(url.pathname);
  });
  try {
    const candidatesResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/assignment-candidates?libraryOrgId=2`
    );
    assert.equal(candidatesResponse.status(), 200, await candidatesResponse.text());
    const candidatePayload = await candidatesResponse.json();
    const cases = [
      {
        name: 'title request',
        aId: args.staleTitleAId,
        bId: args.staleTitleBId,
        pageUrl: `${args.baseOrigin}/staff/?request=${args.staleTitleAId}`,
        apiPath: id => `/api/asap/staff/title-requests/${id}`,
        assignPath: id => `/api/asap/staff/title-requests/${id}/assign`,
        openLabel: id => `Open request ${id}`,
        pickerLabel: 'Assign to staff member',
        kicker: id => `Request ${id}`
      },
      {
        name: 'additional copy',
        aId: args.staleCopyAId,
        bId: args.staleCopyBId,
        pageUrl: `${args.baseOrigin}/staff/?stage=additional_copies&request=${args.staleCopyAId}`,
        apiPath: id => `/api/asap/staff/additional-copies/${id}`,
        assignPath: id => `/api/asap/staff/additional-copies/${id}/assign`,
        openLabel: id => `Open additional-copy task ${id}`,
        pickerLabel: 'Assign additional-copy task',
        kicker: id => `Additional copy ${id}`
      }
    ];

    for (const scenario of cases) {
      await page.goto(scenario.pageUrl, { waitUntil: 'networkidle' });
      await page.locator('#request-dialog[open]').waitFor();
      await page.locator('#request-dialog-kicker').filter({ hasText: scenario.kicker(scenario.aId) }).waitFor();
      const beforeAResponse = await context.request.get(`${args.baseOrigin}${scenario.apiPath(scenario.aId)}`);
      assert.equal(beforeAResponse.status(), 200, await beforeAResponse.text());
      const beforeA = await beforeAResponse.json();

      const delayed = await delayNextCandidateResponse(page, candidatePayload);
      await page.getByRole('button', { name: 'Assign', exact: true }).click();
      await delayed.requested;
      await page.keyboard.press('Escape');
      await page.locator('#request-dialog').waitFor({ state: 'hidden' });
      await page.getByRole('button', { name: scenario.openLabel(scenario.bId) }).click();
      await page.locator('#request-dialog[open]').waitFor();
      await page.locator('#request-dialog-kicker').filter({ hasText: scenario.kicker(scenario.bId) }).waitFor();
      delayed.release();
      await delayed.completed;
      await page.waitForTimeout(50);
      assert.equal(
        await page.getByLabel(scenario.pickerLabel).count(),
        0,
        `Delayed ${scenario.name} A picker attached to B dialog`
      );
      await delayed.dispose();

      const unchangedAResponse = await context.request.get(`${args.baseOrigin}${scenario.apiPath(scenario.aId)}`);
      assert.equal(unchangedAResponse.status(), 200, await unchangedAResponse.text());
      const unchangedA = await unchangedAResponse.json();
      assert.equal(unchangedA.version, beforeA.version, `${scenario.name} A rowversion changed`);
      assert.equal(unchangedA.claimedByStaffUserId, beforeA.claimedByStaffUserId, `${scenario.name} A claimant changed`);

      await page.getByRole('button', { name: 'Assign', exact: true }).click();
      const assignment = page.getByLabel(scenario.pickerLabel);
      await assignment.waitFor({ state: 'visible' });
      await assignment.locator(`option[value="${args.staffIdentity.staffId}"]`).waitFor({ state: 'attached' });
      await assignment.selectOption(args.staffIdentity.staffId);
      const expectedPost = scenario.assignPath(scenario.bId);
      const [assignedResponse] = await Promise.all([
        page.waitForResponse(response =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === expectedPost),
        assignment.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click()
      ]);
      assert.equal(assignedResponse.status(), 200, await assignedResponse.text());
      await page.getByText('Claimed by Browser Staff', { exact: true }).waitFor();

      const assignedBResponse = await context.request.get(`${args.baseOrigin}${scenario.apiPath(scenario.bId)}`);
      assert.equal(assignedBResponse.status(), 200, await assignedBResponse.text());
      assert.equal((await assignedBResponse.json()).claimedByStaffUserId, args.staffIdentity.staffId);
      const finalAResponse = await context.request.get(`${args.baseOrigin}${scenario.apiPath(scenario.aId)}`);
      const finalA = await finalAResponse.json();
      assert.equal(finalA.version, beforeA.version, `${scenario.name} A changed after B assignment`);
      assert.equal(finalA.claimedByStaffUserId, beforeA.claimedByStaffUserId, `${scenario.name} A was assigned`);
      assert.equal(assignmentPosts.includes(scenario.assignPath(scenario.aId)), false, `${scenario.name} A received an assignment POST`);
      assert.equal(assignmentPosts.filter(pathname => pathname === expectedPost).length, 1);
      await page.getByRole('button', { name: 'Close request details' }).click();
      await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    }

    report.staleAssignmentCandidates = {
      titleRequestBOnly: true,
      additionalCopyBOnly: true
    };
    assert.deepEqual(errors, [], `Stale candidate browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Stale candidate browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runOrdinaryTitleAssignment(browser, args, report) {
  const completed = {};
  for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const { context, traffic } = await createContext(browser, viewport, args.baseOrigin, args.staffIdentity);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      await page.goto(`${args.baseOrigin}/staff/?request=${args.staleTitleAId}`, { waitUntil: 'networkidle' });
      await page.locator('#request-dialog[open]').waitFor();
      await page.locator('#request-dialog-kicker').filter({ hasText: `Request ${args.staleTitleAId}` }).waitFor();

      const managementResponse = await context.request.get(`${args.baseOrigin}/api/asap/staff/users?orgId=2`);
      assert.equal(managementResponse.status(), 403, `${name} ordinary staff gained Staff Access list permission`);
      const candidatesResponse = await context.request.get(
        `${args.baseOrigin}/api/asap/staff/assignment-candidates?libraryOrgId=2`
      );
      assert.equal(candidatesResponse.status(), 200, await candidatesResponse.text());
      const candidates = (await candidatesResponse.json()).candidates;
      const candidateById = new Map(candidates.map(candidate => [candidate.id, candidate]));
      assert.ok(candidateById.has(args.invalidClaimantId), `${name} same-library peer was omitted`);
      assert.ok(candidateById.has(args.superIdentity.staffId), `${name} system super-admin was omitted`);
      assert.equal(candidateById.has(args.foreignStaffId), false, `${name} foreign staff candidate leaked`);

      const assignButton = page.getByRole('button', { name: 'Assign', exact: true });
      await assignButton.waitFor({ state: 'visible' });
      for (const assigneeId of [args.invalidClaimantId, args.superIdentity.staffId]) {
        await assignButton.click();
        const picker = page.getByLabel('Assign to staff member');
        await picker.waitFor({ state: 'visible' });
        await picker.locator(`option[value="${args.invalidClaimantId}"]`).waitFor({ state: 'attached' });
        await picker.locator(`option[value="${args.superIdentity.staffId}"]`).waitFor({ state: 'attached' });
        const optionIds = await picker.locator('option').evaluateAll(options => options.map(option => option.value));
        assert.equal(optionIds.includes(args.foreignStaffId), false, `${name} title picker leaked foreign staff`);
        await picker.selectOption(assigneeId);
        const [response] = await Promise.all([
          page.waitForResponse(candidate =>
            candidate.request().method() === 'POST' &&
            new URL(candidate.url()).pathname === `/api/asap/staff/title-requests/${args.staleTitleAId}/assign`),
          picker.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click()
        ]);
        assert.equal(response.status(), 200, await response.text());
        await page.getByText(`Claimed by ${candidateById.get(assigneeId).displayName}`, { exact: true }).waitFor();
      }

      completed[name] = true;
      assert.deepEqual(errors, [], `${name} ordinary title assignment raised a browser error: ${errors.join('; ')}`);
      assert.equal(traffic.externalRequests, 0, `${name} ordinary title assignment requested an external asset`);
    } finally {
      await context.close();
    }
  }
  report.ordinaryTitleAssignment = completed;
}

async function runStaleMutationCompletions(browser, args, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    const candidateResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/assignment-candidates?libraryOrgId=2`
    );
    assert.equal(candidateResponse.status(), 200, await candidateResponse.text());
    const candidatePayload = await candidateResponse.json();

    const scenario = (type, id) => type === 'additional_copy'
      ? {
          apiPath: `/api/asap/staff/additional-copies/${id}`,
          openLabel: `Open additional-copy task ${id}`,
          kicker: `Additional copy ${id}`,
          picker: 'Assign additional-copy task',
          listPath: '/api/asap/staff/additional-copies',
          claimFilter: '#additional-copy-claim-filter'
        }
      : {
          apiPath: `/api/asap/staff/title-requests/${id}`,
          openLabel: `Open request ${id}`,
          kicker: `Request ${id}`,
          picker: 'Assign to staff member',
          listPath: '/api/asap/staff/title-requests',
          claimFilter: '#claim-filter'
        };

    async function holdCurrentB(type, id) {
      const target = scenario(type, id);
      await page.keyboard.press('Escape');
      await page.locator('#request-dialog').waitFor({ state: 'hidden' });
      await page.locator(target.claimFilter).selectOption('all');
      await page.getByRole('button', { name: target.openLabel }).click();
      await page.locator('#request-dialog-kicker').filter({ hasText: target.kicker }).waitFor();
      if (type === 'title_request') {
        await page.getByRole('link', { name: 'Open patron in LEAP' }).waitFor();
      }
      const beforeResponse = await context.request.get(`${args.baseOrigin}${target.apiPath}`);
      assert.equal(beforeResponse.status(), 200, await beforeResponse.text());
      const before = await beforeResponse.json();
      const visible = {
        title: await page.locator('#request-dialog-title').textContent(),
        kicker: await page.locator('#request-dialog-kicker').textContent(),
        body: await page.locator('#request-dialog-body').textContent(),
        url: page.url()
      };
      const candidates = await delayNextCandidateResponse(page, candidatePayload);
      await page.getByRole('button', { name: 'Assign', exact: true }).click();
      await candidates.requested;
      visible.status = await page.locator('#app-status').textContent();
      return { target, before, visible, candidates };
    }

    async function releaseMutationWithBPending(mutation, heldB) {
      const refresh = page.waitForResponse(response =>
        response.request().method() === 'GET' &&
        new URL(response.url()).pathname === heldB.target.listPath);
      try {
        mutation.release();
        await mutation.completed;
        await refresh;
        await page.waitForTimeout(100);
        assert.equal(await page.locator('#request-dialog').getAttribute('open'), '');
        assert.equal(await page.locator('#request-dialog-title').textContent(), heldB.visible.title);
        assert.equal(await page.locator('#request-dialog-kicker').textContent(), heldB.visible.kicker);
        assert.equal(await page.locator('#request-dialog-body').textContent(), heldB.visible.body);
        assert.equal(await page.locator('#app-status').textContent(), heldB.visible.status);
        assert.equal(page.url(), heldB.visible.url);
        const afterResponse = await context.request.get(`${args.baseOrigin}${heldB.target.apiPath}`);
        assert.equal(afterResponse.status(), 200, await afterResponse.text());
        const after = await afterResponse.json();
        assert.equal(after.version, heldB.before.version);
        assert.equal(after.claimedByStaffUserId, heldB.before.claimedByStaffUserId);
      } finally {
        heldB.candidates.release();
        await heldB.candidates.completed.catch(() => {});
        await heldB.candidates.dispose();
        mutation.release();
        await mutation.dispose();
      }
      await page.getByLabel(heldB.target.picker).waitFor({ state: 'visible' });
    }

    const copyPage = id => `${args.baseOrigin}/staff/?stage=additional_copies&request=${id}`;

    await page.goto(copyPage(args.staleCopyAId), { waitUntil: 'networkidle' });
    await page.locator('#request-dialog-kicker').filter({ hasText: `Additional copy ${args.staleCopyAId}` }).waitFor();
    const staleCopyResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.staleCopyAId}`
    );
    const staleCopy = await staleCopyResponse.json();
    const externalClaim = await mutate(
      context,
      args.baseOrigin,
      `/api/asap/staff/additional-copies/${args.staleCopyAId}/claim`,
      { version: staleCopy.version }
    );
    assert.equal(externalClaim.status(), 200, await externalClaim.text());
    const externallyClaimed = await externalClaim.json();
    let delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/additional-copies/${args.staleCopyAId}/claim`
    );
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    assert.equal((await delayedMutation.accepted).status, 409);
    let heldB = await holdCurrentB('additional_copy', args.staleCopyBId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const conflictedAResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.staleCopyAId}`
    );
    const conflictedA = await conflictedAResponse.json();
    assert.equal(conflictedA.version, externallyClaimed.version);
    assert.equal(conflictedA.claimedByStaffUserId, args.superIdentity.staffId);

    await page.goto(copyPage(args.staleCopyAId), { waitUntil: 'networkidle' });
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/additional-copies/${args.staleCopyAId}/close`
    );
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Close task' }).click();
    assert.equal((await delayedMutation.accepted).status, 200);
    heldB = await holdCurrentB('additional_copy', args.staleCopyBId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const closedAResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.staleCopyAId}`
    );
    assert.equal((await closedAResponse.json()).status, 'closed');

    await page.goto(copyPage(args.staleCopyAId), { waitUntil: 'networkidle' });
    delayedMutation = await delayNextServerResponse(
      page,
      'DELETE',
      `/api/asap/staff/additional-copies/${args.staleCopyAId}`
    );
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Delete task' }).click();
    assert.equal((await delayedMutation.accepted).status, 200);
    heldB = await holdCurrentB('additional_copy', args.staleCopyBId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const deletedAResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.staleCopyAId}`
    );
    assert.equal(deletedAResponse.status(), 404);

    await page.goto(`${args.baseOrigin}/staff/?request=${args.staleTitleAId}`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Assign', exact: true }).click();
    const titlePicker = page.getByLabel('Assign to staff member');
    await titlePicker.waitFor({ state: 'visible' });
    await titlePicker.selectOption(args.invalidClaimantId);
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/title-requests/${args.staleTitleAId}/assign`
    );
    await titlePicker.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click();
    assert.equal((await delayedMutation.accepted).status, 200);
    heldB = await holdCurrentB('title_request', args.staleTitleBId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const assignedTitleAResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.staleTitleAId}`
    );
    assert.equal((await assignedTitleAResponse.json()).claimedByStaffUserId, args.invalidClaimantId);

    await page.goto(`${args.baseOrigin}/staff/?request=${args.staleCreateSourceId}`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    await page.locator('#additional-copy-create-dialog[open]').waitFor();
    await page.locator('#additional-copy-reminder').uncheck();
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`
    );
    await page.getByRole('button', { name: 'Create task' }).click();
    const acceptedCreate = await delayedMutation.accepted;
    assert.equal(acceptedCreate.status, 200);
    const createdId = acceptedCreate.json.additionalCopyRequest.id;
    await page.getByRole('button', { name: 'Cancel' }).click();
    await page.locator('#additional-copy-create-dialog').waitFor({ state: 'hidden' });
    heldB = await holdCurrentB('title_request', args.staleTitleBId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const createdResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${createdId}`
    );
    assert.equal(createdResponse.status(), 200, await createdResponse.text());

    await page.goto(copyPage(createdId), { waitUntil: 'networkidle' });
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/additional-copies/${createdId}/claim`
    );
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    assert.equal((await delayedMutation.accepted).status, 200);
    heldB = await holdCurrentB('additional_copy', createdId);
    await releaseMutationWithBPending(delayedMutation, heldB);
    const reopenedSameResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${createdId}`
    );
    assert.equal((await reopenedSameResponse.json()).claimedByStaffUserId, args.superIdentity.staffId);

    await page.goto(copyPage(createdId), { waitUntil: 'networkidle' });
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/additional-copies/${createdId}/unclaim`
    );
    await page.getByRole('button', { name: 'Unclaim' }).click();
    assert.equal((await delayedMutation.accepted).status, 200);
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.locator('#signed-out').waitFor({ state: 'visible' });
    delayedMutation.release();
    await delayedMutation.completed;
    await delayedMutation.dispose();
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#workspace').isVisible(), false);
    assert.equal(await page.locator('#app-status').textContent(), '');
    const signedOutMutationResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${createdId}`
    );
    assert.equal(signedOutMutationResponse.status(), 200, await signedOutMutationResponse.text());
    assert.equal((await signedOutMutationResponse.json()).claimedByStaffUserId, null);

    report.staleMutationCompletions = {
      additionalCopyConflict: true,
      additionalCopyNonDelete: true,
      additionalCopyDelete: true,
      titleRequestAssign: true,
      additionalCopyCreate: true,
      sameIdRerender: true,
      signedOutContext: true
    };
    assert.deepEqual(errors, [], `Stale mutation browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Stale mutation browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runStaleOperationErrorCompletion(browser, args, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  let responses = null;
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.resolutionRequestId}`, { waitUntil: 'networkidle' });
    await page.getByText('Hold placement recovery', { exact: true }).waitFor();
    await fillOperatorResolution(page, true);
    responses = await delayRecoveryErrorResponses(page);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Resolve operation' }).click();
    await responses.firstRequested;

    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.locator('[data-status="suggestion"]').click();
    await page.locator('#claim-filter').selectOption('all');
    await page.getByRole('button', { name: `Open request ${args.staleTitleBId}` }).click();
    await page.locator('#request-dialog-kicker').filter({ hasText: `Request ${args.staleTitleBId}` }).waitFor();
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.locator('[data-status="pending_hold"]').click();

    await page.getByRole('button', { name: `Open request ${args.resolutionRequestId}` }).click();
    await page.locator('#request-dialog-kicker').filter({ hasText: `Request ${args.resolutionRequestId}` }).waitFor();
    await page.getByText('Hold placement recovery', { exact: true }).waitFor();
    await fillOperatorResolution(page, true);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Resolve operation' }).click();
    await responses.secondCompleted;
    await page.getByText('Current hold recovery failed.', { exact: true }).waitFor();
    const current = {
      title: await page.locator('#request-dialog-title').textContent(),
      kicker: await page.locator('#request-dialog-kicker').textContent(),
      body: await page.locator('#request-dialog-body').textContent(),
      status: await page.locator('#app-status').textContent()
    };

    responses.releaseFirst();
    await responses.firstCompleted;
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#request-dialog').getAttribute('open'), '');
    assert.equal(await page.locator('#request-dialog-title').textContent(), current.title);
    assert.equal(await page.locator('#request-dialog-kicker').textContent(), current.kicker);
    assert.equal(await page.locator('#request-dialog-body').textContent(), current.body);
    assert.equal(await page.locator('#app-status').textContent(), current.status);
    assert.equal(current.status, 'Current hold recovery failed.');
    assert.equal(await page.getByRole('button', { name: 'Resolve operation' }).isEnabled(), true);
    report.staleMutationCompletions.holdOperationError = true;
    assert.deepEqual(errors, [], `Stale hold-recovery error flow raised a browser error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Stale hold-recovery error flow requested an external asset');
  } finally {
    if (responses) {
      responses.releaseFirst();
      await responses.firstCompleted.catch(() => {});
      await responses.dispose();
    }
    await context.close();
  }
}

async function runAdditionalCopies(browser, args, axeSource, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.copySourceRequestId}`, { waitUntil: 'networkidle' });
    await page.locator('#request-dialog[open]').waitFor();
    const sourceResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.copySourceRequestId}`
    );
    assert.equal(sourceResponse.status(), 200);
    const source = await sourceResponse.json();
    assert.equal(source.claimedAt, '2026-09-01T12:13:14Z');
    assert.equal(source.created, '2026-09-01T12:10:11Z');
    assert.equal(source.updated, '2026-09-01T12:14:15Z');
    assert.equal(new Date(source.claimedAt).toISOString(), '2026-09-01T12:13:14.000Z');
    const createAction = page.getByRole('button', { name: 'Additional copy', exact: true });
    await createAction.click();
    await page.locator('#additional-copy-create-dialog[open]').waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'cancel-additional-copy');
    assert.match(await page.locator('#additional-copy-create-summary').textContent(), /1 open additional-copy task.*BIB 92905/i);
    assert.equal(await page.locator('#additional-copy-reminder').isChecked(), false);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-create-preview');
    await page.keyboard.press('Escape');
    await page.locator('#additional-copy-create-dialog').waitFor({ state: 'hidden' });
    assert.equal(await createAction.evaluate(node => document.activeElement === node), true);

    await createAction.click();
    await page.locator('#additional-copy-reminder').check();
    const [createResponse] = await Promise.all([
      page.waitForResponse(candidate =>
        candidate.request().method() === 'POST' &&
        new URL(candidate.url()).pathname === `/api/asap/staff/title-requests/${args.copySourceRequestId}/additional-copy`),
      page.getByRole('button', { name: 'Create task' }).click()
    ]);
    assert.equal(createResponse.status(), 200, await createResponse.text());
    const createdPayload = await createResponse.json();
    const created = createdPayload.additionalCopyRequest;
    assert.equal(created.claimedByStaffUserId, args.superIdentity.staffId);
    assert.equal(created.libraryOrgName, 'Browser Source Library Snapshot');
    assert.equal(created.claimType, 'legacy');
    assert.equal(created.claimRuleId, args.legacyRuleId);
    assert.equal(created.claimedAt, '2026-09-01T12:13:14Z');
    assert.equal(new Date(created.claimedAt).toISOString(), '2026-09-01T12:13:14.000Z');
    assert.match(created.created, /Z$/);
    assert.match(created.updated, /Z$/);
    assert.equal(createdPayload.purchaseReminderEmail.requested, true);
    assert.equal(createdPayload.purchaseReminderEmail.queued, true);
    await page.locator('#app-status').filter({ hasText: `Additional-copy task ${created.id} created.` }).waitFor();

    await page.getByRole('button', { name: 'Close request details' }).click();
    await page.getByRole('button', { name: 'Additional copies' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'additional-copy-title');
    const openTab = page.locator('[data-copy-status="open"]');
    await openTab.focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.copyStatus), 'closed');
    await page.keyboard.press('ArrowLeft');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.copyStatus), 'open');
    await page.locator('#additional-copy-claim-filter').selectOption('mine');
    await page.locator('#additional-copy-search').fill('Browser additional copy source');
    await page.getByRole('button', { name: `Open additional-copy task ${created.id}` }).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-grid');

    await page.getByRole('button', { name: `Open additional-copy task ${created.id}` }).click();
    await page.locator('#request-dialog[open]').waitFor();
    assert.equal(
      await page.getByRole('link', { name: 'Open source title request' }).getAttribute('href'),
      `/staff/?request=${args.copySourceRequestId}`
    );
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Close task' }).click();
    await page.getByRole('button', { name: 'Reopen task' }).waitFor();
    await page.getByRole('button', { name: 'Reopen task' }).click();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();
    const retainedResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${created.id}`
    );
    assert.equal(retainedResponse.status(), 200);
    const retained = await retainedResponse.json();
    assert.equal(retained.claimedByStaffUserId, args.superIdentity.staffId);
    assert.equal(retained.claimType, 'legacy');
    assert.equal(retained.claimRuleId, args.legacyRuleId);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-valid-reopen');

    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    const unclaimedResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${created.id}`
    );
    const unclaimed = await unclaimedResponse.json();
    const externalClaim = await mutate(
      context,
      args.baseOrigin,
      `/api/asap/staff/additional-copies/${created.id}/claim`,
      { version: unclaimed.version }
    );
    assert.equal(externalClaim.status(), 200, await externalClaim.text());
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.locator('#app-status').filter({ hasText: /changed|reload/i }).waitFor();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();
    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Assign', exact: true }).click();
    const assignment = page.getByLabel('Assign additional-copy task');
    await assignment.selectOption({ label: 'Browser Staff' });
    await assignment.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click();
    await page.getByText('Claimed by Browser Staff', { exact: true }).waitFor();
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Close task' }).click();
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Delete task' }).click();
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.locator('#app-status').filter({ hasText: 'Additional-copy task deleted.' }).waitFor();

    const usersResponse = await context.request.get(`${args.baseOrigin}/api/asap/staff/users?orgId=2`);
    assert.equal(usersResponse.status(), 200);
    const users = await usersResponse.json();
    const invalidClaimant = users.users.find(user => user.id === args.invalidClaimantId);
    assert.ok(invalidClaimant, 'Could not find the retained-claim staff user');
    const beforeDeactivation = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.invalidClosedCopyId}`
    );
    assert.equal((await beforeDeactivation.json()).claimedByStaffUserId, args.invalidClaimantId);
    const deactivation = await mutateDelete(
      context,
      args.baseOrigin,
      `/api/asap/staff/users/${args.invalidClaimantId}`,
      { version: invalidClaimant.version }
    );
    assert.equal(deactivation.status(), 200, await deactivation.text());
    const deactivationResult = await deactivation.json();
    assert.equal(deactivationResult.cleanup.openAdditionalCopyClaimsCleared, 0);
    const retainedClosed = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.invalidClosedCopyId}`
    );
    assert.equal((await retainedClosed.json()).claimedByStaffUserId, args.invalidClaimantId);

    await page.locator('#additional-copy-search').fill('');
    await page.locator('#additional-copy-claim-filter').selectOption('all');
    await page.locator('[data-copy-status="closed"]').click();
    await page.getByRole('button', { name: `Open additional-copy task ${args.invalidClosedCopyId}` }).click();
    const [reopenResponse] = await Promise.all([
      page.waitForResponse(candidate =>
        candidate.request().method() === 'POST' &&
        new URL(candidate.url()).pathname === `/api/asap/staff/additional-copies/${args.invalidClosedCopyId}/reopen`),
      page.getByRole('button', { name: 'Reopen task' }).click()
    ]);
    assert.equal(reopenResponse.status(), 200, await reopenResponse.text());
    const reopened = await reopenResponse.json();
    assert.equal(reopened.claimClearedReason, 'claimant_inactive');
    assert.equal(reopened.claimedByStaffUserId, null);
    await page.locator('#app-status').filter({ hasText: /retained claim was cleared.*claimant inactive/i }).waitFor();
    await page.getByText(/System cleared retained claim while reopening \(claimant_inactive\)/).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-cleared-reopen');

    report.additionalCopy = {
      createdId: created.id,
      inheritedClaimType: created.claimType,
      inheritedClaimRuleId: created.claimRuleId,
      clearedReason: reopened.claimClearedReason
    };
    assert.deepEqual(errors, [], `Additional-copy browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Additional-copy browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runScopedBlocked(browser, args, axeSource, report) {
  const { context, traffic } = await createContext(
    browser,
    { width: 390, height: 844 },
    args.baseOrigin,
    args.staffIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.blockedRequestId}`, { waitUntil: 'networkidle' });
    await page.locator('#request-dialog[open]').waitFor();
    await page.getByText('Hold placement recovery', { exact: true }).waitFor();
    assert.equal(await page.getByLabel('Identifier', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('BIB ID', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('Automatically place hold', { exact: true }).isDisabled(), true);
    assert.equal(await page.getByLabel('Title', { exact: true }).isEnabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Save changes' }).isEnabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Claim', exact: true }).isEnabled(), true);
    for (const name of ['Purchase', 'Already own', 'Reject', 'Close silently']) {
      assert.equal(await page.getByRole('button', { name, exact: true }).isDisabled(), true, `${name} should honor the operation barrier`);
    }
    assert.equal(await page.getByRole('button', { name: 'Reconcile provider state' }).count(), 0);
    assert.equal(await page.getByRole('button', { name: 'Resolve operation' }).count(), 0);
    assert.equal(await page.locator('#scope-field').isVisible(), false);
    const forbidden = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.otherRequestId}`
    );
    assert.equal(forbidden.status(), 404, 'Library staff could read another library request');
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'scoped-blocked-recovery');
    await page.getByRole('button', { name: 'Search Polaris catalog' }).click();
    await page.locator('#polaris-dialog[open]').waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'polaris-search-scoped');
    await page.keyboard.press('Escape');
    await page.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'Close request details' }).click();
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await assertReadableMobileQueue(page);
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'scoped-queue');

    await page.getByRole('button', { name: 'Additional copies' }).click();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'additional-copy-title');
    await page.getByRole('button', { name: `Open additional-copy task ${args.mobileCopyId}` }).waitFor();
    const openTab = page.locator('[data-copy-status="open"]');
    await openTab.focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.copyStatus), 'closed');
    await page.keyboard.press('ArrowLeft');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.copyStatus), 'open');
    await page.keyboard.press('Enter');
    await page.getByRole('button', { name: `Open additional-copy task ${args.mobileCopyId}` }).waitFor();
    await page.locator('#additional-copy-claim-filter').selectOption('mine_unclaimed');
    await assertReadableMobileQueue(page, '#additional-copy-grid');
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'additional-copy-scoped-grid');
    const openCopy = page.getByRole('button', { name: `Open additional-copy task ${args.mobileCopyId}` });
    await openCopy.click();
    await page.getByText('The source title request is no longer available.', { exact: true }).waitFor();
    const managementResponse = await context.request.get(`${args.baseOrigin}/api/asap/staff/users?orgId=2`);
    assert.equal(managementResponse.status(), 403, 'Ordinary staff must not gain Staff Access list permission');
    const candidatesResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/assignment-candidates?libraryOrgId=2`
    );
    assert.equal(candidatesResponse.status(), 200, await candidatesResponse.text());
    const candidatePayload = await candidatesResponse.json();
    for (const candidate of candidatePayload.candidates) {
      assert.deepEqual(Object.keys(candidate).sort(), ['displayName', 'id']);
    }
    const candidateIds = new Set(candidatePayload.candidates.map(candidate => candidate.id));
    const candidateById = new Map(candidatePayload.candidates.map(candidate => [candidate.id, candidate]));
    assert.ok(candidateIds.has(args.staffIdentity.staffId), 'Same-library staff candidate was omitted');
    assert.ok(candidateIds.has(args.superIdentity.staffId), 'System super-admin candidate was omitted');
    assert.equal(candidateIds.has(args.foreignStaffId), false, 'Foreign-library staff candidate leaked');
    assert.equal(candidateIds.has(args.invalidTenantStaffId), true, 'Stored tenant metadata incorrectly affected eligibility');
    assert.equal(candidateIds.has(args.unboundStaffId), true, 'Never-signed-in candidate was omitted');

    const beforeForeignAssignment = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.mobileCopyId}`
    );
    const beforeForeign = await beforeForeignAssignment.json();
    const rejectedForeign = await mutate(
      context,
      args.baseOrigin,
      `/api/asap/staff/additional-copies/${args.mobileCopyId}/assign`,
      { version: beforeForeign.version, assigneeId: args.foreignStaffId }
    );
    assert.equal(rejectedForeign.status(), 400, await rejectedForeign.text());
    assert.equal((await rejectedForeign.json()).code, 'assignee_ineligible');

    await page.getByRole('button', { name: 'Assign', exact: true }).click();
    let assignment = page.getByLabel('Assign additional-copy task');
    await assignment.waitFor({ state: 'visible' });
    await assignment.locator(`option[value="${args.staffIdentity.staffId}"]`).waitFor({ state: 'attached' });
    await assignment.locator(`option[value="${args.superIdentity.staffId}"]`).waitFor({ state: 'attached' });
    let optionIds = await assignment.locator('option').evaluateAll(options => options.map(option => option.value));
    let optionLabels = await assignment.locator('option').allTextContents();
    assert.ok(optionIds.includes(args.staffIdentity.staffId));
    assert.ok(optionIds.includes(args.superIdentity.staffId));
    assert.equal(optionIds.includes(args.foreignStaffId), false);
    assert.ok(optionLabels.includes(candidateById.get(args.staffIdentity.staffId).displayName), `Same-library picker option missing: ${JSON.stringify(optionLabels)}`);
    assert.ok(optionLabels.includes(candidateById.get(args.superIdentity.staffId).displayName), `System super-admin picker option missing: ${JSON.stringify(optionLabels)}`);
    assert.equal(optionLabels.includes('Browser Foreign Staff'), false, `Foreign picker option leaked: ${JSON.stringify(optionLabels)}`);
    await assignment.selectOption(args.superIdentity.staffId);
    await assignment.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click();
    await page.getByText(`Claimed by ${candidateById.get(args.superIdentity.staffId).displayName}`, { exact: true }).waitFor();

    await page.getByRole('button', { name: 'Assign', exact: true }).click();
    assignment = page.getByLabel('Assign additional-copy task');
    await assignment.selectOption(args.staffIdentity.staffId);
    await assignment.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click();
    await page.getByText('Claimed by Browser Staff', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Unclaim' }).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'additional-copy-scoped-detail');
    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    await page.locator('#app-status').filter({ hasText: 'Task unclaimed.' }).waitFor();
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(id =>
      document.activeElement?.getAttribute('aria-label') === `Open additional-copy task ${id}`,
    args.mobileCopyId);
    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.locator('#signed-out').waitFor({ state: 'visible' });
    report.assignmentCandidates = {
      ordinaryStaffAssigned: args.staffIdentity.staffId,
      systemSuperAdminAssigned: args.superIdentity.staffId,
      foreignStaffExcluded: args.foreignStaffId
    };
    assert.deepEqual(errors, [], `Scoped browser flow raised an error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Scoped browser flow requested an external asset');
  } finally {
    await context.close();
  }
}

async function runOperatorResolution(browser, args, axeSource, report) {
  for (const [name, viewport] of [['desktop', { width: 1280, height: 900 }], ['mobile', { width: 390, height: 844 }]]) {
    const { context, traffic } = await createContext(browser, viewport, args.baseOrigin, args.superIdentity);
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    try {
      await page.goto(`${args.baseOrigin}/staff/?request=${args.resolutionRequestId}`, { waitUntil: 'networkidle' });
      await page.locator('#request-dialog[open]').waitFor();
      await page.getByText('Hold placement recovery', { exact: true }).waitFor();
      await page.getByText(/Operation \d+; attempt 2; epoch 3; frozen patron \*+2904; frozen BIB 92904\./).waitFor();
      const resolution = page.getByLabel('Resolution', { exact: true });
      const evidence = page.getByLabel('Evidence type', { exact: true });
      assert.equal(await resolution.inputValue(), 'succeeded');
      assert.equal(await evidence.inputValue(), 'authoritative_correlated_hold');
      assert.equal(await page.getByLabel('Proven final hold ID', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel('Evidence provenance', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel(/I attest that this evidence proves/).isVisible(), true);
      assert.equal(await page.getByLabel(/every responsible or superseded worker/).isVisible(), true);
      assert.equal(await page.getByLabel('Executor exclusion reference', { exact: true }).isVisible(), true);
      assert.equal(await page.getByLabel('Executor exclusion and in-flight work account', { exact: true }).isVisible(), true);
      assert.equal(await evidence.locator('option[value="fenced_never_dispatched"]').count(), 0);
      await resolution.focus();
      await page.keyboard.press('Tab');
      assert.equal(await evidence.evaluate(node => document.activeElement === node), true);
      await scan(page, axeSource, args.artifactRoot, report, name, 'operator-resolution');

      await fillOperatorResolution(page, false);
      const resolveButton = page.getByRole('button', { name: 'Resolve operation' });
      if (name === 'desktop') {
        let resolutionRequests = 0;
        page.on('request', request => {
          if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/resolve')) {
            resolutionRequests += 1;
          }
        });
        await resolveButton.click();
        await page.waitForTimeout(150);
        assert.equal(resolutionRequests, 0, 'Missing proof attestation must not issue a resolution request');
        assert.equal(
          await page.getByLabel(/I attest that this evidence proves/).evaluate(node => node.matches(':invalid')),
          true,
          'Missing operation-specific proof attestation should remain visibly invalid'
        );
      }
      await page.getByLabel(/I attest that this evidence proves/).check();
      const reason = page.getByLabel('Reason', { exact: true });
      await reason.focus();
      await page.keyboard.press('Tab');
      assert.equal(await resolveButton.evaluate(node => document.activeElement === node), true);
      await resolveButton.scrollIntoViewIfNeeded();
      const buttonBounds = await resolveButton.boundingBox();
      assert.ok(buttonBounds && buttonBounds.y >= 0 && buttonBounds.y + buttonBounds.height <= viewport.height,
        `${name} lower resolution command should be visible inside the scrolled dialog`);
      await scan(page, axeSource, args.artifactRoot, report, name, 'operator-resolution-lower');
      assert.deepEqual(errors, [], `${name} operator resolution raised a browser error`);
      assert.equal(traffic.externalRequests, 0, `${name} operator resolution requested an external asset`);
    } finally {
      await context.close();
    }
  }

  const { context, traffic } = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(`${args.baseOrigin}/staff/?request=${args.resolutionRequestId}`, { waitUntil: 'networkidle' });
    await page.getByText(/Operation \d+; attempt 2; epoch 3; frozen patron \*+2904; frozen BIB 92904\./).waitFor();
    await fillOperatorResolution(page, true);
    page.once('dialog', dialog => dialog.accept());
    const [response] = await Promise.all([
      page.waitForResponse(candidate =>
        candidate.request().method() === 'POST' &&
        /\/api\/asap\/staff\/hold-operations\/\d+\/resolve$/.test(new URL(candidate.url()).pathname)),
      page.getByRole('button', { name: 'Resolve operation' }).click()
    ]);
    assert.equal(response.status(), 200, await response.text());
    await page.getByText('Hold recovery state updated.', { exact: true }).waitFor();
    const statusValue = page.locator('#request-dialog .status-badge');
    await statusValue.waitFor();
    assert.equal((await statusValue.textContent()).trim(), 'Hold placed');
    assert.equal(await page.getByRole('button', { name: 'Resolve operation' }).count(), 0);
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'operator-resolution-accepted');
    assert.deepEqual(errors, [], `Accepted operator resolution raised a browser error: ${errors.join('; ')}`);
    assert.equal(traffic.externalRequests, 0, 'Accepted operator resolution requested an external asset');
  } finally {
    await context.close();
  }
}

async function fillOperatorResolution(page, attestProof) {
  await page.getByLabel('Evidence reference', { exact: true }).fill('Support report SR-BROWSER-2904');
  await page.getByLabel('Evidence provenance', { exact: true }).fill('Polaris support final transaction report');
  await page.getByLabel('Connection to this exact attempt', { exact: true })
    .fill('Report identifies operation attempt 2, frozen patron ending 2904, and BIB 92904 as the completed transaction.');
  await page.getByLabel('Proven final hold ID', { exact: true }).fill('8456');
  await page.getByLabel(/every responsible or superseded worker/).check();
  await page.getByLabel('Executor exclusion reference', { exact: true }).fill('Operations record OPS-BROWSER-2904');
  await page.getByLabel('Executor exclusion and in-flight work account', { exact: true })
    .fill('All responsible and superseded workers and interactive hosts ended at 2026-09-13T13:00:00Z; the cited final report accounts for already-sent provider work.');
  await page.getByLabel(/I attest that the exclusion record identifies/).check();
  await page.getByLabel('Reason', { exact: true }).fill('Resolve from the documented final provider result after verified executor exclusion.');
  if (attestProof) await page.getByLabel(/I attest that this evidence proves/).check();
}

async function loadDependencies() {
  let chromium;
  try { ({ chromium } = require('playwright')); } catch {
    throw new Error('Missing local Playwright. Run npm install with playwright@1.62.1.');
  }
  let axePath;
  try { axePath = require.resolve('axe-core/axe.min.js'); } catch {
    throw new Error('Missing local axe-core. Run npm install with axe-core@4.10.3.');
  }
  return { chromium, axeSource: await fs.readFile(axePath, 'utf8') };
}

async function launch(chromium) {
  const executablePath = process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH;
  if (executablePath) await fs.access(executablePath);
  return chromium.launch({ headless: true, ...(executablePath ? { executablePath } : {}) });
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  await fs.mkdir(args.artifactRoot, { recursive: true });
  const { chromium, axeSource } = await loadDependencies();
  const browser = await launch(chromium);
  const report = { states: [] };
  try {
    await runAnonymous(browser, args, axeSource, report);
    await runSuperAdmin(browser, args, axeSource, report);
    await runAnalytics(browser, args, axeSource, report);
    await runStaleAssignmentCandidates(browser, args, report);
    await runOrdinaryTitleAssignment(browser, args, report);
    await runStaleMutationCompletions(browser, args, report);
    await runStaleOperationErrorCompletion(browser, args, report);
    await runAdditionalCopies(browser, args, axeSource, report);
    await runOperatorResolution(browser, args, axeSource, report);
    await runScopedBlocked(browser, args, axeSource, report);
    assert.equal(report.states.length, 22, 'Expected twenty-two major staff browser states');
    await fs.writeFile(
      path.join(args.artifactRoot, 'staff-browser-results.json'),
      JSON.stringify(report, null, 2),
      'utf8'
    );
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(`Staff browser acceptance failed: ${error.stack || error.message || 'unknown failure'}`);
  process.exitCode = 1;
});
