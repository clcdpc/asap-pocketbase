const config = require(`${__hooks}/../lib/config.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const patronCodes = require(`${__hooks}/../lib/patron_codes.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const jobRuns = require(`${__hooks}/../lib/job_runs.js`);
const startJobRun = jobRuns.startJobRun;
const finishJobRun = jobRuns.finishJobRun;

const jobQueue = require(`${__hooks}/../lib/job_queue.js`);
const processPagedQueue = jobQueue.processPagedQueue;

const jobResults = require("./job_results.js");

// Sub-modules containing modularized tasks
const weeklySummary = require("./jobs/weekly_summary.js");
const timeouts = require("./jobs/timeouts.js");
const isbnChecks = require("./jobs/isbn_checks.js");
const purchasePromoter = require("./jobs/purchase_promoter.js");
const holdPlacement = require("./jobs/hold_placement.js");
const fulfillmentTracker = require("./jobs/fulfillment_tracker.js");

function runScheduledHoldCheck(app) {
  var run = startJobRun(app, "asap-hold-check");
  var result = jobResults.createHoldCheckResult();

  try {
    // 1. Process timeouts and auto-closes
    timeouts.processOutstandingTimeout(app, result);
    timeouts.processPendingHoldTimeout(app, result);
    timeouts.processHoldPickupTimeout(app, result);
    timeouts.processAdditionalCopyTimeout(app, result);

    var staff;
    try {
      staff = polaris.adminStaffAuth(app);
    } catch (authErr) {
      result.errors++;
      result.polarisSkipped = true;
      result.error = authErr.message || String(authErr);
      app.logger().error("ASAP scheduled check skipped Polaris-dependent steps", "error", String(authErr));
      finishJobRun(app, run, "failed", result, result.error);
      return result;
    }

    // 2. Process pending ISBN checks (verifying identifier tags)
    isbnChecks.processPendingIsbnChecks(app, staff, result);

    // 3. Process outstanding purchase promoter (evaluating candidate matches)
    purchasePromoter.processOutstandingPurchases(app, staff, result);

    // 4. Process pending holds (automatically placing requests on Polaris)
    holdPlacement.processPendingHolds(app, staff, result);

    // 5. Process fulfilled holds (checking checked-out items)
    fulfillmentTracker.processCheckedOut(app, staff, result);

    finishJobRun(app, run, "success", result, "");
    return result;
  } catch (err) {
    result.errors++;
    app.logger().error("ASAP scheduled check failed", "error", String(err));
    finishJobRun(app, run, "failed", result, err.message || String(err));
    throw err;
  }
}

function runScheduledOrganizationSync(app) {
  var run = startJobRun(app, "asap-org-sync");
  var result = jobResults.createOrgSyncResult();
  try {
    var staff = polaris.adminStaffAuth(app);
    orgs.syncOrganizations(app, staff, result);
    try {
      patronCodes.syncPatronCodes(app, staff);
    } catch (patronCodeErr) {
      result.errors++;
      app.logger().warn("ASAP patron code sync failed during scheduled organization sync", "error", String(patronCodeErr));
    }
    finishJobRun(app, run, "success", result, "");
    return result;
  } catch (err) {
    result.errors++;
    app.logger().error("ASAP organization sync failed", "error", String(err));
    finishJobRun(app, run, "failed", result, err.message || String(err));
    throw err;
  }
}

module.exports = {
  buildWeeklyStaffActionSummary: weeklySummary.buildWeeklyStaffActionSummary,
  classifyPolarisHoldResult: holdPlacement.classifyPolarisHoldResult,
  _processPagedQueue: processPagedQueue,
  runScheduledHoldCheck: runScheduledHoldCheck,
  runScheduledOrganizationSync: runScheduledOrganizationSync,
  runWeeklyStaffActionSummary: weeklySummary.runWeeklyStaffActionSummary,
  processOutstandingPurchases: purchasePromoter.processOutstandingPurchases,
  processPendingSuggestionIsbnChecks: isbnChecks.processPendingSuggestionIsbnChecks,
  processPendingHolds: holdPlacement.processPendingHolds,
  promoteRequestNow: purchasePromoter.promoteRequestNow,
};
