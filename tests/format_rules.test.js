const assert = require('assert');
const { normalizeMessageBehavior, MESSAGE_BEHAVIORS } = require('../lib/format_rules.js');

const testCases = [
  // Valid inputs
  { value: MESSAGE_BEHAVIORS.NONE, fallback: 'any', expected: MESSAGE_BEHAVIORS.NONE },
  { value: MESSAGE_BEHAVIORS.EBOOK, fallback: 'any', expected: MESSAGE_BEHAVIORS.EBOOK },
  { value: MESSAGE_BEHAVIORS.EAUDIOBOOK, fallback: 'any', expected: MESSAGE_BEHAVIORS.EAUDIOBOOK },

  // Valid inputs with whitespace padding
  { value: `  ${MESSAGE_BEHAVIORS.EBOOK}  `, fallback: 'any', expected: MESSAGE_BEHAVIORS.EBOOK },
  { value: `\n${MESSAGE_BEHAVIORS.EAUDIOBOOK}\t`, fallback: 'any', expected: MESSAGE_BEHAVIORS.EAUDIOBOOK },

  // Invalid inputs where it should return fallback
  { value: 'invalid', fallback: MESSAGE_BEHAVIORS.EBOOK, expected: MESSAGE_BEHAVIORS.EBOOK },
  { value: 'some_other_value', fallback: MESSAGE_BEHAVIORS.EAUDIOBOOK, expected: MESSAGE_BEHAVIORS.EAUDIOBOOK },
  { value: 'none123', fallback: MESSAGE_BEHAVIORS.NONE, expected: MESSAGE_BEHAVIORS.NONE },

  // Invalid inputs where no fallback is provided (should default to NONE)
  { value: 'invalid', expected: MESSAGE_BEHAVIORS.NONE },
  { value: 'unknown', fallback: undefined, expected: MESSAGE_BEHAVIORS.NONE },
  { value: 'random', fallback: null, expected: MESSAGE_BEHAVIORS.NONE },
  { value: 'weird', fallback: '', expected: MESSAGE_BEHAVIORS.NONE },

  // Empty, null, or undefined values
  { value: null, fallback: MESSAGE_BEHAVIORS.EBOOK, expected: MESSAGE_BEHAVIORS.EBOOK },
  { value: undefined, fallback: MESSAGE_BEHAVIORS.EAUDIOBOOK, expected: MESSAGE_BEHAVIORS.EAUDIOBOOK },
  { value: '', fallback: MESSAGE_BEHAVIORS.EBOOK, expected: MESSAGE_BEHAVIORS.EBOOK },
  { value: '   ', fallback: MESSAGE_BEHAVIORS.EBOOK, expected: MESSAGE_BEHAVIORS.EBOOK }, // Whitespace only
  { value: null, expected: MESSAGE_BEHAVIORS.NONE },
  { value: undefined, expected: MESSAGE_BEHAVIORS.NONE },
  { value: '', expected: MESSAGE_BEHAVIORS.NONE },
];

console.log('Running tests for pb_hooks/lib/format_rules.js (normalizeMessageBehavior)...');

let passed = 0;
let failed = 0;

testCases.forEach((tc, index) => {
  const actual = normalizeMessageBehavior(tc.value, tc.fallback);

  try {
    assert.strictEqual(actual, tc.expected, `Test case ${index} failed: value="${tc.value}", fallback="${tc.fallback}"`);
    console.log(`✅ Test case ${index} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: ${tc.expected}`);
    console.error(`   Actual:   ${actual}`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
