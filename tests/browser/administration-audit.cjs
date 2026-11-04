'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { decideConfirmation, rejectNativeDialogs } = require('./confirmation.cjs');

async function main() {
  const [origin, artifacts, scope, staffId, tenantId, email, adminId, adminTenant, adminEmail] = process.argv.slice(2);
  assert.ok(adminEmail, 'Usage: administration-audit.cjs origin artifacts scope staff tenant email admin tenant email');
  assert.equal(new URL(origin).origin, origin);
  await fs.mkdir(artifacts, { recursive: true });
  const browser = await chromium.launch({ headless: true, ...(process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.ASAP_TEST_CHROMIUM_EXECUTABLE_PATH } : {}) });
  const axeSource = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const report = { externalRequests: 0, pageErrors: [], writes: 0, states: [], auditReads: [], queueScrolling: [], focus: [] }, contexts = [];
  async function newPage(width, theme, ordinary = false) {
    const context = await browser.newContext({ viewport: { width, height: 1000 }, colorScheme: theme,
      timezoneId: 'America/New_York', locale: 'en-US', extraHTTPHeaders: {
        'X-ASAP-Test-Staff-Id': ordinary ? adminId : staffId,
        'X-ASAP-Test-Tenant-Id': ordinary ? adminTenant : tenantId,
        'X-ASAP-Test-Staff-Email': ordinary ? adminEmail : email
      } });
    contexts.push(context);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === origin) return route.continue();
      report.externalRequests++; return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.on('pageerror', error => report.pageErrors.push(error.message)); rejectNativeDialogs(page, report.pageErrors);
    page.on('request', request => {
      const url = new URL(request.url());
      if (request.method() !== 'GET' && request.method() !== 'HEAD') report.writes++;
      if (url.pathname.endsWith('/staff/audit')) report.auditReads.push(Object.fromEntries(url.searchParams));
    });
    return page;
  }
  async function settled(page) {
    await page.waitForFunction(() => document.querySelector('#audit-status')?.textContent !== 'Loading administration audit…' &&
      !document.querySelector('#audit-results')?.hasAttribute('aria-busy'));
  }
  async function auditRead(page, action, filter = () => true) {
    const response = page.waitForResponse(response => response.request().method() === 'GET' &&
      new URL(response.url()).pathname === '/api/asap/staff/audit' && filter(new URL(response.url()).searchParams));
    await action(); const result = await response;
    assert.equal(result.status(), 200, await result.text()); await settled(page);
    return (await result.json()).data;
  }
  async function assertFocus(page, id, witness) {
    const actual = await page.evaluate(() => {
      const node = document.activeElement, r = node.getBoundingClientRect();
      return { id: node.id, top: r.top, bottom: r.bottom, left: r.left, right: r.right,
        width: r.width, height: r.height, viewportWidth: innerWidth, viewportHeight: innerHeight,
        auditRows: document.querySelectorAll('#audit-results tbody tr').length };
    });
    // A long region cannot fit completely, but its focused start edge must be
    // visible. Buttons/inputs/headings must fit entirely in the viewport.
    const visible = actual.width > 0 && actual.height > 0 && actual.left >= -1 && actual.right <= actual.viewportWidth + 1 &&
      actual.top >= -1 && (id === 'audit-results' && actual.height > actual.viewportHeight
        ? actual.top <= actual.viewportHeight - 24 : actual.bottom <= actual.viewportHeight + 1);
    report.focus.push({ witness, ...actual, visible });
    assert.equal(actual.id, id, witness);
    assert.equal(visible, true, `${witness}: focus must be visible ${JSON.stringify(actual)}`);
  }
  async function heldRead(page, pathname, action, during, { fail = false, filter = () => true } = {}) {
    let release, start;
    const gate = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { start = resolve; });
    const pattern = `**${pathname}*`;
    let intercepted = false;
    await page.route(pattern, async route => {
      if (intercepted || !filter(new URL(route.request().url()).searchParams)) return route.continue();
      intercepted = true;
      const response = await route.fetch(); start(); await gate;
      if (fail) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Delayed read failed.' }) });
      return route.fulfill({ response });
    });
    const response = page.waitForResponse(item => new URL(item.url()).pathname === pathname && filter(new URL(item.url()).searchParams));
    try {
      await action(); await started; await during(); release();
      assert.equal((await response).status(), fail ? 503 : 200);
      await settled(page); await page.locator('#refresh-operations:not([disabled])').waitFor();
    } finally { release(); await page.unroute(pattern); }
  }
  async function verifyQueueScroll(page, name) {
    const region = page.locator('#queue-progress-table');
    await region.locator('tbody tr').first().waitFor();
    const metrics = await region.evaluate(node => {
      const table = node.querySelector('table'), row = table.tBodies[0].rows[0];
      const canvas = document.createElement('canvas'), context = canvas.getContext('2d');
      const fits = cell => {
        const style = getComputedStyle(cell); context.font = style.font;
        const available = cell.getBoundingClientRect().width - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        return cell.textContent.trim().split(/\s+/).every(word => context.measureText(word).width <= available + 1);
      };
      return { regionWidth: node.clientWidth, tableWidth: table.getBoundingClientRect().width,
        headingText: [...table.tHead.rows[0].cells].map(cell => cell.textContent),
        state: row.cells[2].textContent,
        wordsFit: [...table.tHead.rows[0].cells, ...row.cells].every(fits),
        pageWidth: innerWidth, pageScrollWidth: document.documentElement.scrollWidth };
    });
    assert.equal(metrics.state, 'Completed'); assert.equal(metrics.wordsFit, true, `${name}: queue words remain readable`);
    assert.ok(metrics.tableWidth >= 850); assert.ok(metrics.regionWidth < metrics.tableWidth);
    assert.ok(metrics.pageScrollWidth <= metrics.pageWidth, name);
    await region.evaluate(node => { node.scrollIntoView({ block: 'start' }); node.scrollLeft = 0; });
    await region.focus(); await assertFocus(page, 'queue-progress-table', `${name}: keyboard reaches scroll region`);
    await page.screenshot({ path: path.join(artifacts, `${name}-queue-start-viewport.png`) });
    await region.press('ArrowRight');
    await page.waitForFunction(() => document.querySelector('#queue-progress-table').scrollLeft > 0);
    metrics.keyboardScrollLeft = await region.evaluate(node => node.scrollLeft);
    await region.evaluate(node => { node.scrollLeft = node.scrollWidth - node.clientWidth; });
    await page.screenshot({ path: path.join(artifacts, `${name}-queue-end-viewport.png`) });
    report.queueScrolling.push({ name, ...metrics });
    await region.evaluate(node => { node.scrollLeft = 0; });
  }
  async function scan(page, name) {
    await page.evaluate(() => { window.scrollTo(0, 0); });
    await page.evaluate(axeSource);
    const violations = await page.evaluate(async () => (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)));
    const geometry = await page.evaluate(() => {
      const visible = node => { const r = node.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(node).visibility !== 'hidden'; };
      const clipped = [];
      for (const node of document.querySelectorAll('#operations-view button, #operations-view input, #operations-view select, #operations-view summary')) {
        if (!visible(node)) continue;
        const r = node.getBoundingClientRect(); let scrolling = false;
        for (let parent = node.parentElement; parent; parent = parent.parentElement) {
          const style = getComputedStyle(parent), bounds = parent.getBoundingClientRect();
          if (['auto', 'scroll'].includes(style.overflowX) && parent.scrollWidth > parent.clientWidth) {
            scrolling = true;
            if (r.width > parent.clientWidth + 1) clipped.push({ id: node.id, reason: 'control wider than its scrollport' });
            break;
          }
          if (['hidden', 'clip'].includes(style.overflowX) && (r.left < bounds.left - 1 || r.right > bounds.right + 1)) {
            clipped.push({ id: node.id, reason: 'hidden by ancestor', ancestor: parent.tagName }); break;
          }
        }
        if (!scrolling && (r.left < -1 || r.right > innerWidth + 1)) clipped.push({ id: node.id, reason: 'outside viewport', left: r.left, right: r.right });
      }
      return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth, clipped,
        imagesLoaded: [...document.images].filter(visible).every(image => image.complete && image.naturalWidth > 0),
        controls: [...document.querySelectorAll('#operations-view button, #audit-filters input, #audit-filters select')]
          .filter(visible).map(node => { const r = node.getBoundingClientRect(); return { id: node.id, x: r.x, y: r.y, width: r.width, height: r.height }; }),
        columns: [...document.querySelectorAll('#audit-results th')].map(node => ({ text: node.textContent, width: node.getBoundingClientRect().width })) };
    });
    await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true });
    if (name.endsWith('-audit')) {
      await page.evaluate(() => { window.scrollTo(0, 0); });
      await page.screenshot({ path: path.join(artifacts, `${name}-viewport.png`) });
      if (geometry.width <= 390) {
        await page.locator('#audit-results').evaluate(node => { node.scrollIntoView({ block: 'start' }); node.scrollLeft = 0; });
        await page.screenshot({ path: path.join(artifacts, `${name}-table-viewport.png`) });
        await page.locator('#audit-results').evaluate(node => { node.scrollLeft = node.scrollWidth - node.clientWidth; });
        await page.screenshot({ path: path.join(artifacts, `${name}-change-viewport.png`) });
        await page.locator('#audit-results').evaluate(node => { node.scrollLeft = 0; });
        await page.evaluate(() => { window.scrollTo(0, 0); });
      }
    }
    await fs.writeFile(path.join(artifacts, `${name}-geometry.json`), JSON.stringify({ violations, ...geometry }, null, 2));
    report.states.push({ name, violations, geometry });
    assert.deepEqual(violations, [], name); assert.deepEqual(geometry.clipped, [], `Clipped controls: ${name}`);
    assert.ok(geometry.scrollWidth <= geometry.width, `Page overflow: ${name}`); assert.equal(geometry.imagesLoaded, true, name);
  }
  try {
    for (const theme of ['light', 'dark']) {
      for (const width of [1440, 390]) {
        const page = await newPage(width, theme), prefix = `${theme}-${width}`;
        await page.goto(`${origin}/staff/?stage=operations&opSection=audit&auditScope=${scope}`, { waitUntil: 'networkidle' });
        await page.locator('#operations-audit-panel').waitFor({ state: 'visible' }); await settled(page);
        assert.equal(await page.locator('#audit-scope').inputValue(), scope);
        assert.deepEqual(await page.locator('.operations-sections [role=tab]').allTextContents(), ['Workflow', 'Email operations', 'Administration audit']);
        assert.deepEqual(await page.locator('#audit-results th').allTextContents(), ['When', 'Performed by', 'Library / scope', 'Change']);
        assert.equal(await page.locator('#audit-results tbody tr').count(), 25);
        assert.equal(await page.locator('#audit-results img, #audit-results script, #audit-results b').count(), 0, 'recorded HTML stays text');
        assert.match(await page.locator('#audit-results').textContent(), /Recorded actor <b>history<\/b>/);
        assert.doesNotMatch(await page.locator('#audit-results').textContent(), /private-secret-token/);
        assert.equal(await page.getByRole('button', { name: 'Refresh operations', exact: true }).count(), 1);
        const detail = page.locator('#audit-results details').first();
        await detail.locator('summary').focus(); await detail.locator('summary').press('Enter');
        assert.equal(await detail.getAttribute('open'), '');
        await detail.locator('summary').press('Escape'); assert.equal(await detail.getAttribute('open'), null);
        assert.equal(await detail.locator('summary').evaluate(node => node === document.activeElement), true);
        await page.locator('#operations-title').focus();
        await page.locator('#audit-results').evaluate(node => { node.scrollLeft = 0; });
        await scan(page, `${prefix}-audit`);

        let data = await auditRead(page, () => page.locator('#audit-search').fill('older-than-200'), query => query.get('search') === 'older-than-200');
        assert.equal(data.count, 1); assert.match(await page.locator('#audit-results').textContent(), /older-than-200/);
        await auditRead(page, () => page.locator('#audit-clear').click());
        await auditRead(page, () => page.locator('#audit-scope').selectOption(scope));
        await auditRead(page, () => page.locator('#audit-category').selectOption('staff'));
        data = await auditRead(page, () => page.locator('#audit-actor').selectOption(staffId)); assert.ok(data.count >= 261);
        await auditRead(page, () => page.locator('#audit-sort').selectOption('oldest'));
        assert.match(await page.locator('#audit-results tbody tr').first().textContent(), /older-than-200/);
        data = await auditRead(page, () => page.locator('#audit-next').click()); assert.equal(data.page, 2);
        assert.match(await page.locator('#audit-page').textContent(), /Showing 26–50/);
        await auditRead(page, () => page.locator('#audit-prev').click());
        await auditRead(page, () => page.locator('#audit-category').selectOption('organizations'));
        await auditRead(page, () => page.locator('#audit-from').fill('2026-11-01'));
        data = await auditRead(page, () => page.locator('#audit-through').fill('2026-11-01')); assert.equal(data.count, 2);
        assert.equal(report.auditReads.at(-1).fromUtc, '2026-11-01T04:00:00.000Z');
        assert.equal(report.auditReads.at(-1).beforeUtc, '2026-11-02T05:00:00.000Z');
        await page.reload({ waitUntil: 'networkidle' }); await settled(page);
        assert.equal(await page.locator('#audit-from').inputValue(), '2026-11-01');
        assert.match(await page.locator('#audit-count').textContent(), /2 matching entries/);
        await auditRead(page, () => page.locator('#audit-search').fill('no-such-audit-entry'));
        assert.match(await page.locator('#audit-results').textContent(), /No audit entries match/);
        await scan(page, `${prefix}-empty`);

        let fail = true;
        await page.route('**/api/asap/staff/audit?*', route => {
          if (fail) { fail = false; return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Audit fixture is temporarily unavailable.' }) }); }
          return route.continue();
        });
        await page.locator('#audit-clear').click();
        await page.locator('#audit-status').filter({ hasText: 'temporarily unavailable' }).waitFor();
        assert.equal(await page.locator('#audit-next').isDisabled(), true);
        assert.equal(await page.locator('#audit-count').textContent(), '');
        await scan(page, `${prefix}-error`);
        await page.unroute('**/api/asap/staff/audit?*');
        await auditRead(page, () => page.locator('#refresh-operations').click());

        await page.locator('#operations-audit-tab').focus(); await page.keyboard.press('Home');
        assert.equal(await page.locator('#operations-workflow-tab').getAttribute('aria-selected'), 'true');
        assert.equal(await page.locator('#operations-workflow-tab').evaluate(node => node === document.activeElement), true);
        assert.equal(await page.getByRole('button', { name: 'Run weekly summary', exact: true }).count(), 1);
        assert.equal(await page.locator('#force-weekly-now').count(), 0);
        await page.locator('#operations-scope').selectOption(scope); await page.locator('#refresh-operations:not([disabled])').waitFor();
        await scan(page, `${prefix}-workflow`);
        if (width === 390) {
          await verifyQueueScroll(page, prefix);
          await page.setViewportSize({ width: 320, height: 1000 });
          await scan(page, `${theme}-320-workflow`); await verifyQueueScroll(page, `${theme}-320`);
          await page.setViewportSize({ width, height: 1000 });
        }
        await page.locator('#operations-email-tab').click();
        await page.locator('#email-view-scope').selectOption(scope); await page.locator('#refresh-operations:not([disabled])').waitFor();
        assert.deepEqual((await page.locator('#email-operations-table th').allTextContents()).slice(0, 2), ['Reference', 'Created']);
        assert.match(await page.locator('#email-operations-table').textContent(), /recipient_domain_not_allowed/);
        assert.match(await page.locator('#email-operations-table').textContent(), /Provider accepted/);
        assert.equal(await page.locator('#operations-email-test [data-email-test=refresh], #operations-email-test [data-email-test=operations]').count(), 0);
        if (width === 1440) {
          assert.equal(await page.locator('#email-operations-table td:nth-child(2)').first().evaluate(node => node.getBoundingClientRect().height < 75), true);
        }
        await scan(page, `${prefix}-email`);
        await page.locator('#operations-email-tab').focus(); await page.keyboard.press('End'); await settled(page);
        assert.equal(new URL(page.url()).searchParams.get('opSection'), 'audit');
        await page.goBack({ waitUntil: 'networkidle' });
        await page.locator('#operations-email-panel').waitFor({ state: 'visible' });
        assert.equal(await page.locator('#email-view-scope').inputValue(), scope);
        await page.close();
      }
    }

    const keyboard = await newPage(1440, 'light');
    const keyboardUrl = `${origin}/staff/?stage=operations&opSection=audit&auditScope=${scope}`;
    await keyboard.goto(keyboardUrl, { waitUntil: 'networkidle' }); await settled(keyboard);
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    await heldRead(keyboard, '/api/asap/staff/audit', async () => {
      await keyboard.locator('#audit-next').focus();
      await assertFocus(keyboard, 'audit-next', 'Next visible before full-page loading');
      await keyboard.locator('#audit-next').press('Enter');
    }, async () => {
      await assertFocus(keyboard, 'audit-results', 'Next loading handoff');
      assert.equal(await keyboard.locator('#audit-next').isDisabled(), true);
    }, { filter: query => query.get('page') === '2' });
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    await assertFocus(keyboard, 'audit-next', 'Next restored when another page remains');
    await keyboard.screenshot({ path: path.join(artifacts, 'focus-next-visible-viewport.png') });
    await auditRead(keyboard, async () => { await keyboard.locator('#audit-prev').focus(); await keyboard.locator('#audit-prev').press('Space'); });
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    assert.equal(await keyboard.locator('#audit-prev').isDisabled(), true);
    await assertFocus(keyboard, 'audit-results', 'Previous first-page boundary');
    await keyboard.screenshot({ path: path.join(artifacts, 'focus-first-boundary-visible-viewport.png') });
    await keyboard.goto(`${keyboardUrl}&auditPage=999999`, { waitUntil: 'networkidle' }); await settled(keyboard);
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    assert.equal(await keyboard.locator('#audit-next').isDisabled(), true);
    await auditRead(keyboard, async () => { await keyboard.locator('#audit-prev').focus(); await keyboard.locator('#audit-prev').press('Enter'); });
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    await assertFocus(keyboard, 'audit-prev', 'Previous restored when an earlier page remains');
    await keyboard.screenshot({ path: path.join(artifacts, 'focus-previous-visible-viewport.png') });
    await auditRead(keyboard, async () => { await keyboard.locator('#audit-next').focus(); await keyboard.locator('#audit-next').press('Enter'); });
    assert.equal(await keyboard.locator('#audit-next').isDisabled(), true);
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    await assertFocus(keyboard, 'audit-results', 'Next last-page boundary');
    await keyboard.screenshot({ path: path.join(artifacts, 'focus-last-boundary-visible-viewport.png') });
    await heldRead(keyboard, '/api/asap/staff/audit', async () => {
      await keyboard.locator('#audit-prev').focus(); await keyboard.locator('#audit-prev').press('Enter');
    }, async () => {
      await assertFocus(keyboard, 'audit-results', 'Previous delayed loading handoff');
      await keyboard.locator('#audit-search').focus();
    });
    assert.equal(await keyboard.locator('#audit-results tbody tr').count(), 25);
    await assertFocus(keyboard, 'audit-search', 'Previous completion respects deliberate user focus movement');
    await heldRead(keyboard, '/api/asap/staff/audit', async () => {
      await keyboard.locator('#audit-next').focus(); await keyboard.locator('#audit-next').press('Enter');
    }, async () => { await assertFocus(keyboard, 'audit-results', 'Failed page loading handoff'); }, { fail: true });
    await assertFocus(keyboard, 'audit-results', 'Failed page retains stable result focus');
    await keyboard.screenshot({ path: path.join(artifacts, 'focus-failure-visible-viewport.png') });
    for (const scenario of ['success', 'failure', 'moved', 'background']) {
      await heldRead(keyboard, '/api/asap/staff/workflow/queues', async () => {
        if (scenario === 'background') {
          await keyboard.locator('#audit-search').focus();
          await keyboard.evaluate(() => document.querySelector('#refresh-operations').click());
        } else {
          await keyboard.locator('#refresh-operations').focus(); await keyboard.locator('#refresh-operations').press('Enter');
        }
      }, async () => {
        assert.equal(await keyboard.locator('#refresh-operations').isDisabled(), true);
        await assertFocus(keyboard, scenario === 'background' ? 'audit-search' : 'operations-title', `Main refresh ${scenario} loading`);
        if (scenario === 'moved') await keyboard.locator('#operations-email-tab').focus();
      }, { fail: scenario === 'failure' });
      await assertFocus(keyboard, scenario === 'moved' ? 'operations-email-tab' : scenario === 'background' ? 'audit-search' : 'refresh-operations',
        `Main refresh ${scenario} completion`);
      if (scenario === 'success') await keyboard.screenshot({ path: path.join(artifacts, 'focus-main-refresh-visible-viewport.png') });
    }
    await scan(keyboard, 'keyboard-refresh-and-paging');

    const race = await newPage(1440, 'light');
    await race.goto(`${origin}/staff/?stage=operations&opSection=audit&auditScope=${scope}`, { waitUntil: 'networkidle' }); await settled(race);
    let release, begun;
    const gate = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { begun = resolve; });
    await race.route('**/api/asap/staff/audit?*', async route => {
      const params = new URL(route.request().url()).searchParams;
      if (params.get('scope') !== scope || params.get('search') !== 'older-than-200') return route.continue();
      const response = await route.fetch(); begun(); await gate;
      try { await route.fulfill({ response }); } catch { /* The client aborts a retired read. */ }
    });
    await race.locator('#audit-search').fill('older-than-200'); await started;
    await race.locator('#audit-status').filter({ hasText: 'Loading administration audit' }).waitFor();
    assert.equal(await race.locator('#audit-results').textContent(), 'Loading administration audit…');
    await auditRead(race, () => race.locator('#audit-scope').selectOption('system'));
    release(); await race.waitForTimeout(100);
    assert.doesNotMatch(await race.locator('#audit-results').textContent(), /older-than-200/);
    await race.unroute('**/api/asap/staff/audit?*');
    await auditRead(race, () => race.locator('#audit-search').fill('i434-fixture'));
    assert.match(await race.locator('#audit-count').textContent(), /2 matching entries/);
    await scan(race, 'scope-race-system');

    await race.goto(`${origin}/staff/?stage=settings&settingsScope=system#settings-staff`, { waitUntil: 'networkidle' });
    await race.locator('#staff-add-toggle').click();
    await race.locator('#staff-add-email').fill('unsaved-person@example.org');
    await decideConfirmation(race, () => race.locator('#staff-audit-history').press('Enter'), dialog => dialog.dismiss());
    assert.equal(await race.locator('#settings-view').isVisible(), true);
    assert.equal(await race.locator('#staff-add-email').inputValue(), 'unsaved-person@example.org');
    await decideConfirmation(race, () => race.locator('#staff-audit-history').press('Enter'), dialog => dialog.accept());
    await race.waitForFunction(() => document.activeElement === document.querySelector('#audit-search'));
    assert.equal(await race.locator('#audit-scope').inputValue(), 'all'); assert.equal(await race.locator('#audit-category').inputValue(), 'staff');
    assert.equal(await race.locator('#settings-staff-audit-list, #staff-audit-refresh').count(), 0);
    await auditRead(race, () => race.locator('#audit-clear').click());
    assert.equal(await race.locator('#audit-category').inputValue(), '');
    await race.setViewportSize({ width: 320, height: 900 }); await scan(race, 'light-320-controls');

    const ordinary = await newPage(390, 'dark', true);
    const readsBefore = report.auditReads.length;
    await ordinary.goto(`${origin}/staff/?stage=operations&opSection=audit&auditScope=all`, { waitUntil: 'networkidle' }); await settled(ordinary);
    assert.equal(await ordinary.locator('#audit-scope').inputValue(), scope); assert.equal(await ordinary.locator('#audit-scope').isDisabled(), true);
    assert.equal(await ordinary.locator('#audit-scope option').count(), 1);
    assert.ok(report.auditReads.slice(readsBefore).every(read => read.scope === scope));
    assert.doesNotMatch(await ordinary.locator('#audit-results').textContent(), /i434-system-actor/);
    await ordinary.goto(`${origin}/staff/?stage=settings&settingsScope=${scope}#settings-staff`, { waitUntil: 'networkidle' });
    await ordinary.locator('#staff-audit-history').click(); await ordinary.waitForFunction(() => document.activeElement === document.querySelector('#audit-search'));
    assert.equal(await ordinary.locator('#audit-scope').inputValue(), scope); assert.equal(await ordinary.locator('#audit-category').inputValue(), 'staff');
    await scan(ordinary, 'dark-390-ordinary-admin');
    assert.equal(report.externalRequests, 0); assert.deepEqual(report.pageErrors, []); assert.equal(report.writes, 0);
    console.log('Administration audit full-history filters, sections, scopes, drafts, race and visual guards passed.');
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    for (const context of contexts) await context.close();
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
