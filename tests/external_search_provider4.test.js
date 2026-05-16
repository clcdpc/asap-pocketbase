const assert = require("assert");
const fs = require("fs");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const config = require("../lib/config.js");

class MockRecord {
  constructor(data) {
    this.data = data || {};
  }
  get(key) {
    return this.data[key];
  }
  getBool(key) {
    return !!this.data[key];
  }
  getInt(key) {
    return parseInt(this.data[key] || 0, 10) || 0;
  }
}

function makeApp(recordData) {
  return {
    findCollectionByNameOrId(collectionName) {
      assert.strictEqual(collectionName, "workflow_settings");
      return { name: collectionName };
    },
    findRecordById(collectionName, id) {
      assert.strictEqual(collectionName, "workflow_settings");
      assert.strictEqual(id, "workflow0000010");
      return new MockRecord(recordData);
    },
  };
}

function fileText(relativePath) {
  return fs.readFileSync(path.resolve(__dirname, "..", relativePath), "utf8");
}

function extractFunction(source, name) {
  const start = source.indexOf(`function ${name}`);
  if (start < 0) throw new Error(`Function ${name} not found`);
  const braceStart = source.indexOf("{", start);
  let depth = 0;
  for (let i = braceStart; i < source.length; i++) {
    if (source[i] === "{") depth++;
    if (source[i] === "}") depth--;
    if (depth === 0) return source.slice(start, i + 1);
  }
  throw new Error(`Function ${name} body not found`);
}

function runTests() {
  const defaults = config.suggestionLimit(makeApp({}), "");
  assert.strictEqual(defaults.externalSearch4Enabled, false);
  assert.strictEqual(defaults.externalSearch4Label, "");
  assert.strictEqual(defaults.externalSearch4UrlTemplate, "");

  const configured = config.suggestionLimit(makeApp({
    externalSearch4Enabled: true,
    externalSearch4Label: "Search Local Catalog",
    externalSearch4UrlTemplate: "https://catalog.example.test/search?q={{title}}",
  }), "");
  assert.strictEqual(configured.externalSearch4Enabled, true);
  assert.strictEqual(configured.externalSearch4Label, "Search Local Catalog");
  assert.strictEqual(configured.externalSearch4UrlTemplate, "https://catalog.example.test/search?q={{title}}");

  const staffRoutes = fileText("lib/staff/settings_save.js");
  assert.ok(staffRoutes.includes('"externalSearch4Enabled"'));
  assert.ok(staffRoutes.includes('"externalSearch4Label"'));
  assert.ok(staffRoutes.includes('"externalSearch4UrlTemplate"'));

  const mainHook = fileText("pb_hooks/main.pb.js");
  assert.ok(mainHook.includes("response.externalSearch4Enabled"));
  assert.ok(mainHook.includes("response.externalSearch4Label"));
  assert.ok(mainHook.includes("response.externalSearch4UrlTemplate"));

  const settingsJs = fileText("pb_public/staff/js/settings.js");
  assert.ok(settingsJs.includes("wf-external-search-4-enabled"));
  assert.ok(settingsJs.includes("payload.externalSearch4Enabled"));
  const normalizeSource = extractFunction(settingsJs.replace("export function normalizeExternalSearchUrlTemplate", "function normalizeExternalSearchUrlTemplate"), "normalizeExternalSearchUrlTemplate");
  const normalizeExternalSearchUrlTemplate = Function(`${normalizeSource}; return normalizeExternalSearchUrlTemplate;`)();
  assert.strictEqual(normalizeExternalSearchUrlTemplate("domain.example"), "https://domain.example");
  assert.strictEqual(normalizeExternalSearchUrlTemplate("domain.example/search?q={{title}}"), "https://domain.example/search?q={{title}}");
  assert.strictEqual(normalizeExternalSearchUrlTemplate("http://domain.example/search?q={{title}}"), "http://domain.example/search?q={{title}}");
  assert.strictEqual(normalizeExternalSearchUrlTemplate("https://domain.example/search?q={{identifier}}"), "https://domain.example/search?q={{identifier}}");
  assert.strictEqual(normalizeExternalSearchUrlTemplate("   "), "");
  assert.ok(settingsJs.includes("externalSearch1UrlTemplate = normalizeExternalSearchUrlTemplate"));
  assert.ok(settingsJs.includes("externalSearch2UrlTemplate = normalizeExternalSearchUrlTemplate"));
  assert.ok(settingsJs.includes("externalSearch3UrlTemplate = normalizeExternalSearchUrlTemplate"));
  assert.ok(settingsJs.includes("externalSearch4UrlTemplate = normalizeExternalSearchUrlTemplate"));
  assert.ok(settingsJs.includes("setFieldValue('wf-external-search-4-url-template', externalSearch4UrlTemplate)"));

  const modalJs = fileText("pb_public/staff/js/modals.js");
  assert.ok(modalJs.includes("workflowSettings[`externalSearch${i}Enabled`]"));
  assert.ok(modalJs.includes("buttonClasses[index] || 'btn-info'"));

  const html = fileText("pb_public/staff/index.html");
  assert.ok(html.includes("Configure up to 4 buttons"));
  assert.ok(html.includes("wf-external-search-4-url-template"));

  console.log("external search provider 4 tests passed.");
}

runTests();
