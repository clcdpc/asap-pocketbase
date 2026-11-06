'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { rejectNativeDialogs, decideConfirmation } = require('./confirmation.cjs');

async function main() {
  const [origin, artifacts, staffId, tenantId, email] = process.argv.slice(2);
  assert.ok(email, 'Usage: weekly-profile.cjs origin artifacts staffId tenantId email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
      ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const headers = { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email };
  const report = { externalRequests: 0, pageErrors: [], posts: [], states: [] };
  const contexts = [];
  let conflictNext = false, primaryForNextSession;

  async function bind(context, { missingPrimary = false, primaryEmail } = {}) {
    contexts.push(context);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++; return route.abort('blockedbyclient');
    });
    const page = context.pages()[0] || await context.newPage();
    page.on('pageerror', error => report.pageErrors.push(error.message));
    rejectNativeDialogs(page, report.pageErrors);
    await page.route('**/api/asap/staff/profile', async route => {
      const body = route.request().postDataJSON(); report.posts.push(body);
      assert.equal(Object.hasOwn(body, 'notificationEmail'), false);
      const response = await route.fetch();
      if (conflictNext) {
        conflictNext = false;
        assert.equal(response.status(), 200, 'The first real SQL mutation advances rowversion.');
        const stale = await route.fetch();
        assert.equal(stale.status(), 409, 'The same original version must conflict in SQL.');
        primaryForNextSession = 'conflict-review@example.org';
        return route.fulfill({ response: stale });
      }
      return route.fulfill({ response });
    });
    await page.route('**/api/asap/staff/session', async route => {
      const response = await route.fetch();
      const session = await response.json();
      if (primaryEmail !== undefined) session.staff.notificationEmail = primaryEmail;
      if (missingPrimary) session.staff.notificationEmail = null;
      if (primaryForNextSession !== undefined) {
        session.staff.notificationEmail = primaryForNextSession;
        primaryForNextSession = undefined;
      }
      return route.fulfill({ response, json: session });
    });
    await page.goto(`${origin}/staff/?stage=profile`, { waitUntil: 'networkidle' });
    await page.locator('#profile-view:not([hidden])').waitFor();
    return page;
  }

  async function scan(page, name) {
    await page.evaluate(axe);
    const state = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      imagesLoaded: [...document.images].filter(image => image.getClientRects().length)
        .every(image => image.complete && image.naturalWidth > 0)
    }));
    report.states.push({ name, ...state });
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    assert.deepEqual(state.violations, [], name);
    assert.ok(state.scrollWidth <= state.width, `Horizontal overflow: ${name}`);
    assert.equal(state.imagesLoaded, true, `Missing image: ${name}`);
  }

  async function save(page) {
    const response = page.waitForResponse(item => item.url().endsWith('/api/asap/staff/profile') && item.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save profile', exact: true }).focus();
    await page.keyboard.press('Enter'); await response;
    await page.locator('#app-status').filter({ hasText: /^Profile saved\.$/ }).waitFor();
    await page.getByRole('button', { name: 'Save profile', exact: true }).waitFor({ state: 'visible' });
  }

  try {
    const page = await bind(await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers }));
    const primary = page.locator('#notification-email');
    assert.equal(await page.getByText('Primary notification email', { exact: true }).count(), 1);
    assert.equal(await primary.textContent(), 'primary@example.org');
    assert.equal(await page.locator('#notification-email').evaluate(node => node.tagName), 'DD');
    assert.equal(await page.locator('input#notification-email').count(), 0, 'primary account text is not an input');
    assert.equal(await page.locator('#notification-email').getAttribute('tabindex'), null, 'account text is not a keyboard tab stop');
    assert.equal(await page.locator('#notification-email').getAttribute('aria-labelledby'), 'notification-email-label');
    assert.equal(await page.locator('#notification-email').getAttribute('aria-describedby'), 'notification-email-help');
    const selectedPrimary = await page.locator('#notification-email').evaluate(node => {
      const range = document.createRange(); range.selectNodeContents(node);
      const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
      return selection.toString();
    });
    assert.equal(selectedPrimary, 'primary@example.org', 'account email remains selectable and copyable');
    const missingPage = await bind(await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers }),
      { missingPrimary: true });
    assert.equal(await missingPage.locator('#notification-email').textContent(),
      'No primary notification email is set.');
    assert.equal(await missingPage.locator('input#notification-email').count(), 0);
    await scan(missingPage, 'missing-primary');
    const longAddress = `${'long-address-segment-'.repeat(12)}@example.org`;
    const longPrimaryPage = await bind(await browser.newContext({ viewport: { width: 390, height: 844 }, extraHTTPHeaders: headers }),
      { primaryEmail: longAddress });
    assert.equal(await longPrimaryPage.locator('#notification-email').textContent(), longAddress);
    await scan(longPrimaryPage, 'long-primary-mobile');
    const field = page.getByRole('textbox', { name: 'Additional weekly summary email (optional)', exact: true });
    assert.equal(await field.inputValue(), '', 'Absent preference remains blank.');
    assert.equal(await field.getAttribute('aria-describedby'), 'weekly-email-help');
    const help = await page.locator('#weekly-email-help').textContent();
    assert.match(help, /primary notification email/); assert.match(help, /distinct additional address receives a copy/);
    assert.match(help, /leave blank for primary only/); assert.match(help, /Turning off weekly summaries prevents both/);
    await field.fill('invalid');
    await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    assert.equal(await field.evaluate(node => node.validity.typeMismatch), true);
    assert.equal(report.posts.length, 0, 'Browser email validation prevents an invalid mutation.');
    await field.fill(''); await save(page);
    assert.equal(report.posts.at(-1).weeklyActionSummaryEmail, null);
    await field.focus(); await page.keyboard.press('Tab');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'weekly-enabled');
    await field.fill('additional@example.org'); await save(page);
    await page.reload({ waitUntil: 'networkidle' });
    assert.equal(await field.inputValue(), 'additional@example.org');
    assert.equal(await page.locator('#weekly-enabled').isChecked(), true);
    await scan(page, 'desktop');

    await field.fill('draft@example.org');
    await decideConfirmation(page, () => page.getByRole('button', { name: 'Requests', exact: true }).click(), dialog => dialog.dismiss());
    assert.equal(await field.inputValue(), 'draft@example.org');
    await decideConfirmation(page, () => page.getByRole('button', { name: 'Requests', exact: true }).click(), dialog => dialog.accept());
    await page.getByRole('button', { name: 'Profile', exact: true }).click();
    assert.equal(await field.inputValue(), 'additional@example.org');
    await field.fill('conflict@example.org'); conflictNext = true;
    const conflict = page.waitForResponse(item => item.url().endsWith('/api/asap/staff/profile') && item.status() === 409);
    await page.getByRole('button', { name: 'Save profile', exact: true }).click(); await conflict;
    await page.locator('#app-status').filter({ hasText: /changed|stale/i }).waitFor();
    assert.equal(await field.inputValue(), 'conflict@example.org', 'Conflict recovery reloads authoritative SQL preferences.');
    assert.equal(await page.locator('#notification-email').textContent(), 'conflict-review@example.org',
      'authoritative conflict recovery refreshes primary account text while the mutation is being reviewed');
    await field.fill('additional@example.org'); await page.locator('#weekly-enabled').uncheck(); await save(page);
    assert.equal(report.posts.at(-1).weeklyActionSummaryEnabled, false);
    assert.equal(await page.locator('#notification-email').textContent(), 'primary@example.org',
      'the next accepted session refresh replaces the prior authoritative account snapshot');
    await page.reload({ waitUntil: 'networkidle' });
    assert.equal(await field.inputValue(), 'additional@example.org');
    assert.equal(await page.locator('#weekly-enabled').isChecked(), false);
    await page.emulateMedia({ colorScheme: 'dark' }); await scan(page, 'desktop-dark');

    const mobile = await bind(await browser.newContext({ viewport: { width: 390, height: 844 }, extraHTTPHeaders: headers }));
    await scan(mobile, 'mobile');
    const extension = path.join(__dirname, 'native-zoom-extension');
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'zoom-profile'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers,
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, '--window-size=1280,900']
    });
    const zoomPage = await bind(zoomContext);
    const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
    const ratio = await zoomPage.evaluate(() => devicePixelRatio);
    const zoom = await worker.evaluate(async appOrigin => {
      const tab = (await chrome.tabs.query({})).find(tab => tab.url.startsWith(appOrigin));
      await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
      await chrome.tabs.setZoom(tab.id, 2); return chrome.tabs.getZoom(tab.id);
    }, origin);
    assert.equal(zoom, 2);
    await zoomPage.waitForFunction(previous => Math.abs(devicePixelRatio / previous - 2) < 0.01, ratio);
    await scan(zoomPage, 'native-200-percent');
    assert.equal(report.externalRequests, 0); assert.deepEqual(report.pageErrors, []);
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
