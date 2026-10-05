const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
(async () => {
  const source = fs.readFileSync(path.join(__dirname, '../src/Asap.Web/Frontend/staff/js/session-identity.js'), 'utf8');
  const { createSessionIdentity, actorKey } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
  const session = createSessionIdentity();
  const input = { id: '9007199254740993', tenantId: 'tenant-a', authenticationEmail: 'a@example.org',
    organizationId: 2, role: 'staff', version: 'v1', weeklyActionSummaryEnabled: false };
  const first = session.accept(input);
  const actor = session.actor();
  assert.equal(actor.key, actorKey(first), 'canonical helper is the session and durable-recovery identity');
  const revision = session.revision();
  input.id = 'foreign';
  assert.equal(first.id, '9007199254740993');
  assert.ok(Object.isFrozen(actor));
  assert.ok(Object.isFrozen(first));
  const next = session.updatePreferences({ ...first, version: 'v2', weeklyActionSummaryEnabled: true }, first);
  assert.equal(session.actor(), actor);
  assert.ok(session.revision() > revision);
  assert.equal(session.isCurrent(first), true, 'same-actor preference updates do not invalidate initiated work');
  assert.equal(next.version, 'v2');
  assert.equal(actorKey({ ...next, displayName: 'Revised', purchaseReminderDefault: true }), actor.key);
  for (const change of [{ tenantId: 'tenant-b' }, { id: '9007199254740994' },
    { authenticationEmail: 'b@example.org' }, { role: 'admin' }, { organizationId: 3 }]) {
    assert.notEqual(actorKey({ ...next, ...change }), actor.key, 'each captured actor component changes durable identity');
    const boundary = createSessionIdentity(); const original = boundary.accept(next);
    boundary.accept({ ...next, ...change });
    assert.equal(boundary.isCurrent(original), false, 'each identity change establishes a replacement epoch');
  }
  assert.equal(session.updatePreferences({ ...next, tenantId: 'tenant-b' }, first), null);
  session.clear();
  assert.equal(session.isCurrent(first), false);
  session.accept(next);
  assert.notEqual(session.actor().epoch, actor.epoch, 'same actor returning after loss has a new epoch');
  assert.equal(session.isCurrent(first), false);
  const current = session.preferences();
  session.accept({ ...current, role: 'admin' });
  assert.equal(session.isCurrent(current), false);
  assert.equal(session.updatePreferences(next, first), null);
  console.log('Actor/access epochs and independent preference revisions passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
