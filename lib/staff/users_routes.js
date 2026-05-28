
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
const config = require(`${__hooks}/../lib/config.js`);
const publicJson = require(`${__hooks}/../lib/staff/public_json.js`);
const staffPublicJson = publicJson.staffPublicJson;

function staffUsersList(e) {
  try {
    var targetOrgId = String(routeUtils.queryValue(e, "orgId") || "").trim();
    e.app.logger().info("staffUsersList started", "orgId", targetOrgId);
    var admin = routeUtils.requireAdminStaff(e);
    if (!admin) {
      e.app.logger().warn("staffUsersList: Admin access required");
      return e.json(403, { message: "Admin access required" });
    }

    var isSuper = routeUtils.isSuperAdmin(admin);
    e.app.logger().info("staffUsersList: auth verified", "isAdminSuper", isSuper, "adminId", admin.id);

    var allStaff = records.listStaffUsers(e.app);
    e.app.logger().info("staffUsersList: allStaff fetched", "count", allStaff.length);

    var users = allStaff.filter(function (record) {
      try {
        var isSuperView = isSuper && (!targetOrgId || targetOrgId === "system" || routeUtils.sameLibrary(record, targetOrgId));
        var isAdminView = !isSuper && routeUtils.sameLibrary(admin, record.get("libraryOrgId"));
        return isSuperView || isAdminView;
      } catch (filterErr) {
        e.app.logger().error("staffUsersList filter error", "userId", record.id, "error", String(filterErr));
        return false;
      }
    });

    e.app.logger().info("staffUsersList: users filtered", "count", users.length);

    var totalAcrossSystem = undefined;
    if (isSuper && targetOrgId && targetOrgId !== "system") {
      totalAcrossSystem = allStaff.length;
    }

    var response = {
      canAssignSuperAdmin: isSuper,
      users: users.map(function(u) {
        try {
          return staffPublicJson(u);
        } catch (jsonErr) {
          e.app.logger().error("staffUsersList json error", "userId", u.id, "error", String(jsonErr));
          return { id: u.id, error: "failed to map" };
        }
      }),
      totalAcrossSystem: totalAcrossSystem
    };

    e.app.logger().info("staffUsersList: response prepared");
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("staffUsersList top-level failure", "error", String(err));
    return e.json(400, { message: "Failed to load staff users: " + String(err.message || err) });
  }
}


function staffUserRoleUpdate(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) {
    return e.json(403, { message: "Admin access required" });
  }

  var id = String(e.request.pathValue("id") || "").trim();
  var payload = routeUtils.body(e);
  var nextRole = String(payload.role || "").trim().toLowerCase();
  if (!id) {
    return e.json(400, { message: "Staff user id is required." });
  }
  if (["staff", "admin", "super_admin"].indexOf(nextRole) < 0) {
    return e.json(400, { message: "Role must be staff, admin, or super admin." });
  }
  if (nextRole === "super_admin" && !routeUtils.isSuperAdmin(admin)) {
    return e.json(403, { message: "Only a super admin can assign the super admin role." });
  }

  var record;
  try {
    record = e.app.findRecordById("staff_users", id);
  } catch (err) {
    return e.json(404, { message: "Staff user not found." });
  }
  if (!routeUtils.isSuperAdmin(admin) && !routeUtils.sameLibrary(admin, record.get("libraryOrgId"))) {
    return e.json(404, { message: "Staff user not found." });
  }

  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && !routeUtils.isSuperAdmin(admin)) {
    return e.json(403, { message: "Only a super admin can modify a super admin's role." });
  }
  if (currentRole === "super_admin" && nextRole !== "super_admin" && records.countSuperAdminUsers(e.app) <= 1) {
    return e.json(400, { message: "At least one super admin user must remain." });
  }

  record.set("role", nextRole);
  e.app.save(record);

  return e.json(200, staffPublicJson(record));
}

function staffUserCreate(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });

  var payload = routeUtils.body(e);
  var staffDomain = config.polaris(e.app).staffDomain;
  var parsed = identity.parseStaffIdentity(payload.username || payload.identity || "", staffDomain);
  if (!parsed.username || !parsed.identityKey) {
    return e.json(400, { message: "Username or identity is required." });
  }

  var isSuper = routeUtils.isSuperAdmin(admin);
  var libraryOrgId = isSuper ? String(payload.libraryOrgId || "").trim() : String(admin.get("libraryOrgId") || "").trim();
  var libraryOrgName = isSuper ? String(payload.libraryOrgName || "").trim() : String(admin.get("libraryOrgName") || "").trim();
  var role = String(payload.role || "staff").trim().toLowerCase();

  if (["staff", "admin", "super_admin"].indexOf(role) < 0) {
    return e.json(400, { message: "Role must be staff, admin, or super admin." });
  }
  if (role === "super_admin" && !isSuper) {
    return e.json(403, { message: "Only a super admin can assign the super admin role." });
  }

  // Find existing
  var existing = records.findStaffByIdentity(e.app, parsed.identityKey);
  if (existing) {
    var existingLibraryOrgId = String(existing.get("libraryOrgId") || "").trim();
    if (!isSuper && existingLibraryOrgId !== libraryOrgId) {
      return e.json(403, { message: "This user already exists in another library. You do not have permission to manage this identity." });
    }
  }

  try {
    var displayName = String(payload.displayName || "").trim() || (existing ? existing.get("displayName") : "") || parsed.display;
    var record = records.upsertStaffUser(e.app, parsed, displayName, {
      role: role,
      scope: role === "super_admin" ? "system" : "library",
      libraryOrgId: role === "super_admin" ? "" : libraryOrgId,
      libraryOrgName: role === "super_admin" ? "System" : libraryOrgName,
      active: true
    });

    if (!isSuper) {
      e.app.logger().info("Staff identity provisioned by library admin",
        "admin", admin.get("username"),
        "library", libraryOrgName,
        "targetIdentity", parsed.identityKey,
        "targetRole", role
      );
    }

    return e.json(201, staffPublicJson(record));
  } catch (err) {
    e.app.logger().error("Staff provisioning failed", "identityKey", parsed.identityKey, "error", String(err));
    return e.json(400, { message: err.message || "Could not provision staff user." });
  }
}


function staffUserDelete(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });
  var id = String(e.request.pathValue("id") || "").trim();
  if (!id) return e.json(400, { message: "Staff user id is required." });
  var record;
  try { record = e.app.findRecordById("staff_users", id); } catch (err) { return e.json(404, { message: "Staff user not found." }); }
  if (!routeUtils.isSuperAdmin(admin) && !routeUtils.sameLibrary(admin, record.get("libraryOrgId"))) return e.json(404, { message: "Staff user not found." });
  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && !routeUtils.isSuperAdmin(admin)) return e.json(403, { message: "Only a super admin can delete a super admin." });
  if (currentRole === "super_admin" && records.countSuperAdminUsers(e.app) <= 1) return e.json(400, { message: "At least one super admin user must remain." });
  e.app.delete(record);
  return e.json(200, { success: true });
}



module.exports = {
  staffUsersList,
  staffUserRoleUpdate,
  staffUserCreate,
  staffUserDelete
};
