const assert = require('assert');
const path = require('path');

// Mock global variables used by PocketBase hooks
global.__hooks = path.resolve(__dirname, '../pb_hooks');

// Load the module to test
const orgs = require('../pb_hooks/lib/orgs.js');

console.log('Running tests for normalizeOrgId...');

let passed = 0;
let failed = 0;

const testCases = [
  { input: '123', expected: '123', description: 'String input' },
  { input: '  456  ', expected: '456', description: 'String input with whitespace' },
  { input: 789, expected: '789', description: 'Number input' },
  { input: null, expected: '', description: 'Null input' },
  { input: undefined, expected: '', description: 'Undefined input' },
  { input: '', expected: '', description: 'Empty string input' },
  { input: 0, expected: '0', description: 'Zero number input' },
];

testCases.forEach((tc, index) => {
  let actual;
  try {
    actual = orgs.normalizeOrgId(tc.input);
    assert.strictEqual(actual, tc.expected, `Test case ${index} failed: ${tc.description}`);
    console.log(`✅ Test case ${index} passed: ${tc.description}`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: '${tc.expected}'`);
    console.error(`   Actual:   '${actual}'`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

function record(fields) {
  return {
    get: function (key) { return fields[key]; },
  };
}

function appWithOrganizations(organizations) {
  return {
    findFirstRecordByData: function (collection, field, value) {
      if (collection !== 'polaris_organizations' || field !== 'organizationId' || !organizations[value]) {
        throw new Error('not found');
      }
      return record(organizations[value]);
    },
  };
}

console.log('\nRunning tests for attachPatronScope pickup fallback...');

[
  {
    patron: { PatronOrgID: '100', RequestPickupBranchID: '200', PreferredPickupBranchID: '200' },
    expectedId: '200',
    expectedName: 'Request Branch',
    description: 'uses RequestPickupBranchID when present',
  },
  {
    patron: { PatronOrgID: '100', RequestPickupBranchID: '', PreferredPickupBranchID: '100' },
    expectedId: '100',
    expectedName: 'Registered Branch',
    description: 'falls back to PatronOrgID',
  },
  {
    patron: { PatronOrgID: '', RequestPickupBranchID: '', PreferredPickupBranchID: '0' },
    expectedId: '0',
    expectedName: 'Patron registered branch',
    description: 'uses readable default when no branch id is available',
  },
].forEach((tc, index) => {
  try {
    const patron = orgs.attachPatronScope(appWithOrganizations({
      '100': { organizationCodeId: '3', displayName: 'Registered Branch', name: 'Registered Branch', parentOrganizationId: '10' },
      '200': { organizationCodeId: '3', displayName: 'Request Branch', name: 'Request Branch', parentOrganizationId: '10' },
      '10': { organizationCodeId: '2', displayName: 'Library', name: 'Library', parentOrganizationId: '' },
    }), Object.assign({}, tc.patron));
    assert.strictEqual(patron.PreferredPickupBranchID, tc.expectedId, `Pickup fallback ${index} failed: ${tc.description}`);
    assert.strictEqual(patron.PreferredPickupBranchName, tc.expectedName, `Pickup name ${index} failed: ${tc.description}`);
    console.log(`✅ Pickup fallback ${index} passed: ${tc.description}`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    failed++;
  }
});

console.log(`\nAll tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
