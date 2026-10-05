const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'staff', organizationId: 2, version: 'actor-v1' };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const requestA = { id: '9223372036854775807', version: 'v1', title: 'A', libraryOrgId: 2, status: 'open', capabilities: { canClaim: true } };

(async () => {
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createDetailHost } = await load('detail-host');
    const { createCopyDetailController } = await load('copy-detail');
    const session = createSessionIdentity(); session.accept(actorA);
    const host = createDetailHost({ root: get('#request-dialog') });
    const receipts = [], notices = [], posted = []; let complete;
    const options = { host, sessionIdentity: session, announce: message => notices.push(message),
      beforeOpen: () => ({ isCurrent: () => true }), isNavigationCurrent: () => true, onAlign: async () => ({ commit: () => 1 }),
      onOpened() {}, beforeClose: () => true, onClosed() {}, getFocusReturn: () => null,
      refreshQueue: async () => true, onReceipt: (...args) => receipts.push(args), clearReceipt() {},
      request: async (path, init = {}) => {
        if (!init.method) return requestA;
        posted.push(init); return new Promise(resolve => { complete = resolve; });
      }
    };
    const first = createCopyDetailController(options); await first.open(requestA.id);
    const saving = first.mutate(requestA, 'claim', 'Task claimed.');
    assert.equal(posted[0].signal, undefined, 'copy mutation cannot be cancelled by read disposal');
    assert.equal(first.inspectDeparture().blocked, true);
    session.clear(); first.signedOut(); session.accept(actorB);
    const replacement = createCopyDetailController({ ...options, request: async () => ({ ...requestA, title: 'B' }) });
    await replacement.open(requestA.id);
    complete({ committed: true, request: { ...requestA, version: 'v2' }, finalStatus: 'open' }); await saving;
    assert.equal(receipts[0][2].outcome, 'committed', 'record commit before checking retired presentation');
    assert.equal(get('#request-dialog-title').textContent, 'B');
    assert.equal(notices.includes('Task claimed. Final state: Open.'), false);
    first.dispose(); replacement.dispose(); host.dispose();
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createDetailHost } = await load('detail-host');
    const { createCopyDetailController } = await load('copy-detail');
    const session = createSessionIdentity(); session.accept(actorA);
    const host = createDetailHost({ root: get('#request-dialog') });
    let generation = 1, finishRefresh, deleteOptions;
    const notices = [], receipts = [];
    const controller = createCopyDetailController({ host, sessionIdentity: session,
      announce: message => notices.push(message), beforeOpen: () => ({ isCurrent: () => true }),
      isNavigationCurrent: ticket => ticket === generation, getNavigationGeneration: () => generation,
      onAlign: async () => ({ commit: () => generation }), onOpened() {}, beforeClose: () => true, onClosed() {}, getFocusReturn: () => null,
      refreshQueue: () => new Promise(resolve => { finishRefresh = resolve; }),
      onReceipt: (...args) => receipts.push(args), clearReceipt() {},
      request: async (path, init = {}) => { if (init.method) { deleteOptions = init; return { deleted: true }; } return requestA; } });
    await controller.open(requestA.id);
    session.updatePreferences({ ...actorA, version: 'actor-v2' }, session.preferences());
    const deleting = controller.mutate(requestA, 'delete', 'Task deleted.');
    assert.equal(deleteOptions.body.actorVersion, 'actor-v2', 'delete captures the current same-actor rowversion at dispatch');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(receipts[0][2].outcome, 'committed');
    generation++;
    finishRefresh(false); await deleting;
    assert.equal(notices.some(message => message.includes('task list could not refresh')), false,
      'a completed delete cannot announce its stale follow-up over a new route');
    controller.dispose(); host.dispose();
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createDraftScope } = await load('draft-scope');
    const { createCopyCreationController } = await load('copy-creation');
    const session = createSessionIdentity(); session.accept(actorA);
    const drafts = createDraftScope(); let finishPreview;
    const parent = { request: { ...requestA, bibid: 9001 }, actor: session.preferences(), isCurrent: () => true,
      admit: declaration => drafts.admit(declaration).allowed,
      registerDraft: definition => drafts.register(definition), releaseDraft: handle => drafts.release(handle), touchDraft() {} };
    const replacement = Object.freeze({ ...parent });
    const controller = createCopyCreationController({ root: get('#additional-copy-create-dialog'), sessionIdentity: session,
      announce() {}, onReceipt() {}, clearReceipt() {}, onRecoveryChanged() {}, onParentChanged() {},
      request: () => new Promise(resolve => { finishPreview = resolve; }) });
    const opening = controller.preview(replacement);
    controller.invalidate(parent); controller.close({ force: true }, parent);
    finishPreview({ version: 'v1', bibid: 9001, openCount: 0 });
    assert.equal(await opening, true, 'retired same-ID/version parent cannot invalidate replacement child read');
    controller.close({ force: true }, parent);
    assert.equal(get('#additional-copy-create-dialog').open, true, 'retired parent cannot close replacement child UI');
    controller.dispose(); drafts.dispose();
  });
  for (const newerActor of [actorB, actorA, { ...actorA, authenticationEmail: 'replacement@example.org' },
    { ...actorA, role: 'admin' }, { ...actorA, organizationId: 3 }]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity, actorKey } = await load('session-identity');
      const { createDraftScope } = await load('draft-scope');
      const { createCopyCreationController, copyCreationStorageKey, decodeCopyCreation } = await load('copy-creation');
      const session = createSessionIdentity(); session.accept(actorA);
      const drafts = createDraftScope(); const owner = session.preferences();
      let parentLive = true, complete; const posted = [], receipts = [];
      const source = { ...requestA, bibid: 9001, status: 'pending_hold' };
      const parent = { request: source, actor: owner,
        isCurrent: () => parentLive && session.isCurrent(owner), isSelectionCurrent: () => parentLive && session.isCurrent(owner),
        admit: declaration => drafts.admit(declaration).allowed,
        registerDraft: definition => drafts.register(definition), releaseDraft: handle => drafts.release(handle), touchDraft: () => drafts.touch() };
      const controller = createCopyCreationController({ root: get('#additional-copy-create-dialog'), sessionIdentity: session,
        announce() {}, onReceipt: (...args) => receipts.push(args), clearReceipt() {}, onRecoveryChanged() {}, onParentChanged: async () => null,
        request: async (path, init = {}) => {
          if (!init.method) return { version: source.version, bibid: source.bibid, openCount: 0 };
          posted.push(init); return new Promise(resolve => { complete = resolve; });
        } });
      controller.setStaff(); await controller.preview(parent);
      get('#additional-copy-reminder').checked = true;
      assert.equal(drafts.isDirty(), true);
      const saving = controller.create({ preventDefault() {} });
      assert.equal(posted[0].signal, undefined);
      assert.equal(posted[0].body.emailPurchaseReminder, true);
      session.clear(); parentLive = false; controller.signedOut(); session.accept(newerActor);
      const newKey = copyCreationStorageKey(newerActor);
      const newerRecord = JSON.stringify({ actorKey: actorKey(newerActor), libraryOrgId: 2,
        bibid: 9002, sourceId: '11', version: 'newer-version' });
      dom.window.sessionStorage.setItem(newKey, newerRecord); controller.setStaff();
      complete({ committed: true, additionalCopyRequestId: '71', finalStatus: 'open',
        additionalCopyRequest: { id: '71', status: 'open', version: 'copy-v2' } }); await saving;
      assert.equal(receipts[0][2].outcome, 'committed');
      assert.equal(dom.window.sessionStorage.getItem(newKey), newerRecord, 'late completion cannot delete another actor or newer same-actor evidence');
      assert.equal(controller.review.current().sourceId, '11');
      assert.equal(get('#additional-copy-create-dialog').open, false);
      assert.equal(decodeCopyCreation('{broken', newKey, actorKey(newerActor)), null);
      assert.equal(decodeCopyCreation(JSON.stringify({ actorKey: actorKey(newerActor), libraryOrgId: 2,
        bibid: 9002, sourceId: '0', version: 'v1' }), newKey, actorKey(newerActor)), null);
      assert.equal(decodeCopyCreation(newerRecord, newKey, actorKey(newerActor)).sourceId, '11');
      const legacy = JSON.stringify({ libraryOrgId: 2, bibid: 9002, sourceId: '11', version: 'v1' });
      assert.equal(decodeCopyCreation(legacy, newKey, actorKey(newerActor)), null, 'missing actor evidence is never same-actor proof');
      assert.equal(decodeCopyCreation(newerRecord, newKey), null, 'adoption requires a captured actor to compare');
      controller.dispose(); drafts.dispose();
    });
  }
  for (const change of [{ authenticationEmail: 'replacement@example.org' }, { role: 'admin' }, { organizationId: 3 }]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity, actorKey } = await load('session-identity');
      const { createCopyCreationController, copyCreationStorageKey } = await load('copy-creation');
      const session = createSessionIdentity(); session.accept(actorA);
      const source = { ...requestA, bibid: 9001, status: 'pending_hold' };
      const posts = [], changes = [];
      const parent = () => {
        const owner = session.preferences();
        return { request: source, actor: owner, isCurrent: () => session.isCurrent(owner),
          admit: () => true, registerDraft: () => ({}), releaseDraft() {}, touchDraft() {} };
      };
      const controller = createCopyCreationController({ root: get('#additional-copy-create-dialog'), sessionIdentity: session,
        announce() {}, onReceipt() {}, clearReceipt() {}, onRecoveryChanged: value => changes.push(value), onParentChanged() {},
        request: async (path, init = {}) => {
          if (!init.method) return { version: source.version, bibid: source.bibid, openCount: 0 };
          posts.push({ path, init }); throw new Error('Response lost');
        } });
      controller.setStaff(); await controller.preview(parent()); await controller.create({ preventDefault() {} });
      const oldKey = copyCreationStorageKey(actorA), oldRaw = dom.window.sessionStorage.getItem(oldKey);
      assert.equal(JSON.parse(oldRaw).actorKey, actorKey(actorA));
      const oldEvidence = controller.review.begin();
      controller.signedOut(); session.accept({ ...actorA, ...change }); controller.setStaff();
      const newKey = copyCreationStorageKey(session.preferences());
      assert.notEqual(newKey, oldKey, 'every actor-key component isolates same tenant/staff storage');
      assert.equal(controller.review.current(), null);
      assert.equal(controller.review.begin(), null);
      assert.equal(controller.review.loaded(oldEvidence, { status: 'open', scope: 'all' }), false);
      assert.equal(controller.review.acknowledge({ additionalCopyStatus: 'open', scope: 'all' }), false);
      assert.equal(posts.length, 1, 'foreign acknowledgement/review cannot dispatch');
      assert.equal(changes.filter(value => value.restored).length, 0);
      assert.equal(await controller.preview(parent()), true, 'foreign unreviewed recovery does not block ordinary creation');
      await controller.create({ preventDefault() {} });
      assert.equal(posts.length, 2);
      assert.equal(dom.window.sessionStorage.getItem(oldKey), oldRaw, 'replacement creation preserves original evidence');
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(newKey)).actorKey, actorKey(session.preferences()));
      controller.dispose();
    });
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity, actorKey } = await load('session-identity');
    const { createCopyCreationController, copyCreationStorageKey } = await load('copy-creation');
    const session = createSessionIdentity(); const owner = session.accept(actorA);
    const source = { ...requestA, bibid: 9001, status: 'pending_hold' };
    const posts = [], restored = [];
    const options = { root: get('#additional-copy-create-dialog'), sessionIdentity: session,
      announce() {}, onReceipt() {}, clearReceipt() {}, onRecoveryChanged: value => restored.push(value), onParentChanged() {},
      request: async (path, init = {}) => {
        if (!init.method) return { version: source.version, bibid: source.bibid, openCount: 0 };
        posts.push({ path, init }); throw new Error('Response lost');
      } };
    const parent = { request: source, actor: owner, isCurrent: () => session.isCurrent(owner),
      admit: () => true, registerDraft: () => ({}), releaseDraft() {}, touchDraft() {} };
    const first = createCopyCreationController(options); first.setStaff(); await first.preview(parent);
    get('#additional-copy-reminder').checked = true; await first.create({ preventDefault() {} }); first.dispose();
    const key = copyCreationStorageKey(actorA), raw = dom.window.sessionStorage.getItem(key);
    const revised = session.updatePreferences({ ...actorA, version: 'actor-v2', displayName: 'Revised',
      purchaseReminderDefault: false, weeklyActionSummaryEnabled: true }, owner);
    assert.equal(copyCreationStorageKey(revised), key);
    const second = createCopyCreationController(options); second.setStaff();
    assert.equal(second.review.current().sourceId, source.id);
    assert.equal(second.review.current().reviewed, false, 'restored evidence needs fresh review');
    const evidence = second.review.begin();
    assert.equal(second.review.loaded(evidence, { status: 'open', scope: '2' }), true);
    assert.equal(second.review.acknowledge({ additionalCopyStatus: 'open', scope: '2' }), true);
    const refreshed = session.updatePreferences({ ...revised, version: 'actor-v3', displayName: 'Again' }, revised);
    second.setStaff(); assert.equal(second.review.current().reviewed, true, 'same-epoch preferences retain acknowledgement');
    assert.equal(await second.preview({ ...parent, actor: refreshed }), true);
    assert.equal(get('#additional-copy-reminder').checked, true, 'retry preserves captured reminder rather than current default');
    await second.create({ preventDefault() {} });
    assert.equal(posts[1].path, posts[0].path); assert.deepEqual(posts[1].init.body, posts[0].init.body);
    second.dispose();
    for (const evidenceKey of [undefined, null, '', {}, actorKey({ ...actorA, role: 'admin' })]) {
      const invalid = { ...JSON.parse(raw), actorKey: evidenceKey };
      dom.window.sessionStorage.setItem(key, JSON.stringify(invalid));
      const third = createCopyCreationController(options); third.setStaff();
      assert.equal(third.review.current(), null, 'malformed, missing or foreign actor evidence cannot be adopted at the current key');
      assert.equal(third.review.acknowledge({ additionalCopyStatus: 'open', scope: 'all' }), false);
      assert.equal(await third.preview({ ...parent, actor: refreshed }), true);
      assert.equal(dom.window.sessionStorage.getItem(key), JSON.stringify(invalid), 'unknown evidence remains untouched');
      third.dispose();
    }
  });
  console.log('Copy controllers: 12 fixtures passed, including actor replacement, preference restoration and exact-record cleanup');
})().catch(error => { console.error(error); process.exitCode = 1; });
