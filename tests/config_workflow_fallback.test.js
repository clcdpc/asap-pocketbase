const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

function makeRecord(id, values) {
  return {
    id,
    values: Object.assign({}, values || {}),
    get(name) { return this.values[name]; },
    getBool(name) { return !!this.values[name]; },
    getInt(name) { return parseInt(this.values[name], 10) || 0; },
    set(name, value) { this.values[name] = value; }
  };
}

global.Record = function MockRecord(collection) {
  return makeRecord("", { _collection: collection && collection.name ? collection.name : "" });
};

const config = require("../lib/config.js");

function createApp(libraryWorkflowValues) {
  const systemWorkflow = makeRecord("workflow0000010", {
    scope: "system",
    additionalCopyTimeoutEnabled: true,
    additionalCopyTimeoutDays: 30
  });
  const libraryWorkflow = makeRecord("wf-library", Object.assign({
    scope: "library",
    libraryOrganization: "org-rec-100"
  }, libraryWorkflowValues || {}));
  return {
    findCollectionByNameOrId(name) { return { name }; },
    findRecordById(collectionName, id) {
      if (collectionName === "workflow_settings" && id === "workflow0000010") return systemWorkflow;
      throw new Error("not found");
    },
    findFirstRecordByData(collectionName, field, value) {
      if (collectionName === "polaris_organizations" && field === "organizationId" && String(value) === "100") {
        return makeRecord("org-rec-100", { organizationId: "100" });
      }
      throw new Error("not found");
    },
    findFirstRecordByFilter(collectionName, filter) {
      if (collectionName === "workflow_settings" && filter.includes("scope = 'library'")) return libraryWorkflow;
      throw new Error("not found");
    },
    save() {},
    logger() { return { warn() {}, error() {}, info() {} }; }
  };
}

const missingFieldApp = createApp({});
assert.deepStrictEqual(config.additionalCopyTimeout(missingFieldApp, "100"), { enabled: true, days: 30 });
assert.strictEqual(config.librarySettings(missingFieldApp, "100").workflow.additionalCopyTimeoutEnabled, true);
assert.strictEqual(config.librarySettings(missingFieldApp, "100").workflow.additionalCopyTimeoutDays, 30);

const explicitOverrideApp = createApp({ additionalCopyTimeoutEnabled: false, additionalCopyTimeoutDays: 7 });
assert.deepStrictEqual(config.additionalCopyTimeout(explicitOverrideApp, "100"), { enabled: false, days: 7 });

console.log("Workflow fallback config tests passed.");
