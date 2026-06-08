const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

function makeRecord(id, values) {
  return {
    id,
    values: Object.assign({}, values || {}),
    get: function (name) {
      return this.values[name];
    },
    getBool: function (name) {
      return !!this.values[name];
    },
    getInt: function (name) {
      return parseInt(this.values[name], 10) || 0;
    },
    set: function (name, value) {
      this.values[name] = value;
    }
  };
}

const config = require("../lib/config.js");

function createMockApp() {
  const systemUi = makeRecord("uisettings00010", {
    scope: "system",
    publicationOptions: JSON.stringify([{ id: "new", label: "New release", enabled: true, sortOrder: 10 }])
  });
  const workflow = makeRecord("workflow0000010", {
    scope: "system",
    suggestionLimit: 5
  });
  const override2 = makeRecord("override-2", {
    orgId: "2",
    additionalFieldDefinitions: [
      { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
    ],
    patronFormatRules: {
      videogame: { customFields: { platform: { mode: "required" } } }
    }
  });

  return {
    findCollectionByNameOrId: function (name) {
      return { name };
    },
    findRecordById: function (collectionName, id) {
      if (collectionName === "ui_settings" && id === "uisettings00010") return systemUi;
      if (collectionName === "workflow_settings" && id === "workflow0000010") return workflow;
      throw new Error("record not found");
    },
    findFirstRecordByData: function () {
      throw new Error("record not found");
    },
    findFirstRecordByFilter: function (collectionName, filter, params) {
      if (collectionName === "ui_settings" && filter.indexOf("scope = 'system'") >= 0) return systemUi;
      if (collectionName === "patron_settings_overrides" && params.orgId === "2") return override2;
      throw new Error("record not found");
    },
    findRecordsByFilter: function () {
      return [];
    },
    logger: function () {
      return { warn: function () {} };
    }
  };
}

const app = createMockApp();

assert.deepStrictEqual(config.uiText(app, "").additionalFieldDefinitions, []);
assert.deepStrictEqual(config.uiText(app, "2").additionalFieldDefinitions.map(f => f.key), ["platform"]);
assert.deepStrictEqual(config.uiText(app, "3").additionalFieldDefinitions, []);
assert.strictEqual(config.uiText(app, "2").formatRules.videogame.customFields.platform.mode, "required");

console.log("config custom fields scope tests passed");
