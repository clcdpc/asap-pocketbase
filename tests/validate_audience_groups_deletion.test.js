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

// 1. Test scenario where ui.ageGroups is undefined (should return immediately)
runTest('ui.ageGroups is undefined', () => {
  let findRecordsByFilterCalled = false;
  const app = {
    findRecordsByFilter: () => {
      findRecordsByFilterCalled = true;
      return [];
    }
  };

  staffRoutes.validateAudienceGroupsDeletion(app, "system", "", {});
  assert.strictEqual(findRecordsByFilterCalled, false);
});

// 2. Test scenario where group is deleted but NOT in use
runTest('group is deleted but NOT in use', () => {
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(collection, "audience_groups");
      assert.strictEqual(filter, "scope = 'system'");
      return [
        new MockRecord({ id: "group_1_id", code: "group_1", label: "Group 1" }),
        new MockRecord({ id: "group_2_id", code: "group_2", label: "Group 2" })
      ];
    },
    findFirstRecordByFilter: (collection, filter, params) => {
      assert.strictEqual(collection, "title_requests");
      return null; // Not in use
    }
  };

  const ui = {
    ageGroups: ["Group 1"] // Group 2 is being deleted
  };

  assert.doesNotThrow(() => {
    staffRoutes.validateAudienceGroupsDeletion(app, "system", "", ui);
  });
});

// 3. Test scenario where group is deleted and IS in use
runTest('group is deleted and IS in use', () => {
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      return [
        new MockRecord({ id: "group_1_id", code: "group_1", label: "Group 1" }),
        new MockRecord({ id: "group_2_id", code: "group_2", label: "Group 2" })
      ];
    },
    findFirstRecordByFilter: (collection, filter, params) => {
      if (collection === "title_requests" && filter.includes("audienceGroup = {:p0}")) {
        return new MockRecord({ id: "req1", audienceGroup: "group_2_id" });
      }
      return null;
    }
  };

  const ui = {
    ageGroups: ["Group 1"] // Group 2 is being deleted
  };

  let errorThrown = null;
  try {
    staffRoutes.validateAudienceGroupsDeletion(app, "system", "", ui);
  } catch (e) {
    errorThrown = e;
  }

  assert.ok(errorThrown !== null, "Expected an error to be thrown");
  assert.strictEqual(errorThrown.code, 400);
  assert.ok(errorThrown.message.includes("is currently in use by existing requests"), "Error message should mention it's in use");
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
    ageGroups: ["Group 1"]
  };

  staffRoutes.validateAudienceGroupsDeletion(app, "library", "org123", ui);
  assert.deepStrictEqual(passedParams, { org: "org123" });
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
