const assert = require('assert');

class MockRecord {
  constructor(collection) {
    this.collection = collection;
    this.id = '';
    this.values = {};
  }
  get(name) {
    return this.values[name];
  }
  set(name, value) {
    this.values[name] = value;
  }
}

global.Record = MockRecord;

const contexts = require('../lib/patron_session_contexts.js');
const originalTtlEnv = process.env.ASAP_PATRON_CONTEXT_TTL_HOURS;

function restoreTtlEnv() {
  if (originalTtlEnv === undefined) {
    delete process.env.ASAP_PATRON_CONTEXT_TTL_HOURS;
  } else {
    process.env.ASAP_PATRON_CONTEXT_TTL_HOURS = originalTtlEnv;
  }
}

function makeApp(store) {
  return {
    findCollectionByNameOrId(name) {
      return { name };
    },
    save(record) {
      if (!record.id) record.id = `ctx-${Object.keys(store).length + 1}`;
      store[record.id] = record;
    },
    findRecordById(collection, id) {
      assert.strictEqual(collection, 'patron_session_contexts');
      const record = store[id];
      if (!record) throw new Error('not found');
      return record;
    },
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      assert.strictEqual(collection, 'patron_session_contexts');
      assert.ok(filter.includes('expiresAt'));
      const now = new Date(params.now).getTime();
      return Object.values(store)
        .filter((record) => {
          const expiresAt = record.get('expiresAt');
          return expiresAt && new Date(String(expiresAt).replace(' ', 'T')).getTime() <= now;
        })
        .slice(offset, offset + limit);
    },
    delete(record) {
      delete store[record.id];
    }
  };
}

function makePatron(id) {
  return { id };
}

function makeContext(id, patronId, expiresAt) {
  const record = new MockRecord({ name: 'patron_session_contexts' });
  record.id = id;
  record.set('patron', patronId);
  record.set('patronUserId', patronId);
  record.set('experienceLibraryOrgId', '10');
  record.set('experienceLibraryOrgName', 'Library 10');
  record.set('effectiveLibraryOrgId', '10');
  record.set('effectiveLibraryOrgName', 'Library 10');
  record.set('patronHomeLibraryOrgId', '20');
  record.set('patronHomeLibraryOrgName', 'Home Library 20');
  record.set('expiresAt', expiresAt);
  return record;
}

(function ttlHelperDefaultsAndClamps() {
  delete process.env.ASAP_PATRON_CONTEXT_TTL_HOURS;
  assert.strictEqual(contexts.patronContextTtlHours(), 24);
  process.env.ASAP_PATRON_CONTEXT_TTL_HOURS = '2';
  assert.strictEqual(contexts.patronContextTtlHours(), 2);
  process.env.ASAP_PATRON_CONTEXT_TTL_HOURS = '0';
  assert.strictEqual(contexts.patronContextTtlHours(), 24);
  process.env.ASAP_PATRON_CONTEXT_TTL_HOURS = '200';
  assert.strictEqual(contexts.patronContextTtlHours(), 168);
  restoreTtlEnv();
})();

(function createSetsDefaultExpiry() {
  delete process.env.ASAP_PATRON_CONTEXT_TTL_HOURS;
  const store = {};
  const app = makeApp(store);
  const before = Date.now();
  const record = contexts.createPatronSessionContext(app, makePatron('patron-1'), {
    experienceLibraryOrgId: '10',
    experienceLibraryOrgName: 'Library 10',
    effectiveLibraryOrgId: '10',
    effectiveLibraryOrgName: 'Library 10',
    patronHomeLibraryOrgId: '20',
    patronHomeLibraryOrgName: 'Home Library 20'
  });
  const expiresAt = record.get('expiresAt');
  assert.ok(expiresAt, 'created patron session context should include expiresAt');
  assert.ok(new Date(expiresAt).getTime() > before, 'default expiresAt should be in the future');
  assert.ok(new Date(expiresAt).getTime() <= before + 25 * 60 * 60 * 1000, 'default expiresAt should be approximately 24 hours out');
  restoreTtlEnv();
})();

(function expiredContextIsRejected() {
  const store = {};
  const app = makeApp(store);
  store.expired = makeContext('expired', 'patron-1', new Date(Date.now() - 60 * 1000).toISOString());
  assert.throws(
    () => contexts.getPatronSessionContext(app, makePatron('patron-1'), 'expired'),
    (err) => err && err.code === 401 && /log in again/i.test(err.message),
    'expired context should ask the patron to log in again'
  );
})();

(function unexpiredContextIsAccepted() {
  const store = {};
  const app = makeApp(store);
  store.future = makeContext('future', 'patron-1', new Date(Date.now() + 60 * 60 * 1000).toISOString());
  const payload = contexts.getPatronSessionContext(app, makePatron('patron-1'), 'future');
  assert.strictEqual(payload.effectiveLibraryOrgId, '10');
  assert.strictEqual(payload.patronHomeLibraryOrgId, '20');
})();

(function cleanupDeletesExpiredContextsOnly() {
  const store = {};
  const app = makeApp(store);
  store.expired = makeContext('expired', 'patron-1', new Date(Date.now() - 60 * 1000).toISOString());
  store.future = makeContext('future', 'patron-1', new Date(Date.now() + 60 * 60 * 1000).toISOString());
  const deleted = contexts.deleteExpiredPatronSessionContexts(app, 10);
  assert.strictEqual(deleted, 1);
  assert.ok(!store.expired);
  assert.ok(store.future);
})();

restoreTtlEnv();
console.log('Patron session context expiration tests passed');
