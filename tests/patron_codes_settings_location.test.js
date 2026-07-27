const assert = require("assert");
const fs = require("fs");
const path = require("path");

const html = fs.readFileSync(path.join(__dirname, "../pb_public/staff/index.html"), "utf8");
const saveController = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings/save-controller.js"), "utf8");
const patronCodeUi = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings/patron-codes.js"), "utf8");
const serializer = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings/serialize-save.js"), "utf8");
const polarisStart = html.indexOf('<section id="settings-polaris"');
const patronStart = html.indexOf('<section id="settings-patron"');
const polarisSection = html.slice(polarisStart, patronStart);
const patronSection = html.slice(patronStart);

assert.ok(polarisStart >= 0, "Polaris settings section should exist");
assert.ok(patronStart > polarisStart, "Patron settings section should follow Polaris settings");
assert.ok(
  polarisSection.includes('id="patron-codes-status-message"'),
  "Patron-code cache status should be shown in Polaris settings"
);
assert.ok(
  polarisSection.includes("Sync organizations") && polarisSection.includes("and patron codes now"),
  "Polaris reference-data action should describe both cache syncs"
);
assert.ok(
  !html.includes('id="btn-sync-patron-codes"'),
  "Patron codes should not have a separate manual sync button"
);
assert.ok(
  patronSection.includes('id="patron-code-access-all"') &&
    patronSection.includes('id="patron-code-access-restricted"') &&
    patronSection.includes('id="allowed-patron-code-container"') &&
    patronSection.includes('id="patron-code-eligibility-message"'),
  "Scoped patron-code eligibility controls should remain in Patron settings"
);
assert.ok(
  patronSection.includes('class="asap-accordion patron-code-accordion"') &&
    patronSection.includes('id="accordion-patron-code-selection"') &&
    patronSection.includes('aria-expanded="true"'),
  "Patron-code checklist should reuse the existing expanded accordion UI"
);
assert.ok(
  saveController.includes("patronCodeEligibilityEnabled: payload.patronCodeEligibilityEnabled") &&
    saveController.includes("allowedPatronCodeIds: payload.allowedPatronCodeIds") &&
    saveController.includes("patronCodeEligibilityMessage: payload.patronCodeEligibilityMessage"),
  "Scoped save payload should preserve all patron-code eligibility fields"
);
assert.ok(
  patronCodeUi.includes("custom-control custom-checkbox") &&
    patronSection.includes("Select all") &&
    patronSection.includes("Clear all") &&
    !patronCodeUi.includes("createElement('dialog')") &&
    !patronCodeUi.includes("showModal()"),
  "Patron-code selection should use inline checkboxes without a custom dialog"
);
assert.ok(
  serializer.includes("getPatronCodeEligibilityEnabled()") &&
    serializer.includes("Select at least one allowed patron code when patron code access is limited."),
  "Restricted mode should be explicit and require at least one selected code"
);

console.log("patron_codes_settings_location.test.js passed.");
