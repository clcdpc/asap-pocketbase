'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { json, detail, openFixture } = require('../helpers/assertions.cjs');
const { requireFixture } = require('../helpers/environment.cjs');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const d1 = require('./d1.cjs');
const patronPattern = 'https://catalog.example.invalid/leap/patron/{{patron-id}}';
const patronLinks = '#request-dialog .patron-context a[target="_blank"]';
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const choices = ['all', 'mine_unclaimed', 'mine', 'unclaimed'];
const matches = (item, choice, actor) => choice === 'all' ||
  (choice === 'mine' && item.claimedByStaffUserId === actor) ||
  (choice === 'unclaimed' && !item.claimedByStaffUserId) ||
  (choice === 'mine_unclaimed' && (!item.claimedByStaffUserId || item.claimedByStaffUserId === actor));
const save = (t, name, value) => fs.writeFile(path.join(t.directory, name + '.json'), JSON.stringify(value, null, 2));

async function selected(page, choice) {
  await page.waitForFunction(choice => ['claim-filter', 'additional-copy-claim-filter']
    .every(id => document.getElementById(id)?.value === choice), choice);
}
async function view(page, copy, status) {
  await page.locator(`[data-view="${copy ? 'additional-copies' : 'queue'}"]`).click();
  await page.locator(copy ? '#additional-copy-view' : '#queue-view').waitFor({ state: 'visible' });
  await page.locator(copy ? `#additional-copy-status-tabs [data-copy-status="${status}"]`
    : `#status-tabs [data-status="${status}"]`).click();
}
async function rows(t, copy, scope, status, choice, actor) {
  await selected(t.page, choice);
  const base = copy ? 'additional-copies' : 'title-requests';
  const result = await json(t.context, t.origin, `/api/asap/staff/${base}?scope=${scope}&status=${status}`);
  assert.equal(result.scope, scope);
  const ordered = result.items.filter(item => item.status === status && matches(item, choice, actor)).map(item => String(item.id));
  const expected = [...ordered].sort();
  const grid = copy ? '#additional-copy-grid' : '#request-grid';
  const selector = grid + (copy ? ' .additional-copy-open' : ' .request-title-link');
  const summary = copy ? '#additional-copy-summary' : '#queue-summary';
  await t.page.waitForFunction(({ copy, summary, count }) =>
    !document.querySelector(copy ? '#refresh-additional-copies' : '#refresh-queue').disabled &&
    new RegExp('^' + count + ' ').test(document.querySelector(summary).textContent), { copy, summary, count: expected.length });
  // Refresh intentionally preserves the current page. Reset before asserting
  // first-page cardinality, then collect every page for the exact-row oracle.
  // The controller summary becomes ready before asynchronous Grid.js mounting;
  // wait for the pagination control rather than skipping a not-yet-mounted one.
  const first = t.page.locator(grid + ' .gridjs-pages button').filter({ hasText: /^1$/ });
  if (expected.length > 25) {
    await first.waitFor({ state: 'visible' });
    await first.click();
  }
  const actual = [];
  for (let pageIndex = 0; pageIndex < Math.max(1, Math.ceil(ordered.length / 25)); pageIndex++) {
    const expectedPage = ordered.slice(pageIndex * 25, (pageIndex + 1) * 25).sort();
    // Equal row counts are not readiness: an old status or a transient empty
    // pagination render may have the same count. Wait for the authoritative IDs.
    try {
      await t.page.waitForFunction(({ selector, copy, expectedPage }) =>
        JSON.stringify([...document.querySelectorAll(selector)].map(n => n.dataset[copy ? 'copyId' : 'requestId']).sort()) ===
        JSON.stringify(expectedPage), { selector, copy, expectedPage });
    } catch (error) {
      await save(t, t.id + '-row-mismatch', { copy, scope, status, choice, pageIndex, expectedPage,
        actual: await t.page.locator(selector).evaluateAll((nodes, copy) => nodes.map(n => n.dataset[copy ? 'copyId' : 'requestId']), copy) });
      throw error;
    }
    const ids = await t.page.locator(selector).evaluateAll((nodes, copy) => nodes.map(n => n.dataset[copy ? 'copyId' : 'requestId']), copy);
    actual.push(...ids);
    const next = t.page.locator(grid + ' button[aria-label="Next"]');
    if ((pageIndex + 1) * 25 < ordered.length) {
      assert.equal(await next.isDisabled(), false, 'All expected pages are reachable');
      await next.click();
    } else if (await next.count()) assert.equal(await next.isDisabled(), true, 'No unexpected further page');
  }
  assert.deepEqual(actual.sort(), expected, `Exact ${copy ? 'copy' : 'title'} rows: ${scope}/${status}/${choice}`);
  assert.equal(new Set(actual).size, actual.length, 'No duplicate rows across pagination');
  return { copy, scope, status, choice, ids: expected };
}
async function start(t) {
  await t.page.goto(t.origin + '/staff/?scope=2&stage=suggestion');
  await t.page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled && document.querySelector('#request-grid tbody'));
  return (await json(t.context, t.origin, '/api/asap/staff/session')).staff.id;
}
const scenarios = [
  { id: 'i479-claims-matrix', writes: false, requirements: ['#457: four Claims choices, seven statuses, exact rows, authorized scopes, replacement in either queue, refresh and same-session reload'], run: async t => {
    const actor = await start(t), matrix = [];
    for (const scope of ['2', '3', 'all']) {
      await view(t.page, false, 'suggestion');
      await t.page.locator('#library-scope').selectOption(scope);
      for (const choice of choices) {
        await view(t.page, false, 'suggestion');
        await t.page.locator('#claim-filter').selectOption(choice);
        for (const status of stages) {
          await view(t.page, false, status);
          matrix.push(await rows(t, false, scope, status, choice, actor));
        }
        for (const status of ['open', 'closed']) {
          await view(t.page, true, status);
          matrix.push(await rows(t, true, scope, status, choice, actor));
        }
      }
    }
    await t.page.locator('#additional-copy-claim-filter').selectOption('mine_unclaimed');
    await view(t.page, false, 'suggestion');
    await rows(t, false, 'all', 'suggestion', 'mine_unclaimed', actor);
    await t.page.locator('#refresh-queue').click();
    await rows(t, false, 'all', 'suggestion', 'mine_unclaimed', actor);
    await t.page.reload();
    await rows(t, false, 'all', 'suggestion', 'mine_unclaimed', actor);
    await checkpoint(t.page, t.directory, t.id);
    await save(t, t.id, matrix);
    assert.equal(matrix.length, 84);
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'i479-compact-queue', writes: false, requirements: ['#476: six removals, accessible filters, valid references, compact desktop/mobile/native-200 layout, keyboard and scrolling'], run: async t => {
    await start(t); await t.page.locator('#claim-filter').selectOption('all');
    const measurements = [];
    for (const [mode, width, zoom] of [['desktop', 1280, 1], ['mobile', 390, 1], ['native-200', 1280, 2]]) {
      await t.page.setViewportSize({ width, height: 900 });
      for (const status of stages) {
        await view(t.page, false, status);
        await t.page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled && document.querySelector('#request-grid table'));
        if (zoom === 2) await nativeZoom(t.context, t.page, 2);
        assert.equal(await t.page.locator('#status-count-help, #queue-automation, #request-table-help').count(), 0);
        for (const [name, id] of [['Claims filter', 'claim-filter'], ['Tag filter', 'tag-filter'], ['Similarity filter', 'similar-filter']]) {
          if (id === 'similar-filter' && status !== 'suggestion') continue;
          const control = t.page.getByRole('combobox', { name, exact: true });
          assert.equal(await control.getAttribute('id'), id);
          await control.focus(); await control.press('Tab'); await t.page.keyboard.press('Shift+Tab');
          assert.equal(await control.evaluate(n => n === document.activeElement && getComputedStyle(n).outlineStyle !== 'none'), true);
        }
        const geometry = await t.page.locator('#queue-view').evaluate(root => {
          const dangling = [...root.querySelectorAll('[aria-describedby], [aria-labelledby]')].flatMap(n =>
            ['aria-describedby', 'aria-labelledby'].flatMap(a => (n.getAttribute(a) || '').split(/\s+/).filter(id => id && !document.getElementById(id))));
          const labels = [...root.querySelectorAll('.queue-filters label > span')].map(n => ({ text: n.textContent, width: n.getBoundingClientRect().width, height: n.getBoundingClientRect().height }));
          const wrapper = root.querySelector('.gridjs-wrapper');
          wrapper.scrollLeft = wrapper.scrollWidth;
          return { dangling, labels, scrollLeft: wrapper.scrollLeft, scrollWidth: wrapper.scrollWidth, clientWidth: wrapper.clientWidth,
            countStatus: document.querySelector('#status-count-state').getAttribute('role') };
        });
        assert.deepEqual(geometry.dangling, []);
        assert.ok(geometry.labels.every(n => n.width <= 1 && n.height <= 1), 'Hidden headings occupy no visible label space');
        assert.equal(geometry.countStatus, 'status');
        if (geometry.scrollWidth > geometry.clientWidth + 1) assert.ok(geometry.scrollLeft > 0, 'Wide table scrolls within its wrapper');
        measurements.push({ mode, status, ...geometry });
        await checkpoint(t.page, t.directory, t.id + '-' + mode + '-' + status, zoom === 2 ? { nativeViewport: true } : {});
        if (zoom === 2) await nativeZoom(t.context, t.page, 1);
      }
    }
    await save(t, t.id, measurements);
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'i479-upper-patron-links', writes: false, requirements: ['#458: exact upper name/barcode LEAP destinations, keyboard semantics, no lower duplicate, New suggestion and responsive/native-200 layout'], run: async t => {
    const fixture = requireFixture((await t.verify()).evidence, 'draft');
    const before = await detail(t.context, t.origin, fixture);
    assert.ok(before.research.patronId > 0, 'SQL fixture supplies the authoritative patron ID');
    // The disposable default has no LEAP configuration. Supply a deterministic
    // valid URL at the read boundary without changing SQL or visiting a provider.
    await t.page.route(url => url.origin === t.origin &&
      (url.pathname === '/api/asap/staff/title-requests/' + fixture.id || url.pathname === '/api/asap/staff/research-configuration'), async route => {
      assert.equal(route.request().method(), 'GET');
      const response = await route.fetch(), body = await response.json();
      if (new URL(route.request().url()).pathname.includes('research-configuration')) body.leapPatronUrlPattern = patronPattern;
      else body.research = { ...body.research, leapPatronUrlPattern: patronPattern };
      await route.fulfill({ response, json: body });
    });
    await openFixture(t.page, t.origin, fixture);
    const expected = patronPattern.replaceAll('{{patron-id}}', String(before.research.patronId));
    const upper = t.page.locator(patronLinks);
    await upper.first().waitFor();
    assert.equal(await upper.count(), 2);
    for (const link of await upper.all()) {
      assert.equal(await link.getAttribute('href'), new URL(expected).href);
      assert.equal(await link.getAttribute('target'), '_blank');
      assert.equal(await link.getAttribute('rel'), 'noopener noreferrer');
      await link.focus(); await link.press('Tab'); await t.page.keyboard.press('Shift+Tab');
      assert.equal(await link.evaluate(n => n === document.activeElement && getComputedStyle(n).outlineStyle !== 'none'), true);
    }
    assert.equal(await t.page.locator('#request-dialog .polaris-edit-tools').getByRole('link', { name: 'Open patron in LEAP', exact: true }).count(), 0);
    assert.equal(await t.page.locator('#request-dialog .patron-context .grid-new-suggestion').count(), 1);
    assert.ok((await upper.allTextContents()).includes(before.barcode));
    for (const [mode, width, zoom] of [['desktop', 1280, 1], ['mobile', 390, 1], ['native-200', 1280, 2]]) {
      await t.page.setViewportSize({ width, height: 900 });
      if (zoom === 2) await nativeZoom(t.context, t.page, 2);
      await upper.first().scrollIntoViewIfNeeded();
      await checkpoint(t.page, t.directory, t.id + '-' + mode, zoom === 2 ? { nativeViewport: true } : {});
      if (zoom === 2) await nativeZoom(t.context, t.page, 1);
    }
    await save(t, t.id, { expected, labels: await upper.allTextContents(), newSuggestion: true, lowerDuplicate: false });
    assert.equal(t.mutations.length, 0);
  } },
  { ...d1.scenarios.find(s => s.id === 'd1-research-patron'), id: 'i479-research-drafts',
    requirements: ['#458: catalog/BIB/external providers and title/identifier/BIB draft updates, adjacent New suggestion'] },
  { id: 'i479-unavailable-patron-links', writes: false, requirements: ['#458: missing/invalid patron ID and missing/unsafe URL patterns stay plain text without guessed destinations'], run: async t => {
    const fixture = requireFixture((await t.verify()).evidence, 'draft');
    const before = await detail(t.context, t.origin, fixture);
    const cases = [
      { name: 'missing-id', patronId: null }, { name: 'invalid-id', patronId: -1 },
      { name: 'missing-pattern', leapPatronUrlPattern: null }, { name: 'numeric-pattern', leapPatronUrlPattern: 42 },
      { name: 'unsafe-pattern', leapPatronUrlPattern: 'javascript:alert({{patron-id}})' },
      { name: 'missing-token', leapPatronUrlPattern: 'https://example.invalid/patron/' }
    ];
    const seen = [];
    for (const testCase of cases) {
      const { name, ...override } = testCase;
      let intercepted = 0;
      const pattern = url => url.origin === t.origin &&
        (url.pathname === '/api/asap/staff/title-requests/' + fixture.id ||
         url.pathname === '/api/asap/staff/research-configuration');
      const handler = async route => {
        assert.equal(route.request().method(), 'GET');
        const response = await route.fetch(), body = await response.json();
        const research = { patronId: before.research.patronId, leapPatronUrlPattern: patronPattern, ...override };
        if (new URL(route.request().url()).pathname.includes('research-configuration')) Object.assign(body, research);
        else body.research = { ...body.research, ...research };
        intercepted++;
        await route.fulfill({ response, json: body });
      };
      await t.page.route(pattern, handler);
      try {
        await openFixture(t.page, t.origin, fixture);
        await t.page.locator('#request-dialog .polaris-edit-tools').getByRole('button', { name: 'Search Polaris catalog', exact: true }).waitFor();
        await t.page.waitForLoadState('networkidle');
        assert.ok(intercepted >= 2, 'Both initial details and later configuration use the unavailable boundary');
        assert.equal(await t.page.locator(patronLinks).count(), 0, name);
        assert.equal(await t.page.locator('#request-dialog .polaris-edit-tools').getByRole('link', { name: 'Open patron in LEAP', exact: true }).count(), 0);
        assert.equal(await t.page.locator('#request-dialog .patron-context .grid-new-suggestion').count(), 1);
        seen.push({ name, initialAndAsync: true, plainText: true, newSuggestion: true });
      } finally { await t.page.unroute(pattern, handler); }
    }
    await checkpoint(t.page, t.directory, t.id);
    await save(t, t.id, seen);
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'i479-mutation-retention', writes: true, requirements: ['#457: explicit Claims survives details, successful mutation refresh, close and navigation away/back'], run: async t => {
    const actor = await start(t);
    await t.page.locator('#claim-filter').selectOption('unclaimed');
    await d1.scenarios.find(s => s.id === 'd1-close-reopen-refresh').run(t);
    await selected(t.page, 'unclaimed');
    await t.page.keyboard.press('Escape');
    await t.page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await rows(t, false, '2', 'suggestion', 'unclaimed', actor);
    await view(t.page, true, 'open'); await selected(t.page, 'unclaimed');
    await view(t.page, false, 'suggestion');
    await rows(t, false, '2', 'suggestion', 'unclaimed', actor);
  } }
];
module.exports = { id: 'i479', requires: d1.requires, mutations: true, scenarios,
  human: [['i479-human-acceptance', '#398: actual screen reader, visual usability and final accessibility sign-off']],
  uncovered: [['i479-native-identity-and-invalid-links', '#457 identity/Profile/stale responses; #458 delayed valid-link upgrade',
    'Covered by retained shared-claims and leap-consumers native SQL/browser owners plus focused frontend tests. This suite keeps the single verified reviewer.'],
    ['i479-live-leap', '#458 actual external LEAP destination availability', 'No live provider navigation; exact href, target and rel are checked locally.']] };
