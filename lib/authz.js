function requireAuth(e, collectionName) {
  var auth = e.requestInfo().auth;
  if (!auth || !auth.collection || auth.collection().name !== collectionName) {
    throw new UnauthorizedError("Unauthorized");
  }
  return auth;
}

function requireAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isAdminRole(staff)) {
    return null;
  }
  return staff;
}

function requireSuperAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isSuperAdmin(staff)) {
    return null;
  }
  return staff;
}

function isSuperAdmin(staff) {
  return String(staff.get("role") || "").toLowerCase() === "super_admin";
}

function isAdminRole(staff) {
  var role = String(staff.get("role") || "").toLowerCase();
  return role === "admin" || role === "super_admin";
}

function sameLibrary(staff, libraryOrgId) {
  if (isSuperAdmin(staff)) {
    return true;
  }
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  libraryOrgId = String(libraryOrgId || "").trim();
  return !!staffLibraryOrgId && !!libraryOrgId && staffLibraryOrgId === libraryOrgId;
}

function canAccessTitleRequest(staff, record) {
  return sameLibrary(staff, record.get("libraryOrgId"));
}

function requireTitleRequestAccess(e, staff, record) {
  if (!canAccessTitleRequest(staff, record)) {
    return e.json(404, { message: "Suggestion not found." });
  }
  return null;
}

module.exports = {
  requireAuth: requireAuth,
  requireAdminStaff: requireAdminStaff,
  requireSuperAdminStaff: requireSuperAdminStaff,
  isSuperAdmin: isSuperAdmin,
  isAdminRole: isAdminRole,
  sameLibrary: sameLibrary,
  canAccessTitleRequest: canAccessTitleRequest,
  requireTitleRequestAccess: requireTitleRequestAccess,
};
