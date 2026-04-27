const assert = require('assert');
const { clone, normalizeFormat, normalizeMode, FORMAT_KEYS, FIELD_MODES } = require('../pb_hooks/lib/format_rules.js');

console.log('Running tests for pb_hooks/lib/format_rules.js...');

let passed = 0;
let failed = 0;

function runTest(name, fn) {
  try {
    fn();
    console.log(`✅ ${name}`);
    passed++;
  } catch (err) {
    console.error(`❌ ${name}`);
    console.error(`   ${err.stack || err.message}`);
    failed++;
  }
}

// Tests for clone()
runTest('clone() - should deeply clone an object', () => {
  const original = { a: 1, b: { c: 2 } };
  const cloned = clone(original);

  assert.notStrictEqual(cloned, original, 'Cloned object should be a different reference');
  assert.notStrictEqual(cloned.b, original.b, 'Nested objects should be different references');
  assert.deepStrictEqual(cloned, original, 'Cloned object should have same values');
});

runTest('clone() - should clone an array', () => {
  const original = [1, [2, 3], { a: 4 }];
  const cloned = clone(original);

  assert.notStrictEqual(cloned, original, 'Cloned array should be a different reference');
  assert.notStrictEqual(cloned[1], original[1], 'Nested arrays should be different references');
  assert.notStrictEqual(cloned[2], original[2], 'Nested objects should be different references');
  assert.deepStrictEqual(cloned, original, 'Cloned array should have same values');
});

runTest('clone() - handles simple types', () => {
  assert.strictEqual(clone("string"), "string");
  assert.strictEqual(clone(123), 123);
  assert.strictEqual(clone(null), null);
  assert.strictEqual(clone(true), true);
});

// Tests for normalizeFormat()
runTest('normalizeFormat() - should return the valid format directly', () => {
  FORMAT_KEYS.forEach(key => {
    assert.strictEqual(normalizeFormat(key), key);
  });
});

runTest('normalizeFormat() - should return "book" for invalid formats', () => {
  assert.strictEqual(normalizeFormat('invalid_format'), 'book');
  assert.strictEqual(normalizeFormat(''), 'book');
  assert.strictEqual(normalizeFormat(null), 'book');
  assert.strictEqual(normalizeFormat(undefined), 'book');
  assert.strictEqual(normalizeFormat(123), 'book');
});

runTest('normalizeFormat() - should trim inputs before checking', () => {
  assert.strictEqual(normalizeFormat('  audiobook_cd  '), 'audiobook_cd');
});

// Tests for normalizeMode()
runTest('normalizeMode() - should return valid modes directly', () => {
  Object.values(FIELD_MODES).forEach(mode => {
    assert.strictEqual(normalizeMode(mode, 'fallback'), mode);
  });
});

runTest('normalizeMode() - should apply fallback for invalid modes', () => {
  assert.strictEqual(normalizeMode('invalid', 'hidden'), 'hidden');
  assert.strictEqual(normalizeMode(null, 'required'), 'required');
  assert.strictEqual(normalizeMode('', 'optional'), 'optional');
});

runTest('normalizeMode() - should default to "optional" if fallback is not provided', () => {
  assert.strictEqual(normalizeMode('invalid'), 'optional');
  assert.strictEqual(normalizeMode(null), 'optional');
  assert.strictEqual(normalizeMode(undefined), 'optional');
});

runTest('normalizeMode() - should trim inputs before checking', () => {
  assert.strictEqual(normalizeMode('  hidden  ', 'optional'), 'hidden');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
