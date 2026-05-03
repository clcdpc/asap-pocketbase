const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

function loadSettingsSource() {
  const settingsUsers = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings-users.js"), "utf8");
  const settingsTemplates = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings-templates.js"), "utf8");
  const settingsApi = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/api.js"), "utf8");
  const settingsSettings = fs.readFileSync(path.resolve(__dirname, "../pb_public/staff/js/settings.js"), "utf8");
  return settingsUsers + '\n' + settingsTemplates + '\n' + settingsApi + '\n' + settingsSettings;
}

function extractFunction(source, name) {
  const marker = `${name} = function ${name}() {`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}.`);
  let depth = 0;
  for (let index = start; index < source.length; index++) {
    const char = source[index];
    if (char === "{") depth++;
    if (char === "}") {
      depth--;
      if (depth === 0) return source.slice(start, index + 1);
    }
  }
  throw new Error(`Could not parse ${name}.`);
}

function buildHarness(options) {
  const values = Object.assign({
    "smtp-port": "587",
    "outstanding-timeout-days": "30",
    "hold-pickup-timeout-days": "14",
    "pending-hold-timeout-days": "14",
    "outstanding-timeout-rejection-template-id": "",
  }, options.values || {});
  const checked = Object.assign({
    "outstanding-timeout-enabled": true,
    "outstanding-timeout-send-email": true,
  }, options.checked || {});
  const env = {
    buildSettingsPayload: null,
    currentLibraryContextOrgId: "library-1",
    currentRejectionTemplates: options.templates || [],
    defaultAgeGroups: [],
    defaultPublicationOptions: [],
    leapBibUrlPattern: "",
    workflowSettings: { outstandingTimeoutRejectionTemplateId: options.currentTemplateId || "" },
    collectAvailableFormats: function () { return []; },
    collectDuplicateStatusLabels: function () { return {}; },
    collectEnabledLibraryIds: function () { return ""; },
    collectFormatLabels: function () { return {}; },
    collectOptionList: function () { return []; },
    collectPatronFormatRules: function () { return {}; },
    collectSettingsPolaris: function () { return {}; },
    document: { getElementById: function () { return null; } },
    getFieldChecked: function (id, fallback) {
      return Object.prototype.hasOwnProperty.call(checked, id) ? checked[id] : !!fallback;
    },
    getFieldValue: function (id, fallback) {
      return Object.prototype.hasOwnProperty.call(values, id) ? String(values[id]) : String(fallback || "");
    },
    isSuperAdminStaff: function () { return false; },
    setFieldValue: function () {},
    sortAuthorsByLastName: function (value) { return value; },
  };
  new Function("env", `with (env) {\n${extractFunction(loadSettingsSource(), "buildSettingsPayload")}\n}`)(env);
  return env;
}

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    console.log(`PASS ${name}`);
    passed++;
  } catch (err) {
    console.error(`FAIL ${name}`);
    console.error(err && err.stack ? err.stack : err);
    failed++;
  }
}

test("auto-reject email can use standard template with no custom templates", function () {
  const env = buildHarness({ templates: [] });
  const payload = env.buildSettingsPayload();
  assert.strictEqual(payload.outstandingTimeoutRejectionTemplateId, "");
});

test("auto-reject email can use standard template when custom templates exist", function () {
  const env = buildHarness({
    templates: [{ id: "tpl_custom", name: "Custom" });],
    values: { "outstanding-timeout-rejection-template-id": "" },
  });
  const payload = env.buildSettingsPayload();
  assert.strictEqual(payload.outstandingTimeoutRejectionTemplateId, "");
});

test("auto-reject email saves selected custom template id", function () {
  const env = buildHarness({
    templates: [{ id: "tpl_custom", name: "Custom" });],
    values: { "outstanding-timeout-rejection-template-id": "tpl_custom" },
  });
  const payload = env.buildSettingsPayload();
  assert.strictEqual(payload.outstandingTimeoutRejectionTemplateId, "tpl_custom");
});

console.log(`Tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
