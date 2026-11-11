const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 6; i++) await new Promise(resolve => setImmediate(resolve)); };
const actor = { id: '20', tenantId: 'a', authenticationEmail: 'review@example.org', role: 'super_admin', organizationId: 2 };
const capabilities = { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true,
  allowedActions: ['purchase', 'alreadyOwn', 'silentClose', 'reject'] };
const source = { id: '91', version: 'v1', title: 'Title & café', libraryOrgId: 2, libraryOrgName: 'Review Library',
  status: 'suggestion', barcode: 'ACCEPTANCE41007', nameFirst: 'Synthetic', nameLast: 'Reviewer',
  identifier: 'a/b', bibid: 123, capabilities, activity: [], customFields: {} };
const research = { patronId: 41007, leapBibUrlPattern: 'https://example.org/bib/{{bibid}}',
  leapPatronUrlPattern: 'https://example.org/patron/{{patron-id}}', externalSearchProviders: [
    { label: 'Second', sortOrder: 2, urlTemplate: 'https://example.org/id?q={{identifier}}' },
    { label: 'First', sortOrder: 1, urlTemplate: 'https://example.org/title?q={{title}}' },
    { label: 'Disabled', isEnabled: false, urlTemplate: 'https://example.org/' }
  ] };
const buttons = root => [...root.querySelectorAll('.action-bar > button')];
const button = (root, label) => buttons(root).find(n => n.textContent.trim() === label);
const detailValue = (root, label) => [...root.querySelectorAll('.detail-grid div')]
  .find(row => row.querySelector('dt')?.textContent.trim() === label)?.querySelector('dd');

async function setup(ui, initial = source, options = {}) {
  const { createDetailHost } = await ui.load('detail-host');
  const { createSessionIdentity } = await ui.load('session-identity');
  const { createTitleDetailController } = await ui.load('title-detail');
  const identity = createSessionIdentity(); identity.accept(options.actor || actor);
  const host = createDetailHost({ root: ui.get('#request-dialog') });
  let current = initial, pending = null;
  const calls = [], quick = [], lookups = [], announcements = [], emailOperations = [];
  const controller = createTitleDetailController({ host, sessionIdentity: identity,
    polarisLookup: { open: context => lookups.push(context), close() {}, invalidate() {} },
    copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
    announce: (...args) => announcements.push(args), beforeOpen: () => ({ isCurrent: () => true }), beforeClose: options.beforeClose || (() => true),
    isNavigationCurrent: () => true, getNavigationGeneration: () => 1, getScope: () => 'all',
    onAlign: async () => ({ scope: 'all', commit: () => 1 }), onOpened() {}, onClosed() {},
    getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
    rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {},
    onOpenEmailOperation: options.onOpenEmailOperation || (intent => { emailOperations.push(intent); return Promise.resolve(true); }),
    onNewSuggestion: (...args) => quick.push(args), request: async (path, init = {}) => {
      calls.push({ path, init });
      if (options.send) { const custom = options.send(path, init); if (custom !== undefined) return custom; }
      if (init.method) return new Promise(resolve => { pending = resolve; });
      if (path.includes('research-configuration')) return options.researchConfiguration ?? research;
      if (path.includes('assignment-candidates')) return { candidates: [{ id: '20', displayName: 'Reviewer' }] };
      if (path.includes('rejection-templates')) return { items: [] };
      return path.includes('/title-requests/') ? current : {};
    } });
  await controller.open(current.id); await settle();
  return { controller, host, identity, calls, quick, lookups, announcements, root: ui.get('#request-dialog'),
    emailOperations,
    async show(next) { current = next; await controller.open(next.id); await settle(); },
    async start(action, next = current) { current = next; await controller.open(next.id, null, { action }); await settle(); },
    complete(next) { current = next; pending({ committed: true, request: next }); },
    dispose() { controller.dispose(); host.dispose(); } };
}

