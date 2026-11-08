'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');
const { settleActions, activateRowAction, actionGeometry, actionColumn } = require('../acceptance/helpers/request-actions.cjs');

const [baseURL, artifacts] = process.argv.slice(2);
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const expectedCounts = { suggestion: 1, outstanding_purchase: 1, pending_hold: 0, hold_placed: 1, closed: 2 };
const expectedTitles = {
  suggestion: ['Title layout suggestion 0'],
  outstanding_purchase: ['Title layout outstanding_purchase 0'],
  pending_hold: [],
  hold_placed: ['Title layout hold_placed 0'],
  closed: ['Title layout closed 0', 'Long bibliographic title segment']
};
const viewports = [
  { name: 'wide-3000', width: 3000, height: 1000 },
  { name: 'desktop-1440', width: 1440, height: 900 },
  { name: 'intermediate-900', width: 900, height: 900 },
  { name: 'mobile-390', width: 390, height: 844 },
  { name: 'short-narrow-320x480', width: 320, height: 480 }
];

const report = { viewportMatrix: [], resizeOnly: [], nativeZoom: [], external: [], errors: [], missingImages: [], scans: [] };
const headers = actor => ({
  'X-ASAP-Test-Staff-Id': actor.id,
  'X-ASAP-Test-Tenant-Id': actor.tenantId,
  'X-ASAP-Test-Staff-Email': actor.email
});

async function guard(context, baseOrigin) {
  await context.route('**/*', route => {
    if (new URL(route.request().url()).origin === baseOrigin) return route.continue();
    report.external.push(route.request().url());
    return route.abort();
  });
  context.on('page', page => page.on('pageerror', error => report.errors.push(error.message)));
}

async function openStaffPage(page, fixture) {
  await page.goto(`${baseURL}/staff/?stage=closed&scope=${fixture.library}`);
  await page.locator('#request-grid .gridjs-wrapper').waitFor();
  await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody .request-title-link').length === 2);
  await page.addScriptTag({ path: require.resolve('axe-core/axe.min.js') });
  assert.equal(await page.getByRole('combobox', { name: 'Claims filter', exact: true }).count(), 1);
  assert.equal(await page.getByRole('combobox', { name: 'Tag filter', exact: true }).count(), 1);
  const search = page.locator('#request-search');
  await search.focus(); await page.keyboard.press('Tab');
  const claimFocus = await page.locator('#claim-filter').evaluate(node => ({
    active: node === document.activeElement,
    visible: node.matches(':focus-visible')
  }));
  assert.deepEqual(claimFocus, { active: true, visible: true }, 'Claims remains reachable with visible keyboard focus.');
  await page.keyboard.press('Home');
  assert.equal(await page.locator('#claim-filter').inputValue(), 'all', 'Claims accepts a native keyboard selection.');
  await page.keyboard.press('Tab');
  const tagFocus = await page.locator('#tag-filter').evaluate(node => ({
    active: node === document.activeElement,
    visible: node.matches(':focus-visible')
  }));
  assert.deepEqual(tagFocus, { active: true, visible: true }, 'Tag remains reachable with visible keyboard focus.');
  await page.keyboard.press('Home');
  assert.equal(await page.locator('#tag-filter').inputValue(), 'all', 'Tag accepts a native keyboard selection.');
  // Keyboard filter changes replace Grid.js rows asynchronously. Acquire the
  // completed Closed rows/actions before any resize-only or native-zoom baseline.
  await selectStage(page, 'closed');
}

async function selectStage(page, stage) {
  assert.equal(await page.getByRole('combobox', { name: 'Claims filter', exact: true }).count(), 1);
  assert.equal(await page.getByRole('combobox', { name: 'Tag filter', exact: true }).count(), 1);
  await page.locator(`#status-tabs [data-status="${stage}"]`).click();
  await page.waitForFunction(stage => document.querySelector(`#status-tabs [data-status="${stage}"]`)?.getAttribute('aria-selected') === 'true', stage);
  const expected = expectedCounts[stage];
  await page.waitForFunction(expected => {
    const summary = document.querySelector('#queue-summary')?.textContent || '';
    return summary.startsWith(`${expected} `);
  }, expected);
  if (expected === 0) {
    await page.locator('#request-grid .gridjs-notfound').waitFor();
  } else {
    await page.waitForFunction(expected => {
      const titles = [...document.querySelectorAll('#request-grid tbody .request-title-link')].map(node => node.textContent);
      return titles.length === expected.length && expected.every(title => titles.some(value => value.includes(title)));
    }, expectedTitles[stage]);
  }
  if (stage === 'suggestion') {
    assert.equal(await page.getByRole('combobox', { name: 'Similarity filter', exact: true }).count(), 1);
    assert.equal(await page.locator('#similar-filter-field').isVisible(), true);
  } else {
    assert.equal(await page.locator('#similar-filter-field').isVisible(), false,
      `${stage}: Similarity remains Suggestions-only.`);
  }
  if (stage === 'closed') {
    await page.waitForFunction(() => document.querySelectorAll('#request-grid button[data-request-action="reopen"]').length === 2);
  }
}

