const assert = require("assert");
const { JSDOM } = require("jsdom");

(async () => {
  const dom = new JSDOM(`<!doctype html>
    <div id="additional-fields-editor"></div>
    <button id="btn-add-additional-field"></button>
  `, { url: "http://localhost" });
  global.window = dom.window;
  global.document = dom.window.document;
  global.Option = dom.window.Option;
  global.localStorage = dom.window.localStorage;

  window.PocketBase = function MockPocketBase() {};

  const mod = await import("../pb_public/staff/js/settings-additional-fields.js");

  mod.renderAdditionalFieldsEditor([
    { key: "platform", label: "Platform", type: "select", helpText: "Pick one", enabled: true, sortOrder: 10, options: [{ id: "switch", label: "Nintendo Switch", enabled: true, sortOrder: 10 }] }
  ]);

  assert.strictEqual(document.querySelector('[data-additional-field-key="platform"] .additional-field-label-input').value, "Platform");
  assert.strictEqual(document.querySelector(".additional-field-option-label-input").value, "Nintendo Switch");

  const collected = mod.collectAdditionalFieldDefinitions();
  assert.strictEqual(collected[0].key, "platform");
  assert.strictEqual(collected[0].options[0].id, "switch");
  console.log("staff additional fields UI tests passed");
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
