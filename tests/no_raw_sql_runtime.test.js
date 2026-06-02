const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const scanRoots = ["lib", "pb_hooks", "pb_public", "tests"];
const allowedFiles = new Set([
  path.join("tests", "no_raw_sql_runtime.test.js")
]);

function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return walk(full);
    if (!entry.isFile()) return [];
    return /\.(js|mjs|cjs)$/.test(entry.name) ? [full] : [];
  });
}

function stripComments(source) {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/(^|[^:])\/\/.*$/gm, "$1");
}

const forbiddenPatterns = [
  { name: "PocketBase raw DB handle", pattern: /\b(?:app|e\.app|\$app)\.db\s*\(/ },
  { name: "raw query builder", pattern: /\bnewQuery\s*\(/ },
  { name: "SQLite CLI/module", pattern: /\b(sqlite3|better-sqlite3|sqlite)\b/ },
  { name: "SELECT statement", pattern: /\bSELECT\s+[\s\S]{0,120}\bFROM\b/i },
  { name: "INSERT statement", pattern: /\bINSERT\s+INTO\b/i },
  { name: "UPDATE statement", pattern: /\bUPDATE\s+[A-Za-z_][A-Za-z0-9_]*\s+SET\b/i },
  { name: "DELETE statement", pattern: /\bDELETE\s+FROM\b/i },
  { name: "DDL statement", pattern: /\b(?:CREATE|ALTER|DROP)\s+(?:TABLE|INDEX)\b/i }
];

const violations = [];

for (const scanRoot of scanRoots) {
  const dir = path.join(root, scanRoot);
  for (const file of walk(dir)) {
    const rel = path.relative(root, file);
    if (allowedFiles.has(rel)) continue;

    const source = stripComments(fs.readFileSync(file, "utf8"));
    forbiddenPatterns.forEach((rule) => {
      if (rule.pattern.test(source)) {
        violations.push(`${rel}: ${rule.name}`);
      }
    });
  }
}

assert.strictEqual(
  violations.length,
  0,
  [
    "Raw SQL or direct database access detected outside PocketBase migrations/schema setup.",
    "Use PocketBase record/collection APIs such as findRecordById, findFirstRecordByFilter, findRecordsByFilter, app.save, and app.delete.",
    "",
    ...violations
  ].join("\n")
);

console.log("No raw SQL runtime usage detected.");
