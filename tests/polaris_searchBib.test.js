const assert = require('assert');

global.__hooks = require('path').resolve(__dirname, '../pb_hooks');

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

global.$app = {
  logger: function() {
    return {
      error: function() {},
      warn: function() {}
    }
  }
};

let httpSendArgs = null;
let httpSendResult = {};
global.$http = {
  send: function(args) {
    httpSendArgs = args;
    if (httpSendResult.throwError) {
        throw new Error(httpSendResult.errorMessage);
    }
    return httpSendResult;
  }
};

const polaris = require('../lib/polaris.js');

let passed = 0;
let failed = 0;

console.log('Running tests for searchBib...');

// Test 1: HTTP Error response
try {
  httpSendResult = {
    statusCode: 500,
    json: { ErrorMessage: "Internal Server Error" }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "error");
  assert.ok(result.error.includes("500"));
  assert.ok(result.error.includes("Internal Server Error"));
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 1 (HTTP Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 1 failed:', err.stack);
  failed++;
}

// Test 2: Polaris API Error (PAPIErrorCode < 0)
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      PAPIErrorCode: -1234,
      ErrorMessage: "Invalid API query"
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "error");
  assert.ok(result.error.includes("Invalid API query"));
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 2 (Polaris API Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 2 failed:', err.stack);
  failed++;
}

// Test 3: Network level throw error
try {
  httpSendResult = {
    throwError: true,
    errorMessage: "Network connection refused"
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "error");
  assert.ok(result.error.includes("Network connection refused"));
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 3 (Network throw Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 3 failed:', err.stack);
  failed++;
}

// Test 4: Invalid Identifier handled before fetch
try {
  httpSendResult = {
    statusCode: 200,
    json: {}
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  // empty string
  const identifier = "";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "error");
  assert.ok(result.error);
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 4 (Invalid Identifier Error) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 4 failed:', err.stack);
  failed++;
}

// Test 5: Invalid Identifier (bad characters)
try {
  httpSendResult = {
    statusCode: 200,
    json: {}
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  // invalid characters
  const identifier = "!!!";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "error");
  assert.ok(result.error);
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 5 (Invalid Identifier Characters) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5 failed:', err.stack);
  failed++;
}

// Test 6: Happy path searchBib
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 1,
      QueryResultRetainedUntil: "2023-01-01T00:00:00Z",
      BibSearchRows: [
        { ControlNumber: "9876543" }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "found");
  assert.strictEqual(result.error, "");
  assert.strictEqual(result.bibId, "9876543");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 1);

  console.log('✅ Test case 6 (Happy Path) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 6 failed:', err.stack);
  failed++;
}

// Test 7: Not found searchBib
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 0,
      QueryResultRetainedUntil: "2023-01-01T00:00:00Z",
      BibSearchRows: []
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "not_found");
  assert.strictEqual(result.error, "");
  assert.strictEqual(result.bibId, "");
  assert.strictEqual(result.multipleMatches, false);
  assert.strictEqual(result.totalMatches, 0);

  console.log('✅ Test case 7 (Not Found) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 7 failed:', err.stack);
  failed++;
}

// Test 8: Multiple matches searchBib
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 2,
      QueryResultRetainedUntil: "2023-01-01T00:00:00Z",
      BibSearchRows: [
        { ControlNumber: "9876543" },
        { ControlNumber: "1234567" }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const identifier = "123456789";

  const result = polaris.searchBib(staff, identifier);

  assert.strictEqual(result.status, "found");
  assert.strictEqual(result.error, "");
  assert.strictEqual(result.bibId, "9876543");
  assert.strictEqual(result.multipleMatches, true);
  assert.strictEqual(result.totalMatches, 2);

  console.log('✅ Test case 8 (Multiple Matches) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 8 failed:', err.stack);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
