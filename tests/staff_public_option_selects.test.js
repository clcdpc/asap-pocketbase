const assert = require("assert");
const fs = require("fs");
const path = require("path");

const source = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings-ui.js"), "utf8")
  .replace(/\bexport\s+/g, "");

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

class FakeSelect {
  constructor(value) {
    this.options = [];
    this._value = value || "";
  }
  set innerHTML(value) {
    this.options = [];
    this._value = "";
  }
  get value() {
    return this._value;
  }
  set value(value) {
    this._value = String(value || "");
  }
  appendChild(option) {
    this.options.push(option);
    if (!this._value) this._value = option.value;
  }
}

function createDocument(selects) {
  return {
    addEventListener: function () {},
    getElementById: function (id) {
      return selects[id] || null;
    },
    querySelectorAll: function (selector) {
      if (selector === ".publication-options-select") {
        return selects.publication || [];
      }
      return [];
    },
    createElement: function () {
      return { value: "", textContent: "" };
    }
  };
}

function loadHelpers(document) {
  const helperSource = [
    extractFunction("optionIdFromLabel"),
    extractFunction("isByteArray"),
    extractFunction("decodeByteArray"),
    extractFunction("normalizeOptionList"),
    extractFunction("enabledOptionLabels"),
    extractFunction("updatePublicationOptionsUi")
  ].join("\n\n");
  return new Function("document", "defaultPublicationOptions", helperSource + "\nreturn { normalizeOptionList, updatePublicationOptionsUi };")(
    document,
    ["Already published", "Coming soon", "Published a while back"]
  );
}

function byteJson(value) {
  return Array.from(Buffer.from(JSON.stringify(value), "utf8"));
}

function runTests() {
  console.log("Running staff public option select tests...");

  const publicationSelect = new FakeSelect("Previous Library Option");
  const selects = { publication: [publicationSelect] };
  const document = createDocument(selects);
  const helpers = loadHelpers(document);

  helpers.updatePublicationOptionsUi([
    { id: "new", label: "New release", enabled: true, sortOrder: 10 },
    { id: "backlist", label: "Backlist", enabled: true, sortOrder: 20 }
  ]);

  assert.strictEqual(publicationSelect.value, "New release", "publication select should reset stale values to the selected library's first valid option");
  assert.deepStrictEqual(publicationSelect.options.map(function (option) { return option.value; }), ["New release", "Backlist"]);

  const byteOptions = helpers.normalizeOptionList(byteJson([
    { id: "cafe", label: "Café preorder", enabled: true, sortOrder: 10 },
    { id: "ninos", label: "Niños", enabled: false, sortOrder: 20 }
  ]), ["Fallback"]);

  assert.deepStrictEqual(
    byteOptions.map(function (option) { return { id: option.id, label: option.label, enabled: option.enabled }; }),
    [
      { id: "cafe", label: "Café preorder", enabled: true },
      { id: "ninos", label: "Niños", enabled: false }
    ],
    "byte-array JSON option payloads should decode before rendering labels"
  );

  console.log("All staff public option select tests passed!");
}

runTests();
