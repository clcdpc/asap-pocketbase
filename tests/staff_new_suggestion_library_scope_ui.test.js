const assert = require("assert");
const fs = require("fs");
const path = require("path");

const source = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/patron.js"), "utf8");

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

function element(value, classes) {
  const classNames = new Set(classes || []);
  return {
    value: value || "",
    classList: {
      contains: (name) => classNames.has(name),
      add: (name) => classNames.add(name),
      remove: (name) => classNames.delete(name)
    }
  };
}

function loadHelpers(options) {
  const select = element(options.selectValue, options.selectClasses);
  const group = element("", options.groupClasses);
  const document = {
    getElementById: (id) => {
      if (id === "new-suggestion-library") return select;
      if (id === "new-suggestion-library-group") return group;
      return null;
    }
  };
  const helperSource = [
    extractFunction("staffSuggestionRequiresLibrarySelection"),
    extractFunction("staffSuggestionLibrarySelectorVisible"),
    extractFunction("staffSuggestionLibraryPayload")
  ].join("\n\n");
  return new Function(
    "document",
    "isSuperAdminStaff",
    "currentWorkflowOrgScopeId",
    helperSource + "\nreturn { staffSuggestionRequiresLibrarySelection, staffSuggestionLibraryPayload };"
  )(
    document,
    () => !!options.superAdmin,
    options.currentWorkflowOrgScopeId
  );
}

function runTests() {
  console.log("Running staff new suggestion library scope UI tests...");

  let helpers = loadHelpers({
    superAdmin: false,
    currentWorkflowOrgScopeId: "all",
    groupClasses: ["hidden"],
    selectValue: ""
  });
  assert.strictEqual(
    helpers.staffSuggestionRequiresLibrarySelection(),
    false,
    "library staff should not be prompted to select a servicing library when the picker group is hidden"
  );

  helpers = loadHelpers({
    superAdmin: false,
    currentWorkflowOrgScopeId: "all",
    groupClasses: ["hidden"],
    selectValue: "10"
  });
  assert.deepStrictEqual(
    helpers.staffSuggestionLibraryPayload({ barcode: "b1" }),
    { barcode: "b1" },
    "library staff payloads should ignore hidden servicing-library select values"
  );

  helpers = loadHelpers({
    superAdmin: true,
    currentWorkflowOrgScopeId: "all",
    groupClasses: [],
    selectValue: ""
  });
  assert.strictEqual(
    helpers.staffSuggestionRequiresLibrarySelection(),
    true,
    "super admins in an all-library scope should choose a servicing library"
  );

  helpers = loadHelpers({
    superAdmin: true,
    currentWorkflowOrgScopeId: "all",
    groupClasses: [],
    selectValue: "20"
  });
  assert.deepStrictEqual(
    helpers.staffSuggestionLibraryPayload({ barcode: "b2" }),
    { barcode: "b2", libraryOrgId: "20" },
    "super admin payloads should include the selected servicing library"
  );

  console.log("Staff new suggestion library scope UI tests passed.");
}

runTests();
