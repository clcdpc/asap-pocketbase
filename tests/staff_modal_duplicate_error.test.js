(async () => {
const assert = require('assert');
const fs = require('fs');
const path = require('path');

function extractFunction(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}`);
  let depth = 0;
  let opened = false;
  for (let i = start; i < source.length; i++) {
    const ch = source[i];
    if (ch === '{') { depth++; opened = true; }
    if (ch === '}') {
      depth--;
      if (opened && depth === 0) return source.slice(start, i + 1);
    }
  }
  throw new Error(`Could not parse ${name}`);
}

const source = fs.readFileSync(path.resolve(__dirname, '../pb_public/staff/js/modals.js'), 'utf8');
const actionErrorMessage = new Function([
  extractFunction(source, 'workflowStatusLabel'),
  extractFunction(source, 'duplicateOpenRequestMessage'),
  extractFunction(source, 'actionErrorMessage'),
  'return actionErrorMessage;'
].join('\n'))();

{
  const msg = actionErrorMessage(
    409,
    {
      code: 'duplicate_open_request',
      message: 'This patron already has an open request for this BIB ID.',
      duplicate: {
        title: 'Southpaw: music from and inspired by the motion picture.',
        status: 'pending_hold'
      }
    },
    '{"message":"This patron already has an open request for this BIB ID.","title":"Should not leak"}'
  );

  assert.ok(msg.includes('This patron already has an open request for this BIB ID.'));
  assert.ok(msg.includes('Existing request: Southpaw: music from and inspired by the motion picture. - Pending hold.'));
  assert.ok(msg.includes('This request was flagged. Choose another BIB, or close this request as duplicate if it should not continue.'));
  assert.ok(!msg.includes('Error updating suggestion'));
  assert.ok(!msg.includes('Should not leak'));
  assert.ok(!msg.includes('{'));
}

{
  const msg = actionErrorMessage(
    409,
    {
      code: 'duplicate_open_request',
      message: ''
    },
    '{"duplicate":{"title":"Should not leak"}}'
  );

  assert.ok(msg.includes('This patron already has an open request for this BIB ID.'));
  assert.ok(msg.includes('This request was flagged. Choose another BIB, or close this request as duplicate if it should not continue.'));
  assert.ok(!msg.includes('Error updating suggestion'));
  assert.ok(!msg.includes('Should not leak'));
  assert.ok(!msg.includes('{'));
}

{
  const msg = actionErrorMessage(400, { message: 'BIB ID is required.' }, '');
  assert.strictEqual(msg, 'Error updating suggestion (400): BIB ID is required.');
}

{
  // Test confirmDuplicateOpenRequestClose logic
  const source = fs.readFileSync(path.resolve(__dirname, '../pb_public/staff/js/modals.js'), 'utf8');
  
  // Mock dependencies for the test
  let showConfirmCalled = false;
  let closeDuplicateRequestCalled = false;
  const mockShowConfirm = async (title, msg) => {
    showConfirmCalled = true;
    return true; // Simulate confirmation
  };
  const mockCloseDuplicateRequest = async (id) => {
    closeDuplicateRequestCalled = true;
  };
  const mockShowToast = (msg, type) => {};

  const confirmDuplicateOpenRequestClose = new Function(
    'showConfirm', 'closeDuplicateRequest', 'showToast',
    'return async (err, id) => ' + extractFunction(source, 'confirmDuplicateOpenRequestClose').slice(extractFunction(source, 'confirmDuplicateOpenRequestClose').indexOf('{'))
  )(mockShowConfirm, mockCloseDuplicateRequest, mockShowToast);

  const confirmed = await confirmDuplicateOpenRequestClose(null, 'test-id');
  
  assert.strictEqual(confirmed, true);
  assert.strictEqual(showConfirmCalled, true);
  assert.strictEqual(closeDuplicateRequestCalled, true);
}

console.log('Staff modal duplicate error tests passed.');
})();
