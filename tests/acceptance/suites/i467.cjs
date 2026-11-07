'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const { decideConfirmation } = require('../../browser/confirmation.cjs');
const endpoint = '/api/asap/staff/search';
const groups = ['title:suggestion', 'title:outstanding_purchase', 'title:pending_hold',
  'title:hold_placed', 'title:closed', 'copy:open', 'copy:closed'];
const summary = page => page.locator('#search-dialog [data-search="summary"]');
const input = page => page.locator('#search-quick-input');
const key = row => row.type + ':' + row.id;
const write = (t, value) => fs.writeFile(path.join(t.directory, t.id + '-evidence.json'), JSON.stringify(value, null, 2));

async function start(t, stage = 'suggestion', scope = '2') {
  await t.page.goto(`${t.origin}/staff/?stage=${stage}&scope=${scope}${stage === 'settings' ? '&settingsScope=2' : ''}`, { waitUntil: 'networkidle' });
  await t.page.locator('#workspace').waitFor({ state: 'visible' });
  await t.page.locator('#search-asap').waitFor({ state: 'visible' });
  // The launcher is static markup; it can be visible before the session-owned
  // Search controller and the requested destination have finished mounting.
  await input(t.page).waitFor({ state: 'attached' });
  const view = { suggestion: '#queue-view', additional_copies: '#additional-copy-view',
    analytics: '#analytics-view', operations: '#operations-view', settings: '#settings-view' }[stage];
  await t.page.locator(view).waitFor({ state: 'visible' });
  if (stage === 'suggestion') await t.page.locator('#request-grid tbody tr').first().waitFor();
  if (stage === 'settings') await t.page.waitForFunction(() => document.querySelector('#settings-version').value &&
    !document.querySelector('#settings-form').inert && !document.querySelector('#settings-refresh').disabled);
}
async function open(t, keyboard = false) {
  if (keyboard) await t.page.keyboard.press('Control+k');
  else await t.page.locator('#search-asap').click();
  await t.page.locator('#search-dialog[open]').waitFor();
  assert.equal(await input(t.page).evaluate(node => node === document.activeElement), true);
}
async function search(t, query) {
  const response = t.page.waitForResponse(r => new URL(r.url()).pathname === endpoint && r.request().postDataJSON()?.query === query.trim().replace(/\s+/gu, ' '));
  await input(t.page).fill(query);
  assert.equal((await response).status(), 200);
  await t.page.waitForFunction(() => /matching submission|No matching/.test(document.querySelector('#search-dialog [data-search="summary"]').textContent));
}
async function api(t, query, values = {}, expected = 200) {
  const result = await t.page.evaluate(async ({ endpoint, query, values }) => {
    const session = await (await fetch('/api/asap/staff/session')).json();
    const response = await fetch(endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json',
      'X-ASAP-Antiforgery': session.antiforgeryToken }, body: JSON.stringify({ query, scope: '2', pageSize: 50, ...values }) });
    return { status: response.status, body: await response.json() };
  }, { endpoint, query, values });
  assert.equal(result.status, expected); return result.body;
}
async function all(t, query, scope = 'all') {
  const first = await api(t, query, { scope, pageSize: 12 });
  const rows = [...first.items];
  for (let page = 2; page <= Math.ceil(first.total / 12); page++) {
    const current = await api(t, query, { scope, pageSize: 12, page });
    assert.deepEqual(current.groups, first.groups); assert.equal(current.total, first.total);
    rows.push(...current.items);
  }
  assert.equal(new Set(rows.map(key)).size, first.total);
  assert.equal(rows.length, first.total);
  assert.equal(first.groups.reduce((sum, group) => sum + group.count, 0), first.total);
  return { ...first, items: rows };
}
async function selectedDetails(t, query, title) {
  await open(t); await search(t, query);
  await input(t.page).press('ArrowDown'); await input(t.page).press('Enter');
  await t.page.locator('#request-dialog[open]').getByRole('heading', { name: title, exact: true }).waitFor();
  assert.equal(await t.page.locator('dialog[open]').count(), 1);
}
const scenarios = [
  { id: 'i467-sql-matching-counts-and-paging', writes: false,
    requirements: ['#467: real SQL seven groups, fields, literal matching, exact identifiers, stable pages and authorized library facets'], run: async t => {
      await start(t);
      const complete = await all(t, 'Synthetic');
      assert.deepEqual(complete.groups.map(row => row.key), groups);
      assert.ok(complete.groups.every(row => row.count > 0), 'Each supported status/type has actual SQL matches');
      const repeat = await all(t, '  sYnThEtIc  ');
      assert.deepEqual(repeat.items.map(key), complete.items.map(key), 'Case and whitespace preserve deterministic ordering');
      const copies = await all(t, 'Q1 copy'); assert.equal(copies.total, 26);
      assert.ok(copies.items.every(row => row.type === 'copy' && row.patron === null && row.barcode === null));
      const scoped = await all(t, 'Q1 copy', '2'); assert.equal(scoped.total, 25);
      assert.ok(scoped.items.every(row => row.libraryOrgId === 2));
      assert.ok(copies.items.some(row => row.libraryOrgId === 3));
      const filtered = await api(t, 'Q1 copy', { scope: 'all', group: 'copy:closed' });
      assert.equal(filtered.total, 26); assert.equal(filtered.filteredTotal, 1); assert.equal(filtered.items[0].bibId, 942025);
      const patron = await all(t, 'Synthetic Reviewer', '2');
      assert.ok(patron.total > 1 && patron.items.every(row => row.type === 'title' && row.patron.startsWith('Synthetic Reviewer')));
      for (const query of ['ACCEPTANCE41013', 'Q1-IDENTIFIER-25', '942025']) {
        const found = await api(t, query); assert.equal(found.total, 1);
        assert.equal(found.items[0].status, 'closed');
      }
      const closed = (await api(t, 'ACCEPTANCE41013')).items[0];
      assert.ok((await api(t, closed.id)).items.some(row => key(row) === key(closed)));
      for (const query of ['%', '_', '[', "O'Nonexistent", '不存在', '<img src=x onerror=alert(1)>']) {
        assert.equal((await api(t, query)).total, 0, 'Literal input never expands into all records');
      }
      assert.equal((await api(t, '')).total, 0);
      for (const [query, values] of [['x'.repeat(161), {}], ['\0', {}], ['Q1', { page: 0 }], ['Q1', { pageSize: 51 }],
        ['Q1', { scope: 'system' }], ['Q1', { group: 'invalid' }]]) await api(t, query, values, 400);
      await write(t, { totalSynthetic: complete.total, groups: complete.groups, q1All: copies.total, q1Library2: scoped.total,
        patronMatches: patron.total, literalCases: 6, invalidCases: 6, pageIdentitySequence: complete.items.map(key) });
    } },
  { id: 'i467-header-and-keyboard', writes: false,
    requirements: ['#467: header entry throughout staff sections, keyboard/IME/Escape/focus and no query persistence'], run: async t => {
      for (const stage of ['suggestion', 'additional_copies', 'analytics', 'operations', 'settings']) {
        await start(t, stage); await open(t, true); await search(t, 'ACCEPTANCE41013');
        await input(t.page).press('ArrowDown');
        assert.ok(await input(t.page).getAttribute('aria-activedescendant'));
        await input(t.page).evaluate(node => node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true, bubbles: true })));
        assert.equal(await t.page.locator('#search-dialog[open]').count(), 1);
        await t.page.keyboard.press('Tab');
        assert.equal(await t.page.locator('#search-dialog').evaluate(node => node.contains(document.activeElement)), true);
        await t.page.keyboard.press('Escape'); await t.page.locator('#search-dialog').waitFor({ state: 'hidden' });
        assert.equal(await t.page.locator('#search-asap').evaluate(node => node === document.activeElement), true);
        assert.ok(!t.page.url().includes('ACCEPTANCE41013'));
        assert.equal(await t.page.evaluate(() => JSON.stringify(localStorage).includes('ACCEPTANCE41013')), false);
      }
      await write(t, { sections: 5, keyboard: true, compositionDoesNotNavigate: true, queryInUrlOrLocalStorage: false });
    } },
  { id: 'i467-full-results-scope-and-filter', writes: false,
    requirements: ['#467: View all retains query/scope with authoritative counts, page boundaries and type/status filters'], run: async t => {
      await start(t, 'suggestion', 'all'); await open(t); await search(t, 'Q1 copy');
      assert.equal(await t.page.locator('#search-suggestions [role=option]').count(), 12);
      await t.page.locator('#search-dialog [data-search="all"]').click();
      await t.page.locator('#search-view').waitFor({ state: 'visible' });
      await t.page.waitForFunction(() => document.querySelector('#search-view [data-search="page"]').textContent === 'Page 1 of 2');
      assert.equal(await t.page.locator('#search-results-input').inputValue(), 'Q1 copy');
      assert.equal(await t.page.locator('#search-results-scope').inputValue(), 'all');
      const first = await t.page.locator('#search-view .search-result').allTextContents(); assert.equal(first.length, 25);
      await t.page.locator('#search-view [data-search="next"]').click();
      await t.page.waitForFunction(() => document.querySelector('#search-view [data-search="page"]').textContent === 'Page 2 of 2');
      const last = await t.page.locator('#search-view .search-result').allTextContents(); assert.equal(last.length, 1);
      assert.ok(!first.includes(last[0]));
      await t.page.locator('#search-result-filter').selectOption('copy:closed');
      await t.page.waitForFunction(() => document.querySelector('#search-view [data-search="summary"]').textContent === '1 matching submission.');
      assert.match(await t.page.locator('#search-view .search-result').innerText(), /Q1 copy 25/);
      await checkpoint(t.page, t.directory, t.id);
      await t.page.reload();
      await t.page.waitForFunction(() => document.querySelector('#search-view [data-search="summary"]').textContent ===
        'Enter a title, author, patron, barcode, or identifier to search submissions.');
      await write(t, { pageSizes: [first.length, last.length], allCount: 26, closedCount: 1, reloadStartsIdle: true });
    } },
  { id: 'i467-cross-status-details', writes: false,
    requirements: ['#467: closed title and copy selections re-fetch their typed authoritative detail paths'], run: async t => {
      await start(t); const reads = [];
      t.page.on('request', request => { const url = new URL(request.url());
        if (url.searchParams.get('searchResult') === 'true') reads.push({ path: url.pathname, scope: url.searchParams.get('scope') }); });
      await selectedDetails(t, 'ACCEPTANCE41013', 'Acceptance d1-closed');
      await checkpoint(t.page, t.directory, t.id + '-title');
      await t.page.keyboard.press('Escape'); await t.page.locator('#request-dialog').waitFor({ state: 'hidden' });
      await selectedDetails(t, 'Q1-IDENTIFIER-25', 'Q1 copy 25');
      await checkpoint(t.page, t.directory, t.id + '-copy');
      assert.equal(reads.length, 2); assert.match(reads[0].path, /title-requests\/\d+$/); assert.match(reads[1].path, /additional-copies\/\d+$/);
      await write(t, { reads });
    } },
  { id: 'i467-loading-failure-retry-and-stale', writes: false,
    requirements: ['#467: truthful loading/empty/error/retry, superseded query cancellation and closed-panel retirement'], run: async t => {
      await start(t); await open(t);
      await t.page.route('**/api/asap/staff/search', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"Synthetic failure"}' }), { times: 1 });
      await input(t.page).fill('Q1 copy');
      await t.page.waitForFunction(() => document.querySelector('#search-dialog [data-search="summary"]').textContent.startsWith('Search unavailable.'));
      assert.doesNotMatch(await summary(t.page).innerText(), /No matching/);
      await checkpoint(t.page, t.directory, t.id + '-failed');
      await t.page.locator('#search-dialog [data-search="retry"]').click();
      await t.page.waitForFunction(() => document.querySelector('#search-dialog [data-search="summary"]').textContent === '25 matching submissions.');
      let release, started; const gate = new Promise(done => { release = done; }); const seen = new Promise(done => { started = done; });
      await t.page.route('**/api/asap/staff/search', async route => {
        const response = await route.fetch(); started(); await gate;
        try { await route.fulfill({ response }); } catch (error) { if (!/closed|cancel|invalid interception/i.test(error.message)) throw error; }
      }, { times: 1 });
      await input(t.page).fill('Synthetic'); await seen;
      assert.match(await summary(t.page).innerText(), /Searching authorized/);
      await search(t, 'ACCEPTANCE41013'); release();
      await t.page.waitForTimeout(250);
      assert.equal(await summary(t.page).innerText(), '1 matching submission.');
      assert.equal(await t.page.locator('#search-suggestions [role=option]').count(), 1);
      await search(t, 'NoSuchSyntheticSearchResult'); assert.match(await summary(t.page).innerText(), /No matching/);
      await t.page.keyboard.press('Escape'); await open(t); await search(t, 'Q1-IDENTIFIER-25');
      assert.equal(await summary(t.page).innerText(), '1 matching submission.');
      await write(t, { injectedFailure: true, retry: true, replacedResponseRejected: true, empty: true, reopenedFresh: true });
    } },
  { id: 'i467-settings-draft-cancel', writes: false,
    requirements: ['#467: Search selection honors Settings dirty draft and cancelling discard preserves editor and useful focus'], run: async t => {
      await start(t, 'settings'); await t.page.locator('#settings-nav-workflow').click();
      const field = t.page.locator('#patron-code-eligibility-message');
      const value = (await field.inputValue()) + ' Search acceptance unsaved draft'; await field.fill(value);
      assert.equal(await t.page.locator('#settings-save').isEnabled(), true);
      await open(t); await search(t, 'ACCEPTANCE41013'); await input(t.page).press('ArrowDown');
      await decideConfirmation(t.page, () => input(t.page).press('Enter'), dialog => dialog.dismiss());
      assert.equal(await field.inputValue(), value); assert.equal(await t.page.locator('#settings-save').isEnabled(), true);
      assert.equal(await t.page.locator('#request-dialog[open]').count(), 0);
      assert.equal(await t.page.locator('#search-asap').evaluate(node => node === document.activeElement), true);
      await checkpoint(t.page, t.directory, t.id);
      await write(t, { draftPreserved: true, detailsNotOpened: true, focusReturned: true });
    } },
  { id: 'i467-responsive-native-zoom', writes: false,
    requirements: ['#467: desktop, 390px mobile, short viewport and genuine native 200% geometry with axe/image/overflow guards'], run: async t => {
      await start(t); const geometry = [];
      for (const [name, width, height, zoom] of [['desktop', 1280, 900, 1], ['mobile', 390, 844, 1], ['short', 390, 360, 1], ['native200', 1280, 900, 2]]) {
        await t.page.setViewportSize({ width, height }); await nativeZoom(t.context, t.page, zoom);
        await open(t); await search(t, 'Acceptance'); await input(t.page).press('ArrowDown');
        const measured = await t.page.evaluate(() => {
          const list = document.querySelector('#search-suggestions'), bounds = list.getBoundingClientRect();
          const active = document.getElementById(document.querySelector('#search-quick-input').getAttribute('aria-activedescendant'));
          const title = active.querySelector('strong').getBoundingClientRect();
          return { width: innerWidth, height: innerHeight, listHeight: list.clientHeight, titleHeight: title.height,
            visibleTitle: Math.max(0, Math.min(title.bottom, bounds.bottom) - Math.max(title.top, bounds.top)) };
        });
        assert.ok(measured.listHeight >= 64); assert.ok(measured.visibleTitle >= measured.titleHeight - 1);
        await checkpoint(t.page, t.directory, t.id + '-' + name, { nativeViewport: zoom === 2,
          visible: [input(t.page), t.page.locator('#search-dialog [data-search="close"]'), t.page.locator('#search-dialog [data-search="all"]')] });
        geometry.push({ name, zoom, ...measured }); await t.page.keyboard.press('Escape');
      }
      await write(t, geometry);
    } }
];
// Each independent scenario verifies SQL fixture rows/versions and outbox evidence
// before and after, while the runner rejects all unexpected external requests.
for (const scenario of scenarios) {
  const run = scenario.run;
  scenario.run = async t => {
    const before = (await t.verify()).evidence;
    await run(t);
    const after = (await t.verify()).evidence;
    for (const field of ['titles', 'copies', 'outbox']) assert.deepEqual(after[field], before[field], `Search preserves SQL ${field}`);
    assert.equal(t.mutations.length, 0, 'Search/navigation never submits workflow mutations');
  };
}
module.exports = { id: 'i467', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: false, scenarios,
  human: [['human-i467-screen-reader', '#467/#398: actual human visual and screen-reader opening, announcements, navigation and focus review']],
  uncovered: [['i467-role-revocation-and-concurrency', '#467: staff/admin tampering, session replacement, revoked/deleted details and SQL cancellation',
    'The disposable runner uses its normal super-admin cookie. These adversarial boundaries remain covered by the retained real-SQL/direct-HTTP and controller/browser tests, not by impersonating the runner identity.'],
  ['i467-performance-volume', '#467: representative SQL cost and first-context/warm performance budgets',
    'This compact shared fixture is not the retained 12,038-row performance fixture. Keep its recorded 2.410s first-context target miss explicit.']] };
