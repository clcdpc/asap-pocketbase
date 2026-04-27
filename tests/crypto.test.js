const assert = require('assert');
const crypto = require('crypto');
const pbCrypto = require('../pb_hooks/lib/crypto.js');

function nodeHmacSha1Base64(secret, message) {
  return crypto.createHmac('sha1', secret).update(message).digest('base64');
}

const testCases = [
  { secret: 'secret', message: 'message' },
  { secret: '', message: 'message' },
  { secret: 'secret', message: '' },
  { secret: '', message: '' },
  { secret: 'very long secret'.repeat(10), message: 'short message' },
  { secret: 'short', message: 'very long message'.repeat(100) },
  { secret: 'utf8-🚀', message: 'message-🔥' },
  { secret: 'key', message: 'The quick brown fox jumps over the lazy dog' }
];

const utf8TestCases = [
  // 1-byte
  { name: '1-byte lower bound (0x00)', str: '\x00' },
  { name: '1-byte upper bound (0x7F)', str: '\x7F' },
  { name: '1-byte typical', str: 'A' },

  // 2-byte
  { name: '2-byte lower bound (0x80)', str: '\x80' },
  { name: '2-byte upper bound (0x7FF)', str: '\u07FF' },
  { name: '2-byte typical', str: '£' },

  // 3-byte
  { name: '3-byte lower bound 1 (0x800)', str: '\u0800' },
  { name: '3-byte upper bound 1 (0xD7FF)', str: '\uD7FF' },
  { name: '3-byte lower bound 2 (0xE000)', str: '\uE000' },
  { name: '3-byte upper bound 2 (0xFFFF)', str: '\uFFFF' },
  { name: '3-byte typical', str: '€' },

  // 4-byte (surrogate pairs)
  { name: '4-byte lower bound (0x10000)', str: '\uD800\uDC00' },
  { name: '4-byte upper bound (0x10FFFF)', str: '\uDBFF\uDFFF' },
  { name: '4-byte typical', str: '🚀' },

  // Mixed string
  { name: 'Mixed boundary string', str: '\x00\x7F\x80\u07FF\u0800\uD7FF\uE000\uFFFF\uD800\uDC00\uDBFF\uDFFF' }
];

console.log('Running tests for pb_hooks/lib/crypto.js...');

let passed = 0;
let failed = 0;

console.log('--- hmacSha1Base64 Tests ---');
testCases.forEach((tc, index) => {
  const expected = nodeHmacSha1Base64(tc.secret, tc.message);
  const actual = pbCrypto.hmacSha1Base64(tc.secret, tc.message);

  try {
    assert.strictEqual(actual, expected, `Test case ${index} failed: secret="${tc.secret.substring(0, 20)}", message="${tc.message.substring(0, 20)}"`);
    console.log(`✅ Test case ${index} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: ${expected}`);
    console.error(`   Actual:   ${actual}`);
    failed++;
  }
});

console.log('\n--- utf8Bytes Tests ---');
utf8TestCases.forEach((tc, index) => {
  const expected = Array.from(Buffer.from(tc.str, 'utf8'));
  const actual = pbCrypto.utf8Bytes(tc.str);

  try {
    assert.deepStrictEqual(actual, expected, `Test case ${index} failed: name="${tc.name}"`);
    console.log(`✅ Test case ${index} passed (${tc.name})`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case ${index} failed (${tc.name})`);
    console.error(`   Expected: ${JSON.stringify(expected)}`);
    console.error(`   Actual:   ${JSON.stringify(actual)}`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
