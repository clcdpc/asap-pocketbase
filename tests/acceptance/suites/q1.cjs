'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { requireFixture } = require('../helpers/environment.cjs');
const { json } = require('../helpers/assertions.cjs');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');

const grid = '#additional-copy-grid';
const headers = ['Title / Author', 'BIB / Format', 'Library', 'Staff', 'Timing', 'Action'];
const list = (t, scope = '2', status = 'open') => json(t.context, t.origin,
  `/api/asap/staff/additional-copies?scope=${scope}&status=${status}`);
const taskDetail = (t, id) => json(t.context, t.origin, '/api/asap/staff/additional-copies/' + id);
const opener = (page, id) => page.locator(`${grid} .additional-copy-open[data-copy-id="${id}"]`);
const rowFor = (page, id) => opener(page, id).locator('xpath=ancestor::tr');

async function rendered(page, ids) {
  await page.waitForFunction(({ grid, ids }) => {
    const actual = [...document.querySelectorAll(grid + ' .additional-copy-open')].map(n => n.dataset.copyId);
    return JSON.stringify(actual) === JSON.stringify(ids);
  }, { grid, ids });
}
async function search(page, value, expected) {
  await page.locator('#additional-copy-search').fill(value);
  await rendered(page, expected);
}
async function start(t, scope = '2') {
  requireFixture((await t.verify()).evidence, 'copy');
  await t.page.goto(t.origin + '/staff/?stage=additional_copies&scope=' + scope);
  await t.page.locator('#additional-copy-view').waitFor({ state: 'visible' });
  await t.page.locator(grid + ' .additional-copy-open').first().waitFor();
  await t.page.waitForFunction(() => !document.querySelector('#refresh-additional-copies').disabled);
  await t.page.locator('#additional-copy-claim-filter').selectOption('all');
  const result = await list(t, scope);
  assert.equal(result.scope, scope);
  assert.ok(result.items.every(item => scope === 'all' || String(item.libraryOrgId) === scope));
  return result.items;
}
async function close(page, id, fallback = false) {
  await page.keyboard.press('Escape');
  await page.locator('#request-dialog').waitFor({ state: 'hidden' });
  await page.waitForFunction(({ id, fallback }) => fallback
    ? document.activeElement?.id === 'refresh-additional-copies'
    : document.activeElement?.dataset.copyId === id, { id, fallback });
}
async function opened(page, item) {
  const dialog = page.locator('#request-dialog[open]');
  await dialog.getByRole('heading', { name: item.title, exact: true }).waitFor();
  assert.equal(await dialog.locator('#request-dialog-kicker').textContent(), item.libraryOrgName + ' · Additional copy ' + item.id);
  await dialog.locator('.action-bar').waitFor();
  assert.equal(await dialog.locator('#close-request').evaluate(n => n === document.activeElement), true);
}
async function geometry(page, cards) {
  const result = await page.locator(grid).evaluate(root => {
    const rect = node => {
      const r = node.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom };
    };
    const wrapper = root.querySelector('.gridjs-wrapper');
    return { viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scrollY,
      visualWidth: visualViewport.width, visualHeight: visualViewport.height, scale: visualViewport.scale },
      container: rect(root), wrapper: { clientWidth: wrapper.clientWidth, scrollWidth: wrapper.scrollWidth },
      table: rect(root.querySelector('table')), images: root.querySelectorAll('img, picture, svg').length,
      rows: [...root.querySelectorAll('tbody tr')].filter(row => row.querySelector('.additional-copy-open')).map(row => {
        const action = row.querySelector('.additional-copy-open'), range = document.createRange(); range.selectNodeContents(action);
        const text = [...range.getClientRects()].map(r => ({ left: r.left, right: r.right, top: r.top, bottom: r.bottom }));
        const r = action.getBoundingClientRect();
        return { id: action.dataset.copyId, bounds: rect(row), display: getComputedStyle(row).display,
          cells: [...row.cells].map(rect), action: rect(action), text,
          labelFits: text.length === 1 && text.every(t => t.left >= r.left && t.right <= r.right && t.top >= r.top && t.bottom <= r.bottom),
          actionWidth: action.clientWidth, actionScroll: action.scrollWidth,
          nowrap: getComputedStyle(action).whiteSpace };
      }) };
  });
  assert.equal(result.images, 0, 'No cover or artwork dependencies');
  assert.ok(result.wrapper.scrollWidth <= result.wrapper.clientWidth + 1, 'Grid needs no horizontal scrolling');
  assert.ok(result.table.width <= result.container.width + 1, 'Table fits its actual container');
  assert.ok(result.rows.length > 0, 'Geometry measures actual tasks');
  for (const row of result.rows) {
    assert.equal(row.cells.length, 6);
    assert.equal(row.display, cards ? 'grid' : 'table-row', 'Presentation follows the content-container width');
    assert.equal(row.labelFits, true, 'Open details label is complete and on one line');
    assert.equal(row.nowrap, 'nowrap'); assert.ok(row.action.height >= 44);
    assert.ok(row.actionScroll <= row.actionWidth + 1);
    assert.ok(row.cells.every(c => c.width > 0 && c.x >= result.container.x - 1 && c.right <= result.container.right + 1));
    assert.ok(row.cells.every((a, i) => row.cells.slice(i + 1).every(b =>
      Math.min(a.right, b.right) - Math.max(a.x, b.x) <= 1 || Math.min(a.bottom, b.bottom) - Math.max(a.y, b.y) <= 1)),
    'Cells do not overlap');
    if (cards) assert.ok(row.action.width >= row.bounds.width - 32, 'Card action fills the row');
    else {
      assert.ok(row.bounds.height < 180, 'Long text does not create extreme desktop row heights');
      assert.ok(row.cells[0].width > row.cells[1].width * 2, 'Title/Author receives priority over BIB/Format');
    }
  }
  return result;
}
async function evidence(t, name, cards, capture = {}) {
  const action = t.page.locator(grid + ' .additional-copy-open').first();
  await action.locator('xpath=ancestor::tr').scrollIntoViewIfNeeded();
  await action.scrollIntoViewIfNeeded();
  const result = await geometry(t.page, cards);
  await fs.writeFile(path.join(t.directory, name + '-geometry.json'), JSON.stringify(result, null, 2));
  await checkpoint(t.page, t.directory, name, { visible: [action], ...capture });
}
const scenarios = [
  { id: 'q1-desktop', writes: false, requirements: ['#404: six grouped columns at 1440, 1280 and 1024; long/missing values; readable actions; no covers or overflow'], run: async t => {
    const items = await start(t), long = items.find(item => item.legacyId === 'acceptance-q1-1');
    const missing = items.find(item => item.legacyId === 'acceptance-q1-2');
    assert.ok(long && missing, 'Deterministic long and missing-field fixtures exist');
    await search(t.page, 'Q1 copy 01', [long.id]);
    const row = rowFor(t.page, long.id);
    assert.deepEqual(await t.page.locator(grid + ' th .gridjs-th-content').allTextContents(), headers);
    assert.equal(await t.page.locator(grid + ' th button').count(), 5, 'Only the five primary columns sort');
    assert.equal(await row.locator('.additional-copy-title').textContent(), long.title);
    assert.equal(await row.locator('.additional-copy-title').getAttribute('title'), long.title);
    assert.ok((await row.locator('[data-copy-column="title"]').textContent()).includes(long.author));
    assert.equal(await row.locator('.additional-copy-notes').innerText(), 'Notes');
    assert.ok((await row.locator('[data-copy-column="bib"]').textContent()).includes(String(long.bibid)));
    assert.ok((await row.locator('[data-copy-column="bib"]').textContent()).includes(long.formatLabel));
    assert.ok((await row.locator('[data-copy-column="staff"]').textContent()).includes(long.claimedByDisplayName));
    assert.ok((await row.locator('[data-copy-column="staff"]').textContent()).includes('Created by: ' + long.createdByUsername));
    assert.equal(await row.locator('time').getAttribute('datetime'), long.created);
    assert.match(await row.locator('[data-copy-column="timing"]').textContent(), /Task timeout:/);
    for (const width of [1440, 1280, 1024]) {
      await t.page.setViewportSize({ width, height: 900 });
      await evidence(t, 'q1-desktop-' + width, false);
    }
    await search(t.page, 'Q1 copy 02', [missing.id]);
    assert.match(await rowFor(t.page, missing.id).innerText(), /Author not recorded/);
    assert.match(await rowFor(t.page, missing.id).innerText(), /Created by: Not recorded/);
    assert.equal(await rowFor(t.page, missing.id).locator('.additional-copy-notes').count(), 0);
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'q1-row-and-keyboard', writes: false, requirements: ['#405: row/button identity; exactly one opening; BIB/interactive/modified/selection exclusion; Enter/Space, Escape and focus return'], run: async t => {
    const items = await start(t), fixture = requireFixture((await t.verify()).evidence, 'copy');
    const item = items.find(item => item.id === fixture.id), before = await taskDetail(t, fixture.id);
    await search(t.page, item.title, [item.id]);
    const detailReads = [];
    t.page.on('request', request => {
      if (new URL(request.url()).pathname === '/api/asap/staff/additional-copies/' + item.id) detailReads.push(request.method());
    });
    const row = rowFor(t.page, item.id);
    for (const kind of ['title', 'library']) {
      await row.locator(`[data-copy-column="${kind}"]`).click(); await opened(t.page, item); await close(t.page, item.id);
    }
    assert.equal(detailReads.length, 2);
    await opener(t.page, item.id).click(); await opened(t.page, item); await close(t.page, item.id);
    assert.equal(detailReads.length, 3, 'Button click opens once');
    for (const key of ['Enter', 'Space']) {
      await opener(t.page, item.id).focus(); await t.page.keyboard.press('Tab'); await t.page.keyboard.press('Shift+Tab');
      assert.equal(await opener(t.page, item.id).evaluate(n => n === document.activeElement &&
        getComputedStyle(n).outlineStyle !== 'none' && parseFloat(getComputedStyle(n).outlineWidth) > 0), true);
      await t.page.keyboard.press(key); await opened(t.page, item); await close(t.page, item.id);
    }
    assert.equal(detailReads.length, 5);
    const bib = row.locator('[data-copy-column="bib"] a');
    assert.equal(await bib.getAttribute('href'), item.research.leapBibUrlPattern.replace('{{bibid}}', String(item.bibid)));
    assert.equal(await bib.getAttribute('target'), '_blank');
    assert.equal(await bib.getAttribute('rel'), 'noopener noreferrer');
    // Prevent only navigation at the ancestor after the application's handlers
    // run; never contact the external catalog or bypass the row delegation.
    await row.evaluate(node => node.addEventListener('click', event => { if (event.target.closest('a')) event.preventDefault(); }));
    await bib.click();
    for (const modifiers of [['Control'], ['Meta'], ['Shift'], ['Alt']]) {
      await row.locator('[data-copy-column="library"]').click({ modifiers });
    }
    await row.evaluate(node => {
      const text = node.querySelector('.additional-copy-title'), range = document.createRange(); range.selectNodeContents(text);
      getSelection().addRange(range); text.dispatchEvent(new MouseEvent('click', { bubbles: true })); getSelection().removeAllRanges();
      for (const tag of ['button', 'input', 'select', 'textarea', 'summary']) {
        const child = document.createElement(tag); node.querySelector('td').append(child); child.click(); child.remove();
      }
    });
    assert.equal(await t.page.locator('#request-dialog[open]').count(), 0);
    assert.equal(detailReads.length, 5, 'Excluded targets do not fetch details');
    assert.deepEqual(await taskDetail(t, item.id), before, 'Read-only activation preserves task data/history');
    await evidence(t, t.id, false);
  } },
  { id: 'q1-sort-filter-refresh-scope', writes: false, requirements: ['#404; #405: primary sorts, paging, underlying searches, claims, refresh, status/scope identity and stale rows'], run: async t => {
    const items = await start(t);
    assert.ok(items.length > 25, 'Minimal deterministic fixtures cross a page boundary');
    const fields = ['title', 'bibid', 'libraryOrgName', 'claimedByDisplayName', 'created'];
    for (const [index, field] of fields.entries()) {
      const ordered = [...items].sort((a, b) => field === 'bibid' ? a[field] - b[field]
        : String(a[field] || 'Unclaimed').localeCompare(String(b[field] || 'Unclaimed')));
      await t.page.locator(grid + ' th').nth(index).locator('button').click();
      await rendered(t.page, ordered.slice(0, 25).map(item => item.id));
      await rowFor(t.page, ordered[0].id).locator('[data-copy-column="library"]').click();
      await opened(t.page, ordered[0]); await close(t.page, ordered[0].id);
    }
    await t.page.locator(grid + ' button[aria-label="Next"]').click();
    const ordered = [...items].sort((a, b) => a.created.localeCompare(b.created));
    await rendered(t.page, ordered.slice(25).map(item => item.id));
    const last = ordered[25]; await opener(t.page, last.id).click(); await opened(t.page, last); await close(t.page, last.id);
    const item = items.find(item => item.legacyId === 'acceptance-q1-1');
    for (const query of [item.title, item.author, String(item.bibid), item.identifier, item.publication]) {
      await search(t.page, query, [item.id]);
    }
    await t.page.locator('#additional-copy-claim-filter').selectOption('unclaimed'); await rendered(t.page, []);
    await t.page.locator('#additional-copy-claim-filter').selectOption('all'); await rendered(t.page, [item.id]);
    const retired = await opener(t.page, item.id).elementHandle();
    const response = t.page.waitForResponse(r => new URL(r.url()).pathname === '/api/asap/staff/additional-copies');
    await t.page.locator('#refresh-additional-copies').click(); await response;
    await t.page.waitForFunction(node => !node.isConnected, retired);
    await rendered(t.page, [item.id]);
    await retired.evaluate(node => node.click());
    assert.equal(await t.page.locator('#request-dialog[open]').count(), 0, 'Detached refresh opener is retired');
    await rowFor(t.page, item.id).locator('[data-copy-column="title"]').click(); await opened(t.page, item);
    // Exercise the existing fallback if a row opener disappears while details are open.
    await rowFor(t.page, item.id).evaluate(node => node.remove()); await close(t.page, item.id, true);
    await t.page.locator('#additional-copy-status-tabs [data-copy-status="closed"]').click();
    const closed = (await list(t, '2', 'closed')).items;
    await rendered(t.page, closed.map(item => item.id));
    assert.ok(closed.length > 0); assert.match(await rowFor(t.page, closed[0].id).innerText(), /Task timeout: —/);
    await rowFor(t.page, closed[0].id).locator('[data-copy-column="title"]').click();
    await opened(t.page, closed[0]); await close(t.page, closed[0].id);
    await t.page.locator('#additional-copy-status-tabs [data-copy-status="open"]').click();
    await rendered(t.page, items.slice(0, 25).map(item => item.id));
    const oldScope = await t.page.locator(grid + ' .additional-copy-open').first().elementHandle();
    await t.page.locator('#additional-copy-library-scope').selectOption('3');
    const foreign = (await list(t, '3')).items;
    await rendered(t.page, foreign.map(item => item.id));
    await oldScope.evaluate(node => node.click());
    assert.equal(await t.page.locator('#request-dialog[open]').count(), 0, 'Retired library projection cannot open');
    assert.ok(foreign.length === 1 && foreign[0].libraryOrgId === 3);
    await rowFor(t.page, foreign[0].id).locator('[data-copy-column="library"]').click();
    await opened(t.page, foreign[0]); await close(t.page, foreign[0].id);
    await evidence(t, t.id, false);
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'q1-responsive-cards', writes: false, requirements: ['#404: 390px and narrow content container; grouped card content, full-width actions, keyboard and axe'], run: async t => {
    const items = await start(t), item = items.find(item => item.legacyId === 'acceptance-q1-1');
    await search(t.page, 'Q1 copy 01', [item.id]);
    await t.page.setViewportSize({ width: 1280, height: 900 });
    await t.page.locator(grid).evaluate(node => { node.style.maxWidth = '640px'; });
    await evidence(t, 'q1-narrow-container', true);
    await t.page.locator(grid).evaluate(node => { node.style.maxWidth = ''; });
    await t.page.setViewportSize({ width: 390, height: 844 });
    await evidence(t, 'q1-mobile-390', true);
    assert.equal(await rowFor(t.page, item.id).getByRole('gridcell').count(), 6, 'Card preserves native grid semantics');
    await opener(t.page, item.id).press('Enter'); await opened(t.page, item); await close(t.page, item.id);
    await checkpoint(t.page, t.directory, 'q1-mobile-focus', { visible: [opener(t.page, item.id)] });
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'q1-native-200', writes: false, requirements: ['#404; #405: actual native 200% zoom, responsive cards, action geometry, Enter/Escape and focus'], run: async t => {
    const items = await start(t), item = items.find(item => item.legacyId === 'acceptance-q1-1');
    await search(t.page, 'Q1 copy 01', [item.id]);
    await nativeZoom(t.context, t.page, 2);
    const cdp = await t.context.newCDPSession(t.page);
    try {
      await evidence(t, t.id, true, { nativeViewport: true });
      await fs.writeFile(path.join(t.directory, t.id + '-browser.json'), JSON.stringify(await cdp.send('Page.getLayoutMetrics'), null, 2));
      await opener(t.page, item.id).press('Enter'); await opened(t.page, item); await close(t.page, item.id);
      assert.equal(t.mutations.length, 0);
    } finally { await cdp.detach(); } // The runner closes this page; zoom is scoped to its tab.
  } }
];
module.exports = { id: 'q1', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: false, scenarios,
human: [
  ['q1-human-screen-reader', '#398; #404; #405: real NVDA/VoiceOver table/card naming, live announcements and focus comprehension'],
  ['q1-human-visual', '#398; #404: human desktop/mobile/native-zoom and forced-colors review of the saved screenshots']
], uncovered: [] };
