const assert = require("assert");

// Mock __hooks globally for the required modules
global.__hooks = __dirname + "/../pb_hooks";

const Module = require('module');
const originalRequire = Module.prototype.require;

// Mock dependencies of staff_routes.js
let configMock = {
  findOrganization: (app, orgId) => {
    if (orgId === "org123") return { id: "org123", name: "Test Org" };
    return null;
  }
};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return configMock;
  }
  if (moduleName.includes("lib/identity.js")) {
    return {};
  }
  if (moduleName.includes("lib/orgs.js")) {
    return {};
  }
  if (moduleName.includes("lib/polaris.js")) {
    return {};
  }
  if (moduleName.includes("lib/records.js")) {
    return {};
  }
  if (moduleName.includes("lib/route_utils.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const staffRoutes = require("../lib/staff_routes.js");

// Restore original require after importing the module under test
Module.prototype.require = originalRequire;

let passed = 0;
let failed = 0;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ Test case "${name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case "${name}" failed`);
    console.error(`   ${err.stack || err.message || err}`);
    failed++;
  }
}

class MockRecord {
  constructor(data) {
    this.data = data;
    this.id = data.id || Math.random().toString(36).slice(2);
  }
  get(key) {
    return this.data[key];
  }
}

// 1. Test scenario where ui.formatLabels is undefined (should return immediately)
runTest('ui.formatLabels is undefined', () => {
  let findRecordsByFilterCalled = false;
  const app = {
    findRecordsByFilter: () => {
      findRecordsByFilterCalled = true;
      return [];
    }
  };

  staffRoutes.validateMaterialFormatsDeletion(app, "system", "", {});
  assert.strictEqual(findRecordsByFilterCalled, false);
});

// 2. Test scenario where format is deleted but NOT in use
runTest('format is deleted but NOT in use', () => {
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(collection, "material_formats");
      assert.strictEqual(filter, "scope != 'library'");
      return [
        new MockRecord({ id: "fmt_1_id", code: "book", label: "Book" }),
        new MockRecord({ id: "fmt_2_id", code: "dvd", label: "DVD" })
      ];
    },
    findFirstRecordByFilter: (collection, filter, params) => {
      assert.strictEqual(collection, "title_requests");
      return null; // Not in use
    }
  };

  const ui = {
    formatLabels: { "book": "Book" } // DVD is being deleted
  };

  assert.doesNotThrow(() => {
    staffRoutes.validateMaterialFormatsDeletion(app, "system", "", ui);
  });
});

// 3. Test scenario where format is deleted and IS in use
runTest('format is deleted and IS in use', () => {
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      return [
        new MockRecord({ id: "fmt_1_id", code: "book", label: "Book" }),
        new MockRecord({ id: "fmt_2_id", code: "dvd", label: "DVD" })
      ];
    },
    findFirstRecordByFilter: (collection, filter, params) => {
      if (collection === "title_requests" && filter === "format ?= {:formats}") {
        // Return a mock request that uses 'dvd'
        return new MockRecord({ id: "req1", format: "dvd" });
      }
      return null;
    }
  };

  const ui = {
    formatLabels: { "book": "Book" } // DVD is being deleted
  };

  let errorThrown = null;
  try {
    staffRoutes.validateMaterialFormatsDeletion(app, "system", "", ui);
  } catch (e) {
    errorThrown = e;
  }

  assert.ok(errorThrown !== null, "Expected an error to be thrown");
  assert.strictEqual(errorThrown.code, 400);
  assert.strictEqual(errorThrown.message, "Format 'DVD' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
});

// 4. Test scenario with library scope
runTest('library scope applies organization filter', () => {
  let passedParams = null;
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(filter, "scope = 'library' && libraryOrganization = {:org}");
      passedParams = params;
      return [];
    }
  };

  const ui = {
    formatLabels: { "book": "Book" }
  };

  staffRoutes.validateMaterialFormatsDeletion(app, "library", "org123", ui);
  assert.deepStrictEqual(passedParams, { org: "org123" });
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
