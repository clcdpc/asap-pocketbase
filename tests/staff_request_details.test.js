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

async function setup(ui, initial = source, options = {}) {
  const { createDetailHost } = await ui.load('detail-host');
  const { createSessionIdentity } = await ui.load('session-identity');
  const { createTitleDetailController } = await ui.load('title-detail');
  const identity = createSessionIdentity(); identity.accept(options.actor || actor);
  const host = createDetailHost({ root: ui.get('#request-dialog') });
  let current = initial, pending = null;
  const calls = [], quick = [], lookups = [];
  const controller = createTitleDetailController({ host, sessionIdentity: identity,
    polarisLookup: { open: context => lookups.push(context), close() {}, invalidate() {} },
    copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
    announce() {}, beforeOpen: () => ({ isCurrent: () => true }), beforeClose: () => true,
    isNavigationCurrent: () => true, getNavigationGeneration: () => 1, getScope: () => 'all',
    onAlign: async () => ({ scope: 'all', commit: () => 1 }), onOpened() {}, onClosed() {},
    getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
    rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {},
    onNewSuggestion: (...args) => quick.push(args), request: async (path, init = {}) => {
      calls.push({ path, init });
      if (options.send) { const custom = options.send(path, init); if (custom !== undefined) return custom; }
      if (init.method) return new Promise(resolve => { pending = resolve; });
      if (path.includes('research-configuration')) return research;
      if (path.includes('assignment-candidates')) return { candidates: [{ id: '20', displayName: 'Reviewer' }] };
      if (path.includes('rejection-templates')) return { items: [] };
      return path.includes('/title-requests/') ? current : {};
    } });
  await controller.open(current.id); await settle();
  return { controller, host, identity, calls, quick, lookups, root: ui.get('#request-dialog'),
    async show(next) { current = next; await controller.open(next.id); await settle(); },
    complete(next) { current = next; pending({ committed: true, request: next }); },
    dispose() { controller.dispose(); host.dispose(); } };
}

(async () => {
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
        if (label === 'Assign') [...panel.querySelectorAll('button')].find(n => n.textContent.trim() === 'Cancel').click();
        else t.root.dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true }));
        assert.equal(panel.isConnected, false);
        assert.equal(document.activeElement, opener, label + ' cancellation returns to its own button');
      }
      const patron = [...t.root.querySelectorAll('dt')].find(n => n.textContent === 'Patron').nextElementSibling;
      const plus = patron.querySelector('.grid-new-suggestion');
      assert.ok(plus); assert.equal(plus.previousElementSibling.textContent, 'Synthetic Reviewer');
      assert.equal(t.root.querySelectorAll('.grid-new-suggestion').length, 1);
      plus.click(); assert.deepEqual(t.quick[0], [source.barcode, plus, source.id]);
      const stalePlus = plus;
      await t.show({ ...source, id: '92', barcode: ' ' });
      stalePlus.click(); assert.equal(t.quick.length, 1, 'retired quick-create callback is fenced');
      assert.equal(t.root.querySelector('.grid-new-suggestion'), null);
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
      assert.deepEqual([...row.querySelectorAll('a')].map(n => n.textContent), ['Open BIB in LEAP', 'Open patron in LEAP', 'First', 'Second']);
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
      assert.equal(t.root.querySelectorAll('.polaris-edit-tools a').length, 4, 'Revert retains one populated links row');
      assert.ok(t.root.querySelector('a[href$="/bib/123"]'));
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    let release;
    const t = await setup(ui, source, { send: path => path.includes('research-configuration?requestId=91')
      ? new Promise(resolve => { release = resolve; }) : undefined });
    try {
      await t.show({ ...source, id: '92', libraryOrgId: 3, title: 'Other library' });
      const links = [...t.root.querySelectorAll('.polaris-edit-tools a')].map(n => n.href);
      release({ externalSearchProviders: [{ label: 'Stale', urlTemplate: 'https://stale.example.org/{{title}}' }] }); await settle();
      assert.deepEqual([...t.root.querySelectorAll('.polaris-edit-tools a')].map(n => n.href), links);
      assert.equal(ui.get('#request-dialog-title').textContent, 'Other library');
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
      button(t.root, 'Claim').click(); await settle();
      t.root.querySelector('.grid-new-suggestion').click(); assert.equal(t.quick.length, 0, 'pending command blocks quick create');
      t.complete({ ...source, version: 'v2', activity: [...activity, { id: '99', created: '2026-10-24T12:00:00Z', message: 'Newest' }] });
      await settle(); assert.equal(messages()[0], 'Newest');
    } finally { t.dispose(); }
  });
  console.log('D1 detail actions, status/locks, research, activity, quick-create and stale lifecycle passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
