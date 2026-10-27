'use strict';
const assert = require('node:assert/strict');
const { Blocked } = require('../helpers/reporting.cjs');
const { requireFixture, must } = require('../helpers/environment.cjs');
const { checkpoint, confirmationKeyboard, nativeZoom } = require('../helpers/accessibility.cjs');
const { detail, json, openFixture, actionButton, choose, confirm, unchanged, submit } = require('../helpers/assertions.cjs');

const titlePath = f => '/api/asap/staff/title-requests/' + f.id + '/action';
const confirmation = page => page.locator('#confirmation-dialog[open]');
const reviewSubmit = review => review.locator('button[type=submit]');
async function fresh(t, key, expected) {
  const environment = await t.verify();
  const fixture = requireFixture(environment.evidence, key, expected);
  t.allowFixture(fixture);
  return fixture;
}
async function requestDraft(t, key = 'draft') {
  const f = await fresh(t, key);
  await openFixture(t.page, t.origin, f);
  const before = await detail(t.context, t.origin, f);
  const notes = t.page.locator('#request-dialog').getByLabel('Notes', { exact: true });
  await notes.fill('Synthetic acceptance unsaved draft');
  await t.page.locator('.request-save-state').filter({ hasText: 'Unsaved changes' }).waitFor();
  return { f, before, notes };
}
async function discard(t, trigger, approve) {
  await trigger();
  await confirmation(t.page).waitFor();
  await confirm(t.page, approve ? 'Discard changes' : 'Keep editing');
}
async function waitStatus(t, f, expected) {
  const deadline = Date.now() + 10000;
  let current;
  do {
    current = await detail(t.context, t.origin, f);
    if (current.status === expected) return current;
    await new Promise(resolve => setTimeout(resolve, 150));
  } while (Date.now() < deadline);
  assert.equal(current.status, expected, 'Authoritative final status');
}
async function purchase(t, key, expected, reminder, duplicate = false) {
  const f = await fresh(t, key, { bibid: expected === 'outstanding_purchase' ? null :
    expected === 'pending_hold' ? 941003 : 941004, verified: expected !== 'outstanding_purchase' });
  await openFixture(t.page, t.origin, f);
  const before = await detail(t.context, t.origin, f);
  let review = await choose(t.page, 'Purchase');
  assert.equal(await confirmation(t.page).count(), 0);
  assert.match(await review.innerText(), expected === 'outstanding_purchase' ? /Outstanding purchase/ :
    expected === 'pending_hold' ? /Pending hold/ : /closes this request without placing a hold/);
  const checkbox = review.getByRole('checkbox', { name: 'Send purchase reminder', exact: true });
  if (expected === 'outstanding_purchase') {
    const session = await json(t.context, t.origin, '/api/asap/staff/session');
    assert.equal(await checkbox.isChecked(), session.staff.purchaseReminderDefault);
    await checkbox.setChecked(reminder);
  } else {
    assert.equal(await checkbox.count(), 0);
    assert.match(await review.innerText(), /reminder does not apply/);
  }
  await checkpoint(t.page, t.directory, t.id + '-review');
  await review.getByRole('button', { name: 'Cancel', exact: true }).click();
  await unchanged(t.context, t.origin, f, before);
  assert.equal(t.mutations.length, 0, 'Review/cancel performs no mutation');
  review = await choose(t.page, 'Purchase');
  if (expected === 'outstanding_purchase') await checkbox.setChecked(reminder);
  await t.page.evaluate(() => {
    window.acceptancePurchaseNotices = [];
    const status = document.querySelector('#app-status');
    new MutationObserver(() => {
      const text = status.textContent;
      if (text.startsWith('Workflow action completed.') && window.acceptancePurchaseNotices.at(-1) !== text) {
        window.acceptancePurchaseNotices.push(text);
      }
    }).observe(status, { childList: true, subtree: true, characterData: true });
  });
  const outcome = await submit(t.page, reviewSubmit(review), f, 200, duplicate);
  assert.equal(outcome.committed, true);
  await t.page.locator('#request-dialog .status-badge').filter({ hasText:
    expected === 'outstanding_purchase' ? 'Outstanding purchase' : expected === 'pending_hold' ? 'Pending hold' : 'Closed' }).waitFor();
  const current = await waitStatus(t, f, expected);
  assert.notEqual(current.version, before.version);
  assert.equal(await confirmation(t.page).count(), 0, 'No replacement confirmation');
  assert.equal(t.mutations.filter(m => m.path === titlePath(f)).length, 1, 'Exactly one action HTTP request');
  const accepted = t.mutations.find(m => m.path === titlePath(f));
  assert.equal(accepted.action, 'purchase');
  assert.equal(accepted.emailPurchaseReminder, expected === 'outstanding_purchase' && reminder);
  assert.equal(accepted.version, before.version);
  assert.equal(current.activity.filter(e => e.eventType === 'status_changed').length -
    before.activity.filter(e => e.eventType === 'status_changed').length, 1, 'Exactly one committed transition');
  if (expected === 'closed') assert.equal(current.closeReason, 'purchased_no_hold');
  const evidence = (await t.verify()).evidence;
  if (expected === 'outstanding_purchase') {
    const reminderRows = evidence.outbox.filter(o => o.key.startsWith('purchase-reminder:' + f.id + ':'));
    assert.equal(reminderRows.length, reminder ? 1 : 0, 'Reminder choice matches the authoritative outbox');
  }
  assert.equal(await t.page.evaluate(() => window.acceptancePurchaseNotices.length), 1,
    'One normalized success presentation; identical queue-refresh reannouncements share the same presentation');
  await checkpoint(t.page, t.directory, t.id + '-complete');
}

