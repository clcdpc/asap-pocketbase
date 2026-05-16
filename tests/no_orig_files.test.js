const assert = require("assert");
const fs = require("fs");
const path = require("path");

function walk(dir, results) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, results);
    else if (entry.name.endsWith(".orig")) results.push(full);
  }
}

const results = [];
walk(path.resolve(__dirname, "../lib"), results);

assert.deepStrictEqual(results, [], "No .orig source files should remain in lib/");
console.log("no_orig_files.test.js passed.");
