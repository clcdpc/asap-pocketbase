const assert = require("assert");
const path = require("path");
const fs = require("fs");

// Mock environment
global.__hooks = path.resolve(__dirname, "../pb_hooks");
const routeUtils = require("../lib/route_utils.js");
const assignmentPolicy = require("../lib/staff/assignment_policy.js");

function testAssignmentPolicy() {
  console.log("Running staff assignment policy tests...");

  // Mock staff
  const jplStaff = {
    id: "jpl-1",
    get: (k) => k === "libraryOrgId" ? "jpl" : "",
    getBool: (k) => k === "active" ? true : false
  };

  const genericStaff = {
    id: "gen-1",
    get: (k) => k === "libraryOrgId" ? "gen" : "",
    getBool: (k) => k === "active" ? true : false
  };

  const inactiveStaff = {
    id: "inactive-1",
    get: (k) => k === "libraryOrgId" ? "jpl" : "",
    getBool: (k) => k === "active" ? false : true
  };

  const superAdmin = {
    id: "super-1",
    get: (k) => k === "role" ? "super_admin" : "",
    getBool: (k) => k === "active" ? true : false
  };

  // Mock record
  const jplRecord = {
    get: (k) => {
      if (k === "status") return "suggestion";
      if (k === "libraryOrgId") return "jpl";
      return "";
    }
  };

  const closedRecord = {
    get: (k) => {
      if (k === "status") return "closed";
      if (k === "libraryOrgId") return "jpl";
      return "";
    }
  };

  // 1. assertActiveStaff
  assignmentPolicy.assertActiveStaff(jplStaff, "Active Staff");
  assert.throws(() => assignmentPolicy.assertActiveStaff(null, "Missing Staff"), /Missing Staff not found/);
  assert.throws(() => assignmentPolicy.assertActiveStaff(inactiveStaff, "Inactive Staff"), /Inactive Staff not found/);

  // 2. assertOpenItem
  assignmentPolicy.assertOpenItem(jplRecord);
  assert.throws(() => assignmentPolicy.assertOpenItem(closedRecord), /Closed items cannot be assigned/);

  // 3. assertSameLibraryAssignment
  // Happy path: same library
  assignmentPolicy.assertSameLibraryAssignment(jplStaff, jplStaff, "jpl");
  
  // Actor in different library
  assert.throws(() => assignmentPolicy.assertSameLibraryAssignment(genericStaff, jplStaff, "jpl"), /Cannot assign an item outside your library/);
  
  // Assignee in different library
  assert.throws(() => assignmentPolicy.assertSameLibraryAssignment(jplStaff, genericStaff, "jpl"), /Cannot assign to staff in a different library/);
  
  // Super admin can assign items from any library
  assignmentPolicy.assertSameLibraryAssignment(superAdmin, jplStaff, "jpl");
  
  // But Super admin cannot assign TO a staff in a different library (Option 1)
  assert.throws(() => assignmentPolicy.assertSameLibraryAssignment(superAdmin, genericStaff, "jpl"), /Cannot assign to staff in a different library/);

  console.log("Assignment policy tests passed.");
}

try {
  testAssignmentPolicy();
} catch (err) {
  console.error("Assignment policy tests failed:", err);
  process.exit(1);
}