async function measure(page, name, width, stage) {
  const root = page.locator('#request-grid');
  const wrapper = root.locator('.gridjs-wrapper');
  await page.waitForFunction(() => {
    const region = document.querySelector('#request-grid .gridjs-wrapper');
    return region?.getAttribute('role') === 'region' && region.tabIndex === 0;
  });
  await wrapper.focus();
  await page.waitForFunction(() => document.activeElement === document.querySelector('#request-grid .gridjs-wrapper'));
  const metric = await root.evaluate((grid, info) => {
    const elementMetrics = node => {
      if (!node) return null;
      const style = getComputedStyle(node), rect = node.getBoundingClientRect();
      return {
        tag: node.tagName,
        id: node.id || null,
        className: typeof node.className === 'string' ? node.className : null,
        rectWidth: rect.width,
        rectLeft: rect.left,
        clientWidth: node.clientWidth,
        scrollWidth: node.scrollWidth,
        overflowX: style.overflowX,
        width: style.width,
        minWidth: style.minWidth,
        maxWidth: style.maxWidth,
        boxSizing: style.boxSizing,
        paddingInline: `${style.paddingLeft} ${style.paddingRight}`,
        borderInline: `${style.borderLeftWidth} ${style.borderRightWidth}`,
        inlineStyle: node.getAttribute('style')
      };
    };
    const wrapper = grid.querySelector('.gridjs-wrapper');
    const container = grid.querySelector('.gridjs-container');
    const table = grid.querySelector('table.gridjs-table');
    const columns = [...(table?.tHead?.rows[0]?.cells || [])].map(header => {
      const rect = header.getBoundingClientRect(), style = getComputedStyle(header);
      return {
        text: header.querySelector('.gridjs-th-content')?.textContent.trim() || '',
        key: header.getAttribute('data-request-column'),
        width: rect.width,
        computedWidth: style.width,
        inlineWidth: header.style.width || null,
        clientWidth: header.clientWidth,
        scrollWidth: header.scrollWidth,
        boxSizing: style.boxSizing,
        paddingInline: `${style.paddingLeft} ${style.paddingRight}`,
        borderInline: `${style.borderLeftWidth} ${style.borderRightWidth}`,
        action: !!header.querySelector('button.gridjs-sort')
      };
    });
    const ancestors = [];
    for (let node = grid; node && node !== document.body; node = node.parentElement) {
      ancestors.push({ ...elementMetrics(node), role: node.getAttribute('role') });
    }
    const tableIds = window.__titleRequestLayoutTableIds || (window.__titleRequestLayoutTableIds = new WeakMap());
    if (!tableIds.has(table)) {
      window.__titleRequestLayoutNextTableId = (window.__titleRequestLayoutNextTableId || 0) + 1;
      tableIds.set(table, window.__titleRequestLayoutNextTableId);
    }
    const active = document.activeElement, bounds = active?.getBoundingClientRect();
    const queue = document.querySelector('#queue-view'), countState = document.querySelector('#status-count-state');
    const filters = ['claim-filter', 'tag-filter', 'similar-filter'].map(id => {
      const control = document.getElementById(id), label = control.closest('label'), name = label.querySelector('span');
      const labelBounds = name.getBoundingClientRect(), fieldBounds = label.getBoundingClientRect();
      return {
        id,
        name: name.textContent.trim(),
        isSrOnly: name.classList.contains('sr-only') && getComputedStyle(name).position === 'absolute' &&
          labelBounds.width <= 1 && labelBounds.height <= 1 && getComputedStyle(name).clip !== 'auto',
        hidden: label.hidden,
        fieldHeight: fieldBounds.height,
        controlHeight: control.getBoundingClientRect().height
      };
    });
    const danglingDescriptions = [...document.querySelectorAll('[aria-describedby]')].flatMap(node =>
      (node.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean)
        .filter(id => !document.getElementById(id)).map(id => ({ node: node.id || node.tagName, id })));
    const visibleRemovedCopy = [
      'Tab counts show all requests in the selected library scope.',
      'Workflow rules follow each request’s library; open Details to review.',
      'Scroll the table horizontally to reach every column. Use a request title to open details.'
    ].filter(copy => (queue.innerText || '').includes(copy));
    const stateBounds = countState.getBoundingClientRect();
    return {
      name: info.name,
      viewportWidth: innerWidth,
      stage: info.stage,
      tableIdentity: tableIds.get(table),
      document: { clientWidth: document.documentElement.clientWidth, scrollWidth: document.documentElement.scrollWidth },
      focusedRegion: active === wrapper,
      focusBounds: bounds ? { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height } : null,
      region: { role: wrapper.getAttribute('role'), tabIndex: wrapper.tabIndex, label: wrapper.getAttribute('aria-label') },
      grid: elementMetrics(grid),
      container: elementMetrics(container),
      wrapper: { ...elementMetrics(wrapper), hasHorizontalOverflow: wrapper.scrollWidth > wrapper.clientWidth },
      table: { ...elementMetrics(table), hasHorizontalOverflow: table.scrollWidth > wrapper.clientWidth },
      columns,
      rowCount: table?.tBodies[0]?.rows.length ?? 0,
      actionNames: [...grid.querySelectorAll('button[data-request-action]')].map(button => button.getAttribute('aria-label')),
      cleanup: {
        removedNodesAbsent: ['status-count-help', 'queue-automation', 'request-table-help']
          .every(id => document.getElementById(id) === null),
        visibleRemovedCopy,
        filterLabels: filters,
        countState: {
          text: countState.textContent,
          role: countState.getAttribute('role'),
          live: countState.getAttribute('aria-live'),
          display: getComputedStyle(countState).display,
          height: stateBounds.height
        },
        statusTabsDescribedBy: document.querySelector('#status-tabs').getAttribute('aria-describedby'),
        regionDescribedBy: wrapper.getAttribute('aria-describedby'),
        danglingDescriptions
      },
      ancestors
    };
  }, { name, stage });
  assert.equal(metric.columns.length, 8, `${name}/${stage}: keep all eight Title columns.`);
  assert.deepEqual(metric.columns.map(column => column.text), [
    'Barcode', 'Bibliographic details', 'Format', 'Submitted', 'Library', 'Claim', 'Status', 'Actions'
  ]);
  assert.deepEqual(metric.columns.map(column => column.key), [
    'patron', 'bibliographic', 'format', 'submitted', 'library', 'claim', 'status', 'actions'
  ]);
  assert.equal(metric.columns[7].action, false, `${name}/${stage}: Actions stays unsortable.`);
  assert.equal(metric.focusedRegion, true, `${name}/${stage}: the table region receives focus.`);
  assert.deepEqual(metric.region, { role: 'region', tabIndex: 0, label: 'Title requests' });
  assert.equal(metric.cleanup.removedNodesAbsent, true, `${name}/${stage}: removed helper elements are absent.`);
  assert.deepEqual(metric.cleanup.visibleRemovedCopy, [], `${name}/${stage}: removed helper prose is absent.`);
  assert.deepEqual(metric.cleanup.filterLabels.map(filter => filter.name), ['Claims filter', 'Tag filter', 'Similarity filter']);
  assert.ok(metric.cleanup.filterLabels.filter(filter => !filter.hidden).every(filter =>
    filter.isSrOnly && Math.abs(filter.fieldHeight - filter.controlHeight) <= 1),
  `${name}/${stage}: screen-reader labels add no visible heading or field gap.`);
  assert.deepEqual(metric.cleanup.countState, { text: '', role: 'status', live: 'polite', display: 'none', height: 0 },
    `${name}/${stage}: empty ready feedback reserves no layout space.`);
  assert.equal(metric.cleanup.statusTabsDescribedBy, null, `${name}/${stage}: the tablist has no dangling count description.`);
  assert.equal(metric.cleanup.regionDescribedBy, null, `${name}/${stage}: the scroll region has no dangling description.`);
  assert.deepEqual(metric.cleanup.danglingDescriptions, [], `${name}/${stage}: no aria-describedby reference is dangling.`);
  if (stage === 'closed') {
    assert.equal(metric.actionNames.filter(label => label.startsWith('Reopen request ')).length, 2,
      `${name}: both Closed row actions remain in the table.`);
  }
  return metric;
}

