'use strict';
const assert = require('node:assert/strict');
const { must } = require('./environment.cjs');
const { waitForConfirmation } = require('../../browser/confirmation.cjs');

async function json(context, origin, pathname) {
  const response = await context.request.get(origin + pathname, { maxRedirects: 0 });
  must(response.ok(), 'Authenticated authoritative application read failed.');
  return response.json();
}
const detail = (context, origin, fixture) => json(context, origin,
  '/api/asap/staff/' + (fixture.key.endsWith('-copy') ? 'additional-copies/' : 'title-requests/') + fixture.id);
async function openFixture(page, origin, fixture) {
  await page.goto(origin + '/staff/?scope=2&' + (fixture.key.endsWith('-copy') ? 'stage=additional_copies&request=' : 'request=') + fixture.id);
  await page.locator('#request-dialog[open]').waitFor();
  // Request data and capabilities must have arrived, not just the dialog shell.
  await page.locator('#request-dialog .action-bar').waitFor();
}
async function reveal(page) {
  const more = page.locator('#request-dialog .request-more');
  if (await more.count() && !await more.evaluate(n => n.open)) await more.locator('summary').click();
}
async function actionButton(page, name) {
  const bar = page.locator('#request-dialog .action-bar');
  // Hidden-role names include aria-hidden icon glyphs in Chromium. Locate the
  // rendered label in the DOM first, then use the normal accessible role/name
  // after revealing only the menu that actually owns this action.
  const label = new RegExp('^\\s*' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*$');
  const target = bar.locator('button').filter({ hasText: label });
  await target.waitFor({ state: 'attached' });
  if (await target.evaluate(node => Boolean(node.closest('.request-more')))) await reveal(page);
  const button = bar.getByRole('button', { name, exact: true });
  await button.waitFor({ state: 'visible' });
  return button;
}
async function choose(page, name) {
  await (await actionButton(page, name)).click();
  const review = page.locator('#request-dialog .action-choice');
  await review.waitFor();
  await review.locator('button[type=submit]:not([disabled])').waitFor();
  assert.equal(await page.locator('#request-dialog .request-more[open]').count(), 0, 'Action selection lets the application dismiss More');
  return review;
}
async function confirm(page, label) {
  const decision = await waitForConfirmation(page);
  await page.locator('#confirmation-dialog').getByRole('button', { name: label, exact: true }).click();
  await page.locator('#confirmation-dialog[open]').waitFor({ state: 'hidden' });
  return decision;
}
async function unchanged(context, origin, fixture, before) {
  const current = await detail(context, origin, fixture);
  assert.equal(current.version, before.version, 'Cancel preserves authoritative rowversion');
  assert.equal(current.status, before.status, 'Cancel preserves authoritative status');
  assert.equal(current.notes, before.notes, 'Cancel preserves persisted notes');
  assert.deepEqual(current.activity, before.activity, 'Cancel appends no history');
}
async function submit(page, button, fixture, expectedStatus, double = false) {
  const pathname = '/api/asap/staff/title-requests/' + fixture.id + '/action';
  const response = page.waitForResponse(r => new URL(r.url()).pathname === pathname && r.request().method() === 'POST');
  if (double) {
    // Dispatch two actual DOM clicks in one browser task, before a response can arrive.
    await button.evaluate(n => { n.click(); n.click(); });
  } else { await button.click(); }
  const result = await response;
  assert.equal(result.status(), expectedStatus, 'Expected authoritative action response');
  return result.json();
}
module.exports = { json, detail, openFixture, reveal, actionButton, choose, confirm, unchanged, submit };
