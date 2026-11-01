// tests/run_all.js
const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const assert = require("node:assert/strict");

const testDir = __dirname;
const mode = process.argv[2] || "";
const safetyFiles = ["ci_workflow_contract.test.js", "test_iis_deployment_contract.test.js"];
const isSafety = mode === "--ci-safety";
const isRemaining = mode === "--ci-remaining";
const filterArg = isSafety || isRemaining ? "" : mode.toLowerCase();
const allFiles = fs.readdirSync(testDir).filter((name) => name.endsWith(".test.js")).sort();
let prior = { executed: 0, passed: 0, failed: 0, skipped: 0 };
if (isRemaining) {
  prior = JSON.parse(fs.readFileSync(process.env.ASAP_CI_FRONTEND_SAFETY, "utf8"));
  assert.equal(prior.filter, "--ci-safety");
  assert.deepEqual(prior.files, safetyFiles);
  assert.equal(prior.executed, safetyFiles.length);
  assert.equal(prior.passed, safetyFiles.length);
  assert.equal(prior.failed, 0);
  assert.equal(prior.skipped, 0);
}
const files = allFiles
  .filter((name) => {
    const isTest = name.endsWith(".test.js") && name !== "run_all.js";
    if (!isTest) return false;
    if (isSafety) return safetyFiles.includes(name);
    if (isRemaining) return !safetyFiles.includes(name);
    if (filterArg) {
      return name.toLowerCase().includes(filterArg);
    }
    return true;
  })
  .sort();
assert.ok(files.length > 0, "No frontend tests selected");
if (isSafety) assert.deepEqual(files, safetyFiles);

var failCount = 0;
var passCount = 0;

for (const file of files) {
  console.log("\n==> " + file);
  try {
    cp.execFileSync(process.execPath, [path.join(testDir, file)], {
      stdio: "inherit"
    });
    passCount++;
  } catch (err) {
    console.error("FAILED: " + file);
    failCount++;
  }
}

if (process.env.ASAP_CI_FRONTEND_RESULTS) {
  const reportPath = path.resolve(process.env.ASAP_CI_FRONTEND_RESULTS);
  fs.mkdirSync(path.dirname(reportPath), { recursive: true });
  fs.writeFileSync(reportPath, JSON.stringify({
    filter: isSafety ? mode : filterArg, files: isRemaining ? allFiles : files,
    executed: prior.executed + passCount + failCount,
    passed: prior.passed + passCount, failed: prior.failed + failCount, skipped: 0
  }) + '\n');
}

if (failCount > 0) {
  console.error("\n" + failCount + " tests failed.");
  process.exit(1);
} else {
  console.log("\nAll tests passed.");
}
