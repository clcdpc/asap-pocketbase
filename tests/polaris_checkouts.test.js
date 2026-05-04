const assert = require('assert');

global.__hooks = require('path').resolve(__dirname, '../pb_hooks');

// Mock dependencies for polaris.js
const originalRequire = require('module').prototype.require;
require('module').prototype.require = function(moduleName) {
  if (moduleName.includes('lib/config.js')) {
    return {
      polaris: function() {
        return {
          host: "api.polaris.example.com",
          accessId: "TEST_ID",
          apiKey: "TEST_KEY",
          lang: "eng",
          appId: "100",
          orgId: "1"
        };
      }
    };
  }
  if (moduleName.includes('lib/crypto.js')) {
    return {
      hmacSha1Base64: function(key, msg) {
        return "mock_signature";
      }
    };
  }
  return originalRequire.apply(this, arguments);
};

// Mock global variables for error logging
global.$app = {
  logger: function() {
    return {
      error: function() {}
    }
  }
};

// Mock $http
let httpSendArgs = null;
let httpSendResult = {};
global.$http = {
  send: function(args) {
    httpSendArgs = args;
    return httpSendResult;
  }
};

const polaris = require('../lib/polaris.js');

console.log('Running tests for checkPatronCheckouts...');

let passed = 0;
let failed = 0;

// Test 1: Successful checkouts fetch
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      PatronItemsOutGetRows: [
        { BibID: 1, ItemRecordID: 101 },
        { BibID: 2, ItemRecordID: 102 }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const barcode = "123456789";

  const result = polaris.checkPatronCheckouts(staff, barcode);

  assert.strictEqual(Array.isArray(result), true);
  assert.strictEqual(result.length, 2);
  assert.strictEqual(result[0].BibID, 1);

  // Verify the HTTP request args
  assert.ok(httpSendArgs);
  assert.strictEqual(httpSendArgs.method, "GET");
  assert.ok(httpSendArgs.url.includes("/patron/123456789/itemsout/all"));
  assert.ok(httpSendArgs.url.includes("excludeecontent=true"));
  assert.strictEqual(httpSendArgs.headers["X-PAPI-AccessToken"], "mock_token");

  console.log('✅ Test case 1 (Successful checkouts fetch) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 1 failed:', err.stack);
  failed++;
}

// Test 2: Successful fetch but no items out
try {
  httpSendResult = {
    statusCode: 200,
    json: {} // No PatronItemsOutGetRows
  };

  const result = polaris.checkPatronCheckouts(null, "987654321");

  assert.strictEqual(Array.isArray(result), true);
  assert.strictEqual(result.length, 0);

  console.log('✅ Test case 2 (No items out) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 2 failed:', err.stack);
  failed++;
}

// Test 3: HTTP Error response
// The `send` function in polaris.js throws an error when statusCode >= 300.
// We just verify that an error is thrown, and we don't assume the exact string
// other than what polaris.js explicitly constructs.
try {
  httpSendResult = {
    statusCode: 500,
    json: { ErrorMessage: "Internal Server Error" }
  };

  let threwError = false;
  try {
    polaris.checkPatronCheckouts(null, "123");
  } catch (err) {
    threwError = true;
    assert.ok(err.message.includes("500"));
    assert.ok(err.message.includes("Internal Server Error"));
  }

  assert.strictEqual(threwError, true);
  console.log('✅ Test case 3 (HTTP Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 3 failed:', err.stack);
  failed++;
}

// Test 4: Polaris API Error (PAPIErrorCode < 0)
// The `send` function in polaris.js throws an error when PAPIErrorCode < 0.
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      PAPIErrorCode: -1234,
      ErrorMessage: "Invalid patron barcode"
    }
  };

  let threwError = false;
  try {
    polaris.checkPatronCheckouts(null, "INVALID");
  } catch (err) {
    threwError = true;
    assert.ok(err.message.includes("Invalid patron barcode"));
  }

  assert.strictEqual(threwError, true);
  console.log('✅ Test case 4 (Polaris API Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 4 failed:', err.stack);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
