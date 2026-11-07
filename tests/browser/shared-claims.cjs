'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { activateRowAction } = require('../acceptance/helpers/request-actions.cjs');
const { rejectNativeDialogs } = require('./confirmation.cjs');
const [baseURL, artifacts] = process.argv.slice(2);
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const choices = ['all', 'mine_unclaimed', 'mine', 'unclaimed'];
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };

(async () => {
  const fixture = JSON.parse(await fs.readFile(path.join(artifacts, 'fixture.json'), 'utf8'));
  const headers = actor => ({ 'X-ASAP-Test-Staff-Id': actor.id, 'X-ASAP-Test-Tenant-Id': actor.tenantId, 'X-ASAP-Test-Staff-Email': actor.email });
  const report = { matrix: [], states: [], external: [], errors: [], mutation: false, reload: false, actorReplacement: false };
  const browser = await chromium.launch({ headless: true });
  const axe = await fs.readFile(require.resolve('axe-core/axe.min.js'), 'utf8');
  const setup = async (actor, init) => {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: headers(actor) });
    if (init) await context.addInitScript(init);
    await context.route('**/*', route => {
      if (new URL(route.request().url()).origin === new URL(baseURL).origin) return route.continue();
      report.external.push(route.request().url()); return route.abort();
    });
    context.on('page', page => { page.on('pageerror', error => report.errors.push(error.message)); rejectNativeDialogs(page, report.errors); });
    return context;
  };
  const selected = async (page, expected) => {
    await page.waitForFunction(expected => document.querySelector('#claim-filter')?.value === expected &&
      document.querySelector('#additional-copy-claim-filter')?.value === expected, expected);
    assert.equal(await page.locator('#claim-filter').inputValue(), expected);
    assert.equal(await page.locator('#additional-copy-claim-filter').inputValue(), expected);
  };
  const rows = async (page, { copy = false, scope = fixture.library, status = 'suggestion', choice, actor = fixture.first }) => {
    await selected(page, choice);
    const expected = (copy ? fixture.copies : fixture.titles).filter(item => (scope === 'all' || item.scope === scope) && item.status === status &&
      (choice === 'all' || choice === 'mine' && item.claimedBy === actor.id || choice === 'unclaimed' && !item.claimedBy ||
       choice === 'mine_unclaimed' && (!item.claimedBy || item.claimedBy === actor.id))).map(item => item.id).sort();
    const selector = copy ? '#additional-copy-grid .additional-copy-open' : '#request-grid .request-title-link';
    const totals = scope === 'all' ? fixture.allCounts : stages.map(stage => fixture.titles.filter(item => item.scope === scope && item.status === stage).length);
    await page.waitForFunction(({ selector, expected, copy, totals }) =>
      !document.querySelector(copy ? '#refresh-additional-copies' : '#refresh-queue').disabled &&
      JSON.stringify([...document.querySelectorAll(selector)].map(node => node.dataset[copy ? 'copyId' : 'requestId']).sort()) === JSON.stringify(expected) &&
      (copy || JSON.stringify([...document.querySelectorAll('#status-tabs .status-count')].map(node => node.textContent)) ===
        JSON.stringify(totals.map(total => `(${total})`))), { selector, expected, copy, totals });
    assert.deepEqual(await page.locator(selector).evaluateAll((nodes, copy) => nodes.map(node => node.dataset[copy ? 'copyId' : 'requestId']).sort(), copy), expected);
    if (!copy) {
      assert.deepEqual(await page.locator('#status-tabs .status-count').allTextContents(), totals.map(total => `(${total})`));
    }
  };
  const view = async (page, copy, status) => {
    await page.locator(`[data-view="${copy ? 'additional-copies' : 'queue'}"]`).click();
    await page.locator(copy ? '#additional-copy-view' : '#queue-view').waitFor({ state: 'visible' });
    await page.locator(copy ? `#additional-copy-status-tabs [data-copy-status="${status}"]` : `#status-tabs [data-status="${status}"]`).click();
  };
  const matrix = async (page, scope, actor) => {
    for (const choice of choices) {
      await view(page, false, 'suggestion'); await page.locator('#claim-filter').selectOption(choice);
      for (const status of stages) {
        await page.locator(`#status-tabs [data-status="${status}"]`).click();
        await rows(page, { scope, status, choice, actor }); report.matrix.push({ queue: 'requests', scope, actor: actor.id, status, choice });
      }
      for (const status of ['open', 'closed']) {
        await view(page, true, status); await rows(page, { copy: true, scope, status, choice, actor });
        await page.locator('#additional-copy-claim-filter').selectOption(choice);
        report.matrix.push({ queue: 'copies', scope, actor: actor.id, status, choice });
      }
    }
  };
  const scan = async (page, state) => {
    await page.evaluate(axe);
    const result = await page.evaluate(async () => ({
      violations: (await axe.run()).violations.filter(item => ['serious', 'critical'].includes(item.impact)),
      overflow: document.documentElement.scrollWidth > innerWidth,
      images: [...document.images].filter(node => node.getClientRects().length).every(node => node.complete && node.naturalWidth > 0)
    }));
    assert.deepEqual(result.violations, []); assert.equal(result.overflow, false); assert.equal(result.images, true);
    report.states.push({ state, ...result }); await page.screenshot({ path: path.join(artifacts, `${state}.png`), fullPage: true });
  };
  const saveDefault = async (page, enabled) => {
    await page.locator('#profile-button').click(); await page.locator('#mine-default').setChecked(enabled);
    const response = page.waitForResponse(response => response.url().endsWith('/api/asap/staff/profile') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Save profile', exact: true }).click();
    const saved = await response; assert.equal(saved.status(), 200, await saved.text());
    await page.waitForFunction(() => document.querySelector('#app-status').textContent === 'Profile saved.');
  };
  try {
    const context = await setup(fixture.first), page = await context.newPage();
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`);
    await rows(page, { choice: 'mine_unclaimed' });
    await saveDefault(page, false); await view(page, false, 'suggestion'); await rows(page, { choice: 'all' });
    await page.locator('#claim-filter').selectOption('all');
    await saveDefault(page, true); await view(page, false, 'suggestion'); await rows(page, { choice: 'all' });
    await page.reload(); await rows(page, { choice: 'all' }); report.reload = true;
    await matrix(page, fixture.library, fixture.first);
    await page.locator('#additional-copy-library-scope').selectOption(fixture.otherLibrary);
    await matrix(page, fixture.otherLibrary, fixture.first);
    const allResponse = await context.request.get(`${baseURL}/api/asap/staff/title-requests?scope=all`);
    assert.equal(allResponse.status(), 200); const all = await allResponse.json(); assert.equal(all.scope, 'all');
    fixture.allCounts = stages.map(status => all.items.filter(item => item.status === status).length);
    await page.locator('#additional-copy-claim-filter').selectOption('mine');
    await page.locator('#additional-copy-library-scope').selectOption('all');
    await rows(page, { copy: true, scope: 'all', status: 'closed', choice: 'mine' });
    await view(page, false, 'suggestion'); await rows(page, { scope: 'all', choice: 'mine' });
    await view(page, true, 'open'); await rows(page, { copy: true, scope: 'all', status: 'open', choice: 'mine' }); report.allScope = true;
    await page.locator('#additional-copy-library-scope').selectOption(fixture.library);
    await page.locator('#additional-copy-claim-filter').selectOption('mine');
    await view(page, true, 'open'); await rows(page, { copy: true, status: 'open', choice: 'mine' });
    await page.reload(); await rows(page, { copy: true, status: 'open', choice: 'mine' });
    await page.locator('#additional-copy-grid .additional-copy-open').click();
    await page.locator('#request-dialog[open]').waitFor(); await page.keyboard.press('Escape');
    await page.locator('#request-dialog[open]').waitFor({ state: 'hidden' }); await rows(page, { copy: true, status: 'open', choice: 'mine' });
    await view(page, false, 'suggestion'); await rows(page, { choice: 'mine' });
    await page.locator('#request-grid .request-title-link').click(); await page.locator('#request-dialog[open] .edit-form').waitFor();
    await page.keyboard.press('Escape'); await page.locator('#request-dialog[open]').waitFor({ state: 'hidden' }); await rows(page, { choice: 'mine' });
    await page.locator('#refresh-queue').click(); await rows(page, { choice: 'mine' });
    await page.locator('#claim-filter').selectOption('unclaimed'); await rows(page, { choice: 'unclaimed' });
    const mutationRow = page.locator(`#request-grid [data-request-id="${fixture.mutationId}"]`).locator('xpath=ancestor::tr');
    const mutation = page.waitForResponse(response => response.request().method() === 'POST' && response.url().includes(`/title-requests/${fixture.mutationId}/`));
    await activateRowAction(mutationRow, 'claim');
    const committed = await mutation; assert.equal(committed.status(), 200, await committed.text());
    fixture.titles.find(item => item.id === fixture.mutationId).claimedBy = fixture.first.id;
    await page.locator('#request-dialog[open]').getByRole('button', { name: 'Unclaim', exact: true }).waitFor();
    await rows(page, { choice: 'unclaimed' }); report.mutation = true;
    await page.keyboard.press('Escape'); await page.locator('#request-dialog[open]').waitFor({ state: 'hidden' });
    await page.locator('#claim-filter').selectOption('mine'); await rows(page, { choice: 'mine' }); await scan(page, 'claims-desktop');
    const held = { started: deferred(), release: deferred(), completed: deferred() }; let holdNext = true;
    await context.route('**/api/asap/staff/title-requests?*', async route => {
      if (!holdNext) return route.continue(); holdNext = false;
      const response = await route.fetch(); held.started.resolve(); await held.release.promise;
      try { await route.fulfill({ response }); } catch (error) { if (!/closed|canceled|cancelled|invalid interception/i.test(error.message)) throw error; }
      finally { held.completed.resolve(); }
    });
    await page.locator('#refresh-queue').click(); await held.started.promise;
    await context.setExtraHTTPHeaders(headers(fixture.second));
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.otherLibrary}`);
    await rows(page, { choice: 'all', actor: fixture.second });
    held.release.resolve(); await held.completed.promise;
    await rows(page, { choice: 'all', actor: fixture.second }); report.actorReplacement = true;
    assert.equal(await page.locator('#scope-field').isVisible(), false);
    for (const requested of ['all', fixture.otherLibrary]) {
      for (const queue of ['title-requests', 'additional-copies']) {
        const response = await context.request.get(`${baseURL}/api/asap/staff/${queue}?scope=${requested}`);
        assert.equal(response.status(), 200); const result = await response.json();
        assert.equal(result.scope, fixture.library); assert.ok(result.items.every(item => String(item.libraryOrgId) === fixture.library));
      }
    }
    await matrix(page, fixture.library, fixture.second);
    await page.setViewportSize({ width: 390, height: 844 }); await scan(page, 'claims-mobile-ordinary');
    await page.locator('#sign-out').click(); await page.locator('#signed-out').waitFor({ state: 'visible' });
    await context.setExtraHTTPHeaders(headers(fixture.first));
    await page.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`); await rows(page, { choice: 'mine' }); report.signOut = true;
    await context.close();

    const malformed = await setup(fixture.first); const malformedPage = await malformed.newPage();
    await malformedPage.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`);
    await malformedPage.evaluate(actor => {
      sessionStorage.setItem('asap.staff.claims.' + encodeURIComponent(JSON.stringify([actor.tenantId, actor.id, actor.email])),
        JSON.stringify({ staff: JSON.stringify([actor.tenantId, actor.id, actor.email]), value: 'unknown' }));
    }, fixture.first);
    await malformedPage.reload(); await rows(malformedPage, { choice: 'mine_unclaimed' }); report.malformed = true; await malformed.close();
    const disabled = await setup(fixture.first, () => { Object.defineProperty(window, 'sessionStorage', { get() { throw new Error('Storage disabled'); } }); });
    const disabledPage = await disabled.newPage(); await disabledPage.goto(`${baseURL}/staff/?stage=suggestion&scope=${fixture.library}`);
    await rows(disabledPage, { choice: 'mine_unclaimed' }); await disabledPage.locator('#claim-filter').selectOption('unclaimed');
    await view(disabledPage, true, 'open'); await rows(disabledPage, { copy: true, status: 'open', choice: 'unclaimed' });
    await disabledPage.reload(); await rows(disabledPage, { copy: true, status: 'open', choice: 'mine_unclaimed' });
    report.storageDisabled = 'Shared in-page choice works; reload falls back to Profile when browser storage is disabled.';
    await disabled.close(); assert.deepEqual(report.external, []); assert.deepEqual(report.errors, []);
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2)); await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
