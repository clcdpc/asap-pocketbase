const jobs = require(`${__hooks}/../lib/jobs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

function runHoldCheck(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  return e.json(200, jobs.runScheduledHoldCheck(e.app));
}

function runWeeklyStaffActionSummary(e) {
  var secret = "";
  try {
    secret = String($os.getenv("ASAP_CRON_SECRET") || "").trim();
  } catch (err) { }
  var authorized = false;
  var authHeader = String(routeUtils.requestHeader(e, "Authorization") || "").trim();
  if (secret && authHeader === "Bearer " + secret) {
    authorized = true;
  } else {
    var auth = e.requestInfo().auth;
    authorized = !!(auth && auth.isSuperuser && auth.isSuperuser());
    if (!authorized && auth && auth.collection && auth.collection().name === "staff_users") {
      authorized = routeUtils.isSuperAdmin(auth);
    }
  }
  if (!authorized) {
    return e.json(401, { message: "Unauthorized" });
  }
  try {
    return e.json(200, jobs.runWeeklyStaffActionSummary(e.app, { force: routeUtils.boolValue(routeUtils.body(e).force, false) }));
  } catch (err) {
    e.app.logger().error("Weekly staff action summary job failed", "error", String(err));
    return e.json(400, { message: err.message || String(err) });
  }
}

function staffRunPromoterCheck(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    var auth = polaris.adminStaffAuth();
    var result = { promoted: 0 };
    jobs.processOutstandingPurchases(e.app, auth, result);
    return e.json(200, result);
  } catch (err) {
    return e.json(400, { message: err.message || String(err) });
  }
}

module.exports = {
  runHoldCheck: runHoldCheck,
  runWeeklyStaffActionSummary: runWeeklyStaffActionSummary,
  staffRunPromoterCheck: staffRunPromoterCheck
};
