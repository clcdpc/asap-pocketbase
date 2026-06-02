const assert = require('assert');

global.__hooks = __dirname + '/../pb_hooks';

const duplicateMessages = require('../lib/duplicate_messages.js');

function runTests() {
  // Test null/undefined input
  assert.strictEqual(duplicateMessages.duplicateStatusKey(null), 'suggestion');
  assert.strictEqual(duplicateMessages.duplicateStatusKey(undefined), 'suggestion');

  // Test empty object
  assert.strictEqual(duplicateMessages.duplicateStatusKey({}), 'suggestion');

  // Test missing or empty status resolving to suggestion
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: '' }), 'suggestion');
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: ' ' }), 'suggestion');

  // Test valid statuses without closeReason
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'pending_hold' }), 'pending_hold');
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'outstanding_purchase' }), 'outstanding_purchase');

  // Test closed status without closeReason
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'closed' }), 'closed');

  // Test closed status with populated closeReason
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'closed', closeReason: 'rejected' }), 'rejected');
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'closed', closeReason: 'manual' }), 'manual');

  // Test non-closed status with closeReason (closeReason is ignored)
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: 'pending_hold', closeReason: 'rejected' }), 'pending_hold');

  // Test whitespace trimming
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: ' closed ', closeReason: ' manual ' }), 'manual');
  assert.strictEqual(duplicateMessages.duplicateStatusKey({ status: ' closed ', closeReason: ' ' }), 'closed');

  console.log('duplicateStatusKey tests passed.');
}

runTests();
