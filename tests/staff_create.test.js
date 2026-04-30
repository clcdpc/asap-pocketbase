const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(collection) {
    this.collection = collection;
    this.data = {};
    this.id = "mock_" + Math.random().toString(16).slice(2);
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  setEmail(value) {
    this.data.email = value;
  }
  email() {
    return this.data.email || "";
  }
  setRandomPassword() {
    this.data.passwordSet = true;
  }
  setVerified(value) {
    this.data.verified = !!value;
  }
  getBool(key) {
    return !!this.data[key];
  }
  getString(key) {
    return this.data[key] || "";
  }
}

global.Record = MockRecord;

const records = require("../pb_hooks/lib/records.js");
const identity = require("../pb_hooks/lib/identity.js");

function appWithRows(rows, options = {}) {
  return {
    saved: [],
    findCollectionByNameOrId(name) {
      return { name };
    },
    findFirstRecordByData(collection, field, value) {
      const normalizedValue = String(value || "").toLowerCase();
      const found = rows.find(row => String(row.get(field) || "").toLowerCase() === normalizedValue);
      if (!found) throw new Error("not found");
      return found;
    },
    save(record) {
      if (options.throwUniqueOnSave) {
        throw new Error("UNIQUE constraint failed: staff_users.identityKey");
      }
      rows.push(record);
      this.saved.push(record);
    },
  };
}

function staff(data) {
  const record = new MockRecord({ name: "staff_users" });
  Object.keys(data).forEach(key => record.set(key, data[key]));
  return record;
}

function expectDuplicate(fn) {
  assert.throws(fn, err => err && err.code === 409 && err.duplicate === true);
}

console.log("Running staff create-only tests...");

{
  const existing = staff({
    username: "wes",
    domain: "library",
    identityKey: "library\\wes",
    role: "super_admin",
    email: "library.wes@staff.asap.local",
  });
  const app = appWithRows([existing]);

  expectDuplicate(() => records.createStaffUser(
    app,
    identity.parseStaffIdentity("  LIBRARY\\wes  ", "DEFAULT"),
    "Wes",
    { role: "staff", libraryOrgId: "2", libraryOrgName: "Branch" }
  ));

  assert.strictEqual(existing.get("role"), "super_admin");
  assert.strictEqual(app.saved.length, 0);
}

{
  const existing = staff({
    username: "sam",
    domain: "library",
    identityKey: "library\\sam",
    role: "admin",
    email: "sam@example.org",
  });
  const app = appWithRows([existing]);

  expectDuplicate(() => records.createStaffUser(
    app,
    identity.parseStaffIdentity("other", "LIBRARY"),
    "Other",
    { role: "staff", email: " SAM@example.org " }
  ));

  assert.strictEqual(existing.get("role"), "admin");
  assert.strictEqual(app.saved.length, 0);
}

{
  const app = appWithRows([]);
  const created = records.createStaffUser(
    app,
    identity.parseStaffIdentity("LIBRARY\\newuser", "DEFAULT"),
    "New User",
    { role: "staff", libraryOrgId: "2", libraryOrgName: "Branch" }
  );

  assert.strictEqual(app.saved.length, 1);
  assert.strictEqual(created.get("identityKey"), "library\\newuser");
  assert.strictEqual(created.get("role"), "staff");
  assert.strictEqual(created.get("libraryOrgId"), "2");
}

{
  const app = appWithRows([], { throwUniqueOnSave: true });
  expectDuplicate(() => records.createStaffUser(
    app,
    identity.parseStaffIdentity("LIBRARY\\race", "DEFAULT"),
    "Race User",
    { role: "staff" }
  ));
}

console.log("Staff create-only tests passed.");
