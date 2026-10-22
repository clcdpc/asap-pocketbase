const assert = require('node:assert/strict');
const path = require('node:path');
const { fixture } = require('./helpers/staff-controller.cjs');
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const pause = () => new Promise(resolve => setTimeout(resolve, 15));
async function until(predicate, message) {
  for (let index = 0; index < 250; index++) {
    if (predicate()) return;
    await pause();
  }
  assert.ok(predicate(), message);
}

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createNavigationController } = await load('navigation');
    const { createTitleQueue, createCopyQueue } = await load('queues');
    const { createRouter } = await load('router');
    const session = createSessionIdentity();
    const staff = { id: '20', tenantId: 'a', role: 'super_admin', organizationId: 1 };
    session.accept(staff);
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    const vendor = path.resolve(__dirname, '../src/Asap.Web/Frontend/vendor/gridjs/6.2.0/gridjs.umd.js');
    delete require.cache[require.resolve(vendor)];
    dom.window.gridjs = require(vendor);
    const calls = [], copies = [], opened = [], router = createRouter();
    let rendered = 0, hold = false;
    const data = stages.flatMap((status, index) => Array.from({ length: [55, 1, 0, 2, 3][index] }, (_, row) => ({
      id: `${index + 1}${String(row).padStart(3, '0')}`, title: `${status} ${row}`, status,
      libraryOrgId: 2, libraryOrgName: 'Library two', workflowTags: []
    })));
    const navigation = createNavigationController({ router, sessionIdentity: session, announce() {},
      present() {}, closeTransient() {}, getFeatures: () => [],
      onInvalidate: () => { title.invalidate(); copy.invalidate(); },
      onContextChanged: (next, previous) => { title.contextChanged(next, previous); copy.contextChanged(next, previous); },
      views: { queue: { isReady: value => title.isReady(value), refresh: options => title.refresh(options),
        prepare: value => title.prepare(value), find: id => title.find(id), libraryScope: () => '2', resetFilters: () => title.resetFilters() },
        'additional-copies': { refresh: options => copy.refresh(options) } } });
    const common = { sessionIdentity: session, getContext: navigation.context, announce() {}, onOpen: id => opened.push(id),
      onScopeAccepted: navigation.queueScopeAccepted, onLibraries() {}, onRefreshed() {}, onScopeIntent() {} };
    const title = createTitleQueue({ ...common, root: get('#queue-view'), onRendered: () => rendered++,
      onStatusIntent: status => navigation.changeQueueContext('queue', { status }),
      request: (url, options) => {
        let resolve;
        const result = { scope: new URL(url, 'https://localhost').searchParams.get('scope'), items: data,
          organizations: [{ id: 2, name: 'Library two' }, { id: 3, name: 'Library three' }] };
        const promise = hold ? new Promise(done => { resolve = done; }) : Promise.resolve(result);
        calls.push({ url, signal: options.signal, resolve: value => resolve(value || result) });
        return promise;
      } });
    const copy = createCopyQueue({ ...common, root: get('#additional-copy-view'), onRendered() {},
      onStatusIntent: additionalCopyStatus => navigation.changeQueueContext('additional-copies', { additionalCopyStatus }),
      recovery: { current: () => null, begin() {}, loaded() {}, acknowledge() {} },
      request: async url => { copies.push(url); return { scope: navigation.context().scope,
        status: navigation.context().additionalCopyStatus, items: [], availableLibraries: [] }; } });
    try {
      navigation.align({ scope: '2' }); title.setStaff(staff); copy.setStaff(staff);
      await title.refresh();
      await until(() => get('#request-grid .request-title-link'), 'Initial title rows render.');
      assert.equal(title.isReady({ scope: '2', status: 'closed' }), true,
        'Readiness describes the scope-wide accepted collection, not its previous selected status.');
      for (const status of [...stages.slice(1), stages[0]]) {
        const before = { reads: calls.length, rendered };
        get(`#status-tabs [data-status="${status}"]`).click();
        await until(() => !get('#request-grid').firstElementChild.inert &&
          (status === 'pending_hold' ? get('#request-grid .gridjs-notfound') :
            get('#request-grid .request-title-link')?.textContent.startsWith(status)), `${status} commits its own rows.`);
        assert.equal(calls.length, before.reads, 'Fresh status-only selection must not fetch.');
        assert.equal(rendered, before.rendered + 1, 'One selection must request exactly one table revision.');
        assert.equal(title.isReady(navigation.context()), true);
        assert.equal(title.refreshOnEntry(), undefined, 'Same-scope re-entry uses accepted data.');
      }
      const beforeRecent = calls.length;
      data[0] = { ...data[0], status: 'pending_hold' };
      const alignment = await navigation.alignTitle(data[0], { fromRecent: true }, session.preferences(), navigation.prepareDeparture());
      assert.equal(calls.length, beforeRecent + 1, 'A fresh detail with a changed status must reload the accepted queue projection.');
      assert.ok(alignment.commit() !== false);
      await until(() => get('#request-grid .request-title-link')?.dataset.requestId === data[0].id,
        'Closing the aligned detail can return to its authoritatively updated row.');
      hold = true;
      const required = title.refresh();
      assert.equal(title.isReady(navigation.context()), false, 'A required reload makes accepted data non-authoritative.');
      get('#request-grid .request-title-link').click();
      assert.equal(opened.at(-1), data[0].id, 'Accepted rows can still open fresh details while a required collection reload is pending.');
      const prior = calls.at(-1);
      navigation.changeQueueContext('queue', { status: 'closed' });
      const replacement = calls.at(-1);
      assert.notEqual(replacement, prior, 'Canceling a required reload must start a replacement read.');
      assert.equal(prior.signal.aborted, true);
      replacement.resolve({ scope: '2', items: [{ ...data.at(-1), title: 'Authoritative closed' }], organizations: [] });
      await until(() => title.find(data.at(-1).id)?.title === 'Authoritative closed', 'Replacement reload is accepted.');
      prior.resolve({ scope: '2', items: [{ ...data[0], title: 'Obsolete response' }], organizations: [] });
      assert.equal(await required, false);
      assert.equal(title.find(data[0].id), undefined, 'A delayed canceled read cannot restore old data.');

      hold = false;
      title.markStale('2');
      const invalidated = calls.length;
      navigation.changeQueueContext('queue', { status: 'hold_placed' });
      await until(() => title.isReady(navigation.context()), 'An invalidated projection is fetched.');
      assert.equal(calls.length, invalidated + 1);
      const scoped = calls.length;
      navigation.changeQueueContext('queue', { scope: '3' });
      await until(() => title.isReady(navigation.context()), 'A new scope gets its own accepted response.');
      assert.equal(calls.length, scoped + 1); assert.match(calls.at(-1).url, /scope=3$/);
      await copy.refresh();
      const copyReads = copies.length;
      navigation.changeQueueContext('additional-copies', { additionalCopyStatus: 'closed' });
      await until(() => copies.length > copyReads, 'Additional copies still fetches its status-specific endpoint.');
      assert.equal(copies.length, copyReads + 1); assert.match(copies.at(-1), /status=closed/);
    } finally {
      title.dispose(); copy.dispose(); navigation.dispose();
      await pause();
    }
  });
  console.log('Title status-only navigation, scope-wide readiness, canceled authoritative reloads and Copy isolation passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
