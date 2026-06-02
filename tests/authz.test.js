const assert = require("assert");
const authz = require("../lib/authz.js");

console.log("Running tests for authz.isSuperAdmin...");

// Mock PocketBase Record
function createMockRecord(roleValue) {
  return {
    get: function(key) {
      if (key === "role") return roleValue;
      return null;
    }
  };
}

let passed = 0;
let failed = 0;

const testCases = [
  { input: "super_admin", expected: true, description: "exact match" },
  { input: "SUPER_ADMIN", expected: true, description: "case-insensitivity" },
  { input: "Super_Admin", expected: true, description: "mixed case" },
  { input: "admin", expected: false, description: "admin role" },
  { input: "staff", expected: false, description: "staff role" },
  { input: "", expected: false, description: "empty string" },
  { input: null, expected: false, description: "null value" },
  { input: undefined, expected: false, description: "undefined value" },
];

for (const tc of testCases) {
  try {
    const record = createMockRecord(tc.input);
    const result = authz.isSuperAdmin(record);
    assert.strictEqual(result, tc.expected, `Expected ${tc.expected} for input '${tc.input}', got ${result}`);
    passed++;
  } catch (err) {
    console.error(`FAILED: ${tc.description}`);
    console.error(err.message);
    failed++;
  }
}

if (failed > 0) {
  console.error(`\n${failed} tests failed. ${passed} tests passed.`);
  process.exit(1);
} else {
  console.log(`All ${passed} tests passed for authz.isSuperAdmin.`);
}
