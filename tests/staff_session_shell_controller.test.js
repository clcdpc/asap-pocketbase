const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'c82cb85b-69fc-4d5c-a6d7-7be94f7589d3', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1, version: 'a-v1' };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const flush = () => new Promise(resolve => setImmediate(resolve));

(async () => {
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createStaffShell } = await load('shell');
    const identity = createSessionIdentity();
    let readiness, viewChanges = 0;
    const options = { root: document, sessionIdentity: identity, getContext: () => ({ activeView: 'queue', scope: 'all' }),
      settingsScope: () => 'system', operationsScope: () => 'all', findTitle: () => null,
      openRecentTitle() {}, onViewIntent() { viewChanges += 1; }, onSignOutIntent() {},
      request: () => new Promise(resolve => { readiness = resolve; }) };
    const shell = createStaffShell(options);
    const ownerA = identity.accept(actorA); shell.showWorkspace(ownerA);
    const oldRead = readiness;
    identity.clear(); shell.showSignedOut('Session ended.');
    shell.recordReceipt('A committed after access loss.', ownerA, {});
    assert.equal(get('#signed-out-message').textContent, 'A committed after access loss.');
    const ownerB = identity.accept(actorB); shell.showWorkspace(ownerB);
    oldRead({ state: 'not_configured' }); await flush();
    assert.equal(get('#email-readiness-warning').hidden, true, 'old readiness cannot paint a replacement actor');
    const attemptB = {};
    shell.recordReceipt('B committed.', ownerB, attemptB);
    shell.recordReceipt('Late A must not replace B.', ownerA, {});
    identity.updatePreferences({ ...actorB, version: 'b-v2' }, ownerB);
    shell.showSignedOut('Access ended.');
    assert.equal(get('#signed-out-message').textContent, 'B committed.');
    shell.clearReceipt(attemptB); shell.showSignedOut('B receipt cleared.');
    assert.equal(get('#signed-out-message').textContent, 'B receipt cleared.', 'same-actor preference revision preserves cleanup ownership');
    const staffAttempt = { slot: 'administration-staff-mutation', context: { scope: '2' } };
    shell.recordReceipt('Staff update still needs roster review.', ownerB, staffAttempt, { feature: 'settings' });
    shell.settingsRefreshed(identity.preferences(), { kind: 'settings', scope: '2' });
    shell.showSignedOut('Main settings reloaded.');
    assert.equal(get('#signed-out-message').textContent, 'Staff update still needs roster review.', 'a settings read cannot clear a staff command receipt');
    shell.settingsRefreshed(identity.preferences(), { kind: 'staff', scope: '3' });
    shell.showSignedOut('Another roster reloaded.');
    assert.equal(get('#signed-out-message').textContent, 'Staff update still needs roster review.', 'another scope cannot clear the receipt');
    shell.settingsRefreshed(identity.preferences(), { kind: 'staff', scope: '2' });
    shell.showSignedOut('Owned roster reviewed.');
    assert.equal(get('#signed-out-message').textContent, 'Owned roster reviewed.');
    shell.dispose(); const replacement = createStaffShell(options); replacement.showWorkspace(identity.preferences());
    shell.dispose(); get('[data-view="profile"]').click(); assert.equal(viewChanges, 1, 'disposed shell listeners do not survive recreation');
    shell.recordReceipt('Retired shell must not paint.', ownerB, {});
    assert.equal(get('#workspace').hidden, false); replacement.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSessionCoordinator } = await load('session');
    const identity = createSessionIdentity();
    let releaseOld, dirty = true, lost = 0, workspaces = 0;
    const features = [{ inspectDeparture: () => ({ dirty }), signedOut() { lost += 1; }, setStaff() {} }];
    const shell = { showWorkspace() { workspaces += 1; }, announce() {}, showSignedOut() {} };
    const navigation = { invalidate() {}, prepareDeparture: () => ({}), navigateFromUrl: async () => true };
    const first = createSessionCoordinator({ identity, shell, navigation, getFeatures: () => features, onAccepted() {}, onPreferencesChanged() {},
      sessionRequest: () => new Promise(resolve => { releaseOld = resolve; }) });
    const oldStart = first.start(); first.dispose();
    const replacement = createSessionCoordinator({ identity, shell, navigation, getFeatures: () => features, onAccepted() {}, onPreferencesChanged() {},
      sessionRequest: async () => ({ authenticated: true, accessAllowed: true, staff: actorB }) });
    await replacement.start(); first.dispose();
    releaseOld({ authenticated: true, accessAllowed: true, staff: actorA }); await oldStart;
    assert.equal(identity.actor().id, actorB.id); assert.equal(workspaces, 1, 'retired session load cannot replace accepted identity');
    const unload = new dom.window.Event('beforeunload', { cancelable: true }); window.dispatchEvent(unload);
    assert.equal(unload.defaultPrevented, true, 'composed guard protects a Settings draft');
    dirty = false;
    const { authorizedJson } = await load('http');
    global.fetch = async () => ({ ok: false, status: 401, statusText: 'Unauthorized', json: async () => ({ code: 'staff_session_invalid' }) });
    await assert.rejects(() => authorizedJson('/test'), { status: 401 });
    assert.equal(lost, 1, 'retired unsubscribe cannot detach the replacement session handler');
    assert.equal(identity.actor(), null);
    replacement.dispose();
    const cleanUnload = new dom.window.Event('beforeunload', { cancelable: true }); window.dispatchEvent(cleanUnload);
    assert.equal(cleanUnload.defaultPrevented, false);
    assert.ok(get('#workspace'));
  });
  for (const review of ['unauthenticated', 'active', 'accepted_unknown', 'unavailable', 'malformed', 'replacement', 'access unavailable']) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createStaffShell } = await load('shell');
      const { createSessionCoordinator } = await load('session');
      const identity = createSessionIdentity();
      let sessionReads = 0, cleanups = 0;
      const shell = createStaffShell({ root: document, sessionIdentity: identity,
        getContext: () => ({ activeView: 'queue', scope: 'all' }), settingsScope: () => 'system', operationsScope: () => 'all',
        findTitle: () => null, openRecentTitle() {}, onViewIntent() {}, onSignOutIntent() {}, request: async () => ({}) });
      const coordinator = createSessionCoordinator({ identity, shell,
        navigation: { invalidate() {}, prepareDeparture: () => ({}), navigateFromUrl: async () => true },
        getFeatures: () => [{ signedOut() { cleanups++; } }], onAccepted() {}, onPreferencesChanged() {},
        request: async (path, options) => {
          assert.equal(path, '/api/asap/staff/sign-out');
          assert.equal(options.signal, undefined, 'committing Sign Out is independent of read cancellation');
          if (review === 'accepted_unknown') {
            throw Object.assign(new Error('Accepted sign-out response could not be confirmed.'), {
              status: 200, outcomeUnknown: true
            });
          }
          throw Object.assign(new Error('Sign Out response lost'), { status: review === 'active' ? 503 : review === 'unavailable' ? 408 : 0 });
        },
        sessionRequest: async options => {
          sessionReads++;
          assert.ok(options.signal, 'authoritative session review is a disposable read');
          if (sessionReads === 1) return { authenticated: true, accessAllowed: true, staff: actorA };
          if (review === 'unavailable') throw new Error('Session unavailable');
          if (review === 'malformed') return {};
          if (review === 'unauthenticated') return { authenticated: false, accessAllowed: false };
          if (review === 'access unavailable') return { authenticated: true, accessAllowed: false };
          return { authenticated: true, accessAllowed: true, staff: review === 'replacement' ? actorB : { ...actorA, version: 'a-v2' } };
        } });
      try {
        await coordinator.start();
        if (review === 'unavailable') shell.recordReceipt('An operation still needs authoritative review.', identity.preferences(), {});
        await coordinator.signOut();
        assert.equal(sessionReads, 2, `${review}: response loss must trigger a fresh authoritative session read`);
        const sessionStillActive = review === 'active' || review === 'accepted_unknown';
        assert.equal(get('#workspace').hidden, !sessionStillActive);
        assert.equal(cleanups, sessionStillActive ? 0 : 1);
        if (sessionStillActive) {
          assert.equal(identity.preferences().version, 'a-v2');
          assert.match(get('#app-status').textContent, /not confirmed.*session is still active/i);
          await coordinator.signOut(); assert.equal(sessionReads, 3, 'same active session permits an explicit retry');
        } else {
          assert.equal(identity.actor(), null);
          assert.match(get('#signed-out-message').textContent, review === 'unauthenticated' ? /You are signed out/
            : review === 'unavailable' || review === 'malformed' ? /Sign out.*could not be confirmed.*Sign in again/i
              : review === 'replacement' ? /account or access changed/ : /access is not currently available/);
          assert.doesNotMatch(get('#signed-out-message').textContent, /did not complete/);
          if (review === 'unavailable') assert.match(get('#signed-out-message').textContent, /operation still needs authoritative review/);
        }
      } finally { coordinator.dispose(); shell.dispose(); }
    });
  }

  for (const stage of ['command', 'review']) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createStaffShell } = await load('shell');
      const { createSessionCoordinator } = await load('session');
      const identity = createSessionIdentity();
      let failCommand, finishReview, sessionReads = 0, reviewSignal;
      const shell = createStaffShell({ root: document, sessionIdentity: identity,
        getContext: () => ({ activeView: 'queue', scope: 'all' }), settingsScope: () => 'system', operationsScope: () => 'all',
        findTitle: () => null, openRecentTitle() {}, onViewIntent() {}, onSignOutIntent() {}, request: async () => ({}) });
      const coordinator = createSessionCoordinator({ identity, shell,
        navigation: { invalidate() {}, prepareDeparture: () => ({}), navigateFromUrl: async () => true }, getFeatures: () => [],
        onAccepted() {}, onPreferencesChanged() {},
        request: () => new Promise((resolve, reject) => { failCommand = reject; }),
        sessionRequest: async ({ signal }) => {
          sessionReads++;
          if (sessionReads === 1) return { authenticated: true, accessAllowed: true, staff: actorA };
          reviewSignal = signal;
          return new Promise(resolve => { finishReview = resolve; });
        } });
      try {
        await coordinator.start();
        const pending = coordinator.signOut();
        if (stage === 'review') {
          failCommand(new Error('Response lost')); await flush();
          assert.ok(finishReview, 'authoritative review started');
        }
        coordinator.lose('Actor A ended.');
        const replacement = identity.accept(actorB); shell.showWorkspace(replacement);
        if (stage === 'command') failCommand(new Error('Late response lost'));
        else {
          assert.equal(reviewSignal.aborted, true);
          finishReview({ authenticated: false });
        }
        await pending;
        assert.equal(sessionReads, stage === 'command' ? 1 : 2);
        assert.equal(identity.actor().id, actorB.id, 'late Sign Out cannot revoke a replacement actor');
        assert.equal(get('#workspace').hidden, false);
        assert.equal(get('#app-status').textContent, '');
      } finally { coordinator.dispose(); shell.dispose(); }
    });
  }
  for (const boundary of ['preferences', 'tenantId', 'id', 'authenticationEmail', 'role', 'organizationId', 'access']) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSessionCoordinator } = await load('session');
      const { createStaffShell } = await load('shell');
      const identity = createSessionIdentity();
      const shell = createStaffShell({ root: document, sessionIdentity: identity,
        getContext: () => ({ activeView: 'queue', scope: 'all' }), settingsScope: () => 'system', operationsScope: () => 'all',
        findTitle: () => null, openRecentTitle() {}, onViewIntent() {}, onSignOutIntent() {}, request: async () => ({}) });
      let reads = 0, preferenceUpdates = 0, losses = 0;
      const next = { ...actorA, version: 'a-v2', displayName: 'Authoritative name', notificationEmail: 'new@example.org',
        weeklyActionSummaryEnabled: true, weeklyActionSummaryEmail: 'weekly@example.org', purchaseReminderDefault: true };
      if (boundary !== 'preferences' && boundary !== 'access') next[boundary] = boundary === 'organizationId' ? 2 : `${actorA[boundary]}-changed`;
      const coordinator = createSessionCoordinator({ identity, shell,
        navigation: { invalidate() {}, prepareDeparture: () => ({}), navigateFromUrl: async () => true },
        getFeatures: () => [{ signedOut() { losses++; } }], onAccepted() {}, onPreferencesChanged() { preferenceUpdates++; },
        sessionRequest: async ({ signal }) => {
          assert.ok(signal); reads++;
          return { authenticated: true, accessAllowed: reads === 1 || boundary !== 'access', staff: reads === 1 ? actorA : next };
        } });
      try {
        await coordinator.start();
        const owner = identity.preferences(), epoch = identity.actor().epoch, revision = identity.revision();
        shell.recordReceipt('Same-session receipt survives preferences.', owner, {});
        const refreshed = await coordinator.refreshCurrentStaff(owner);
        if (boundary === 'preferences') {
          assert.deepEqual(refreshed, next);
          assert.equal(identity.actor().epoch, epoch);
          assert.equal(identity.revision(), revision + 1);
          assert.equal(identity.isCurrent(owner), true);
          assert.equal(get('#staff-identity').textContent, next.displayName);
          assert.equal(preferenceUpdates, 1); assert.equal(losses, 0);
          shell.showSignedOut('Ended.');
          assert.equal(get('#signed-out-message').textContent, 'Same-session receipt survives preferences.');
        } else {
          assert.equal(refreshed, null); assert.equal(identity.actor(), null);
          assert.equal(preferenceUpdates, 0); assert.equal(losses, 1);
          assert.equal(get('#workspace').hidden, true);
        }
      } finally { coordinator.dispose(); shell.dispose(); }
    });
  }
  await fixture(async ({ load }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSessionCoordinator } = await load('session');
    const identity = createSessionIdentity();
    let release, updates = 0, losses = 0;
    const coordinator = createSessionCoordinator({ identity,
      shell: { showSignedOut() { losses++; } }, navigation: { invalidate() {} }, getFeatures: () => [],
      onAccepted() {}, onPreferencesChanged() { updates++; },
      sessionRequest: () => new Promise(resolve => { release = resolve; }) });
    try {
      const retired = identity.accept(actorA);
      const reviewing = coordinator.refreshCurrentStaff(retired);
      identity.clear(); identity.accept(actorB);
      release({ authenticated: true, accessAllowed: true, staff: { ...actorA, version: 'late-v2' } });
      assert.equal(await reviewing, null);
      assert.equal(coordinator.updatePreferences({ ...actorA, version: 'late-v3' }, retired), false);
      assert.equal(identity.preferences().id, actorB.id); assert.equal(updates, 0); assert.equal(losses, 0);
    } finally { coordinator.dispose(); }
  });
  console.log('Session refresh/boundaries, startup/disposal, uncertain Sign Out, actor-bound receipt and shell recreation checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
