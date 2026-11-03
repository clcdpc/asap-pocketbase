'use strict';
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

async function main() {
  const [origin, artifacts, scope, staffId, tenantId, email] = process.argv.slice(2);
  assert.ok(email, 'Usage: saved-email-test.cjs origin artifacts scope staff tenant email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], posts: [], lookups: [], states: [] };
  const contexts = [];
  async function newPage(width, stored = {}) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, extraHTTPHeaders: {
      'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
    } });
    contexts.push(context);
    await context.addInitScript(entries => {
      for (const [key, value] of Object.entries(entries)) sessionStorage.setItem(key, value);
    }, stored);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests += 1; return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/asap/staff/email-operations/test') {
        report.posts.push({ url: request.url(), body: request.postDataJSON() });
      }
      if (request.method() === 'GET' && new URL(request.url()).pathname === '/api/asap/staff/email-operations/test') {
        report.lookups.push(request.url());
      }
    });
    return page;
  }
  async function scan(page, name) {
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await axe.run()).violations
      .filter(item => ['serious', 'critical'].includes(item.impact)));
    const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: [...document.images].filter(image => {
        const bounds = image.getBoundingClientRect(); return bounds.width > 0 && bounds.height > 0;
      }).every(image => image.complete && image.naturalWidth > 0) }));
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    report.states.push({ name, violations, layout });
    assert.deepEqual(violations, [], name);
    assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow: ${name}`);
    assert.equal(layout.imagesLoaded, true, `Missing image: ${name}`);
  }
  try {
    const page = await newPage(1280);
    const startTime = Date.now();
    await page.clock.setFixedTime(startTime);
    let abandonNext = true;
    await page.route('**/api/asap/staff/email-operations/test?*', async route => {
      if (route.request().method() === 'POST' && abandonNext) {
        abandonNext = false;
        return route.abort('failed'); // The first intent never reaches Kestrel/SQL.
      }
      if (route.request().method() === 'POST') {
        const result = await route.fetch();
        assert.equal(result.status(), 202, 'the second request commits before its acknowledgement is lost');
        return route.abort('failed');
      }
      return route.continue();
    });
    await page.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-smtp`, { waitUntil: 'networkidle' });
    const setup = page.locator('#settings-email-test');
    await setup.waitFor({ state: 'visible' });
    assert.equal(await setup.locator('[data-email-test="refresh"]').count(), 0);
    assert.equal(await setup.locator('[data-email-test="retire"]').count(), 0);
    assert.equal(await setup.locator('[data-email-test="operations"]').isVisible(), true, 'navigation is available before a send');
    await setup.locator('[data-email-test="send"]:not([disabled])').waitFor().catch(async error => {
      await page.screenshot({ path: path.join(artifacts, 'email-test-unavailable.png'), fullPage: true });
      throw new Error(`${error.message}\nSaved email context: ${await setup.textContent()}`);
    });
    assert.match(await setup.textContent(), /Saved Email Test Library/);
    assert.match(await setup.textContent(), /Saved library sender/);
    assert.match(await setup.textContent(), /primary-test@example.org/);
    assert.match(await setup.textContent(), /Live email via Postmark/);
    assert.doesNotMatch(await setup.textContent(), /weekly-test@example.org|system-controlled-test-token|obsolete-library-token/);
    await page.locator('#email-from-name').fill('Unsaved test sender');
    assert.equal(await setup.locator('[data-email-test="send"]').isDisabled(), true);
    assert.match(await setup.locator('[data-email-test="result"]').textContent(), /Save or discard/);
    assert.match(await setup.locator('[data-email-test="context"]').textContent(), /Saved library sender/,
      'draft sender controls never replace the saved test context');
    await decideConfirmation(page, () => setup.locator('[data-email-test="operations"]').press('Enter'), dialog => dialog.dismiss());
    assert.equal(await page.locator('#settings-view').isVisible(), true);
    assert.equal(await page.locator('#email-from-name').inputValue(), 'Unsaved test sender', 'declined navigation preserves the draft');
    await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
    await setup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.equal(report.posts.length, 0, 'saved-context checks and discard never send');
    await setup.locator('[data-email-test="send"]').focus();
    assert.equal(await setup.locator('[data-email-test="send"]').evaluate(node => node === document.activeElement), true);
    await setup.locator('[data-email-test="send"]').press('Space');
    await setup.locator('[data-email-test="result"]').filter({ hasText: 'outcome is unconfirmed' }).waitFor();
    const abandoned = new URL(report.posts[0].url).searchParams.get('operationId');
    assert.equal(await setup.locator('[data-email-test="send"]').isDisabled(), true);
    assert.match(await setup.locator('[data-email-test="result"]').textContent(), /View email operations/);
    await page.reload({ waitUntil: 'networkidle' });
    await setup.locator('[data-email-test="result"]').filter({ hasText: `Reference ${abandoned}.` }).waitFor();
    assert.equal(report.posts.length, 1, 'controller recreation only reconciles the original absent intent');
    await setup.locator('[data-email-test="operations"]').press('Enter');
    await page.locator('#operations-view').waitFor({ state: 'visible' });
    const operations = page.locator('#operations-email-test');
    const emailOperationsTable = page.locator('#email-operations-table');
    await operations.locator('[data-email-test="result"]').filter({ hasText: `No server record was found for request ${abandoned}` }).waitFor();
    await page.waitForFunction(() => document.activeElement === document.getElementById('email-operations-table'));
    assert.equal(await operations.locator('[data-email-test="refresh"]').count(), 0);
    assert.equal(await operations.locator('[data-email-test="operations"]').count(), 0,
      'Operations does not show a redundant link to its own Email operations log');
    assert.equal(await operations.locator('[data-email-test="retire"]').isHidden(), true);
    await page.clock.setFixedTime(startTime + 60001);
    await page.locator('#refresh-operations').click();
    await operations.locator('[data-email-test="retire"]').waitFor({ state: 'visible' });
    await operations.locator('[data-email-test="retire"]').focus();
    await decideConfirmation(page, () => operations.locator('[data-email-test="retire"]').press('Enter'), dialog => dialog.accept());
    await operations.locator('[data-email-test="result"]').filter({ hasText: 'Retired the unrecorded client request' }).waitFor();
    assert.equal(report.posts.length, 1, 'explicit retirement makes only a final real status lookup');
    assert.equal(await operations.locator('[data-email-test="send"]').evaluate(node => node === document.activeElement), true,
      'retirement restores useful keyboard focus');
    await operations.locator('[data-email-test="history"] summary').click();
    assert.match(await operations.locator('[data-email-test="history"]').textContent(), new RegExp(abandoned));
    await scan(page, 'operations-email-retired-desktop');
    await page.setViewportSize({ width: 390, height: 900 });
    await scan(page, 'operations-email-retired-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-smtp`, { waitUntil: 'networkidle' });
    await setup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.equal(await setup.locator('[data-email-test="history"]').count(), 0, 'Settings leaves detailed references in Operations');
    assert.equal(report.posts.length, 1, 'restoring a retired reference never sends');
    report.retiredUnrecordedIntent = abandoned;
    await setup.locator('[data-email-test="send"]').press('Space');
    await setup.locator('[data-email-test="result"]').filter({ hasText: 'outcome is unconfirmed' }).waitFor();
    assert.equal(report.posts.length, 2);
    assert.equal(new URL(report.posts[1].url).searchParams.get('organizationId'), scope);
    assert.match(new URL(report.posts[1].url).searchParams.get('operationId'), /^[0-9a-f-]{36}$/i);
    assert.notEqual(new URL(report.posts[1].url).searchParams.get('operationId'), abandoned);
    assert.equal(report.posts[1].body.intendedDeliveryMode, 'postmark');
    assert.match(report.posts[1].body.configurationVersion, /^[0-9a-f]{64}$/i);
    assert.doesNotMatch(await setup.locator('[data-email-test="result"]').textContent(), /was delivered|was received/i);
    const committedIntent = new URL(report.posts[1].url).searchParams.get('operationId');
    assert.match(await setup.locator('[data-email-test="result"]').textContent(), new RegExp(committedIntent),
      'a committed test with a lost acknowledgement keeps the original operation ID');
    await scan(page, 'settings-email-desktop');
    await setup.locator('[data-email-test="operations"]').click();
    await page.locator('#operations-view').waitFor({ state: 'visible' });
    await page.waitForFunction(scope => document.getElementById('operations-scope').value === scope, scope);
    await page.waitForFunction(() => Object.keys(sessionStorage).some(key => {
      if (!key.startsWith('asap.staff.email-test.') || key.endsWith('.retirement-references')) return false;
      const record = JSON.parse(sessionStorage.getItem(key)); return record.id && !record.pending && !record.retired;
    }), null, { timeout: 60000 });
    const reference = await page.evaluate(operationId => Object.keys(sessionStorage).map(key => {
      try { return JSON.parse(sessionStorage.getItem(key)); } catch { return null; }
    }).find(record => record?.operationId === operationId).id, committedIntent);
    report.reference = reference;
    assert.ok(report.lookups.some(url => new URL(url).searchParams.get('operationId') === committedIntent &&
      new URL(url).searchParams.get('organizationId') === scope), 'lost committed acknowledgement is reconciled by its original scoped identity');
    report.lostAcknowledgementReconciled = true;
    await page.locator('#refresh-operations').click();
    await emailOperationsTable.locator('tbody tr').filter({ hasText: reference }).filter({ hasText: 'Provider accepted' }).waitFor();
    assert.equal(report.posts.length, 2, 'the scoped Operations handoff observes the original reference');
    assert.equal(await emailOperationsTable.getAttribute('tabindex'), '0', 'the Email operations log remains keyboard reachable');
    await emailOperationsTable.focus();
    assert.equal(await emailOperationsTable.evaluate(node => node === document.activeElement), true,
      'keyboard focus can reach the Email operations log');
    const stored = await page.evaluate(() => Object.fromEntries(Object.keys(sessionStorage)
      .filter(key => key.startsWith('asap.staff.email-test.')).map(key => [key, sessionStorage.getItem(key)])));
    await scan(page, 'operations-email-desktop');

    const mobile = await newPage(390, stored);
    await mobile.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-smtp`, { waitUntil: 'networkidle' });
    const mobileSetup = mobile.locator('#settings-email-test');
    await mobileSetup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.equal(await mobileSetup.locator('[data-email-test="result"]').isHidden(), true, 'routine saved results are shown in the log');
    assert.equal(report.posts.length, 2, 'restoration and manual observation never request another message');
    await scan(mobile, 'settings-email-mobile');
    await mobile.reload({ waitUntil: 'networkidle' });
    await mobileSetup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.equal(report.posts.length, 2, 'page reload restores status without replay');
    assert.equal(await mobileSetup.locator('[data-email-test="operations"]').count(), 1,
      'Settings retains View email operations after restoring a test reference');
    await mobileSetup.locator('[data-email-test="operations"]').press('Enter');
    await mobile.locator('#operations-view').waitFor({ state: 'visible' });
    await mobile.waitForFunction(scope => document.getElementById('operations-scope').value === scope, scope);
    await mobile.waitForFunction(() => document.activeElement === document.getElementById('email-operations-table'));
    await mobile.locator('#refresh-operations').press('Enter');
    await mobile.locator('#email-operations-table tbody tr').filter({ hasText: reference }).filter({ hasText: 'Provider accepted' }).waitFor();
    assert.equal(report.posts.length, 2, 'Settings navigation observes the existing test without sending another email');
    assert.equal(await mobile.locator('#operations-email-test [data-email-test="operations"]').count(), 0,
      'the Operations-only action stays absent on narrow layouts');
    await scan(mobile, 'operations-email-mobile');
    report.sameReferenceAfterReload = true;
    assert.equal(report.externalRequests, 0);
    assert.deepEqual(report.pageErrors, []);
    console.log('Settings UI -> durable SQL intent -> automatic Hangfire worker -> recording production Postmark -> scoped observation passed.');
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
