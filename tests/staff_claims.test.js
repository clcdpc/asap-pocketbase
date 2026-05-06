const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");

class MockRecord {
  constructor(collectionName, data) {
    this.collectionName = collectionName;
    this.data = Object.assign({}, data || {});
    this.id = this.data.id || collectionName + "_id";
  }
  collection() {
    return { name: this.collectionName };
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  getBool(key) {
    return !!this.data[key];
  }
  getString(key) {
    return this.data[key] || "";
  }
  email() {
    return this.data.email || "";
  }
}

function makeStaff(data) {
  return new MockRecord("staff_users", Object.assign({
    id: "staff1",
    role: "staff",
    username: "jane",
    displayName: "Jane",
    libraryOrgId: "10"
  }, data || {}));
}

function makeApp(request) {
  return {
    saved: [],
    findRecordById(collection, id) {
      if (collection === "title_requests" && id === request.id) return request;
      throw new Error("not found");
    },
    findRecordsByFilter(collection) {
      if (collection === "title_request_tags") return [];
      return [];
    },
    save(record) {
      this.saved.push(record);
    },
    logger() {
      return { error() {}, warn() {} };
    }
  };
}

function makeEvent(app, staff, id) {
  return {
    app,
    request: {
      pathValue(name) {
        return name === "id" ? id : "";
      }
    },
    requestInfo() {
      return { auth: staff, body: {} };
    },
    json(status, body) {
      return { status, body };
    }
  };
}

function makeRequest(data) {
  return new MockRecord("title_requests", Object.assign({
    id: "request1",
    status: "suggestion",
    libraryOrgId: "10",
    title: "A Book",
    created: "2026-05-01 10:00:00",
    updated: "2026-05-01 10:00:00"
  }, data || {}));
}

{
  const request = makeRequest();
  const staff = makeStaff();
  const app = makeApp(request);
  const res = staffRoutes.staffClaimTitleRequest(makeEvent(app, staff, request.id));
  assert.strictEqual(res.status, 200);
  assert.strictEqual(request.get("claimedByStaffUserId"), "staff1");
  assert.strictEqual(request.get("claimedByDisplayName"), "Jane");
  assert.ok(request.get("claimedAt"));
  assert.strictEqual(res.body.claimedByStaffUserId, "staff1");
}

{
  const request = makeRequest({ claimedByStaffUserId: "other", claimedByDisplayName: "Sam" });
  const staff = makeStaff();
  const app = makeApp(request);
  const res = staffRoutes.staffClaimTitleRequest(makeEvent(app, staff, request.id));
  assert.strictEqual(res.status, 409);
  assert.match(res.body.message, /already claimed by Sam/);
  assert.strictEqual(request.get("claimedByStaffUserId"), "other");
}

{
  const request = makeRequest({ claimedByStaffUserId: "staff1", claimedByDisplayName: "Jane", claimedAt: "2026-05-01 10:00:00" });
  const staff = makeStaff();
  const app = makeApp(request);
  const res = staffRoutes.staffUnclaimTitleRequest(makeEvent(app, staff, request.id));
  assert.strictEqual(res.status, 200);
  assert.strictEqual(request.get("claimedByStaffUserId"), "");
  assert.strictEqual(request.get("claimedByDisplayName"), "");
  assert.strictEqual(request.get("claimedAt"), "");
}

{
  const request = makeRequest({ claimedByStaffUserId: "other", claimedByDisplayName: "Sam" });
  const staff = makeStaff();
  const app = makeApp(request);
  const res = staffRoutes.staffUnclaimTitleRequest(makeEvent(app, staff, request.id));
  assert.strictEqual(res.status, 403);
  assert.strictEqual(request.get("claimedByStaffUserId"), "other");
}

{
  const request = makeRequest({ claimedByStaffUserId: "other", claimedByDisplayName: "Sam" });
  const admin = makeStaff({ role: "admin" });
  const app = makeApp(request);
  const res = staffRoutes.staffUnclaimTitleRequest(makeEvent(app, admin, request.id));
  assert.strictEqual(res.status, 200);
  assert.strictEqual(request.get("claimedByStaffUserId"), "");
}

{
  const request = makeRequest({ libraryOrgId: "20" });
  const staff = makeStaff({ libraryOrgId: "10" });
  const app = makeApp(request);
  const res = staffRoutes.staffClaimTitleRequest(makeEvent(app, staff, request.id));
  assert.strictEqual(res.status, 404);
}

console.log("Staff request claim tests passed.");
