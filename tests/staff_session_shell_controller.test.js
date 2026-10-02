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
    const navigation = { invalidate() {}, allow: () => true, navigateFromUrl: async () => true };
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
  console.log('Session startup/disposal, composed unload, actor-bound receipt and shell recreation checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
