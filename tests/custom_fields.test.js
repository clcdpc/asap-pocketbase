const assert = require("assert");
const customFields = require("../lib/custom_fields.js");

function testNormalizeDefinitions() {
  const defs = customFields.normalizeDefinitions([
    {
      key: "Platform!",
      label: " Platform ",
      type: "select",
      helpText: " Preferred platform ",
      enabled: true,
      sortOrder: 20,
      options: [
        { id: "Switch", label: " Nintendo Switch ", enabled: true, sortOrder: 20 },
        { label: "PlayStation 5", enabled: true, sortOrder: 10 },
        { id: "Switch", label: "Duplicate", enabled: true, sortOrder: 30 }
      ]
    },
    { key: "bad", label: "", type: "date" }
  ]);

  assert.deepStrictEqual(defs.map(d => d.key), ["platform"]);
  assert.strictEqual(defs[0].label, "Platform");
  assert.strictEqual(defs[0].type, "select");
  assert.strictEqual(defs[0].helpText, "Preferred platform");
  assert.deepStrictEqual(defs[0].options.map(o => o.id), ["playstation_5", "switch"]);
  assert.deepStrictEqual(defs[0].options.map(o => o.label), ["PlayStation 5", "Nintendo Switch"]);
}

function testNormalizeFormatCustomFieldRules() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Switch" }] },
    { key: "content_note", label: "Content note", type: "textarea" },
    { key: "disabled_field", label: "Disabled", type: "text", enabled: false }
  ]);
  const rules = customFields.normalizeFormatCustomFieldRules({
    platform: { mode: "required" },
    content_note: { mode: "banana" },
    disabled_field: { mode: "required" },
    unknown: { mode: "required" }
  }, defs);

  assert.deepStrictEqual(rules, {
    platform: { mode: "required" },
    content_note: { mode: "hidden" },
    disabled_field: { mode: "hidden" }
  });
}

function testSanitizeSubmittedValues() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] },
    { key: "content_note", label: "Content note", type: "textarea" },
    { key: "edition", label: "Edition", type: "text" }
  ]);
  const rules = {
    platform: { mode: "required" },
    content_note: { mode: "optional" },
    edition: { mode: "hidden" }
  };
  const sanitized = customFields.sanitizeSubmittedValues({
    platform: "Nintendo Switch",
    content_note: "  Keep as plain text  ",
    edition: "discard me",
    unknown: "discard me"
  }, defs, rules);

  assert.deepStrictEqual(sanitized, {
    platform: { label: "Platform", type: "select", value: "switch", displayValue: "Nintendo Switch" },
    content_note: { label: "Content note", type: "textarea", value: "Keep as plain text" }
  });
}

function testRequiredValueError() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
  ]);
  assert.throws(
    () => customFields.sanitizeSubmittedValues({}, defs, { platform: { mode: "required" } }),
    /Platform is required/
  );
}

function testSelectValuesMustMatchExactOptionIdOrLabel() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
  ]);
  assert.throws(
    () => customFields.sanitizeSubmittedValues({ platform: "SWITCH" }, defs, { platform: { mode: "required" } }),
    (err) => err && err.code === 400 && /Platform is required/.test(err.message)
  );
}

testNormalizeDefinitions();
testNormalizeFormatCustomFieldRules();
testSanitizeSubmittedValues();
testRequiredValueError();
testSelectValuesMustMatchExactOptionIdOrLabel();
console.log("custom_fields tests passed");