async function scan(page, label) {
  const result = await page.evaluate(async () => {
    const axe = await window.axe.run();
    return {
      seriousOrCritical: axe.violations.filter(item => ['serious', 'critical'].includes(item.impact))
        .map(item => ({ id: item.id, impact: item.impact, help: item.help })),
      documentOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      missingImages: [...document.images].filter(image => image.getClientRects().length > 0)
        .filter(image => !image.complete || image.naturalWidth <= 0).map(image => image.currentSrc || image.src)
    };
  });
  report.scans.push({ label, ...result });
  report.missingImages.push(...result.missingImages);
}

async function capture(page, metric) {
  const wrapper = page.locator('#request-grid .gridjs-wrapper');
  await wrapper.scrollIntoViewIfNeeded();
  await wrapper.evaluate(node => { node.scrollLeft = 0; });
  await wrapper.focus();
  const name = `${metric.stage}-${metric.name}`;
  await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: false });
}

async function collectViewport(page, viewport) {
  await page.setViewportSize({ width: viewport.width, height: viewport.height });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const stillMountedStage = await page.locator('#status-tabs [aria-selected="true"]').getAttribute('data-status');
  report.resizeOnly.push(await measure(page, `${viewport.name}-resize-only`, viewport.width, stillMountedStage));
  const entries = [];
  for (const stage of stages) {
    await selectStage(page, stage);
    const metric = await measure(page, viewport.name, viewport.width, stage);
    entries.push(metric);
    if ([3000, 390, 320].includes(viewport.width)) await scan(page, `${stage}-${viewport.name}`);
    if ([3000, 320].includes(viewport.width) || viewport.width === 390 && ['closed', 'hold_placed'].includes(stage)) await capture(page, metric);
  }
  report.viewportMatrix.push({ ...viewport, stages: entries });
  if (viewport.width === 390) {
    const closed = entries.find(entry => entry.stage === 'closed');
    const wrapper = page.locator('#request-grid .gridjs-wrapper');
    await selectStage(page, 'closed');
    await wrapper.focus();
    const before = await wrapper.evaluate(node => node.scrollLeft);
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(before => document.querySelector('#request-grid .gridjs-wrapper').scrollLeft > before, before);
    closed.keyboardScrolled = true;
    closed.scrollAfterKeyboard = await wrapper.evaluate(node => node.scrollLeft);
  }
}

