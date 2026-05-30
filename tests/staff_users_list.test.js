const assert = require("assert");
const path = require("path");
const { MockRecord, createMockApp } = require("./helpers/mock_pb.js");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const usersRoutes = require("../lib/staff/users_routes.js");
const records = require("../lib/records.js");

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

// 1. Non-admin gets 403
runTest("non-admin staff gets 403", () => {
  const mockApp = createMockApp();
  const e = {
    app: mockApp,
    requestInfo: () => ({
      auth: {
        collection: () => ({ name: "staff_users" }),
        get: (k) => {
          if (k === "role") return "staff";
          return "";
        }
      },
      query: {}
    }),
    json: (status, data) => {
      assert.strictEqual(status, 403);
      assert.strictEqual(data.message, "Admin access required");
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(e);
});

// 2. Admin own-library scoping & Admin cannot force another orgId
runTest("admin own-library scoping, admin cannot force another orgId", () => {
  let filterCalled = "";
  let paramsCalled = null;

  const mockApp = createMockApp({
    onFindRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(collection, "staff_users");
      filterCalled = filter;
      paramsCalled = params;
      return [
        new MockRecord({ id: "staff-1", username: "staff1", libraryOrgId: "jpl", role: "staff" })
      ];
    }
  });

  // Admin from JPL requests with no target orgId
  const e1 = {
    app: mockApp,
    requestInfo: () => ({
      auth: {
        collection: () => ({ name: "staff_users" }),
        get: (k) => {
          if (k === "role") return "admin";
          if (k === "libraryOrgId") return "jpl";
          return "";
        }
      },
      query: {}
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "libraryOrgId = {:adminLibraryOrgId}");
      assert.deepStrictEqual(paramsCalled, { adminLibraryOrgId: "jpl" });
      assert.strictEqual(data.users.length, 1);
      assert.strictEqual(data.users[0].id, "staff-1");
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(e1);

  // Admin from JPL requests orgId "gen" (cannot force another library)
  const e2 = {
    app: mockApp,
    requestInfo: () => ({
      auth: {
        collection: () => ({ name: "staff_users" }),
        get: (k) => {
          if (k === "role") return "admin";
          if (k === "libraryOrgId") return "jpl";
          return "";
        }
      },
      query: { orgId: "gen" }
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "libraryOrgId = {:adminLibraryOrgId}");
      assert.deepStrictEqual(paramsCalled, { adminLibraryOrgId: "jpl" });
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(e2);
});

// 3. Super admin all view (no orgId, orgId=all, orgId=system)
runTest("super_admin all view (no orgId, orgId=all, orgId=system)", () => {
  let filterCalled = "";

  const mockApp = createMockApp({
    onFindRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(collection, "staff_users");
      filterCalled = filter;
      return [
        new MockRecord({ id: "staff-1", username: "staff1", libraryOrgId: "jpl", role: "staff" }),
        new MockRecord({ id: "super-1", username: "super1", libraryOrgId: "system", role: "super_admin" })
      ];
    }
  });

  const baseAuth = {
    collection: () => ({ name: "staff_users" }),
    get: (k) => {
      if (k === "role") return "super_admin";
      if (k === "libraryOrgId") return "system";
      return "";
    }
  };

  // Test Case A: no orgId
  const eNoOrg = {
    app: mockApp,
    requestInfo: () => ({
      auth: baseAuth,
      query: {}
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "id != ''");
      assert.strictEqual(data.users.length, 2);
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(eNoOrg);

  // Test Case B: orgId=all
  const eAll = {
    app: mockApp,
    requestInfo: () => ({
      auth: baseAuth,
      query: { orgId: "all" }
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "id != ''");
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(eAll);

  // Test Case C: orgId=system
  const eSystem = {
    app: mockApp,
    requestInfo: () => ({
      auth: baseAuth,
      query: { orgId: "system" }
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "id != ''");
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(eSystem);
});

// 4. Super admin filtered view (with specific orgId)
runTest("super_admin filtered view (with specific orgId)", () => {
  let filterCalled = "";
  let paramsCalled = null;

  const mockApp = createMockApp({
    onFindRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
      assert.strictEqual(collection, "staff_users");
      filterCalled = filter;
      paramsCalled = params;
      return [
        new MockRecord({ id: "staff-1", username: "staff1", libraryOrgId: "jpl", role: "staff" }),
        new MockRecord({ id: "super-1", username: "super1", libraryOrgId: "system", role: "super_admin" })
      ];
    }
  });

  const e = {
    app: mockApp,
    requestInfo: () => ({
      auth: {
        collection: () => ({ name: "staff_users" }),
        get: (k) => {
          if (k === "role") return "super_admin";
          if (k === "libraryOrgId") return "system";
          return "";
        }
      },
      query: { orgId: "jpl" }
    }),
    json: (status, data) => {
      assert.strictEqual(status, 200);
      assert.strictEqual(filterCalled, "libraryOrgId = {:targetOrgId} || role = 'super_admin'");
      assert.deepStrictEqual(paramsCalled, { targetOrgId: "jpl" });
      assert.strictEqual(data.users.length, 2);
      return { status, data };
    }
  };
  usersRoutes.staffUsersList(e);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);
if (failed > 0) {
  process.exit(1);
}
