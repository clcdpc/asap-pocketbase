const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'staff',
  organizationId: 2, version: 'v1', weeklyActionSummaryEmail: 'saved@example.org' };
const event = { preventDefault() {} };

(async () => {
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const session = createSessionIdentity(); session.accept(staff);
    let finish;
    const notices = [], receipts = [], posted = [];
    const options = { root: get('#profile-view'), sessionIdentity: session,
      announce: message => notices.push(message), onReceipt: (...args) => receipts.push(args), clearReceipt() {},
      onPreferences: (value, owner) => session.updatePreferences(value, owner),
      onSessionLost() {}, onAccessUnavailable() {}, loadSession: async () => ({ authenticated: true, accessAllowed: true, staff }),
      request: (path, init) => { posted.push(init); return new Promise(resolve => { finish = resolve; }); } };
    const first = createProfileController(options); first.setStaff(session.preferences());
    get('#weekly-email').value = 'draft@example.org';
    assert.equal(first.inspectDeparture().dirty, true);
    const saving = first.save(event);
    await first.save(event);
    assert.equal(posted.length, 1);
    assert.equal(posted[0].signal, undefined, 'committing save is independent of read disposal');
    assert.equal(first.inspectDeparture().blocked, true);
    first.dispose();
    const replacement = createProfileController(options); replacement.setStaff(session.preferences());
    get('#weekly-email').value = 'replacement@example.org';
    finish({ staff: { ...staff, version: 'v2' } }); await saving;
    assert.equal(receipts.length, 1, 'disposed UI still records authoritative commit');
    assert.equal(receipts[0][2].outcome, 'committed');
    assert.equal(get('#weekly-email').value, 'replacement@example.org');
    assert.ok(!notices.includes('Profile saved.'), 'old UI cannot announce over replacement');
    replacement.discardDraft();
    assert.equal(replacement.isDirty(), false);
    replacement.dispose();
  });
  for (const losesSession of [false, true]) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createProfileController } = await load('profile-controller');
      const session = createSessionIdentity(); session.accept(staff);
      const receipts = [], cleared = [], notices = [];
      const conflict = Object.assign(new Error('Profile has changed.'), { status: 409 });
      const controller = createProfileController({ root: get('#profile-view'), sessionIdentity: session,
        announce: message => notices.push(message), onReceipt: (...args) => receipts.push(args),
        clearReceipt: attempt => cleared.push(attempt),
        onPreferences: (value, owner) => session.updatePreferences(value, owner),
        onSessionLost() {}, onAccessUnavailable() {}, request: async () => { throw conflict; },
        loadSession: async () => {
          assert.equal(receipts.length, 1, 'record definitive conflict before recovery can replace the session');
          if (losesSession) {
            session.clear(); controller.signedOut();
            throw Object.assign(new Error('Session replaced'), { name: 'AbortError' });
          }
          return { authenticated: true, accessAllowed: true, staff: { ...staff, version: 'v2' } };
        }
      });
      controller.setStaff(session.preferences());
      await controller.save(event);
      assert.equal(receipts[0][2].outcome, 'rejected');
      assert.match(receipts[0][0], /Profile could not be saved.*Sign in again/);
      assert.equal(cleared.length, losesSession ? 0 : 1, 'successful current review clears only its receipt');
      assert.equal(notices.includes('Profile has changed.'), !losesSession);
      controller.dispose();
    });
  }
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const identity = createSessionIdentity();
    const ownerA = identity.accept(staff);
    let reject;
    const receipts = [], notices = [], posted = [];
    const options = { root: get('#profile-view'), sessionIdentity: identity,
      announce: message => notices.push(message), onReceipt: (...args) => receipts.push(args), clearReceipt() {},
      onPreferences: (value, owner) => identity.updatePreferences(value, owner),
      onSessionLost() {}, onAccessUnavailable() {},
      request: (path, init) => {
        posted.push(init);
        return new Promise((resolve, fail) => { reject = fail; });
      } };
    const first = createProfileController(options);
    first.setStaff(ownerA);
    get('#weekly-email').value = 'a-draft@example.org';
    const saving = first.save(event);
    assert.equal(posted.length, 1);
    first.dispose();
    identity.clear();
    const actorB = { ...staff, id: '21', weeklyActionSummaryEmail: 'b@example.org' };
    identity.accept(actorB);
    const replacement = createProfileController(options);
    replacement.setStaff(identity.preferences());
    get('#weekly-email').value = 'b-draft@example.org';
    reject(Object.assign(new Error('HTTP 200 response could not be confirmed.'), {
      status: 200, outcomeUnknown: true
    }));
    await saving;
    assert.equal(receipts.length, 1, 'the unconfirmed response remains attached to the submitted actor');
    assert.equal(receipts[0][1].id, staff.id);
    assert.equal(receipts[0][2].outcome, 'uncertain');
    assert.equal(get('#weekly-email').value, 'b-draft@example.org', 'retired completion cannot repaint the replacement profile');
    assert.equal(notices.some(message => /could not be confirmed/i.test(message)), false,
      'retired completion cannot announce over the replacement actor');
    replacement.dispose();
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const session = createSessionIdentity(); const original = session.accept(staff);
    const controller = createProfileController({ root: get('#profile-view'), sessionIdentity: session,
      announce() {}, onReceipt() {}, clearReceipt() {}, onPreferences() {}, onSessionLost() {}, onAccessUnavailable() {} });
    try {
      controller.setStaff(original);
      const current = session.updatePreferences({ ...staff, version: 'v2', notificationEmail: 'authoritative@example.org' }, original);
      controller.preferencesChanged(current);
      assert.equal(get('#notification-email').value, 'authoritative@example.org'); assert.equal(controller.isDirty(), false);
      get('#weekly-email').value = 'unsaved@example.org';
      const newer = session.updatePreferences({ ...current, version: 'v3', weeklyActionSummaryEmail: 'external@example.org' }, current);
      controller.preferencesChanged(newer);
      assert.equal(get('#weekly-email').value, 'unsaved@example.org', 'an accepted preference refresh preserves the current Profile draft');
      controller.discardDraft(); assert.equal(get('#weekly-email').value, 'external@example.org');
      session.clear(); const replacement = session.accept({ ...staff, id: '21', notificationEmail: 'replacement@example.org' });
      controller.setStaff(replacement); controller.preferencesChanged(newer);
      assert.equal(get('#notification-email').value, 'replacement@example.org', 'retired preferences cannot populate a replacement Profile');
    } finally { controller.dispose(); }
  });
  console.log('Profile controller preferences, drafts, disposal, replacement, single flight and actor-bound outcome receipts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