async function resizeSameMountedGrid(page) {
  const sequence = [];
  for (const width of [3000, 1440, 900, 390, 1440, 3000, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const metric = await measure(page, `same-table-resize-${width}-${sequence.length + 1}`, width, 'closed');
    sequence.push(metric);
    report.resizeOnly.push(metric);
  }
  const tableIdentities = [...new Set(sequence.map(metric => metric.tableIdentity))];
  assert.equal(tableIdentities.length, 1, 'The resize sequence keeps one mounted table without a status or data render.');
  report.resizeSequence = { tableIdentities, measurements: sequence };
}

async function verifySortingAndActions(page, fixture) {
  await selectStage(page, 'closed');
  const headers = page.locator('#request-grid thead th');
  assert.equal(await headers.count(), 8, 'Title requests retains all eight columns.');
  assert.deepEqual(await headers.locator('.gridjs-th-content').allTextContents(), [
    'Barcode', 'Bibliographic details', 'Format', 'Submitted', 'Library', 'Claim', 'Status', 'Actions'
  ]);
  assert.deepEqual(await headers.evaluateAll(nodes => nodes.map(node => node.getAttribute('data-request-column'))),
    ['patron', 'bibliographic', 'format', 'submitted', 'library', 'claim', 'status', 'actions'],
    'Each header retains its Title-only CSS sizing key.');
  assert.equal(await headers.nth(7).locator('button.gridjs-sort').count(), 0, 'Actions stays unsortable.');
  const sort = headers.nth(1), sortButton = sort.locator('button.gridjs-sort');
  await sort.focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.querySelector('#request-grid thead th:nth-child(2)')?.getAttribute('aria-sort') === 'ascending');
  const enterSort = await sort.evaluate(node => node.getAttribute('aria-sort'));
  assert.equal(enterSort, 'ascending');
  await page.keyboard.press('Space');
  await page.waitForFunction(() => document.querySelector('#request-grid thead th:nth-child(2)')?.getAttribute('aria-sort') === 'descending');
  const spaceSort = await sort.evaluate(node => node.getAttribute('aria-sort'));
  assert.equal(spaceSort, 'descending');
  assert.equal(await sortButton.getAttribute('aria-label'), 'Sort Bibliographic details ascending');

  await page.waitForFunction(({ id, expectedFirst }) => {
    const links = [...document.querySelectorAll('#request-grid tbody .request-title-link')];
    const row = links.find(link => link.dataset.requestId === id)?.closest('tr');
    const titleSearch = row?.querySelector('button.request-search-shortcut');
    return links.length === 2 && links[0].dataset.requestId === id && links[0].textContent.includes(expectedFirst) &&
      links[1].textContent.includes('Long bibliographic title segment') && row?.querySelectorAll('button.request-search-shortcut').length === 2 &&
      titleSearch?.isConnected && !titleSearch.disabled && titleSearch.getClientRects().length > 0;
  }, { id: fixture.closedIds[0], expectedFirst: expectedTitles.closed[0] });
  const row = page.locator(`#request-grid a[data-request-id="${fixture.closedIds[0]}"]`).locator('xpath=ancestor::tr');
  assert.equal(await row.locator('button.request-search-shortcut').count(), 2, 'Title and author lookup shortcuts remain in the bibliographic cell.');
  const titleSearch = row.locator('button.request-search-shortcut').first();
  await titleSearch.focus();
  const titleFocus = await titleSearch.evaluate(node => ({ active: node === document.activeElement,
    connected: node.isConnected, disabled: node.disabled, bounds: node.getBoundingClientRect().toJSON() }));
  assert.equal(titleFocus.active, true, `Title lookup remains keyboard focusable: ${JSON.stringify(titleFocus)}`);
  const action = row.locator('button[data-request-action="reopen"]');
  assert.equal(await action.count(), 1, 'Closed row keeps its Reopen action.');
  await action.waitFor({ state: 'visible' });
  await action.focus();
  assert.equal(await action.evaluate(node => node === document.activeElement), true, 'Row actions remain keyboard focusable.');
  return {
    sortEnter: enterSort,
    sortSpace: spaceSort,
    sortButtonNext: await sortButton.getAttribute('aria-label'),
    action: await action.getAttribute('aria-label')
  };
}

