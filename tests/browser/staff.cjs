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
  assert.equal(geometry.cellCount, selector === '#request-grid' ? 9 : 7,
    'Mobile queue should render all request columns');
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
  assert.equal(args.primaryRequestId, '9007199254741993');
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
    await storedBibPage.locator('.action-choice button[type="submit"]').click();
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
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /Outstanding purchase/);
      return dialog.accept();
    });
    await page.locator('.action-choice button[type="submit"]').click();
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
      assert.equal(body.requestId, '9007199254741993');
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

    let applyFallbackDetail = false;
    const searchWithFallbackMetadata = async route => {
      const body = JSON.parse(route.request().postData() || '{}');
      if (body.mode === 'title' && body.query === 'Fallback metadata') {
        const response = await route.fetch();
        const result = await response.json();
        applyFallbackDetail = true;
        await route.fulfill({ response, json: { ...result, status: 'found', totalMatches: 1,
          results: [{ bibId: '9001', title: 'Catalog title 9001', author: 'Catalog author',
            publication: '2026', format: 'Book', identifier: '9780000000001' }] } });
        return;
      }
      if (body.mode !== 'bib' || !applyFallbackDetail) return route.continue();
      const response = await route.fetch();
      const detail = await response.json();
      applyFallbackDetail = false;
      await route.fulfill({ response, json: { ...detail, identifier: null, publication: '', format: null,
        holdingsSummary: { myLibraryCount: 3, otherLibraryCount: 4, consortiumCount: 7,
          isHoldable: true, hasHoldableAtMyLibrary: true },
        holdingsUnavailable: false, patronHasHold: true } });
    };
    await page.route('**/api/asap/staff/bib-lookup', searchWithFallbackMetadata);
    await page.locator('#polaris-mode').selectOption('title');
    await page.locator('#polaris-query').fill('Fallback metadata');
    await page.locator('#polaris-form button[type=submit]').click();
    await page.getByText(/Publication: 2026/).waitFor();
    await page.getByRole('button', { name: 'Use BIB 9001' }).click();
    await page.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    await page.unroute('**/api/asap/staff/bib-lookup', searchWithFallbackMetadata);
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
    assert.match(selectedMetadataContext, /Polaris format: Book/);
    assert.match(selectedMetadataContext, /3 item\(s\) at this library, 4 elsewhere; holdable/);
    assert.match(selectedMetadataContext, /This patron already has a hold for this BIB/);
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
    await page.getByLabel('Automatically place hold').uncheck();
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /No hold will be placed automatically/);
      return dialog.dismiss();
    });
    await page.getByRole('button', { name: 'Save changes' }).click();
    assert.equal(await page.getByRole('button', { name: 'Save changes' }).isEnabled(), true);
    page.once('dialog', dialog => dialog.accept());
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
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /Pending hold/);
      assert.match(dialog.message(), /cannot place a hold until it is enabled/);
      return dialog.accept();
    });
    await page.getByRole('button', { name: 'Ready for hold' }).click();
    await page.locator('#request-dialog .status-badge').filter({ hasText: 'Pending hold' }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Place hold' }).count(), 0);

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
      assert.equal(await page.locator('#request-dialog').isVisible(), true,
        'Escape must keep a consequential in-flight action visible until its result arrives');
      await page.locator(target.claimFilter).selectOption('all');
      await page.getByRole('button', { name: target.openLabel }).evaluate(node => node.click());
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
      try {
        mutation.release();
        await mutation.completed;
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
    await page.getByRole('button', { name: 'Permanently delete task' }).click();
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
    const assignedTitleA = await assignedTitleAResponse.json();
    assert.equal(assignedTitleA.claimedByStaffUserId, args.invalidClaimantId);
    const forgedTitleUnclaim = await mutate(context, args.baseOrigin,
      `/api/asap/staff/title-requests/${args.staleTitleAId}/unclaim`,
      { version: assignedTitleA.version });
    assert.equal(forgedTitleUnclaim.status(), 403, await forgedTitleUnclaim.text());
    const { context: ordinaryTitleContext } = await createContext(
      browser, { width: 1280, height: 900 }, args.baseOrigin, args.staffIdentity);
    try {
      const forbidden = await mutate(ordinaryTitleContext, args.baseOrigin,
        `/api/asap/staff/title-requests/${args.staleTitleAId}/clear-claim`,
        { version: assignedTitleA.version });
      assert.equal(forbidden.status(), 403, await forbidden.text());
    } finally {
      await ordinaryTitleContext.close();
    }
    await page.goto(`${args.baseOrigin}/staff/?request=${args.staleTitleAId}`, { waitUntil: 'networkidle' });
    const titleClear = page.getByRole('button', { name: 'Clear claim' });
    await titleClear.waitFor();
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /become unclaimed/);
      return dialog.accept();
    });
    await titleClear.click();
    await page.locator('#request-dialog .detail-meta').filter({ hasText: 'Unclaimed' }).waitFor();
    await page.locator('#request-dialog .request-activity').filter({
      hasText: `Administrator cleared ${assignedTitleA.claimedByDisplayName}'s claim.`
    }).waitFor();
    const repeatedTitleClear = await mutate(context, args.baseOrigin,
      `/api/asap/staff/title-requests/${args.staleTitleAId}/clear-claim`,
      { version: assignedTitleA.version });
    assert.equal(repeatedTitleClear.status(), 409, await repeatedTitleClear.text());

    await page.goto(`${args.baseOrigin}/staff/?request=${args.staleCreateSourceId}`, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    await page.locator('#additional-copy-create-dialog[open]').waitFor();
    await page.locator('#additional-copy-reminder').uncheck();
    const createPath = `**/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`;
    const noCommitCreate = route => route.fulfill({ status: 503, contentType: 'application/json',
      body: JSON.stringify({ code: 'notification_dependency_unavailable',
        message: 'Reminder configuration is temporarily unavailable. The task was not changed.' }) });
    await page.route(createPath, noCommitCreate);
    await page.getByRole('button', { name: 'Create task' }).click();
    await page.locator('#app-status').filter({ hasText: /The task was not changed/ }).waitFor();
    assert.equal(await page.evaluate(() => window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation')), null,
      'a definitive pre-mutation dependency error clears the pending attempt marker');
    assert.equal(await page.getByRole('button', { name: 'Create task' }).isEnabled(), true);
    await page.unroute(createPath, noCommitCreate);
    delayedMutation = await delayNextServerResponse(
      page,
      'POST',
      `/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`
    );
    await page.getByRole('button', { name: 'Create task' }).click();
    const acceptedCreate = await delayedMutation.accepted;
    assert.equal(acceptedCreate.status, 200);
    const pendingAttempt = await page.evaluate(() =>
      JSON.parse(window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation')));
    assert.equal(pendingAttempt.sourceId, String(args.staleCreateSourceId),
      'the original source version must be saved before the task request is dispatched');
    const createdId = acceptedCreate.json.additionalCopyRequest.id;
    await page.getByRole('button', { name: 'Cancel' }).click();
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), true,
      'Cancel must retain an in-flight creation until the committed result is known');
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), true,
      'Escape must retain an in-flight creation until the committed result is known');
    delayedMutation.release();
    await delayedMutation.completed;
    await delayedMutation.dispose();
    await page.locator('#app-status').filter({ hasText: `Additional-copy task ${createdId} created.` }).waitFor();
    assert.equal(await page.evaluate(() => window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation')), null);
    await page.locator('#additional-copy-create-dialog').waitFor({ state: 'hidden' });
    const createdResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${createdId}`
    );
    assert.equal(createdResponse.status(), 200, await createdResponse.text());

    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    await page.locator('#additional-copy-create-dialog[open]').waitFor();
    const uncertainPreview = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`);
    assert.equal(uncertainPreview.status(), 200, await uncertainPreview.text());
    const uncertainSourceVersion = (await uncertainPreview.json()).version;
    let releaseOldCopyList;
    let oldCopyListReached;
    const oldCopyListAccepted = new Promise(resolve => { oldCopyListReached = resolve; });
    const oldCopyList = async route => {
      const held = new Promise(resolve => { releaseOldCopyList = resolve; });
      oldCopyListReached();
      await held;
      try {
        await route.fulfill({ status: 200, contentType: 'application/json',
          body: JSON.stringify({ scope: 'all', status: 'open', items: [], availableLibraries: [] }) });
      } catch {
        // The mutation's uncertainty fence aborts this pre-mutation list request.
      }
    };
    await page.route(/\/api\/asap\/staff\/additional-copies\?/, oldCopyList);
    const oldListStarted = page.waitForRequest(candidate =>
      candidate.method() === 'GET' && /\/api\/asap\/staff\/additional-copies\?/.test(candidate.url()));
    await page.evaluate(() => document.querySelector('#refresh-additional-copies').click());
    await oldListStarted;
    await oldCopyListAccepted;
    const uncertainCreatePath = `**/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`;
    const uncertainCreate = route => route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ committed: true, additionalCopyRequestId: '1', finalStatus: 'open' }) });
    await page.route(uncertainCreatePath, uncertainCreate);
    await page.getByRole('button', { name: 'Create task' }).click();
    await page.locator('#app-status').filter({ hasText: /Additional-copy creation could not be confirmed/ }).waitFor();
    await page.unroute(uncertainCreatePath, uncertainCreate);
    releaseOldCopyList();
    await page.unroute(/\/api\/asap\/staff\/additional-copies\?/, oldCopyList);
    await page.getByRole('button', { name: 'Cancel' }).click();
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), false,
      'an uncertain create cannot be retried from a fresh dialog before task-list refresh');
    await page.locator('#app-status').filter({ hasText: /Refresh the open additional-copy task list/ }).waitFor();
    await page.reload({ waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), false,
      'an uncertain create remains blocked after a full page reload');
    await page.evaluate(() => document.querySelector('[data-copy-status="closed"]').click());
    await page.locator('#close-request').click();
    await page.locator('.view-tab[data-view="additional-copies"]').click();
    await page.locator('#refresh-additional-copies').click();
    await page.locator('.view-tab[data-view="queue"]').click();
    await page.locator('[data-status="hold_placed"]').click();
    await page.getByRole('button', { name: `Open request ${args.staleCreateSourceId}` }).click();
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    await page.locator('#app-status').filter({ hasText: /Refresh the open additional-copy task list/ }).waitFor();
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), false,
      'the closed-task list cannot resolve an uncertain open-task creation');
    await page.locator('#close-request').click();
    await page.locator('.view-tab[data-view="additional-copies"]').click();
    await page.locator('[data-copy-status="open"]').click();
    await page.locator('#refresh-additional-copies').click();
    await page.locator('#additional-copy-create-review').waitFor();
    await page.locator('#additional-copy-create-review-summary').filter({ hasText: /Creation for BIB .* could not be confirmed/ }).waitFor();
    await page.locator('.view-tab[data-view="queue"]').click();
    await page.locator('[data-status="hold_placed"]').click();
    await page.getByRole('button', { name: `Open request ${args.staleCreateSourceId}` }).click();
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    assert.equal(await page.locator('#additional-copy-create-dialog').isVisible(), false,
      'a refreshed open list still requires the staff user to review matching tasks');
    await page.locator('#close-request').click();
    await page.locator('.view-tab[data-view="additional-copies"]').click();
    await page.getByRole('button', { name: 'I reviewed these tasks' }).click();
    assert.equal(await page.locator('#additional-copy-create-review').isVisible(), false);
    assert.equal(await page.locator('#refresh-additional-copies').evaluate(node => document.activeElement === node), true,
      'focus returns to the task-list refresh control after review acknowledgment');
    const earlierCreation = await mutate(context, args.baseOrigin,
      `/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`,
      { version: uncertainSourceVersion, emailPurchaseReminder: false });
    assert.equal(earlierCreation.status(), 200, await earlierCreation.text());
    const earlierTaskId = (await earlierCreation.json()).additionalCopyRequest.id;
    await page.locator('.view-tab[data-view="queue"]').click();
    await page.locator('[data-status="hold_placed"]').click();
    await page.getByRole('button', { name: `Open request ${args.staleCreateSourceId}` }).click();
    await page.getByRole('button', { name: 'Additional copy', exact: true }).click();
    await page.locator('#additional-copy-create-dialog[open]').waitFor();
    const [fencedRetry] = await Promise.all([
      page.waitForResponse(candidate => candidate.request().method() === 'POST' &&
        candidate.url().endsWith(`/api/asap/staff/title-requests/${args.staleCreateSourceId}/additional-copy`)),
      page.getByRole('button', { name: 'Create task' }).click()
    ]);
    assert.equal(fencedRetry.status(), 409, await fencedRetry.text());
    assert.equal(fencedRetry.request().postDataJSON().version, uncertainSourceVersion,
      'the retry must submit the original source version, even after a newer preview');
    await page.locator('#additional-copy-create-dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.evaluate(() => window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation')), null,
      'the original-version retry fence clears after a definitive stale-version result');
    const refreshedTasks = await context.request.get(`${args.baseOrigin}/api/asap/staff/additional-copies?scope=all&status=open`);
    assert.equal(refreshedTasks.status(), 200, await refreshedTasks.text());
    const matchingTasks = (await refreshedTasks.json()).items.filter(item =>
      item.sourceTitleRequest === String(args.staleCreateSourceId));
    assert.deepEqual(matchingTasks.map(item => item.id).sort(), [createdId, earlierTaskId].sort(),
      'a retry after the earlier creation commits cannot create a second task');

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
    assert.equal(await page.locator('#request-dialog').isVisible(), true,
      'Escape must retain the in-flight unclaim until its result is known');
    await page.getByRole('button', { name: 'Sign out' }).evaluate(node => node.click());
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
    assert.equal(await page.locator('#request-dialog').isVisible(), true,
      'Escape must retain an in-flight hold resolution until its outcome is known');
    await page.locator('[data-status="suggestion"]').evaluate(node => node.click());
    await page.locator('#claim-filter').selectOption('all');
    await page.getByRole('button', { name: `Open request ${args.staleTitleBId}` }).evaluate(node => node.click());
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
    await page.locator('#app-status').filter({ hasText: 'The hold recovery outcome could not be confirmed.' }).waitFor();
    const current = {
      title: await page.locator('#request-dialog-title').textContent(),
      kicker: await page.locator('#request-dialog-kicker').textContent(),
      recovery: await page.locator('#request-dialog .hold-operation').textContent(),
      status: await page.locator('#app-status').textContent()
    };

    responses.releaseFirst();
    await responses.firstCompleted;
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#request-dialog').getAttribute('open'), '');
    assert.equal(await page.locator('#request-dialog-title').textContent(), current.title);
    assert.equal(await page.locator('#request-dialog-kicker').textContent(), current.kicker);
    assert.equal(await page.locator('#request-dialog .hold-operation').textContent(), current.recovery);
    assert.equal(await page.locator('#app-status').textContent(), current.status);
    assert.equal(current.status, 'The hold recovery outcome could not be confirmed. Reload before trying again.');
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
    assert.equal(createdPayload.committed, true);
    assert.equal(createdPayload.finalStatus, 'open');
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
    const { context: ordinaryStaffContext } = await createContext(
      browser, { width: 1280, height: 900 }, args.baseOrigin, args.staffIdentity);
    try {
      const forgedClear = await mutate(ordinaryStaffContext, args.baseOrigin,
        `/api/asap/staff/additional-copies/${created.id}/clear-claim`, { version: created.version });
      assert.equal(forgedClear.status(), 403, await forgedClear.text());
    } finally {
      await ordinaryStaffContext.close();
    }
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
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /return to open work/);
      return dialog.dismiss();
    });
    await page.getByRole('button', { name: 'Reopen task' }).click();
    assert.equal(await page.getByRole('button', { name: 'Reopen task' }).isVisible(), true);
    assert.equal(await page.getByRole('button', { name: 'Reopen task' }).evaluate(node => document.activeElement === node), true);
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /return to open work/);
      return dialog.accept();
    });
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
    const claimTransportFailure = route => route.abort('failed');
    await page.route(`**/api/asap/staff/additional-copies/${created.id}/claim`, claimTransportFailure);
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.locator('#app-status').filter({ hasText: /additional-copy action outcome could not be confirmed/i }).waitFor();
    await page.unroute(`**/api/asap/staff/additional-copies/${created.id}/claim`, claimTransportFailure);
    const emptyClaimReply = route => route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ committed: true, finalStatus: 'open' }) });
    await page.route(`**/api/asap/staff/additional-copies/${created.id}/claim`, emptyClaimReply);
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.locator('#app-status').filter({ hasText: /additional-copy action outcome could not be confirmed/i }).waitFor();
    await page.unroute(`**/api/asap/staff/additional-copies/${created.id}/claim`, emptyClaimReply);
    const uncertainClaimReply = route => route.fulfill({ status: 503, contentType: 'application/json',
      body: JSON.stringify({ code: 'additional_copy_outcome_unconfirmed',
        message: 'The additional-copy outcome could not be confirmed. Reload before trying again.' }) });
    await page.route(`**/api/asap/staff/additional-copies/${created.id}/claim`, uncertainClaimReply);
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.locator('#app-status').filter({ hasText: /additional-copy action outcome could not be confirmed/i }).waitFor();
    await page.unroute(`**/api/asap/staff/additional-copies/${created.id}/claim`, uncertainClaimReply);
    const afterTransportFailure = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${created.id}`);
    assert.equal((await afterTransportFailure.json()).version, unclaimed.version);
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
    const assignmentPath = `**/api/asap/staff/additional-copies/${created.id}/assign`;
    const partialAssignment = async route => {
      const response = await route.fetch();
      const committed = await response.json();
      assert.equal(committed.committed, true);
      await route.fulfill({ response, json: { ...committed,
        notificationStatus: 'dispatch_failed', notificationReason: 'queue_unavailable' } });
    };
    await page.route(assignmentPath, partialAssignment);
    await assignment.locator('xpath=ancestor::form').getByRole('button', { name: 'Assign', exact: true }).click();
    await page.getByText('Claimed by Browser Staff', { exact: true }).waitFor();
    await page.locator('#app-status').filter({ hasText: /Assignment notification could not be queued/ }).waitFor();
    assert.match(await page.locator('#app-status').getAttribute('class'), /warning/);
    await page.unroute(assignmentPath, partialAssignment);
    const assignedForClear = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${created.id}`);
    const assignedClaim = await assignedForClear.json();
    const forgedCopyUnclaim = await mutate(context, args.baseOrigin,
      `/api/asap/staff/additional-copies/${created.id}/unclaim`,
      { version: assignedClaim.version });
    assert.equal(forgedCopyUnclaim.status(), 403, await forgedCopyUnclaim.text());
    const staleClear = await mutate(context, args.baseOrigin,
      `/api/asap/staff/additional-copies/${created.id}/clear-claim`, { version: created.version });
    assert.equal(staleClear.status(), 409, await staleClear.text());
    const clearClaim = page.getByRole('button', { name: 'Clear claim' });
    await clearClaim.waitFor();
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /remain open and unclaimed/);
      return dialog.dismiss();
    });
    await clearClaim.click();
    assert.equal(await clearClaim.evaluate(node => document.activeElement === node), true);
    page.once('dialog', dialog => dialog.accept());
    await clearClaim.click();
    await page.locator('#request-dialog .detail-meta').filter({ hasText: 'Unclaimed' }).waitFor();
    const clearedTaskResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${created.id}`);
    const clearedTask = await clearedTaskResponse.json();
    assert.match(clearedTask.notes, /cleared another staff member's claim.*Previous claimant: Browser Staff/);
    const repeatedClear = await mutate(context, args.baseOrigin,
      `/api/asap/staff/additional-copies/${created.id}/clear-claim`, { version: assignedClaim.version });
    assert.equal(repeatedClear.status(), 409, await repeatedClear.text());
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Close task' }).click();
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Permanently delete task' }).click();
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
    const reopenPath = `**/api/asap/staff/additional-copies/${args.invalidClosedCopyId}/reopen`;
    const advancedReopenDetail = async route => {
      const response = await route.fetch();
      const committed = await response.json();
      assert.equal(committed.status, 'open');
      await route.fulfill({ response, json: { ...committed, finalStatus: 'closed' } });
    };
    await page.route(reopenPath, advancedReopenDetail);
    page.once('dialog', dialog => dialog.accept());
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
    assert.match(await page.locator('#app-status').textContent(), /Final state: Open/);
    await page.unroute(reopenPath, advancedReopenDetail);
    await page.getByText(/System cleared retained claim while reopening \(claimant_inactive\)/).waitFor();
    await scan(page, axeSource, args.artifactRoot, report, 'desktop', 'additional-copy-cleared-reopen');

    let postCommitClaimVersion;
    await page.route(`**/api/asap/staff/additional-copies/${args.invalidClosedCopyId}/claim`, async route => {
      const response = await route.fetch();
      const committed = await response.json();
      assert.equal(committed.committed, true);
      postCommitClaimVersion = committed.version;
      await route.fulfill({ response, json: { committed: true, request: null,
        finalStatus: committed.finalStatus, refreshUnavailable: true } });
    });
    await page.route(`**/api/asap/staff/additional-copies/${args.invalidClosedCopyId}`, route =>
      route.fulfill({ status: 401, contentType: 'application/json',
        body: JSON.stringify({ code: 'staff_session_invalid' }) }));
    await page.getByRole('button', { name: 'Claim', exact: true }).click();
    await page.locator('#signed-out').waitFor({ state: 'visible' });
    assert.match(await page.locator('#signed-out-message').textContent(),
      /Task claimed\. Final state: Open\..*Sign in again to review the committed task/);
    assert.ok(postCommitClaimVersion, 'The committed claim must return a version for cleanup');
    const unclaimAfterSessionTest = await mutate(context, args.baseOrigin,
      `/api/asap/staff/additional-copies/${args.invalidClosedCopyId}/unclaim`,
      { version: postCommitClaimVersion });
    assert.equal(unclaimAfterSessionTest.status(), 200, await unclaimAfterSessionTest.text());

    const laterPage = await context.newPage();
    laterPage.on('pageerror', error => errors.push(error.message));
    await laterPage.goto(`${args.baseOrigin}/staff/?stage=additional_copies`, { waitUntil: 'networkidle' });
    await laterPage.getByRole('button', { name: `Open additional-copy task ${args.invalidClosedCopyId}` }).click();
    await laterPage.getByRole('button', { name: 'Claim', exact: true }).click();
    await laterPage.getByRole('button', { name: 'Unclaim' }).waitFor();
    await laterPage.locator('#refresh-additional-copies').waitFor({ state: 'visible' });
    const laterClaimResponse = await context.request.get(
      `${args.baseOrigin}/api/asap/staff/additional-copies/${args.invalidClosedCopyId}`);
    const laterClaim = await laterClaimResponse.json();
    await laterPage.locator('#close-request').click();
    await laterPage.route(/\/api\/asap\/staff\/additional-copies\?/, route =>
      route.fulfill({ status: 401, contentType: 'application/json',
        body: JSON.stringify({ code: 'staff_session_invalid' }) }));
    await laterPage.locator('#refresh-additional-copies').click();
    await laterPage.locator('#signed-out').waitFor({ state: 'visible' });
    assert.doesNotMatch(await laterPage.locator('#signed-out-message').textContent(), /Final state:/,
      'a later unrelated sign-out must not replay the earlier committed copy result');
    const laterUnclaim = await mutate(context, args.baseOrigin,
      `/api/asap/staff/additional-copies/${args.invalidClosedCopyId}/unclaim`,
      { version: laterClaim.version });
    assert.equal(laterUnclaim.status(), 200, await laterUnclaim.text());
    await laterPage.close();

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
    let releaseUnclaimList;
    let listRequestStarted;
    let heldUnclaimList = false;
    const listRequestStartedPromise = new Promise(resolve => { listRequestStarted = resolve; });
    await page.route('**/api/asap/staff/additional-copies?**', async route => {
      if (heldUnclaimList) {
        await route.continue();
        return;
      }
      heldUnclaimList = true;
      await new Promise(resolve => {
        releaseUnclaimList = resolve;
        listRequestStarted();
      });
      await route.continue();
    });
    await page.getByRole('button', { name: 'Unclaim' }).click();
    await page.getByRole('button', { name: 'Claim', exact: true }).waitFor();
    await page.locator('#app-status').filter({ hasText: 'Task unclaimed.' }).waitFor();
    let listStartTimeout;
    try {
      await Promise.race([
        listRequestStartedPromise,
        new Promise((_, reject) => {
          listStartTimeout = setTimeout(() => reject(new Error('Additional-copy follow-up list did not start.')), 30000);
        })
      ]);
    } finally {
      clearTimeout(listStartTimeout);
    }
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(id =>
      document.activeElement?.getAttribute('aria-label') === `Open additional-copy task ${id}`,
    args.mobileCopyId);
    releaseUnclaimList();
    await page.waitForFunction(() => !document.getElementById('refresh-additional-copies').disabled);
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
    const uncertainResolutionPath = /\/api\/asap\/staff\/hold-operations\/\d+\/resolve$/;
    const readinessUnavailable = route => route.fulfill({ status: 503, contentType: 'application/json',
      body: JSON.stringify({ code: 'hold_resolution_dependency_unavailable',
        message: 'Hold resolution could not start because a dependency is unavailable. The operation was not changed.' }) });
    await page.route(uncertainResolutionPath, readinessUnavailable);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Resolve operation' }).click();
    await page.locator('#app-status').filter({ hasText: /The operation was not changed/ }).waitFor();
    assert.doesNotMatch(await page.locator('#app-status').textContent(), /outcome could not be confirmed/);
    await page.unroute(uncertainResolutionPath, readinessUnavailable);
    await fillOperatorResolution(page, true);
    const emptyResolutionReply = route => route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ committed: true,
        operationId: new URL(route.request().url()).pathname.match(/hold-operations\/(\d+)\/resolve$/)[1] }) });
    await page.route(uncertainResolutionPath, emptyResolutionReply);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Resolve operation' }).click();
    await page.locator('#app-status').filter({ hasText: /hold recovery outcome could not be confirmed/i }).waitFor();
    await page.unroute(uncertainResolutionPath, emptyResolutionReply);
    await fillOperatorResolution(page, true);
    const providerUncertain = route => route.fulfill({ status: 502, contentType: 'application/json',
      body: JSON.stringify({ code: 'hold_provider_error',
        message: 'The hold provider outcome could not be confirmed. Review the operation before retrying.' }) });
    await page.route(uncertainResolutionPath, providerUncertain);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Resolve operation' }).click();
    await page.locator('#app-status').filter({ hasText: /hold recovery outcome could not be confirmed/i }).waitFor();
    await page.getByText(/Operation \d+; attempt 2; epoch 3; frozen patron \*+2904; frozen BIB 92904\./).waitFor();
    await page.unroute(uncertainResolutionPath, providerUncertain);
    await fillOperatorResolution(page, true);
    page.once('dialog', dialog => dialog.accept());
    const [response] = await Promise.all([
      page.waitForResponse(candidate =>
        candidate.request().method() === 'POST' &&
        /\/api\/asap\/staff\/hold-operations\/\d+\/resolve$/.test(new URL(candidate.url()).pathname)),
      page.getByRole('button', { name: 'Resolve operation' }).click()
    ]);
    assert.equal(response.status(), 200, await response.text());
    await page.locator('#app-status').filter({ hasText: /Hold operation .* resolved as succeeded/ }).waitFor();
    const statusValue = page.locator('#request-dialog .status-badge');
    await statusValue.filter({ hasText: 'Hold placed' }).waitFor();
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

async function runClosedDeletionControls(browser, args, axeSource, report) {
  const highId = '9007199254741993';
  const nextId = '9007199254741994';
  const { context, traffic } = await createContext(
    browser, { width: 390, height: 844 }, args.baseOrigin, args.superIdentity
  );
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(args.baseOrigin + '/staff/', { waitUntil: 'networkidle' });
    const detailPath = args.baseOrigin + '/api/asap/staff/title-requests/' + args.staleTitleAId;
    const deletePath = args.baseOrigin + '/api/asap/staff/requests/' + args.staleTitleAId;
    const sourceResponse = await context.request.get(detailPath);
    assert.equal(sourceResponse.status(), 200);
    const source = await sourceResponse.json();
    const actorVersion = (await session(context, args.baseOrigin)).staff.version;
    const closedDetail = { ...source, status: 'closed', closeReason: 'rejected',
      capabilities: { ...source.capabilities, canChangeWorkflowState: true } };
    let singleDeletes = 0;
    const detailRoute = route => route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify(closedDetail) });
    const singleDeleteRoute = route => {
      singleDeletes += 1;
      assert.equal(route.request().postDataJSON().version, source.version);
      assert.equal(route.request().postDataJSON().actorVersion, actorVersion);
      return route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ deleted: true }) });
    };
    await page.route(detailPath, detailRoute);
    await page.route(deletePath, singleDeleteRoute);
    await page.goto(args.baseOrigin + '/staff/?request=' + args.staleTitleAId, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Permanently delete request' }).waitFor();
    assert.match(await page.locator('#request-dialog .detail-grid').textContent(),
      /Close reason\s*Rejected/);
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'single-closed-title-delete');
    page.once('dialog', dialog => {
      assert.match(dialog.message(), /cannot be undone/);
      return dialog.dismiss();
    });
    await page.getByRole('button', { name: 'Permanently delete request' }).click();
    assert.equal(singleDeletes, 0);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Permanently delete request' }).click();
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    assert.equal(singleDeletes, 1);
    await page.unroute(detailPath, detailRoute);
    await page.unroute(deletePath, singleDeleteRoute);
    await page.locator('[data-status="closed"]').click();
    await page.locator('#request-search').fill('a hidden unrelated filter');
    const title = {
      id: highId, type: 'title_request', title: 'Closed high ID title',
      status: 'closed', closeReason: 'rejected', version: 'AQIDBAUGBwg=',
      libraryOrgId: 2, libraryOrgName: 'Preview library'
    };
    const copy = {
      id: highId, type: 'additional_copy', title: 'Closed colliding copy',
      status: 'closed', version: 'AgMEBQYHCAk=',
      libraryOrgId: 2, libraryOrgName: 'Preview library'
    };
    const missingCopy = {
      ...copy, id: nextId, title: 'Closed missing copy', version: 'AwQFBgcICQo='
    };
    let titleLoads = 0;
    let copyLoads = 0;
    await page.route(/\/api\/asap\/staff\/title-requests\?scope=/, route => {
      titleLoads += 1;
      return route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ scope: 'all', organizations: [], items: [title] }) });
    });
    await page.route(/\/api\/asap\/staff\/additional-copies\?scope=/, route => {
      copyLoads += 1;
      return route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ scope: 'all', status: 'closed', availableLibraries: [],
          items: [copy, missingCopy] }) });
    });
    const sent = [];
    let sessionLoss = false;
    let actorChange = false;
    let timeoutFailure = false;
    await page.route(/\/api\/asap\/staff\/requests\/9007199254741993$/, route => {
      sent.push({ path: new URL(route.request().url()).pathname,
        version: route.request().postDataJSON().version,
        actorVersion: route.request().postDataJSON().actorVersion });
      return route.fulfill({ status: actorChange ? 409 : 200, contentType: 'application/json',
        body: JSON.stringify(actorChange ? { code: 'actor_changed_since_preview' } : { deleted: true }) });
    });
    await page.route(/\/api\/asap\/staff\/additional-copies\/900719925474199[34]$/, route => {
      const pathname = new URL(route.request().url()).pathname;
      sent.push({ path: pathname, version: route.request().postDataJSON().version,
        actorVersion: route.request().postDataJSON().actorVersion });
      return route.fulfill({ status: sessionLoss && pathname.endsWith(highId) ? 401
        : timeoutFailure && pathname.endsWith(highId) ? 503
        : pathname.endsWith(nextId) ? 404 : 409,
        contentType: 'application/json',
        body: JSON.stringify({ code: sessionLoss && pathname.endsWith(highId) ? 'staff_session_invalid'
          : timeoutFailure && pathname.endsWith(highId) ? 'request_outcome_unconfirmed'
          : pathname.endsWith(nextId) ? 'not_found' : 'stale_version' }) });
    });
    await page.getByRole('button', { name: 'Delete closed work...' }).click();
    await page.locator('#bulk-delete-dialog[open]').waitFor();
    assert.equal(await page.evaluate(() => document.activeElement.id), 'bulk-delete-scope');
    assert.equal(await page.locator('#bulk-delete-execute').isDisabled(), true);
    await page.locator('#bulk-delete-scope').selectOption('all');
    await page.getByRole('button', { name: 'Preview closed records' }).click();
    await page.locator('#bulk-delete-summary').filter({
      hasText: '1 closed title requests and 2 closed additional-copy tasks'
    }).waitFor();
    assert.equal(await page.locator('#bulk-delete-items li').count(), 3);
    assert.match(await page.locator('#bulk-delete-items').textContent(), /Title request 9007199254741993/);
    assert.match(await page.locator('#bulk-delete-items').textContent(), /Additional-copy task 9007199254741993/);
    assert.equal(await page.locator('#bulk-delete-execute').isDisabled(), true);
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'bulk-closed-preview');
    await page.locator('#bulk-delete-confirmation').fill('DELETE');
    await page.locator('#bulk-delete-execute').evaluate(button => {
      button.click();
      button.click();
    });
    await page.locator('#bulk-delete-results').filter({ hasText: 'Confirmed deleted: 1 of 3. Attempted: 3.' }).waitFor();
    assert.deepEqual(sent, [
      { path: '/api/asap/staff/requests/' + highId, version: title.version, actorVersion },
      { path: '/api/asap/staff/additional-copies/' + highId, version: copy.version, actorVersion },
      { path: '/api/asap/staff/additional-copies/' + nextId, version: missingCopy.version, actorVersion }
    ]);
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Title request 9007199254741993.*deleted/);
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Additional-copy task 9007199254741993.*stale/);
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Additional-copy task 9007199254741994.*not found/);
    assert.ok(titleLoads >= 2 && copyLoads >= 2, 'Both Closed views must refresh after partial deletion');
    await scan(page, axeSource, args.artifactRoot, report, 'mobile', 'bulk-closed-results');
    await page.keyboard.press('Escape');
    await page.locator('#bulk-delete-dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.evaluate(() => document.activeElement.id), 'bulk-delete-closed');
    actorChange = true;
    await page.getByRole('button', { name: 'Delete closed work...' }).click();
    await page.locator('#bulk-delete-scope').selectOption('all');
    await page.getByRole('button', { name: 'Preview closed records' }).click();
    await page.locator('#bulk-delete-items li').first().waitFor();
    await page.locator('#bulk-delete-confirmation').fill('DELETE');
    await page.locator('#bulk-delete-execute').click();
    await page.locator('#bulk-delete-results').filter({
      hasText: 'Confirmed deleted: 0 of 3. Attempted: 1.'
    }).waitFor();
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Title request 9007199254741993.*actor changed/);
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Additional-copy task 9007199254741993.*not attempted/);
    assert.equal(sent.length, 4, 'Actor change must stop the remaining batch');
    await page.keyboard.press('Escape');
    await page.locator('#bulk-delete-dialog').waitFor({ state: 'hidden' });
    actorChange = false;
    timeoutFailure = true;
    await page.getByRole('button', { name: 'Delete closed work...' }).click();
    await page.locator('#bulk-delete-scope').selectOption('all');
    await page.getByRole('button', { name: 'Preview closed records' }).click();
    await page.locator('#bulk-delete-items li').first().waitFor();
    await page.locator('#bulk-delete-confirmation').fill('DELETE');
    await page.locator('#bulk-delete-execute').click();
    await page.locator('#bulk-delete-results').filter({
      hasText: 'Confirmed deleted: 1 of 3. Attempted: 2.'
    }).waitFor();
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Additional-copy task 9007199254741993.*outcome unconfirmed/);
    assert.match(await page.locator('#bulk-delete-results').textContent(),
      /Additional-copy task 9007199254741994.*not attempted/);
    assert.equal(sent.length, 6, 'Unconfirmed outcome must stop the remaining batch');
    await page.keyboard.press('Escape');
    await page.locator('#bulk-delete-dialog').waitFor({ state: 'hidden' });
    timeoutFailure = false;
    sessionLoss = true;
    await page.goto(args.baseOrigin + '/staff/', { waitUntil: 'networkidle' });
    await page.locator('[data-status="closed"]').click();
    await page.getByRole('button', { name: 'Delete closed work...' }).click();
    await page.locator('#bulk-delete-dialog[open]').waitFor();
    await page.locator('#bulk-delete-scope').selectOption('all');
    await page.getByRole('button', { name: 'Preview closed records' }).click();
    await page.locator('#bulk-delete-items li').first().waitFor();
    await page.locator('#bulk-delete-confirmation').fill('DELETE');
    await page.locator('#bulk-delete-execute').click();
    await page.locator('#signed-out-message').filter({
      hasText: 'Confirmed deleted: 1 of 3. Attempted: 2.'
    }).waitFor();
    assert.match(await page.locator('#signed-out-message').textContent(),
      /Title request 9007199254741993.*deleted/);
    assert.match(await page.locator('#signed-out-message').textContent(),
      /Additional-copy task 9007199254741993.*forbidden\/out of scope/);
    assert.match(await page.locator('#signed-out-message').textContent(),
      /Additional-copy task 9007199254741994.*not attempted/);
    assert.deepEqual(errors, []);
    assert.equal(traffic.externalRequests, 0);
  } finally {
    await context.close();
  }

  const ordinary = await createContext(browser, { width: 1280, height: 900 },
    args.baseOrigin, args.staffIdentity);
  try {
    const staffPage = await ordinary.context.newPage();
    await staffPage.goto(args.baseOrigin + '/staff/', { waitUntil: 'networkidle' });
    await staffPage.locator('[data-status="closed"]').click();
    assert.equal(await staffPage.locator('#bulk-delete-closed').isHidden(), true);
    const detail = await ordinary.context.request.get(
      args.baseOrigin + '/api/asap/staff/title-requests/' + args.staleTitleAId);
    assert.equal(detail.status(), 200);
    const denied = await mutateDelete(ordinary.context, args.baseOrigin,
      '/api/asap/staff/requests/' + args.staleTitleAId, { version: (await detail.json()).version });
    assert.equal(denied.status(), 403);
    assert.equal((await denied.json()).code, 'delete_forbidden');
  } finally {
    await ordinary.context.close();
  }
}

async function runStaffSuggestion(browser, args, axeSource, report) {
  const scopedState = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.staffIdentity
  );
  const scopedPage = await scopedState.context.newPage();
  const scopedErrors = [];
  scopedPage.on('pageerror', error => scopedErrors.push(error.message));
  try {
    await scopedPage.goto(`${args.baseOrigin}/staff/`, { waitUntil: 'networkidle' });
    await scopedPage.locator('#workspace').waitFor({ state: 'visible' });
    await scopedPage.getByRole('button', { name: 'New suggestion', exact: true }).click();
    await scopedPage.locator('#staff-suggestion-dialog[open]').waitFor();
    const scopedLibrary = scopedPage.getByLabel('Servicing library', { exact: true });
    assert.equal(await scopedLibrary.isDisabled(), true, 'Ordinary staff must not edit suggestion scope');
    assert.equal(await scopedLibrary.inputValue(), '2');
    const scopedSession = await session(scopedState.context, args.baseOrigin);
    const forbiddenResponse = await scopedState.context.request.post(
      `${args.baseOrigin}/api/asap/staff/patron-lookup`,
      {
        headers: { 'X-ASAP-Antiforgery': scopedSession.antiforgeryToken },
        data: { query: '20000000000001', barcode: null, libraryOrgId: 3 }
      }
    );
    assert.equal(forbiddenResponse.status(), 403, await forbiddenResponse.text());
    const forbidden = await forbiddenResponse.json();
    assert.equal(forbidden.code, 'staff_scope_forbidden');
    assert.match(forbidden.message, /outside your authorized scope/i);
    await scopedPage.keyboard.press('Escape');
    await scopedPage.locator('#staff-suggestion-dialog').waitFor({ state: 'hidden' });
    assert.deepEqual(scopedErrors, [], `Ordinary staff suggestion flow raised an error: ${scopedErrors.join('; ')}`);
    assert.equal(scopedState.traffic.externalRequests, 0, 'Ordinary staff suggestion flow requested an external asset');
  } finally {
    await scopedState.context.close();
  }

  const raceState = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const racePage = await raceState.context.newPage();
  const raceErrors = [];
  const releaseConfiguration = deferred();
  const configurationRequested = deferred();
  const configurationResponse = await raceState.context.request.get(
    `${args.baseOrigin}/api/asap/staff/suggestion-configuration?libraryOrgId=2`
  );
  assert.equal(configurationResponse.status(), 200, await configurationResponse.text());
  const configurationBody = await configurationResponse.json();
  const delayedConfiguration = async route => {
    configurationRequested.resolve();
    await releaseConfiguration.promise;
    try {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(configurationBody)
      });
    } catch {
      // The browser is expected to abort this response after the scope changes.
    }
  };
  racePage.on('pageerror', error => raceErrors.push(error.message));
  await racePage.route('**/api/asap/staff/suggestion-configuration?*', delayedConfiguration);
  try {
    await racePage.goto(`${args.baseOrigin}/staff/`, { waitUntil: 'networkidle' });
    await racePage.locator('#workspace').waitFor({ state: 'visible' });
    await racePage.getByRole('button', { name: 'New suggestion', exact: true }).click();
    await racePage.locator('#staff-suggestion-dialog[open]').waitFor();
    const raceScope = racePage.getByLabel('Servicing library', { exact: true });
    await raceScope.selectOption('2');
    await racePage.getByLabel('Patron barcode or name', { exact: true }).fill('20000000000001');
    await racePage.getByRole('button', { name: 'Look up patron', exact: true }).click();
    await configurationRequested.promise;
    await raceScope.selectOption('');
    releaseConfiguration.resolve();
    await racePage.getByText('Choose a servicing library.', { exact: true }).waitFor();
    assert.equal(
      await racePage.getByRole('button', { name: 'Create suggestion', exact: true }).count(),
      0,
      'A stale configuration response must not render a creation form'
    );
    assert.deepEqual(raceErrors, [], `Suggestion configuration race raised an error: ${raceErrors.join('; ')}`);
    assert.equal(raceState.traffic.externalRequests, 0, 'Suggestion configuration race requested an external asset');
  } finally {
    releaseConfiguration.resolve();
    await racePage.unroute('**/api/asap/staff/suggestion-configuration?*', delayedConfiguration);
    await raceState.context.close();
  }

  const titleSeed = `Browser on behalf ${Date.now()}`;
  const desktopState = await createContext(
    browser,
    { width: 1280, height: 900 },
    args.baseOrigin,
    args.superIdentity
  );
  const desktopPage = await desktopState.context.newPage();
  const desktopErrors = [];
  let suggestionPosts = 0;
  desktopPage.on('pageerror', error => desktopErrors.push(error.message));
  desktopPage.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/suggestions') {
      suggestionPosts += 1;
    }
  });
  try {
    await desktopPage.goto(`${args.baseOrigin}/staff/?scope=2`, { waitUntil: 'networkidle' });
    await desktopPage.locator('#workspace').waitFor({ state: 'visible' });
    await desktopPage.getByRole('button', { name: 'New suggestion', exact: true }).click();
    await desktopPage.locator('#staff-suggestion-dialog[open]').waitFor();
    const scope = desktopPage.getByLabel('Servicing library', { exact: true });
    assert.equal(await scope.inputValue(), '', 'Super-admin suggestion scope must start blank');
    await desktopPage.waitForFunction(() => document.activeElement?.getAttribute('aria-label') === 'Servicing library');
    assert.equal(await desktopPage.evaluate(() => document.activeElement.getAttribute('aria-label')), 'Servicing library');
    await scope.selectOption('2');
    await desktopPage.getByLabel('Patron barcode or name', { exact: true }).fill('MULTIPLE');
    await desktopPage.getByRole('button', { name: 'Look up patron', exact: true }).click();
    await desktopPage.getByRole('button', { name: /Alex Example/ }).waitFor();
    assert.equal(await desktopPage.getByRole('button', { name: /Avery Example/ }).count(), 1);
    await desktopPage.getByRole('button', { name: /Avery Example/ }).click();

    const format = desktopPage.getByLabel('Material format', { exact: true });
    await format.waitFor();
    const title = desktopPage.locator('#staff-suggestion-form label')
      .filter({ hasText: /title/i }).locator('input').first();
    await title.fill(titleSeed);
    const audience = desktopPage.getByLabel('Audience note', { exact: true });
    await audience.waitFor();
    await audience.fill('Created by the staff browser journey.');

    await format.selectOption('ebook');
    const formatNotice = desktopPage.locator('.staff-format-notice');
    await formatNotice.waitFor({ state: 'visible' });
    assert.match(await formatNotice.textContent(), /This is an eBook suggestion/);
    assert.equal(await formatNotice.locator('p').count(), 2);
    assert.equal(await formatNotice.locator('a').getAttribute('href'),
      'https://help.libbyapp.com/en-us/6260.htm');
    assert.equal((await formatNotice.textContent()).includes('<p>'), false);
    assert.equal(await desktopPage.getByRole('button', { name: 'Create suggestion', exact: true }).isDisabled(), false);
    await format.selectOption('book');
    assert.equal(await audience.isVisible(), true);
    await audience.fill('Created by the staff browser journey.');
    const publication = desktopPage.locator('#staff-suggestion-form label')
      .filter({ hasText: /publication timing/i }).locator('select').first();
    await publication.selectOption({ index: 1 });
    const binding = desktopPage.locator('#staff-suggestion-form label')
      .filter({ hasText: /binding/i }).locator('select').first();
    await binding.selectOption({ index: 1 });

    await desktopPage.getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    await desktopPage.locator('#polaris-dialog[open]').waitFor();
    await desktopPage.locator('#polaris-mode').selectOption('title');
    await desktopPage.locator('#polaris-query').fill('Browser staff suggestion catalog');
    await desktopPage.locator('#polaris-form button[type=submit]').click();
    await desktopPage.getByRole('button', { name: 'Use BIB 9001' }).waitFor();
    await desktopPage.getByRole('button', { name: 'Use BIB 9001' }).click();
    await desktopPage.locator('#polaris-dialog').waitFor({ state: 'hidden' });
    assert.match(await title.inputValue(), /Catalog title 9001/);
    assert.equal(await desktopPage.getByLabel('Email patron confirmation (optional)', { exact: true }).isChecked(), false);
    assert.equal(await desktopPage.getByLabel('Automatically place hold', { exact: true }).isChecked(), true);
    const invalidSuggestionFields = await desktopPage.locator('#staff-suggestion-form').evaluate(form =>
      [...form.elements]
        .filter(element => !element.checkValidity())
        .map(element => ({
          name: element.name,
          type: element.type,
          value: element.value,
          label: element.closest('label')?.textContent?.trim() || element.getAttribute('aria-label')
        })));
    assert.deepEqual(invalidSuggestionFields, [], 'Configured suggestion fields must be valid before submit');
    await scan(desktopPage, axeSource, args.artifactRoot, report, 'desktop', 'staff-suggestion-editor');

    const createResponsePromise = desktopPage.waitForResponse(response =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === '/api/asap/staff/suggestions');
    await desktopPage.evaluate(() => {
      const form = document.querySelector('#staff-suggestion-form');
      form.requestSubmit();
      form.requestSubmit();
    });
    const createResponse = await createResponsePromise;
    const created = await createResponse.json();
    assert.equal(createResponse.status(), 201, JSON.stringify(created));
    assert.equal(typeof created.id, 'string');
    assert.equal(suggestionPosts, 1, 'Double submit must issue one staff suggestion request');
    await desktopPage.locator('#staff-suggestion-dialog').waitFor({ state: 'hidden' });
    await desktopPage.locator('#request-dialog[open]').waitFor();
    assert.match(desktopPage.url(), /[?&]request=[0-9]+$/);
    assert.match(await desktopPage.locator('#request-dialog-title').textContent(), /Catalog title 9001/i);
    await scan(desktopPage, axeSource, args.artifactRoot, report, 'desktop', 'staff-suggestion-created');
    assert.deepEqual(desktopErrors, [], `Staff suggestion browser flow raised an error: ${desktopErrors.join('; ')}`);
    assert.equal(desktopState.traffic.externalRequests, 0, 'Staff suggestion browser flow requested an external asset');
  } finally {
    await desktopState.context.close();
  }

  const mobileState = await createContext(
    browser,
    { width: 390, height: 844 },
    args.baseOrigin,
    args.superIdentity
  );
  const mobilePage = await mobileState.context.newPage();
  const mobileErrors = [];
  mobilePage.on('pageerror', error => mobileErrors.push(error.message));
  try {
    await mobilePage.goto(`${args.baseOrigin}/staff/?scope=2`, { waitUntil: 'networkidle' });
    await mobilePage.locator('#workspace').waitFor({ state: 'visible' });
    await mobilePage.getByRole('button', { name: 'New suggestion', exact: true }).click();
    await mobilePage.locator('#staff-suggestion-dialog[open]').waitFor();
    const mobileScope = mobilePage.getByLabel('Servicing library', { exact: true });
    assert.equal(await mobileScope.inputValue(), '', 'Mobile super-admin suggestion scope must start blank');
    await mobileScope.selectOption('2');
    await mobilePage.getByLabel('Patron barcode or name', { exact: true }).fill('MULTIPLE');
    await mobilePage.getByRole('button', { name: 'Look up patron', exact: true }).click();
    await mobilePage.getByRole('button', { name: /Alex Example/ }).waitFor();
    await scan(mobilePage, axeSource, args.artifactRoot, report, 'mobile', 'staff-suggestion-matches');
    await mobilePage.getByRole('button', { name: /Alex Example/ }).click();
    await mobilePage.getByLabel('Material format', { exact: true }).waitFor();
    await scan(mobilePage, axeSource, args.artifactRoot, report, 'mobile', 'staff-suggestion-editor');
    await mobilePage.keyboard.press('Escape');
    await mobilePage.locator('#staff-suggestion-dialog').waitFor({ state: 'hidden' });
    assert.deepEqual(mobileErrors, [], `Mobile staff suggestion flow raised an error: ${mobileErrors.join('; ')}`);
    assert.equal(mobileState.traffic.externalRequests, 0, 'Mobile staff suggestion flow requested an external asset');
  } finally {
    await mobileState.context.close();
  }
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
    await runStaffSuggestion(browser, args, axeSource, report);
    await runClosedDeletionControls(browser, args, axeSource, report);
    assert.equal(report.states.length, 29, 'Expected twenty-nine major staff browser states');
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
