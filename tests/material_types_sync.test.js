const assert = require("assert");
const path = require("path");
const { MockRecord, createMockApp, interceptRequire } = require("./helpers/mock_pb.js");

// Mock environment
global.__hooks = path.resolve(__dirname, "../pb_hooks");

let mockSuperAdmin = true;
let mockAuth = { token: "mock_polaris_token" };
let mockMARCTypeOfMaterialsResult = null;
let mockMARCTypeOfMaterialsError = null;

const routeUtilsMock = {
  requireSuperAdminStaff: (e) => {
    return mockSuperAdmin;
  }
};

const polarisMock = {
  adminStaffAuth: () => {
    return mockAuth;
  },
  getMARCTypeOfMaterials: (auth) => {
    assert.strictEqual(auth, mockAuth);
    if (mockMARCTypeOfMaterialsError) {
      throw mockMARCTypeOfMaterialsError;
    }
    return mockMARCTypeOfMaterialsResult;
  }
};

interceptRequire({
  "lib/route_utils.js": routeUtilsMock,
  "lib/polaris.js": polarisMock,
  "lib/config.js": {},
  "lib/records.js": {},
  "lib/mail.js": {},
  "lib/orgs.js": {},
  "lib/additional_copies.js": {},
  "lib/format_claim_rules.js": {},
  "lib/staff/effective_library.js": {}
});

// Require the admin routes after mocking dependencies
const adminRoutes = require("../lib/staff/admin_routes.js");

function runTests() {
  console.log("Running staff material types sync tests...");

  // Mock settings record
  let savedRecord = null;
  let saveCount = 0;
  const mockSettingsRecord = new MockRecord({
    id: "polaris00000010",
    materialTypesCache: null,
    materialTypesCacheUpdated: null
  });

  const app = createMockApp({
    onFindRecordById: (coll, id) => {
      if (coll === "polaris_settings" && id === "polaris00000010") {
        return mockSettingsRecord;
      }
      throw new Error("Record not found");
    },
    onSave: (record) => {
      savedRecord = record;
      saveCount++;
    }
  });

  function makeEvent() {
    let jsonStatus = null;
    let jsonPayload = null;
    return {
      app: app,
      json: (status, payload) => {
        jsonStatus = status;
        jsonPayload = payload;
        return { status: jsonStatus, payload: jsonPayload };
      }
    };
  }

  // 1. Non-super-admin gets 403.
  mockSuperAdmin = false;
  mockMARCTypeOfMaterialsResult = { "1": { id: "1", description: "Book" } };
  mockMARCTypeOfMaterialsError = null;
  let e = makeEvent();
  let res = adminRoutes.staffMaterialTypesSync(e);
  assert.strictEqual(res.status, 403);
  assert.strictEqual(res.payload.success, false);
  assert.strictEqual(res.payload.message, "Super admin access required");

  // Reset roles
  mockSuperAdmin = true;

  // 2. Super admin route calls polaris.getMARCTypeOfMaterials and saves successfully.
  savedRecord = null;
  saveCount = 0;
  mockMARCTypeOfMaterialsResult = {
    "1": { id: "1", id2: "01", searchCode: "bks", description: "Book" },
    "33": { id: "33", id2: "33", searchCode: "dvd", description: "DVD" }
  };
  mockMARCTypeOfMaterialsError = null;
  mockSettingsRecord.set("materialTypesCache", null);
  mockSettingsRecord.set("materialTypesCacheUpdated", null);

  e = makeEvent();
  res = adminRoutes.staffMaterialTypesSync(e);
  assert.strictEqual(res.status, 200);
  assert.strictEqual(res.payload.success, true);
  assert.strictEqual(res.payload.count, 2);
  assert.strictEqual(saveCount, 1);
  assert.deepStrictEqual(savedRecord.get("materialTypesCache"), {
    version: 2,
    rows: mockMARCTypeOfMaterialsResult
  });
  assert.ok(savedRecord.get("materialTypesCacheUpdated"));

  // 3. Empty return gives 400.
  savedRecord = null;
  saveCount = 0;
  mockMARCTypeOfMaterialsResult = {};
  mockMARCTypeOfMaterialsError = null;

  e = makeEvent();
  res = adminRoutes.staffMaterialTypesSync(e);
  assert.strictEqual(res.status, 400);
  assert.strictEqual(res.payload.success, false);
  assert.strictEqual(res.payload.message, "No material types returned from Polaris.");
  assert.strictEqual(saveCount, 0);

  // Null return gives 400.
  mockMARCTypeOfMaterialsResult = null;
  e = makeEvent();
  res = adminRoutes.staffMaterialTypesSync(e);
  assert.strictEqual(res.status, 400);
  assert.strictEqual(res.payload.success, false);
  assert.strictEqual(res.payload.message, "No material types returned from Polaris.");
  assert.strictEqual(saveCount, 0);

  // 4. Polaris error gives 400 with message.
  mockMARCTypeOfMaterialsResult = null;
  mockMARCTypeOfMaterialsError = new Error("Polaris API down");

  e = makeEvent();
  res = adminRoutes.staffMaterialTypesSync(e);
  assert.strictEqual(res.status, 400);
  assert.strictEqual(res.payload.success, false);
  assert.strictEqual(res.payload.message, "Polaris API down");
  assert.strictEqual(saveCount, 0);

  console.log("All staff material types sync tests passed successfully!");
}

runTests();
