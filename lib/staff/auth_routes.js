
const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const publicJson = require(`${__hooks}/../lib/staff/public_json.js`);
const staffPublicJson = publicJson.staffPublicJson;
const splitList = require(`${__hooks}/../lib/split-list.js`);

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
  var data = routeUtils.body(e);
  var polarisConfig = config.polaris(e.app);
  var staffIdentity = identity.parseStaffIdentity(data.username || "", polarisConfig.staffDomain);
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
    e.app.logger().warn("Staff login denied before Polaris authentication",
      "identityKey", staffIdentity.identityKey,
      "reason", existing ? "inactive_staff_account" : "staff_identity_not_found");
    return e.json(401, { message: "Unable to sign in. Check your credentials and ask an administrator to verify your full domain and username are active in Staff Access." });
  }

  var override = polarisConfig.overridePassword;
  var isOverride = override && $security.equal(password, override);

  var displayName = staffIdentity.display;
  var staffScope = null;
  var auth = null;
  if (!isOverride) {
    try {
      auth = polaris.staffAuth(staffIdentity.username, password, polarisConfig, staffIdentity.authDomain || staffIdentity.domain);
    } catch (err) {
      e.app.logger().warn("Staff login Polaris authentication request failed", "identityKey", staffIdentity.identityKey,
        "papiErrorCode", err.papiErrorCode || "");
      if (err.papiErrorCode === -8001) {
        return e.json(403, { message: "Polaris user is not permitted (Code: -8001). Ask an administrator to verify your Polaris staff domain, username, and API access.", papiErrorCode: -8001 });
      }
      return e.json(502, { message: "Polaris staff authentication failed. Ask an administrator to check the Polaris API logs and connection settings." });
    }
    if (!auth || !auth.AccessToken) {
      e.app.logger().warn("Staff login Polaris response missing access token", "identityKey", staffIdentity.identityKey);
      return e.json(502, { message: "Polaris did not return a staff authentication token." });
    }
    if (auth && auth.DisplayName) {
      displayName = auth.DisplayName;
    }
    var branchOrgId = String(auth.BranchID || "").trim();
    staffScope = orgs.resolveParentLibrary(e.app, branchOrgId, {
      staffAuth: auth,
      logger: e.app.logger()
    });
    if (!staffScope || !staffScope.libraryOrgId) {
      e.app.logger().warn("Staff login library mapping failed", "identityKey", staffIdentity.identityKey, "branchOrgId", branchOrgId);
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
    var enabledList = splitList.split(enabledListStr);
    var isEnabled = enabledList.indexOf(String(staffScope.libraryOrgId).trim()) >= 0;
    
    if (!isEnabled) {
      e.app.logger().warn("Staff login library disabled", "identityKey", staffIdentity.identityKey, "libraryOrgId", staffScope.libraryOrgId);
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
}


module.exports = {
  staffProfileUpdate,
  staffLogin
};
