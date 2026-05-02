const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const config = require("../pb_hooks/lib/config.js");

function createMockApp(options) {
  let calls = [];
  return {
    calls,
    findRecordsByFilter: function(collectionName, filter, sortField, limit, offset, params) {
      calls.push({ collectionName, filter, params });

      if (options.shouldThrowSystem && filter.includes("scope = 'system'")) {
        throw new Error("System scope not found");
      }

      if (options.shouldThrowSystemFallback && filter.includes("id != ''")) {
        throw new Error("Fallback failed");
      }

      if (options.shouldThrowLibrary && filter.includes("scope = 'library'")) {
        throw new Error("Library scope not found");
      }

      if (filter.includes("scope = 'system'") && options.systemRecords) {
        return options.systemRecords;
      }
      if (filter.includes("id != ''") && options.fallbackRecords) {
        return options.fallbackRecords;
      }
      if (filter.includes("scope = 'library'") && options.libraryRecords) {
        return options.libraryRecords;
      }

      return [];
    },
    findFirstRecordByData: function(collection, field, value) {
      if (collection === "polaris_organizations" && field === "organizationId" && value === "org123") {
        return { id: "recordId123", get: () => {} };
      }
      throw new Error("Not found");
    }
  };
}

global.$app = createMockApp({});

function runTests() {
  console.log("Running scopedRows tests...");

  // Test 1: Happy path - both system and library records are found
  let app1 = createMockApp({
    systemRecords: [{ id: "sys1" }, { id: "sys2" }],
    libraryRecords: [{ id: "lib1" }]
  });

  let result1 = config.scopedRows(app1, "test_collection", "org123");
  assert.strictEqual(result1.length, 3);
  assert.strictEqual(result1[0].id, "sys1");
  assert.strictEqual(result1[2].id, "lib1");
  assert.strictEqual(app1.calls.length, 2);
  assert.strictEqual(app1.calls[0].filter, "scope = 'system'");
  assert.strictEqual(app1.calls[1].filter, "scope = 'library' && libraryOrganization = {:org}");

  // Test 2: System filter throws, fallback succeeds
  let app2 = createMockApp({
    shouldThrowSystem: true,
    fallbackRecords: [{ id: "fall1" }]
  });

  let result2 = config.scopedRows(app2, "test_collection", null); // no org
  assert.strictEqual(result2.length, 1);
  assert.strictEqual(result2[0].id, "fall1");
  assert.strictEqual(app2.calls.length, 2);
  assert.strictEqual(app2.calls[0].filter, "scope = 'system'");
  assert.strictEqual(app2.calls[1].filter, "id != ''");

  // Test 3: System filter throws, fallback throws (nested catch block coverage)
  let app3 = createMockApp({
    shouldThrowSystem: true,
    shouldThrowSystemFallback: true
  });

  let result3 = config.scopedRows(app3, "test_collection", null);
  assert.strictEqual(result3.length, 0);
  assert.strictEqual(app3.calls.length, 2);
  assert.strictEqual(app3.calls[0].filter, "scope = 'system'");
  assert.strictEqual(app3.calls[1].filter, "id != ''");

  // Test 4: Library filter throws
  let app4 = createMockApp({
    systemRecords: [{ id: "sys1" }],
    shouldThrowLibrary: true
  });

  let result4 = config.scopedRows(app4, "test_collection", "org123");
  assert.strictEqual(result4.length, 1);
  assert.strictEqual(result4[0].id, "sys1");
  assert.strictEqual(app4.calls.length, 2);
  assert.strictEqual(app4.calls[0].filter, "scope = 'system'");
  assert.strictEqual(app4.calls[1].filter, "scope = 'library' && libraryOrganization = {:org}");

  console.log("All scopedRows tests passed!");
}

runTests();
