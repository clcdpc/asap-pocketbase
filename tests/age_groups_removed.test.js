const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.join(__dirname, "..");
const removedTerms = [
  "age" + "Groups",
  "age" + "Group",
  "age_" + "group",
  "age" + "group",
  "Age " + "Group",
  "Age " + "Groups",
  "audience_" + "groups",
  "audience" + "Group",
  "audience" + "Groups",
  "audience" + "Mode",
  "audience" + "Label",
  "default" + "Age" + "Groups",
  "normalize" + "Agegroup",
  "normalize" + "Age" + "Groups",
  "set" + "Age" + "Groups"
];

const activeRoots = [
  "lib",
  "pb_hooks",
  "pb_public",
  "pb_migrations"
];

const ignoredFiles = new Set([
  path.join(root, "pb_migrations", "202605130002_remove_age_groups.js")
]);

function walk(dir, files) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(fullPath, files);
      continue;
    }
    if (entry.isFile() && /\.(js|html|css)$/.test(entry.name)) {
      files.push(fullPath);
    }
  }
}

const files = [];
for (const activeRoot of activeRoots) {
  const fullPath = path.join(root, activeRoot);
  if (fs.existsSync(fullPath)) walk(fullPath, files);
}

const matches = [];
for (const file of files) {
  if (ignoredFiles.has(file)) continue;
  const source = fs.readFileSync(file, "utf8");
  for (const term of removedTerms) {
    if (source.includes(term)) {
      matches.push(path.relative(root, file) + " contains " + term);
    }
  }
}

assert.deepStrictEqual(matches, [], "retired patron option terms should not appear in active runtime/schema files");

const agents = fs.readFileSync(path.join(root, "AGENTS.md"), "utf8");
assert.ok(!agents.includes("audience_" + "groups"), "AGENTS.md should not describe the retired lookup collection as active");
assert.ok(!agents.includes("Age " + "Groups"), "AGENTS.md should not describe the retired patron option as active");

console.log("Retired patron option residue checks passed");
