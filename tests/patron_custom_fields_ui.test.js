const assert = require("assert");
const { JSDOM } = require("jsdom");

(async () => {
  const dom = new JSDOM(`<!doctype html>
    <form id="suggestion-form">
      <div id="additional-fields-container"></div>
    </form>
  `, { url: "http://localhost" });
  global.window = dom.window;
  global.document = dom.window.document;
  global.Option = dom.window.Option;

  const mod = await import("../pb_public/patron/js/custom-fields.js");

  mod.renderCustomFields(
    [
      { key: "platform", label: "Platform", type: "select", helpText: "Pick one", enabled: true, options: [{ id: "switch", label: "Nintendo Switch", enabled: true }] },
      { key: "note", label: "Note", type: "textarea", enabled: true }
    ],
    { platform: { mode: "required" }, note: { mode: "optional" } }
  );

  assert.strictEqual(document.querySelector('label[for="custom-field-platform"]').textContent.includes("Platform"), true);
  assert.strictEqual(document.getElementById("custom-field-platform").required, true);
  assert.strictEqual(document.querySelector("option").textContent, "Nintendo Switch");

  document.getElementById("custom-field-platform").value = "switch";
  document.getElementById("custom-field-note").value = "Plain text";
  assert.deepStrictEqual(mod.collectCustomFieldValues(), { platform: "switch", note: "Plain text" });
  console.log("patron custom fields UI tests passed");
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
