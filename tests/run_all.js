// tests/run_all.js
const fs = require("fs");
const path = require("path");
const cp = require("child_process");

const testDir = __dirname;
const filterArg = process.argv[2] ? process.argv[2].toLowerCase() : "";
const registeredTests = [
  "custom_fields.test.js",
  "custom_fields_schema.test.js",
  "config_custom_fields_scope.test.js",
  "records_custom_fields.test.js",
  "staff_additional_fields_ui.test.js"
];

const files = Array.from(new Set(registeredTests.concat(fs.readdirSync(testDir))))
  .filter((name) => {
    const isTest = name.endsWith(".test.js") && name !== "run_all.js";
    if (!isTest) return false;
    if (filterArg) {
      return name.toLowerCase().includes(filterArg);
    }
    return true;
  })
  .sort();

var failCount = 0;

for (const file of files) {
  console.log("\n==> " + file);
  try {
    cp.execFileSync(process.execPath, [path.join(testDir, file)], {
      stdio: "inherit"
    });
  } catch (err) {
    console.error("FAILED: " + file);
    failCount++;
  }
}

if (failCount > 0) {
  console.error("\n" + failCount + " tests failed.");
  process.exit(1);
} else {
  console.log("\nAll tests passed.");
}
