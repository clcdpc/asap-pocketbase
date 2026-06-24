const assert = require('assert');
const helpers = require('../lib/polaris/helpers.js');

console.log('Running tests for redactPayload in lib/polaris/helpers.js...');

let passed = 0;
let failed = 0;

// Test 1: Handle non-object payloads
try {
  assert.strictEqual(helpers.redactPayload(null), null);
  assert.strictEqual(helpers.redactPayload(undefined), undefined);
  assert.strictEqual(helpers.redactPayload("string"), "string");
  assert.strictEqual(helpers.redactPayload(123), 123);
  assert.strictEqual(helpers.redactPayload(true), true);

  console.log('✅ Test case 1 (Handle non-object payloads) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 1 failed:', err.stack);
  failed++;
}

// Test 2: Redact sensitive keys at root level
try {
  const payload = {
    Barcode: "123456789",
    Password: "mysecretpassword",
    EmailAddress: "test@example.com",
    NameFirst: "John",
    NameLast: "Doe",
    PhoneNumber: "555-1234",
    PublicID: "987"
  };

  const expected = {
    Barcode: "[REDACTED]",
    Password: "[REDACTED]",
    EmailAddress: "[REDACTED]",
    NameFirst: "[REDACTED]",
    NameLast: "[REDACTED]",
    PhoneNumber: "[REDACTED]",
    PublicID: "987"
  };

  const result = helpers.redactPayload(payload);
  assert.deepStrictEqual(result, expected);

  console.log('✅ Test case 2 (Redact sensitive keys at root level) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 2 failed:', err.stack);
  failed++;
}

// Test 3: Redact sensitive keys in nested structures (objects)
try {
  const payload = {
    PublicID: "987",
    Details: {
      Barcode: "123456789",
      NestedDetails: {
        Password: "mysecretpassword",
        SafeKey: "SafeValue"
      }
    }
  };

  const expected = {
    PublicID: "987",
    Details: {
      Barcode: "[REDACTED]",
      NestedDetails: {
        Password: "[REDACTED]",
        SafeKey: "SafeValue"
      }
    }
  };

  const result = helpers.redactPayload(payload);
  assert.deepStrictEqual(result, expected);

  console.log('✅ Test case 3 (Redact sensitive keys in nested objects) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 3 failed:', err.stack);
  failed++;
}

// Test 4: Redact sensitive keys in arrays
try {
  const payload = {
    Users: [
      { Barcode: "111", Name: "User1" },
      { Barcode: "222", EmailAddress: "user2@test.com" }
    ],
    Status: "Active"
  };

  const expected = {
    Users: [
      { Barcode: "[REDACTED]", Name: "User1" },
      { Barcode: "[REDACTED]", EmailAddress: "[REDACTED]" }
    ],
    Status: "Active"
  };

  const result = helpers.redactPayload(payload);
  assert.deepStrictEqual(result, expected);

  console.log('✅ Test case 4 (Redact sensitive keys in arrays) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 4 failed:', err.stack);
  failed++;
}

// Test 5: Verify original payload is not modified (pure function check)
try {
  const payload = { Barcode: "123456" };
  helpers.redactPayload(payload);
  assert.strictEqual(payload.Barcode, "123456");

  console.log('✅ Test case 5 (Original payload is not modified) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5 failed:', err.stack);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);
if (failed > 0) {
  process.exit(1);
}
