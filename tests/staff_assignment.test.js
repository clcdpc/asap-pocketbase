const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const staffRoutes = require("../lib/staff_routes.js");
const additionalCopyRoutes = require("../lib/staff/additional_copy_routes.js");
const routeUtils = require("../lib/route_utils.js");

// 1. Export tests
function testExports() {
  console.log("Running staff assignment export tests...");
  assert.strictEqual(typeof staffRoutes.staffAssignAdditionalCopy, "function", "staffAssignAdditionalCopy should be a function");
  assert.strictEqual(typeof staffRoutes.staffAssignTitleRequest, "function", "staffAssignTitleRequest should be a function");
  console.log("Staff assignment route exports test passed.");
}

// 2. Route tests
function testAdditionalCopyAssignment() {
  console.log("Running additional-copy assignment route tests...");

  // Mock record
  class MockRecord {
    constructor(data) {
      this.id = data.id || "id" + Math.random();
      this.data = data;
    }
    get(key) { return this.data[key]; }
    getBool(key) { return !!this.data[key]; }
    getInt(key) { return parseInt(this.data[key], 10) || 0; }
    set(key, val) { this.data[key] = val; }
  }

  global.DateTime = class {
    constructor() { return new Date().toISOString(); }
  };

  // Mock app
  const app = {
    findRecordById: (coll, id) => {
      if (coll === "additional_copy_requests" && id === "task1") {
        return new MockRecord({ id: "task1", status: "open", libraryOrgId: "10" });
      }
      if (coll === "staff_users" && id === "assignee1") {
        return new MockRecord({ id: "assignee1", libraryOrgId: "10", username: "assignee", active: true });
      }
      if (coll === "staff_users" && id === "actor1") {
        return new MockRecord({ id: "actor1", libraryOrgId: "10", username: "actor", active: true });
      }
      throw new Error("not found: " + coll + " " + id);
    },
    runInTransaction: (fn) => fn(app),
    logger: () => ({
      error: () => {},
      warn: () => {}
    }),
    save: () => {}
  };

  global.$app = app;

  // Mock event
  const e = {
    app: app,
    request: {
      pathValue: (key) => {
        if (key === "id") return "task1";
        return "";
      }
    },
    json: (status, body) => ({ status, body })
  };

  // Mock routeUtils.requireAuth to return actor
  const originalRequireAuth = routeUtils.requireAuth;
  routeUtils.requireAuth = () => app.findRecordById("staff_users", "actor1");

  // Mock routeUtils.body to return payload
  const originalBody = routeUtils.body;
  routeUtils.body = () => ({ assigneeId: "assignee1" });

  // Success case
  const res = additionalCopyRoutes.staffAssignAdditionalCopy(e);
  assert.strictEqual(res.status, 200);
  assert.strictEqual(res.body.id, "task1");

  // Error case: missing assigneeId
  routeUtils.body = () => ({});
  const res2 = additionalCopyRoutes.staffAssignAdditionalCopy(e);
  assert.strictEqual(res2.status, 400);
  assert.strictEqual(res2.body.message, "Assignee ID is required.");

  // Restore mocks
  routeUtils.requireAuth = originalRequireAuth;
  routeUtils.body = originalBody;

  console.log("Additional-copy assignment route tests passed.");
}

testExports();
testAdditionalCopyAssignment();
