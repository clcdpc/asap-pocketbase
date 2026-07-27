const assert = require("assert");
const fs = require("fs");
const path = require("path");

// Mock environment for settings.js logic testing
const serializeSaveSource = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings/serialize-save.js"), "utf8")
  .replace(/\bexport\s+/g, "");
const formPopulationSource = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings/form-population.js"), "utf8")
  .replace(/\bexport\s+/g, "");
const source = serializeSaveSource + "\n" + formPopulationSource;

function extractFunction(name) {
  const start = source.indexOf("function " + name + "(");
  if (start < 0) throw new Error("Could not find function " + name);
  const bodyStart = source.indexOf("{", start);
  let depth = 0;
  for (let i = bodyStart; i < source.length; i++) {
    if (source[i] === "{") depth++;
    if (source[i] === "}") depth--;
    if (depth === 0) return source.slice(start, i + 1);
  }
  throw new Error("Could not extract function " + name);
}

function loadSettingsLogic(options = {}) {
  const eligibilityEnabled = !!options.eligibilityEnabled;
  const allowedPatronCodeIds = String(options.allowedPatronCodeIds || "");
  const mocks = `
    const currentLibraryContextOrgId = 'system';
    const leapBibUrlPattern = '';
    const leapPatronUrlPattern = '';
    const currentRejectionTemplates = [];
    const defaultPublicationOptions = [];
    const lastWorkflowEnabledList = [];
    const formatMap = {};
    const availableFormats = [];
    
    const window = {
      location: { origin: 'https://example.org' },
      localStorage: {
        getItem: () => null,
        setItem: () => {}
      }
    };
    
    function isSuperAdminStaff() { return true; }
    function getFieldValue(id, fallback) { return fallback || ''; }
    function getFieldChecked(id) { return false; }
    function setFieldValue(id, val) {}
    function setFieldChecked(id, val) {}
    function setLastWorkflowEnabledList(l) {}
    function toggleTimeoutGroup() {}
    function updateAutoRejectEmailControls() {}
    function toggleHoldPickupTimeoutGroup() {}
    function togglePendingHoldTimeoutGroup() {}
    function toggleAdditionalCopyTimeoutGroup() {}
    function toggleCommonAuthorsGroup() {}
    function validateStaffUrl(url) { return null; }
    function normalizeStaffUrl(url) { return url; }
    function normalizeLeapBibUrlPattern(p) { return p; }
    function normalizeLeapPatronUrlPattern(p) { return p; }
    function normalizeExternalSearchUrlTemplate(t) { return t; }
    function collectDuplicateStatusLabels() { return {}; }
    function collectFormatLabels() { return {}; }
    function collectFormatOrder() { return []; }
    function collectAvailableFormats() { return []; }
    function collectOptionList() { return []; }
    function collectPatronFormatRules() { return {}; }
    function collectAdditionalFieldDefinitions() { return []; }
    function collectFormatClaimRules() { return []; }
    function sortAuthorsByLastName(l) { return l; }
    function collectSettingsPolaris() { return {}; }
    function collectEnabledLibraryIds() { return []; }
    function collectAllowedPatronCodeIds() { return ${JSON.stringify(allowedPatronCodeIds)}; }
    function getPatronCodeEligibilityEnabled() { return ${JSON.stringify(eligibilityEnabled)}; }
    function setPatronCodeEligibilityMode() {}
    function renderFormatSettings() {}
    function updateModalFormatDropdowns() {}
    function renderOptionListEditor() {}
    function renderPatronFormatRulesEditor() {}
    function renderAdditionalFieldsEditor() {}
    function setAdditionalFieldDefinitions() {}
    function setCurrentPatronFieldConfig() {}
    function updatePublicationOptionsUi() {}
    function renderDuplicateStatusLabelSettings() {}
    function renderPatronCodeEligibilityOptions() {}
    function updatePatronCodesStatusUi() {}
    
    const workflowSettings = {};
    const document = {
      getElementById: (id) => ({ 
        id, 
        checked: false, 
        classList: { 
          add: () => {}, 
          remove: () => {}, 
          toggle: () => {} 
        },
        querySelectorAll: () => [] 
      }),
      querySelector: () => ({ 
        classList: { 
          add: () => {}, 
          remove: () => {}, 
          toggle: () => {} 
        } 
      })
    };
  `;

  const fnSource = mocks + "\n" + 
    extractFunction("_serializeSettingsState") + "\n" +
    extractFunction("populateWorkflowForms") + "\n" +
    extractFunction("populatePatronUiForms") + "\n" +
    "return { _serializeSettingsState, populateWorkflowForms, populatePatronUiForms };";
  return new Function(fnSource)();
}

console.log("Running settings logic tests...");

try {
  const logic = loadSettingsLogic();
  
  console.log("  Testing _serializeSettingsState...");
  const payload1 = logic._serializeSettingsState(false);
  assert.ok(payload1 && typeof payload1 === 'object');

  console.log("  Testing populateWorkflowForms...");
  logic.populateWorkflowForms({});

  console.log("  Testing populatePatronUiForms...");
  logic.populatePatronUiForms({});

  console.log("  Testing restricted patron code validation...");
  const restrictedLogic = loadSettingsLogic({ eligibilityEnabled: true, allowedPatronCodeIds: "" });
  assert.throws(
    () => restrictedLogic._serializeSettingsState(true),
    /Select at least one allowed patron code/
  );
  const selectedLogic = loadSettingsLogic({ eligibilityEnabled: true, allowedPatronCodeIds: "1,14" });
  assert.strictEqual(selectedLogic._serializeSettingsState(true).patronCodeEligibilityEnabled, true);
  assert.strictEqual(selectedLogic._serializeSettingsState(true).allowedPatronCodeIds, "1,14");

  console.log("PASS: Settings logic is sound.");
} catch (err) {
  console.error("FAIL: Settings logic error:", err);
  process.exit(1);
}