const scenarios = [
  { id: 'staff-shell', requirements: ['#401: authenticated staff UI and browser guards'], writes: false,
    run: async t => {
      await t.page.goto(t.origin + '/staff/?scope=2');
      await t.page.locator('#workspace').waitFor({ state: 'visible' });
      await checkpoint(t.page, t.directory, t.id);
    } },
  { id: 'confirmation-desktop-mobile-zoom', requirements: ['#401: stacked dialog, keyboard, Escape, focus, responsive/native 200%'], writes: false,
    run: async t => {
      const { f, before, notes } = await requestDraft(t);
      const revert = t.page.locator('#request-dialog').getByRole('button', { name: 'Revert changes', exact: true });
      for (const mode of ['desktop', 'mobile', 'zoom']) {
        await t.page.setViewportSize(mode === 'mobile' ? { width: 390, height: 844 } : { width: 1280, height: 900 });
        if (mode === 'zoom') await nativeZoom(t.context, t.page, 2);
        await revert.focus();
        await t.page.keyboard.press('Enter');
        await confirmationKeyboard(t.page, 'Revert request changes?', 'Revert changes', 'Keep editing');
        assert.equal(await t.page.locator('#request-dialog[open]').count(), 1, 'Confirmation stacks above details');
        await checkpoint(t.page, t.directory, t.id + '-' + mode);
        await t.page.keyboard.press('Escape');
        await confirmation(t.page).waitFor({ state: 'hidden' });
        assert.equal(await revert.evaluate(n => n === document.activeElement), true, 'Focus returns to opener');
        assert.equal(await notes.inputValue(), 'Synthetic acceptance unsaved draft');
        await unchanged(t.context, t.origin, f, before);
      }
      // Revert is an intentional local discard, not a server mutation.
      await revert.click(); await confirm(t.page, 'Revert changes');
      await t.page.locator('.request-save-state').filter({ hasText: 'All changes saved' }).waitFor();
      assert.equal(await notes.inputValue(), before.notes);
      assert.equal(t.mutations.length, 0);
    } },
  { id: 'purchase-reminder-on', requirements: ['#407: no BIB, reminder on, Cancel, one approval and authoritative Outstanding purchase'], writes: true,
    run: t => purchase(t, 'purchase-reminder-on', 'outstanding_purchase', true) },
  { id: 'purchase-reminder-off', requirements: ['#407: no BIB, reminder off and default preference'], writes: true,
    run: t => purchase(t, 'purchase-reminder-off', 'outstanding_purchase', false) },
  { id: 'purchase-verified-autohold', requirements: ['#407: verified BIB, automatic hold enabled, Pending hold'], writes: true,
    run: t => purchase(t, 'purchase-hold', 'pending_hold', false) },
  { id: 'purchase-verified-no-hold', requirements: ['#407: verified BIB, automatic hold disabled, closure consequence'], writes: true,
    run: t => purchase(t, 'purchase-no-hold', 'closed', false) },
  { id: 'purchase-double-submit', requirements: ['#407: rapid duplicate clicks, one HTTP mutation and committed transition'], writes: true,
    run: t => purchase(t, 'purchase-duplicate', 'outstanding_purchase', false, true) },
  { id: 'reject-template', requirements: ['#401: Reject and close is final approval, template, Cancel, outbox and local delivery'], writes: true,
    run: async t => {
      const f = await fresh(t, 'reject');
      const evidence = (await t.verify()).evidence;
      must(evidence.rejectionTemplates?.length === 1, 'Acceptance rejection template is unavailable.');
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      let review = await choose(t.page, 'Reject');
      await review.getByLabel('Rejection template', { exact: true }).selectOption(evidence.rejectionTemplates[0]);
      await review.getByRole('button', { name: 'Cancel', exact: true }).click();
      await unchanged(t.context, t.origin, f, before);
      assert.equal(t.mutations.length, 0);
      review = await choose(t.page, 'Reject');
      await review.getByLabel('Rejection template', { exact: true }).selectOption(evidence.rejectionTemplates[0]);
      await checkpoint(t.page, t.directory, t.id + '-review');
      const outcome = await submit(t.page, review.getByRole('button', { name: 'Reject and close', exact: true }), f, 200);
      assert.equal(outcome.committed, true);
      assert.equal(outcome.notificationStatus, 'queued');
      const current = await waitStatus(t, f, 'closed');
      assert.equal(current.closeReason, 'rejected');
      assert.equal(await confirmation(t.page).count(), 0);
      assert.equal(t.mutations.length, 1);
      const rows = (await t.verify()).evidence.outbox.filter(o => o.key.startsWith('rejection:' + f.id + ':'));
      assert.equal(rows.length, 1);
      assert.ok(['pending', 'sending', 'sent'].includes(rows[0].status));
      assert.ok(rows[0].deliveryMode === null || rows[0].deliveryMode === 'file');
      assert.match(current.activity.map(e => e.message).join(' '), /Acceptance rejection/);
      await checkpoint(t.page, t.directory, t.id + '-complete');
    } },
  { id: 'dirty-editor-navigation', requirements: ['#401: detail close, status/view switch, Cancel retains draft/route, discard intended draft', '#407: dirty editor blocks purchase'], writes: false,
    run: async t => {
      const { f, before, notes } = await requestDraft(t);
      const route = t.page.url();
      await discard(t, () => t.page.locator('#close-request').click(), false);
      assert.equal(t.page.url(), route);
      assert.equal(await notes.inputValue(), 'Synthetic acceptance unsaved draft');
      const purchase = await actionButton(t.page, 'Purchase');
      if (await purchase.isEnabled()) await purchase.click();
      assert.equal(await t.page.locator('.action-choice').count(), 0, 'Dirty editor cannot purchase');
      await discard(t, () => t.page.locator('#status-tabs [data-status=closed]').evaluate(n => n.click()), false);
      assert.equal(t.page.url(), route);
      assert.equal(await notes.inputValue(), 'Synthetic acceptance unsaved draft');
      await discard(t, () => t.page.locator('[data-view=profile]').evaluate(n => n.click()), false);
      assert.equal(await notes.inputValue(), 'Synthetic acceptance unsaved draft');
      await discard(t, () => t.page.locator('#close-request').click(), true);
      await t.page.locator('#request-dialog[open]').waitFor({ state: 'hidden' });
      await openFixture(t.page, t.origin, f);
      assert.equal(await notes.inputValue(), before.notes);
      await unchanged(t.context, t.origin, f, before);
      assert.equal(t.mutations.length, 0);
    } },
  { id: 'stale-draft-consent', requirements: ['#401: event-free late draft edit revokes pending consent'], writes: false,
    run: async t => {
      const { f, before, notes } = await requestDraft(t, 'stale-consent');
      await t.page.locator('#close-request').click();
      await confirmation(t.page).waitFor();
      const old = await confirmation(t.page).getByRole('button', { name: 'Discard changes', exact: true }).elementHandle();
      await notes.evaluate(n => { n.value = 'Late synthetic draft while confirmation is pending'; });
      await old.evaluate(n => n.click());
      await confirmation(t.page).waitFor({ state: 'hidden' });
      assert.equal(await t.page.locator('#request-dialog[open]').count(), 1);
      assert.equal(await notes.inputValue(), 'Late synthetic draft while confirmation is pending');
      await t.page.locator('#close-request').click();
      await confirmation(t.page).waitFor();
      await old.evaluate(n => n.click()); // Detached prior control must not accept the replacement.
      assert.equal(await confirmation(t.page).count(), 1);
      await confirm(t.page, 'Keep editing');
      await unchanged(t.context, t.origin, f, before);
      assert.equal(t.mutations.length, 0);
    } },
  { id: 'revert-preserves-unrelated-draft', requirements: ['#401: approving editor Revert discards only its draft and retains an unrelated inline draft'], writes: false,
    run: async t => {
      const f = await fresh(t, 'draft');
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      const review = await choose(t.page, 'Purchase');
      const reminder = review.getByRole('checkbox', { name: 'Send purchase reminder', exact: true });
      const selected = !await reminder.isChecked();
      await reminder.setChecked(selected);
      const notes = t.page.locator('#request-dialog').getByLabel('Notes', { exact: true });
      await notes.fill('Synthetic editor draft beside a dirty inline review');
      await t.page.locator('#request-dialog').getByRole('button', { name: 'Revert changes', exact: true }).click();
      await confirmation(t.page).waitFor(); await confirm(t.page, 'Revert changes');
      assert.equal(await notes.inputValue(), before.notes);
      assert.equal(await review.isVisible(), true);
      assert.equal(await reminder.isChecked(), selected);
      await discard(t, () => t.page.locator('#close-request').click(), false);
      assert.equal(await reminder.isChecked(), selected, 'The unrelated draft still owns its departure guard');
      await review.getByRole('button', { name: 'Cancel', exact: true }).click();
      await unchanged(t.context, t.origin, f, before);
      assert.equal(t.mutations.length, 0);
    } },
  { id: 'profile-navigation-and-history', requirements: ['#401: dirty Profile, view switch, Back/Forward, route restoration'], writes: false,
    run: async t => {
      await t.page.goto(t.origin + '/staff/?scope=2');
      await t.page.locator('[data-view=profile]').click();
      const form = t.page.locator('#profile-form');
      await form.waitFor({ state: 'visible' });
      const preference = t.page.locator('#purchase-default');
      const initial = await preference.isChecked();
      await preference.setChecked(!initial);
      const route = t.page.url();
      await discard(t, () => t.page.locator('[data-view=settings]').click(), false);
      assert.equal(t.page.url(), route);
      assert.equal(await preference.isChecked(), !initial);
      await t.page.evaluate(() => history.back());
      await confirmation(t.page).waitFor(); await confirm(t.page, 'Keep editing');
      await t.page.waitForURL(route);
      assert.equal(await preference.isChecked(), !initial);
      await t.page.evaluate(() => history.back());
      await confirmation(t.page).waitFor(); await confirm(t.page, 'Discard changes');
      await form.waitFor({ state: 'hidden' });
      await t.page.evaluate(() => history.forward());
      await form.waitFor({ state: 'visible' });
      assert.equal(await preference.isChecked(), initial);
      assert.equal(t.mutations.length, 0);
      await checkpoint(t.page, t.directory, t.id);
    } },
  { id: 'competing-navigation-consent', requirements: ['#401: newer navigation invalidates prior approval and preserves the current Profile draft'], writes: false,
    run: async t => {
      await t.page.goto(t.origin + '/staff/?stage=profile&scope=2');
      const preference = t.page.locator('#purchase-default');
      await t.page.locator('#profile-form').waitFor({ state: 'visible' });
      const initial = await preference.isChecked();
      await preference.setChecked(!initial);
      await t.page.locator('[data-view=settings]').click();
      await confirmation(t.page).waitFor();
      const old = await confirmation(t.page).getByRole('button', { name: 'Discard changes', exact: true }).elementHandle();
      // Synchronous intent dispatch represents an already-queued input beneath the top layer.
      await t.page.locator('[data-view=queue]').evaluate(n => n.click());
      await old.evaluate(n => n.click());
      await confirmation(t.page).waitFor();
      assert.equal(await preference.isChecked(), !initial, 'New intent does not inherit old consent');
      assert.equal(await t.page.locator('#profile-form').isVisible(), true);
      await confirm(t.page, 'Keep editing');
      assert.equal(t.mutations.length, 0);
    } },
  { id: 'settings-discard', requirements: ['#401: dirty Settings reload/discard, Cancel, no server mutation'], writes: false,
    run: async t => {
      await t.page.goto(t.origin + '/staff/?stage=settings&settingsScope=2#settings-patron');
      await t.page.locator('#settings-version').waitFor({ state: 'attached' });
      await t.page.waitForFunction(() => document.querySelector('#settings-version').value && !document.querySelector('#settings-form').inert);
      await t.page.locator('#settings-nav-patron').click();
      const input = t.page.locator('#patron-success-title'), original = await input.inputValue();
      await input.fill('Synthetic acceptance settings draft');
      await t.page.locator('#settings-discard').click();
      await confirmation(t.page).waitFor();
      await confirm(t.page, 'Cancel');
      assert.equal(await input.inputValue(), 'Synthetic acceptance settings draft');
      await t.page.locator('#settings-discard').click();
      await confirmation(t.page).waitFor();
      await confirmation(t.page).locator('.form-actions button').last().click();
      await confirmation(t.page).waitFor({ state: 'hidden' });
      await t.page.waitForFunction(value => document.querySelector('#patron-success-title').value === value, original);
      const before = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=2');
      assert.equal(before.orgId, '2', 'Authoritative Settings read uses Review Library scope');
      await input.fill('Synthetic draft before inherited reset review');
      await t.page.locator('#settings-reset').click();
      await confirmationKeyboard(t.page, 'Reset inherited overrides?', 'Reset inherited overrides');
      assert.match(await confirmation(t.page).innerText(), /Discard unsaved settings changes and reset/);
      assert.match(await confirmation(t.page).innerText(), /Library-owned custom fields, formats, rejection templates, auto-claim rules and their history are preserved/);
      await checkpoint(t.page, t.directory, t.id + '-reset-review');
      await confirm(t.page, 'Cancel');
      assert.equal(await input.inputValue(), 'Synthetic draft before inherited reset review');
      const after = await json(t.context, t.origin, '/api/asap/staff/settings?orgId=2');
      assert.equal(after.orgId, '2');
      assert.equal(after.version, before.version, 'Reset Cancel preserves authoritative settings');
      assert.equal(t.mutations.length, 0);
      await checkpoint(t.page, t.directory, t.id);
    } },
  { id: 'profile-uncertain-recovery', requirements: ['#401: Profile uncertain-save review, accurate warning, safe Cancel (injected pre-dispatch network failure)'], writes: false,
    run: async t => {
      await t.page.goto(t.origin + '/staff/?stage=profile&scope=2');
      await t.page.locator('#profile-form').waitFor({ state: 'visible' });
      const preference = t.page.locator('#purchase-default'), original = await preference.isChecked();
      await preference.setChecked(!original);
      const handler = route => route.request().method() === 'POST' ? route.abort('failed') : route.fallback();
      await t.page.route('**/api/asap/staff/profile', handler);
      try {
        await t.page.getByRole('button', { name: 'Save profile', exact: true }).click();
        await t.page.locator('#profile-refresh').waitFor({ state: 'visible' });
        await t.page.locator('#profile-refresh').click();
        await confirmation(t.page).waitFor();
        assert.match(await confirmation(t.page).innerText(), /earlier save may have completed/);
        await confirm(t.page, 'Keep draft');
        assert.equal(await preference.isChecked(), !original);
        await t.page.locator('#profile-refresh').click();
        await confirmation(t.page).waitFor(); await confirm(t.page, 'Reload Profile');
        await t.page.locator('#profile-refresh').waitFor({ state: 'hidden' });
        assert.equal(await preference.isChecked(), original);
        assert.equal(t.mutations.length, 0, 'Injected failure never dispatched to server');
      } finally { await t.page.unroute('**/api/asap/staff/profile', handler); }
    } },
  { id: 'beforeunload', requirements: ['#401: genuine browser-managed unload safeguard'], writes: false,
    run: async t => {
      const { f, before, notes } = await requestDraft(t);
      await notes.click(); // A real user gesture is required for Chromium unload protection.
      t.guards.expectUnload = true;
      await t.page.reload({ timeout: 10000 }).catch(() => {});
      t.guards.expectUnload = false;
      assert.equal(t.guards.unloadCount, 1, 'Actual beforeunload prompt appeared and was dismissed');
      assert.equal(await notes.inputValue(), 'Synthetic acceptance unsaved draft');
      await unchanged(t.context, t.origin, f, before);
    } },
  { id: 'close-reopen-and-claim', requirements: ['#401: Close silently, Cancel, Reopen, one approval, claim authority'], writes: true,
    run: async t => {
      const f = await fresh(t, 'close-reopen');
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      await (await actionButton(t.page, 'Close silently')).click();
      await confirmationKeyboard(t.page, 'Close silently', 'Close silently');
      await confirm(t.page, 'Cancel');
      await unchanged(t.context, t.origin, f, before);
      await (await actionButton(t.page, 'Close silently')).click();
      await confirmation(t.page).waitFor();
      const response = t.page.waitForResponse(r => new URL(r.url()).pathname === titlePath(f) && r.request().method() === 'POST');
      await confirm(t.page, 'Close silently'); assert.equal((await response).status(), 200);
      await waitStatus(t, f, 'closed');
      await (await actionButton(t.page, 'Reopen')).click();
      await confirmation(t.page).waitFor();
      const reopenedResponse = t.page.waitForResponse(r => new URL(r.url()).pathname === titlePath(f) && r.request().method() === 'POST');
      await confirm(t.page, 'Reopen request'); assert.equal((await reopenedResponse).status(), 200);
      const reopened = await waitStatus(t, f, 'suggestion');
      const session = await json(t.context, t.origin, '/api/asap/staff/session');
      assert.equal(reopened.claimedByStaffUserId, session.staff.id);
      assert.equal(reopened.claimType, 'manual');
      assert.equal(t.mutations.length, 2);
    } },
  { id: 'stale-purchase-failure', requirements: ['#407: real SQL rowversion conflict, no false success or extra commit'], writes: true,
    run: async t => {
      const f = await fresh(t, 'stale-action');
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      const review = await choose(t.page, 'Purchase');
      // Competing write through the shipped session/antiforgery helper, against real SQL.
      await t.page.evaluate(async ({ path, version }) => {
        const { authorizedJson } = await import('/staff/js/http.js');
        await authorizedJson(path, { method: 'POST', body: { action: 'edit', version, notes: 'Synthetic competing edit' } });
      }, { path: titlePath(f), version: before.version });
      const winner = await detail(t.context, t.origin, f);
      const outcome = await submit(t.page, reviewSubmit(review), f, 409);
      assert.equal(outcome.code, 'stale_version');
      await t.page.locator('#app-status').filter({ hasText: /changed|stale|reload|review/i }).waitFor();
      await unchanged(t.context, t.origin, f, winner);
      assert.equal(t.mutations.filter(m => m.action === 'purchase').length, 1);
      assert.doesNotMatch(await t.page.locator('#app-status').innerText(), /Workflow action completed/);
    } },
  { id: 'unverified-bib-blocked', requirements: ['#407: authoritative BIB gate, invalid action never reports success'], writes: false,
    run: async t => {
      const f = await fresh(t, 'unverified', { verified: false, bibid: 941010 });
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      const button = await actionButton(t.page, 'Purchase');
      if (await button.isEnabled()) {
        const review = await choose(t.page, 'Purchase');
        await reviewSubmit(review).click();
      }
      await unchanged(t.context, t.origin, f, before);
      assert.equal(t.mutations.length, 0);
      assert.doesNotMatch(await t.page.locator('#app-status').innerText(), /Workflow action completed/);
    } },
  { id: 'additional-copy-close-reopen', requirements: ['#401: additional-copy confirmation, safe Cancel and authoritative results'], writes: true,
    run: async t => {
      const f = await fresh(t, 'copy');
      await openFixture(t.page, t.origin, f);
      const before = await detail(t.context, t.origin, f);
      await (await actionButton(t.page, 'Close task')).click();
      await confirmation(t.page).waitFor(); await confirm(t.page, 'Cancel');
      await unchanged(t.context, t.origin, f, before);
      for (const [label, operation, status] of [['Close task', 'close', 'closed'], ['Reopen task', 'reopen', 'open']]) {
        await (await actionButton(t.page, label)).click();
        await confirmation(t.page).waitFor();
        const response = t.page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/' + f.id + '/' + operation) &&
          r.request().method() === 'POST');
        await confirmation(t.page).locator('.form-actions button').last().click();
        assert.equal((await response).status(), 200);
        await t.page.locator('#request-dialog .status-badge').filter({ hasText: status === 'closed' ? 'Closed' : 'Open' }).waitFor();
        assert.equal((await detail(t.context, t.origin, f)).status, status);
      }
      assert.equal(t.mutations.length, 2);
    } }
];

