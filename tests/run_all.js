// tests/run_all.js
const fs = require("fs");
const path = require("path");
const cp = require("child_process");

const testDir = __dirname;
const files = fs.readdirSync(testDir)
  .filter((name) => name.endsWith(".test.js") && name !== "run_all.js")
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
