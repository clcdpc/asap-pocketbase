'use strict';
const { rejectNativeDialogs } = require('./confirmation.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

async function main() {
  const [origin, artifacts, scope, staffId, tenantId, email, adminId, adminTenantId, adminEmail] = process.argv.slice(2);
  assert.ok(adminEmail, 'Usage: weekly-summary.cjs origin artifacts scope superId superTenant superEmail adminId adminTenant adminEmail');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], posts: [], clicks: [], repeatedKeys: [], states: [] };
  const contexts = [];
  let mode = 'immediate', held = null;

  async function newPage(identity, width) {
    const context = await browser.newContext({ viewport: { width, height: 900 }, extraHTTPHeaders: {
      'X-ASAP-Test-Staff-Id': identity.id, 'X-ASAP-Test-Tenant-Id': identity.tenant, 'X-ASAP-Test-Staff-Email': identity.email
    } });
    contexts.push(context);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests += 1; return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
    // Every accepted submission uses Kestrel/SQL/Hangfire. Only the response
    // acknowledgement is held or lost to exercise the real client journal.
    await page.route('**/api/asap/staff/workflow/weekly-summary/run-now?*', async route => {
      const url = new URL(route.request().url());
      assert.equal(route.request().method(), 'POST');
      assert.equal(url.searchParams.get('force'), 'true');
      const post = { operationId: url.searchParams.get('operationId'), scope: url.searchParams.get('organizationId') || 'all' };
      report.posts.push(post);
      assert.match(post.operationId, /^[0-9a-f-]{36}$/i);
      const response = await route.fetch();
      assert.equal(response.status(), 202, await response.text());
      const result = await response.json();
      assert.equal(result.manualRunId, post.operationId);
      post.jobId = result.jobId;
      if (mode === 'hold') {
        held.dispatched.resolve(); await held.release.promise;
      } else if (mode === 'lose') {
        return route.abort('failed');
      }
      return route.fulfill({ response });
    });
    await page.goto(`${origin}/staff/?stage=operations`, { waitUntil: 'networkidle' });
    await page.locator('#operations-view').waitFor({ state: 'visible' });
    assert.equal(await page.getByRole('button', { name: 'Run weekly summary', exact: true }).count(), 1);
    assert.equal(await page.locator('#force-weekly-now').count(), 0);
    return page;
  }

  async function settled(page) {
    await page.locator('#run-weekly-now:not([disabled])').waitFor();
    await page.locator('#refresh-operations:not([disabled])').waitFor();
    await page.locator('#app-status').filter({ hasText: `Weekly summary Run ${report.posts.at(-1).operationId} queued.` }).waitFor();
    assert.doesNotMatch(await page.locator('#app-status').textContent(), /forced|sent|delivered/i);
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
    const page = await newPage({ id: staffId, tenant: tenantId, email }, 1280);
    const run = page.getByRole('button', { name: 'Run weekly summary', exact: true });
    await page.locator('#operations-scope').selectOption(scope);
    await page.locator('#refresh-operations:not([disabled])').waitFor();
    await page.locator('[data-view="settings"]').click();
    await page.locator('#settings-form').waitFor({ state: 'visible' });
    await page.locator('#settings-scope').selectOption('system');
    await page.locator('[data-view="operations"]').click();
    await page.locator('#refresh-operations:not([disabled])').waitFor();
    assert.equal(await page.locator('#operations-scope').inputValue(), scope, 'Settings scope cannot redirect the run');

    held = { dispatched: deferred(), release: deferred() }; mode = 'hold';
    await run.dblclick(); await held.dispatched.promise;
    assert.equal(report.posts.length, 1, 'pending native double-click queues one operation');
    assert.equal(await run.isDisabled(), true);
    assert.equal(await page.locator('#operations-scope').isDisabled(), true);
    assert.equal(await page.locator('#send-test-email').isDisabled(), true);
    held.release.resolve(); mode = 'immediate'; await settled(page);

    // Deliver a successful acknowledgement between the two physical clicks.
    // The second native event has clickCount/detail 2 even though the button
    // is already enabled. This is the fast-response double-click regression.
    await run.evaluate(node => node.addEventListener('click', event => {
      (window.weeklyClicks ||= []).push({ detail: event.detail, trusted: event.isTrusted });
    }));
    const bounds = await run.boundingBox();
    const point = { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
    await page.mouse.click(point.x, point.y, { clickCount: 1 }); await settled(page);
    assert.equal(report.posts.length, 2);
    // mouse.click({ clickCount: 2 }) emits a new two-click sequence. Send only
    // the second press/release here, preserving the original gesture's count.
    await page.mouse.down({ clickCount: 2 });
    await page.mouse.up({ clickCount: 2 });
    report.clicks = await page.evaluate(() => window.weeklyClicks);
    report.fastAckState = await page.evaluate(() => ({ disabled: document.querySelector('#run-weekly-now').disabled,
      status: document.querySelector('#app-status').textContent,
      operations: Object.entries(sessionStorage).filter(([key]) => key.startsWith('asap.staff.operation.')) }));
    assert.equal(await run.isDisabled(), false);
    assert.deepEqual(report.clicks, [{ detail: 1, trusted: true }, { detail: 2, trusted: true }]);
    assert.equal(report.posts.length, 2, 'a fast acknowledgement does not turn a double-click into two runs');

    await run.focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
    const focus = await run.evaluate(node => ({ focused: node === document.activeElement,
      visible: node.matches(':focus-visible'), outline: getComputedStyle(node).outlineWidth }));
    assert.deepEqual(focus, { focused: true, visible: true, outline: '3px' });
    await run.evaluate(node => node.addEventListener('keydown', event => {
      if (event.repeat) (window.weeklyRepeats ||= []).push({ key: event.key, prevented: event.defaultPrevented, trusted: event.isTrusted });
    }));
    await page.keyboard.down('Enter'); await settled(page);
    assert.equal(report.posts.length, 3);
    await run.focus();
    await page.keyboard.down('Enter'); await page.keyboard.down('Enter'); await page.keyboard.up('Enter');
    report.repeatedKeys = await page.evaluate(() => window.weeklyRepeats);
    assert.deepEqual(report.repeatedKeys, Array.from({ length: 2 }, () => ({ key: 'Enter', prevented: true, trusted: true })));
    assert.equal(await run.isDisabled(), false);
    assert.equal(report.posts.length, 3, 'held Enter repeats cannot create new operations after acknowledgement');
    await scan(page, 'weekly-summary-desktop-keyboard');

    await page.setViewportSize({ width: 390, height: 900 });
    await run.focus(); await page.keyboard.press('Space'); await settled(page);
    assert.equal(report.posts.length, 4, 'a new keyboard gesture after release is new work');
    assert.equal(new Set(report.posts.map(post => post.operationId)).size, 4);
    assert.ok(report.posts.every(post => post.scope === scope));
    await scan(page, 'weekly-summary-mobile-queued');

    mode = 'lose'; await run.press('Enter');
    await page.locator('#operations-outcome').filter({ hasText: 'outcome is unconfirmed' }).waitFor();
    assert.equal(report.posts.length, 5);
    const lost = { ...report.posts[4] };
    assert.equal(await run.isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Retry same operation', exact: true }).count(), 0);
    mode = 'immediate';
    await page.reload({ waitUntil: 'networkidle' });
    const retry = page.getByRole('button', { name: 'Retry same operation', exact: true });
    await retry.waitFor({ state: 'visible' });
    assert.equal(report.posts.length, 5, 'reload reviews the retained operation without resending');
    assert.equal(await page.locator('#operations-scope').inputValue(), scope, 'reload retains the addressable Run scope');
    const aggregateReview = new URL(page.url()); aggregateReview.searchParams.set('runScope', 'all');
    await page.goto(aggregateReview.href, { waitUntil: 'networkidle' });
    await retry.waitFor({ state: 'visible' });
    assert.equal(await page.locator('#operations-scope').inputValue(), 'all', 'an explicit aggregate view cannot replace original retry authority');
    await page.locator('#refresh-operations').press('Enter');
    await retry.waitFor({ state: 'visible' });
    assert.equal(report.posts.length, 5, 'explicit refresh remains read-only');
    await scan(page, 'weekly-summary-mobile-recovery');
    await retry.press('Enter');
    await page.locator('#run-weekly-now:not([disabled])').waitFor();
    await page.locator('#refresh-operations:not([disabled])').waitFor();
    // The acknowledgement belongs to the original library, while an explicit
    // aggregate view is selected. Scope guards suppress a foreign-scope toast;
    // confirmed journal cleanup and the real response establish this retry.
    assert.equal(await page.evaluate(() => Object.keys(sessionStorage).some(key => key.startsWith('asap.staff.operation.'))), false);
    assert.equal(report.posts.length, 6);
    assert.equal(report.posts[5].operationId, lost.operationId);
    assert.equal(report.posts[5].scope, lost.scope, 'retry uses captured library despite the reloaded aggregate view');
    assert.equal(await page.locator('#operations-scope').inputValue(), 'all');
    await run.press('Space'); await settled(page);
    assert.equal(report.posts.length, 7);
    assert.equal(report.posts[6].scope, 'all');
    assert.notEqual(report.posts[6].operationId, lost.operationId);

    const adminPage = await newPage({ id: adminId, tenant: adminTenantId, email: adminEmail }, 1280);
    assert.equal(await adminPage.locator('#operations-scope-field').isHidden(), true);
    await adminPage.getByRole('button', { name: 'Run weekly summary', exact: true }).press('Enter'); await settled(adminPage);
    assert.equal(report.posts.length, 8);
    assert.equal(report.posts[7].scope, scope, 'ordinary admin actions remain locked to their own library');
    await scan(adminPage, 'weekly-summary-admin-desktop');
    assert.equal(report.externalRequests, 0);
    assert.deepEqual(report.pageErrors, []);
    console.log('Weekly summary native gestures, repeated runs, lost acknowledgement, retry, scope, keyboard and responsive checks passed.');
  } finally {
    if (held) held.release.resolve();
    for (const context of contexts) await context.close();
    await browser.close();
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
