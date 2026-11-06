const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
const source = { id: '91', version: 'v1', title: 'Staff title', author: 'Staff author', identifier: 'STAFF-ID',
  libraryOrgId: 2, libraryOrgName: 'Library', status: 'suggestion', autohold: true, bibid: null,
  bibidStaffVerified: false, notes: 'Saved note', format: 'book', customFields: {}, activity: [],
  capabilities: { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true, allowedActions: ['edit', 'alreadyOwn'] } };
const failure = (status, code, message = code) => Object.assign(new Error(message), { status, response: { code } });

async function setup(ui, initial = source) {
  const { createDetailHost } = await ui.load('detail-host');
  const { createSessionIdentity } = await ui.load('session-identity');
  const { createTitleDetailController } = await ui.load('title-detail');
  const identity = createSessionIdentity();
  identity.accept({ id: '20', role: 'staff', organizationId: 2 });
  const host = createDetailHost({ root: ui.get('#request-dialog') });
  let current = initial, mutate, failDetail = false;
  let detailRead = null;
  const calls = [], lookups = [], announcements = [];
  const controller = createTitleDetailController({ host, sessionIdentity: identity,
    polarisLookup: { open: context => lookups.push(context), close() {}, invalidate() {} },
    copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
    announce: (...args) => announcements.push(args), beforeOpen: () => ({ isCurrent: () => true }), beforeClose: () => true,
    isNavigationCurrent: () => true, getNavigationGeneration: () => 1, getScope: () => '2',
    onAlign: async () => ({ scope: '2', commit: () => 1 }), onOpened() {}, onClosed() {},
    getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
    rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
      calls.push({ path, init });
      if (init.method) return mutate(init.body);
      if (path.includes('research-configuration')) return {};
      if (path.includes('/title-requests/')) {
        if (failDetail) throw failure(503, 'detail_unavailable');
        if (detailRead) return detailRead(current);
        return current;
      }
      return { availableFormats: ['book', 'ebook'], additionalFieldDefinitions: [], formatRules: {} };
    } });
  await controller.open(current.id); await settle();
  const root = ui.get('#request-dialog');
  const button = label => [...root.querySelectorAll('button')].find(n => n.textContent.trim() === label);
  const input = label => [...root.querySelectorAll('.edit-form label')].find(n => n.firstElementChild.textContent === label)?.querySelector('input, textarea, select');
  const change = (label, text) => { const control = input(label); control.value = text; control.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true })); };
  const apply = () => {
    button('Search Polaris catalog').click();
    lookups.at(-1).apply({ bibId: 9001, title: 'Catalog title' }, { bibId: 9001, title: 'Catalog title' });
  };
  return { controller, identity, root, calls, lookups, announcements, button, input, change, apply,
    posts: () => calls.filter(c => c.init.method),
    setMutation: fn => { mutate = fn; }, setCurrent: next => { current = next; }, failDetail: yes => { failDetail = yes; },
    setDetailRead: fn => { detailRead = fn; },
    final: () => root.querySelector('.bib-link-panel .primary-button'),
    async show(next) { current = next; await controller.open(next.id); await settle(); },
    dispose() { controller.dispose(); host.dispose(); } };
}

