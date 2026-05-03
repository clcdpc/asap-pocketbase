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
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}.`);

  let functionStart = start;
  while(functionStart > 0 && source[functionStart - 1] !== '\n') {
      functionStart--;
  }

  let depth = 0;
  let opened = false;
  for (let index = start; index < source.length; index++) {
    const char = source[index];
    if (char === "{") {
      depth++;
      opened = true;
    }
    if (char === "}") {
      depth--;
      if (opened && depth === 0) {
          let extracted = source.slice(functionStart, index + 1);
          extracted = extracted.replace(/^export\s+/, '');
          return 'env.' + name + ' = ' + extracted;
      }
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
  const env = { leapBibUrlPattern: "",
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
    collectSettingsPolaris: function() { return {}; },
    collectDuplicateStatusLabels: function() { return {}; },
    collectFormatLabels: function() { return []; },
    collectAvailableFormats: function() { return []; },
    collectOptionList: function() { return []; },
    collectPatronFormatRules: function() { return {}; },
    collectEnabledLibraryIds: function() { return []; },
    setFieldValue: function () {},
    sortAuthorsByLastName: function (value) { return value; },
  };
  global.leapBibUrlPattern = ""; global.isSuperAdminStaff = function() { return false; };
  global.currentLibraryContextOrgId = "system";
  global.workflowSettings = { outstandingTimeoutRejectionTemplateId: "" };
  global.document = { getElementById: function() { return null; } };
  global.defaultPublicationOptions = [];
  global.defaultAgeGroups = [];
  global.currentRejectionTemplates = [];
  eval(extractFunction(loadSettingsSource(), "buildSettingsPayload").replace(/getFieldValue/g, 'env.getFieldValue').replace(/getFieldChecked/g, 'env.getFieldChecked').replace(/isSuperAdminStaff/g, 'env.isSuperAdminStaff').replace(/setFieldValue/g, 'env.setFieldValue').replace(/sortAuthorsByLastName/g, 'env.sortAuthorsByLastName').replace(/collectSettingsPolaris/g, 'env.collectSettingsPolaris').replace(/collectDuplicateStatusLabels/g, 'env.collectDuplicateStatusLabels').replace(/collectFormatLabels/g, 'env.collectFormatLabels').replace(/collectAvailableFormats/g, 'env.collectAvailableFormats').replace(/collectOptionList/g, 'env.collectOptionList').replace(/collectPatronFormatRules/g, 'env.collectPatronFormatRules').replace(/collectEnabledLibraryIds/g, 'env.collectEnabledLibraryIds'));
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
    templates: [{ id: "tpl_custom", name: "Custom" }],
    values: { "outstanding-timeout-rejection-template-id": "" },
  });
  const payload = env.buildSettingsPayload();
  assert.strictEqual(payload.outstandingTimeoutRejectionTemplateId, "");
});

test("auto-reject email saves selected custom template id", function () {
  const env = buildHarness({
    templates: [{ id: "tpl_custom", name: "Custom" }],
    values: { "outstanding-timeout-rejection-template-id": "tpl_custom" },
  });
  const payload = env.buildSettingsPayload();
  assert.strictEqual(payload.outstandingTimeoutRejectionTemplateId, "tpl_custom");
});

console.log(`Tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
