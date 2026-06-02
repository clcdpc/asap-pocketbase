const assert = require('assert');
const http_utils = require('../lib/http_utils.js');
const { boolValue } = http_utils;

console.log('Running tests for boolValue...');

let passed = 0;
let failed = 0;

function runTest(name, fn) {
  try {
    fn();
    console.log(`✅ ${name} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${name} failed:`, err.message);
    failed++;
  }
}

// Test 1: undefined, null, "" return defaultValue
runTest('undefined returns defaultValue', () => {
  assert.strictEqual(boolValue(undefined, true), true);
  assert.strictEqual(boolValue(undefined, false), false);
});

runTest('null returns defaultValue', () => {
  assert.strictEqual(boolValue(null, true), true);
  assert.strictEqual(boolValue(null, false), false);
});

runTest('empty string returns defaultValue', () => {
  assert.strictEqual(boolValue("", true), true);
  assert.strictEqual(boolValue("", false), false);
});

// Test 2: strict booleans true and false return themselves
runTest('strict booleans return themselves', () => {
  assert.strictEqual(boolValue(true, false), true);
  assert.strictEqual(boolValue(false, true), false);
});

// Test 3: case-insensitive string parsing with whitespaces for truthy strings
runTest('truthy strings with variations', () => {
  const truthyStrings = ["true", "1", "on", "yes", " TRUE ", "  yes  ", "On", "  1  "];
  truthyStrings.forEach(str => {
    assert.strictEqual(boolValue(str, false), true, `Failed for string: "${str}"`);
  });
});

// Test 4: case-insensitive string parsing with whitespaces for falsy strings
runTest('falsy strings with variations', () => {
  const falsyStrings = ["false", "0", "off", "no", " FALSE ", "   no   ", "Off", "  0  "];
  falsyStrings.forEach(str => {
    assert.strictEqual(boolValue(str, true), false, `Failed for string: "${str}"`);
  });
});

// Test 5: numeric inputs 1 and 0
runTest('numeric inputs 1 and 0', () => {
  assert.strictEqual(boolValue(1, false), true);
  assert.strictEqual(boolValue(0, true), false);
});

// Test 6: fallback behavior for other values via !!value
runTest('fallback behavior for other values via !!value', () => {
  // Truthy objects/strings/numbers not in the explicit list
  assert.strictEqual(boolValue({}, false), true);
  assert.strictEqual(boolValue([], false), true);
  assert.strictEqual(boolValue("random", false), true);
  assert.strictEqual(boolValue(42, false), true);

  // Falsy value NaN
  assert.strictEqual(boolValue(NaN, true), false);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
