'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const verifyMixedFieldLayout = require('./form-layout.cjs');
const [baseURL, artifacts, staffId, tenantId, email, sourceId, library, barcode, pickup, title] = process.argv.slice(2);

(async () => {
  const browser = await chromium.launch({ headless: true });
  const report = { externalRequests: [], errors: [], lookups: [], states: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 },
      extraHTTPHeaders: { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email } });
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
      report.externalRequests.push(route.request().url()); return route.abort();
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.errors.push(error.message));
    page.on('request', request => {
      if (request.url().endsWith('/patron-lookup') && request.method() === 'POST') report.lookups.push(request.postDataJSON());
    });
    const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
    const scan = async state => {
      await page.evaluate(axe);
      const result = await page.evaluate(async () => ({
        violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
        overflow: document.documentElement.scrollWidth > innerWidth,
        images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
      }));
      assert.deepEqual(result.violations, []); assert.equal(result.overflow, false); assert.equal(result.images, true);
      report.states.push({ state, ...result });
      await page.screenshot({ path: path.join(artifacts, `${state}.png`), fullPage: true });
    };
    const dialog = page.locator('#staff-suggestion-dialog');
    const quick = () => page.getByRole('button', { name: `New suggestion for patron ${barcode} from request ${sourceId}`, exact: true });
    const verifyFresh = async () => {
      await dialog.getByRole('button', { name: 'Create suggestion', exact: true }).waitFor();
      assert.doesNotMatch(await page.locator('#app-status').textContent(), /Refreshing current Polaris/);
      const card = dialog.locator('.staff-patron-card');
      const text = await card.textContent();
      assert.match(text, /Fresh Browser Patron/); assert.match(text, /fresh-browser@example.org/);
      assert.match(text, /Fresh registered branch/); assert.match(text, /Recorded home library/);
      assert.match(text, /Browser servicing library/); assert.doesNotMatch(text, /Old Snapshot|old-snapshot@example.org|Historical ASAP pickup/);
      assert.equal(await dialog.getByLabel(/^Title/).inputValue(), '');
      assert.equal(await dialog.getByLabel(/^Author/).inputValue(), '');
      assert.equal(await dialog.getByLabel(/Identifier number/i).inputValue(), '');
      assert.equal(await dialog.getByLabel('Preferred pickup location', { exact: true }).inputValue(), '');
      const describedBy = await dialog.getByLabel('Preferred pickup location', { exact: true }).getAttribute('aria-describedby');
      assert.match(describedBy, /staff-suggestion-pickup-warning/);
      assert.match(describedBy, /staff-suggestion-pickup-refreshed/);
      assert.equal(await dialog.getByLabel('Preferred pickup location', { exact: true }).getAttribute('aria-required'), 'true');
      assert.match(await dialog.locator('#staff-suggestion-pickup-warning').textContent(), /missing or ineligible/i);
      assert.equal(await dialog.locator('#staff-suggestion-pickup-warning').isVisible(), true);
      assert.ok(await dialog.locator('#staff-suggestion-pickup-refreshed time').getAttribute('datetime'));
      assert.match(await dialog.locator('#staff-suggestion-pickup-help').textContent(), /Unfilled requests use the live preference when each hold is placed/i);
    };
    await page.goto(`${baseURL}/staff/?stage=queue&scope=${library}&status=suggestion`);
    await quick().waitFor(); await quick().focus(); await page.keyboard.press('Enter');
    await verifyFresh();
    assert.equal(await dialog.getByLabel(/^Title/).evaluate(node => node === document.activeElement), true);
    assert.equal(report.lookups.length, 1);
    assert.equal(report.lookups[0].barcode, barcode); assert.equal(report.lookups[0].libraryOrgId, Number(library));
    assert.equal(report.lookups[0].query, null, 'quick create uses explicit identity rather than name fallback');
    report.mixedFields = await verifyMixedFieldLayout(page, '#staff-suggestion-body');
    const aligned = await dialog.evaluate(root => {
      const date = root.querySelector('input[type="date"]'), pickup = root.querySelector('[aria-label="Preferred pickup location"]');
      return { date: date.getBoundingClientRect().toJSON(), pickup: pickup.getBoundingClientRect().toJSON() };
    });
    assert.ok(Math.abs(aligned.date.top - aligned.pickup.top) <= 1, 'Exact publication date and pickup start at the same height.');
    await dialog.getByRole('button', { name: 'Search Polaris catalog', exact: true }).click();
    assert.equal(await page.locator('#polaris-query').inputValue(), '');
    await page.keyboard.press('Escape');
    assert.equal(await dialog.getByRole('button', { name: 'Search Polaris catalog', exact: true }).evaluate(node => document.activeElement === node), true);
    await scan('quick-create-desktop');
    await page.evaluate(() => { document.documentElement.style.zoom = '2'; });
    await scan('quick-create-zoom-200');
    await page.evaluate(() => { document.documentElement.style.zoom = ''; });
    await page.setViewportSize({ width: 390, height: 844 }); await scan('quick-create-mobile');
    await verifyMixedFieldLayout(page, '#staff-suggestion-body');
    await page.keyboard.press('Escape');
    assert.equal(await quick().evaluate(node => node === document.activeElement), true);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByRole('button', { name: `Open request ${sourceId}`, exact: true }).click();
    await page.locator('#request-dialog[open]').waitFor();
    assert.match(await page.locator('#request-dialog').textContent(), /Recorded ASAP pickup/);
    assert.match(await page.locator('#request-dialog').textContent(), /Historical ASAP pickup/);
    assert.match(await page.locator('#request-dialog').textContent(), /live Polaris pickup preference/);
    await page.locator('#request-dialog').getByRole('button', { name: `New suggestion for patron ${barcode} from request ${sourceId}`, exact: true }).click();
    await verifyFresh(); assert.equal(report.lookups.length, 2);
    await page.keyboard.press('Escape');
    assert.equal(await quick().evaluate(node => node === document.activeElement), true);

    await page.locator('#library-scope').selectOption('all');
    await page.locator('#refresh-queue:not([disabled])').waitFor();
    await quick().click();
    const scope = dialog.getByRole('combobox', { name: 'Servicing library', exact: true });
    assert.equal(await scope.inputValue(), '');
    assert.equal(await dialog.getByRole('searchbox').inputValue(), barcode);
    assert.equal(report.lookups.length, 2, 'All scope cannot silently select a servicing library');
    await scan('quick-create-all-scope');
    await scope.selectOption(library); await verifyFresh();
    assert.equal(report.lookups.length, 3);
    await dialog.getByLabel('Material format', { exact: true }).selectOption('book');
    await dialog.getByLabel(/^Title/).fill(title);
    await dialog.getByLabel(/^Author/).fill('Current staff author');
    await dialog.getByLabel(/Identifier number/i).fill('BROWSER-MATCH');
    await dialog.getByLabel(/Publication Timing/i).selectOption({ index: 1 });
    await dialog.getByLabel('Preferred pickup location', { exact: true }).selectOption(pickup);
    const catalog = dialog.getByRole('button', { name: 'Search Polaris catalog', exact: true });
    await catalog.click();
    assert.equal(await page.locator('#polaris-mode').inputValue(), 'title');
    assert.equal(await page.locator('#polaris-query').inputValue(), title);
    assert.equal(await page.locator('#polaris-author').inputValue(), '');
    await page.locator('#polaris-query').fill('Temporary query'); await page.keyboard.press('Escape');
    assert.equal(await dialog.getByLabel(/^Title/).inputValue(), title);
    assert.equal(await dialog.getByLabel(/^Author/).inputValue(), 'Current staff author');
    assert.equal(await dialog.getByLabel(/Identifier number/i).inputValue(), 'BROWSER-MATCH');
    await catalog.click(); assert.equal(await page.locator('#polaris-query').inputValue(), title); await page.keyboard.press('Escape');
    assert.equal(await dialog.getByLabel('Email patron confirmation (optional)', { exact: true }).isChecked(), false);
    const accepted = page.waitForResponse(response => response.url().endsWith('/staff/suggestions') && response.request().method() === 'POST');
    const detailLoaded = page.waitForResponse(response => /\/staff\/title-requests\/\d+$/.test(new URL(response.url()).pathname) && response.request().method() === 'GET');
    await dialog.getByRole('button', { name: 'Create suggestion', exact: true }).click();
    const response = await accepted; assert.equal(response.status(), 201, await response.text());
    const created = await response.json(); assert.equal(typeof created.id, 'string');
    assert.equal(created.notificationStatus, 'not_requested');
    const acceptedDetail = await (await detailLoaded).json();
    assert.equal(acceptedDetail.id, created.id);
    await page.locator('#request-dialog[open]').waitFor();
    assert.ok((await page.locator('#request-dialog').textContent()).includes(acceptedDetail.title));
    await page.keyboard.press('Escape');
    await page.locator('#library-scope').selectOption(library);
    await page.locator('#refresh-queue:not([disabled])').waitFor();
    assert.equal(await page.locator('#library-scope').inputValue(), library);
    assert.equal(await page.locator('#status-tabs [aria-selected="true"]').getAttribute('data-status'), 'suggestion');
    const claims = page.locator('#claim-filter');
    await claims.selectOption('all');
    const row = page.locator('#request-grid tr').filter({ has: page.getByRole('button', { name: `Open request ${created.id}`, exact: true }) });
    await row.waitFor(); assert.match(await row.textContent(), /Browser claimant/); assert.match(await row.textContent(), /Duplicate suggestion/i);
    await claims.selectOption('mine_unclaimed');
    await row.waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: `Open request ${sourceId}`, exact: true }).waitFor();
    await claims.selectOption('all');
    await row.waitFor();
    await scan('created-queue');
    assert.deepEqual(report.externalRequests, []); assert.deepEqual(report.errors, []);
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await context.close();
  } finally { await browser.close(); }
  console.log('Staff queue/detail quick create, fresh context, explicit All scope and queue outcome passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
