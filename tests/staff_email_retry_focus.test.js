const assert = require('node:assert/strict');
const { fixture, emailHistoryPage } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 14; i++) await new Promise(resolve => setImmediate(resolve)); };
const staff = () => ({ id: '7', tenantId: 'tenant', authenticationEmail: 'admin@example.org', role: 'super_admin', organizationId: 1, version: 'v1' });
const libraries = [{ id: 2, name: 'Library Two', organizationCodeId: 2, isActive: true }, { id: 3, name: 'Library Three', organizationCodeId: 2, isActive: true }];

(async () => {
  for (const movement of ['stay', 'filter', 'page', 'scope', 'session', 'rejected']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await load('operations-controller');
      const identity = createSessionIdentity(), owner = identity.accept(staff());
      const id = '9007199254740993', posts = [];
      let release, reject, status = 'failed';
      const pending = new Promise((resolve, fail) => { release = resolve; reject = fail; });
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
          const url = new URL(path, 'https://localhost');
          if (init.method === 'POST') { posts.push({ path, init }); return pending; }
          if (url.pathname.endsWith('/organizations')) return { data: libraries };
          if (url.pathname.endsWith('/test-context')) return { code: 'ok', data: { organizationId: 1,
            scopeName: 'System', deliveryMode: 'postmark', state: 'ready', configurationVersion: 'A'.repeat(64) } };
          if (url.pathname.endsWith('/email-operations')) return emailHistoryPage({ count: 60, libraries,
            items: [{ id, organizationId: 2, status, deliveryClass: 'business_event', typeLabel: 'Business event',
              lastErrorCode: status === 'failed' ? 'mail_not_configured' : null, canRetry: status === 'failed',
              createdUtc: '2026-11-01T06:30:00Z', version: status === 'failed' ? 'AAAAAAAAAAE=' : 'AAAAAAAAAAI=' }] }, path);
          return { items: [] };
        } });
      controller.setStaff(owner); controller.activate({ section: 'email' }); await controller.refresh();
      const retry = get('#email-operations-table button'); retry.focus(); retry.click();
      assert.equal(posts.length, 1);
      assert.equal(posts[0].init.body.version, 'AAAAAAAAAAE=', 'focus handoff preserves the certified rowversion');
      assert.equal(dom.window.document.activeElement, get('#email-operations-table'), `${movement}: pending Retry retains stable focus`);
      let expected = '#email-operations-table';
      if (movement === 'filter') {
        get('#email-search').focus(); get('#email-search').value = 'Business event';
        get('#email-search').dispatchEvent(new dom.window.Event('input'));
        get('#email-filters').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
        expected = '#email-search';
      }
      if (movement === 'page') {
        get('#email-next').focus(); get('#email-next').click(); expected = '#email-next';
      }
      if (movement === 'scope') {
        get('#email-view-scope').focus(); get('#email-view-scope').value = 'system';
        get('#email-view-scope').dispatchEvent(new dom.window.Event('change')); expected = '#email-view-scope';
      }
      if (movement === 'session') {
        identity.clear(); controller.signedOut(); controller.setStaff(identity.accept(staff()));
        controller.activate({ section: 'email' }); get('#email-search').focus(); expected = '#email-search';
      }
      await settle();
      if (movement === 'rejected') {
        reject(Object.assign(new Error('Retry was rejected.'), { status: 400 }));
        expected = `[data-email-retry-id="${id}"]`;
      } else {
        status = 'pending';
        release({ code: 'queued', data: { id, dispatchDelayed: false, version: 'AAAAAAAAAAI=' } });
      }
      await settle();
      assert.equal(dom.window.document.activeElement, get(expected), `${movement}: completed Retry respects current query/session and deliberate focus`);
      assert.equal(posts.length, 1, 'focus recovery never dispatches another operation');
      controller.dispose();
    });
  }
  console.log('Email retry focus stays useful while pending and respects changed filter/page/scope/session after completion');
})().catch(error => { console.error(error); process.exitCode = 1; });