async function verifyActionColumnSizingAndCleanFlows(page, fixture, artifacts) {
  await selectStage(page, 'suggestion');
  const row = page.locator(`#request-grid a[data-request-id="${fixture.suggestionId}"]`).locator('xpath=ancestor::tr');
  await row.locator('.request-row-actions[data-ready]').waitFor();
  const table = page.locator('#request-grid table.gridjs-table');
  const wrapper = page.locator('#request-grid .gridjs-wrapper');
  const tableElement = await table.elementHandle();
  const keys = ['purchase', 'reject', 'alreadyOwn', 'claim', 'assign', 'silentClose'];
  const widthMetrics = async () => ({
    action: await row.locator('td').nth(7).evaluate(node => node.getBoundingClientRect().width),
    table: await table.evaluate(node => node.getBoundingClientRect().width),
    columns: await page.locator('#request-grid thead th').evaluateAll(nodes => nodes.map(node => ({
      key: node.getAttribute('data-request-column'), width: node.getBoundingClientRect().width
    }))),
    wrapper: await wrapper.evaluate(node => ({ clientWidth: node.clientWidth, scrollWidth: node.scrollWidth })),
    tableScrollWidth: await table.evaluate(node => node.scrollWidth),
    document: await page.evaluate(() => ({ clientWidth: document.documentElement.clientWidth,
      scrollWidth: document.documentElement.scrollWidth }))
  });
  const assertFit = async label => {
    const widths = await widthMetrics();
    assert.equal(widths.wrapper.scrollWidth, widths.wrapper.clientWidth, `${label}: wrapper has no wide overflow.`);
    assert.equal(widths.tableScrollWidth, widths.wrapper.clientWidth, `${label}: table exactly fits its wrapper.`);
    assert.equal(widths.document.scrollWidth, widths.document.clientWidth, `${label}: no document-level overflow.`);
    assert.equal(await tableElement.evaluate(node => node.isConnected), true, `${label}: the mounted table is preserved.`);
    return widths;
  };

  const original = await actionGeometry(row);
  assert.deepEqual(original.keys, keys, 'The suggestion row retains all six eligible commands without duplicates.');
  const originalWidths = await assertFit('Original Title proportions');
  await actionColumn(page, 70);
  const wide = await actionGeometry(row);
  const wideWidths = await assertFit('70% Actions column');
  assert.ok(wideWidths.action > originalWidths.action + 1, 'An inline 70% Actions width expands the actual table column.');
  assert.ok(Math.abs(wideWidths.action - wideWidths.table * 0.70) <= 1, 'The actual Actions cell follows the inline 70% width.');
  assert.deepEqual(wide.inline, keys, 'At 70%, all six commands are direct.');
  assert.deepEqual(wide.overflow, []);
  await row.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(artifacts, 'suggestion-actions-wide-70.png'), fullPage: false });

  await actionColumn(page, 12);
  const narrow = await actionGeometry(row);
  const narrowWidths = await assertFit('12% Actions column');
  assert.ok(narrowWidths.action < wideWidths.action - 1, 'An inline 12% Actions width reduces the actual table column.');
  assert.ok(Math.abs(narrowWidths.action - narrowWidths.table * 0.12) <= 1, 'The actual Actions cell follows the inline 12% width.');
  assert.deepEqual(narrow.inline, ['purchase'], 'The first meaningful command stays direct at 12%.');
  assert.deepEqual(narrow.overflow, keys.slice(1), 'The other five commands move into More without duplication.');
  assert.equal(narrow.more, 1);
  await row.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(artifacts, 'suggestion-actions-narrow-12.png'), fullPage: false });

  await actionColumn(page);
  const restored = await actionGeometry(row);
  const restoredWidths = await assertFit('Restored Title proportions');
  assert.ok(Math.abs(restoredWidths.action - originalWidths.action) <= 1, 'Reset restores the original Actions width.');
  assert.deepEqual(restored.inline, original.inline, 'Reset restores the original direct command set.');
  assert.deepEqual(restored.overflow, original.overflow, 'Reset restores the original More command set.');
  assert.ok(restored.overflow.length > 0, 'The original Actions width provides a More trigger for keyboard checks.');
  const proportions = [0.14, 0.23, 0.08, 0.10, 0.10, 0.09, 0.09, 0.17];
  assert.deepEqual(restoredWidths.columns.map((column, index) => Math.abs(column.width - restoredWidths.table * proportions[index]) <= 1),
    proportions.map(() => true), 'Reset restores all eight fractional Title column proportions.');
  await row.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(artifacts, 'suggestion-actions-restored.png'), fullPage: false });

  const more = row.locator('.request-row-more');
  const openAndEscape = async (key, firstOverflowKey) => {
    await more.focus();
    assert.equal(await more.evaluate(node => node === document.activeElement), true, `${key} starts from the More trigger.`);
    await page.keyboard.press(key);
    assert.equal(await more.getAttribute('aria-expanded'), 'true', `${key} opens the More menu.`);
    assert.equal(await page.evaluate(() => document.activeElement?.dataset.requestAction), firstOverflowKey,
      `${key} opens More with its first overflow command focused.`);
    await page.keyboard.press('Escape');
    await page.waitForFunction(id => {
      const link = document.querySelector(`#request-grid a[data-request-id="${id}"]`);
      const row = link?.closest('tr');
      const more = row?.querySelector('.request-row-more');
      return more?.getAttribute('aria-expanded') === 'false' && document.activeElement === more;
    }, fixture.suggestionId);
  };
  await openAndEscape('Enter', restored.overflow[0]);
  await openAndEscape('Space', restored.overflow[0]);

  const writes = [];
  const requestPrefix = `/api/asap/staff/title-requests/${fixture.suggestionId}`;
  const watchWrites = request => {
    const pathname = new URL(request.url()).pathname;
    if (request.method() !== 'GET' && (pathname === requestPrefix || pathname.startsWith(`${requestPrefix}/`) ||
        pathname === `/api/asap/staff/requests/${fixture.suggestionId}`)) {
      writes.push({ method: request.method(), pathname });
    }
  };
  page.on('request', watchWrites);
  await activateRowAction(row, 'assign');
  const dialog = page.locator('#request-dialog[open]');
  await dialog.waitFor();
  const assignment = dialog.locator('.inline-form');
  await assignment.waitFor();
  assert.equal(await dialog.locator('.action-bar').isVisible(), false, 'Assign opens one focused inline panel.');
  assert.equal(await assignment.getByRole('button', { name: 'Cancel', exact: true }).isVisible(), true);
  await assignment.getByRole('button', { name: 'Cancel', exact: true }).click();
  await assignment.waitFor({ state: 'detached' });
  assert.equal(await dialog.locator('.action-bar').isVisible(), true, 'Cancel returns to the single action toolbar.');

  const footerClose = dialog.locator('.request-save-footer').getByRole('button', { name: 'Close', exact: true });
  await footerClose.waitFor();
  assert.equal(await footerClose.isEnabled(), true, 'A clean request has an enabled footer Close.');
  await footerClose.press('Enter');
  await page.locator('#request-dialog[open]').waitFor({ state: 'hidden' });
  await page.waitForFunction(id => {
    const titleLink = document.querySelector(`#request-grid .grid-open[data-request-id="${id}"]`);
    return titleLink && document.activeElement === titleLink;
  }, fixture.suggestionId);
  page.off('request', watchWrites);
  assert.deepEqual(writes, [], 'Opening More/Assign and closing clean details sends no mutation request.');

  await selectStage(page, 'closed');
  return {
    commandKeys: keys,
    original: { actionWidth: originalWidths.action, inline: original.inline, overflow: original.overflow },
    wide70: { actionWidth: wideWidths.action, inline: wide.inline, overflow: wide.overflow },
    narrow12: { actionWidth: narrowWidths.action, inline: narrow.inline, overflow: narrow.overflow, more: narrow.more },
    restored: { actionWidth: restoredWidths.action, inline: restored.inline, overflow: restored.overflow },
    moreKeyboard: ['Enter', 'Space'].map(key => ({ key, escapedToTrigger: true })),
    assignment: { opened: true, canceled: true, toolbarReturned: true },
    footerClose: { clean: true, keyboard: 'Enter', focusReturnedToTitleLink: true },
    mutationRequests: writes
  };
}

