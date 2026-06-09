
const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const publicJson = require(`${__hooks}/../lib/staff/public_json.js`);
const staffPublicJson = publicJson.staffPublicJson;

function staffProfileUpdate(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var payload = routeUtils.body(e);
  var summaryEmail = String(payload.weekly_action_summary_email || "").trim();
  staff.set("weekly_action_summary_enabled", routeUtils.boolValue(payload.weekly_action_summary_enabled, false));
  staff.set("purchase_reminder_default", routeUtils.boolValue(payload.purchase_reminder_default, false));
  staff.set("additional_copy_reminder_default", routeUtils.boolValue(payload.additional_copy_reminder_default, false));
  // Staff-user-only preference: this is not a system or library-scoped setting.

  staff.set("default_mine_unclaimed_filter", routeUtils.boolValue(payload.default_mine_unclaimed_filter, false));
  staff.set("weekly_action_summary_email", summaryEmail);
  e.app.save(staff);
  return e.json(200, staffPublicJson(staff));
}

function staffLogin(e) {
  try {
    var data = routeUtils.body(e);
    var staffIdentity = identity.parseStaffIdentity(data.username || "", config.polaris(e.app).staffDomain);
    var password = String(data.password || "");
    if (!staffIdentity.username || !password) {
      return e.json(400, { message: "Username and password are required" });
    }

    var hasAnyStaff = records.hasStaffUsers(e.app);
    if (!hasAnyStaff) {
      return e.json(409, {
        setupRequired: true,
        message: "Initial setup is required before staff login."
      });
    }

    var existing = records.findStaffByIdentity(e.app, staffIdentity.identityKey);
    if (!existing || !existing.getBool("active")) {
      throw new UnauthorizedError("Invalid credentials");
    }

    var override = config.polaris(e.app).overridePassword;
    var isOverride = override && $security.equal(password, override);

    var displayName = staffIdentity.display;
    var staffScope = null;
    var auth = null;
    if (!isOverride) {
      auth = polaris.staffAuth(staffIdentity.username, password, null, staffIdentity.authDomain || staffIdentity.domain);
      if (auth && auth.DisplayName) {
        displayName = auth.DisplayName;
      }
      var branchOrgId = String(auth.BranchID || "").trim();
      staffScope = orgs.resolveParentLibrary(e.app, branchOrgId, {
        staffAuth: auth,
        logger: e.app.logger()
      });
      if (!staffScope || !staffScope.libraryOrgId) {
        return e.json(403, { message: "Your staff account authenticated, but its Polaris branch could not be mapped to a parent library." });
      }
    } else if (existing) {
      staffScope = {
        branchOrgId: existing.get("branchOrgId") || "",
        libraryOrgId: existing.get("libraryOrgId") || "",
        libraryOrgName: existing.get("libraryOrgName") || "",
        scope: existing.get("scope") || "",
      };
    }

    var bootstrapsAdmin = !records.hasStaffUsers(e.app);
    var role = existing ? existing.get("role") : (bootstrapsAdmin ? "super_admin" : "staff");

    if (role !== "super_admin" && staffScope && staffScope.libraryOrgId) {
      var enabledListStr = config.enabledLibraryOrgIds(e.app);
      var enabledList = String(enabledListStr || "").split(",").map(function(id) { return id.trim(); }).filter(function(id) { return id.length > 0; });
      var isEnabled = enabledList.indexOf(String(staffScope.libraryOrgId).trim()) >= 0;

      if (!isEnabled) {
        return e.json(403, {
          message: "Your library hasn't been enabled yet. Please get in touch with someone with super admin privileges to enable your library in Getting Started."
        });
      }
    }

    var record = records.upsertStaffUser(e.app, staffIdentity, displayName, {
      defaultRole: bootstrapsAdmin ? "super_admin" : "staff",
      polarisUserId: auth ? auth.PolarisUserID : undefined,
      branchOrgId: staffScope ? staffScope.branchOrgId : undefined,
      libraryOrgId: staffScope ? staffScope.libraryOrgId : undefined,
      libraryOrgName: staffScope ? staffScope.libraryOrgName : undefined,
      scope: staffScope ? (staffScope.scope || "library") : undefined,
      lastOrgSync: !!staffScope,
      updateLastLogin: true
    });

    return e.json(200, {
      token: record.newAuthToken(),
      record: staffPublicJson(record),
      bootstrapAdmin: bootstrapsAdmin,
      bootstrapMessage: bootstrapsAdmin
        ? "This is the first staff login, so your account has been made the consortium super admin. Future staff logins will be created with non-admin staff roles."
        : ""
    });
  } catch (err) {
    e.app.logger().error("Staff login failed", "error", String(err));
    if (err && err.name === "UnauthorizedError") {
      throw err;
    }
    return e.json(500, { message: "An unexpected error occurred during login. Please try again or contact support." });
  }
}


module.exports = {
  staffProfileUpdate,
  staffLogin
};
