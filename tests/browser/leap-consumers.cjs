'use strict';
const { rejectNativeDialogs } = require('./confirmation.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const [baseURL, artifacts, staffId, tenantId, email, requestId, legacyId, copyId] = process.argv.slice(2);

(async () => {
  const browser = await chromium.launch({ headless: true });
  const report = { externalRequests: [], errors: [], states: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 },
      extraHTTPHeaders: { 'X-ASAP-Test-Staff-Id': staffId, 'X-ASAP-Test-Tenant-Id': tenantId, 'X-ASAP-Test-Staff-Email': email } });
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
      report.externalRequests.push(route.request().url()); return route.abort();
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors);
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
    const checkAnchor = async (anchor, href) => {
      await anchor.waitFor();
      assert.equal(await anchor.getAttribute('href'), href);
      assert.equal(await anchor.getAttribute('target'), '_blank');
      assert.equal(await anchor.getAttribute('rel'), 'noopener noreferrer');
    };
    const bib = 'https://leap.example.test/#bib/9001/9001';
    const patron = 'https://leap.example.test/#patron/7001';
    await page.goto(`${baseURL}/staff/?stage=pending_hold&scope=2`);
    const titleRow = page.locator('#request-grid tr').filter({ has: page.locator(`a[data-request-id="${requestId}"]`) });
    await checkAnchor(titleRow.locator('a[href*="#bib/"]'), bib);
    await checkAnchor(titleRow.locator('a[href*="#patron/"]'), patron);
    await titleRow.locator('a[href*="#patron/"]').focus();
    assert.equal(await page.locator('#request-dialog').evaluate(node => node.open), false, 'external links have independent keyboard targets');
    await titleRow.locator('a.grid-open').click();
    await page.locator('#request-dialog[open]').waitFor();
    await checkAnchor(page.locator('#request-dialog .research-section a[href*="#bib/"]'), bib);
    await checkAnchor(page.locator('#request-dialog dd a[href*="#patron/"]').first(), patron);
    await scan('title-detail'); await page.keyboard.press('Escape');
    await page.locator('[data-status="suggestion"]').click();
    const legacyRow = page.locator('#request-grid tr').filter({ has: page.locator(`a[data-request-id="${legacyId}"]`) });
    await legacyRow.waitFor(); assert.equal(await legacyRow.locator('a[href*="#patron/"]').count(), 0);
    await page.locator('[data-view="additional-copies"]').click();
    const copyRow = page.locator('#additional-copy-grid tr').filter({ has: page.locator(`button[data-copy-id="${copyId}"]`) });
    await checkAnchor(copyRow.locator('a[href*="#bib/"]'), bib);
    assert.equal(await copyRow.locator('a[href*="#patron/"]').count(), 0, 'copy tasks have no patron identity');
    await copyRow.locator('button.grid-open').click();
    await checkAnchor(page.locator('#request-dialog dd a[href*="#bib/"]'), bib);
    await page.setViewportSize({ width: 390, height: 844 }); await scan('copy-mobile');
    await page.keyboard.press('Escape'); await page.locator('[data-view="queue"]').click();
    await page.locator('#new-suggestion').click();
    const dialog = page.locator('#staff-suggestion-dialog');
    const servicing = dialog.locator('select[aria-label="Servicing library"]');
    await servicing.selectOption('2');
    await dialog.locator('input[type="search"]').fill('20000000000001');
    const verified = page.waitForResponse(response => response.url().endsWith('/patron-lookup') && response.request().method() === 'POST');
    await dialog.locator('button[type="submit"]').click();
    const lookup = await (await verified).json();
    const nativeId = lookup.patron.research.patronId;
    await checkAnchor(dialog.locator('#staff-patron-card-title a'), `https://leap.example.test/#patron/${nativeId}`);
    await dialog.locator('#staff-patron-card-title').scrollIntoViewIfNeeded();
    await scan('selected-patron'); await page.keyboard.press('Escape');

    // Defensive rendering against unusable legacy metadata is a separate
    // controlled response probe. Positive surfaces above used real SQL/API data.
    await page.route('**/api/asap/staff/additional-copies?*', async route => {
      const response = await route.fetch(); const data = await response.json();
      for (const row of data.items) row.research = { leapBibUrlPattern: 'javascript:alert({{bibid}})' };
      await route.fulfill({ response, json: data });
    });
    await page.locator('[data-view="additional-copies"]').click();
    await page.locator('#refresh-additional-copies').click();
    await page.locator('#refresh-additional-copies:not([disabled])').waitFor();
    assert.equal(await page.locator('#additional-copy-grid a').count(), 0, 'malformed destinations stay plain text');
    assert.deepEqual(report.externalRequests, []); assert.deepEqual(report.errors, []);
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await context.close();
  } finally { await browser.close(); }
  console.log('LEAP queue/detail/copy/selected-patron consumers passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
