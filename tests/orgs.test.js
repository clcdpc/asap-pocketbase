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

if (failed > 0) {
  process.exit(1);
}
