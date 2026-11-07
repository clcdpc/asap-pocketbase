'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { rejectNativeDialogs } = require('./confirmation.cjs');

async function main() {
  const [origin, artifacts, scope, targetIds, staffId, tenantId, email] = process.argv.slice(2);
  const ids = targetIds?.split(',');
  assert.ok(email && ids?.length === 3 && ids.every(id => /^[1-9]\d*$/.test(id)),
    'Usage: email-retry-focus.cjs origin artifacts scope id,id,id staff tenant email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: {
    'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
  } });
  const report = { externalRequests: 0, pageErrors: [], writes: [], scenarios: [], violations: [] };
  const releases = new Set();
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const list = response => response.request().method() === 'GET' && new URL(response.url()).pathname === '/api/asap/staff/email-operations';
  try {
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++; return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.setDefaultTimeout(15000); page.setDefaultNavigationTimeout(20000);
    page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
    page.on('request', request => {
      if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) report.writes.push({ path: new URL(request.url()).pathname, method: request.method() });
    });
    const settled = async () => {
      await page.locator('#email-operations-table:not([aria-busy])').waitFor();
      await page.locator('#refresh-operations:not([disabled])').waitFor();
    };
    for (const [index, movement] of ['stay', 'filter', 'page'].entries()) {
      const id = ids[index], url = new URL('/staff/', origin);
      for (const [key, value] of Object.entries({ stage: 'operations', opSection: 'email', emailScope: scope,
        emailStatus: 'failed', emailTestScope: '1' })) url.searchParams.set(key, value);
      await page.goto(url.toString(), { waitUntil: 'networkidle' }); await settled();
      const row = page.locator('#email-operations-table tbody tr').filter({ has: page.getByText(id, { exact: true }) });
      const retry = row.getByRole('button', { name: 'Retry', exact: true });
      assert.equal(await retry.count(), 1, `${movement}: the real SQL row has certified retry permission`);
      let release, began;
      const gate = new Promise(resolve => { release = resolve; });
      const started = new Promise(resolve => { began = resolve; });
      releases.add(release);
      const pattern = `**/api/asap/staff/email-operations/${id}/retry`;
      let version, acknowledged;
      await page.route(pattern, async route => {
        assert.equal(route.request().method(), 'POST');
        version = route.request().postDataJSON().version;
        const response = await route.fetch();
        assert.equal(response.status(), 202, await response.text());
        acknowledged = await response.json();
        assert.equal(acknowledged.code, 'queued'); assert.equal(acknowledged.data.id, id);
        began(); await gate;
        try { await route.fulfill({ response }); } catch { /* Test teardown may close a failed scenario. */ }
      });
      await retry.focus(); await retry.press('Enter');
      await Promise.race([started, new Promise((_, reject) => {
        const timeout = setTimeout(() => reject(new Error(`${movement}: actual retry never reached the persisted boundary`)), 10000);
        started.finally(() => clearTimeout(timeout));
      })]);
      assert.equal(await page.evaluate(() => document.activeElement?.id), 'email-operations-table', `${movement}: pending retry keeps useful focus`);
      assert.equal(await page.locator('#run-workflow-now').isDisabled(), true, 'the existing command guard stays active');
      let expectedFocus = 'email-operations-table';
      if (movement === 'filter') {
        const read = page.waitForResponse(list);
        await page.locator('#email-search').focus(); await page.locator('#email-search').fill('business_event');
        await read; await settled(); expectedFocus = 'email-search';
      }
      if (movement === 'page') {
        const read = page.waitForResponse(response => list(response) && new URL(response.url()).searchParams.get('page') === '2');
        await page.locator('#email-next').focus(); await page.locator('#email-next').press('Enter');
        await read; await page.locator('#email-page').filter({ hasText: 'Page 2' }).waitFor(); expectedFocus = 'email-next';
        assert.equal(await page.evaluate(() => document.activeElement?.id), expectedFocus);
      }
      await page.evaluate(axeSource);
      const violations = await page.evaluate(async () => (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)));
      report.violations.push(...violations); assert.deepEqual(violations, []);
      await page.screenshot({ path: path.join(artifacts, `${movement}-pending.png`), fullPage: true });
      const refreshed = page.waitForResponse(list);
      release(); releases.delete(release);
      await refreshed; await settled();
      await page.waitForFunction(expected => document.activeElement?.id === expected, expectedFocus);
      assert.equal(await page.evaluate(() => document.activeElement?.id), expectedFocus, `${movement}: completion respects the user's current focus owner`);
      if (movement === 'page') assert.equal(new URL(page.url()).searchParams.get('emailPage'), '2');
      assert.equal(await page.locator(`[data-email-retry-id="${id}"]`).count(), 0, 'the accepted pending row no longer supplies a retry action');
      await page.screenshot({ path: path.join(artifacts, `${movement}-completed.png`), fullPage: true });
      report.scenarios.push({ movement, id, version, status: acknowledged.code, focusPending: 'email-operations-table', focusAfter: expectedFocus });
      await page.unroute(pattern);
    }
    assert.equal(report.writes.length, 3);
    assert.ok(report.writes.every(item => item.method === 'POST' && ids.some(id => item.path === `/api/asap/staff/email-operations/${id}/retry`)),
      'Only the three explicit fixture retries may mutate the disposable SQL state.');
    assert.equal(report.externalRequests, 0); assert.deepEqual(report.pageErrors, []);
    console.log('Actual SQL-backed Retry preserves keyboard focus while pending, after completion and after filter/page movement');
  } finally {
    for (const release of releases) release();
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await context.close(); await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
