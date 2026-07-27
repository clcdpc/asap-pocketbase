const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const Module = require("module");
const originalRequire = Module.prototype.require;

let polarisRows = [];
let authCalls = 0;

Module.prototype.require = function(moduleName) {
  if (moduleName === "./polaris.js" || moduleName.endsWith("/lib/polaris.js") || moduleName.includes("lib/polaris.js")) {
    return {
      adminStaffAuth: () => {
        authCalls++;
        return { AccessToken: "token", AccessSecret: "secret" };
      },
      patronCodes: () => polarisRows
    };
  }
  return originalRequire.apply(this, arguments);
};

const patronCodes = require("../lib/patron_codes.js");

Module.prototype.require = originalRequire;

function mockRecord(initial) {
  const data = Object.assign({}, initial || {});
  return {
    id: data.id || "",
    get: key => data[key],
    set: (key, value) => { data[key] = value; },
    data
  };
}

function mockApp() {
  const rows = [];
  const settings = mockRecord({ id: "settings0000001" });
  return {
    rows,
    settings,
    findCollectionByNameOrId: name => ({ name }),
    findRecordById: (collection, id) => {
      if (collection === "system_settings" && id === "settings0000001") return settings;
      throw new Error("not found");
    },
    findFirstRecordByData: (collection, field, value) => {
      const row = rows.find(record => record.get(field) === value);
      if (!row) throw new Error("not found");
      return row;
    },
    save: record => {
      if (!rows.includes(record) && record !== settings) rows.push(record);
    }
  };
}

global.Record = function MockRecord() {
  return mockRecord();
};

assert.deepStrictEqual(
  patronCodes.normalizeRow({ PatronCodeID: 91, Description: "Adult" }),
  { patronCodeId: "91", description: "Adult", raw: { PatronCodeID: 91, Description: "Adult" } }
);
assert.strictEqual(patronCodes.normalizeRow({ Description: "No ID" }), null);

let app = mockApp();
polarisRows = [
  { PatronCodeID: 91, Description: "Adult" },
  { PatronCodeID: "91", Description: "Duplicate Adult" },
  { PatronCodeID: 92 }
];
authCalls = 0;
let result = patronCodes.syncPatronCodes(app);
assert.strictEqual(result.synced, 2);
assert.strictEqual(authCalls, 1);
assert.strictEqual(app.rows.length, 2);
assert.strictEqual(app.rows[0].get("description"), "Adult");
assert.strictEqual(app.settings.get("patronCodesSyncStatus"), "loaded");

assert.deepStrictEqual(patronCodes.splitAllowedIds("91, 92,, "), ["91", "92"]);
assert.deepStrictEqual(
  patronCodes.isEligible({ patronCodeEligibilityEnabled: true, allowedPatronCodeIds: "91" }, { PatronCodeID: 91 }),
  { allowed: true }
);
assert.strictEqual(
  patronCodes.isEligible({ patronCodeEligibilityEnabled: true, allowedPatronCodeIds: "91", patronCodeEligibilityMessage: "Blocked" }, { PatronCodeID: 92 }).allowed,
  false
);
assert.strictEqual(
  patronCodes.isEligible({ patronCodeEligibilityEnabled: true, allowedPatronCodeIds: "91" }, {}).allowed,
  true
);

console.log("patron_codes.test.js passed.");
