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

global.Record = function MockRecord() {
  return makeRecord("", {});
};

const config = require("../lib/config.js");

function createMockApp(options) {
  options = options || {};
  const org = makeRecord("org-rec-100", {
    organizationId: "100",
    displayName: "Branch 100"
  });
  const systemUi = makeRecord("uisettings00010", {
    scope: "system",
    publicationOptions: JSON.stringify([
      { id: "new", label: "New release", enabled: true, sortOrder: 10 },
      { id: "backlist", label: "Backlist", enabled: true, sortOrder: 20 }
    ])
  });
  const libraryUi = makeRecord("library-ui-100", {
    scope: "library",
    libraryOrganization: org.id,
    logoAlt: "Branch Logo"
  });
  const workflow = makeRecord("workflow0000010", {
    scope: "system",
    suggestionLimit: 5
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
    findFirstRecordByData: function (collectionName, field, value) {
      if (collectionName === "polaris_organizations" && field === "organizationId" && value === "100") return org;
      throw new Error("record not found");
    },
    findFirstRecordByFilter: function (collectionName, filter, params) {
      if (collectionName === "ui_settings" && filter.indexOf("scope = 'library'") >= 0 && params.org === org.id) return libraryUi;
      if (collectionName === "ui_settings" && filter.indexOf("scope = 'system'") >= 0) return systemUi;
      if (collectionName === "patron_settings_overrides" && options.overrideRecord) return options.overrideRecord;
      throw new Error("record not found");
    },
    findRecordsByFilter: function () {
      return [];
    },
    save: function () {},
    logger: function () {
      return { warn: function () {} };
    }
  };
}

function labels(options) {
  return options.map(function (option) {
    return option.label;
  });
}

function byteJson(value) {
  return Array.from(Buffer.from(JSON.stringify(value), "utf8"));
}

console.log("Running ui_text patron option scope tests...");

let inherited = config.uiText(createMockApp(), "100");
assert.deepStrictEqual(labels(inherited.publicationOptions), ["New release", "Backlist"]);
assert.strictEqual(Object.prototype.hasOwnProperty.call(inherited, "ageGroups"), false);

let overrideRecord = makeRecord("override-100", {
  orgId: "100",
  publicationOptions: JSON.stringify([
    { id: "local", label: "Local preorder", enabled: true, sortOrder: 10 }
  ])
});
let overridden = config.uiText(createMockApp({ overrideRecord }), "100");
assert.deepStrictEqual(labels(overridden.publicationOptions), ["Local preorder"]);
assert.strictEqual(Object.prototype.hasOwnProperty.call(overridden, "ageGroups"), false);

let byteOverrideRecord = makeRecord("override-byte-100", {
  orgId: "100",
  publicationOptions: byteJson([
    { id: "cafe", label: "Café preorder", enabled: true, sortOrder: 10 }
  ])
});
let byteOverridden = config.uiText(createMockApp({ overrideRecord: byteOverrideRecord }), "100");
assert.deepStrictEqual(labels(byteOverridden.publicationOptions), ["Café preorder"]);
assert.strictEqual(Object.prototype.hasOwnProperty.call(byteOverridden, "ageGroups"), false);

console.log("All ui_text patron option scope tests passed!");
