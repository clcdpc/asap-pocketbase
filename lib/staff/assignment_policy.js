const routeUtils = require("../route_utils.js");

// Assignment is claim transfer. Any active staff member may transfer the claim
// for an open item within their own library to another active staff member
// in that same library.

function assertActiveStaff(staff, label) {
  if (!staff || staff.getBool("active") === false) {
    var err = new Error((label || "Staff member") + " not found or inactive.");
    err.statusCode = 400;
    throw err;
  }
}

function assertOpenItem(record) {
  var status = String(record.get("status") || "").trim().toLowerCase();
  if (status === "closed") {
    var err = new Error("Closed items cannot be assigned.");
    err.statusCode = 409;
    throw err;
  }
}

function assertSameLibraryAssignment(actor, assignee, libraryOrgId) {
  libraryOrgId = String(libraryOrgId || "").trim();

  if (!libraryOrgId) {
    var err = new Error("Item library is required for assignment.");
    err.statusCode = 400;
    throw err;
  }

  if (!routeUtils.isSuperAdmin(actor) && !routeUtils.sameLibrary(actor, libraryOrgId)) {
    var err = new Error("Cannot assign an item outside your library.");
    err.statusCode = 403;
    throw err;
  }

  if (!routeUtils.sameLibrary(assignee, libraryOrgId)) {
    var err = new Error("Cannot assign to staff in a different library.");
    err.statusCode = 403;
    throw err;
  }
}

module.exports = {
  assertActiveStaff: assertActiveStaff,
  assertOpenItem: assertOpenItem,
  assertSameLibraryAssignment: assertSameLibraryAssignment
};
