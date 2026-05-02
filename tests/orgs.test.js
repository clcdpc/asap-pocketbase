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

console.log('\nRunning tests for resolveParentLibrary...');

// Mock polaris explicitly for this section
const polarisMock = {
  callCount: 0,
  adminStaffAuth: function() {
    return { AccessToken: 'mock-token' };
  },
  organizations: function(kind) {
    this.callCount++;
    return [];
  }
};

// Intercept require('.../polaris.js') to serve our mock
const originalRequire = require('module').prototype.require;
require('module').prototype.require = function(moduleName) {
  if (moduleName.includes('lib/polaris.js')) {
    return polarisMock;
  }
  return originalRequire.apply(this, arguments);
};

// Re-require orgs so it uses the mocked polaris
delete require.cache[require.resolve('../pb_hooks/lib/orgs.js')];
const orgsWithMock = require('../pb_hooks/lib/orgs.js');

let mockLogs = [];
const mockLogger = {
  warn: (...args) => mockLogs.push(['warn', ...args]),
  info: (...args) => mockLogs.push(['info', ...args]),
};

// Define some standard static organizations for testing the traversal
const standardOrgs = {
  '10': { organizationId: '10', organizationCodeId: '2', displayName: 'Main Library', name: 'Main Library', parentOrganizationId: '1' },
  '20': { organizationId: '20', organizationCodeId: '3', displayName: 'Downtown Branch', name: 'Downtown Branch', parentOrganizationId: '10' },
  '21': { organizationId: '21', organizationCodeId: '3', displayName: 'Southside Branch', name: 'Southside Branch', parentOrganizationId: '20' }, // nested branch
  '1':  { organizationId: '1', organizationCodeId: '1', displayName: 'The System', name: 'The System', parentOrganizationId: '' },

  // Bad data tests
  '50': { organizationId: '50', organizationCodeId: '3', displayName: 'Loop 1', name: 'Loop 1', parentOrganizationId: '51' },
  '51': { organizationId: '51', organizationCodeId: '3', displayName: 'Loop 2', name: 'Loop 2', parentOrganizationId: '50' },
  '60': { organizationId: '60', organizationCodeId: '3', displayName: 'Orphan Branch', name: 'Orphan Branch', parentOrganizationId: '999' }
};

function createMockApp(initialOrgs = standardOrgs, triggerSyncToPopulate = null) {
  let orgsData = Object.assign({}, initialOrgs);
  return {
    findFirstRecordByData: function(collection, field, value) {
      if (collection !== 'polaris_organizations' || field !== 'organizationId' || !orgsData[value]) {
        throw new Error('not found');
      }
      return record(orgsData[value]);
    },
    findRecordById: function() {
      return record({ set: () => {} });
    },
    findRecordsByFilter: function() {
      return [];
    },
    save: function() {},
    // Expose a way to inject data post-sync
    _injectOrgs: function(newOrgs) {
      Object.assign(orgsData, newOrgs);
    }
  };
}

[
  {
    orgId: null,
    expected: null,
    description: 'Returns null for empty ID'
  },
  {
    orgId: '10',
    expected: { branchOrgId: '10', libraryOrgId: '10', libraryOrgName: 'Main Library' },
    description: 'Resolves direct library (code 2)'
  },
  {
    orgId: '20',
    expected: { branchOrgId: '20', libraryOrgId: '10', libraryOrgName: 'Main Library' },
    description: 'Traverses branch up to library'
  },
  {
    orgId: '21',
    expected: { branchOrgId: '21', libraryOrgId: '10', libraryOrgName: 'Main Library' },
    description: 'Traverses nested branches up to library'
  },
  {
    orgId: '1',
    expected: { branchOrgId: '1', libraryOrgId: '', libraryOrgName: 'The System', scope: 'system' },
    description: 'Resolves system level correctly'
  },
  {
    orgId: '50',
    expected: null,
    description: 'Breaks out of circular parent references'
  },
  {
    orgId: '60',
    expected: null,
    description: 'Returns null if parent is missing'
  }
].forEach((tc, index) => {
  try {
    const actual = orgsWithMock.resolveParentLibrary(createMockApp(), tc.orgId);
    assert.deepStrictEqual(actual, tc.expected, `Resolution ${index} failed: ${tc.description}`);
    console.log(`✅ Resolution ${index} passed: ${tc.description}`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message} (Test: ${tc.description})`);
    failed++;
  }
});

console.log('\nRunning tests for resolveParentLibrary sync fallback...');

// Test 1: Sync is suppressed
try {
  polarisMock.callCount = 0;
  mockLogs = [];
  const app = createMockApp({}); // Empty DB
  const actual = orgsWithMock.resolveParentLibrary(app, '99', { syncIfMissing: false });
  assert.strictEqual(actual, null, 'Should return null when sync is suppressed');
  assert.strictEqual(polarisMock.callCount, 0, 'Should not have called sync');
  console.log(`✅ Sync fallback passed: Sync suppressed correctly`);
  passed++;
} catch (err) {
  console.error(`❌ Sync fallback failed: Sync suppressed - ${err.message}`);
  failed++;
}

// Test 2: Sync is triggered and finds org
try {
  polarisMock.callCount = 0;
  mockLogs = [];
  const app = createMockApp({}); // Initially empty DB

  // Redefine the mock polaris.organizations to simulate loading data
  polarisMock.organizations = function(kind) {
    this.callCount++;
    if (kind === 'library') {
      app._injectOrgs({
        '99': { organizationId: '99', organizationCodeId: '2', displayName: 'Newly Synced', name: 'Newly Synced', parentOrganizationId: '1' }
      });
    }
    return [];
  };

  const actual = orgsWithMock.resolveParentLibrary(app, '99');
  assert.deepStrictEqual(actual, { branchOrgId: '99', libraryOrgId: '99', libraryOrgName: 'Newly Synced' }, 'Should resolve after sync');
  assert.ok(polarisMock.callCount > 0, 'Should have called sync');
  console.log(`✅ Sync fallback passed: Sync triggered and resolved org`);
  passed++;
} catch (err) {
  console.error(`❌ Sync fallback failed: Sync triggered and resolved org - ${err.message}`);
  failed++;
}

// Test 3: Sync is triggered but still fails to find org (with logger)
try {
  polarisMock.callCount = 0;
  mockLogs = [];
  const app = createMockApp({}); // Empty DB

  polarisMock.organizations = function() {
    this.callCount++;
    // Throw an error to simulate sync failure
    throw new Error("Polaris is down");
  };

  const actual = orgsWithMock.resolveParentLibrary(app, '88', { logger: mockLogger });
  assert.strictEqual(actual, null, 'Should return null if org still missing after sync attempt');
  assert.ok(polarisMock.callCount > 0, 'Should have called sync');
  assert.ok(mockLogs.length > 0 && mockLogs[0][0] === 'warn', 'Should have logged a warning');
  assert.ok(mockLogs[0].includes('Polaris organization sync failed'), 'Warning message should be correct');
  console.log(`✅ Sync fallback passed: Sync error handled and logged`);
  passed++;
} catch (err) {
  console.error(`❌ Sync fallback failed: Sync error handled - ${err.message}`);
  failed++;
}

// Restore original require just in case
require('module').prototype.require = originalRequire;

console.log(`\nAll tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
