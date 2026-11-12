const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

const staff = {
  id: '2', tenantId: '11111111-1111-4111-8111-111111111111',
  authenticationEmail: 'staff@example.org', userPrincipalName: 'staff@example.org',
  displayName: 'Staff User', role: 'library', organizationId: 2, organizationName: 'Library Two'
};
const requestId = '9007199254741993';

(async () => {
  await fixture(async ({ load, dom, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSessionCoordinator } = await load('session');
    const { createStaffShell } = await load('shell');
    const { createRouter, readInitialSignInFailure } = await load('router');
    const { createNavigationController } = await load('navigation');

    for (const route of [
      `/staff/?stage=closed&scope=2&request=${requestId}&sort=recent#title-notes`,
      `/staff/?stage=additional_copies&scope=2&copyStatus=closed&request=${requestId}&page=3#copy-notes`
    ]) {
      dom.window.history.replaceState({}, '', route);
      const identity = createSessionIdentity();
      const shell = createStaffShell({ root: document, sessionIdentity: identity,
        getContext: () => navigation.context(), settingsScope: () => 'system', operationsScope: () => 'all',
        findTitle: () => null, openRecentTitle() {}, onViewIntent() {}, onSignOutIntent() {},
        request: async () => ({ state: 'ready' }) });
      shell.showSignedOut('Sign in.');

      const link = new URL(get('.sign-in').getAttribute('href'), dom.window.location.origin);
      assert.equal(link.pathname, '/api/asap/staff/sign-in');
      assert.equal(link.searchParams.get('returnUrl'), route, 'the challenge link carries the entire live staff route once-encoded');

      const failedCallback = new URL(route, dom.window.location.origin);
      failedCallback.searchParams.set('signInError', 'cancelled');
      dom.window.history.replaceState({}, '', `${failedCallback.pathname}${failedCallback.search}${failedCallback.hash}`);
      assert.equal(readInitialSignInFailure(), 'cancelled');
      assert.equal(`${dom.window.location.pathname}${dom.window.location.search}${dom.window.location.hash}`, route,
        'a cancelled challenge returns to the intended route so retry remains targeted');
      shell.showAuthenticationFailure('cancelled');
      const retryLink = new URL(get('.sign-in').getAttribute('href'), dom.window.location.origin);
      assert.equal(retryLink.searchParams.get('returnUrl'), route);

      // A successful deterministic OIDC callback returns to the challenge's local RedirectUri.
      dom.window.history.replaceState({}, '', retryLink.searchParams.get('returnUrl'));
      const router = createRouter();
      const resolved = [];
      const makeQueue = name => ({
        async prepareDetail(id, options) {
          resolved.push({ name, id, scope: options.scope });
          return { request: { id, status: name === 'additional-copies' ? 'closed' : 'closed' }, isCurrent: () => true };
        },
        async prepare(context) { return { scope: context.scope, isCurrent: () => true, accept() {} }; },
        presentDetail(detail) { resolved.push({ name: `${name}-presented`, id: detail.request.id }); },
        activate() {}, deactivate() {}, async refresh() { return true; }, isReady() { return true; }
      });
      const queue = makeQueue('queue'), copies = makeQueue('additional-copies');
      const navigation = createNavigationController({ router, sessionIdentity: identity, detailHost: {},
        getFeatures: () => [], views: { queue, 'additional-copies': copies, settings: { currentScope: () => 'system' } },
        announce() {}, present: shell.presentView, closeTransient() {}, onInvalidate() {}, onContextChanged() {} });
      const session = createSessionCoordinator({ identity, shell, navigation, getFeatures: () => [],
        onAccepted: owner => navigation.align({ scope: String(owner.organizationId) }), onPreferencesChanged() {},
        sessionRequest: async () => ({ authenticated: true, accessAllowed: true, staff }) });
      try {
        navigation.start();
        await session.start();
        assert.deepEqual(resolved.slice(0, 2), [
          { name: route.includes('additional_copies') ? 'additional-copies' : 'queue', id: requestId, scope: '2' },
          { name: `${route.includes('additional_copies') ? 'additional-copies' : 'queue'}-presented`, id: requestId }
        ], 'post-auth navigation loads and presents the exact bigint request identity in its route type');
        assert.equal(new URL(dom.window.location.href).searchParams.get('request'), requestId);
        assert.equal(new URL(dom.window.location.href).searchParams.get('copyStatus'), route.includes('additional_copies') ? 'closed' : null);
        assert.equal(dom.window.location.hash, route.includes('additional_copies') ? '#copy-notes' : '#title-notes');

        router.pushRequest(`${requestId}8`, 'additional_copies', { scope: '2', copyStatus: 'open' });
        router.remember();
        const latestRoute = `${dom.window.location.pathname}${dom.window.location.search}${dom.window.location.hash}`;
        session.lose('Your staff session ended. Sign in again.');
        const recoveryLink = new URL(get('.sign-in').getAttribute('href'), dom.window.location.origin);
        assert.equal(recoveryLink.searchParams.get('returnUrl'), latestRoute,
          'expired-session recovery reads the current route instead of a startup snapshot');
      } finally {
        session.dispose();
        navigation.dispose();
        shell.dispose();
      }
    }
  });
  console.log('Staff sign-in destination, retry, and post-auth route resolution passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
