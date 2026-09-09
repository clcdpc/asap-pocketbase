const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
const records = require(`${__hooks}/../lib/records.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const helpers = require(`${__hooks}/../lib/polaris/helpers.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const splitList = require(`${__hooks}/../lib/split-list.js`);

function staffLoginDiagnostics(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required." });
  }
  e.response.header().set("Cache-Control", "no-store");
  var data = routeUtils.body(e);
  var report = { timestamp: new Date().toISOString(), stage: "configuration", ok: false };
  try {
    var c = helpers.cfg(config.polaris(e.app));
    var parsed = identity.parseStaffIdentity(data.username, c.staffDomain);
    var password = String(data.password || "");
    if (!parsed.username || !password) {
      return e.json(400, { message: "Staff username and password are required." });
    }
    // Explicit allowlist: never return config objects, upstream payloads, or headers.
    report.request = {
      method: "POST",
      url: helpers.endpoint("protected", "authenticator/staff", c).full,
      domain: parsed.authDomain || parsed.domain,
      username: parsed.username,
      identityKey: parsed.identityKey,
      authentication: "PWS signed request with Domain, Username, Password JSON body",
      overridePasswordUsed: false
    };
    report.stage = "local_account";
    var existing = records.findStaffByIdentity(e.app, parsed.identityKey);
    report.localAccount = { found: !!existing, active: !!(existing && existing.getBool("active")) };
    if (!report.localAccount.active) {
      report.message = "Normal staff login stops here: the exact staff identity must exist and be active in Staff Access.";
      return e.json(200, report);
    }
    report.stage = "polaris_authentication";
    var auth = polaris.staffAuth(parsed.username, password, c, parsed.authDomain || parsed.domain);
    report.polaris = {
      accessTokenPresent: !!(auth && auth.AccessToken),
      branchId: String(auth && auth.BranchID || ""),
      polarisUserId: String(auth && auth.PolarisUserID || "")
    };
    if (!report.polaris.accessTokenPresent) {
      report.message = "Polaris returned no staff access token.";
      return e.json(200, report);
    }
    report.stage = "library_mapping";
    // Inspect current organization data without syncing or saving records.
    var scope = orgs.resolveParentLibrary(e.app, report.polaris.branchId, { syncIfMissing: false });
    report.library = { id: String(scope && scope.libraryOrgId || ""), mappingFound: !!(scope && scope.libraryOrgId) };
    if (!report.library.mappingFound) {
      report.message = "Polaris authentication succeeded, but cached organizations do not map this branch to a library. Normal login may attempt an organization sync.";
      return e.json(200, report);
    }
    report.stage = "library_access";
    report.library.enabled = splitList.split(config.enabledLibraryOrgIds(e.app)).indexOf(report.library.id) >= 0;
    report.library.superAdminExemption = existing.get("role") === "super_admin";
    if (!report.library.enabled && !report.library.superAdminExemption) {
      report.message = "Polaris authentication succeeded, but the library is not enabled in ASAP.";
      return e.json(200, report);
    }
    report.ok = true;
    report.stage = "complete";
    report.message = "Authentication and access checks passed. This test does not save the staff record or create an ASAP session.";
  } catch (err) {
    report.papiErrorCode = typeof err.papiErrorCode === "number" ? err.papiErrorCode : null;
    report.message = report.papiErrorCode === -8001
      ? "Polaris user is not permitted. Compare this request's domain, username, and URL with the executed Swagger request."
      : "The diagnostic failed at the reported stage. Check the matching PocketBase server log for details.";
    // Raw exception text may contain credentials or tokens supplied by an upstream server.
    e.app.logger().warn("Staff login diagnostic failed", "stage", report.stage, "papiErrorCode", report.papiErrorCode);
  }
  return e.json(200, report);
}

module.exports = { staffLoginDiagnostics };