const human = [
  ['human-screen-reader', '#398; #401; #407: real NVDA/VoiceOver dialog naming, live announcements, focus and workflow comprehension'],
  ['human-visual', '#398; #401: desktop/mobile/native zoom, long text, forced colors, visual consistency and clipping'],
];
const uncovered = [
  ['settings-admin-and-reset', '#401: activation, logo changes, saved Polaris diagnostic, roster refresh and inherited reset approval',
    'Live suite covers Settings discard and combined dirty-reset review/Cancel. Existing deterministic Settings/SQL/browser tests cover these branches; reset approval/owned-history preservation and administration need dedicated fixtures or reviewer execution.'],
  ['editor-combined-save', '#401: combined automatic-hold/automatic-claim Save consequences',
    'Existing request-action/controller tests cover the combined warning; no live auto-claim-rule fixture is installed by this suite.'],
  ['hold-delete-email-retirement', '#401: hold/recovery, clear another claim, permanent delete, email-test retirement',
    'Provider-outcome and retirement variants remain covered by existing deterministic tests; not exercised in this live suite.'],
  ['navigation-scope-signout-new-suggestion', '#401: library scope, sign-out and new-suggestion draft departures',
    'The focused live matrix exercises request/Profile/Settings, statuses, views, history and competing intents; remaining combinations are retained deterministic coverage and reviewer checks.'],
  ['unauthorized-session-race', '#407: server authorization and session change during purchase',
    'No live reviewer role/identity is altered. Existing real-SQL authorization and frontend session-race tests retain this contract.']
];
module.exports = { id: 'pr410', requires: ['docker-review-0.3.0', 'sql-server-2022', 'entra-super-admin', 'review-library-2',
  'deterministic-providers', 'chromium-native-zoom'], mutations: true, scenarios, human, uncovered };