(async () => {
  // These row intents enter the same real detail controls as toolbar clicks.
  // Leave confirmation open to prove the opener cannot bypass consent.
  for (const [status, action, allowed, autohold, label] of [
    ['outstanding_purchase', 'catalogFound', ['catalogFound'], true, 'Ready for hold'],
    ['outstanding_purchase', 'catalogFound', ['catalogFound'], false, 'Close without hold'],
    ['pending_hold', 'place-hold', [], true, 'Place hold'],
    ['hold_placed', 'close', ['close'], true, 'Close request'],
    ['closed', 'reopen', ['reopen'], true, 'Reopen request'],
    ['suggestion', 'alreadyOwn', ['alreadyOwn'], true, 'Already own'],
    ['suggestion', 'silentClose', ['silentClose'], true, 'Close silently']
  ]) await fixture(async ui => {
    require('./helpers/confirmation.cjs').answerConfirmations(ui.dom.window, () => null);
    const request = { ...source, status, autohold, bibidStaffVerified: true,
      capabilities: { ...capabilities, allowedActions: allowed, canPlaceHold: action === 'place-hold' } };
    const t = await setup(ui, request);
    try {
      const mutations = () => t.calls.filter(call => call.init.method);
      await t.start(action);
      const confirmation = t.root.querySelector('.action-choice');
      assert.ok(confirmation, action + ' opens its inline review');
      assert.equal(ui.get('#request-action-confirmation-title').textContent, label);
      assert.equal(mutations().length, 0, 'Opening a row workflow never mutates before approval');
      confirmation.querySelector('.form-actions button:last-child').click(); await settle();
      assert.equal(mutations().length, 0, 'Cancel leaves the request unchanged');
      assert.equal(document.activeElement, button(t.root, action === 'reopen' ? 'Reopen' : label));
      await t.start(action);
      t.root.querySelector('.action-choice button[type="submit"]').click(); await settle();
      assert.equal(mutations().length, 1, 'Approval dispatches exactly once');
      assert.equal(mutations()[0].init.body.version, 'v1');
      assert.equal(mutations()[0].path.endsWith(action === 'place-hold' ? '/place-hold' : '/action'), true);
      if (action !== 'place-hold') assert.equal(mutations()[0].init.body.action, action);
    } finally { t.dispose(); }
  });

  for (const [status, action, extra = {}] of [
    ['suggestion', 'purchase'], ['suggestion', 'reject'], ['suggestion', 'alreadyOwn'],
    ['suggestion', 'silentClose'], ['outstanding_purchase', 'catalogFound'],
    ['pending_hold', 'place-hold', { canPlaceHold: false }],
    ['pending_hold', 'place-hold', { canPlaceHold: true, canChangeWorkflowState: false, blockingReason: 'hold_operation_incomplete' }],
    ['pending_hold', 'place-hold', { canPlaceHold: true, canChangeWorkflowState: false, blockingReason: 'pickup_reconciliation_required' }],
    ['hold_placed', 'close'], ['closed', 'reopen', { blockingReason: 'hold_history_retained' }]
  ]) await fixture(async ui => {
    const t = await setup(ui);
    try {
      await t.start(action, { ...source, status, capabilities: { ...capabilities, allowedActions: [], ...extra } });
      assert.equal(t.calls.some(call => call.init.method), false, 'Capability revoked between row and detail cannot mutate');
      assert.equal(Boolean(ui.get('#confirmation-dialog')?.open), false);
      assert.equal(t.root.querySelector('.action-choice'), null);
      assert.ok(t.announcements.some(([message]) => /no longer available/.test(message)), action + ' explains invalidation');
    } finally { t.dispose(); }
  });

  for (const action of ['purchase', 'reject', 'assign', 'claim']) await fixture(async ui => {
    const t = await setup(ui);
    try {
      await t.start(action);
      if (action === 'claim') {
        const panel = t.root.querySelector('.action-choice');
        assert.ok(panel, 'claim opens its inline confirmation');
        assert.equal(t.calls.filter(c => c.init.method).length, 0);
        panel.querySelector('button[type="submit"]').click(); await settle();
        assert.equal(t.calls.filter(c => c.init.method).length, 1);
        assert.match(t.calls.find(c => c.init.method).path, /\/claim$/);
      } else {
        const panel = t.root.querySelector(action === 'assign' ? '.inline-form' : '.action-choice');
        assert.ok(panel, action + ' opens its choice form');
        assert.equal(t.calls.some(c => c.init.method), false);
        [...panel.querySelectorAll('button')].find(b => b.textContent.trim() === 'Cancel').click(); await settle();
        assert.equal(panel.isConnected, false);
        assert.equal(t.calls.some(c => c.init.method), false);
      }
    } finally { t.dispose(); }
  });

  for (const [status, action, allowed] of [
    ['suggestion', 'purchase', ['purchase']], ['suggestion', 'reject', ['reject']],
    ['suggestion', 'alreadyOwn', ['alreadyOwn']], ['suggestion', 'silentClose', ['silentClose']],
    ['outstanding_purchase', 'catalogFound', ['catalogFound']], ['pending_hold', 'place-hold', []],
    ['hold_placed', 'close', ['close']], ['closed', 'reopen', ['reopen']],
    ['suggestion', 'claim', []], ['suggestion', 'assign', []]
  ]) await fixture(async ui => {
    const request = { ...source, status, bibidStaffVerified: true, autohold: true,
      capabilities: { ...capabilities, allowedActions: allowed, canPlaceHold: true } };
    const t = await setup(ui, request);
    try {
      // A late queued row intent cannot start against a changed actor/library.
      for (const replacement of [{ ...actor, role: 'staff', organizationId: 3 }, { ...actor, role: 'retired' }]) {
        t.identity.accept(replacement); await t.start(action);
        assert.equal(t.calls.some(c => c.init.method), false, action + ' honors changed actor scope/role');
        assert.equal(t.root.querySelector('.action-choice, .inline-form'), null);
        assert.equal(Boolean(ui.get('#confirmation-dialog')?.open), false);
      }
    } finally { t.dispose(); }
  });

  for (const [status, action] of [['outstanding_purchase', 'catalogFound'], ['pending_hold', 'place-hold'],
    ['hold_placed', 'close'], ['closed', 'reopen']]) await fixture(async ui => {
    const t = await setup(ui, { ...source, status, autohold: true, bibidStaffVerified: true,
      capabilities: { ...capabilities, canPlaceHold: true, allowedActions: [action] } });
    try {
      await t.start(action);
      const review = t.root.querySelector('.action-choice');
      assert.ok(review, action + ' opens its inline review');
      t.identity.accept({ ...actor, role: 'staff', organizationId: 3 });
      review.querySelector('button[type="submit"]').click(); await settle();
      assert.equal(t.calls.some(c => c.init.method), false, action + ' rechecks ownership after consent');
    } finally { t.dispose(); }
  });

  for (const [action, next] of [['claim', { ...source, claimedByStaffUserId: '99' }],
    ['assign', { ...source, status: 'closed', capabilities: { allowedActions: [] } }]]) await fixture(async ui => {
    const t = await setup(ui);
    try {
      await t.start(action, next);
      assert.equal(t.calls.some(c => c.init.method), false);
      assert.equal(t.root.querySelector('.inline-form'), null);
      assert.ok(t.announcements.some(([message]) => /no longer available/.test(message)));
    } finally { t.dispose(); }
  });

  for (const [request, staff, expected, disabled = []] of [
    [{ ...source, claimedByStaffUserId: '20' }, actor, ['Unclaim', 'Purchase', 'Assign', 'Already own', 'Close silently', 'Reject']],
    [{ ...source, claimedByStaffUserId: '21' }, actor, ['Clear claim', 'Purchase', 'Assign', 'Already own', 'Close silently', 'Reject']],
    [{ ...source, claimedByStaffUserId: '21' }, { ...actor, role: 'staff' }, ['Purchase', 'Assign', 'Already own', 'Close silently', 'Reject']],
    [{ ...source, status: 'outstanding_purchase', autohold: true, capabilities: { ...capabilities, allowedActions: ['catalogFound'] } }, actor, ['Claim', 'Assign', 'Ready for hold']],
    [{ ...source, status: 'outstanding_purchase', autohold: false }, actor, ['Claim', 'Assign', 'Close without hold'], ['Close without hold']],
    [{ ...source, status: 'pending_hold', capabilities: { ...capabilities, canPlaceHold: true, allowedActions: ['closeDuplicate'] }, workflowTags: ['Hold exists (same patron)'] }, actor,
      ['Claim', 'Assign', 'Additional copy', 'Pickup', 'Place hold', 'Close duplicate']],
    [{ ...source, status: 'pending_hold', bibid: null, capabilities: { ...capabilities, canChangeWorkflowState: false } }, actor,
      ['Claim', 'Assign', 'Additional copy', 'Pickup'], ['Additional copy', 'Pickup']],
    [{ ...source, status: 'hold_placed', capabilities: { ...capabilities, allowedActions: ['close'] } }, actor, ['Claim', 'Assign', 'Additional copy', 'Close request']],
    [{ ...source, status: 'closed', capabilities: { ...capabilities, allowedActions: ['reopen'] } }, actor, ['Reopen', 'Permanently delete request']],
    [{ ...source, status: 'closed', capabilities: { ...capabilities, allowedActions: [] } }, { ...actor, role: 'staff' }, []],
    [{ ...source, capabilities: { ...capabilities, canRetryIdentifierCheck: true } }, actor, ['Claim', 'Purchase', 'Assign', 'Already own', 'Close silently', 'Retry identifier check', 'Reject']]
  ]) await fixture(async ui => {
    const t = await setup(ui, request, { actor: staff });
    try {
      assert.deepEqual(buttons(t.root).map(n => n.textContent.trim()), expected);
      assert.deepEqual(buttons(t.root).filter(n => n.disabled).map(n => n.textContent.trim()), disabled);
      assert.equal(t.root.querySelector('.request-more'), null);
    } finally { t.dispose(); }
  });

  const missingBib = { ...source, status: 'outstanding_purchase', autohold: true, bibid: null, bibidStaffVerified: false,
    capabilities: { ...capabilities, allowedActions: ['catalogFound'] } };
  await fixture(async ui => {
    const t = await setup(ui, missingBib);
    try {
      const notice = t.root.querySelector('.request-bib-prerequisite');
      assert.ok(notice);
      assert.equal(notice.querySelector('h3').textContent, 'Waiting for a BIB ID');
      assert.equal(notice.querySelector('p').textContent,
        'Add and save a Polaris BIB ID before moving this request to Ready for hold.');
      assert.equal(notice.querySelector('.request-bib-prerequisite-icon i').getAttribute('aria-hidden'), 'true');
      assert.equal(notice.previousElementSibling.className, 'detail-meta');
      assert.equal(notice.nextElementSibling.className, 'action-bar');
      assert.equal(button(t.root, 'Ready for hold').disabled, true);
      assert.deepEqual(buttons(t.root).map(item => item.textContent.trim()), ['Claim', 'Assign', 'Ready for hold']);
      assert.equal(t.root.querySelector('.request-bib-prerequisite-link').textContent, 'Go to BIB ID');
      const bib = t.root.querySelector('#request-91-bib-id');
      const describedBy = bib.getAttribute('aria-describedby').split(' ');
      assert.equal(document.getElementById(describedBy[0]).textContent, 'Required for Ready for hold');
      assert.equal(describedBy.length, 1, 'The field help is associated without duplicating the full notice description.');

      const title = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      title.value = 'Retained request title draft';
      title.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      t.root.querySelector('[role="tab"][aria-controls$="activity"]').click();
      const link = notice.querySelector('.request-bib-prerequisite-link');
      assert.equal(t.root.querySelector('[role="tabpanel"][id$="activity"]').hidden, false);
      link.focus(); link.click(); await settle();
      assert.equal(t.root.querySelector('[role="tab"][aria-controls$="details"]').getAttribute('aria-selected'), 'true');
      assert.equal(document.activeElement, bib, 'The link returns to and focuses the current BIB input.');
      assert.equal(title.value, 'Retained request title draft', 'Switching tabs and focusing the BIB preserves drafts.');
      assert.equal(t.calls.some(call => call.init.method), false, 'The link does not mutate the request.');
      assert.equal(t.lookups.length, 0, 'The link does not open or call Polaris.');

      bib.value = '9001';
      bib.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), notice);
      assert.equal(button(t.root, 'Ready for hold').disabled, true, 'Typing alone cannot satisfy the saved-BIB prerequisite.');
      [...t.root.querySelectorAll('button')].find(item => item.textContent.trim() === 'Search Polaris catalog').click();
      assert.equal(t.lookups.length, 1);
      t.lookups[0].apply({ bibId: 9001, title: 'Verified title' }, { bibId: 9001, holdingsSummary: null });
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), notice);
      assert.equal(button(t.root, 'Ready for hold').disabled, true, 'Selecting a BIB without saving cannot enable Ready for hold.');
      assert.equal(t.calls.some(call => call.init.method), false);

      const revert = [...t.root.querySelectorAll('.request-save-footer button')]
        .find(item => item.textContent.includes('Revert changes'));
      revert.click(); await settle();
      const replacementBib = t.root.querySelector('#request-91-bib-id');
      assert.notEqual(replacementBib, bib, 'Revert replaces the BIB control.');
      link.click(); await settle();
      assert.equal(document.activeElement, replacementBib, 'The link resolves the current control after Revert.');
      assert.equal(replacementBib.disabled, false, 'Navigation does not change or bypass the field lock.');
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });

  for (const [nextStatus, allowedActions] of [['pending_hold', []], ['outstanding_purchase', []]]) await fixture(async ui => {
    const saved = { ...missingBib, version: 'v2', status: nextStatus, bibid: 9001, bibidStaffVerified: true,
      capabilities: { ...capabilities, canChangeBib: nextStatus === 'outstanding_purchase', allowedActions } };
    const t = await setup(ui, missingBib, { send: (path, init) => init.method
      ? { committed: true, request: saved, finalStatus: saved.status }
      : undefined });
    try {
      [...t.root.querySelectorAll('button')].find(item => item.textContent.trim() === 'Search Polaris catalog').click();
      t.lookups[0].apply({ bibId: 9001, title: 'Verified title' }, { bibId: 9001 });
      t.root.querySelector('.edit-form').dispatchEvent(new ui.dom.window.Event('submit', { bubbles: true, cancelable: true })); await settle();
      assert.equal(t.calls.filter(call => call.init.method).length, 1);
      assert.equal(t.calls.find(call => call.init.method).init.body.staffSelectedBibId, 9001);
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), null, 'A refreshed saved BIB removes the notice.');
      assert.equal(t.root.querySelector('#request-91-bib-required'), null, 'A saved BIB removes the missing-BIB field help.');
      assert.equal(ui.get('#request-dialog-status').textContent, nextStatus === 'pending_hold' ? 'Pending hold' : 'Outstanding purchase',
        'The refreshed server status is rendered without inferring a transition from AutoHold.');
      const ready = button(t.root, 'Ready for hold');
      if (nextStatus === 'outstanding_purchase') {
        assert.ok(ready);
        assert.equal(ready.disabled, true, 'The refreshed server capabilities still govern action availability.');
      } else assert.equal(ready, undefined, 'A server status transition is not replaced with a client-inferred action.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, missingBib);
    try {
      const bib = t.root.querySelector('#request-91-bib-id');
      const title = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      bib.value = 'not-a-bib';
      bib.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      title.value = 'Changed title';
      title.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      t.root.querySelector('.edit-form').dispatchEvent(new ui.dom.window.Event('submit', { bubbles: true, cancelable: true })); await settle();
      assert.equal(t.calls.some(call => call.init.method), false, 'Invalid BIB validation does not dispatch a save.');
      assert.equal(bib.value, 'not-a-bib');
      assert.equal(document.activeElement, bib);
      assert.ok(t.announcements.some(([message]) => /positive Polaris BIB ID/.test(message)));
      assert.ok(t.root.querySelector('.request-bib-prerequisite'));
      assert.equal(button(t.root, 'Ready for hold').disabled, true);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, missingBib, { send: (path, init) => init.method
      ? Promise.reject(Object.assign(new Error('BIB validation failed.'), { status: 400, response: { code: 'bib_not_found' } }))
      : undefined });
    try {
      const title = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      [...t.root.querySelectorAll('button')].find(item => item.textContent.trim() === 'Search Polaris catalog').click();
      t.lookups[0].apply({ bibId: 9001, title: 'Verified title' }, { bibId: 9001 });
      title.value = 'Keep this draft after save failure';
      title.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      t.root.querySelector('.request-save-footer button[type="submit"]').click(); await settle();
      assert.equal(t.calls.filter(call => call.init.method).length, 1);
      assert.equal(title.value, 'Keep this draft after save failure');
      assert.equal(t.root.querySelector('#request-91-bib-id').value, '9001');
      assert.ok(t.root.querySelector('.request-bib-prerequisite'));
      assert.equal(button(t.root, 'Ready for hold').disabled, true);
      assert.equal(t.root.querySelector('.request-save-state').dataset.state, 'error');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let committed = false;
    const t = await setup(ui, missingBib, { send: (path, init) => {
      if (init.method) {
        committed = true;
        return { committed: true, request: null, refreshUnavailable: true, finalStatus: 'pending_hold' };
      }
      if (committed && path.includes('/title-requests/91')) {
        return Promise.reject(Object.assign(new Error('Current details unavailable.'), { status: 503 }));
      }
      return undefined;
    } });
    try {
      [...t.root.querySelectorAll('button')].find(item => item.textContent.trim() === 'Search Polaris catalog').click();
      t.lookups[0].apply({ bibId: 9001, title: 'Verified title' }, { bibId: 9001 });
      t.root.querySelector('.request-save-footer button[type="submit"]').click(); await settle();
      assert.equal(t.calls.filter(call => call.init.method).length, 1);
      assert.match(t.root.textContent, /The action committed\. Reload this request to review current details\./,
        'A failed refresh reports the confirmed commit and directs staff to reload.');
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), null,
        'Without refreshed authority, the UI does not present a stale missing-BIB notice.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    require('./helpers/confirmation.cjs').answerConfirmations(ui.dom.window, () => false);
    const t = await setup(ui, missingBib);
    try {
      [...t.root.querySelectorAll('button')].find(item => item.textContent.trim() === 'Search Polaris catalog').click();
      t.lookups[0].apply({ bibId: 9001, title: 'Verified title' }, { bibId: 9001 });
      const autoHold = [...t.root.querySelectorAll('.edit-form input[type="checkbox"]')][0];
      autoHold.checked = false;
      autoHold.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
      t.root.querySelector('.request-save-footer button[type="submit"]').click(); await settle();
      assert.equal(t.calls.some(call => call.init.method), false, 'Canceling the no-hold consequence does not save.');
      assert.equal(autoHold.checked, false, 'The canceled save retains the editor draft.');
      assert.equal(t.root.querySelector('.request-bib-prerequisite') !== null, true,
        'The notice continues to reflect the saved AutoHold state after a canceled edit.');
      assert.equal(button(t.root, 'Ready for hold').disabled, true);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const noHold = { ...missingBib, autohold: false };
    const t = await setup(ui, noHold);
    try {
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), null);
      assert.equal(t.root.querySelector('#request-91-bib-required'), null);
      assert.equal(button(t.root, 'Close without hold').disabled, false);
      const autoHold = [...t.root.querySelectorAll('.edit-form input[type="checkbox"]')][0];
      autoHold.checked = true;
      autoHold.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), null,
        'Unsaved AutoHold changes do not introduce guidance for a different saved workflow path.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const blocked = { ...missingBib, claimedByStaffUserId: '99', claimedByDisplayName: 'Other staff',
      capabilities: { ...capabilities, canChangeBib: false, canEditIdentifier: false,
        blockingReason: 'hold_operation_incomplete', allowedActions: ['catalogFound'] } };
    const t = await setup(ui, blocked, { actor: { ...actor, role: 'staff' } });
    try {
      const bib = t.root.querySelector('#request-91-bib-id');
      assert.equal(bib.disabled, true);
      assert.match(t.root.querySelector('.blocked-callout').textContent, /recovery/i);
      const helpIds = bib.getAttribute('aria-describedby').split(' ');
      assert.equal(helpIds.length, 2);
      assert.equal(document.getElementById(helpIds[0]).textContent, 'Locked');
      assert.equal(document.getElementById(helpIds[1]).textContent, 'Required for Ready for hold');
      const notice = t.root.querySelector('.request-bib-prerequisite');
      notice.querySelector('a').click(); await settle();
      assert.equal(bib.disabled, true, 'Navigation never enables an identifier-locked field.');
      assert.equal(document.activeElement, document.getElementById(helpIds[0]));
      assert.deepEqual(buttons(t.root).map(item => item.textContent.trim()), ['Assign', 'Ready for hold']);
      assert.equal(button(t.root, 'Ready for hold').disabled, true);
      assert.match(t.root.querySelector('.blocked-callout').textContent, /Workflow-changing edits are blocked/);
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, missingBib);
    try {
      button(t.root, 'Claim').click();
      const review = t.root.querySelector('.action-choice');
      review.querySelector('button[type="submit"]').click(); await settle();
      const bib = t.root.querySelector('#request-91-bib-id');
      assert.equal(bib.disabled, true, 'A pending request mutation continues to disable the BIB control.');
      t.root.querySelector('.request-bib-prerequisite-link').click(); await settle();
      assert.equal(bib.disabled, true, 'Following the notice cannot bypass an in-flight mutation.');
      assert.ok(t.root.querySelector('.request-bib-prerequisite'));
      assert.equal(t.calls.filter(call => call.init.method).length, 1, 'The link does not add a second pending mutation.');
      assert.equal(t.lookups.length, 0);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, missingBib);
    try {
      const oldLink = t.root.querySelector('.request-bib-prerequisite-link');
      t.root.querySelector('[role="tab"][aria-controls$="activity"]').click();
      t.identity.accept({ ...actor, role: 'retired' });
      oldLink.click(); await settle();
      assert.equal(t.root.querySelector('[role="tabpanel"][id$="activity"]').hidden, false,
        'A notice from a retired session cannot change tabs.');
      assert.notEqual(document.activeElement, t.root.querySelector('#request-91-bib-id'));
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, missingBib);
    try {
      const oldLink = t.root.querySelector('.request-bib-prerequisite-link');
      await t.show({ ...source, id: '92', title: 'Different request' });
      const newTitle = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      newTitle.focus();
      oldLink.click(); await settle();
      assert.equal(document.activeElement, newTitle, 'A stale request link cannot focus another request\'s field.');
      assert.equal(t.root.querySelector('.request-bib-prerequisite'), null);
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui);
    try {
      assert.deepEqual(buttons(t.root).map(n => n.textContent.trim()), ['Claim', 'Purchase', 'Assign', 'Already own', 'Close silently', 'Reject']);
      assert.equal(t.root.querySelector('.request-more'), null);
      assert.equal(ui.get('#request-dialog-title').textContent, source.title);
      assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
      assert.equal(t.root.getAttribute('aria-describedby'), 'request-dialog-status');
      assert.equal(t.root.querySelector('.detail-meta .status-badge'), null);
      for (const label of ['Purchase', 'Reject', 'Assign']) {
        const opener = button(t.root, label); opener.focus(); opener.click(); await settle();
        const panel = t.root.querySelector(label === 'Assign' ? '.inline-form' : '.action-choice');
        assert.ok(panel, label + ' retains its review/choice form');
        assert.equal(t.root.querySelector('.action-bar').hidden, true, label + ' hides competing toolbar controls');
        assert.equal(panel.contains(document.activeElement), true, label + ' focuses its inline panel');
        assert.equal(ui.get('#request-dialog-title').textContent, source.title);
        assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
        assert.equal(ui.get('#close-request').disabled, false);
        if (label === 'Purchase') assert.equal(panel.querySelector('button[type=submit]').textContent, 'Confirm purchase');
        const heldEnter = new ui.dom.window.KeyboardEvent('keydown', { key: 'Enter', repeat: true, bubbles: true, cancelable: true });
        document.activeElement.dispatchEvent(heldEnter);
        assert.equal(heldEnter.defaultPrevented, true, 'Opening Enter repeats cannot approve the focused inline panel');
        document.activeElement.dispatchEvent(new ui.dom.window.KeyboardEvent('keyup', { key: 'Enter', bubbles: true }));
        const freshEnter = new ui.dom.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
        document.activeElement.dispatchEvent(freshEnter);
        assert.equal(freshEnter.defaultPrevented, false, 'A deliberate fresh Enter keeps normal form behavior');
        opener.click(); await settle();
        assert.equal(t.root.querySelectorAll('.action-choice, .inline-form').length, 1, 'Repeated activation retains one panel');
        const escape = new ui.dom.window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
        t.root.dispatchEvent(escape);
        assert.equal(escape.defaultPrevented, true, 'Inline cancellation owns native Escape before dialog close');
        t.root.dispatchEvent(new ui.dom.window.KeyboardEvent('keydown', { key: 'Escape', repeat: true, bubbles: true, cancelable: true }));
        t.root.dispatchEvent(new ui.dom.window.KeyboardEvent('keyup', { key: 'Escape', bubbles: true, cancelable: true }));
        await settle();
        assert.equal(panel.isConnected, false);
        assert.equal(t.root.open, true, 'Held Escape cannot also close the request');
        assert.equal(t.root.querySelector('.action-bar').hidden, false);
        assert.equal(document.activeElement, opener, label + ' cancellation returns to its own button');
        assert.equal(t.calls.some(c => c.init.method), false);
      }
      const patron = [...t.root.querySelectorAll('dt')].find(n => n.textContent === 'Patron').nextElementSibling;
      const plus = patron.querySelector('.grid-new-suggestion');
      assert.ok(plus); assert.equal(plus.previousElementSibling.textContent, 'Synthetic Reviewer');
      assert.equal(t.root.querySelectorAll('.grid-new-suggestion').length, 1);
      assert.equal(detailValue(t.root, 'Patron').querySelector('a').href, 'https://example.org/patron/41007');
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a').href, 'https://example.org/patron/41007');
      for (const link of [detailValue(t.root, 'Patron').querySelector('a'), detailValue(t.root, 'Barcode').querySelector('a')]) {
        assert.equal(link.target, '_blank'); assert.equal(link.rel, 'noopener noreferrer');
      }
      assert.equal(t.root.querySelector('.polaris-edit-tools').textContent.includes('Open patron in LEAP'), false);
      plus.click(); assert.deepEqual(t.quick[0], [source.barcode, plus, source.id]);
      const stalePlus = plus;
      await t.show({ ...source, id: '92', barcode: ' ' });
      stalePlus.click(); assert.equal(t.quick.length, 1, 'retired quick-create callback is fenced');
      assert.equal(t.root.querySelector('.grid-new-suggestion'), null);
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const created = '2026-10-23T12:00:00.1234567Z';
    const activity = Object.freeze([
      Object.freeze({ id: '9007199254740993', source: 'email_event', eventType: 'email', created,
        email: Object.freeze({ operationReference: '9223372036854775807', messageType: 'Patron confirmation',
          outcome: 'suppressed', outcomeLabel: 'Suppressed; no email sent', reason: '<img src=x onerror=alert(1)> ' + 'R'.repeat(300),
          intendedRecipient: '<svg onload=alert(2)> patron@example.org', deliveryMode: 'unknown',
          isRecordedSummary: false, timestampKind: 'suppressed', operationsScope: '2' }) }),
      Object.freeze({ id: '9007199254740992', source: 'email_event', created: '2026-10-23T12:00:00.1234567Z',
        email: Object.freeze({ operationReference: '9007199254740993', messageType: 'Staff notification',
          outcome: 'provider_accepted', outcomeLabel: 'Provider accepted; delivery unconfirmed',
          reason: null, intendedRecipient: 'staff@example.org', deliveryMode: 'postmark',
          isRecordedSummary: false, timestampKind: 'sent', operationsScope: '2' }) }),
      Object.freeze({ id: '9007199254740993', source: 'email_summary', created,
        email: Object.freeze({ operationReference: '9223372036854775807', messageType: 'Patron confirmation',
          outcome: 'recorded_sent', outcomeLabel: 'Current recorded summary: sent status; delivery unconfirmed',
          reason: null, intendedRecipient: null, deliveryMode: 'file', isRecordedSummary: true,
          timestampKind: 'created', operationsScope: '2' }) }),
      Object.freeze({ id: '9007199254740993', source: 'request_event', created, eventType: 'created',
        actorType: 'system', message: 'Saved workflow event' }),
      Object.freeze({ id: '1', source: 'email_event', created: '2026-10-23T12:00:00.1234568Z',
        email: Object.freeze({ operationReference: '01', messageType: null, outcome: 'captured', outcomeLabel: null,
          intendedRecipient: null, deliveryMode: 'future-mode', timestampKind: 'future-time', isRecordedSummary: false,
          operationsScope: '2' }) }),
      Object.freeze({ id: 'bad', source: 'email_event', created: 'not a date',
        email: Object.freeze({ operationReference: '9223372036854775808', messageType: null, outcome: 'suppressed',
          outcomeLabel: 'Suppressed; no email sent', reason: null, intendedRecipient: null, deliveryMode: 'unknown',
          isRecordedSummary: false, timestampKind: 'future-time', operationsScope: '2' }) }),
      Object.freeze({ id: '5', source: 'email_summary', created: 'not a date', email: Object.freeze({}) }),
      Object.freeze({ id: '2', source: 'request_event', created: 'not a date', eventType: 'unknown', message: 'Missing date' })
    ]);
    const t = await setup(ui, { ...source, notes: 'Saved note is not system history.', activity });
    try {
      const rows = [...t.root.querySelectorAll('.request-activity li')];
      assert.deepEqual(rows.map(row => row.dataset.activityId), [
        'email_event:1', 'email_event:9007199254740993', 'email_event:9007199254740992',
        'email_summary:9007199254740993', 'request_event:9007199254740993',
        'email_event:bad', 'email_summary:5', 'request_event:2'
      ], 'SQL fractional ticks, ordinal source, then exact BigInt IDs define a stable reverse chronology.');
      assert.equal(rows[1].querySelector('code').textContent, '9223372036854775807');
      assert.equal(rows[1].querySelectorAll('.request-email-reference-link').length, 1);
      assert.equal(rows[1].querySelector('.request-email-fields').textContent.includes('<svg onload=alert(2)> patron@example.org'), true);
      assert.equal(rows[1].querySelectorAll('img, svg').length, 0, 'Malicious API strings remain text.');
      assert.equal(rows[1].textContent.includes('R'.repeat(300)), true, 'Long recorded reasons remain available and wrap in the rendered content.');
      assert.equal(rows[1].textContent.includes('<img src=x onerror=alert(1)>'), true, 'A reason is rendered as safe text.');
      assert.match(rows[0].textContent, /Delivery mode unknown/);
      assert.match(rows[0].textContent, /Recorded time \(meaning unavailable\)/);
      assert.match(rows[2].textContent, /Provider accepted; delivery unconfirmed/);
      assert.doesNotMatch(rows[2].textContent, /Delivered/);
      assert.match(rows[3].textContent, /File capture; no live email sent/);
      assert.match(rows[3].textContent, /Recorded summary; earlier email activity may be incomplete/);
      assert.match(rows[4].textContent, /Saved workflow event/);
      assert.match(rows[5].textContent, /Email type not recorded/);
      assert.match(rows[5].textContent, /Recorded time \(meaning unavailable\): Time not recorded/);
      assert.match(rows[5].textContent, /Delivery mode unknown/);
      assert.equal(rows[5].querySelector('.request-email-reference-link'), null, 'Out-of-range canonical IDs cannot become an action.');
      assert.equal(rows[6].querySelector('.request-email-reference-link'), null, 'Missing values never create a reference action.');
      const notes = t.root.querySelector('.edit-form textarea[maxlength="10000"]');
      assert.equal(notes.value, 'Saved note is not system history.');
      assert.equal(activity.length, 8, 'The frozen Activity array is retained.');
      assert.equal(activity[0].id, '9007199254740993', 'Rendering leaves frozen DTO input unchanged.');
      rows[1].querySelector('.request-email-reference-link').click(); await settle();
      assert.equal(t.emailOperations.length, 1);
      assert.equal(t.emailOperations[0].operationReference, '9223372036854775807');
      assert.equal(t.emailOperations[0].operationsScope, '2');
      assert.equal(typeof t.emailOperations[0].isCurrent, 'function');
      const oldButton = rows[1].querySelector('.request-email-reference-link');
      await t.show({ ...source, id: '91', notes: 'Saved note is not system history.', activity });
      oldButton.click(); await settle();
      assert.equal(t.emailOperations.length, 1, 'A detached previous detail reference cannot invoke navigation.');
    } finally { t.dispose(); }
  });
  for (const [role, organizationId, operationsScope, expectedLinks] of [
    ['admin', 2, '2', 1], ['admin', 2, '3', 0], ['staff', 2, '2', 0], ['super_admin', 1, '2', 1]
  ]) await fixture(async ui => {
    const item = { id: '9223372036854775807', source: 'email_event', created: '2026-10-23T12:00:00Z',
      email: { operationReference: '9223372036854775807', messageType: 'Patron confirmation', outcomeLabel: 'Suppressed',
        operationsScope, intendedRecipient: null, deliveryMode: 'unknown', timestampKind: 'suppressed' } };
    const t = await setup(ui, { ...source, activity: [item] }, { actor: { ...actor, role, organizationId } });
    try {
      assert.equal(t.root.querySelectorAll('.request-email-reference-link').length, expectedLinks,
        'Only authorized administrators receive a canonical exact-reference action.');
      if (!expectedLinks) assert.equal(t.root.querySelector('.request-email-reference').textContent.includes('9223372036854775807'), true,
        'Unauthorized viewers keep the plain exact reference.');
    } finally { t.dispose(); }
  });

  for (const [status, reason, message, warning] of [
    ['closed', 'identifier_locked_by_stage', /closed.*Identifier and BIB ID.*Reopen/, false],
    ['closed', 'hold_history_retained', /previous hold placement.*can't be reopened/, false],
    ['hold_placed', 'identifier_locked_by_stage', /reached the hold stage/, false],
    ['suggestion', 'identifier_locked_by_stage', /previous hold placement prevents changes/, false],
    ['pending_hold', 'hold_operation_incomplete', /hold placement needs recovery/, true],
    ['pending_hold', 'pickup_reconciliation_required', /live preference.*will not be repeated/, true]
  ]) await fixture(async ui => {
    const t = await setup(ui, { ...source, status, closeReason: status === 'closed' ? 'rejected' : null,
      capabilities: { ...capabilities, canChangeBib: false, canEditIdentifier: false, blockingReason: reason, allowedActions: [] } });
    try {
      const help = t.root.querySelector('.identifier-lock-help, .blocked-callout');
      assert.match(help.textContent, message); assert.equal(help.classList.contains('blocked-callout'), warning);
      if (reason === 'identifier_locked_by_stage' && status === 'closed') assert.doesNotMatch(help.textContent, /previous|placement history/);
      const disabled = [...t.root.querySelectorAll('.edit-form input:disabled')];
      assert.equal(disabled.length, 2);
      for (const input of disabled) assert.equal(document.getElementById(input.getAttribute('aria-describedby')).textContent, 'Locked');
      assert.equal(t.root.querySelector('.edit-form input').disabled, false);
      assert.equal(ui.get('#request-dialog-status').textContent, status === 'closed' ? 'Closed · Rejected' :
        status === 'hold_placed' ? 'Hold placed' : status === 'pending_hold' ? 'Pending hold' : 'Suggestion');
      await t.show(source);
      assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
      assert.equal(t.root.querySelector('.edit-form input:disabled'), null);
      assert.equal(t.root.querySelector('.field-lock'), null);
      const old = t.host.acquire({}); old.heading('Additional copy', 'Copy 1');
      assert.equal(ui.get('#request-dialog-status').hidden, true);
      t.controller.signedOut(); assert.equal(ui.get('#request-dialog-status').hidden, true);
      old.release();
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui);
    try {
      const row = t.root.querySelector('.polaris-edit-tools');
      assert.equal(t.root.querySelector('.research-section'), null);
      assert.equal(row.previousElementSibling.querySelector('input').getAttribute('inputmode'), 'numeric');
      assert.match(row.nextElementSibling.textContent, /Publication timing/);
      assert.deepEqual([...row.querySelectorAll('a')].map(n => n.textContent), ['Open BIB in LEAP', 'First', 'Second']);
      assert.doesNotMatch(row.textContent, /Catalog and research links/);
      for (const a of row.querySelectorAll('a')) { assert.equal(a.target, '_blank'); assert.equal(a.rel, 'noopener noreferrer'); }
      for (const [label, value, link] of [['Title', 'New & café', 'title?q=New%20%26%20caf%C3%A9'],
        ['Identifier', 'x/y', 'id?q=x%2Fy'], ['BIB ID', '456', 'bib/456']]) {
        const input = [...t.root.querySelectorAll('.edit-form label')].find(n => n.firstElementChild.textContent === label).querySelector('input');
        input.value = value; input.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
        assert.ok([...row.querySelectorAll('a')].some(a => a.href.endsWith(link)));
      }
      [...t.root.querySelectorAll('button')].find(n => n.textContent.trim() === 'Search Polaris catalog').click();
      assert.equal(t.lookups[0].query, 'New & café');
      assert.equal(t.lookups[0].requestId, source.id);
      t.lookups[0].apply({ bibId: 789, title: 'Catalog' }, { bibId: 789 });
      assert.ok([...row.querySelectorAll('a')].some(a => a.href.endsWith('/bib/789')));
      t.root.querySelector('.request-save-footer .secondary-button').click(); await settle();
      assert.equal(t.root.querySelectorAll('.polaris-edit-tools a').length, 3, 'Revert retains one populated links row');
      assert.ok(t.root.querySelector('a[href$="/bib/123"]'));
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      const titleInput = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      titleInput.value = 'Unsaved title draft';
      titleInput.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      titleInput.focus();
      assert.equal(detailValue(t.root, 'Patron').querySelector('a'), null);
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a'), null);
      const retainedNameText = detailValue(t.root, 'Patron').textContent;
      release(research); await settle();
      assert.equal(detailValue(t.root, 'Patron').querySelector('a').href, 'https://example.org/patron/41007');
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a').href, 'https://example.org/patron/41007');
      assert.equal(detailValue(t.root, 'Patron').textContent, retainedNameText);
      assert.equal(titleInput.value, 'Unsaved title draft');
      assert.equal(document.activeElement, titleInput, 'Accepted research refresh retains editor focus.');
      assert.equal(titleInput.isConnected, true, 'Accepted research refresh does not rebuild the dirty editor.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      const titleInput = [...t.root.querySelectorAll('.edit-form label')]
        .find(label => label.firstElementChild.textContent === 'Title').querySelector('input');
      titleInput.value = 'Retained draft & focus';
      titleInput.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
      titleInput.focus();
      release({ ...research, leapPatronUrlPattern: 123 }); await settle();

      assert.equal(detailValue(t.root, 'Patron').querySelector('a'), null,
        'A non-string patron pattern remains plain text.');
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a'), null);
      const links = [...t.root.querySelectorAll('.polaris-edit-tools a')];
      assert.deepEqual(links.map(link => link.textContent), ['Open BIB in LEAP', 'First', 'Second']);
      assert.equal(links[0].href, 'https://example.org/bib/123');
      assert.equal(t.root.querySelector('.polaris-edit-tools a[href*="/patron/"]'), null);
      assert.ok([...t.root.querySelectorAll('.polaris-edit-tools button')]
        .some(control => control.textContent.trim() === 'Search Polaris catalog'));
      assert.equal(titleInput.value, 'Retained draft & focus');
      assert.equal(titleInput.isConnected, true);
      assert.equal(document.activeElement, titleInput,
        'Malformed patron metadata does not interrupt accepted config or move editor focus.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const initialResearch = { ...research, patronId: 62001,
      leapPatronUrlPattern: 'https://initial.example.org/patron/{{patronId}}' };
    let release;
    const t = await setup(ui, { ...source, research: initialResearch }, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      const nameLink = detailValue(t.root, 'Patron').querySelector('a');
      const barcodeLink = detailValue(t.root, 'Barcode').querySelector('a');
      assert.equal(nameLink.href, 'https://initial.example.org/patron/62001');
      assert.equal(barcodeLink.href, nameLink.href);
      nameLink.focus();
      release({ ...research, patronId: null, leapPatronUrlPattern: 'javascript:open({{patronId}})' });
      await settle();
      assert.strictEqual(detailValue(t.root, 'Patron').querySelector('a'), nameLink,
        'An unresolved later configuration preserves the useful DTO link.');
      assert.strictEqual(detailValue(t.root, 'Barcode').querySelector('a'), barcodeLink);
      assert.equal(document.activeElement, nameLink, 'A still-correct focused DTO link is not replaced.');
      assert.equal(nameLink.target, '_blank'); assert.equal(nameLink.rel, 'noopener noreferrer');
      assert.equal(t.root.querySelector('.polaris-edit-tools').textContent.includes('Open patron in LEAP'), false);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      await t.show({ ...source, id: '92', libraryOrgId: 3, title: 'Other library' });
      const links = [...t.root.querySelectorAll('.polaris-edit-tools a')].map(n => n.href);
      const upperLinks = [detailValue(t.root, 'Patron').querySelector('a').href,
        detailValue(t.root, 'Barcode').querySelector('a').href];
      release({ patronId: 99999, leapPatronUrlPattern: 'https://stale.example.org/patron/{{patron-id}}',
        externalSearchProviders: [{ label: 'Stale', urlTemplate: 'https://stale.example.org/{{title}}' }] }); await settle();
      assert.deepEqual([...t.root.querySelectorAll('.polaris-edit-tools a')].map(n => n.href), links);
      assert.deepEqual([detailValue(t.root, 'Patron').querySelector('a').href,
        detailValue(t.root, 'Barcode').querySelector('a').href], upperLinks);
      assert.equal(ui.get('#request-dialog-title').textContent, 'Other library');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      t.identity.accept({ ...actor, id: '21', authenticationEmail: 'replacement@example.org' });
      release(research); await settle();
      assert.equal(detailValue(t.root, 'Patron').querySelector('a'), null,
        'A response for a retired actor cannot attach patron links.');
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a'), null);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      t.controller.invalidate();
      release(research); await settle();
      assert.equal(detailValue(t.root, 'Patron').querySelector('a'), null,
        'An aborted research load cannot attach patron links.');
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a'), null);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? Promise.reject(new Error('research configuration unavailable')) : undefined });
    try {
      assert.equal(detailValue(t.root, 'Patron').querySelector('a'), null);
      assert.equal(detailValue(t.root, 'Barcode').querySelector('a'), null);
      assert.equal(buttons(t.root).some(control => control.textContent.trim() === 'Already own'), true,
        'A failed research lookup leaves the editor and catalog workflow available.');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const created = '2026-10-21T12:00:00Z', recent = '2026-10-23T12:00:00Z';
    const activity = Object.freeze([
      { id: '1', created, eventType: 'created', message: 'Created' },
      { id: '9007199254740992', created: recent, message: 'Smaller' },
      { id: '9007199254740993', created: recent, message: 'Larger' },
      { id: '10', created: recent, message: 'Ten' },
      { id: '2', created: recent, message: 'Two' },
      { id: 'bad', created: recent, message: 'Invalid ID' },
      { id: '500', created: '2026-10-23T12:00:00.0010001Z', message: 'Earlier tick' },
      { id: '400', created: '2026-10-23T12:00:00.0010002Z', message: 'Later tick' },
      { created: 'bad', message: 'Invalid date' },
      { message: 'Missing date' }
    ].map(Object.freeze));
    const t = await setup(ui, { ...source, activity });
    try {
      const messages = () => [...t.root.querySelectorAll('.request-activity li > span')].map(n => n.textContent.trim());
      assert.deepEqual(messages(), ['Later tick', 'Earlier tick', 'Larger', 'Smaller', 'Ten', 'Two', 'Invalid ID', 'Created', 'Invalid date', 'Missing date']);
      assert.equal(activity[0].message, 'Created'); assert.equal(activity.length, 10);
      assert.equal(t.root.querySelector('.request-activity ol').hasAttribute('reversed'), false);
      button(t.root, 'Claim').click();
      assert.equal(t.root.querySelector('.action-choice button[type="submit"]')?.textContent, 'Claim');
      t.root.querySelector('.action-choice button[type="submit"]').click(); await settle();
      t.root.querySelector('.grid-new-suggestion').click(); assert.equal(t.quick.length, 0, 'pending command blocks quick create');
      t.complete({ ...source, version: 'v2', activity: [...activity, { id: '99', created: '2026-10-24T12:00:00Z', message: 'Newest' }] });
      await settle(); assert.equal(messages()[0], 'Newest');
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const t = await setup(ui);
    try {
      const close = t.root.querySelector('.request-detail-close');
      assert.ok(close, 'The persistent title footer exposes Close.');
      assert.equal(close.textContent.trim(), 'Close');
      assert.equal(close.classList.contains('secondary-button'), true);
      close.focus(); assert.equal(document.activeElement, close, 'Close is keyboard focusable.');
      close.click(); await settle();
      assert.equal(t.root.open, false, 'Clean Close follows the existing dismissal path.');
      assert.equal(t.calls.some(call => call.init.method), false, 'Close does not submit a request mutation.');
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const t = await setup(ui, source, { beforeClose: () => false });
    try {
      const close = t.root.querySelector('.request-detail-close');
      close.click(); await settle();
      assert.equal(t.root.open, true, 'Close respects the existing departure guard.');
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const t = await setup(ui);
    try {
      button(t.root, 'Claim').click();
      const review = t.root.querySelector('.action-choice');
      assert.ok(review, 'Claim opens its inline confirmation before a pending request mutation.');
      review.querySelector('button[type="submit"]').click(); await settle();
      const close = t.root.querySelector('.request-detail-close');
      assert.equal(close.disabled, false, 'Pending operations keep Close available to invoke its guard.');
      close.click(); await settle();
      assert.equal(t.root.open, true, 'Pending operations block footer dismissal.');
      assert.equal(t.calls.filter(call => call.init.method).length, 1, 'Footer Close starts no additional mutation.');
      assert.ok(t.announcements.some(([message]) => /workflow action is in progress/i.test(message)));
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const closed = { ...source, status: 'closed', closeReason: 'rejected',
      capabilities: { ...capabilities, allowedActions: [] } };
    const t = await setup(ui, closed);
    try {
      assert.match(ui.get('#request-dialog-status').textContent, /^Closed/);
      const close = t.root.querySelector('.request-detail-close');
      assert.ok(close); assert.equal(close.disabled, false, 'Closed requests keep Close available.');
      close.click(); await settle();
      assert.equal(t.root.open, false);
      assert.equal(t.calls.some(call => call.init.method), false);
    } finally { t.dispose(); }
  });
  console.log('D1 detail actions, status/locks, research, activity, quick-create and stale lifecycle passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
