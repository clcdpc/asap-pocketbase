const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(collectionName, data) {
    this.collectionName = typeof collectionName === "string" ? collectionName : (collectionName && collectionName.name) || "";
    this.data = Object.assign({}, data || {});
    this.id = this.data.id || "";
  }
  get(key) { return this.data[key]; }
  set(key, value) { this.data[key] = value; }
  getBool(key) { return !!this.data[key]; }
  getInt(key) { return parseInt(this.data[key], 10) || 0; }
  getString(key) { return this.data[key] == null ? "" : String(this.data[key]); }
  collection() { return { name: this.collectionName }; }
}

global.Record = MockRecord;
global.$os = { getenv() { return ""; } };

function createStaff(data) {
  return new MockRecord("staff_users", Object.assign({
    id: "staff_admin",
    role: "super_admin",
    libraryOrgId: "10",
    active: true
  }, data || {}));
}

function createEvent(app, auth, body, query) {
  const responses = [];
  return {
    app,
    responses,
    requestInfo() {
      return { auth, body: body || {}, query: query || {} };
    },
    json(code, payload) {
      responses.push({ code, payload });
      return { code, payload };
    }
  };
}

function createMockApp() {
  const db = {
    polaris_organizations: [
      new MockRecord("polaris_organizations", { id: "org_row_10", organizationId: "10" }),
      new MockRecord("polaris_organizations", { id: "org_row_20", organizationId: "20" })
    ],
    staff_users: [
      new MockRecord("staff_users", { id: "staff_admin", role: "super_admin", libraryOrgId: "10", active: true }),
      new MockRecord("staff_users", { id: "staff_a", role: "staff", libraryOrgId: "10", active: true }),
      new MockRecord("staff_users", { id: "staff_b", role: "staff", libraryOrgId: "10", active: true }),
      new MockRecord("staff_users", { id: "staff_c", role: "staff", libraryOrgId: "20", active: true })
    ],
    format_claim_rules: []
  };
  let seq = 1;

  function parseFilter(filter) {
    if (filter.includes("libraryOrgId = {:libraryOrgId}") && filter.includes("active = true")) {
      return function (row, params) {
        return String(row.get("libraryOrgId") || "") === String(params.libraryOrgId || "") && row.getBool("active");
      };
    }
    if (filter.includes("libraryOrgId = {:libraryOrgId}")) {
      return function (row, params) {
        return String(row.get("libraryOrgId") || "") === String(params.libraryOrgId || "");
      };
    }
    if (filter === "scope = 'system'") return function () { return false; };
    if (filter.includes("scope = 'library'")) return function () { return false; };
    return function () { return false; };
  }

  return {
    db,
    findCollectionByNameOrId(name) { return { name }; },
    findRecordById(collection, id) {
      const row = (db[collection] || []).find((r) => r.id === id);
      if (!row) throw new Error("not found");
      return row;
    },
    findFirstRecordByData(collection, field, value) {
      const row = (db[collection] || []).find((r) => String(r.get(field) || "") === String(value || ""));
      if (!row) throw new Error("not found");
      return row;
    },
    findFirstRecordByFilter() { throw new Error("not found"); },
    findRecordsByFilter(collection, filter, sortField, limit, offset, params) {
      const match = parseFilter(filter);
      const rows = (db[collection] || []).filter((row) => match(row, params || {}));
      if (sortField === "format") rows.sort((a, b) => String(a.get("format")).localeCompare(String(b.get("format"))));
      return rows;
    },
    save(record) {
      if (!record.id) record.id = `${record.collectionName}_${seq++}`;
      const rows = db[record.collectionName] || (db[record.collectionName] = []);
      const idx = rows.findIndex((r) => r.id === record.id);
      if (idx >= 0) rows[idx] = record;
      else rows.push(record);
    },
    delete(record) {
      const rows = db[record.collectionName] || [];
      const idx = rows.findIndex((r) => r.id === record.id);
      if (idx >= 0) rows.splice(idx, 1);
    },
    logger() { return { error() {}, warn() {} }; }
  };
}

const staffRoutes = require("../lib/staff_routes.js");

function loadRules(app, auth, orgId) {
  const e = createEvent(app, auth, {}, { orgId });
  const response = staffRoutes.getLibrarySettings(e);
  assert.strictEqual(response.code, 200);
  return response.payload.formatClaimRules;
}

function saveRules(app, auth, orgId, rules) {
  const e = createEvent(app, auth, { orgId, formatClaimRules: rules }, {});
  const response = staffRoutes.updateLibrarySettings(e);
  assert.strictEqual(response.code, 200);
}

(function run() {
  const app = createMockApp();
  global.$app = app;
  const admin = createStaff();

  const initialRules = [
    { format: "dvd", staffUserId: "staff_a" },
    { format: "book", staffUserId: "staff_b" }
  ];

  saveRules(app, admin, "10", initialRules);
  const loadedAfterFirstSave = loadRules(app, admin, "10");
  assert.deepStrictEqual(loadedAfterFirstSave.map((r) => ({ format: r.format, staffUserId: r.staffUserId })), [
    { format: "book", staffUserId: "staff_b" },
    { format: "dvd", staffUserId: "staff_a" }
  ]);

  const updatedRules = [
    { format: "dvd", staffUserId: "staff_b" }, // updated
    { format: "music_cd", staffUserId: "staff_a" }, // added
    { format: "book", staffUserId: "" } // cleared/deleted
  ];
  saveRules(app, admin, "10", updatedRules);
  const loadedAfterSecondSave = loadRules(app, admin, "10");
  assert.deepStrictEqual(loadedAfterSecondSave.map((r) => ({ format: r.format, staffUserId: r.staffUserId })), [
    { format: "dvd", staffUserId: "staff_b" },
    { format: "music_cd", staffUserId: "staff_a" }
  ]);

  saveRules(app, admin, "20", [{ format: "dvd", staffUserId: "staff_c" }]);
  const org10Rules = loadRules(app, admin, "10");
  const org20Rules = loadRules(app, admin, "20");
  assert.deepStrictEqual(org10Rules.map((r) => ({ format: r.format, staffUserId: r.staffUserId })), [
    { format: "dvd", staffUserId: "staff_b" },
    { format: "music_cd", staffUserId: "staff_a" }
  ]);
  assert.deepStrictEqual(org20Rules.map((r) => ({ format: r.format, staffUserId: r.staffUserId })), [
    { format: "dvd", staffUserId: "staff_c" }
  ]);

  saveRules(app, admin, "20", [{ format: "dvd", staffUserId: "" }]);
  assert.strictEqual(app.db.format_claim_rules.some((row) => row.get("libraryOrgId") === "20" && row.get("format") === "dvd"), false);
  assert.deepStrictEqual(loadRules(app, admin, "20"), []);

  console.log("Settings format auto-claim persistence tests passed.");
})();