(async () => {
  const fixture = JSON.parse(await fs.readFile(path.join(artifacts, 'fixture.json'), 'utf8'));
  const baseOrigin = new URL(baseURL).origin;
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: headers(fixture.super) });
    await guard(context, baseOrigin);
    const page = await context.newPage();
    await openStaffPage(page, fixture);
    report.sortAndActions = await verifySortingAndActions(page, fixture);
    report.actionColumn = await verifyActionColumnSizingAndCleanFlows(page, fixture, artifacts);
    await resizeSameMountedGrid(page);
    for (const viewport of viewports) await collectViewport(page, viewport);
    await context.close();

    const extension = path.join(__dirname, 'native-zoom-extension');
    const width = 1440;
    const zoomContext = await chromium.launchPersistentContext(path.join(artifacts, 'native-zoom-1440'), {
      channel: 'chromium', headless: true, viewport: null, extraHTTPHeaders: headers(fixture.super),
      args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`, `--window-size=${width},900`]
    });
    try {
      await guard(zoomContext, baseOrigin);
      const worker = zoomContext.serviceWorkers()[0] || await zoomContext.waitForEvent('serviceworker');
      const zoomPage = zoomContext.pages()[0] || await zoomContext.newPage();
      zoomPage.on('pageerror', error => report.errors.push(error.message));
      await openStaffPage(zoomPage, fixture);
      const ratio = await zoomPage.evaluate(() => devicePixelRatio);
      assert.equal(await worker.evaluate(async origin => {
        const tab = (await chrome.tabs.query({})).find(candidate => candidate.url.startsWith(origin));
        await chrome.tabs.setZoomSettings(tab.id, { mode: 'automatic', scope: 'per-tab' });
        await chrome.tabs.setZoom(tab.id, 2);
        return chrome.tabs.getZoom(tab.id);
      }, baseOrigin), 2);
      await zoomPage.waitForFunction(ratio => Math.abs(devicePixelRatio / ratio - 2) < 0.01, ratio);
      const cdp = await zoomContext.newCDPSession(zoomPage);
      const layoutMetrics = await cdp.send('Page.getLayoutMetrics');
      const cssZoom = await zoomPage.evaluate(() => getComputedStyle(document.documentElement).zoom);
      const zoomedDevicePixelRatio = await zoomPage.evaluate(() => devicePixelRatio);
      assert.equal(layoutMetrics.visualViewport.zoom, 2);
      assert.equal(cssZoom, '1');
      report.nativeZoomContext = {
        physicalWidth: width,
        visualViewportZoom: layoutMetrics.visualViewport.zoom,
        cssViewportWidth: await zoomPage.evaluate(() => innerWidth),
        devicePixelRatioBefore: ratio,
        devicePixelRatioAfter: zoomedDevicePixelRatio,
        cssZoom
      };
      report.resizeOnly.push(await measure(zoomPage, 'native-200-resize-only', width, 'closed'));
      for (const stage of ['closed', 'hold_placed']) {
        await selectStage(zoomPage, stage);
        const metric = await measure(zoomPage, 'native-200-1440', width, stage);
        const wrapper = zoomPage.locator('#request-grid .gridjs-wrapper');
        const before = await wrapper.evaluate(node => node.scrollLeft);
        await zoomPage.keyboard.press('ArrowRight');
        await zoomPage.waitForFunction(before => document.querySelector('#request-grid .gridjs-wrapper').scrollLeft > before, before);
        metric.keyboardScrolled = true;
        metric.scrollAfterKeyboard = await wrapper.evaluate(node => node.scrollLeft);
        report.nativeZoom.push(metric);
        await scan(zoomPage, `${stage}-native-200`);
        await wrapper.scrollIntoViewIfNeeded();
        await wrapper.evaluate(node => { node.scrollLeft = 0; });
        await wrapper.focus();
        const png = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
        await fs.writeFile(path.join(artifacts, `${metric.stage}-${metric.name}.png`), Buffer.from(png.data, 'base64'));
      }
      await cdp.detach();
    } finally {
      await zoomContext.close();
    }

    const wide = report.viewportMatrix.filter(item => item.width >= 1280);
    const overwide = stage => stage.wrapper.scrollWidth !== stage.wrapper.clientWidth ||
      stage.table.scrollWidth !== stage.wrapper.clientWidth;
    const wideOverflow = [
      ...wide.flatMap(item => item.stages.filter(overwide)
        .map(stage => ({ viewport: item.name, stage: stage.stage, wrapper: stage.wrapper, table: stage.table }))),
      ...report.resizeOnly.filter(stage => stage.viewportWidth >= 1280 && overwide(stage))
        .map(stage => ({ viewport: stage.name, stage: stage.stage, wrapper: stage.wrapper, table: stage.table }))
    ];
    const narrow = report.viewportMatrix.find(item => item.width === 390);
    const shortNarrow = report.viewportMatrix.find(item => item.width === 320);
    const intermediate = report.viewportMatrix.find(item => item.width === 900);
    const mobileScroll = narrow.stages.find(stage => stage.stage === 'closed');
    const allMeasurements = [
      ...report.viewportMatrix.flatMap(item => item.stages),
      ...report.resizeOnly,
      ...report.nativeZoom
    ];
    const proportions = [0.14, 0.23, 0.08, 0.10, 0.10, 0.09, 0.09, 0.17];
    report.assertions = {
      wideOverflow,
      mobileContained: narrow.stages.every(stage => stage.document.scrollWidth <= stage.document.clientWidth),
      mobileTableScrolls: mobileScroll.wrapper.hasHorizontalOverflow && mobileScroll.keyboardScrolled,
      shortNarrowContained: shortNarrow.stages.every(stage => stage.document.scrollWidth === stage.document.clientWidth),
      shortNarrowTableScrolls: shortNarrow.stages.every(stage => stage.wrapper.hasHorizontalOverflow),
      intermediateContainedScrolling: intermediate.stages.every(stage => stage.table.minWidth === '1080px' &&
        stage.wrapper.scrollWidth > stage.wrapper.clientWidth && stage.document.scrollWidth === stage.document.clientWidth),
      proportionalWidthErrors: allMeasurements.flatMap(metric => metric.columns.flatMap((column, index) => {
        const expected = metric.table.rectWidth * proportions[index];
        return Math.abs(column.width - expected) > 1
          ? [{ name: metric.name, stage: metric.stage, column: column.text, width: column.width, expected }]
          : [];
      })),
      keyboardRegionFailures: allMeasurements.filter(metric => !metric.focusedRegion || metric.region.role !== 'region' ||
        metric.region.tabIndex !== 0 || metric.region.label !== 'Title requests')
        .map(metric => ({ name: metric.name, stage: metric.stage, focused: metric.focusedRegion, region: metric.region })),
      documentOverflow: [...report.viewportMatrix.flatMap(item => item.stages), ...report.resizeOnly, ...report.nativeZoom]
        .filter(stage => stage.document.scrollWidth > stage.document.clientWidth)
        .map(stage => ({ viewport: stage.name, stage: stage.stage, document: stage.document })),
      axeSeriousOrCritical: report.scans.flatMap(scan => scan.seriousOrCritical.map(item => ({ label: scan.label, ...item }))),
      missingImages: report.missingImages,
      externalRequests: report.external,
      pageErrors: report.errors
    };
    assert.equal(report.resizeSequence.tableIdentities.length, 1, 'The measured resize path stays on one DOM table.');
    assert.deepEqual(report.assertions.wideOverflow, [], 'No unnecessary horizontal scrollbar at desktop widths.');
    assert.equal(report.assertions.intermediateContainedScrolling, true, 'Intermediate widths retain contained 1080px table scrolling.');
    assert.equal(report.assertions.mobileContained, true, 'Narrow scrolling stays contained at 390 CSS pixels.');
    assert.equal(report.assertions.mobileTableScrolls, true, 'The narrow table remains keyboard-scrollable in its region.');
    assert.equal(report.assertions.shortNarrowContained, true, 'The short 320 CSS pixel viewport has no document overflow.');
    assert.equal(report.assertions.shortNarrowTableScrolls, true, 'Every status retains contained table scrolling at short/narrow size.');
    assert.deepEqual(report.assertions.proportionalWidthErrors, [], 'The original column proportions remain exact within pixel rounding.');
    assert.deepEqual(report.assertions.keyboardRegionFailures, [], 'The table region retains its keyboard focus contract.');
    assert.deepEqual(report.assertions.documentOverflow, [], 'No document-level horizontal overflow.');
    assert.deepEqual(report.assertions.axeSeriousOrCritical, [], 'No serious or critical axe findings.');
    assert.deepEqual(report.assertions.missingImages, [], 'No visible image is missing.');
    assert.deepEqual(report.assertions.externalRequests, [], 'The browser made no external requests.');
    assert.deepEqual(report.assertions.pageErrors, [], 'The page raised no errors.');
    assert.equal(report.nativeZoom.length, 2);
    assert.ok(report.nativeZoom.every(stage => stage.wrapper.hasHorizontalOverflow && stage.keyboardScrolled &&
      stage.document.scrollWidth <= stage.document.clientWidth),
      'True 200% browser zoom retains contained title-table scrolling.');
  } finally {
    await fs.writeFile(path.join(artifacts, 'browser-results.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
