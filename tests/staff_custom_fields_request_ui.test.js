const assert = require("assert");
const { JSDOM } = require("jsdom");

(async () => {
  const dom = new JSDOM(`<!doctype html>
    <div id="edit-custom-fields"></div>
  `, { url: "http://localhost" });
  global.window = dom.window;
  global.document = dom.window.document;
  global.Option = dom.window.Option;

  const mod = await import("../pb_public/staff/js/request-custom-fields.js");

  mod.renderEditCustomFieldsForTest(
    { customFields: { platform: { label: "Platform", type: "select", value: "switch", displayValue: "Nintendo Switch" } } },
    [{ key: "platform", label: "Platform", type: "select", enabled: true, options: [{ id: "switch", label: "Nintendo Switch", enabled: true }] }],
    { platform: { mode: "required" } }
  );

  assert.strictEqual(document.querySelector('#edit-custom-fields label').textContent.includes("Platform"), true);
  assert.strictEqual(document.querySelector('#edit-custom-fields select').value, "switch");
  console.log("staff custom fields request UI tests passed");
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
