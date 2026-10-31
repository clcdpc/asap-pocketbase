'use strict';
const assert = require('node:assert/strict');
const { requireFixture } = require('../helpers/environment.cjs');
const { checkpoint } = require('../helpers/accessibility.cjs');
const { detail, json, openFixture, actionButton, submit } = require('../helpers/assertions.cjs');
const { waitForConfirmation } = require('../../browser/confirmation.cjs');
const { human } = require('./pr410.cjs');

function initialRequest(request, fixture) {
  assert.equal(request.id, fixture.id, 'Authoritative request identity');
  assert.equal(request.legacyId, fixture.key, 'Authoritative synthetic fixture identity');
  assert.equal(request.libraryOrgId, 2, 'Request belongs to Review Library');
  assert.equal(request.status, 'suggestion', 'Authoritative initial Suggestion status');
  assert.equal(typeof request.version, 'string', 'Authoritative initial rowversion');
  assert.ok(request.version.length > 0, 'Authoritative initial rowversion is present');
}

const scenarios = [
  { id: 'smoke-authenticated-review', requirements: [
    'smoke: authorized super-admin session, Review Library (2), SQL-backed fixture details and unmasked synthetic screenshot'
  ], writes: false, run: async t => {
    await t.page.goto(t.origin + '/staff/?scope=2');
    await t.page.locator('#workspace').waitFor({ state: 'visible' });
    const session = await json(t.context, t.origin, '/api/asap/staff/session');
    assert.equal(session.authenticated, true, 'Staff session is authenticated');
    assert.equal(session.accessAllowed, true, 'Staff access is allowed');
    assert.equal(session.staff?.role, 'super_admin', 'Expected reviewer role');
    const fixture = requireFixture((await t.verify()).evidence, 'draft');
    await openFixture(t.page, t.origin, fixture);
    const request = await detail(t.context, t.origin, fixture);
    initialRequest(request, fixture);
    await t.page.locator('#request-dialog').getByRole('heading', { name: request.title, exact: true }).waitFor();
    await t.page.locator('#request-dialog .status-badge').filter({ hasText: /^Suggestion$/ }).waitFor();
    assert.equal(t.mutations.length, 0, 'Authenticated review performs no mutation');
    await checkpoint(t.page, t.directory, t.id, { visible: [t.page.locator('#request-dialog-title'),
      t.page.locator('#request-dialog .status-badge')] });
  } },
  { id: 'smoke-synthetic-close', requirements: [
    'smoke: authorized UI closure, one action POST, persisted Closed and changed rowversion'
  ], writes: true, run: async t => {
    const fixture = requireFixture((await t.verify()).evidence, 'close-reopen');
    t.allowFixture(fixture);
    await openFixture(t.page, t.origin, fixture);
    const before = await detail(t.context, t.origin, fixture);
    initialRequest(before, fixture);
    await t.page.locator('#request-dialog').getByRole('heading', { name: before.title, exact: true }).waitFor();
    await (await actionButton(t.page, 'Close silently')).click();
    const decision = await waitForConfirmation(t.page);
    const dialog = t.page.locator('#confirmation-dialog[open]');
    await dialog.getByRole('heading', { name: 'Close silently', exact: true }).waitFor();
    assert.match(decision.message(), /without a rejection email/);
    assert.equal(t.mutations.length, 0, 'Confirmation precedes server mutation');
    const outcome = await submit(t.page, dialog.getByRole('button', { name: 'Close silently', exact: true }), fixture, 200);
    assert.equal(outcome.committed, true, 'Closure response confirms a commit');
    assert.equal(outcome.finalStatus, 'closed', 'Closure response confirms Closed');
    await dialog.waitFor({ state: 'hidden' });
    await t.page.locator('#request-dialog-status').filter({ hasText: /^Closed · Closed silently$/ }).waitFor();
    const current = await detail(t.context, t.origin, fixture);
    assert.equal(current.id, fixture.id, 'Closed request identity');
    assert.equal(current.status, 'closed', 'Authoritative persisted Closed status');
    assert.equal(typeof current.version, 'string', 'Authoritative final rowversion');
    assert.ok(current.version.length > 0, 'Authoritative final rowversion is present');
    assert.notEqual(current.version, before.version, 'Closure changes the rowversion');
    assert.equal(t.mutations.length, 1, 'Exactly one action POST was dispatched');
    assert.equal(t.mutations[0].path, '/api/asap/staff/title-requests/' + fixture.id + '/action');
    assert.equal(t.mutations[0].action, 'silentClose');
    assert.equal(t.mutations[0].version, before.version, 'Closure uses the initial rowversion');
    await checkpoint(t.page, t.directory, t.id, { visible: [t.page.locator('#request-dialog-title'),
      t.page.locator('#request-dialog .status-badge')] });
  } }
];

module.exports = { id: 'smoke', requires: ['wslc-review-0.4.0', 'sql-server-2022', 'authorized-super-admin',
  'review-library-2', 'deterministic-providers'], mutations: true, scenarios, human,
  uncovered: [['pr410-feature-acceptance', '#401; #407: full PR #410 acceptance',
    'Smoke validates the testing process only. Run the pr410 suite and complete its remaining human and feature acceptance requirements.']] };
