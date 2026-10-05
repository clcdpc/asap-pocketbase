const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { resolve, promise }; };

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createDetailHost } = await load('detail-host');
    const host = createDetailHost({ root: get('#request-dialog') });
    const firstRoot = document.createElement('section');
    const secondRoot = document.createElement('section');
    let disposed = 0, closed = 0;
    const first = host.acquire({ dispose() { disposed += 1; }, onClose() {} });
    first.content.append(firstRoot); first.show();
    const second = host.acquire({ dispose() {}, onClose() { closed += 1; } });
    assert.equal(disposed, 1, 'old owner disposes before replacement mounts');
    assert.equal(firstRoot.isConnected, false);
    assert.equal(first.content.isConnected, false, 'retired content root cannot reach the replacement DOM');
    assert.equal(first.isCurrent(), false);
    first.heading('Stale', 'Stale'); first.show();
    second.content.append(secondRoot); second.heading('Current', 'Current'); second.show();
    get('#request-dialog').dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));
    assert.equal(closed, 1);
    assert.equal(get('#request-dialog-title').textContent, 'Current');
    first.release();
    assert.equal(second.isCurrent(), true, 'retired lease cannot release a current owner');
    second.release(); assert.equal(secondRoot.isConnected, false);
    host.dispose();
  });
  for (const copy of [false, true]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createTitleQueue, createCopyQueue } = await load('queues');
      const session = createSessionIdentity(); session.accept(staff);
      let context = Object.freeze({ scope: '2', status: 'suggestion', additionalCopyStatus: 'open', activeView: copy ? 'additional-copies' : 'queue' });
      const calls = [], notices = [], grids = [], opened = [];
      dom.window.gridjs = { h: () => null, Grid: class {
        constructor(config) { this.config = config; grids.push(this); }
        render() {} updateConfig(config) { Object.assign(this.config, config); return this; }
        forceRender() { return this; } destroy() { this.destroyed = true; }
      } };
      const owner = (copy ? createCopyQueue : createTitleQueue)({ root: get(copy ? '#additional-copy-view' : '#queue-view'),
        sessionIdentity: session, getContext: () => context, announce: message => notices.push(message),
        onScopeAccepted() {}, onLibraries() {}, onRefreshed() {}, onRendered() {},
        onOpen: (...args) => opened.push(args), onScopeIntent() {}, onStatusIntent() {},
        recovery: { current: () => null, begin() {}, loaded() {}, acknowledge() {} },
        request: (path, init) => { const pending = deferred(); calls.push({ path, init, pending }); return pending.promise; }
      });
      owner.setStaff(staff);
      const old = owner.refresh();
      context = Object.freeze({ ...context, scope: '3' }); owner.contextChanged(context, { ...context, scope: '2' });
      const current = owner.refresh();
      const id = '9223372036854775807';
      calls[1].pending.resolve({ items: [{ id, title: 'Current', status: copy ? 'open' : 'suggestion' }], scope: '3', status: 'open' });
      assert.equal(await current, true);
      calls[0].pending.resolve({ items: [{ id: '91', title: 'Old', status: 'suggestion' }], scope: '2' }); await old;
      assert.equal(owner.find(id).title, 'Current'); assert.equal(owner.find('91'), undefined);
      assert.ok(calls[0].init.signal.aborted);
      owner.markStale('2');
      assert.equal(owner.find(id).title, 'Current', 'unrelated library commits preserve the projection');
      const beforeCommit = owner.refresh();
      owner.markStale('3'); owner.render();
      assert.equal(owner.find(id), undefined, 'known stale rows are retired immediately');
      assert.ok(calls[2].init.signal.aborted);
      calls[2].pending.resolve({ items: [{ id, title: 'Pre-commit', status: copy ? 'open' : 'suggestion' }], scope: '3', status: 'open' });
      assert.equal(await beforeCommit, false);
      assert.equal(owner.find(id), undefined, 'a late pre-commit read cannot restore retired data');
      const failedEntry = owner.refreshOnEntry({ context, previous: 'settings' });
      calls[3].pending.resolve(Promise.reject(Object.assign(new Error('Review unavailable'), { status: 503 })));
      assert.equal(await failedEntry, false);
      assert.equal(owner.find(id), undefined, 'a failed same-scope re-entry keeps data unavailable');
      const retryEntry = owner.refreshOnEntry({ context, previous: 'settings' });
      calls[4].pending.resolve({ items: [{ id, title: 'Reviewed', status: copy ? 'open' : 'suggestion' }], scope: '3', status: 'open' });
      assert.equal(await retryEntry, true);
      assert.equal(owner.find(id).title, 'Reviewed');
      const stale = owner.refresh(); owner.dispose();
      calls.at(-1).pending.resolve({ items: [], scope: '3' }); await stale;
      assert.ok(grids.every(grid => grid.destroyed));
      assert.equal(notices.includes('0 authorized requests loaded.'), false, 'disposed read cannot announce');
      const replacement = (copy ? createCopyQueue : createTitleQueue)({ root: get(copy ? '#additional-copy-view' : '#queue-view'),
        sessionIdentity: session, getContext: () => context, announce() {}, onScopeAccepted() {}, onLibraries() {}, onRefreshed() {}, onRendered() {},
        onOpen() {}, onScopeIntent() {}, onStatusIntent() {}, recovery: { current: () => null, begin() {}, loaded() {}, acknowledge() {} },
        request: async () => ({ items: [], scope: '3', status: 'open' }) });
      replacement.setStaff(staff); await replacement.refresh();
      const liveGrid = get(copy ? '#additional-copy-grid' : '#request-grid');
      owner.dispose(); owner.signedOut(); owner.contextChanged({ ...context, scope: '2' }, context);
      assert.equal(get(copy ? '#additional-copy-grid' : '#request-grid'), liveGrid, 'retired lifecycle cannot detach the replacement grid');
      replacement.dispose();
    });
  }
  console.log('Exclusive detail lease and locally owned queue read/disposal/recreation contracts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
