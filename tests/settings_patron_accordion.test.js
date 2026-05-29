const assert = require("assert");
const fs = require("fs");
const path = require("path");

const html = fs.readFileSync(path.join(__dirname, "../pb_public/staff/index.html"), "utf8");

console.log("Running patron settings accordion structure tests...");

assert.ok(html.includes('id="settings-patron"'), "patron settings panel should exist");
assert.ok(html.includes('id="patron-experience-accordion"'), "patron settings should use a top-level accordion");
assert.ok(
  html.includes('id="patron-experience-accordion" class="asap-accordion patron-settings-accordion" data-accordion-multiple="true"'),
  "patron settings accordion should allow multiple open sections"
);

[
  "accordion-patron-branding",
  "accordion-patron-login-text",
  "accordion-patron-suggestion-messages",
  "accordion-patron-publication-options",
  "accordion-patron-format-display",
  "accordion-format-rules"
].forEach(id => {
  assert.ok(html.includes(`id="${id}"`), `${id} should exist`);
});

["accordion-patron-branding", "accordion-patron-login-text"].forEach(id => {
  const openItemPattern = new RegExp(`<div class="asap-accordion-item active" id="${id}"[\\s\\S]*?<button[^>]+aria-expanded="true"`);
  assert.ok(openItemPattern.test(html), `${id} should be open and expanded by default`);
});

[
  "ui-logo-preview",
  "ui-logo-file",
  "ui-logo-alt",
  "btn-reset-logo",
  "btn-upload-logo",
  "ui-patron-page-title",
  "ui-barcode-label",
  "ui-pin-label",
  "ui-login-prompt",
  "ui-login-note",
  "ui-suggestion-note",
  "ui-no-email-msg",
  "ui-success-title",
  "ui-success-msg",
  "ui-already-submitted-msg",
  "duplicate-status-labels-container",
  "ui-publication-options-editor",
  "btn-add-publication-option",
  "patron-options-scope",
  "btn-open-add-format-modal",
  "format-settings-container",
  "format-rules-editor"
].forEach(id => {
  assert.ok(html.includes(`id="${id}"`), `${id} should still exist for existing load/save code`);
});

const formatSettingsIndex = html.indexOf('id="format-settings-container"');
const formatRulesIndex = html.indexOf('id="format-rules-editor"');
assert.ok(
  formatSettingsIndex >= 0 && formatRulesIndex > formatSettingsIndex,
  "format rules editor should remain directly after the format display names area"
);

console.log("All patron settings accordion structure tests passed!");
