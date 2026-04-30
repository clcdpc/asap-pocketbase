const config = require(`${__hooks}/lib/config.js`);
const mail = require(`${__hooks}/lib/mail.js`);
const orgs = require(`${__hooks}/lib/orgs.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);
const records = require(`${__hooks}/lib/records.js`);

const POLARIS_TAG_FOUND = "dupe found in Polaris";
const POLARIS_TAG_NOT_FOUND = "ISBN not found in system";

function runScheduledHoldCheck(app) {
  var jobRun = startJobRun(app, "asap-hold-check");
  var result = {
    holdsPlaced: 0,
    checkoutClosures: 0,
    holdPickupTimeouts: 0,
    promoted: 0,
    timedOut: 0,
    skipped: 0,
    isbnChecksFound: 0,
    isbnChecksNotFound: 0,
    errors: 0,
  };

  try {
    var staff = polaris.adminStaffAuth();
    processOutstandingTimeout(app, result);
    processHoldPickupTimeout(app, result);
    processPendingHoldTimeout(app, result);
    processPendingIsbnChecks(app, staff, result);
    processOutstandingPurchases(app, staff, result);
    processPendingSuggestionIsbnChecks(app, staff, result);
    processPendingHolds(app, staff, result);
    processCheckedOut(app, staff, result);
    app.logger().info("ASAP hold check completed", "result", JSON.stringify(result));
    finishJobRun(app, jobRun, "success", result, "");
    return result;
  } catch (err) {
    result.errors++;
    finishJobRun(app, jobRun, "failed", result, err.message || String(err));
    throw err;
  }
}

function runScheduledOrganizationSync(app) {
  var jobRun = startJobRun(app, "asap-organization-sync");
  try {
    var result = orgs.syncOrganizations(app, polaris.adminStaffAuth());
    result.success = true;
    app.logger().info("ASAP Polaris organization sync completed", "result", JSON.stringify(result));
    finishJobRun(app, jobRun, "success", result, "");
    return result;
  } catch (err) {
    var failed = {
      success: false,
      synced: 0,
      error: err.message || String(err),
    };
    app.logger().error("ASAP Polaris organization sync failed", "error", String(err));
    finishJobRun(app, jobRun, "failed", failed, failed.error);
    return failed;
  }
}

function startJobRun(app, name) {
  try {
    var record = new Record(app.findCollectionByNameOrId("job_runs"));
    record.set("jobName", name);
    record.set("status", "running");
    record.set("startedAt", new Date().toISOString());
    app.save(record);
    return record;
  } catch (err) {
    return null;
  }
}

function finishJobRun(app, record, status, summary, error) {
  if (!record) return;
  try {
    record.set("status", status);
    record.set("finishedAt", new Date().toISOString());
    record.set("summary", summary || {});
    record.set("error", error || "");
    app.save(record);
  } catch (err) {}
}

function dateOnly(date) {
  return date.toISOString().slice(0, 10);
}

function startOfLocalDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

function weeklySummaryPeriod(now) {
  now = now || new Date();
  var end = startOfLocalDay(now);
  end.setDate(end.getDate() + 1);
  var start = new Date(end);
  start.setDate(start.getDate() - 7);
  return {
    start: start,
    end: end,
    sundayKey: dateOnly(now),
  };
}

function staffViewUrl(app, stage) {
  var url = config.staffUrl(app);
  var separator = url.indexOf("?") >= 0 ? "&" : "?";
  return url + separator + "stage=" + encodeURIComponent(stage);
}

function cleanSummaryValue(value) {
  return String(value === undefined || value === null ? "" : value).replace(/[<>]/g, "").trim();
}

function summaryItem(record) {
  return {
    title: cleanSummaryValue(record.get("title")) || "Untitled",
    author: cleanSummaryValue(record.get("author")),
  };
}

function listOpenRequests(app, status, sort) {
  var items = [];
  var limit = 500;
  var offset = 0;
  while (true) {
    var page = app.findRecordsByFilter(
      "title_requests",
      "status = {:status}",
      sort || "-created",
      limit,
      offset,
      { status: status }
    );
    if (!page.length) break;
    items = items.concat(page);
    if (page.length < limit) break;
    offset += limit;
  }
  return items;
}

function buildWeeklyStaffActionSummary(app, options) {
  options = options || {};
  var newSubmissions = listOpenRequests(app, records.STATUS.SUGGESTION, "-created");
  var purchasesWithoutBibs = listOpenRequests(app, records.STATUS.OUTSTANDING_PURCHASE, "-updated").filter(function (record) {
    return !String(record.get("bibid") || "").trim();
  });

  return {
    newSubmissionsCount: newSubmissions.length,
    newSubmissionSample: newSubmissions.slice(0, 5).map(summaryItem),
    purchasesWithoutBibsCount: purchasesWithoutBibs.length,
    purchasesWithoutBibsSample: purchasesWithoutBibs.slice(0, 5).map(summaryItem),
    newSubmissionsUrl: staffViewUrl(app, "submitted"),
    purchasesWithoutBibsUrl: staffViewUrl(app, "purchased_waiting_for_bib"),
  };
}

function hasWeeklyActionItems(summary) {
  return !!summary && (summary.newSubmissionsCount > 0 || summary.purchasesWithoutBibsCount > 0);
}

function formatSummaryLines(items) {
  if (!items.length) return ["None"];
  var lines = [];
  for (var i = 0; i < items.length; i++) {
    var item = items[i];
    lines.push((i + 1) + ". " + item.title + (item.author ? " - " + item.author : ""));
  }
  return lines;
}

function weeklySummarySubject(summary) {
  return "Weekly ASAP action summary: " + summary.newSubmissionsCount + " new, " + summary.purchasesWithoutBibsCount + " awaiting bibs";
}

function weeklySummaryText(summary) {
  return [
    "Weekly ASAP action summary",
    "",
    "There are items that may need staff attention.",
    "",
    "New submissions",
    summary.newSubmissionsCount + " active requests",
    "Five most recent:",
  ].concat(formatSummaryLines(summary.newSubmissionSample)).concat([
    "",
    "View new submissions:",
    summary.newSubmissionsUrl,
    "",
    "Approved purchases without bibs",
    summary.purchasesWithoutBibsCount + " active requests",
    "Five most recent:",
  ]).concat(formatSummaryLines(summary.purchasesWithoutBibsSample)).concat([
    "",
    "View purchases awaiting bibs:",
    summary.purchasesWithoutBibsUrl,
  ]).join("\n");
}

function escapeHtml(value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function weeklySummaryHtmlList(items) {
  if (!items.length) return "<p>None</p>";
  var html = "<ol>";
  for (var i = 0; i < items.length; i++) {
    html += "<li>" + escapeHtml(items[i].title) + (items[i].author ? " - " + escapeHtml(items[i].author) : "") + "</li>";
  }
  return html + "</ol>";
}

function weeklySummaryHtml(summary) {
  return [
    "<h1>Weekly ASAP action summary</h1>",
    "<p>There are items that may need staff attention.</p>",
    "<h2>New submissions</h2>",
    "<p>" + summary.newSubmissionsCount + " active requests</p>",
    "<p><strong>Five most recent:</strong></p>",
    weeklySummaryHtmlList(summary.newSubmissionSample),
    '<p><a href="' + escapeHtml(summary.newSubmissionsUrl) + '">View new submissions</a></p>',
    "<h2>Approved purchases without bibs</h2>",
    "<p>" + summary.purchasesWithoutBibsCount + " active requests</p>",
    "<p><strong>Five most recent:</strong></p>",
    weeklySummaryHtmlList(summary.purchasesWithoutBibsSample),
    '<p><a href="' + escapeHtml(summary.purchasesWithoutBibsUrl) + '">View purchases awaiting bibs</a></p>',
  ].join("\n");
}

function completedWeeklyRunExists(app, jobKey) {
  try {
    var existing = app.findRecordsByFilter(
      "scheduled_email_runs",
      "job_key = {:jobKey} && (status = 'success' || status = 'partial_failure' || status = 'skipped')",
      "",
      1,
      0,
      { jobKey: jobKey }
    );
    return existing.length > 0;
  } catch (err) {
    return false;
  }
}

function createScheduledEmailRun(app, jobKey, period) {
  var record = new Record(app.findCollectionByNameOrId("scheduled_email_runs"));
  record.set("job_key", jobKey);
  record.set("period_start", period.start.toISOString());
  record.set("period_end", period.end.toISOString());
  record.set("started_at", new Date().toISOString());
  record.set("status", "running");
  record.set("recipient_count", 0);
  app.save(record);
  return record;
}

function finishScheduledEmailRun(app, run, status, recipientCount, error) {
  if (!run) return;
  run.set("completed_at", new Date().toISOString());
  run.set("status", status);
  run.set("recipient_count", recipientCount || 0);
  run.set("error", error || "");
  app.save(run);
}

function recordScheduledEmailDelivery(app, run, staff, email, status, error) {
  try {
    var delivery = new Record(app.findCollectionByNameOrId("scheduled_email_deliveries"));
    delivery.set("run", run.id);
    delivery.set("staff_user", staff && staff.id ? staff.id : "");
    delivery.set("email", email || "");
    delivery.set("status", status || "sent");
    delivery.set("error", error || "");
    delivery.set("sent_at", new Date().toISOString());
    app.save(delivery);
  } catch (err) {}
}

function optedInStaff(app) {
  var users = [];
  var limit = 200;
  var offset = 0;
  while (true) {
    var page = app.findRecordsByFilter(
      "staff_users",
      "weekly_action_summary_enabled = true && weekly_action_summary_email != '' && verified = true",
      "username",
      limit,
      offset
    );
    if (!page.length) break;
    users = users.concat(page);
    if (page.length < limit) break;
    offset += limit;
  }
  return users;
}

function runWeeklyStaffActionSummary(app, options) {
  options = options || {};
  var period = weeklySummaryPeriod(options.now || new Date());
  var jobKey = "weekly_staff_action_summary:" + period.sundayKey;
  var runJobKey = options.force ? jobKey + ":force:" + Date.now() : jobKey;
  var result = {
    jobKey: runJobKey,
    skipped: false,
    newSubmissionsCount: 0,
    purchasesWithoutBibsCount: 0,
    recipients: 0,
    sent: 0,
    failed: 0,
  };

  if (!options.force && completedWeeklyRunExists(app, jobKey)) {
    result.skipped = true;
    result.reason = "completed_run_exists";
    return result;
  }

  var run = createScheduledEmailRun(app, runJobKey, period);

  try {
    var summary = buildWeeklyStaffActionSummary(app, options);
    result.newSubmissionsCount = summary.newSubmissionsCount;
    result.purchasesWithoutBibsCount = summary.purchasesWithoutBibsCount;

    if (!hasWeeklyActionItems(summary)) {
      result.skipped = true;
      result.reason = "no_actionable_records";
      finishScheduledEmailRun(app, run, "skipped", 0, "");
      return result;
    }

    var recipients = optedInStaff(app);
    result.recipients = recipients.length;
    var subject = weeklySummarySubject(summary);
    var text = weeklySummaryText(summary);
    var html = weeklySummaryHtml(summary);

    for (var i = 0; i < recipients.length; i++) {
      var staff = recipients[i];
      var email = String(staff.get("weekly_action_summary_email") || "").trim();
      try {
        var sent = mail.send(app, email, subject, text, html, {
          templateKey: "weekly_staff_action_summary",
          recipientName: staff.get("displayName") || staff.get("username") || "Library Staff",
        });
        if (sent) {
          result.sent++;
          recordScheduledEmailDelivery(app, run, staff, email, "sent", "");
        } else {
          result.failed++;
          recordScheduledEmailDelivery(app, run, staff, email, "skipped", "Email notifications are not configured.");
        }
      } catch (err) {
        result.failed++;
        app.logger().error("Weekly staff action summary email failed", "staffUserId", staff.id, "email", email, "error", String(err));
        recordScheduledEmailDelivery(app, run, staff, email, "failed", err.message || String(err));
      }
    }

    finishScheduledEmailRun(app, run, result.failed ? "partial_failure" : "success", result.sent, "");
    return result;
  } catch (err) {
    finishScheduledEmailRun(app, run, "failed", result.sent, err.message || String(err));
    throw err;
  }
}

function mapIsbnCheckSuggestion(status) {
  if (status === "found") {
    return "dupe found in Polaris";
  }
  if (status === "not_found") {
    return "ISBN not found in system";
  }
  return "";
}

function processPendingIsbnChecks(app, staff, result) {
  var pending = app.findRecordsByFilter(
    "title_requests",
    "status = {:status} && isbnCheckStatus = {:isbnCheckStatus}",
    "created",
    200,
    0,
    { status: records.STATUS.SUGGESTION, isbnCheckStatus: "pending" }
  );

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    var identifier = String(record.get("identifier") || "").trim();
    var retryCount = parseInt(record.get("isbnCheckRetryCount") || 0, 10) || 0;
    var maxRetries = 5;

    if (!identifier) {
      record.set("isbnCheckStatus", "error");
      records.appendSystemNote(record, "ISBN check skipped: missing identifier.");
      app.save(record);
      continue;
    }

    records.appendSystemNote(record, "ISBN check attempt #" + (retryCount + 1) + " for identifier " + identifier + ".");
    var bibResult = polaris.searchBib(staff, identifier);

    if (bibResult.status === "found" || bibResult.status === "not_found") {
      record.set("isbnCheckStatus", bibResult.status);
      record.set("isbnCheckResult", mapIsbnCheckSuggestion(bibResult.status));
      record.set("isbnCheckRetryCount", 0);
      records.appendSystemNote(record, "ISBN check result: " + bibResult.status + (bibResult.bibId ? " (BIB " + bibResult.bibId + ")" : "") + ".");
      app.save(record);
      continue;
    }

    retryCount += 1;
    record.set("isbnCheckRetryCount", retryCount);
    record.set("isbnCheckStatus", retryCount >= maxRetries ? "error_max_retries" : "pending");
    records.appendSystemNote(record, "ISBN check transient error" + (bibResult.error ? ": " + bibResult.error : "") + ".");

    if (retryCount >= maxRetries) {
      records.appendSystemNote(record, "ISBN check reached max retries; admin follow-up required.");
    }
    app.save(record);
    if (result) result.errors++;
  }
}

function evaluatePurchase(app, staff, record, bibCache, result) {
  var identifier = String(record.get("identifier") || "").trim();
  var existingBibId = String(record.get("bibid") || "").trim();

  // Always update the check timestamp so staff knows the system is processing the record
  record.set("lastPromoterCheck", new Date().toISOString());
  app.save(record);

  // If it already has a BIBID (manually entered by staff), promote it immediately
  if (existingBibId) {
    record.set("status", records.STATUS.PENDING_HOLD);
    record.set("editedBy", "system");
    record.set("updated", new Date().toISOString());
    records.appendSystemNote(record, "Moved to Pending hold because a manual BIB ID was found.");
    records.setCanonicalRefs(app, record);
    app.save(record);
    records.recordEvent(app, record, "status_changed", "Moved to Pending hold because a manual BIB ID was found.", { toStatus: records.STATUS.PENDING_HOLD });
    result.promoted++;
    return;
  }

  // Only attempt auto-promotion search if an ISBN/Identifier is present
  if (!identifier) {
    return;
  }

  try {
    if (bibCache[identifier] === undefined) {
      bibCache[identifier] = polaris.searchBib(staff, identifier);
    }
    var bibResult = bibCache[identifier];
    var bibId = bibResult && bibResult.status === "found" ? bibResult.bibId : "";

    if (bibId) {
      records.addWorkflowTagForRequest(app, record, POLARIS_TAG_FOUND);
      record.set("bibid", bibId);
      polaris.reconcileRecord(app, staff, record, bibId);
      record.set("status", records.STATUS.PENDING_HOLD);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(record, "Automated promoter found BIB ID: " + bibId);
      records.setCanonicalRefs(app, record);
      app.save(record);
      records.recordEvent(app, record, "promoted", "Automated promoter found BIB ID: " + bibId, { toStatus: records.STATUS.PENDING_HOLD });
      result.promoted++;
    } else if (records.addWorkflowTagForRequest(app, record, POLARIS_TAG_NOT_FOUND)) {
      app.save(record);
    }
  } catch (err) {
    app.logger().error("Outstanding purchase promoter failed", "recordId", record.id, "error", String(err));
  }
}

function processOutstandingPurchases(app, staff, result) {
  const autoPromote = config.polaris().autoPromote !== false;

  if (!autoPromote) {
    app.logger().info("ASAP auto-promoter is disabled in settings. Skipping.");
    return;
  }

  var items = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-created",
    100,
    0,
    { status: records.STATUS.OUTSTANDING_PURCHASE }
  );

  var bibCache = {};

  for (var i = 0; i < items.length; i++) {
    evaluatePurchase(app, staff, items[i], bibCache, result);
  }
}

function processOutstandingTimeout(app, result) {
  var pending = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-created",
    2000,
    0,
    { status: records.STATUS.SUGGESTION }
  );

  var cfgCache = {};

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.outstandingTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];
    
    if (!cfg.enabled) continue;

    var cutoff = new Date();
    cutoff.setDate(cutoff.getDate() - cfg.days);
    var created = new Date(record.get("created"));

    if (created < cutoff) {
      var emailCfg = config.outstandingTimeoutEmail(app, orgId);
      record.set("status", records.STATUS.CLOSED);
      record.set("closeReason", records.CLOSE_REASON.REJECTED);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(
        record, 
        "Auto-rejected because it remained in Suggestions for more than " + cfg.days + " days." + (emailCfg.enabled ? " Rejection email queued." : " No rejection email sent.")
      );
      records.setCanonicalRefs(app, record);
      app.save(record);
      records.recordEvent(app, record, "timeout_closed", "Auto-rejected after " + cfg.days + " days in Suggestions.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.REJECTED });
      try {
        if (emailCfg.enabled) {
          if (!mail.autoRejected(app, record, emailCfg.templateId)) {
            mail.noteSkipped(app, record);
          }
        }
      } catch (mailErr) {
        app.logger().error("Auto-reject email failed", "recordId", record.id, "error", String(mailErr));
      }
      result.timedOut++;
    }
  }
}

function processPendingHoldTimeout(app, result) {
  var pending = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-updated",
    2000,
    0,
    { status: records.STATUS.PENDING_HOLD }
  );

  var cfgCache = {};

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.pendingHoldTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];

    if (!cfg.enabled) continue;

    var cutoff = new Date();
    cutoff.setDate(cutoff.getDate() - cfg.days);
    var updated = new Date(record.get("updated"));

    if (updated < cutoff) {
      try {
        record.set("status", records.STATUS.CLOSED);
        record.set("closeReason", records.CLOSE_REASON.REJECTED);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.appendSystemNote(record, "Auto-closed because it remained in Pending hold for more than " + cfg.days + " days.");
        records.setCanonicalRefs(app, record);
        app.save(record);
        records.recordEvent(app, record, "timeout_closed", "Auto-closed after " + cfg.days + " days in Pending hold.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.REJECTED });
        result.timedOut++;
      } catch (err) {
        result.errors++;
        app.logger().error("ASAP pending hold timeout failed", "recordId", record.id, "error", String(err));
      }
    }
  }
}

function processHoldPickupTimeout(app, result) {
  var holds = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-updated",
    2000,
    0,
    { status: records.STATUS.HOLD_PLACED }
  );

  var cfgCache = {};

  for (var i = 0; i < holds.length; i++) {
    var record = holds[i];
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.holdPickupTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];
    
    if (!cfg.enabled) continue;

    var cutoff = new Date();
    cutoff.setDate(cutoff.getDate() - cfg.days);
    var updated = new Date(record.get("updated"));

    if (updated < cutoff) {
      try {
        record.set("status", records.STATUS.CLOSED);
        record.set("closeReason", records.CLOSE_REASON.HOLD_NOT_PICKED_UP);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.appendSystemNote(record, "Auto-closed because the hold was not picked up within " + cfg.days + " days.");
        records.setCanonicalRefs(app, record);
        app.save(record);
        records.recordEvent(app, record, "timeout_closed", "Auto-closed because the hold was not picked up within " + cfg.days + " days.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.HOLD_NOT_PICKED_UP });
        result.holdPickupTimeouts++;
      } catch (err) {
        result.errors++;
        app.logger().error("ASAP hold pickup timeout failed", "recordId", record.id, "error", String(err));
      }
    }
  }
}


function processPendingSuggestionIsbnChecks(app, staff, result) {
  var pending = app.findRecordsByFilter(
    "title_requests",
    "status = {:status} && isbnCheckStatus = {:isbnCheckStatus}",
    "created",
    200,
    0,
    { status: records.STATUS.SUGGESTION, isbnCheckStatus: "pending" }
  );

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    var identifier = String(record.get("identifier") || "").trim();
    var now = new Date().toISOString();

    if (!identifier) {
      record.set("isbnCheckStatus", "skipped_no_isbn");
      record.set("lastChecked", now);
      record.set("updated", now);
      record.set("editedBy", "system");
      records.appendSystemNote(record, "ISBN verification skipped: no identifier provided.");
      app.save(record);
      result.skipped++;
      continue;
    }

    try {
      var bibResult = polaris.searchBib(staff, identifier);
      var bibId = bibResult && bibResult.status === "found" ? String(bibResult.bibId || "").trim() : "";
      var found = bibResult && bibResult.status === "found" && !!bibId;

      record.set("isbnCheckStatus", found ? "found" : "not_found");
      record.set("isbnCheckResult", mapIsbnCheckSuggestion(found ? "found" : "not_found"));
      record.set("lastChecked", now);
      record.set("updated", now);
      record.set("editedBy", "system");

      if (found) {
        record.set("bibid", bibId);
        records.addWorkflowTagForRequest(app, record, POLARIS_TAG_FOUND);
        records.appendSystemNote(record, "ISBN verification found a Polaris bibliographic match (BIB ID " + bibId + ").");
        result.isbnChecksFound++;
      } else {
        records.addWorkflowTagForRequest(app, record, POLARIS_TAG_NOT_FOUND);
        records.appendSystemNote(record, "ISBN verification completed: no Polaris bibliographic match found.");
        result.isbnChecksNotFound++;
      }
      app.save(record);
    } catch (err) {
      result.errors++;
      app.logger().error("Pending suggestion ISBN check failed", "recordId", record.id, "error", String(err));
    }
  }
}

function processPendingHolds(app, staff, result) {
  var pending = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-created",
    100,
    0,
    { status: records.STATUS.PENDING_HOLD }
  );

  var patronCache = {};
  var bibCache = {};

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    try {
      var bibId = String(record.get("bibid") || "").trim();
      if (!bibId) {
        var identifier = String(record.get("identifier") || "").trim();
        if (bibCache[identifier] === undefined) {
          bibCache[identifier] = polaris.searchBib(staff, identifier);
        }
        var bibResult = bibCache[identifier];
        bibId = bibResult && bibResult.status === "found" ? bibResult.bibId : "";
      }
      if (!bibId) {
        records.appendSystemNote(record, "SKIP: Could not find BIB ID in Polaris for hold placement.");
        app.save(record);
        result.skipped++;
        continue;
      }

      var barcode = record.get("barcode");
      if (patronCache[barcode] === undefined) {
        patronCache[barcode] = polaris.lookupPatron(staff, barcode);
      }
      var patron = patronCache[barcode];
      if (!patron.PatronID) {
        records.appendSystemNote(record, "SKIP: Patron not found in Polaris using barcode.");
        app.save(record);
        result.skipped++;
        continue;
      }

      var hold = polaris.placeHold(staff, bibId, patron.PatronID);
      // Status 29 or 6 means "Duplicate hold request" - i.e., the hold already exists in Polaris.
      var isDuplicate = String(hold.statusValue) === "29" || String(hold.statusValue) === "6";

      if (!hold.ok && !isDuplicate) {
        var errMsg = "";
        if (hold.payload) {
          errMsg = hold.payload.Message || hold.payload.ErrorMessage || "";
        }
        errMsg = errMsg || ("Polaris Error " + hold.statusValue);
        records.appendSystemNote(record, "SKIP: Hold placement failed. " + errMsg);
        app.save(record);
        app.logger().warn("ASAP hold placement skipped", "recordId", record.id, "statusValue", hold.statusValue, "payload", JSON.stringify(hold.payload));
        result.skipped++;
        continue;
      }

      var note = isDuplicate ? "HOLD ALREADY EXISTS IN POLARIS" : "HOLD PLACED FOR PATRON";
      
      record.set("bibid", bibId);
      polaris.reconcileRecord(app, staff, record, bibId);
      record.set("status", records.STATUS.HOLD_PLACED);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(record, note);
      records.setCanonicalRefs(app, record);
      app.save(record);
      records.recordEvent(app, record, "hold_placed", note, { toStatus: records.STATUS.HOLD_PLACED });
      try {
        if (!mail.holdPlaced(app, record, patron)) {
          mail.noteSkipped(app, record);
        }
      } catch (mailErr) {
        app.logger().error("Hold placement email failed", "recordId", record.id, "error", String(mailErr));
      }
      result.holdsPlaced++;
    } catch (err) {
      result.errors++;
      records.appendSystemNote(record, "ERROR: " + String(err));
      app.save(record);
      app.logger().error("ASAP hold placement failed", "recordId", record.id, "error", String(err));
    }
  }
}

function processCheckedOut(app, staff, result) {
  var holds = app.findRecordsByFilter(
    "title_requests",
    "status = {:status}",
    "-created",
    100,
    0,
    { status: records.STATUS.HOLD_PLACED }
  );

  var checkoutsCache = {};

  for (var i = 0; i < holds.length; i++) {
    var record = holds[i];
    try {
      var barcode = record.get("barcode");
      if (checkoutsCache[barcode] === undefined) {
        checkoutsCache[barcode] = polaris.checkPatronCheckouts(staff, barcode);
      }
      var checkouts = checkoutsCache[barcode];
      var bibId = String(record.get("bibid") || "");
      for (var j = 0; j < checkouts.length; j++) {
        if (String(checkouts[j].BibID) === bibId) {
          record.set("status", records.STATUS.CLOSED);
          record.set("closeReason", records.CLOSE_REASON.HOLD_COMPLETED);
          record.set("editedBy", "system");
          record.set("updated", new Date().toISOString());
          records.appendSystemNote(record, "ITEM CHECKED OUT BY PATRON");
          records.setCanonicalRefs(app, record);
          app.save(record);
          records.recordEvent(app, record, "fulfilled", "Item checked out by patron.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.HOLD_COMPLETED });
          result.checkoutClosures++;
          break;
        }
      }
    } catch (err) {
      result.errors++;
      app.logger().error("ASAP checkout check failed", "recordId", record.id, "error", String(err));
    }
  }
}

module.exports = {
  buildWeeklyStaffActionSummary: buildWeeklyStaffActionSummary,
  runScheduledHoldCheck: runScheduledHoldCheck,
  runScheduledOrganizationSync: runScheduledOrganizationSync,
  runWeeklyStaffActionSummary: runWeeklyStaffActionSummary,
  processOutstandingPurchases: processOutstandingPurchases,
  processPendingSuggestionIsbnChecks: processPendingSuggestionIsbnChecks,
};
