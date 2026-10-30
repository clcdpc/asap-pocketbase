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
  const report = { externalRequests: 0, pageErrors: [], posts: [], states: [] };
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
    await page.route('**/api/asap/staff/email-operations/test?*', route => {
      if (route.request().method() === 'POST' && abandonNext) {
        abandonNext = false;
        return route.abort('failed'); // The first intent never reaches Kestrel/SQL.
      }
      return route.continue();
    });
    await page.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-smtp`, { waitUntil: 'networkidle' });
    const setup = page.locator('#settings-email-test');
    await setup.waitFor({ state: 'visible' });
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
    await decideConfirmation(page, () => page.locator('#settings-discard').click(), dialog => dialog.accept());
    await setup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.equal(report.posts.length, 0, 'saved-context checks and discard never send');
    await setup.locator('[data-email-test="send"]').focus();
    assert.equal(await setup.locator('[data-email-test="send"]').evaluate(node => node === document.activeElement), true);
    await setup.locator('[data-email-test="send"]').press('Space');
    await setup.locator('[data-email-test="result"]').filter({ hasText: 'outcome is unconfirmed' }).waitFor();
    const abandoned = new URL(report.posts[0].url).searchParams.get('operationId');
    assert.equal(await setup.locator('[data-email-test="send"]').isDisabled(), true);
    await setup.locator('[data-email-test="refresh"]').click();
    await setup.locator('[data-email-test="result"]').filter({ hasText: `No server record was found for request ${abandoned}` }).waitFor();
    assert.equal(await setup.locator('[data-email-test="retire"]').isHidden(), true);
    await page.reload({ waitUntil: 'networkidle' });
    await setup.locator('[data-email-test="result"]').filter({ hasText: `No server record was found for request ${abandoned}` }).waitFor();
    assert.equal(report.posts.length, 1, 'controller recreation only reconciles the original absent intent');
    await page.clock.setFixedTime(startTime + 60001);
    await setup.locator('[data-email-test="refresh"]').click();
    await setup.locator('[data-email-test="retire"]').waitFor({ state: 'visible' });
    await setup.locator('[data-email-test="retire"]').focus();
    await decideConfirmation(page, () => setup.locator('[data-email-test="retire"]').press('Enter'), dialog => dialog.accept());
    await setup.locator('[data-email-test="result"]').filter({ hasText: 'Retired the unrecorded client request' }).waitFor();
    assert.equal(report.posts.length, 1, 'explicit retirement makes only a final real status lookup');
    assert.equal(await setup.locator('[data-email-test="send"]').evaluate(node => node === document.activeElement), true,
      'retirement restores useful keyboard focus');
    await setup.locator('[data-email-test="history"] summary').click();
    assert.match(await setup.locator('[data-email-test="history"]').textContent(), new RegExp(abandoned));
    await scan(page, 'settings-email-retired-desktop');
    await page.setViewportSize({ width: 390, height: 900 });
    await scan(page, 'settings-email-retired-mobile');
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.reload({ waitUntil: 'networkidle' });
    await setup.locator('[data-email-test="send"]:not([disabled])').waitFor();
    assert.match(await setup.locator('[data-email-test="history"]').textContent(), new RegExp(abandoned));
    assert.equal(report.posts.length, 1, 'restoring a retired reference never sends');
    report.retiredUnrecordedIntent = abandoned;
    await setup.locator('[data-email-test="send"]').press('Space');
    await setup.locator('[data-email-test="result"]').filter({ hasText: 'The email service accepted your test message.' })
      .waitFor({ timeout: 60000 });
    assert.equal(report.posts.length, 2);
    assert.equal(new URL(report.posts[1].url).searchParams.get('organizationId'), scope);
    assert.match(new URL(report.posts[1].url).searchParams.get('operationId'), /^[0-9a-f-]{36}$/i);
    assert.notEqual(new URL(report.posts[1].url).searchParams.get('operationId'), abandoned);
    assert.equal(report.posts[1].body.intendedDeliveryMode, 'postmark');
    assert.match(report.posts[1].body.configurationVersion, /^[0-9a-f]{64}$/i);
    assert.doesNotMatch(await setup.locator('[data-email-test="result"]').textContent(), /was delivered|was received/i);
    const reference = (await setup.locator('[data-email-test="result"]').textContent()).match(/Reference (\d+)\./)[1];
    report.reference = reference;
    await scan(page, 'settings-email-desktop');
    await setup.locator('[data-email-test="operations"]').click();
    await page.locator('#operations-view').waitFor({ state: 'visible' });
    await page.waitForFunction(scope => document.getElementById('operations-scope').value === scope, scope);
    const operations = page.locator('#operations-email-test');
    await operations.locator('[data-email-test="result"]').filter({ hasText: `Reference ${reference}.` }).waitFor();
    assert.equal(report.posts.length, 2, 'the scoped Operations handoff observes the original reference');
    const stored = await page.evaluate(() => Object.fromEntries(Object.keys(sessionStorage)
      .filter(key => key.startsWith('asap.staff.email-test.')).map(key => [key, sessionStorage.getItem(key)])));
    await scan(page, 'operations-email-desktop');

    const mobile = await newPage(390, stored);
    await mobile.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-smtp`, { waitUntil: 'networkidle' });
    const mobileSetup = mobile.locator('#settings-email-test');
    await mobileSetup.locator('[data-email-test="result"]').filter({ hasText: `Reference ${reference}.` }).waitFor();
    assert.match(await mobileSetup.textContent(), /email service accepted/);
    await mobileSetup.locator('[data-email-test="refresh"]').click();
    await mobileSetup.locator('[data-email-test="result"]').filter({ hasText: `Reference ${reference}.` }).waitFor();
    assert.equal(report.posts.length, 2, 'restoration and manual observation never request another message');
    await scan(mobile, 'settings-email-mobile');
    await mobile.reload({ waitUntil: 'networkidle' });
    await mobileSetup.locator('[data-email-test="result"]').filter({ hasText: `Reference ${reference}.` }).waitFor();
    assert.equal(report.posts.length, 2, 'page reload restores status without replay');
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
