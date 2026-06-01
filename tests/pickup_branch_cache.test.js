const assert = require('assert');
const path = require('path');

const polarisPath = require.resolve('../lib/polaris.js');
const originalPolarisCache = require.cache[polarisPath];
let polarisCalls = 0;
require.cache[polarisPath] = {
  id: polarisPath,
  filename: polarisPath,
  loaded: true,
  exports: {
    getPickupBranches(staff, patronOrgId) {
      polarisCalls++;
      return [
        { id: String(patronOrgId), label: 'Branch ' + patronOrgId },
        { id: String(patronOrgId), label: 'Duplicate' },
        { id: '', label: 'Blank' },
      ];
    }
  }
};

global.Record = function(collection) {
  this.collection = collection;
  this.data = {};
  this.get = (key) => this.data[key];
  this.set = (key, value) => { this.data[key] = value; };
};

function makeApp(initialRows) {
  const rows = (initialRows || []).slice();
  return {
    rows,
    findFirstRecordByFilter(collection, filter, params) {
      const row = rows.find((record) => record.get('patronOrgId') === params.patronOrgId);
      if (!row) throw new Error('not found');
      return row;
    },
    findCollectionByNameOrId(name) { return { name }; },
    findRecordById(collection, id) {
      return { get(field) {
        const values = { host: 'api.polaris.example.com', accessId: 'TEST_ID', apiKey: 'TEST_KEY', langId: '1033', appId: '100', orgId: '1', workstationId: '1', userId: '1' };
        return values[field] || '';
      } };
    },
    save(record) {
      if (!rows.includes(record)) rows.push(record);
    },
    delete(record) {
      const index = rows.indexOf(record);
      if (index >= 0) rows.splice(index, 1);
    },
  };
}

function makeRecord(data) {
  const record = new global.Record({ name: 'polaris_pickup_branch_cache' });
  Object.keys(data).forEach((key) => record.set(key, data[key]));
  return record;
}

try {
  delete require.cache[require.resolve('../lib/polaris/pickup_branch_cache.js')];
  const cache = require('../lib/polaris/pickup_branch_cache.js');
  const now = new Date('2026-06-01T12:00:00Z');

  assert.strictEqual(cache.PICKUP_BRANCH_CACHE_TTL_MS, 24 * 60 * 60 * 1000);
  assert.strictEqual(cache.isPickupBranchCacheFresh(makeRecord({ refreshedAt: '2026-06-01T00:00:01Z' }), now), true);
  assert.strictEqual(cache.isPickupBranchCacheFresh(makeRecord({ refreshedAt: '2026-05-31T11:59:59Z' }), now), false);

  polarisCalls = 0;
  let app = makeApp([makeRecord({ patronOrgId: '10', branches: [{ id: '10', label: 'Cached' }], refreshedAt: '2026-06-01T00:00:01Z', sourceKey: 'api.polaris.example.com|1033|100|10' })]);
  assert.deepStrictEqual(cache.getCachedPickupBranches(app, {}, '10', { now }), [{ id: '10', label: 'Cached' }]);
  assert.strictEqual(polarisCalls, 0);

  polarisCalls = 0;
  app = makeApp([]);
  assert.deepStrictEqual(cache.getCachedPickupBranches(app, {}, '11', { now }), [{ id: '11', label: 'Branch 11' }]);
  assert.strictEqual(polarisCalls, 1);
  assert.strictEqual(app.rows.length, 1);
  assert.deepStrictEqual(app.rows[0].get('branches'), [{ id: '11', label: 'Branch 11' }]);

  polarisCalls = 0;
  app = makeApp([makeRecord({ patronOrgId: '12', branches: [{ id: '12', label: 'Stale' }], refreshedAt: '2026-05-30T00:00:00Z', sourceKey: 'api.polaris.example.com|1033|100|12' })]);
  assert.deepStrictEqual(cache.getCachedPickupBranches(app, {}, '12', { now }), [{ id: '12', label: 'Branch 12' }]);
  assert.strictEqual(polarisCalls, 1);

  app = makeApp([
    makeRecord({ patronOrgId: '20', branches: [], refreshedAt: now.toISOString() }),
    makeRecord({ patronOrgId: '21', branches: [], refreshedAt: now.toISOString() }),
  ]);
  assert.strictEqual(cache.invalidatePickupBranchCache(app, '20'), true);
  assert.deepStrictEqual(app.rows.map((record) => record.get('patronOrgId')), ['21']);

  console.log('Pickup branch cache tests passed.');
} finally {
  if (originalPolarisCache) require.cache[polarisPath] = originalPolarisCache;
  else delete require.cache[polarisPath];
}
