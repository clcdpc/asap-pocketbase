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
  console.log('Profile controller disposal, replacement, single flight and actor-bound outcome receipts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
