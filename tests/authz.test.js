const assert = require("assert");
const authz = require("../lib/authz.js");

class MockStaff {
  constructor(role) {
    this.role = role;
  }
  get(key) {
    if (key === "role") return this.role;
    return null;
  }
}

function runTests() {
  // Test isAdminRole

  // Happy paths
  assert.strictEqual(authz.isAdminRole(new MockStaff("admin")), true, "Should return true for admin");
  assert.strictEqual(authz.isAdminRole(new MockStaff("super_admin")), true, "Should return true for super_admin");

  // Case insensitivity
  assert.strictEqual(authz.isAdminRole(new MockStaff("ADMIN")), true, "Should be case-insensitive for admin");
  assert.strictEqual(authz.isAdminRole(new MockStaff("SUPER_ADMIN")), true, "Should be case-insensitive for super_admin");
  assert.strictEqual(authz.isAdminRole(new MockStaff("Admin")), true, "Should be case-insensitive for Admin");

  // Other non-admin roles
  assert.strictEqual(authz.isAdminRole(new MockStaff("staff")), false, "Should return false for staff");
  assert.strictEqual(authz.isAdminRole(new MockStaff("user")), false, "Should return false for user");
  assert.strictEqual(authz.isAdminRole(new MockStaff("librarian")), false, "Should return false for librarian");

  // Edge cases
  assert.strictEqual(authz.isAdminRole(new MockStaff(null)), false, "Should handle null role");
  assert.strictEqual(authz.isAdminRole(new MockStaff("")), false, "Should handle empty string role");
  assert.strictEqual(authz.isAdminRole(new MockStaff(undefined)), false, "Should handle undefined role");

  // Also good to have coverage for isSuperAdmin since it's used in isAdminRole in a way but independent function
  assert.strictEqual(authz.isSuperAdmin(new MockStaff("super_admin")), true, "Should return true for super_admin");
  assert.strictEqual(authz.isSuperAdmin(new MockStaff("SUPER_ADMIN")), true, "Should be case-insensitive for super_admin");
  assert.strictEqual(authz.isSuperAdmin(new MockStaff("admin")), false, "admin is not super_admin");

  console.log("authz.test.js passed.");
}

runTests();
