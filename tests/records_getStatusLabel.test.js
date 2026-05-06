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

const { getStatusLabel, STATUS } = require('../lib/records.js');

const testCases = [
  {
    name: 'STATUS.SUGGESTION',
    input: STATUS.SUGGESTION,
    expected: 'Suggestions'
  },
  {
    name: 'STATUS.PENDING_HOLD',
    input: STATUS.PENDING_HOLD,
    expected: 'Pending Hold'
  },
  {
    name: 'STATUS.HOLD_PLACED',
    input: STATUS.HOLD_PLACED,
    expected: 'Hold Placed'
  },
  {
    name: 'STATUS.OUTSTANDING_PURCHASE',
    input: STATUS.OUTSTANDING_PURCHASE,
    expected: 'Pending Purchase'
  },
  {
    name: 'STATUS.CLOSED',
    input: STATUS.CLOSED,
    expected: 'Closed'
  },
  {
    name: 'fallback: unknown status returns itself',
    input: 'unknown_status_123',
    expected: 'unknown_status_123'
  },
  {
    name: 'fallback: empty string returns empty string',
    input: '',
    expected: ''
  },
  {
    name: 'fallback: null returns null',
    input: null,
    expected: null
  },
  {
    name: 'fallback: undefined returns undefined',
    input: undefined,
    expected: undefined
  }
];

console.log('Running tests for getStatusLabel in pb_hooks/lib/records.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc) => {
  const actual = getStatusLabel(tc.input);

  try {
    assert.strictEqual(actual, tc.expected, `Test case "${tc.name}" failed: input="${tc.input}"`);
    console.log(`✅ Test case "${tc.name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: "${tc.expected}"`);
    console.error(`   Actual:   "${actual}"`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}

  // Restore original require to avoid polluting the global environment
  Module.prototype.require = originalRequire;
