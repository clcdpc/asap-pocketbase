const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

// Mock PocketBase $app
const mockSettings = {
  get: (key) => {
    if (key === "formatIconUrlPattern") return mockSettings.storedValue;
    return "";
  },
  getInt: (key) => 0,
  getBool: (key) => false,
  storedValue: ""
};

global.Record = function(collection) {
  this.collection = collection;
  this.data = {};
  this.get = (key) => this.data[key];
  this.getInt = (key) => 0;
  this.getBool = (key) => false,
  this.set = (key, val) => { this.data[key] = val; };
};

global.$app = {
  findCollectionByNameOrId: (name) => ({ name }),
  findRecordById: (coll, id) => {
    if (coll === "system_settings") return mockSettings;
    throw new Error("not found");
  },
  findFirstRecordByFilter: (collection) => {
    if (collection === "system_settings") return mockSettings;
    return null;
  },
  logger: () => ({
    warn: () => {},
    error: () => {}
  }),
  save: () => {}
};

const settings = require("../lib/config/settings.js");
const normalization = require("../lib/config/normalization.js");

function testReadNormalization() {
  console.log("Running formatIconUrlPattern read normalization tests...");

  const defaultPattern = normalization.defaultFormatIconUrlPattern();

  // 1. Valid stored value
  mockSettings.storedValue = "https://catalog.example.org/formatid{MARCTypeOfMaterialID2}.gif";
  assert.strictEqual(settings.formatIconUrlPattern($app), mockSettings.storedValue);

  // 2. Unsafe stored value (XSS attempt)
  mockSettings.storedValue = "javascript:alert(1){id}";
  assert.strictEqual(settings.formatIconUrlPattern($app), defaultPattern);

  // 3. Data URL stored value
  mockSettings.storedValue = "data:text/html,{id}";
  assert.strictEqual(settings.formatIconUrlPattern($app), defaultPattern);

  // 4. Incomplete pattern (no placeholder)
  mockSettings.storedValue = "https://example.org/static/icon.gif";
  assert.strictEqual(settings.formatIconUrlPattern($app), defaultPattern);

  // 5. Blank stored value
  mockSettings.storedValue = "";
  assert.strictEqual(settings.formatIconUrlPattern($app), defaultPattern);

  // Test getSettings
  mockSettings.storedValue = "javascript:alert(1){id}";
  const currentSettings = settings.getSettings($app);
  assert.strictEqual(currentSettings.formatIconUrlPattern, defaultPattern);

  // Test librarySettings
  const currentLibSettings = settings.librarySettings($app, "10");
  assert.strictEqual(currentLibSettings.formatIconUrlPattern, defaultPattern);

  console.log("Format icon read normalization tests passed.");
}

testReadNormalization();
