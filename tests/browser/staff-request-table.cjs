'use strict';
const { decideConfirmation } = require('./confirmation.cjs');
const assert = require('node:assert/strict');

module.exports = async function verifyRequestTable({ page, context, baseURL, library, scan }) {
  const grid = page.locator('#request-grid'), search = page.locator('#request-search');
  const closeDetail = async () => {
    await page.keyboard.press('Escape');
    await page.locator('#request-dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => !new URL(location.href).searchParams.has('request'));
  };
  await page.locator('#library-scope').selectOption(library);
  await page.locator('#refresh-queue:not([disabled])').waitFor();
  await page.locator('#claim-filter').selectOption('all');
  await page.waitForFunction(() => document.querySelectorAll('#request-grid tbody tr').length === 25);
  assert.deepEqual(await grid.locator('th .gridjs-th-content').allTextContents(),
    ['Barcode', 'Bibliographic details', 'Format', 'Submitted', 'Library', 'Claim', 'Status', 'Actions']);
  await grid.locator('th:nth-child(2) button').click();
  await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Quick-create source');
  await grid.getByRole('button', { name: 'Next', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Table browser record 24');
  const previous = await grid.locator('.request-title-link').first().elementHandle();
  await page.locator('#refresh-queue').click();
  await page.waitForFunction(node => !node.isConnected, previous);
  await previous.dispose();
  await page.waitForFunction(() => !document.querySelector('#refresh-queue').disabled &&
    document.querySelector('#request-grid .request-title-link')?.textContent === 'Table browser record 24');
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 0)))));
  assert.equal(await grid.locator('.gridjs-currentPage').textContent(), '2');
  assert.match(await grid.locator('.gridjs-summary').textContent(), /26 to 50 of 56/);
  await search.fill('TABLE-BROWSER-00');
  await page.waitForFunction(() => document.querySelectorAll('#request-grid .request-title-link').length === 1);
  const row = grid.locator('tbody tr');
  const id = await row.locator('.request-title-link').getAttribute('data-request-id');
  const before = await (await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`)).json();
  let release, started, completed;
  const held = new Promise(done => { release = done; }), pending = new Promise(done => { started = done; });
  const completion = new Promise(done => { completed = done; });
  const lookupRoute = '**/staff/bib-lookup';
  await context.route(lookupRoute, async route => {
    const body = route.request().postDataJSON();
    if (body.mode !== 'title') return route.continue();
    const response = await route.fetch(); started(body); await held;
    try { await route.fulfill({ response }); } catch (error) {
      if (!/closed|canceled|cancelled|invalid interception/i.test(error.message)) throw error;
    } finally { completed(); }
  });
  try {
    await row.getByRole('button', { name: 'Search Polaris for title: Table browser record 00', exact: true }).click();
    assert.equal(await page.locator('#request-dialog').isVisible(), false);
    assert.equal(await page.locator('#polaris-mode').inputValue(), 'title');
    assert.equal(await page.locator('#polaris-query').inputValue(), before.title);
    const lookup = page.locator('#polaris-dialog');
    await lookup.getByRole('button', { name: 'Search Polaris', exact: true }).click();
    assert.equal((await pending).query, before.title);
    await page.keyboard.press('Escape');
    await row.getByRole('button', { name: 'Search Polaris for author: Table browser author 00', exact: true }).click();
    assert.equal(await page.locator('#polaris-mode').inputValue(), 'author');
    assert.equal(await page.locator('#polaris-query').inputValue(), before.author);
    const found = page.waitForResponse(response => response.url().endsWith('/staff/bib-lookup') && response.request().postDataJSON().mode === 'author');
    await lookup.getByRole('button', { name: 'Search Polaris', exact: true }).click();
    const response = await found; assert.equal(response.status(), 200, await response.text());
    assert.equal(response.request().postDataJSON().query, before.author);
    await lookup.getByText('Current Author lookup result retained', { exact: true }).waitFor();
    release();
    await completion;
    assert.match(await lookup.textContent(), /Current Author lookup result retained/);
    assert.doesNotMatch(await lookup.textContent(), /Stale Title lookup result/);
    assert.equal(await page.locator('#polaris-mode').inputValue(), 'author');
    assert.equal(await lookup.getByRole('button', { name: /^Use BIB/ }).count(), 0);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#request-dialog').isVisible(), false);
    const after = await (await context.request.get(`${baseURL}/api/asap/staff/title-requests/${id}`)).json();
    assert.deepEqual(after, before, 'Catalog shortcuts must not modify the request or append activity.');
  } finally { release(); await context.unroute(lookupRoute); }
  const more = row.getByRole('button', { name: `More actions for request ${id}`, exact: true });
  await more.focus(); await page.keyboard.press('Enter');
  await page.waitForFunction(id => document.querySelector(`#request-row-${id}-actions`).matches(':popover-open') &&
    document.querySelector(`[aria-controls="request-row-${id}-actions"]`).getAttribute('aria-expanded') === 'true', id);
  assert.equal(await more.getAttribute('aria-expanded'), 'true');
  const bounds = await page.locator(`#request-row-${id}-actions`).boundingBox(), viewport = page.viewportSize();
  assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= viewport.width && bounds.y + bounds.height <= viewport.height);
  await scan('table-more-keyboard');
  await page.keyboard.press('Escape');
  assert.equal(await more.evaluate(node => node === document.activeElement), true);
  await row.locator('td').nth(2).click();
  await page.locator('#request-dialog[open]').waitFor();
  assert.equal(await page.locator('#request-dialog').getByLabel('Title', { exact: true }).inputValue(), before.title);
  assert.equal(await page.locator('#request-dialog .request-more').count(), 0);
  await decideConfirmation(page, () => page.locator('#request-dialog').getByRole('button', { name: 'Close silently', exact: true }).press('Enter'), dialog => dialog.dismiss());
  assert.equal(await page.locator('#request-dialog').getByRole('button', { name: 'Close silently', exact: true }).evaluate(node => node === document.activeElement), true);
  await closeDetail();
  await row.locator('.request-title-link').focus(); await page.keyboard.press('Enter');
  await page.locator('#request-dialog[open] .edit-form').waitFor();
  await closeDetail();
  const actions = [];
  for (const [index, action, status] of [['00', 'Purchase', 'outstanding_purchase'], ['01', 'Reject', 'closed']]) {
    // Accepted actions follow the request to its new stage. Return through the
    // actual Suggestions tab before choosing the next independent request.
    await page.locator('#status-tabs [data-status="suggestion"]').click();
    await page.locator('#refresh-queue:not([disabled])').waitFor();
    await search.fill(`TABLE-BROWSER-${index}`);
    try {
      await page.waitForFunction(text => document.querySelectorAll('#request-grid .request-title-link').length === 1 &&
        document.querySelector('#request-grid .request-title-link').textContent === text, `Table browser record ${index}`);
    } catch (error) {
      throw new Error(`List action ${action}: ${JSON.stringify(await page.evaluate(() => ({
        url: location.href, search: document.querySelector('#request-search').value,
        summary: document.querySelector('#queue-summary').textContent,
        titles: [...document.querySelectorAll('#request-grid .request-title-link')].map(node => node.textContent)
      })))}`, { cause: error });
    }
    const requestId = await row.locator('.request-title-link').getAttribute('data-request-id');
    await row.getByRole('button', { name: `${action} request ${requestId}`, exact: true }).click();
    const dialog = page.locator('#request-dialog[open]');
    await dialog.locator('.action-choice button[type="submit"]:not([disabled])').waitFor();
    assert.equal(await dialog.getByLabel('Title', { exact: true }).inputValue(), `Table browser record ${index}`);
    const reminder = dialog.getByLabel('Send purchase reminder', { exact: true });
    if (await reminder.count()) await reminder.uncheck();
    const committed = page.waitForResponse(response => response.url().endsWith(`/title-requests/${requestId}/action`));
    await dialog.locator('.action-choice button[type="submit"]').click();
    const response = await committed; assert.equal(response.status(), 200, await response.text());
    const saved = await response.json(); assert.equal(saved.finalStatus, status);
    await page.waitForFunction(() => !document.querySelector('#request-dialog .action-choice'));
    actions.push({ id: requestId, status }); await closeDetail();
  }
  await page.locator('#status-tabs [data-status="suggestion"]').click();
  await page.locator('#refresh-queue:not([disabled])').waitFor();
  await search.fill('TABLE-BROWSER-02');
  await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Table browser record 02');
  const claimId = await row.locator('.request-title-link').getAttribute('data-request-id');
  await row.getByRole('button', { name: `More actions for request ${claimId}`, exact: true }).click();
  await page.locator(`#request-row-${claimId}-actions`).getByRole('button', { name: `Claim request ${claimId}`, exact: true }).click();
  await page.locator('#request-dialog').getByRole('button', { name: 'Unclaim', exact: true }).waitFor();
  await page.locator('#request-dialog').getByRole('button', { name: 'Unclaim', exact: true }).click();
  await page.locator('#request-dialog').getByRole('button', { name: 'Claim', exact: true }).waitFor();
  await closeDetail();
  return actions;
};
