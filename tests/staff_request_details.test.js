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
  const calls = [], quick = [], lookups = [], announcements = [];
  const controller = createTitleDetailController({ host, sessionIdentity: identity,
    polarisLookup: { open: context => lookups.push(context), close() {}, invalidate() {} },
    copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
    announce: (...args) => announcements.push(args), beforeOpen: () => ({ isCurrent: () => true }), beforeClose: options.beforeClose || (() => true),
    isNavigationCurrent: () => true, getNavigationGeneration: () => 1, getScope: () => 'all',
    onAlign: async () => ({ scope: 'all', commit: () => 1 }), onOpened() {}, onClosed() {},
    getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
    rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {},
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
      const confirmation = ui.get('#confirmation-dialog');
      assert.equal(confirmation.open, true, action + ' requires its established confirmation');
      assert.equal(ui.get('#confirmation-title').textContent, label);
      assert.equal(mutations().length, 0, 'Opening a row workflow never mutates before approval');
      confirmation.querySelector('.form-actions button:first-child').click(); await settle();
      assert.equal(mutations().length, 0, 'Cancel leaves the request unchanged');
      assert.equal(document.activeElement, button(t.root, action === 'reopen' ? 'Reopen' : label));
      await t.start(action);
      confirmation.querySelector('.form-actions button:last-child').click(); await settle();
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
    require('./helpers/confirmation.cjs').answerConfirmations(ui.dom.window, () => null);
    const t = await setup(ui, { ...source, status, autohold: true, bibidStaffVerified: true,
      capabilities: { ...capabilities, canPlaceHold: true, allowedActions: [action] } });
    try {
      await t.start(action);
      const consent = ui.get('#confirmation-dialog .form-actions button:last-child'); assert.ok(consent);
      t.identity.accept({ ...actor, role: 'staff', organizationId: 3 }); consent.click(); await settle();
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
      button(t.root, 'Claim').click(); await settle();
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
      button(t.root, 'Claim').click(); await settle();
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
