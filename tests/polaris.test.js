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

console.log('Running tests for polaris.js...');

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


// Test 5: placeHold error path (StatusType === 1)
try {
  httpSendResult = {
    statusCode: 200,
    json: { StatusType: 1, StatusValue: 10 }
  };
  const result = polaris.placeHold(null, 123, 456);
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.statusValue, 10);
  console.log('✅ Test case 5 (placeHold error path) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5 failed:', err.stack);
  failed++;
}

// Test 5b: placeHold does not auto-reply to StatusValue 6
try {
  let calls = [];
  global.$http.send = function(args) {
    calls.push(args);
    return { statusCode: 200, json: { StatusType: 3, StatusValue: 6, RequestGUID: "rg1" } };
  };
  const result = polaris.placeHold(null, 123, 456);
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.statusValue, 6);
  assert.strictEqual(calls.length, 1);
  global.$http.send = function(args) {
    httpSendArgs = args;
    return httpSendResult;
  };
  console.log('✅ Test case 5b (placeHold does not auto-reply to no-items conditional) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5b failed:', err.stack);
  failed++;
}

// Test 5c: holdability summary requires at least one holdable row
try {
  let result = polaris.summarizeHoldability([
    { Barcode: "1", ItemsTotal: 1, ItemsIn: 0, Holdable: false },
    { Barcode: "2", ItemsTotal: 1, ItemsIn: 1, Holdable: "false" }
  ]);
  assert.strictEqual(result.hasHoldableItems, false);

  result = polaris.summarizeHoldability([
    { Barcode: "3", ItemsTotal: 1, ItemsIn: 0, Holdable: "true" }
  ]);
  assert.strictEqual(result.hasHoldableItems, true);
  console.log('✅ Test case 5c (holdability summary) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5c failed:', err.stack);
  failed++;
}

// Test 6: patron name search builds protected PATNF query and normalizes rows
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      TotalRecordsFound: 2,
      PatronSearchRows: {
        PatronSearchRow: [
          {
            PatronID: "p1",
            Barcode: "29000000000001",
            NameFirst: "Jane",
            NameLast: "Smith",
            OrganizationName: "Main Library"
          },
          {
            PatronID: "p2",
            Barcode: "29000000000002",
            PatronFirstLastName: "Jane Smythe",
            OrganizationName: "Branch Library"
          }
        ]
      }
    }
  };

  const staff = { AccessToken: "mock_token", AccessSecret: "mock_secret" };
  const result = polaris.searchPatrons(staff, { query: "Jane Smith", limit: 10 });

  assert.strictEqual(result.status, "found");
  assert.strictEqual(result.totalMatches, 2);
  assert.strictEqual(result.results.length, 2);
  assert.strictEqual(result.results[0].barcode, "29000000000001");
  assert.strictEqual(result.results[0].name, "Jane Smith");
  assert.strictEqual(result.results[1].name, "Jane Smythe");
  assert.strictEqual(httpSendArgs.method, "GET");
  assert.ok(httpSendArgs.url.includes("/protected/v1/"));
  assert.ok(httpSendArgs.url.includes("/mock_token/search/patrons/boolean"));
  assert.ok(httpSendArgs.url.includes("q=PATNF%3D%22Jane%20Smith%22"));
  assert.ok(httpSendArgs.url.includes("sortby=PATNF"));
  assert.ok(httpSendArgs.url.includes("patronsperpage=10"));
  assert.strictEqual(httpSendArgs.headers["X-PAPI-AccessToken"], "mock_token");

  console.log('✅ Test case 6 (patron name search) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 6 failed:', err.stack);
  failed++;
}

// Test 7: patron name search maps Polaris no-results code to not_found
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      PAPIErrorCode: -1,
      ErrorMessage: "No records found"
    }
  };

  const result = polaris.searchPatrons({ AccessToken: "mock_token", AccessSecret: "mock_secret" }, { query: "No One" });
  assert.strictEqual(result.status, "not_found");
  assert.strictEqual(result.results.length, 0);

  console.log('✅ Test case 7 (patron search not found) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 7 failed:', err.stack);
  failed++;
}

// Test 8: BIB details map title and series from correct ElementIDs
try {
  httpSendResult = {
    statusCode: 200,
    json: {
      BibGetRows: [
        { ElementID: 35, Label: "Title:", Value: "Green is all around me! / Kathleen Connors." },
        { ElementID: 18, Label: "Author:", Value: "Connors, Kathleen, author." },
        { ElementID: 19, Label: "Series:", Value: "Colors in my world" },
        { ElementID: 3, Label: "Description:", Value: "pages cm." }
      ]
    }
  };

  const result = polaris.getBib({ AccessToken: "mock_token", AccessSecret: "mock_secret" }, "4230422");

  assert.strictEqual(result.title, "Green is all around me! / Kathleen Connors.");
  assert.strictEqual(result.author, "Connors, Kathleen, author.");
  assert.strictEqual(result.series, "Colors in my world");
  assert.strictEqual(result.description, "pages cm.");

  console.log('✅ Test case 8 (BIB detail title/series parsing) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 8 failed:', err.stack);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
