var __hooks = typeof __hooks !== "undefined" ? __hooks : __dirname + "/../../pb_hooks";


const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
// const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
// const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);

function staffLogin(e) {
  var data = routeUtils.body(e);
  var staffIdentity = identity.parseStaffIdentity(data.username || "", config.polaris().staffDomain);
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

  var override = config.polaris().overridePassword;
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
    record: record,
    bootstrapAdmin: bootstrapsAdmin,
    bootstrapMessage: bootstrapsAdmin
      ? "This is the first staff login, so your account has been made the consortium super admin. Future staff logins will be created with non-admin staff roles."
      : ""
  });
}

function staffPublicJson(record) {
  return {
    id: record.id,
    username: record.get("username") || "",
    domain: record.get("domain") || "",
    identityKey: record.get("identityKey") || "",
    displayName: record.get("displayName") || "",
    role: record.get("role") || "staff",
    active: !!record.getBool("active"),
    branchOrgId: record.get("branchOrgId") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    scope: record.get("scope") || "",
    lastLogin: record.get("lastLogin") || "",
    lastPolarisLogin: record.get("lastPolarisLogin") || "",

    weekly_action_summary_enabled: !!record.getBool("weekly_action_summary_enabled"),
    purchase_reminder_default: !!record.getBool("purchase_reminder_default"),
    default_mine_unclaimed_filter: !!record.getBool("default_mine_unclaimed_filter"),
    weekly_action_summary_email: record.get("weekly_action_summary_email") || "",
  };
}

module.exports = {
  staffLogin: staffLogin,
  staffPublicJson: staffPublicJson
};
