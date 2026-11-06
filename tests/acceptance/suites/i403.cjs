'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { requireFixture } = require('../helpers/environment.cjs');
const { checkpoint, nativeZoom } = require('../helpers/accessibility.cjs');
const { json, detail, unchanged, submit } = require('../helpers/assertions.cjs');
const { settleActions, activateRowAction, actionGeometry, actionColumn } = require('../helpers/request-actions.cjs');
const rowFor = (page, id) => page.locator(`#request-grid .request-title-link[data-request-id="${id}"]`).locator('xpath=ancestor::tr');
async function start(t, key = 'draft') {
  const f = requireFixture((await t.verify()).evidence, key), before = await detail(t.context, t.origin, f);
  await queue(t, before.status, before.title);
  return { f, before, row: rowFor(t.page, f.id) };
}
async function queue(t, status, title = '') {
  await t.page.goto(t.origin + '/staff/?scope=2&stage=' + status);
  await t.page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled && document.querySelector('#request-grid tbody'));
  await t.page.locator('#claim-filter').selectOption('all');
  await t.page.locator('#request-search').fill(title);
  if (title) await t.page.waitForFunction(title => [...document.querySelectorAll('#request-grid .request-title-link')].some(n => n.textContent === title), title);
  await settleActions(t.page);
}
async function evidence(t, row, name = t.id, capture = {}) {
  const direct = row.locator('.request-row-actions > [data-request-action]').first();
  await (await direct.count() ? direct : row.locator('.request-title-link')).scrollIntoViewIfNeeded(); await settleActions(t.page);
  const geometry = await actionGeometry(row);
  await fs.writeFile(path.join(t.directory, name + '-geometry.json'), JSON.stringify(geometry, null, 2));
  await checkpoint(t.page, t.directory, name, { visible: [await direct.count()
    ? row.locator('.request-row-actions > button') : row.locator('.request-title-link')], ...capture });
  return geometry;
}
async function close(t) {
  await t.page.keyboard.press('Escape'); await t.page.locator('#request-dialog').waitFor({ state: 'hidden' });
  await t.page.waitForURL(url => !url.searchParams.has('request'));
}
const scenarios = [
  { id: 'i403-closed-and-empty', writes: false, requirements: ['#403: eligible Closed has sole direct Reopen at desktop/mobile/native 200%; protected Closed has zero commands and retains row/title details'], run: async t => {
    const { f, before, row } = await start(t, 'd1-closed');
    for (const [name, width, zoom] of [['desktop', 1440, 1], ['mobile', 390, 1], ['native-200', 1280, 2]]) {
      await t.page.setViewportSize({ width, height: 900 });
      if (zoom === 2) await nativeZoom(t.context, t.page, 2);
      const geometry = await evidence(t, row, t.id + '-' + name, zoom === 2 ? { nativeViewport: true } : {});
      assert.deepEqual(geometry.inline, ['reopen']); assert.equal(geometry.more, 0);
      await activateRowAction(row, 'reopen', name === 'mobile' ? 'Space' : 'Enter');
      const confirmation = t.page.locator('#confirmation-dialog[open]'); await confirmation.waitFor();
      await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
      assert.equal(await t.page.locator('#request-dialog .action-bar').getByRole('button', { name: 'Reopen', exact: true })
        .evaluate(node => node === document.activeElement), true);
      await t.page.locator('#request-dialog[open] .action-bar').waitFor();
      assert.equal(await t.page.locator('#request-dialog .request-more, #request-dialog [popover]').count(), 0);
      await close(t);
      if (zoom === 2) await nativeZoom(t.context, t.page, 1);
    }
    const protectedFixture = requireFixture((await t.verify()).evidence, 'd1-protected');
    const protectedBefore = await detail(t.context, t.origin, protectedFixture);
    assert.equal(protectedBefore.capabilities.allowedActions.includes('reopen'), false);
    await queue(t, 'closed', protectedBefore.title);
    const protectedRow = rowFor(t.page, protectedFixture.id);
    assert.deepEqual((await evidence(t, protectedRow, t.id + '-protected')).keys, []);
    assert.equal(await protectedRow.locator('button.request-row-more, [popover], [data-request-action]').count(), 0);
    for (const path of ['title', 'row']) {
      if (path === 'title') await protectedRow.locator('.request-title-link').press('Enter');
      else await protectedRow.locator('td').nth(2).click();
      await t.page.locator('#request-dialog[open]').waitFor();
      assert.equal(await t.page.locator('#request-dialog .action-bar').getByRole('button', { name: 'Reopen', exact: true }).count(), 0);
      await close(t);
    }
    await unchanged(t.context, t.origin, protectedFixture, protectedBefore);
    await t.page.locator('#request-search').fill('I403 no matching synthetic request');
    await t.page.waitForFunction(() => document.querySelectorAll('#request-grid .request-title-link').length === 0);
    assert.equal(await t.page.locator('#request-grid .request-row-actions button, #request-grid [popover]').count(), 0);
    await unchanged(t.context, t.origin, f, before); assert.equal(t.mutations.length, 0);
  } },
  { id: 'i403-two-and-many-resize', writes: false, requirements: ['#403: two/many actions; measured container/column changes; promotion/demotion; unique ordered commands; font changes'], run: async t => {
    const result = await json(t.context, t.origin, '/api/asap/staff/title-requests?scope=2');
    const matches = result.items.filter(item => item.barcode === 'ACCEPTANCE-I403-pending-ready');
    assert.equal(matches.length, 1, 'Exactly one native synthetic eligible claimed Pending hold exists');
    const [two] = matches;
    assert.equal(two.status, 'pending_hold');
    await queue(t, two.status, two.title);
    let row = rowFor(t.page, two.id);
    assert.equal(two.capabilities.canPlaceHold, true);
    await actionColumn(t.page, 25);
    assert.deepEqual((await evidence(t, row, 'i403-two-fit')).inline, ['place-hold', 'assign']);
    // Grid.js retains pixel column widths when only the outer grid narrows.
    // Constrain the actual Actions container to exercise a real width change.
    await row.locator('.request-row-actions').evaluate(node => { node.style.maxWidth = '120px'; }); await settleActions(t.page);
    const narrowTwo = await evidence(t, row, 'i403-two-container');
    assert.deepEqual(narrowTwo.inline, ['place-hold']); assert.deepEqual(narrowTwo.overflow, ['assign']);
    await row.locator('.request-row-actions').evaluate(node => { node.style.maxWidth = ''; }); await settleActions(t.page);
    assert.equal((await actionGeometry(row)).more, 0);
    await actionColumn(t.page);
    const source = await start(t); row = source.row;
    const initial = await evidence(t, row, 'i403-many-default');
    assert.deepEqual(initial.keys, ['purchase', 'reject', 'alreadyOwn', 'claim', 'assign', 'silentClose']);
    await actionColumn(t.page, 70);
    assert.deepEqual((await actionGeometry(row)).inline, initial.keys);
    await actionColumn(t.page, 12);
    assert.ok((await actionGeometry(row)).overflow.length > 0);
    await actionColumn(t.page);
    await row.locator('.request-row-actions').evaluate(node => { node.style.fontFamily = 'monospace'; }); await settleActions(t.page);
    await evidence(t, row, 'i403-font-change');
    await unchanged(t.context, t.origin, source.f, source.before); assert.equal(t.mutations.length, 0);
  } },
  { id: 'i403-primary-workflows', writes: false, requirements: ['#403: Purchases AutoHold on/off, eligible/blocked Pending holds, Placed holds; primary priority and existing confirmation/Cancel; secondary Assign; no direct mutation'], run: async t => {
    const items = (await json(t.context, t.origin, '/api/asap/staff/title-requests?scope=2')).items;
    for (const [suffix, key, label] of [
      ['catalog-hold', 'catalogFound', 'Ready for hold'], ['catalog-close', 'catalogFound', 'Close without hold'],
      ['pending-ready', 'place-hold', 'Place hold'], ['placed', 'close', 'Close request']
    ]) {
      const matches = items.filter(item => item.barcode === 'ACCEPTANCE-I403-' + suffix); assert.equal(matches.length, 1);
      const before = await json(t.context, t.origin, '/api/asap/staff/title-requests/' + matches[0].id);
      await queue(t, before.status, before.title); const row = rowFor(t.page, before.id);
      for (const percent of [70, 8]) {
        await actionColumn(t.page, percent);
        const geometry = await evidence(t, row, t.id + '-' + suffix + '-' + percent);
        assert.equal(geometry.inline[0], key);
        if (percent === 70) assert.equal(geometry.more, 0);
        else assert.deepEqual(geometry.overflow, ['assign']);
        await activateRowAction(row, key, percent === 70 ? 'Enter' : 'Space');
        const confirmation = t.page.locator('#confirmation-dialog[open]'); await confirmation.waitFor();
        assert.equal(await confirmation.locator('h2').textContent(), label);
        assert.equal(t.mutations.length, 0, 'Opening the workflow must not mutate');
        await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
        assert.equal(await t.page.locator('#request-dialog .action-bar').getByRole('button', { name: label, exact: true })
          .evaluate(node => node === document.activeElement), true);
        await close(t);
        await activateRowAction(row, 'assign');
        const assignment = t.page.locator('#request-dialog .inline-form'); await assignment.waitFor();
        assert.equal(await t.page.locator('#request-dialog .action-bar').isVisible(), false);
        await assignment.getByRole('button', { name: 'Cancel', exact: true }).click(); await close(t);
      }
      assert.deepEqual(await json(t.context, t.origin, '/api/asap/staff/title-requests/' + before.id), before);
      await actionColumn(t.page);
    }
    for (const suffix of ['1', 'pending-blocked']) {
      const before = items.find(item => item.barcode === 'ACCEPTANCE-I403-' + suffix); assert.ok(before);
      assert.equal(before.capabilities.canPlaceHold, false);
      await queue(t, 'pending_hold', before.title);
      assert.deepEqual((await evidence(t, rowFor(t.page, before.id), t.id + '-ineligible-' + suffix)).inline, ['assign']);
    }
    assert.equal(t.mutations.length, 0);
  } },
  { id: 'i403-keyboard-and-guarded-dispatch', writes: true, requirements: ['#403: direct/overflow share workflow review; exactly one dispatch; Enter/Space/Escape; menu/focus reconciliation'], run: async t => {
    const { f, before, row } = await start(t, 'purchase-reminder-off'); t.allowFixture(f);
    const more = row.locator('.request-row-more');
    await more.press('Space'); assert.equal(await more.getAttribute('aria-expanded'), 'true');
    await t.page.keyboard.press('Escape'); assert.equal(await more.getAttribute('aria-expanded'), 'false');
    assert.equal(await more.evaluate(node => node === document.activeElement), true);
    await more.press('Enter');
    const focused = await t.page.evaluate(() => document.activeElement.dataset.requestAction);
    await actionColumn(t.page, 70);
    assert.equal(await more.count(), 0); assert.equal(await t.page.evaluate(() => document.activeElement.dataset.requestAction), focused);
    const reads = []; t.page.on('request', request => { if (new URL(request.url()).pathname === '/api/asap/staff/title-requests/' + f.id) reads.push(request.method()); });
    await activateRowAction(row, 'purchase', 'Enter');
    const review = t.page.locator('#request-dialog .action-choice'); await review.locator('button[type="submit"]:not([disabled])').waitFor();
    assert.equal(await t.page.locator('#request-dialog .action-bar').isVisible(), false);
    assert.equal(await review.locator('button[type=submit]').textContent(), 'Confirm purchase');
    assert.equal(reads.length, 1, 'Direct workflow click opens details exactly once');
    await review.getByRole('button', { name: 'Cancel', exact: true }).click(); await close(t);
    await actionColumn(t.page, 8); // A real narrow column moves Reject into overflow.
    await activateRowAction(row, 'reject', 'Space'); await review.locator('button[type="submit"]:not([disabled])').waitFor();
    assert.equal(await t.page.locator('#request-dialog .action-bar').isVisible(), false);
    assert.equal(await review.locator('button[type=submit]').textContent(), 'Reject and close');
    assert.equal(reads.length, 2, 'Overflow workflow click opens details exactly once');
    await review.getByRole('button', { name: 'Cancel', exact: true }).click(); await close(t);
    await unchanged(t.context, t.origin, f, before); assert.equal(t.mutations.length, 0);
    await actionColumn(t.page); await activateRowAction(row, 'purchase'); await review.locator('button[type="submit"]:not([disabled])').waitFor();
    await review.getByRole('checkbox', { name: 'Send purchase reminder', exact: true }).uncheck();
    await submit(t.page, review.locator('button[type="submit"]'), f, 200, true);
    await t.page.locator('#request-dialog .status-badge').filter({ hasText: 'Outstanding purchase' }).waitFor();
    const current = await detail(t.context, t.origin, f);
    assert.equal(current.status, 'outstanding_purchase'); assert.notEqual(current.version, before.version);
    assert.equal(t.mutations.length, 1); assert.equal(t.mutations[0].action, 'purchase');
    await checkpoint(t.page, t.directory, t.id, { visible: [t.page.locator('#request-dialog .status-badge')] });
  } },
  { id: 'i403-grid-replacement', writes: false, requirements: ['#403: sorting/paging/filtering/refresh/scope replacement retire stale controls and menus'], run: async t => {
    await queue(t, 'suggestion');
    const items = (await json(t.context, t.origin, '/api/asap/staff/title-requests?scope=2')).items.filter(item => item.status === 'suggestion');
    assert.ok(items.length > 25, 'Synthetic Suggestions cross the real page boundary');
    await t.page.locator('#request-grid .gridjs-pages button[aria-label="Next"]:not([disabled])').waitFor();
    const sortedTitle = items.map(item => item.title).sort((a, b) => a.localeCompare(b))[0];
    await t.page.locator('#request-grid th').nth(1).locator('button').click();
    await t.page.waitForFunction(title => document.querySelector('#request-grid .request-title-link')?.textContent === title, sortedTitle);
    const first = t.page.locator('#request-grid .request-title-link').first();
    const old = await first.locator('xpath=ancestor::tr').locator('[data-request-action="purchase"]').elementHandle();
    await t.page.locator('#request-grid button[aria-label="Next"]').click();
    await t.page.waitForFunction(node => !node.isConnected, old);
    await old.evaluate(node => node.click()); assert.equal(await t.page.locator('#request-dialog[open]').count(), 0);
    await t.page.locator('#request-search').fill('I403 request 02');
    await t.page.waitForFunction(() => document.querySelectorAll('#request-grid .request-title-link').length === 1 &&
      document.querySelector('#request-grid .request-title-link')?.textContent === 'I403 request 02');
    const row = t.page.locator('#request-grid tbody tr'), retired = await row.locator('.request-row-more').elementHandle();
    await retired.click();
    await t.page.locator('#refresh-queue').click(); await t.page.waitForFunction(node => !node.isConnected, retired);
    assert.equal(await t.page.locator('.request-table-menu:popover-open').count(), 0);
    await retired.evaluate(node => node.click()); assert.equal(await t.page.locator('.request-table-menu:popover-open').count(), 0);
    await t.page.locator('#library-scope').selectOption('3');
    await t.page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'I403 request 16');
    await old.evaluate(node => { document.querySelector('#request-grid tbody tr td').append(node); node.click(); node.remove(); });
    assert.equal(await t.page.locator('#request-dialog[open]').count(), 0, 'Reinserted retired commands cannot activate a new scope');
    await evidence(t, t.page.locator('#request-grid tbody tr')); assert.equal(t.mutations.length, 0);
    await old.dispose(); await retired.dispose();
  } },
  { id: 'i403-mobile-native-and-forced-colors', writes: false, requirements: ['#403: desktop/mobile/native 200%; geometry, forced colors, direct labels, no clipping or unexpected page overflow'], run: async t => {
    const { f, before, row } = await start(t);
    for (const [mode, width, zoom] of [['desktop', 1440, 1], ['mobile', 390, 1], ['native-200', 1280, 2]]) {
      await t.page.setViewportSize({ width, height: 900 });
      if (zoom === 2) await nativeZoom(t.context, t.page, 2);
      await evidence(t, row, 'i403-' + mode, zoom === 2 ? { nativeViewport: true } : {});
      if (zoom === 2) await nativeZoom(t.context, t.page, 1);
    }
    await t.page.emulateMedia({ forcedColors: 'active' }); await evidence(t, row, 'i403-forced-colors');
    await unchanged(t.context, t.origin, f, before); assert.equal(t.mutations.length, 0);
  } }
];
module.exports = { id: 'i403', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers', 'chromium-native-zoom'], mutations: true, scenarios,
  human: [['i403-human-accessibility', '#398: final visual and screen-reader acceptance']],
  uncovered: [['i403-role-and-session-variants', '#403: role/capability revocation and sign-out',
    'Retained controller/table and SQL/browser tests cover these guards. This suite keeps the authorized synthetic super-admin and does not replace its session.']] };
