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

function byteJson(value) {
  return Array.from(Buffer.from(JSON.stringify(value), 'utf8'));
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

  // Verify branch name mapping lookup
  const appForMapping = makeApp([]);
  appForMapping.findFirstRecordByData = function(collection, field, value) {
    if (collection === "polaris_organizations" && field === "organizationId" && value === "10") {
      return {
        get(f) {
          if (f === "displayName") return "Synced Main Branch";
          return "";
        }
      };
    }
    return null;
  };
  const mockBranches = cache.normalizeBranchList([{ id: '10', label: 'Branch 10' }], appForMapping);
  assert.strictEqual(mockBranches[0].label, 'Synced Main Branch');

  const byteBranchCache = makeRecord({
    patronOrgId: '30',
    branches: byteJson([{ id: '30', label: 'Byte Branch' }]),
    refreshedAt: now.toISOString(),
    sourceKey: 'api.polaris.example.com|1033|100|30'
  });
  app = makeApp([byteBranchCache]);
  assert.deepStrictEqual(cache.getCachedPickupBranches(app, {}, '30', { now }), [{ id: '30', label: 'Byte Branch' }]);

  // Verify branch name sorting is alphabetical
  const unsorted = [
    { id: '3', label: 'Z Branch' },
    { id: '1', label: 'A Branch' },
    { id: '2', label: 'M Branch' },
  ];
  const sorted = cache.normalizeBranchList(unsorted);
  assert.deepStrictEqual(sorted, [
    { id: '1', label: 'A Branch' },
    { id: '2', label: 'M Branch' },
    { id: '3', label: 'Z Branch' },
  ]);

  console.log('Pickup branch cache tests passed.');
} finally {
  if (originalPolarisCache) require.cache[polarisPath] = originalPolarisCache;
  else delete require.cache[polarisPath];
}
