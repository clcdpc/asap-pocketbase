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

function staffProfileUpdate(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var payload = routeUtils.body(e);
  var summaryEmail = String(payload.weekly_action_summary_email || "").trim();
  staff.set("weekly_action_summary_enabled", routeUtils.boolValue(payload.weekly_action_summary_enabled, false));
  staff.set("purchase_reminder_default", routeUtils.boolValue(payload.purchase_reminder_default, false));
  // Staff-user-only preference: this is not a system or library-scoped setting.
  staff.set("default_mine_unclaimed_filter", routeUtils.boolValue(payload.default_mine_unclaimed_filter, false));
  staff.set("weekly_action_summary_email", summaryEmail);
  e.app.save(staff);
  return e.json(200, require(`./auth.js`).staffPublicJson(staff));
}

module.exports = {
  staffProfileUpdate: staffProfileUpdate
};
