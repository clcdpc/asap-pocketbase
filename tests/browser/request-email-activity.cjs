'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

const requiredScenarios = ['actualSuppression', 'sourceAndTickOrdering', 'safeText', 'activityRetention',
  'loadingErrorReload', 'exactOlderReference', 'clearReference', 'referenceOnlyStaff', 'unauthorizedReferenceDenied',
  'dirtyCancelKeepsDraft', 'dirtyDiscardNavigates', 'testRecoveryUnchanged', 'staleDetailRetired',
  'sessionEpochRetired', 'focusMovementPreserved', 'noNotesContamination'];
const canonicalId = value => typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
  (value.length < 19 || value.length === 19 && value <= '9223372036854775807');

async function main() {
  const [origin, artifacts] = process.argv.slice(2);
  assert.ok(origin && artifacts && process.argv.length === 4, 'Usage: request-email-activity.cjs origin artifacts');
  const base = new URL(origin);
  assert.equal(base.origin, origin);
  assert.ok(['http:', 'https:'].includes(base.protocol));
  assert.equal(base.username + base.password, '');
  await fs.mkdir(artifacts, { recursive: true });
  const fixture = JSON.parse(await fs.readFile(path.join(artifacts, 'fixture.json'), 'utf8'));
  for (const value of [...Object.values(fixture.requests), ...Object.values(fixture.references), fixture.collisionId]) {
    assert.ok(canonicalId(value), `Fixture identity must remain an exact Int64 string: ${value}`);
  }
  assert.ok(BigInt(fixture.references.confirmation) > BigInt(Number.MAX_SAFE_INTEGER));
  assert.ok(fixture.newerUnrelatedOperationCount > 500);
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const contexts = [], releases = new Set();
  const report = { externalRequests: 0, pageErrors: [], writes: 0, scenarios: {}, states: [],
    detailReads: [], emailReads: [], exactTestReads: [], readinessReads: [], apiAssertions: [], gates: [], cleanupErrors: [] };
  const headerFor = actor => ({ 'X-ASAP-Test-Staff-Id': actor.id, 'X-ASAP-Test-Tenant-Id': actor.tenantId,
    'X-ASAP-Test-Staff-Email': actor.email });
  const primary = fixture.requests.primary;
  const detailPath = id => `/api/asap/staff/title-requests/${id}`;
  const isHistory = response => response.request().method() === 'GET' && new URL(response.url()).pathname === '/api/asap/staff/email-operations';
  const isDetail = (response, id) => response.request().method() === 'GET' && new URL(response.url()).pathname === detailPath(id);
  const isExactReference = reference => url => url.pathname === '/api/asap/staff/email-operations' &&
    url.searchParams.get('scope') === fixture.library && url.searchParams.get('reference') === reference;
  const requestDialog = page => page.locator('#request-dialog');
  const activity = page => requestDialog(page).locator('.request-activity');
  const activityRows = page => activity(page).locator(':scope > ol > li');
  const notes = page => requestDialog(page).locator('.edit-form textarea[maxlength="10000"]');
  const referenceButton = (page, reference = fixture.references.confirmation) =>
    activity(page).getByRole('button', { name: `Open email operation ${reference}`, exact: true }).first();

  function watch(page) {
    page.setDefaultTimeout(15000);
    page.setDefaultNavigationTimeout(20000);
    page.on('pageerror', error => report.pageErrors.push(error.message));
    rejectNativeDialogs(page, report.pageErrors);
    page.on('request', request => {
      if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) report.writes++;
    });
    page.on('response', response => {
      if (response.request().method() !== 'GET') return;
      const url = new URL(response.url());
      if (/^\/api\/asap\/staff\/title-requests\/\d+$/.test(url.pathname)) {
        report.detailReads.push({ path: url.pathname, scope: url.searchParams.get('scope'), status: response.status() });
      } else if (url.pathname === '/api/asap/staff/email-operations') {
        report.emailReads.push({ ...Object.fromEntries(url.searchParams), statusCode: response.status() });
      } else if (url.pathname.startsWith('/api/asap/staff/email-operations/') &&
          (url.pathname.endsWith('/test') || /\/\d+$/.test(url.pathname))) {
        report.exactTestReads.push({ path: url.pathname, ...Object.fromEntries(url.searchParams), statusCode: response.status() });
      } else if (url.pathname.endsWith('/email-operations/test-context') || url.pathname.endsWith('/email-operations/readiness')) {
        report.readinessReads.push({ path: url.pathname, ...Object.fromEntries(url.searchParams), statusCode: response.status() });
      }
    });
  }

  async function configureContext(context, { recovery = false } = {}) {
    contexts.push(context);
    context.setDefaultTimeout(15000);
    context.setDefaultNavigationTimeout(20000);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++;
      return route.abort('blockedbyclient');
    });
    await context.addInitScript(recovered => {
      // A new document is a new presentation/session owner. This public probe
      // identifies reloads without inspecting private application controllers.
      window.__emailActivityDocument = crypto.randomUUID();
      if (recovered && !sessionStorage.getItem(recovered.storageKey)) {
        sessionStorage.setItem(recovered.storageKey, JSON.stringify(recovered.record));
      }
    }, recovery ? fixture.recovery : null);
  }

  async function newPage(profile = 'superAdmin', { width = 1280, height = 900, theme = 'light', recovery = false } = {}) {
    const context = await browser.newContext({ viewport: { width, height }, locale: 'en-US',
      timezoneId: 'America/New_York', colorScheme: theme, extraHTTPHeaders: headerFor(fixture.profiles[profile]) });
    await configureContext(context, { recovery });
    const page = await context.newPage();
    watch(page);
    return { page, context };
  }

  function queueUrl(id = null) {
    const url = new URL('/staff/', origin);
    url.searchParams.set('stage', 'suggestion'); url.searchParams.set('scope', fixture.library);
    if (id) url.searchParams.set('request', id);
    return url.href;
  }

  function originalOperationsUrl() {
    const url = new URL('/staff/', origin);
    for (const [key, value] of Object.entries({ stage: 'operations', opSection: 'email', emailScope: 'system',
      emailTestScope: fixture.otherLibrary, emailSearch: 'excluded-original-history', emailStatus: 'failed',
      emailType: 'operational_test', emailFrom: '2030-01-01', emailThrough: '2030-01-02', emailSort: 'oldest', emailPage: '6' })) {
      url.searchParams.set(key, value);
    }
    return url.href;
  }

  async function queueReady(page) {
    await page.locator('#workspace:not([hidden])').waitFor();
    await page.locator('#queue-view:not([hidden])').waitFor();
    await page.locator('#refresh-queue:not([disabled])').waitFor();
    await page.waitForFunction(() => document.querySelector('#request-grid a[data-request-id]') ||
      document.querySelector('#queue-empty')?.getClientRects().length > 0);
  }

  async function enterQueue(page) {
    await page.locator('[data-view="queue"]').click();
    await queueReady(page);
    await page.locator('#claim-filter').selectOption('all');
    await page.locator('#request-search').fill('');
  }

  async function showActivity(page, id = primary) {
    await page.locator(`#request-${id}-activity-tab`).waitFor();
    await page.locator(`#request-${id}-activity-tab`).click();
    await page.locator(`#request-${id}-activity`).waitFor({ state: 'visible' });
  }

  async function openRequest(page, id = primary) {
    await page.locator('#claim-filter').selectOption('all');
    await page.locator(`#request-grid a[data-request-id="${id}"]`).first().click();
    await requestDialog(page).waitFor({ state: 'visible' });
    await showActivity(page, id);
  }

  async function closeRequest(page) {
    await page.locator('#close-request').click();
    await requestDialog(page).waitFor({ state: 'hidden' });
  }

  async function settledHistory(page) {
    await page.locator('#operations-email-panel').waitFor({ state: 'visible' });
    await page.waitForFunction(() => {
      const table = document.querySelector('#email-operations-table');
      return table && !table.hasAttribute('aria-busy') && Boolean(table.querySelector('table'));
    });
  }

  async function twoFrames(page) {
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }

  async function apiGet(context, pathname, expectedStatus = 200) {
    const response = await context.request.get(`${origin}${pathname}`);
    const body = await response.text();
    assert.equal(response.status(), expectedStatus, `${pathname}: ${body}`);
    report.apiAssertions.push({ path: pathname, status: response.status() });
    let json = null;
    if (body) { try { json = JSON.parse(body); } catch { assert.fail(`Expected JSON from ${pathname}: ${body}`); } }
    return { body, json };
  }

  async function assertActivity(page, profile = 'superAdmin') {
    const expected = fixture.expectedActivity[profile];
    assert.deepEqual(await activityRows(page).evaluateAll(rows => rows.map(row => row.dataset.activityId)),
      fixture.expectedDisplayOrdering, `${profile}: display reverses authoritative SQL chronology across sources and exact IDs`);
    assert.deepEqual(expected.map(item => `${item.source}:${item.id}`), fixture.expectedApiOrdering);
    assert.deepEqual([...fixture.expectedApiOrdering].reverse(), fixture.expectedDisplayOrdering);
    assert.equal(new Set(fixture.expectedDisplayOrdering).size, fixture.expectedDisplayOrdering.length);
    for (const entry of expected) {
      const row = activity(page).locator(`[data-activity-id="${entry.source}:${entry.id}"]`);
      assert.equal(await row.count(), 1, `${entry.source}:${entry.id}`);
      if (!entry.email) {
        if (entry.message) assert.ok((await row.textContent()).includes(entry.message));
        continue;
      }
      const email = entry.email;
      assert.equal(await row.locator(':scope > strong').textContent(), email.messageType);
      assert.equal(await row.locator('.request-email-outcome').textContent(), email.outcomeLabel);
      const fields = await row.locator('.request-email-fields > div').evaluateAll(values => Object.fromEntries(values.map(value =>
        [value.querySelector('dt').textContent, value.querySelector('dd').textContent])));
      assert.equal(fields['Intended recipient'], email.intendedRecipient || 'Recipient not recorded');
      if (email.reason) assert.equal(fields['Recorded reason'], email.reason);
      const time = row.locator('time');
      assert.equal(await time.getAttribute('datetime'), entry.created);
      const local = await page.evaluate(value => new Date(value).toLocaleString(), entry.created);
      assert.equal(await time.textContent(), local);
      const labels = { transition: 'Recorded', created: 'Created', last_attempt: 'Last attempt', sent: 'Sent', suppressed: 'Suppressed', recorded: 'Recorded' };
      assert.ok((await row.locator('.request-email-recorded-time').textContent()).startsWith(`${labels[email.timestampKind]}: `));
      assert.equal(await row.locator('.request-email-history-note').count(), email.isRecordedSummary ? 1 : 0);
      assert.equal(await row.locator('.request-email-reference code').textContent(), email.operationReference);
      assert.equal(await row.locator('.request-email-reference-link').count(), email.operationsScope ? 1 : 0);
      if (email.outcome === 'provider_accepted') assert.match(email.outcomeLabel, /delivery unconfirmed/i);
      if (email.outcome === 'captured') {
        assert.match(email.outcomeLabel, /captured to file; no live email sent/i);
        assert.equal(fields['Delivery mode'], 'File capture; no live email sent');
      }
      assert.notEqual(email.outcome, 'delivered', 'This fixture records acceptance/capture, never confirmed delivery.');
    }
    assert.equal(await activity(page).locator('img, svg, script, b').count(), 0, 'Runtime activity markup remains text.');
    const rendered = await requestDialog(page).textContent();
    for (const forbidden of fixture.forbiddenText) assert.equal(rendered.includes(forbidden), false, `Private value leaked: ${forbidden}`);
    assert.equal(await notes(page).inputValue(), fixture.savedNotes, 'Email evidence is separate from editable Notes.');
    return expected;
  }

  async function revealActivityFocus(page, name) {
    const entry = fixture.expectedActivity.superAdmin.find(item => item.email?.operationReference === fixture.references.confirmation);
    assert.equal(entry.email.operationsScope, fixture.library);
    const card = activity(page).locator(`[data-activity-id="${entry.source}:${entry.id}"]`);
    const link = card.getByRole('button', { name: `Open email operation ${entry.email.operationReference}`, exact: true });
    const tab = page.locator(`#request-${primary}-activity-tab`);
    const pane = requestDialog(page).locator('.request-middle');
    await tab.click();
    const beforeScroll = await pane.evaluate(node => node.scrollTop);
    await card.scrollIntoViewIfNeeded();
    await twoFrames(page);
    const cardScroll = await pane.evaluate(node => node.scrollTop);
    assert.ok(cardScroll > beforeScroll + 1, `${name}: the real request pane scrolls from its tab to the email entry.`);

    // Start at the selected tab, then let keyboard navigation reveal the
    // reference itself. Do not repair an obscured target after Tab reaches it.
    await tab.click();
    let tabsToReference = 0;
    while (!await link.evaluate(node => document.activeElement === node) && tabsToReference < 80) {
      await page.keyboard.press('Tab'); tabsToReference++;
    }
    assert.equal(await link.evaluate(node => document.activeElement === node), true, `${name}: Tab reaches the authorized exact reference.`);
    await twoFrames(page);
    const proof = await link.evaluate((node, scroll) => {
      const pane = node.closest('.request-middle'), card = node.closest('.request-activity-email');
      const bounds = element => {
        const rect = element.getBoundingClientRect();
        return { left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height };
      };
      const intersect = (...rects) => {
        const left = Math.max(...rects.map(rect => rect.left)), top = Math.max(...rects.map(rect => rect.top));
        const right = Math.min(...rects.map(rect => rect.right)), bottom = Math.min(...rects.map(rect => rect.bottom));
        return { left, top, right, bottom, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
      };
      const viewport = { left: 0, top: 0, right: document.documentElement.clientWidth, bottom: innerHeight };
      const paneBounds = bounds(pane);
      const port = { left: paneBounds.left + pane.clientLeft, top: paneBounds.top + pane.clientTop,
        right: paneBounds.left + pane.clientLeft + pane.clientWidth, bottom: paneBounds.top + pane.clientTop + pane.clientHeight };
      const visiblePane = intersect(port, viewport), cardBounds = bounds(card), referenceBounds = bounds(node);
      const style = getComputedStyle(node), outlineWidth = parseFloat(style.outlineWidth), outlineOffset = parseFloat(style.outlineOffset);
      const ring = outlineWidth + Math.max(0, outlineOffset);
      const x = (referenceBounds.left + referenceBounds.right) / 2, y = (referenceBounds.top + referenceBounds.bottom) / 2;
      const hit = document.elementFromPoint(x, y);
      return { ...scroll, viewport, operationReference: card.querySelector('.request-email-reference code').textContent,
        activityId: card.dataset.activityId, messageType: card.querySelector(':scope > strong').textContent,
        outcome: card.querySelector('.request-email-outcome').textContent,
        pane: { bounds: paneBounds, port, visible: visiblePane, scrollTop: pane.scrollTop,
          scrollHeight: pane.scrollHeight, clientHeight: pane.clientHeight, overflowY: getComputedStyle(pane).overflowY },
        card: { bounds: cardBounds, visible: intersect(cardBounds, port, viewport) },
        reference: { bounds: referenceBounds, visible: intersect(referenceBounds, port, viewport),
          focused: document.activeElement === node, focusVisible: node.matches(':focus-visible'), outlineStyle: style.outlineStyle,
          outlineWidth, outlineOffset, focusRingContained: referenceBounds.left - ring >= visiblePane.left - 1 &&
            referenceBounds.right + ring <= visiblePane.right + 1 && referenceBounds.top - ring >= visiblePane.top - 1 &&
            referenceBounds.bottom + ring <= visiblePane.bottom + 1,
          hit: hit ? { tag: hit.tagName, id: hit.id, className: hit.className } : null,
          unobscured: hit === node || node.contains(hit) } };
    }, { beforeScroll, cardScroll, tabsToReference });
    assert.equal(proof.operationReference, entry.email.operationReference);
    assert.equal(proof.activityId, `${entry.source}:${entry.id}`);
    assert.equal(proof.messageType, entry.email.messageType);
    assert.equal(proof.outcome, entry.email.outcomeLabel);
    assert.ok(['auto', 'scroll'].includes(proof.pane.overflowY) && proof.pane.scrollTop > 0, `${name}: the inner pane owns scrolling.`);
    assert.ok(proof.card.visible.width > 0 && proof.card.visible.height >= Math.min(100, proof.card.bounds.height, proof.pane.visible.height),
      `${name}: a substantial part of the email entry intersects both the pane and viewport; the whole long entry need not fit.`);
    assert.ok(proof.reference.visible.width > 0 && proof.reference.visible.height > 0 && proof.reference.unobscured,
      `${name}: the focused reference is visible and passes the actual viewport hit test.`);
    assert.equal(proof.reference.focused && proof.reference.focusVisible && proof.reference.focusRingContained, true,
      `${name}: keyboard focus and its ring remain inside the visible pane.`);
    assert.notEqual(proof.reference.outlineStyle, 'none');
    assert.ok(proof.reference.outlineWidth > 0);
    return proof;
  }

  async function scan(page, name, { nativeViewport = false, mobile = false, desktop = false } = {}) {
    const activityFocus = mobile || desktop ? await revealActivityFocus(page, name) : null;
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)));
    const layout = await page.evaluate(() => {
      const dialog = document.querySelector('#request-dialog'), activity = dialog.querySelector('.request-activity');
      return { width: document.documentElement.clientWidth, scrollWidth: document.documentElement.scrollWidth,
        dialogWidth: dialog.getBoundingClientRect().width, dialogScrollWidth: dialog.scrollWidth, dialogClientWidth: dialog.clientWidth,
        activityScrollWidth: activity.scrollWidth, activityClientWidth: activity.clientWidth,
        imagesLoaded: [...document.images].filter(image => {
          const box = image.getBoundingClientRect(); return box.width > 0 && box.height > 0;
        }).every(image => image.complete && image.naturalWidth > 0),
        activeTab: dialog.querySelector('[role="tab"][aria-selected="true"]')?.textContent };
    });
    if (mobile) layout.mobileActivity = activityFocus;
    if (desktop) layout.desktopActivity = activityFocus;
    let capture = null;
    if (nativeViewport) {
      const cdp = await page.context().newCDPSession(page);
      try {
        assert.equal((await cdp.send('Page.getLayoutMetrics')).visualViewport.zoom, 2);
        const viewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, ratio: devicePixelRatio,
          htmlZoom: getComputedStyle(document.documentElement).zoom }));
        assert.equal(viewport.htmlZoom, '1');
        const result = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        const buffer = Buffer.from(result.data, 'base64');
        assert.equal(buffer.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
        const width = buffer.readUInt32BE(16), height = buffer.readUInt32BE(20);
        const expectedWidth = Math.round(viewport.width * viewport.ratio), expectedHeight = Math.round(viewport.height * viewport.ratio);
        assert.ok(Math.abs(width - expectedWidth) <= 1 && Math.abs(height - expectedHeight) <= 1,
          `Native capture ${width}x${height} must match physical viewport ${expectedWidth}x${expectedHeight}.`);
        capture = { method: 'cdp-viewport', width, height, cssWidth: viewport.width, cssHeight: viewport.height,
          expectedWidth, expectedHeight, devicePixelRatio: viewport.ratio, htmlZoom: viewport.htmlZoom };
        assert.ok(width >= viewport.width * 1.9);
        await fs.writeFile(path.join(artifacts, `${name}.png`), buffer);
      } finally { await cdp.detach(); }
    } else await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, violations, layout, capture });
    await fs.writeFile(path.join(artifacts, `${name}-layout.json`), JSON.stringify({ violations, layout, capture }, null, 2));
    assert.deepEqual(violations, [], name);
    assert.ok(layout.scrollWidth <= layout.width, `Page overflow: ${name}`);
    assert.ok(layout.dialogWidth <= layout.width && layout.dialogScrollWidth <= layout.dialogClientWidth + 1, `Dialog overflow: ${name}`);
    assert.ok(layout.activityScrollWidth <= layout.activityClientWidth + 1, `Activity overflow: ${name}`);
    assert.equal(layout.imagesLoaded, true, `Unexpected missing image: ${name}`);
    assert.equal(layout.activeTab, 'Activity');
  }

  async function holdRealRead(page, predicate, label) {
    let start, failStart, finish, release;
    const started = new Promise((resolve, reject) => { start = resolve; failStart = reject; });
    // The route may fail while the triggering Playwright action is settling.
    // Preserve its real error for started(), without an unhandled rejection.
    started.catch(() => {});
    const finished = new Promise(resolve => { finish = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    let used = false;
    const evidence = { name: label, actualStatus: null, releasedAfterRetirement: false, aborted: false };
    const unblock = () => { releases.delete(unblock); release(); };
    releases.add(unblock);
    const handler = async route => {
      if (used || route.request().method() !== 'GET' || !predicate(new URL(route.request().url()))) return route.fallback();
      used = true;
      try {
        const response = await route.fetch();
        evidence.actualStatus = response.status();
        assert.equal(response.status(), 200, `${label}: gate must be backed by a real successful SQL read.`);
        evidence.url = route.request().url(); evidence.data = await response.json();
        start(); await gate;
        try { await route.fulfill({ response }); }
        catch (error) {
          if (route.request().failure() || page.isClosed() || /Target.*closed|already handled|Invalid InterceptionId|cancelled|canceled|aborted/i.test(error.message)) evidence.aborted = true;
          else throw error;
        }
      } catch (error) {
        evidence.error = error.message;
        failStart(error);
        try { await route.abort('failed'); } catch { /* A retired request is already closed. */ }
      } finally { finish(); }
    };
    await page.route('**/api/asap/staff/**', handler);
    report.gates.push(evidence);
    return { evidence,
      async started() { await bounded(started, `${label} did not reach its real read.`); },
      async release() {
        unblock(); await bounded(finished, `${label} did not finish after release.`);
        await page.unroute('**/api/asap/staff/**', handler);
        assert.equal(evidence.error, undefined, `${label}: ${evidence.error}`);
      },
      unblock };
  }

  async function bounded(promise, message) {
    let timer;
    try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), 12000); })]); }
    finally { clearTimeout(timer); }
  }

  async function recoveryIdentity(page) {
    const record = await page.evaluate(key => JSON.parse(sessionStorage.getItem(key) || 'null'), fixture.recovery.storageKey);
    assert.ok(record);
    assert.equal(record.storageKey, fixture.recovery.storageKey);
    assert.equal(record.operationId, fixture.recovery.record.operationId);
    assert.equal(record.id, fixture.recovery.record.id);
    assert.equal(record.version, fixture.recovery.record.version);
    assert.equal(record.status, fixture.recovery.record.status);
    assert.equal(record.pending, true);
    // Observing the exact saved operation explicitly normalizes these flags.
    assert.equal(record.retired, false);
    assert.equal(record.unconfirmed, false);
    return { storageKey: record.storageKey, operationId: record.operationId, id: record.id,
      version: record.version, status: record.status, pending: record.pending,
      retired: record.retired, unconfirmed: record.unconfirmed };
  }

  let failure = null;
  try {
    const { page, context } = await newPage('superAdmin', { recovery: true });
    await page.goto(originalOperationsUrl(), { waitUntil: 'networkidle' });
    await settledHistory(page);
    assert.equal(await page.locator('#email-test-scope').inputValue(), fixture.otherLibrary);
    await page.locator('#operations-email-test [data-email-test="result"]').filter({ hasText: fixture.recovery.record.id }).waitFor();
    const retained = await recoveryIdentity(page);
    assert.ok(report.exactTestReads.some(read => read.path.endsWith(`/${fixture.recovery.record.id}`) && read.organizationId === fixture.otherLibrary));
    await enterQueue(page);

    const loading = await holdRealRead(page, url => url.pathname === detailPath(primary), 'initial-detail-loading');
    try {
      const opening = page.locator(`#request-grid a[data-request-id="${primary}"]`).first().click();
      await loading.started();
      assert.equal(await page.locator('#request-dialog[open]').count(), 0);
      assert.match(await page.locator('#app-status').textContent(), /Loading request details/i);
      await loading.release(); await opening;
      await showActivity(page);
    } finally { loading.unblock(); }
    const api = await apiGet(context, `${detailPath(primary)}?scope=${fixture.library}`);
    assert.deepEqual(api.json.activity, fixture.expectedActivity.superAdmin);
    await assertActivity(page);
    const suppression = fixture.expectedActivity.superAdmin.find(item => item.email?.operationReference === fixture.references.confirmation);
    assert.equal(suppression.email.outcome, 'suppressed');
    assert.equal(suppression.email.reason, 'Recipient domain not allowed');
    assert.equal(fixture.expectedActivity.superAdmin.filter(item => item.email?.operationReference === fixture.references.confirmation).length, 1);
    assert.equal(suppression.email.isRecordedSummary, false);
    assert.equal(api.json.status, 'suggestion');
    report.scenarios.actualSuppression = true;
    const collision = await activityRows(page).evaluateAll((rows, id) => rows.filter(row => row.dataset.eventId === id).map(row => row.dataset.source), fixture.collisionId);
    assert.ok(collision.includes('email_event') && collision.includes('request_event'));
    const precision = fixture.expectedActivity.superAdmin.filter(item => item.created.includes('.123456'));
    assert.ok(precision.some(item => item.created.includes('.1234562')) && precision.some(item => item.created.includes('.1234561')));
    report.scenarios.sourceAndTickOrdering = true;
    assert.ok((await activity(page).textContent()).includes("<img src=x onerror=alert('activity')>"));
    const heading = page.locator('#request-dialog-title');
    assert.match(api.json.title, /<b>request<\/b>/i);
    assert.equal(await heading.textContent(), api.json.title, 'The persisted title renders exactly as literal text.');
    assert.equal(await heading.locator('b, img, svg, script').count(), 0, 'Title markup never creates elements.');
    report.scenarios.safeText = true;
    report.scenarios.noNotesContamination = true;

    await page.locator(`#request-${primary}-details-tab`).click();
    const draft = 'Unsaved Activity navigation draft';
    await notes(page).fill(draft);
    await showActivity(page);
    const beforeCancelReads = report.emailReads.filter(read => read.reference).length;
    const link = referenceButton(page);
    await page.locator(`#request-${primary}-activity-tab`).focus();
    let tabsToReference = 0;
    while (!await link.evaluate(node => document.activeElement === node) && tabsToReference < 80) {
      await page.keyboard.press('Tab'); tabsToReference++;
    }
    assert.equal(await link.evaluate(node => document.activeElement === node), true, 'The exact reference action is keyboard reachable.');
    const focus = await link.evaluate(node => {
      const style = getComputedStyle(node);
      return { focusVisible: node.matches(':focus-visible'), outlineStyle: style.outlineStyle, outlineWidth: parseFloat(style.outlineWidth) };
    });
    assert.equal(focus.focusVisible, true); assert.notEqual(focus.outlineStyle, 'none'); assert.ok(focus.outlineWidth > 0);
    report.keyboardReference = { tabsToReference, ...focus };
    await decideConfirmation(page, () => link.press('Enter'), dialog => dialog.dismiss());
    assert.equal(await requestDialog(page).isVisible(), true);
    assert.equal(await page.locator(`#request-${primary}-activity-tab`).getAttribute('aria-selected'), 'true');
    assert.equal(await notes(page).inputValue(), draft);
    assert.equal(await link.evaluate(node => document.activeElement === node), true);
    assert.equal(report.emailReads.filter(read => read.reference).length, beforeCancelReads);
    assert.deepEqual(await recoveryIdentity(page), retained);
    assert.equal(await page.locator('#operations-view').isVisible(), false);
    report.scenarios.dirtyCancelKeepsDraft = true;
    await page.screenshot({ path: path.join(artifacts, 'dirty-cancel-reference-focus.png') });

    const destination = await holdRealRead(page, isExactReference(fixture.references.confirmation), 'guarded-exact-reference');
    try {
      await decideConfirmation(page, () => link.press('Enter'), dialog => dialog.accept());
      await destination.started();
      assert.equal(await requestDialog(page).isVisible(), false);
      assert.equal(await page.locator('#confirmation-dialog[open]').count(), 0);
      assert.equal(await page.locator('#operations-email-panel').isVisible(), true);
      assert.equal(await page.locator('#email-operations-table').evaluate(node => document.activeElement === node), true);
      assert.equal(await page.locator('#email-view-scope').inputValue(), fixture.library);
      assert.equal(await page.locator('#email-test-scope').inputValue(), fixture.otherLibrary);
      await page.locator('#email-search').focus();
      await destination.release(); await settledHistory(page);
      assert.equal(await page.locator('#email-search').evaluate(node => document.activeElement === node), true);
      assert.equal(destination.evidence.data.count, 1);
      assert.equal(destination.evidence.data.items[0].id, fixture.references.confirmation);
      const query = new URL(destination.evidence.url).searchParams;
      assert.equal(query.get('page'), '1'); assert.equal(query.get('pageSize'), '25'); assert.equal(query.get('sort'), 'newest');
      for (const field of ['search', 'status', 'type', 'fromUtc', 'beforeUtc']) assert.equal(query.has(field), false);
    } finally { destination.unblock(); }
    assert.equal(await page.locator('#email-reference-value').textContent(), `Showing exact email operation ${fixture.references.confirmation}.`);
    assert.equal(await page.locator('#email-operations-table tbody tr').count(), 1);
    assert.match(await page.locator('#email-operations-table').textContent(), /Recipient domain not allowed/);
    assert.equal(new URL(page.url()).searchParams.get('emailReference'), fixture.references.confirmation);
    assert.deepEqual(await recoveryIdentity(page), retained);
    assert.equal((await apiGet(context, `${detailPath(primary)}?scope=${fixture.library}`)).json.notes, fixture.savedNotes);
    report.scenarios.dirtyDiscardNavigates = true;
    report.scenarios.focusMovementPreserved = true;
    report.scenarios.testRecoveryUnchanged = true;
    const newerBoundary = new Date(Date.parse(suppression.created) + 86400000).toISOString();
    const newer = await apiGet(context, `/api/asap/staff/email-operations?scope=${fixture.library}&fromUtc=${encodeURIComponent(newerBoundary)}&pageSize=1`);
    assert.ok(newer.json.count >= fixture.newerUnrelatedOperationCount);
    report.scenarios.exactOlderReference = true;

    const clearedRead = page.waitForResponse(response => isHistory(response) &&
      new URL(response.url()).searchParams.get('scope') === fixture.library && !new URL(response.url()).searchParams.has('reference'));
    await page.locator('#email-reference-clear').press('Enter');
    const cleared = await clearedRead, fullHistory = await cleared.json();
    assert.equal(cleared.status(), 200); await settledHistory(page);
    assert.ok(fullHistory.count > 500);
    assert.equal(fullHistory.page, 1);
    assert.equal(fullHistory.items.some(item => item.id === fixture.references.confirmation), false);
    assert.equal(await page.locator('#email-reference-context').isVisible(), false);
    assert.equal(new URL(page.url()).searchParams.has('emailReference'), false);
    report.scenarios.clearReference = true;
    await enterQueue(page); await openRequest(page); await assertActivity(page);

    for (const [theme, width] of [['light', 1280], ['light', 390], ['dark', 1280], ['dark', 390]]) {
      await page.setViewportSize({ width, height: width === 1280 ? 720 : 900 });
      await page.emulateMedia({ colorScheme: theme });
      await twoFrames(page);
      assert.deepEqual(await activityRows(page).evaluateAll(rows => rows.map(row => row.dataset.activityId)), fixture.expectedDisplayOrdering);
      assert.equal(await page.locator(`#request-${primary}-activity-tab`).getAttribute('aria-selected'), 'true');
      await scan(page, `${theme}-${width}`, { mobile: width === 390, desktop: width === 1280 });
      assert.deepEqual(await recoveryIdentity(page), retained);
    }
    report.scenarios.activityRetention = true;
    await page.setViewportSize({ width: 1280, height: 900 }); await page.emulateMedia({ colorScheme: 'light' });

    // A real successful SQL read is converted to a transport error once. The
    // retry uses the actual request link, then a document reload re-reads SQL.
    await closeRequest(page);
    let failOnce = true;
    const failHandler = async route => {
      if (!failOnce || route.request().method() !== 'GET' || new URL(route.request().url()).pathname !== detailPath(primary)) return route.fallback();
      failOnce = false;
      const response = await route.fetch(); assert.equal(response.status(), 200);
      report.apiAssertions.push({ path: detailPath(primary), status: 200, injectedResponse: 503 });
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Activity detail fixture unavailable.' }) });
    };
    await page.route('**/api/asap/staff/**', failHandler);
    try {
      await page.locator(`#request-grid a[data-request-id="${primary}"]`).first().click();
      await page.locator('#app-status').filter({ hasText: 'Activity detail fixture unavailable.' }).waitFor();
      assert.equal(await page.locator('#request-dialog[open]').count(), 0);
    } finally { await page.unroute('**/api/asap/staff/**', failHandler); }
    await openRequest(page); await assertActivity(page);
    await page.reload({ waitUntil: 'networkidle' }); await showActivity(page); await assertActivity(page);
    report.scenarios.loadingErrorReload = true;

    const stale = await referenceButton(page).elementHandle();
    await closeRequest(page); await openRequest(page, fixture.requests.other);
    assert.match(await activity(page).textContent(), /No recorded activity/);
    const beforeStaleUrl = page.url(), beforeStaleReads = report.emailReads.filter(read => read.reference).length;
    await stale.evaluate(node => node.click()); await twoFrames(page);
    assert.equal(page.url(), beforeStaleUrl);
    assert.equal(report.emailReads.filter(read => read.reference).length, beforeStaleReads);
    assert.equal(await page.locator(`#request-${fixture.requests.other}-activity-tab`).getAttribute('aria-selected'), 'true');
    assert.equal(await activityRows(page).count(), 0);
    report.scenarios.staleDetailRetired = true;
    await stale.dispose();

    await closeRequest(page); await openRequest(page);
    const oldDocument = await page.evaluate(() => window.__emailActivityDocument);
    const retiredRead = await holdRealRead(page, isExactReference(fixture.references.confirmation), 'same-identity-reload-retirement');
    try {
      await referenceButton(page).click(); await retiredRead.started();
      await page.goto(queueUrl(primary), { waitUntil: 'domcontentloaded' });
      await showActivity(page); await assertActivity(page);
      const newDocument = await page.evaluate(() => window.__emailActivityDocument);
      assert.notEqual(newDocument, oldDocument);
      const sameActor = await apiGet(context, '/api/asap/staff/session');
      assert.equal(sameActor.json.staff.id, fixture.profiles.superAdmin.id);
      await closeRequest(page); await openRequest(page, fixture.requests.other);
      retiredRead.evidence.releasedAfterRetirement = true;
      await retiredRead.release(); await twoFrames(page);
      assert.equal(new URL(page.url()).searchParams.get('request'), fixture.requests.other);
      assert.equal(await activityRows(page).count(), 0);
      assert.equal(await page.locator('#operations-view').isVisible(), false);
      report.sessionRetirement = { method: 'same-identity document reload with held prior-owner SQL response', oldDocument, newDocument,
        actorId: fixture.profiles.superAdmin.id, inProcessEpochCoverage: 'retained staff_request_details and staff_navigation_drafts_ui Node tests' };
      report.scenarios.sessionEpochRetired = true;
    } finally { retiredRead.unblock(); }

    const { page: adminPage, context: adminContext } = await newPage('libraryAdmin');
    await adminPage.goto(queueUrl(primary), { waitUntil: 'networkidle' }); await showActivity(adminPage);
    const adminApi = await apiGet(adminContext, detailPath(primary));
    assert.deepEqual(adminApi.json.activity, fixture.expectedActivity.libraryAdmin);
    await assertActivity(adminPage, 'libraryAdmin');
    const adminHistory = adminPage.waitForResponse(response => isHistory(response) && isExactReference(fixture.references.confirmation)(new URL(response.url())));
    await referenceButton(adminPage).press('Enter');
    const adminResult = await adminHistory;
    assert.equal(adminResult.status(), 200);
    assert.equal((await adminResult.json()).items[0].id, fixture.references.confirmation);
    await settledHistory(adminPage);
    assert.equal(await adminPage.locator('#email-view-scope').inputValue(), fixture.library);

    const { page: staffPage, context: staffContext } = await newPage('ordinaryStaff', { width: 390, theme: 'dark' });
    await staffPage.goto(queueUrl(primary), { waitUntil: 'networkidle' }); await showActivity(staffPage);
    const staffApi = await apiGet(staffContext, detailPath(primary));
    assert.deepEqual(staffApi.json.activity, fixture.expectedActivity.ordinaryStaff);
    await assertActivity(staffPage, 'ordinaryStaff');
    assert.equal(await activity(staffPage).locator('.request-email-reference-link').count(), 0);
    assert.equal(await staffPage.locator('#operations-view-tab').isVisible(), false);
    assert.ok((await activity(staffPage).locator('.request-email-reference code').allTextContents()).includes(fixture.references.confirmation));
    report.scenarios.referenceOnlyStaff = true;
    const deniedHistory = await apiGet(staffContext, `/api/asap/staff/email-operations?scope=${fixture.library}&reference=${fixture.references.confirmation}`, 403);
    assert.equal(deniedHistory.body.includes('confirmation@blocked.invalid'), false);
    const { page: foreignPage, context: foreignContext } = await newPage('foreignAdmin');
    const deniedRequest = await apiGet(foreignContext, detailPath(primary), 404);
    const deniedScope = await apiGet(foreignContext, `/api/asap/staff/email-operations?scope=${fixture.library}&reference=${fixture.references.confirmation}`, 403);
    assert.equal(deniedRequest.body.includes('confirmation@blocked.invalid') || deniedScope.body.includes('confirmation@blocked.invalid'), false);
    await foreignPage.goto(queueUrl(primary), { waitUntil: 'networkidle' });
    await foreignPage.locator('#app-status').filter({ hasText: 'That request is no longer available.' }).waitFor();
    assert.equal(await foreignPage.locator('#request-dialog[open]').count(), 0);
    assert.equal(await foreignPage.locator('.request-activity-email').count(), 0);
    report.scenarios.unauthorizedReferenceDenied = true;

    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'native-zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, locale: 'en-US', timezoneId: 'America/New_York',
      extraHTTPHeaders: headerFor(fixture.profiles.libraryAdmin),
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900']
    });
    await configureContext(zoomContext);
    const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage(); watch(zoomPage);
    await zoomPage.goto(queueUrl(primary), { waitUntil: 'networkidle' }); await showActivity(zoomPage);
    const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker', { timeout: 10000 });
    const initialRatio = await zoomPage.evaluate(() => devicePixelRatio);
    const zoom = await worker.evaluate(async appOrigin => {
      const tab = (await chrome.tabs.query({})).find(value => value.url.startsWith(appOrigin));
      await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
      await chrome.tabs.setZoom(tab.id, 2);
      return chrome.tabs.getZoom(tab.id);
    }, origin);
    assert.equal(zoom, 2);
    await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, initialRatio);
    await activity(zoomPage).evaluate(node => node.scrollIntoView({ block: 'start', inline: 'nearest' }));
    await scan(zoomPage, 'request-email-native-200', { nativeViewport: true });
    report.nativeZoom = { zoom, initialRatio };

    for (const name of requiredScenarios) assert.equal(report.scenarios[name], true, name);
    assert.equal(report.externalRequests, 0);
    assert.deepEqual(report.pageErrors, []);
    assert.equal(report.writes, 0, 'Activity browsing, navigation, Cancel/discard and direct probes perform no mutations.');
    console.log('Request email Activity: real SQL evidence, guarded exact reference navigation, privacy, races and layout guards passed.');
  } catch (error) {
    failure = error;
    report.failure = { message: error.message, stack: error.stack };
    throw error;
  } finally {
    for (const release of [...releases]) release();
    for (const context of contexts.reverse()) {
      try { await context.close(); } catch (error) { report.cleanupErrors.push(error.message); }
    }
    try { await browser.close(); } catch (error) { report.cleanupErrors.push(error.message); }
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    if (!failure && report.cleanupErrors.length) throw new Error(`Browser cleanup failed: ${report.cleanupErrors.join('; ')}`);
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
