const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(collection, initial) {
    this.collection = collection;
    this.data = Object.assign({}, initial || {});
    this.id = this.data.id || "rec_" + Math.random().toString(16).slice(2);
  }
  get(key) {
    return this.data[key];
  }
  getInt(key) {
    return parseInt(this.data[key], 10) || 0;
  }
  getBool(key) {
    return !!this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  email() {
    return this.data.email || "";
  }
}

global.Record = MockRecord;

const records = require("../lib/records.js");

function makeApp() {
  const rows = {
    title_requests: [],
    title_request_events: [],
  };
  const materialFormat = new MockRecord({ name: "material_formats" }, {
    id: "format-videogame",
    code: "videogame",
  });

  return {
    rows,
    findCollectionByNameOrId(name) {
      return { name };
    },
    findFirstRecordByData(collection, field, value) {
      if (collection === "polaris_organizations") throw new Error("not found");
      if (collection === "material_formats" && field === "code" && value === "videogame") return materialFormat;
      throw new Error("not found");
    },
    findFirstRecordByFilter(collection, filter, params) {
      if (collection === "material_formats" && params && params.code === "videogame") return materialFormat;
      throw new Error("not found");
    },
    findRecordsByFilter(collection) {
      if (collection === "title_requests") return [];
      return [];
    },
    save(record) {
      const collection = record.collection && record.collection.name;
      if (!rows[collection]) rows[collection] = [];
      rows[collection].push(record);
    },
    logger() {
      return { warn() {} };
    },
  };
}

function patron() {
  return new MockRecord({ name: "patron_users" }, {
    id: "patron1",
    barcode: "330621030000",
    email: "patron@example.test",
    libraryOrgId: "10",
    libraryOrgName: "Test Library",
  });
}

console.log("Running record format normalization tests...");

assert.strictEqual(records.normalizeFormat("4"), "dvd");
assert.strictEqual(records.normalizeFormat(" audiobook_cd "), "audiobook_cd");
assert.strictEqual(records.normalizeFormat("videogame"), "videogame");
assert.strictEqual(records.normalizeFormat(""), "book");

{
  const app = makeApp();
  const created = records.createSuggestion(app, patron(), {
    title: "Spear of Destiny",
    author: "",
    identifier: "",
    publication: "Already published",
    format: "videogame",
  }, { skipLimits: true });

  assert.strictEqual(created.get("format"), "videogame");
  assert.strictEqual(created.get("formatRef"), "format-videogame");
}

console.log("Record format normalization tests passed.");
