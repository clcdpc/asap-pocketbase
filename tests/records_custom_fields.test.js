const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const customFields = require("../lib/custom_fields.js");
const suggestions = require("../lib/records/suggestions.js");

const defs = customFields.normalizeDefinitions([
  { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
]);
const values = customFields.sanitizeSubmittedValues(
  { platform: "switch" },
  defs,
  { platform: { mode: "required" } }
);

assert.deepStrictEqual(values.platform, {
  label: "Platform",
  type: "select",
  value: "switch",
  displayValue: "Nintendo Switch"
});

function makeRecord(values) {
  return {
    id: "req1",
    values: Object.assign({
      title: "Game",
      author: "Studio",
      identifier: "",
      publication: "",
      status: "suggestion",
      format: "videogame",
      customFields: {}
    }, values || {}),
    get: function (name) {
      return this.values[name];
    },
    getBool: function (name) {
      return !!this.values[name];
    },
    set: function (name, value) {
      this.values[name] = value;
    }
  };
}

const record = makeRecord();
const app = {
  findRecordById: function (collectionName, id) {
    assert.strictEqual(collectionName, "title_requests");
    assert.strictEqual(id, "req1");
    return record;
  },
  findFirstRecordByData: function () {
    throw new Error("record not found");
  },
  findFirstRecordByFilter: function () {
    throw new Error("record not found");
  },
  save: function (rec) {
    assert.strictEqual(rec, record);
  }
};

suggestions.updateTitleRequest(app, "req1", { customFields: values }, "staff");
assert.deepStrictEqual(record.get("customFields"), values);

const json = suggestions.titleRequestToJson(record);
assert.deepStrictEqual(json.customFields, values);

console.log("records custom field tests passed");
