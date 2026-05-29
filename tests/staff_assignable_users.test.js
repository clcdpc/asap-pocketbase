const assert = require("assert");
const path = require("path");
const { MockRecord, createMockApp, interceptRequire } = require("./helpers/mock_pb.js");

// Mock environment
global.__hooks = path.resolve(__dirname, "../pb_hooks");

const mockRecords = {
  listStaffUsers: (app) => [
    new MockRecord({ id: "jpl-1", username: "jpl1", displayName: "JPL One", active: true, libraryOrgId: "jpl", identityKey: "secret-1" }),
    new MockRecord({ id: "jpl-2", username: "jpl2", displayName: "JPL Two", active: false, libraryOrgId: "jpl" }),
    new MockRecord({ id: "gen-1", username: "gen1", displayName: "Gen One", active: true, libraryOrgId: "gen" }),
    new MockRecord({ id: "super-1", username: "super1", displayName: "Super One", active: true, role: "super_admin", libraryOrgId: "" })
  ],
  titleRequestToJson: (r) => ({ id: r.id })
};

interceptRequire({
  "lib/records.js": mockRecords
});

const usersRoutes = require("../lib/staff/users_routes.js");

function testAssignableUsers() {
  console.log("Running staff assignable-users tests...");

  const mockApp = createMockApp({
    onError: (msg, ...args) => console.error("MOCK APP ERROR:", msg, ...args)
  });
  mockApp.findRecordById = (coll, id) => {
    if (coll === "title_requests" && id === "req-1") return new MockRecord({ id: "req-1", libraryOrgId: "jpl" });
    if (coll === "additional_copy_requests" && id === "copy-1") return new MockRecord({ id: "copy-1", libraryOrgId: "jpl" });
    throw new Error("Not found");
  };

  // JPL staff requests users for a JPL request
  const e = {
    app: mockApp,
    requestInfo: () => ({
      auth: { collection: () => ({ name: "staff_users" }), id: "jpl-1", get: (k) => k === "libraryOrgId" ? "jpl" : "" },
      query: { type: "title_request", id: "req-1" }
    }),
    json: (status, data) => {
      if (status !== 200) {
        console.error("DEBUG staffAssignableUsersList failed:", status, data);
      }
      assert.strictEqual(status, 200);
      assert.strictEqual(data.users.length, 2, "Should include JPL staff and Super Admin");
      assert.ok(data.users.some(u => u.id === "jpl-1"));
      assert.ok(data.users.some(u => u.id === "super-1"));
      assert.strictEqual(data.users[0].identityKey, undefined, "Should not expose identityKey");
      return { status, data };
    }
  };

  usersRoutes.staffAssignableUsersList(e);

  // JPL staff requests users for a GEN request (forbidden)
  const eForbidden = {
    app: mockApp,
    requestInfo: () => ({
      auth: { collection: () => ({ name: "staff_users" }), id: "jpl-1", get: (k) => k === "libraryOrgId" ? "jpl" : "" },
      query: { type: "title_request", id: "gen-req-1" }
    }),
    json: (status, data) => {
      assert.strictEqual(status, 404);
      return { status, data };
    }
  };
  mockApp.findRecordById = (coll, id) => {
    if (id === "gen-req-1") return new MockRecord({ id: "gen-req-1", libraryOrgId: "gen" });
    return null;
  };

  usersRoutes.staffAssignableUsersList(eForbidden);

  console.log("Assignable users tests passed.");
}

testAssignableUsers();
