const assert = require('assert');

// Mock __hooks globally for the required modules
global.__hooks = __dirname + "/../pb_hooks";

// Mock dependencies of records.js
const Module = require('module');
const originalRequire = Module.prototype.require;
Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return {};
  }
  if (moduleName.includes("lib/identity.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const { organizationByPolarisId } = require('../lib/records.js');

const testCases = [
  {
    name: 'empty orgId',
    orgId: '',
    setupApp: () => ({}),
    expected: null
  },
  {
    name: 'null orgId',
    orgId: null,
    setupApp: () => ({}),
    expected: null
  },
  {
    name: 'whitespace orgId',
    orgId: '   ',
    setupApp: () => ({}),
    expected: null
  },
  {
    name: 'successful lookup',
    orgId: '15',
    setupApp: () => ({
      findFirstRecordByData: (collection, field, value) => {
        assert.strictEqual(collection, "polaris_organizations");
        assert.strictEqual(field, "organizationId");
        assert.strictEqual(value, "15");
        return { id: "org123", organizationId: "15" };
      }
    }),
    expected: { id: "org123", organizationId: "15" }
  },
  {
    name: 'orgId gets trimmed',
    orgId: ' 42 ',
    setupApp: () => ({
      findFirstRecordByData: (collection, field, value) => {
        assert.strictEqual(value, "42");
        return { id: "org42", organizationId: "42" };
      }
    }),
    expected: { id: "org42", organizationId: "42" }
  },
  {
    name: 'findFirstRecordByData throws error',
    orgId: '99',
    setupApp: () => ({
      findFirstRecordByData: (collection, field, value) => {
        throw new Error("Database error");
      }
    }),
    expected: null
  }
];

console.log('Running tests for organizationByPolarisId in lib/records.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc) => {
  const app = tc.setupApp();
  try {
    const actual = organizationByPolarisId(app, tc.orgId);
    assert.deepStrictEqual(actual, tc.expected, `Test case "${tc.name}" failed: expected ${JSON.stringify(tc.expected)}, got ${JSON.stringify(actual)}`);
    console.log(`✅ Test case "${tc.name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case "${tc.name}" failed: ${err.message}`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
