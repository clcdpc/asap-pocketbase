'use strict';
const { rejectNativeDialogs } = require('./confirmation.cjs');

const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

async function main() {
  const [origin, artifacts, scope, staffId, tenantId, email] = process.argv.slice(2);
  assert.ok(email, 'Usage: operations-dto-boundary.cjs origin artifacts scope staff tenant email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], states: [], localClocks: {} };
  const contexts = [];
  const scenarios = [
    { zone: 'America/New_York', width: 1280, expected: ['3/8 1:59', '3/8 3:1', '11/1 1:59', '11/1 1:1', '1/15 10:0', '7/15 11:0'] },
    { zone: 'Europe/London', width: 1280, expected: ['3/8 6:59', '3/8 7:1', '11/1 5:59', '11/1 6:1', '1/15 15:0', '7/15 16:0'] },
    { zone: 'Asia/Tokyo', width: 1280, expected: ['3/8 15:59', '3/8 16:1', '11/1 14:59', '11/1 15:1', '1/16 0:0', '7/16 0:0'] },
    { zone: 'America/New_York', width: 390, expected: ['3/8 1:59', '3/8 3:1', '11/1 1:59', '11/1 1:1', '1/15 10:0', '7/15 11:0'] }
  ];

  const endpoint = (response, pathname) => response.request().method() === 'GET' &&
    new URL(response.url()).pathname === pathname;
  async function refresh(page, action) {
    const queues = page.waitForResponse(response => endpoint(response, '/api/asap/staff/workflow/queues'));
    const emails = page.waitForResponse(response => endpoint(response, '/api/asap/staff/email-operations'));
    await action();
    const responses = await Promise.all([queues, emails]);
    for (const response of responses) assert.equal(response.status(), 200, await response.text());
    await page.locator('#refresh-operations:not([disabled])').waitFor();
  }

  async function verifyProjection(page, scenario) {
    const actual = await page.evaluate(async scope => {
      const [queue, email] = await Promise.all([
        fetch(`/api/asap/staff/workflow/queues?organizationId=${scope}`).then(response => response.json()),
        fetch(`/api/asap/staff/email-operations?organizationId=${scope}`).then(response => response.json())
      ]);
      const rows = [...document.querySelectorAll('#queue-progress-table tbody tr')];
      const queueDom = Object.fromEntries(rows.map(row => [row.cells[0].textContent, {
        cells: [...row.cells].map(cell => cell.textContent), datetime: row.querySelector('time')?.getAttribute('datetime')
      }]));
      const emailDom = Object.fromEntries([...document.querySelectorAll('#email-operations-table tbody tr')]
        .map(row => [row.cells[0].textContent, { cells: [...row.cells].map(cell => cell.textContent),
          datetime: row.querySelector('time')?.getAttribute('datetime') }]));
      const local = value => new Date(value).toLocaleString();
      return { queue, email, queueDom, emailDom,
        localized: Object.fromEntries(queue.items.map(row => [row.queueName, {
          created: row.lastCreatedUtc ? local(row.lastCreatedUtc) : null, updated: local(row.updatedUtc)
        }])),
        emailLocalized: Object.fromEntries(email.items.map(row => [row.id, local(row.createdUtc)])),
        clocks: ['IdentifierProcessing', 'PurchasePromotion', 'FulfillmentTracking', 'OutstandingTimeout', 'HoldPlacement', 'PendingHoldTimeout']
          .map(name => {
            const date = new Date(queue.items.find(row => row.queueName === name).updatedUtc);
            return `${date.getMonth() + 1}/${date.getDate()} ${date.getHours()}:${date.getMinutes()}`;
          }) };
    }, scope);
    assert.equal(actual.queue.scopeOrganizationId, Number(scope));
    assert.equal(actual.queue.items.length, 6);
    assert.equal(actual.email.items.length, 3);
    const queue = Object.fromEntries(actual.queue.items.map(row => [row.queueName, row]));
    for (const [name, lastItemId, cycleMaxId, lastOutcomeItemId, state] of [
      ['IdentifierProcessing', '9007199254740993', '9007199254740995', '9007199254740997', 'Active'],
      ['PurchasePromotion', '9223372036854775807', '9223372036854775807', '9223372036854775807', 'Active'],
      ['HoldPlacement', '0', '0', '0', 'Empty'],
      ['FulfillmentTracking', '1', null, null, 'Completed'],
      ['OutstandingTimeout', '2', null, null, 'Idle'],
      ['PendingHoldTimeout', null, null, null, 'Idle']
    ]) {
      const row = queue[name], dom = actual.queueDom[name];
      assert.equal(row.lastItemId, lastItemId, name);
      assert.equal(row.cycleMaxId, cycleMaxId, name);
      assert.equal(row.lastOutcomeItemId, lastOutcomeItemId, name);
      assert.equal(dom.cells[1], cycleMaxId ?? 'None', name);
      assert.equal(dom.cells[2], state, name);
      assert.equal(dom.cells[3], row.lastCreatedUtc ? `${actual.localized[name].created} / ${lastItemId}` : 'None', name);
      assert.equal(dom.cells[5], actual.localized[name].updated, name);
      assert.equal(dom.datetime, row.updatedUtc, name);
      for (const field of ['lastCreatedUtc', 'lastOutcomeUtc', 'updatedUtc']) {
        if (row[field] !== null) assert.match(row[field], /Z$/, `${name}.${field}`);
      }
    }
    assert.equal(queue.IdentifierProcessing.lastCreatedUtc, '2026-03-08T06:59:00.1234567Z');
    assert.equal(queue.IdentifierProcessing.lastOutcomeUtc, '2026-07-15T15:00:00.7654321Z');
    assert.equal(queue.PendingHoldTimeout.lastCreatedUtc, null);
    assert.equal(queue.PendingHoldTimeout.lastOutcomeUtc, null);
    assert.equal(actual.email.items[0].status, 'failed', 'failed operations retain first priority');
    for (const row of actual.email.items) {
      assert.equal(row.organizationId, Number(scope));
      assert.match(row.createdUtc, /Z$/);
      assert.equal(actual.emailDom[row.id].cells[1], actual.emailLocalized[row.id]);
      assert.equal(actual.emailDom[row.id].datetime, row.createdUtc);
      if (row.sentUtc !== null) assert.equal(row.sentUtc, '2026-11-01T06:01:00.1234567Z');
    }
    assert.equal(actual.email.items.find(row => row.status === 'failed').sentUtc, null);
    assert.deepEqual(actual.clocks, scenario.expected, scenario.zone);
    report.localClocks[`${scenario.zone}-${scenario.width}`] = actual.clocks;
  }

  async function verifyInvalidFallback(page) {
    // This negative presentation witness deliberately supplies malformed read
    // responses; the positive journeys above use the real SQL-backed responses.
    const invalid = '<img src=x onerror="throw Error(1)">';
    await page.route('**/api/asap/staff/workflow/queues*', async route => {
      const response = await route.fetch();
      const body = await response.json();
      body.items[0].lastCreatedUtc = invalid;
      body.items[0].updatedUtc = invalid;
      body.items[1].lastCreatedUtc = null;
      body.items[1].updatedUtc = null;
      await route.fulfill({ response, json: body });
    });
    await page.route('**/api/asap/staff/email-operations*', async route => {
      const response = await route.fetch();
      const body = await response.json();
      body.items[0].createdUtc = invalid;
      body.items[1].createdUtc = null;
      await route.fulfill({ response, json: body });
    });
    await refresh(page, () => page.locator('#refresh-operations').click());
    const fallback = await page.locator('#operations-view').evaluate(root => ({
      queues: [...root.querySelectorAll('#queue-progress-table tbody tr')].map(row => ({
        cursor: row.cells[3].textContent, updated: row.cells[5].textContent,
        datetime: row.querySelector('time').getAttribute('datetime') })),
      emails: [...root.querySelectorAll('#email-operations-table time')].map(time => ({
        text: time.textContent, datetime: time.getAttribute('datetime'), children: time.children.length })),
      injected: root.querySelectorAll('#queue-progress-table img, #email-operations-table img').length,
      invalidDate: root.textContent.includes('Invalid Date')
    }));
    assert.equal(fallback.queues[0].updated, invalid);
    assert.ok(fallback.queues[0].cursor.startsWith(invalid));
    assert.equal(fallback.queues[1].cursor, 'None');
    assert.equal(fallback.queues[1].updated, 'Not recorded');
    assert.equal(fallback.queues[1].datetime, null);
    assert.deepEqual(fallback.emails[0], { text: invalid, datetime: invalid, children: 0 });
    assert.deepEqual(fallback.emails[1], { text: 'Not recorded', datetime: null, children: 0 });
    assert.equal(fallback.injected, 0);
    assert.equal(fallback.invalidDate, false);
    await page.unroute('**/api/asap/staff/workflow/queues*');
    await page.unroute('**/api/asap/staff/email-operations*');
    report.invalidFallback = true;
  }

  try {
    for (const scenario of scenarios) {
      const context = await browser.newContext({ timezoneId: scenario.zone, locale: 'en-US',
        viewport: { width: scenario.width, height: 900 }, extraHTTPHeaders: {
          'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email
        } });
      contexts.push(context);
      await context.route('**/*', route => {
        if (new URL(route.request().url()).origin === origin) return route.continue();
        report.externalRequests += 1;
        return route.abort('blockedbyclient');
      });
      const page = await context.newPage();
      page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
      await page.goto(`${origin}/staff/?stage=operations`, { waitUntil: 'networkidle' });
      await page.locator('#operations-view').waitFor({ state: 'visible' });
      await refresh(page, () => page.locator('#operations-scope').selectOption(scope));
      await verifyProjection(page, scenario);
      await refresh(page, () => page.locator('#operations-scope').selectOption('2'));
      assert.equal(await page.locator('#queue-progress-table').getByText('9007199254740995', { exact: true }).count(), 0,
        'changing scope retires the former library cursor');
      await refresh(page, () => page.locator('#operations-scope').selectOption(scope));
      await verifyProjection(page, scenario);
      await page.reload({ waitUntil: 'networkidle' });
      // Operations scope follows the URL/history contract. Re-select only when
      // reload starts at All libraries, then verify the refreshed read projection.
      if (await page.locator('#operations-scope').inputValue() !== scope) {
        await refresh(page, () => page.locator('#operations-scope').selectOption(scope));
      }
      await verifyProjection(page, scenario);
      if (scenario.width === 390) {
        await verifyInvalidFallback(page);
        await refresh(page, () => page.locator('#refresh-operations').click());
        await verifyProjection(page, scenario);
      }
      await page.evaluate(axeSource);
      const violations = await page.evaluate(async () => (await axe.run()).violations
        .filter(item => ['serious', 'critical'].includes(item.impact)));
      const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
        imagesLoaded: [...document.images].filter(image => {
          const bounds = image.getBoundingClientRect();
          return bounds.width > 0 && bounds.height > 0;
        }).every(image => image.complete && image.naturalWidth > 0) }));
      const name = `${scenario.zone.replaceAll('/', '-')}-${scenario.width}`;
      await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
      report.states.push({ name, violations, layout });
      assert.deepEqual(violations, [], name);
      assert.ok(layout.scrollWidth <= layout.width, `Horizontal overflow: ${name}`);
      assert.equal(layout.imagesLoaded, true, `Missing image: ${name}`);
      await context.close();
    }
    assert.equal(report.externalRequests, 0);
    assert.deepEqual(report.pageErrors, []);
    console.log('Operations SQL/HTTP browser timezone, DST, bigint, scope, reload and safe fallback checks passed.');
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
