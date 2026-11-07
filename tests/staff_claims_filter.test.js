const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'tenant', authenticationEmail: 'one@example.org', role: 'super_admin', organizationId: 1 };
const key = actor => JSON.stringify([actor.tenantId, String(actor.id), actor.authenticationEmail]);
const storageKey = actor => 'asap.staff.claims.' + encodeURIComponent(key(actor));

test('Claims uses current Profile defaults only until an explicit selection, including All', async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const identity = createSessionIdentity();
    let owner = identity.accept({ ...staff, defaultMineUnclaimedFilter: true });
    const claims = createClaimsFilter({ sessionIdentity: identity });
    let changes = 0;
    const controls = ['#claim-filter', '#additional-copy-claim-filter'].map(selector => claims.bind(get(selector), () => changes++));
    const sync = () => controls.forEach(control => control.setStaff(owner));
    const selected = expected => {
      assert.equal(claims.value(), expected);
      for (const selector of ['#claim-filter', '#additional-copy-claim-filter']) assert.equal(get(selector).value, expected);
    };
    sync(); selected('mine_unclaimed');
    assert.equal(dom.window.sessionStorage.length, 0, 'Resolving a default does not create an explicit choice.');
    owner = identity.updatePreferences({ ...owner, defaultMineUnclaimedFilter: false }, owner);
    sync(); selected('all');
    owner = identity.updatePreferences({ ...owner, defaultMineUnclaimedFilter: true }, owner);
    sync(); selected('mine_unclaimed');
    get('#claim-filter').value = 'all'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
    selected('all'); assert.equal(changes, 2, 'Both queue projections observe a genuine choice.');
    owner = identity.updatePreferences({ ...owner, defaultMineUnclaimedFilter: true, version: 'next' }, owner);
    sync(); selected('all');
    get('#additional-copy-claim-filter').value = 'mine';
    get('#additional-copy-claim-filter').dispatchEvent(new dom.window.Event('change'));
    selected('mine');
    assert.deepEqual(JSON.parse(dom.window.sessionStorage.getItem(storageKey(staff))), { staff: key(staff), value: 'mine' });
    for (const control of controls) control.dispose();
  });
});

test('Claims reload validates each stored choice and treats malformed state as absence', async () => {
  await fixture(async ({ load, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const identity = createSessionIdentity(); identity.accept({ ...staff, defaultMineUnclaimedFilter: true });
    for (const value of ['all', 'mine_unclaimed', 'mine', 'unclaimed']) {
      const first = createClaimsFilter({ sessionIdentity: identity });
      first.select(value, identity.preferences());
      assert.equal(createClaimsFilter({ sessionIdentity: identity }).value(), value, `${value} reloads as explicit.`);
    }
    for (const invalid of ['{', 'null', '[]', '"mine"', '{"value":"all"}',
      JSON.stringify({ staff: key(staff), value: 'unknown' }), JSON.stringify({ staff: 'another account', value: 'all' }),
      JSON.stringify({ staff: key(staff), value: { value: 'mine' } })]) {
      dom.window.sessionStorage.setItem(storageKey(staff), invalid);
      const claims = createClaimsFilter({ sessionIdentity: identity });
      assert.equal(claims.value(), 'mine_unclaimed', `Invalid storage ${invalid} remains absent.`);
      identity.accept({ ...staff, defaultMineUnclaimedFilter: false });
      assert.equal(claims.value(), 'all', 'An invalid value must not suppress subsequent default changes.');
      identity.accept({ ...staff, defaultMineUnclaimedFilter: true });
    }
  });
});

test('Claims shares an in-memory fallback when storage access, reads or writes throw', async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const identity = createSessionIdentity(); const owner = identity.accept({ ...staff, defaultMineUnclaimedFilter: true });
    for (const storage of [() => { throw new Error('disabled'); }, () => ({
      getItem() { throw new Error('read denied'); }, setItem() { throw new Error('quota'); }
    }), () => null]) {
      identity.updatePreferences({ ...owner, defaultMineUnclaimedFilter: true }, owner);
      const claims = createClaimsFilter({ sessionIdentity: identity, storage });
      const title = claims.bind(get('#claim-filter'), () => {}), copy = claims.bind(get('#additional-copy-claim-filter'), () => {});
      title.setStaff(owner); copy.setStaff(owner);
      assert.equal(claims.value(), 'mine_unclaimed');
      get('#claim-filter').value = 'unclaimed'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
      title.sync(); copy.sync();
      assert.equal(get('#additional-copy-claim-filter').value, 'unclaimed');
      identity.updatePreferences({ ...owner, defaultMineUnclaimedFilter: false }, owner);
      assert.equal(claims.value(), 'unclaimed');
      assert.equal(claims.select('unexpected', owner), false);
      title.dispose(); copy.dispose();
    }
  });
});

test('Claims isolates principals, keeps same-principal authority changes, and retires old controls and epochs', async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const identity = createSessionIdentity(); const first = identity.accept(staff);
    const claims = createClaimsFilter({ sessionIdentity: identity });
    const binding = claims.bind(get('#claim-filter'), () => {}); binding.setStaff(first);
    claims.select('unclaimed', first);
    const oldControl = get('#claim-filter');
    const authority = identity.accept({ ...staff, role: 'staff', organizationId: 2 });
    binding.setStaff(authority);
    assert.equal(claims.value(), 'unclaimed');
    assert.equal(oldControl.isConnected, false);
    oldControl.value = 'all'; oldControl.dispatchEvent(new dom.window.Event('change'));
    assert.equal(claims.select('all', first), false);
    assert.equal(claims.value(), 'unclaimed');
    for (const replacement of [{ ...staff, id: '21' }, { ...staff, tenantId: 'other' }, { ...staff, authenticationEmail: 'other@example.org' }]) {
      const current = identity.accept({ ...replacement, defaultMineUnclaimedFilter: true }); binding.setStaff(current);
      assert.equal(claims.value(), 'mine_unclaimed', 'No new account inherits another principal’s explicit choice.');
      claims.select('mine', current);
    }
    binding.setStaff(null); const signedOutControl = get('#claim-filter'); identity.clear();
    assert.equal(claims.value(), 'all'); assert.equal(claims.select('mine', authority), false);
    const returned = identity.accept(staff); binding.setStaff(returned);
    assert.equal(claims.value(), 'unclaimed');
    signedOutControl.value = 'all'; signedOutControl.dispatchEvent(new dom.window.Event('change'));
    assert.equal(claims.value(), 'unclaimed', 'A control retired on sign-out cannot change the returning session.');
    binding.dispose();
  });
});
