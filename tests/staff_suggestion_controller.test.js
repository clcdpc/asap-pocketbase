const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const flush = () => new Promise(resolve => setImmediate(resolve));

(async () => {
  for (const outcome of ['committed', 'uncertain', 'accepted_unknown', 'pickup_changed']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      let complete, reject; const receipts = [], posted = [], intents = [];
      const root = get('#staff-suggestion-dialog');
      const options = { root, trigger: get('#new-suggestion'), sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} }, announce() {}, getLibraries: () => [{ id: '2', name: 'A' }],
        beforeOpen: () => true, beforeClose: () => true, openCreatedTitle: async intent => { intents.push(intent); return null; },
        openExistingTitle: async intent => { intents.push(intent); }, onReceipt: (...args) => receipts.push(args), clearReceipt() {},
        request: async (path, init = {}) => {
          if (path.endsWith('/patron-lookup')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101 } };
          if (path.includes('/suggestion-configuration')) return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          posted.push(init); return new Promise((resolve, fail) => { complete = resolve; reject = fail; });
        } };
      const first = createSuggestionController(options); await first.open();
      const scope = root.querySelector('select'); scope.value = '2'; scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      root.querySelector('input[type="search"]').value = 'Patron';
      const form = get('#staff-suggestion-form'); form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); await flush();
      const title = root.querySelector('.staff-suggestion-fields input');
      assert.equal(first.isDirty(), false, 'verified patron establishes a clean configured form baseline');
      title.value = 'Draft'; title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.equal(first.isDirty(), true);
      title.value = ''; title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.equal(first.isDirty(), false, 'returning to configured baseline releases creation draft');
      title.value = 'Submitted title';
      title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true })); await flush();
      assert.ok(complete, `creation dispatch: ${[...form.querySelectorAll(':invalid')].map(node => `${node.tagName}:${node.getAttribute('aria-label') || node.type}`).join(', ')}; ${get('#staff-suggestion-status').textContent}`); assert.equal(posted[0].signal, undefined);
      assert.equal(posted[0].body.libraryOrgId, 2); assert.equal(first.inspectDeparture().blocked, true);
      session.clear(); first.dispose(); session.accept(actorB);
      const replacement = createSuggestionController(options); await replacement.open();
      first.dispose();
      assert.equal(root.open, true, 'repeated retired disposal cannot close a recreated dialog');
      if (outcome === 'committed') complete({ id: '9223372036854775807', libraryOrgId: 2, notificationStatus: 'queued' });
      else reject(Object.assign(new Error(outcome === 'pickup_changed' ? 'Pickup changed; request not created.' : 'Response lost'),
        outcome === 'pickup_changed' ? { status: 401, response: { code: 'request_not_created_pickup_changed', pickupPreferenceChanged: true } }
          : outcome === 'accepted_unknown' ? { status: 200, outcomeUnknown: true } : { status: 0 }));
      await flush();
      assert.equal(receipts.length, 1, `${outcome}: the captured actor receives one terminal receipt before presentation checks`);
      assert.equal(receipts[0][2].outcome, outcome === 'accepted_unknown' ? 'uncertain' : outcome,
        'record outcome before retired UI checks');
      assert.equal(receipts[0][1].id, actorA.id);
      if (outcome === 'committed') assert.equal(receipts[0][2].body.libraryOrgId, 2,
        'the committed stale receipt retains its captured numeric serving library');
      assert.equal(intents.length, 0, 'old actor cannot navigate the replacement');
      assert.equal(root.open, true); assert.equal(root.querySelector('input[type="search"]').value, '');
      assert.equal(replacement.inspectDeparture().blocked, false);
      replacement.dispose();
    });
  }

  for (const acknowledgement of [
    { name: 'missing-library', make: id => ({ id, notificationStatus: 'queued' }) },
    { name: 'wrong-library', make: id => ({ id, libraryOrgId: 3, notificationStatus: 'queued' }) },
    { name: 'string-library', make: id => ({ id, libraryOrgId: '2', notificationStatus: 'queued' }) },
    { name: 'nonnumeric-library', make: id => ({ id, libraryOrgId: 'not-a-library', notificationStatus: 'queued' }) }
  ]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      let complete;
      const receipts = [], posted = [], intents = [];
      const root = get('#staff-suggestion-dialog');
      const controller = createSuggestionController({
        root,
        trigger: get('#new-suggestion'),
        sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} },
        announce() {},
        getLibraries: () => [{ id: '2', name: 'A' }, { id: '3', name: 'B' }],
        beforeOpen: () => true,
        beforeClose: () => true,
        openCreatedTitle: async intent => { intents.push(intent); return null; },
        openExistingTitle: async intent => { intents.push(intent); },
        onReceipt: (...args) => receipts.push(args),
        clearReceipt() {},
        request: async (path, init = {}) => {
          if (path.endsWith('/patron-lookup')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' },
            pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101
          } };
          if (path.includes('/suggestion-configuration')) {
            return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          }
          posted.push(init);
          return new Promise(resolve => { complete = resolve; });
        }
      });

      await controller.open();
      const scope = root.querySelector('select');
      scope.value = '2';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      root.querySelector('input[type="search"]').value = 'Patron';
      const form = get('#staff-suggestion-form');
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); await flush();
      const title = root.querySelector('.staff-suggestion-fields input');
      title.value = 'Title for library two';
      title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush();
      assert.equal(posted.length, 1, `${acknowledgement.name}: one creation request was dispatched`);
      assert.equal(posted[0].body.libraryOrgId, 2, `${acknowledgement.name}: captured scope is the native selected ID`);

      complete(acknowledgement.make('9223372036854775807'));
      await flush();
      assert.equal(receipts.length, 1, `${acknowledgement.name}: preserve one terminal owner receipt`);
      assert.equal(receipts[0][1].id, actorA.id);
      assert.equal(receipts[0][2].outcome, 'uncertain',
        `${acknowledgement.name}: missing or mismatched serving scope cannot confirm creation`);
      assert.equal(receipts[0][2].body.libraryOrgId, 2);
      assert.equal(intents.length, 0, `${acknowledgement.name}: never open details using unconfirmed scope evidence`);
      assert.equal(posted.length, 1, `${acknowledgement.name}: do not blindly replay the uncertain creation`);
      assert.equal(controller.inspectDeparture().blocked, true,
        `${acknowledgement.name}: retain the uncertainty guard for authoritative review`);
      controller.dispose();
    });
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSuggestionController } = await load('suggestion-controller');
    const { createPolarisLookup } = await load('research');
    const session = createSessionIdentity(); session.accept(actorA);
    let finishVerification;
    const lookup = createPolarisLookup({ announce() {}, isAbortError: error => error.name === 'AbortError',
      authorizedJson: async (path, init) => init.body.mode === 'bib'
        ? new Promise(resolve => { finishVerification = resolve; })
        : { results: [{ bibId: 9001, title: 'Catalog title', author: 'Catalog author', identifier: 'Catalog identifier' }] } });
    const root = get('#staff-suggestion-dialog');
    const controller = createSuggestionController({ root, trigger: get('#new-suggestion'), sessionIdentity: session,
      polarisLookup: lookup, announce() {}, getLibraries: () => [{ id: '2', name: 'A' }],
      beforeOpen: () => true, beforeClose: () => true, openCreatedTitle() {}, openExistingTitle() {}, onReceipt() {}, clearReceipt() {},
      request: async path => path.endsWith('/patron-lookup')
        ? { status: 'verified', libraryOrgId: 2, patron: { patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101 } }
        : { libraryOrgId: 2, configuration: { availableFormats: ['book'] } } });
    await controller.open();
    root.querySelector('select').value = '2'; root.querySelector('select').dispatchEvent(new dom.window.Event('change'));
    root.querySelector('input[type="search"]').value = 'Patron';
    get('#staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await flush(); await flush();
    const fields = root.querySelector('.staff-suggestion-fields');
    const [title, author] = fields.querySelectorAll('input[maxlength="500"]');
    const identifier = fields.querySelector('input[maxlength="100"]');
    const button = fields.querySelector('.polaris-edit-tools button');
    assert.equal(fields.querySelector('.staff-suggestion-catalog'), null);
    button.click(); assert.equal(get('#polaris-query').value, '', 'An empty form can open search.'); get('#close-polaris').click();
    title.value = 'Staff title'; author.value = 'Staff author'; identifier.value = 'Staff identifier';
    title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    button.click();
    assert.equal(get('#polaris-mode').value, 'title'); assert.equal(get('#polaris-query').value, 'Staff title');
    assert.equal(get('#polaris-author').value, '');
    get('#polaris-query').value = 'Temporary catalog query'; get('#close-polaris').click();
    assert.equal(title.value, 'Staff title'); assert.equal(author.value, 'Staff author'); assert.equal(identifier.value, 'Staff identifier');
    assert.equal(document.activeElement, button);
    title.value = 'Current title'; button.click(); assert.equal(get('#polaris-query').value, 'Current title');
    get('#polaris-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true })); await flush();
    get('#polaris-results button').click(); await flush();
    finishVerification({ bibId: 9001, title: 'Catalog title', author: 'Catalog author', identifier: 'Catalog identifier' });
    await flush();
    assert.equal(title.value, 'Catalog title (Current title)'); assert.equal(author.value, 'Staff author');
    assert.equal(identifier.value, 'Staff identifier'); assert.equal(fields.querySelector('input[type="hidden"]').value, '9001');
    assert.equal(controller.isDirty(), true);
    button.click(); get('#close-polaris').click();
    assert.equal(fields.querySelector('input[type="hidden"]').value, '9001', 'Cancel preserves verification.');
    button.click(); get('#polaris-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true })); await flush();
    get('#polaris-results button').click(); await flush();
    controller.close({ force: true }); await controller.open();
    finishVerification({ bibId: 9001, title: 'Late title' }); await flush();
    assert.equal(root.querySelector('.staff-suggestion-fields'), null, 'A late selection cannot replace a newer suggestion stage.');
    controller.dispose(); lookup.dispose();
  });
  console.log('Suggestion scope, create outcomes, Title-only Polaris selection/cancel and stale ownership passed');
  for (const notificationStatus of ['queued', 'suppressed', 'dispatch_failed', 'not_requested', undefined]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      const root = get('#staff-suggestion-dialog');
      const posted = [], receipts = [], announcements = [], intents = [];
      const controller = createSuggestionController({ root, trigger: get('#new-suggestion'), sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} }, announce: (...args) => announcements.push(args),
        getLibraries: () => [{ id: '2', name: 'A' }], beforeOpen: () => true, beforeClose: () => true,
        openCreatedTitle: async intent => { intents.push(intent); return null; }, openExistingTitle() {},
        onReceipt: (...args) => receipts.push(args), clearReceipt() {},
        request: async (target, init = {}) => {
          if (target.endsWith('/patron-lookup')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }],
            currentPreferredPickupBranchId: 101 } };
          if (target.includes('/suggestion-configuration')) return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          posted.push(init.body);
          return { id: '9223372036854775807', libraryOrgId: 2, notificationStatus };
        } });
      try {
        await controller.open();
        const library = root.querySelector('select'); library.value = '2';
        library.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
        root.querySelector('input[type="search"]').value = 'Patron';
        const form = get('#staff-suggestion-form');
        form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
        await flush(); await flush();
        const title = root.querySelector('.staff-suggestion-fields input'); title.value = 'Committed suggestion';
        title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
        root.querySelectorAll('input[type="checkbox"]')[1].checked = notificationStatus !== 'not_requested';
        form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
        await flush(); await flush();
        assert.equal(posted.length, 1, 'A notification follow-up failure cannot replay request creation.');
        assert.equal(posted[0].emailPatronConfirmation, notificationStatus !== 'not_requested');
        assert.equal(receipts[0][2].outcome, 'committed');
        assert.equal(intents.length, 1, 'The successfully created request remains available for review.');
        const announcement = announcements.find(item => /created on behalf of the patron/.test(item[0]));
        const expected = { queued: /Confirmation email queued/, suppressed: /suppressed/,
          dispatch_failed: /Confirmation email saved for delivery.*Automatic recovery is pending/,
          not_requested: /No confirmation email was requested/, undefined: /Confirmation email status is unconfirmed/ }[notificationStatus];
        assert.match(announcement[0], expected);
        assert.match(receipts[0][0], expected, 'The captured owner receipt must preserve the same notification outcome.');
        if (notificationStatus === 'dispatch_failed') assert.equal(announcement[1], 'warning');
      } finally { controller.dispose(); }
    });
  }
  const codeMessage = 'This patron cannot submit suggestions because their Polaris patron code is not eligible for the selected servicing library. Staff cannot override this restriction in this workflow.';
  for (const outcome of ['barcode', 'name', 'candidate', 'submit', 'submit_partial']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      const root = get('#staff-suggestion-dialog'), calls = [];
      const denied = () => Object.assign(new Error('Generic error'), {
        status: 403, response: outcome === 'submit_partial'
          ? { code: 'request_not_created_pickup_changed', pickupPreferenceChanged: true, reasonCode: 'patron_ineligible',
              message: 'The suggestion was not confirmed, but the patron\'s preferred pickup location was changed successfully. ' + codeMessage }
          : { code: 'patron_ineligible', status: 'ineligible', message: codeMessage }
      });
      const controller = createSuggestionController({ root, trigger: get('#new-suggestion'), sessionIdentity: session,
        getScope: () => '2', getLibraries: () => [{ id: '2', name: 'Servicing library' }],
        polarisLookup: { close() {}, invalidate() {} }, announce() {}, beforeOpen: () => true, beforeClose: () => true,
        onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
          calls.push({ path, body: init.body });
          if (path.includes('/suggestion-configuration')) return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          if (path.endsWith('/suggestions')) throw denied();
          if (outcome.startsWith('submit')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: 'verified-card', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101 } };
          if (outcome === 'name') return { status: 'ineligible', code: 'patron_ineligible', message: codeMessage };
          if (outcome === 'candidate' && !init.body.barcode) return { status: 'multiple_matches', matches: [
            { barcode: 'candidate-card', name: 'Candidate', homeLibraryOrganizationId: 2 }
          ] };
          throw denied();
        } });
      try {
        await controller.open();
        root.querySelector('input[type="search"]').value = outcome === 'barcode' ? 'excluded-card' : 'Candidate Name';
        const form = get('#staff-suggestion-form');
        form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
        await flush(); await flush();
        if (outcome === 'candidate') {
          root.querySelector('.staff-suggestion-candidate').click();
          await flush(); await flush();
          assert.equal(calls[1].body.barcode, 'candidate-card');
          assert.equal(calls[1].body.query, null, 'Candidate selection always re-verifies the explicit barcode.');
        }
        if (outcome.startsWith('submit')) {
          const title = root.querySelector('.staff-suggestion-fields input');
          title.value = 'Retain this title'; title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
          const notes = root.querySelector('.staff-suggestion-fields textarea');
          notes.value = 'Retain these staff notes';
          form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
          await flush(); await flush();
          assert.equal(title.value, 'Retain this title'); assert.equal(notes.value, 'Retain these staff notes');
          assert.notEqual(root.querySelector('.staff-suggestion-fields').inert, true, 'Retained draft and patron context remain accessible.');
          assert.equal(title.readOnly, true); assert.equal(notes.readOnly, true);
          assert.equal(root.querySelector('.staff-suggestion-fields select').disabled, true);
          assert.equal(root.querySelector('button[type="submit"]').disabled, true);
          assert.equal(controller.inspectDeparture().blocked, false, 'A definite admission rejection does not become an uncertain outcome.');
          form.dispatchEvent(new dom.window.Event('submit', { cancelable: true })); await flush();
          assert.equal(calls.filter(call => call.path.endsWith('/suggestions')).length, 1);
          assert.equal([...root.querySelectorAll('button')].find(button => button.textContent === 'Change patron').disabled, false);
          assert.equal(document.activeElement.textContent, 'Change patron');
        } else {
          assert.equal(root.querySelector('.staff-suggestion-fields'), null, 'Ineligible patrons cannot enable the request form.');
          assert.equal(root.querySelector('.staff-suggestion-matches'), null, 'Rejected candidates are retired.');
          assert.equal(calls.some(call => call.path.includes('/suggestion-configuration')), false);
        }
        const status = get('#staff-suggestion-status');
        assert.equal(status.textContent, denied().response.message); assert.ok(status.classList.contains('error'));
        assert.equal(status.getAttribute('role'), 'status'); assert.equal(status.getAttribute('aria-live'), 'polite');
        assert.equal(status.children.length, 0, 'The rejection explanation is plain text.');
        assert.equal(root.open, true);
        if (outcome.startsWith('submit')) {
          [...root.querySelectorAll('button')].find(button => button.textContent === 'Change patron').click();
          await flush();
          get('#staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
          await flush(); await flush();
          assert.equal(root.querySelector('button[type="submit"]').disabled, false, 'Fresh verification permits a new deliberate attempt.');
          root.querySelector('.staff-suggestion-fields input').value = 'New verified attempt';
          get('#staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
          await flush(); await flush();
          assert.equal(calls.filter(call => call.path.endsWith('/suggestions')).length, 2);
        }
      } finally { controller.dispose(); }
    });
  }
  console.log('Suggestion scope, notification, actor safety and accessible patron-code rejections passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
