const config = require("../config.js");
const additionalCopies = require("../additional_copies.js");
const mail = require("../mail.js");
const records = require("../records.js");

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

function additionalCopySummaryItem(record) {
  var item = additionalCopies.toJson(record);
  return {
    title: cleanSummaryValue(item.title) || "Untitled",
    author: cleanSummaryValue(item.author || item.bibid),
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

function listOpenAdditionalCopyRequests(app) {
  var items = [];
  var limit = 500;
  var offset = 0;
  while (true) {
    var page;
    try {
      page = app.findRecordsByFilter(
        "additional_copy_requests",
        "status = 'open'",
        "-created",
        limit,
        offset
      );
    } catch (err) {
      return [];
    }
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
  var additionalCopyTasks = listOpenAdditionalCopyRequests(app);

  return {
    newSubmissionsCount: newSubmissions.length,
    newSubmissionSample: newSubmissions.slice(0, 5).map(summaryItem),
    purchasesWithoutBibsCount: purchasesWithoutBibs.length,
    purchasesWithoutBibsSample: purchasesWithoutBibs.slice(0, 5).map(summaryItem),
    additionalCopiesCount: additionalCopyTasks.length,
    additionalCopiesSample: additionalCopyTasks.slice(0, 5).map(additionalCopySummaryItem),
    newSubmissionsUrl: staffViewUrl(app, "submitted"),
    purchasesWithoutBibsUrl: staffViewUrl(app, "purchased_waiting_for_bib"),
    additionalCopiesUrl: staffViewUrl(app, "additional_copies"),
  };
}

function hasWeeklyActionItems(summary) {
  return !!summary && (summary.newSubmissionsCount > 0 || summary.purchasesWithoutBibsCount > 0 || summary.additionalCopiesCount > 0);
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
  return "Weekly ASAP action summary: " + summary.newSubmissionsCount + " new, " + summary.purchasesWithoutBibsCount + " awaiting bibs, " + summary.additionalCopiesCount + " additional copies";
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
    "",
    "Additional copies",
    summary.additionalCopiesCount + " open tasks",
    "Five most recent:",
  ]).concat(formatSummaryLines(summary.additionalCopiesSample)).concat([
    "",
    "View additional copies:",
    summary.additionalCopiesUrl,
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
    "<h2>Additional copies</h2>",
    "<p>" + summary.additionalCopiesCount + " open tasks</p>",
    "<p><strong>Five most recent:</strong></p>",
    weeklySummaryHtmlList(summary.additionalCopiesSample),
    '<p><a href="' + escapeHtml(summary.additionalCopiesUrl) + '">View additional copies</a></p>',
  ].join("\n");
}

function completedWeeklyRunExists(app, jobKey) {
  try {
    var existing = app.findRecordsByFilter(
      "scheduled_email_runs",
      "job_key = {:jobKey} && status = 'success'",
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
    additionalCopiesCount: 0,
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
    result.additionalCopiesCount = summary.additionalCopiesCount;

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

module.exports = {
  weeklySummaryPeriod: weeklySummaryPeriod,
  buildWeeklyStaffActionSummary: buildWeeklyStaffActionSummary,
  weeklySummarySubject: weeklySummarySubject,
  weeklySummaryText: weeklySummaryText,
  weeklySummaryHtml: weeklySummaryHtml,
  runWeeklyStaffActionSummary: runWeeklyStaffActionSummary,
};
