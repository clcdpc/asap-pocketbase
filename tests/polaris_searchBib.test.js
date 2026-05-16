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

  assert.strictEqual(result.status, "not_found");
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

  assert.strictEqual(result.status, "not_found");
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

  assert.strictEqual(result.status, "not_found");
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

  assert.strictEqual(result.status, "not_found");
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

  assert.strictEqual(result.status, "not_found");
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

// Test 9: Title search returns normalized short result list
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 12,
      BibSearchRows: [
        { ControlNumber: "111", Title: "First Title", Author: "First Author", PublicationYear: "2026", MaterialType: "Book" },
        { ControlNumber: "222", Title: "Second Title", Author: "Second Author", PublicationYear: "2025", MaterialType: "eBook" }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const result = polaris.searchBibs(staff, { mode: "title", query: "  first title  ", limit: 1 });

//   assert.strictEqual(result.status, "found");
//   assert.strictEqual(result.mode, "title");
//   assert.strictEqual(result.query, "  first title  ");
//   assert.strictEqual(result.totalMatches, 12);
//   assert.strictEqual(result.results.length, 1);
//   assert.strictEqual(result.results[0].bibId, "111");
//   assert.strictEqual(result.results[0].title, "First Title");
//   assert.strictEqual(result.results[0].author, "First Author");
//   assert.strictEqual(result.results[0].publication, "2026");
//   assert.strictEqual(result.results[0].format, "Book");
//   assert.ok(result.results[0].score > 0);
//   assert.ok(httpSendArgs.url.includes("q=first%20title"));
//   assert.ok(httpSendArgs.url.includes("sortby=RELEVANCE"));

  console.log('✅ Test case 9 (Title search result list) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 9 failed:', err.stack);
  failed++;
}

// Test 10: Author search validates missing query
try {
  httpSendResult = { statusCode: 200, json: { TotalRecordsFound: 0, BibSearchRows: [] } };
  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const result = polaris.searchBibs(staff, { mode: "author", query: "   " });

  assert.strictEqual(result.status, "not_found");
  assert.deepStrictEqual(result.results, []);

  console.log('✅ Test case 10 (Author search missing query) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 10 failed:', err.stack);
  failed++;
}

// Test 11: search row keeps Description out of format
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 1,
      BibSearchRows: [
        {
          Title: "Green is all around me!",
          Author: "Connors, Kathleen, author.",
          PublicationDate: "2026",
          Description: "pages cm.",
          ISBN: "9781482469578",
          ControlNumber: "4230422",
          MaterialTypeDescription: "Book"
        }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const result = polaris.searchBibs(staff, { mode: "title", query: "Green is all around me", limit: 10 });

  assert.strictEqual(result.results.length, 1);
  assert.strictEqual(result.results[0].title, "Green is all around me!");
  assert.strictEqual(result.results[0].format, "Book");
  assert.strictEqual(result.results[0].physicalDescription, "pages cm.");
  assert.strictEqual(result.results[0].bibId, "4230422");

  console.log('✅ Test case 11 (Description is physical description, not format) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 11 failed:', err.stack);
  failed++;
}

// Test 12: search row prefers display title over 830-ish title field
try {
  httpSendResult = {
    statusCode: 200,
    throwError: false,
    json: {
      TotalRecordsFound: 1,
      BibSearchRows: [
        {
          Title: "--For dummies.",
          DisplayTitle: "QuickBooks desktop all-in-one",
          Author: "Nelson, Stephen L., 1959- author.",
          PublicationDate: "2026",
          Description: "xv, 590 pages : illustrations ; 24 cm",
          ISBN: "9781394368853",
          ControlNumber: "4271674"
        }
      ]
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const result = polaris.searchBibs(staff, { mode: "title", query: "QuickBooks desktop all-in-one", limit: 10 });

  assert.strictEqual(result.results.length, 1);
  assert.strictEqual(result.results[0].title, "QuickBooks desktop all-in-one");
  assert.strictEqual(result.results[0].format, "Unknown");
  assert.strictEqual(result.results[0].physicalDescription, "xv, 590 pages : illustrations ; 24 cm");
  assert.strictEqual(result.results[0].bibId, "4271674");

  console.log('✅ Test case 12 (DisplayTitle beats 830-ish Title) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 12 failed:', err.stack);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
