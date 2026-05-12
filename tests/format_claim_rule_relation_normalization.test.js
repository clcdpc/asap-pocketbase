const assert = require('assert');
const path = require('path');

global.__hooks = path.resolve(__dirname, '../pb_hooks');

function makeRecord(id, values, collectionName) {
  return {
    id,
    values: Object.assign({}, values || {}),
    get(name) { return this.values[name]; },
    getBool(name) { return !!this.values[name]; },
    collection() { return { name: collectionName || 'mock' }; }
  };
}

const config = require('../lib/config.js');
const staffRoutes = require('../lib/staff_routes.js');

const originalLibrarySettings = config.librarySettings;
const originalEmailStatus = config.emailStatus;
config.librarySettings = function () { return { emails: {}, ui_text: {}, workflow: {}, leapBibUrlPattern: '' }; };
config.emailStatus = function () { return {}; };

console.log('Running format-claim relation normalization tests...');

const app = {
  findRecordsByFilter(collectionName) {
    if (collectionName === 'format_claim_rules') {
      return [makeRecord('rule-1', { format: 'book', staffUser: { id: 'staff-obj-1' }, active: true })];
    }
    if (collectionName === 'staff_users') return [];
    return [];
  },
  logger() { return { error() {}, warn() {} }; }
};

const auth = makeRecord('admin-1', { role: 'admin', libraryOrgId: '100' }, 'staff_users');
const result = staffRoutes.getLibrarySettings({
  app,
  requestInfo() {
    return {
      auth,
      query: { get: (key) => key === 'orgId' ? '100' : '' }
    };
  },
  json(code, payload) { return { code, payload }; }
});

assert.strictEqual(result.code, 200);
assert.strictEqual(result.payload.formatClaimRules[0].staffUserId, 'staff-obj-1');

config.librarySettings = originalLibrarySettings;
config.emailStatus = originalEmailStatus;

console.log('All format-claim relation normalization tests passed!');
