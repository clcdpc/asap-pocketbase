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

const { setRelation } = require('../lib/records.js');

// Restore original require to prevent test pollution
Module.prototype.require = originalRequire;

const testCases = [
  {
    name: 'valid related record with id',
    fieldName: 'someRef',
    relatedRecord: { id: 'rec123' },
    expectedValue: 'rec123'
  },
  {
    name: 'null related record',
    fieldName: 'someRef',
    relatedRecord: null,
    expectedValue: ''
  },
  {
    name: 'undefined related record',
    fieldName: 'someRef',
    relatedRecord: undefined,
    expectedValue: ''
  },
  {
    name: 'related record without id (edge case)',
    fieldName: 'someRef',
    relatedRecord: { other: 'data' },
    expectedValue: undefined
  }
];

console.log('Running tests for setRelation in pb_hooks/lib/records.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc) => {
  let setCalled = false;
  let setFieldName = null;
  let setValue = null;

  const mockRecord = {
    set: function(field, val) {
      setCalled = true;
      setFieldName = field;
      setValue = val;
    }
  };

  try {
    setRelation(mockRecord, tc.fieldName, tc.relatedRecord);

    assert.strictEqual(setCalled, true, `Test case "${tc.name}" failed: set() was not called`);
    assert.strictEqual(setFieldName, tc.fieldName, `Test case "${tc.name}" failed: fieldName mismatch`);
    assert.strictEqual(setValue, tc.expectedValue, `Test case "${tc.name}" failed: value mismatch`);

    console.log(`✅ Test case "${tc.name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: "${tc.expectedValue}"`);
    console.error(`   Actual:   "${setValue}"`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
