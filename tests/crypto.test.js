const assert = require('assert');
const crypto = require('crypto');
const pbCrypto = require('../pb_hooks/lib/crypto.js');

function nodeHmacSha1Base64(secret, message) {
  return crypto.createHmac('sha1', secret).update(message).digest('base64');
}

function nodeSha1Hex(message) {
  return crypto.createHash('sha1').update(message).digest('hex');
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

const sha1TestCases = [
  '',
  'a',
  'message',
  'The quick brown fox jumps over the lazy dog',
  'The quick brown fox jumps over the lazy cog',
  'very long message '.repeat(100),
  'utf8-🚀🔥',
];

console.log('Running tests for pb_hooks/lib/crypto.js...');

let passed = 0;
let failed = 0;

console.log('\n--- hmacSha1Base64 ---');
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

console.log('\n--- sha1 ---');
sha1TestCases.forEach((tc, index) => {
  const expected = nodeSha1Hex(tc);
  const bytes = pbCrypto.utf8Bytes(tc);
  const hashBytes = pbCrypto.sha1(bytes);
  const actual = hashBytes.map(b => b.toString(16).padStart(2, '0')).join('');

  try {
    assert.strictEqual(actual, expected, `Test case ${index} failed: message="${tc.substring(0, 20)}"`);
    console.log(`✅ Test case ${index} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: ${expected}`);
    console.error(`   Actual:   ${actual}`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