(async () => {
  for (const bibid of [null, -2, 9001]) await fixture(async ui => {
    const t = await setup(ui, { ...source, bibid });
    try {
      t.change('Title', 'Unsaved title'); t.change('Notes', 'Unsaved note');
      t.root.querySelector('[role=tab][aria-controls$="-activity"]').click();
      t.button('Already own').click();
      assert.equal(t.root.open, true);
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, false);
      assert.match(t.root.querySelector('.bib-link-panel h3').textContent, /^Link a catalog record to continue$/);
      assert.equal(t.root.querySelector('[aria-selected=true]').textContent, 'Details');
      assert.equal(document.activeElement, t.button('Search Polaris catalog'));
      assert.equal(t.final().textContent.trim(), 'Save Bib ID and move to Pending hold');
      t.final().click(); await settle();
      assert.match(t.root.querySelector('.bib-link-panel [role=alert]').textContent, bibid > 0 ? /verify/ : /positive Polaris/);
      assert.equal(t.posts().length, 0);
      t.apply();
      assert.equal(t.input('Title').value, 'Unsaved title', 'Linking must not merge catalog text into other draft fields');
      assert.equal(t.input('Notes').value, 'Unsaved note');
      assert.equal(t.posts().length, 0, 'Selection alone cannot mutate');
      let complete;
      t.setMutation(() => new Promise(resolve => { complete = resolve; }));
      t.final().click(); t.final().click(); await settle();
      assert.equal(t.posts().length, 1);
      assert.deepEqual(t.posts()[0].init.body, { version: 'v1', action: 'alreadyOwn', bibid: 9001, staffSelectedBibId: 9001 });
      t.root.dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true })); await settle();
      assert.equal(t.root.open, true, 'Escape cannot close a pending action');
      complete({ committed: true, request: { ...source, version: 'v2', bibid: 9001, bibidStaffVerified: true, status: 'pending_hold' } });
      await settle();
      assert.equal(t.input('BIB ID').value, '9001');
      assert.equal(t.input('Title').value, 'Unsaved title'); assert.equal(t.input('Notes').value, 'Unsaved note');
      assert.equal(t.controller.isDirty(), true);
      assert.match(t.root.querySelector('.bib-link-outcome').textContent, /Pending hold.*Other request edits remain unsaved/);
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, true);
    } finally { t.dispose(); }
  });

  for (const code of ['bib_not_found', 'notification_dependency_unavailable', 'stale_version']) await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Author', 'Draft author'); t.button('Already own').click(); t.apply();
      let count = 0;
      t.setMutation(body => {
        if (!count++) {
          if (code === 'stale_version') t.setCurrent({ ...source, version: 'v2', notes: 'Server note' });
          throw failure(code === 'stale_version' ? 409 : code === 'bib_not_found' ? 400 : 503, code);
        }
        return { committed: true, request: { ...source, version: 'v3', status: 'pending_hold', bibid: 9001, bibidStaffVerified: true } };
      });
      t.final().click(); await settle();
      assert.equal(t.input('Author').value, 'Draft author');
      assert.equal(t.input('BIB ID').value, '9001');
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, false);
      assert.match(t.root.querySelector('.bib-link-status, [id$="-bib-link-status"]').textContent, new RegExp(code));
      assert.doesNotMatch(t.root.querySelector('.request-activity').textContent, /Already own action saved/);
      if (code === 'stale_version') assert.equal(t.input('Notes').value, 'Server note', 'Unedited fields use the fresh snapshot');
      t.final().click(); await settle();
      assert.equal(t.posts().length, 2);
      assert.equal(t.posts()[1].init.body.version, code === 'stale_version' ? 'v2' : 'v1');
      assert.match(t.root.querySelector('.bib-link-outcome').textContent, /saved/);
    } finally { t.dispose(); }
  });

  for (const failedRefresh of [false, true]) await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Title', 'Draft title during duplicate recovery'); t.change('Notes', 'Draft note during duplicate recovery');
      t.button('Already own').click(); t.apply();
      t.setMutation(() => {
        t.failDetail(failedRefresh);
        throw Object.assign(failure(409, 'duplicate_open_request'), { response: { code: 'duplicate_open_request',
          duplicate: { id: '99', title: 'Other saved request', status: 'pending_hold', bibid: 9001 } } });
      });
      t.final().click(); await settle();
      assert.equal(t.posts().length, 1);
      assert.equal(t.input('Title').value, 'Draft title during duplicate recovery');
      assert.equal(t.input('Notes').value, 'Draft note during duplicate recovery');
      assert.equal(t.input('BIB ID').value, '9001');
      assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
      if (failedRefresh) {
        assert.equal(t.final().textContent, 'Reload current request');
        assert.match(t.root.querySelector('[id$="-bib-link-status"]').textContent, /not saved.*could not refresh/);
        t.failDetail(false); t.final().click(); await settle();
        assert.equal(t.posts().length, 1, 'Duplicate recovery reload cannot repeat the rejected action');
        assert.equal(t.input('Notes').value, 'Draft note during duplicate recovery');
      } else {
        assert.ok(t.root.querySelector('.duplicate-recovery'));
        assert.match(t.root.querySelector('[id$="-bib-link-status"]').textContent, /not saved/);
        t.button('Close current request as duplicate').click(); await settle();
        assert.equal(t.posts().length, 1, 'A duplicate close cannot silently consume unrelated drafts');
        t.button('Continue editing').click();
        assert.equal(t.root.querySelector('.duplicate-recovery'), null);
        assert.equal(document.activeElement, t.input('BIB ID'));
      }
    } finally { t.dispose(); }
  });

  for (const canceled of [false, true]) await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Notes', 'Draft before manual reload'); t.button('Already own').click(); t.apply();
      t.setMutation(() => { t.failDetail(true); throw failure(503, 'unknown', 'Response not confirmed'); });
      t.final().click(); await settle(); t.failDetail(false);
      let release;
      t.setDetailRead(current => new Promise(resolve => { release = () => resolve(current); }));
      t.final().click(); await settle();
      assert.equal(t.input('Notes').disabled, false);
      t.change('Notes', 'Latest note typed during manual reload');
      t.change('Title', 'Latest title typed during manual reload');
      if (canceled) t.root.dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true }));
      release(); await settle();
      assert.equal(t.posts().length, 1);
      assert.equal(t.input('Notes').value, 'Latest note typed during manual reload');
      assert.equal(t.input('Title').value, 'Latest title typed during manual reload');
      assert.equal(t.input('BIB ID').value, '9001');
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, canceled, 'A late reload cannot reopen canceled linking');
      assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, { ...source, autohold: false });
    try {
      t.button('Already own').click(); t.apply();
      assert.equal(t.final().textContent.trim(), 'Save Bib ID and close without hold');
      t.setMutation(() => ({ committed: true, request: { ...source, autohold: false, bibid: 9001, bibidStaffVerified: true, status: 'closed', version: 'v2' } }));
      t.final().click(); await settle();
      assert.equal(Object.hasOwn(t.posts()[0].init.body, 'autohold'), false);
      assert.match(t.root.querySelector('.bib-link-outcome').textContent, /Closed/);
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui, { ...source, bibid: 9001, bibidStaffVerified: true });
    try {
      t.setMutation(() => { throw failure(400, 'bib_not_found', 'The saved BIB is no longer valid.'); });
      t.button('Already own').click(); await settle();
      assert.equal(t.posts().length, 1, 'A saved verified BIB retains the established direct action');
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, false, 'A rejected saved BIB offers the same inline recovery');
      assert.match(t.root.querySelector('[id$="-bib-link-status"]').textContent, /saved BIB is no longer valid/);
      assert.equal(document.activeElement, t.button('Search Polaris catalog'));
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Notes', 'Keep this draft'); t.button('Already own').click(); t.apply();
      const old = t.lookups.at(-1);
      t.change('BIB ID', '9002');
      assert.match(t.root.querySelector('[id$="-bib-link-status"]').textContent, /BIB changed/);
      t.final().click(); await settle();
      assert.equal(t.posts().length, 0, 'Changing a verified BIB revokes its selection');
      old.onError('Lookup failed; try again');
      assert.match(t.root.querySelector('[id$="-bib-link-status"]').textContent, /Lookup failed/);
      t.root.dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true })); await settle();
      assert.equal(t.root.open, true); assert.equal(t.root.querySelector('.bib-link-panel').hidden, true);
      assert.equal(document.activeElement, t.button('Already own'));
      assert.equal(t.input('Notes').value, 'Keep this draft'); assert.equal(t.posts().length, 0);
      await t.show({ ...source, id: '92', title: 'Other request' });
      old.apply({ bibId: 123 }, { bibId: 123 });
      assert.equal(t.input('BIB ID').value, '', 'A stale callback cannot update the replacement request');
    } finally { t.dispose(); }
  });

  await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Notes', 'Draft survives unknown response'); t.button('Already own').click(); t.apply();
      t.setMutation(() => { t.failDetail(true); throw failure(503, 'unknown', 'Acknowledgement lost'); });
      t.final().click(); await settle();
      assert.equal(t.final().textContent, 'Reload current request');
      assert.equal(t.input('Notes').value, 'Draft survives unknown response');
      t.button('Cancel linking').click(); t.button('Already own').click();
      assert.equal(t.root.querySelector('.bib-link-panel').hidden, false, 'Recovery remains reachable after cancel/reopen');
      assert.equal(t.final().textContent, 'Reload current request');
      t.failDetail(false);
      t.setCurrent({ ...source, version: 'v2', bibid: 9001, bibidStaffVerified: true, status: 'pending_hold' });
      t.final().click(); await settle();
      assert.equal(t.posts().length, 1, 'Recovery reads current state instead of repeating the write');
      assert.equal(t.input('Notes').value, 'Draft survives unknown response');
      assert.equal(ui.get('#request-dialog-status').textContent, 'Pending hold');
    } finally { t.dispose(); }
  });
  for (const perturb of ['lookup error', 'selection', 'BIB input', 'identifier input', 'editor revert']) await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.button('Already own').click(); t.apply();
      t.setMutation(() => { t.failDetail(true); throw failure(503, 'unknown', 'Uncertain action'); });
      t.final().click(); await settle();
      assert.equal(t.final().textContent, 'Reload current request');
      if (perturb === 'lookup error') t.lookups.at(-1).onError('Later catalog lookup failed');
      if (perturb === 'selection') t.apply();
      if (perturb === 'BIB input') t.change('BIB ID', '9002');
      if (perturb === 'identifier input') t.change('Identifier', 'UNSAVED-ID');
      if (perturb === 'editor revert') {
        t.button('Revert changes').click(); await settle();
        assert.equal(t.controller.isDirty(), false);
        const outcome = t.root.querySelector('.bib-link-outcome');
        assert.equal(outcome.getAttribute('role'), 'alert');
        assert.match(outcome.textContent, /The request outcome could not be confirmed\. Reload before trying again\./);
        assert.doesNotMatch(outcome.textContent, /Other request edits remain unsaved/);
        t.button('Already own').click();
      }
      const readsBeforeReload = t.calls.filter(call => !call.init.method && call.path.includes('/title-requests/')).length;
      t.failDetail(false);
      t.final().click(); await settle();
      assert.equal(t.posts().length, 1, `${perturb}: Reload must never repeat the uncertain workflow action`);
      assert.equal(t.calls.filter(call => !call.init.method && call.path.includes('/title-requests/')).length, readsBeforeReload + 1,
        `${perturb}: Reload must read authoritative request details`);
      assert.equal(ui.get('#request-dialog-status').textContent, 'Suggestion');
      assert.equal(t.final().textContent.trim(), 'Save Bib ID and move to Pending hold', 'Only a successful authoritative read releases the recovery gate');
    } finally { t.dispose(); }
  });
  await fixture(async ui => {
    const t = await setup(ui);
    try {
      t.change('Notes', 'Keep draft when committed detail is unavailable'); t.button('Already own').click(); t.apply();
      t.setMutation(() => {
        t.failDetail(true);
        return { committed: true, request: null, refreshUnavailable: true, finalStatus: 'pending_hold' };
      });
      t.final().click(); await settle();
      assert.equal(t.final().textContent, 'Reload current request');
      assert.equal(t.input('Notes').value, 'Keep draft when committed detail is unavailable');
      t.root.querySelector('.edit-form').dispatchEvent(new ui.dom.window.Event('submit', { bubbles: true, cancelable: true })); await settle();
      assert.equal(t.posts().length, 1, 'Other edits wait for an authoritative refresh after a committed response without details');
      t.failDetail(false);
      t.setCurrent({ ...source, version: 'v2', bibid: 9001, bibidStaffVerified: true, status: 'pending_hold' });
      t.final().click(); await settle();
      assert.equal(t.input('Notes').value, 'Keep draft when committed detail is unavailable');
      assert.equal(ui.get('#request-dialog-status').textContent, 'Pending hold');
    } finally { t.dispose(); }
  });
  console.log('Already own linking: missing/invalid BIB, atomic action, drafts, errors, conflicts, retry, cancellation and stale context passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
