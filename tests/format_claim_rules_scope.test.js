const assert = require('assert');
const path = require('path');

global.__hooks = path.resolve(__dirname, '../pb_hooks');

// Mock Record class
global.Record = function MockRecord(collection) {
  this.values = {};
  this.id = '';
  this.get = (name) => this.values[name];
  this.set = (name, value) => { this.values[name] = value; };
  this.getBool = (name) => !!this.values[name];
  this.collection = () => (typeof collection === 'string' ? { name: collection } : collection);
};

// Mock Error classes for PocketBase
global.UnauthorizedError = class UnauthorizedError extends Error { constructor(m) { super(m); this.name = 'UnauthorizedError'; } };
global.ForbiddenError = class ForbiddenError extends Error { constructor(m) { super(m); this.name = 'ForbiddenError'; } };
global.BadRequestError = class BadRequestError extends Error { constructor(m) { super(m); this.name = 'BadRequestError'; } };
global.NotFoundError = class NotFoundError extends Error { constructor(m) { super(m); this.name = 'NotFoundError'; } };


const staffRoutes = require('../lib/staff_routes.js');

function createMockApp() {
  const orgs = {
    '100': { id: 'org-100', values: { organizationId: '100', displayName: 'Library 100' }, get(n) { return this.values[n]; } },
    '200': { id: 'org-200', values: { organizationId: '200', displayName: 'Library 200' }, get(n) { return this.values[n]; } }
  };
  const staff = {
    'staff-100': { id: 'staff-100', values: { libraryOrgId: '100', role: 'staff', active: true }, get(n) { return this.values[n]; }, getBool(n) { return !!this.values[n]; }, collection: () => ({ name: 'staff_users' }) },
    'staff-200': { id: 'staff-200', values: { libraryOrgId: '200', role: 'staff', active: true }, get(n) { return this.values[n]; }, getBool(n) { return !!this.values[n]; }, collection: () => ({ name: 'staff_users' }) },
    'super-admin': { id: 'super-admin', values: { role: 'super_admin', active: true }, get(n) { return this.values[n]; }, getBool(n) { return !!this.values[n]; }, collection: () => ({ name: 'staff_users' }) }
  };

  const claimRules = [];

  return {
    claimRules,
    app: {
      findCollectionByNameOrId(name) { return { name }; },
      findFirstRecordByData(collectionName, field, value) {
        if (collectionName === 'polaris_organizations' && field === 'organizationId') return orgs[value];
        throw new Error('not found');
      },
      findRecordById(collectionName, id) {
        if (collectionName === 'staff_users') return staff[id];
        throw new Error('not found');
      },
      findRecordsByFilter(collectionName, filter, sort, limit, offset, params) {
        if (collectionName === 'format_claim_rules') {
          return claimRules.filter(r => r.get('libraryOrgId') === params.libraryOrgId);
        }
        return [];
      },
      save(record) {
        if (!record.id) record.id = 'rule-' + Math.random();
        if (record.collection && record.collection().name === 'format_claim_rules') {
           if (!claimRules.includes(record)) claimRules.push(record);
        }
      },

      delete(record) {
        const idx = claimRules.indexOf(record);
        if (idx >= 0) claimRules.splice(idx, 1);
      },
      logger() { return { warn() {}, error() {}, info() {}, debug() {} }; }
    }
  };
}

function makeEvent(app, body, staff) {
  return {
    app,
    requestInfo() { return { body, auth: staff }; },
    json(code, payload) { return { code, payload }; }
  };
}

console.log('Running format claim rules scope tests...');

const { app, claimRules } = createMockApp();
const admin = { id: 'admin-1', values: { role: 'admin', libraryOrgId: '100' }, get(n) { return this.values[n]; }, collection: () => ({ name: 'staff_users' }) };


// Test 1: Save for Library 100 with Staff from Library 100
staffRoutes.updateLibrarySettings(makeEvent(app, {
  orgId: '100',
  formatClaimRules: [{ format: 'book', staffUserId: 'staff-100' }]
}, admin));

assert.strictEqual(claimRules.length, 1);
assert.strictEqual(claimRules[0].get('format'), 'book');
assert.strictEqual(claimRules[0].get('staffUserId'), 'staff-100');

// Test 2: Save for Library 100 with Staff from Library 200 (Should FAIL)
const result2 = staffRoutes.updateLibrarySettings(makeEvent(app, {
  orgId: '100',
  formatClaimRules: [{ format: 'dvd', staffUserId: 'staff-200' }]
}, admin));
assert.strictEqual(result2.code, 400, 'Expected 400 error for cross-library staff assignment');
assert.ok(result2.payload.message.includes('must belong to the selected library'), 'Expected cross-library error message in payload: ' + result2.payload.message);



// Test 3: Save for Library 200 with Super Admin (Should PASS)
const superAdminUser = { id: 'super-admin-user', values: { role: 'super_admin' }, get(n) { return this.values[n]; }, collection: () => ({ name: 'staff_users' }) };
staffRoutes.updateLibrarySettings(makeEvent(app, {
  orgId: '200',
  formatClaimRules: [{ format: 'book', staffUserId: 'super-admin' }]
}, superAdminUser));
assert.strictEqual(claimRules.length, 2);


// Test 4: Save for System Scope (Should NOT save rules)
staffRoutes.updateLibrarySettings(makeEvent(app, {
  orgId: 'system',
  formatClaimRules: [{ format: 'book', staffUserId: 'staff-100' }]
}, admin));
// rules for system are ignored/cleared in saveFormatClaimRules
assert.strictEqual(claimRules.length, 2);

console.log('All format claim rules scope tests passed!');
