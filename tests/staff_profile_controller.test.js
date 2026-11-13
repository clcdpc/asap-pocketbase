const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'staff',
  organizationId: 2, version: 'v1', weeklyActionSummaryEmail: 'saved@example.org' };
const event = { preventDefault() {} };

(async () => {
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const session = createSessionIdentity();
    let current = { ...staff, notificationEmail: 'primary@example.org', weeklyActionSummaryEmail: null,
      newPatronRequestEmailEnabled: true, weeklyActionSummaryEnabled: true };
    session.accept(current);
    const posted = [];
    const controller = createProfileController({ root: get('#profile-view'), sessionIdentity: session,
      announce() {}, onReceipt() {}, clearReceipt() {}, onSessionLost() {}, onAccessUnavailable() {},
      onPreferences: (value, owner) => session.updatePreferences(value, owner),
      loadSession: async () => ({ authenticated: true, accessAllowed: true, staff: current }),
      request: async (path, init) => {
        posted.push(init.body); current = { ...current, ...init.body, version: `saved-${posted.length}` };
        return { staff: current };
      } });
    try {
      controller.setStaff(session.preferences());
      const field = get('#weekly-email');
      const primary = get('#notification-email');
      assert.equal(primary.tagName, 'DD', 'primary email is a semantic definition, not a form control');
      assert.equal(primary.textContent, 'primary@example.org');
      assert.equal(primary.getAttribute('aria-labelledby'), 'notification-email-label');
      assert.equal(primary.getAttribute('aria-describedby'), 'notification-email-help');
      assert.equal(get('#notification-email-label').textContent, 'Primary notification email');
      assert.equal(get('#profile-form input#notification-email'), null, 'primary email adds no input or tab stop');
      assert.equal(primary.closest('label'), null, 'account information is separate from editable labels');
      const newRequest = get('#new-patron-request-email-enabled');
      assert.equal(newRequest.checked, true, 'new request email subscription defaults on');
      assert.equal(newRequest.closest('label').querySelector('span').textContent,
        'Email me when a new patron purchase request is submitted.');
      assert.equal(field.closest('label').querySelector('span').textContent, 'Additional weekly summary email (optional)');
      assert.equal(field.getAttribute('aria-describedby'), 'weekly-email-help');
      const help = get('#weekly-email-help').textContent;
      assert.match(help, /primary notification email/); assert.match(help, /distinct additional address receives a copy/);
      assert.match(help, /leave blank for primary only/); assert.match(help, /Turning off weekly summaries prevents both/);
      assert.equal(field.value, '', 'an absent additional preference stays blank');
      assert.equal(field.required, false); assert.equal(field.checkValidity(), true);
      field.value = 'not-an-email'; assert.equal(field.checkValidity(), false);
      field.value = ''; await controller.save(event);
      assert.equal(posted[0].weeklyActionSummaryEmail, null);
      assert.equal(posted[0].newPatronRequestEmailEnabled, true);
      newRequest.checked = false; await controller.save(event);
      assert.equal(posted[1].newPatronRequestEmailEnabled, false);
      assert.equal(newRequest.checked, false, 'saved opt-out survives authoritative profile reload');
      field.value = ' additional@example.org '; await controller.save(event);
      assert.equal(posted[2].weeklyActionSummaryEmail, 'additional@example.org');
      assert.equal(field.value, 'additional@example.org');
      assert.equal(posted[2].newPatronRequestEmailEnabled, false);
      assert.equal(Object.hasOwn(posted[2], 'notificationEmail'), false);
      get('#weekly-enabled').checked = false; await controller.save(event);
      assert.equal(posted[3].weeklyActionSummaryEnabled, false);
      assert.equal(posted[3].newPatronRequestEmailEnabled, false);
      assert.equal(Object.hasOwn(posted[3], 'notificationEmail'), false);
      field.value = 'discard@example.org'; controller.discardDraft();
      assert.equal(field.value, 'additional@example.org');
    } finally { controller.dispose(); }
  });
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
      const session = createSessionIdentity(); session.accept({ ...staff, notificationEmail: 'before-conflict@example.org' });
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
          return { authenticated: true, accessAllowed: true, staff: { ...staff, version: 'v2',
            notificationEmail: 'conflict-current@example.org' } };
        }
      });
      controller.setStaff(session.preferences());
      await controller.save(event);
      assert.equal(receipts[0][2].outcome, 'rejected');
      assert.match(receipts[0][0], /Profile could not be saved.*Sign in again/);
      assert.equal(cleared.length, losesSession ? 0 : 1, 'successful current review clears only its receipt');
      assert.equal(notices.includes('Profile has changed.'), !losesSession);
      if (!losesSession) {
        assert.equal(get('#notification-email').textContent, 'conflict-current@example.org',
          'conflict recovery renders primary email from the accepted latest snapshot');
      }
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
      assert.equal(get('#notification-email').textContent, 'authoritative@example.org'); assert.equal(controller.isDirty(), false);
      get('#weekly-email').value = 'unsaved@example.org';
      const newer = session.updatePreferences({ ...current, version: 'v3', notificationEmail: 'latest@example.org',
        weeklyActionSummaryEmail: 'external@example.org' }, current);
      controller.preferencesChanged(newer);
      assert.equal(get('#notification-email').textContent, 'latest@example.org', 'authoritative account data refreshes during a Profile draft');
      assert.equal(get('#weekly-email').value, 'unsaved@example.org', 'an accepted preference refresh preserves the current Profile draft');
      controller.preferencesChanged(current);
      assert.equal(get('#notification-email').textContent, 'latest@example.org', 'an older same-actor snapshot cannot overwrite the latest account value');
      controller.discardDraft(); assert.equal(get('#weekly-email').value, 'external@example.org');
      session.clear(); const replacement = session.accept({ ...staff, id: '21', notificationEmail: 'replacement@example.org' });
      controller.setStaff(replacement); controller.preferencesChanged(newer);
      assert.equal(get('#notification-email').textContent, 'replacement@example.org', 'retired preferences cannot populate a replacement Profile');
    } finally { controller.dispose(); }
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const identity = createSessionIdentity();
    const owner = identity.accept({ ...staff, notificationEmail: '<img src=x onerror=alert(1)>', weeklyActionSummaryEmail: 'saved@example.org' });
    const controller = createProfileController({ root: get('#profile-view'), sessionIdentity: identity,
      announce() {}, onReceipt() {}, clearReceipt() {}, onPreferences() {}, onSessionLost() {}, onAccessUnavailable() {} });
    try {
      controller.setStaff(owner);
      assert.equal(get('#notification-email').textContent, '<img src=x onerror=alert(1)>', 'primary account data is inserted as text');
      assert.equal(get('#notification-email img'), null, 'primary account data cannot create markup');
      const missing = identity.updatePreferences({ ...owner, version: 'missing-v2', notificationEmail: '   ' }, owner);
      controller.preferencesChanged(missing);
      assert.equal(get('#notification-email').textContent, 'No primary notification email is set.');
    } finally { controller.dispose(); }
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createProfileController } = await load('profile-controller');
    const identity = createSessionIdentity();
    const original = identity.accept({ ...staff, version: 'v1', notificationEmail: 'primary@example.org',
      weeklyActionSummaryEmail: 'saved@example.org', weeklyActionSummaryEnabled: true });
    let serverStaff = { ...original };
    let finishRequest;
    const posted = [];
    const controller = createProfileController({ root: get('#profile-view'), sessionIdentity: identity,
      announce() {}, onReceipt() {}, clearReceipt() {}, onSessionLost() {}, onAccessUnavailable() {},
      onPreferences: (value, owner) => identity.updatePreferences(value, owner),
      loadSession: async () => ({ authenticated: true, accessAllowed: true, staff: serverStaff }),
      request: (path, init) => {
        posted.push(init.body);
        return new Promise(resolve => {
          finishRequest = () => {
            serverStaff = { ...serverStaff, ...init.body, version: 'v3' };
            resolve({ staff: serverStaff });
          };
        });
      } });
    try {
      controller.setStaff(original);
      get('#weekly-email').value = 'submitted-draft@example.org';
      const saving = controller.save(event);
      assert.equal(controller.hasPendingMutation(), true);
      assert.equal(Object.hasOwn(posted[0], 'notificationEmail'), false, 'primary email never enters the Profile mutation');
      const pendingOwner = identity.preferences();
      serverStaff = { ...serverStaff, version: 'v2', notificationEmail: 'pending-refresh@example.org',
        weeklyActionSummaryEmail: 'server-change@example.org' };
      const refreshed = identity.updatePreferences(serverStaff, pendingOwner);
      controller.preferencesChanged(refreshed);
      assert.equal(get('#notification-email').textContent, 'pending-refresh@example.org', 'authoritative account text refreshes during a pending save');
      assert.equal(get('#weekly-email').value, 'submitted-draft@example.org', 'pending refresh leaves the editable draft untouched');
      finishRequest();
      await saving;
      assert.equal(get('#notification-email').textContent, 'pending-refresh@example.org');
      assert.equal(get('#weekly-email').value, 'submitted-draft@example.org');
    } finally { controller.dispose(); }
  });
  console.log('Profile controller account presentation, authoritative refresh, draft protection, disposal, replacement and mutation contracts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
