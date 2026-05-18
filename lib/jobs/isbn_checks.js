const polaris = require("../polaris.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");
const helpers = require("./helpers.js");

const processPagedQueue = jobQueue.processPagedQueue;
const mapIsbnCheckSuggestion = helpers.mapIsbnCheckSuggestion;
const flagMultiplePolarisMatches = helpers.flagMultiplePolarisMatches;
const POLARIS_TAG_FOUND = helpers.POLARIS_TAG_FOUND;
const POLARIS_TAG_NOT_FOUND = helpers.POLARIS_TAG_NOT_FOUND;

function processPendingIsbnChecks(app, staff, result) {
  processPagedQueue(app, result, {
    queueName: "pending_isbn_checks",
    collection: "title_requests",
    filter: "status = {:status} && isbnCheckStatus = {:isbnCheckStatus}",
    sortField: "created",
    params: { status: records.STATUS.SUGGESTION, isbnCheckStatus: "pending" },
  }, function (record) {
    var identifier = String(record.get("identifier") || "").trim();
    var retryCount = parseInt(record.get("isbnCheckRetryCount") || 0, 10) || 0;
    var maxRetries = 5;

    if (!identifier) {
      record.set("isbnCheckStatus", "error");
      records.appendSystemNote(record, "Identifier number check skipped: missing identifier.");
      app.save(record);
      return;
    }

    records.appendSystemNote(record, "Identifier number check attempt #" + (retryCount + 1) + " for identifier " + identifier + ".");
    var bibResult = polaris.searchBib(app, staff, identifier);

    if (bibResult.status === "found" || bibResult.status === "not_found") {
      record.set("isbnCheckStatus", bibResult.status);
      record.set("isbnCheckResult", mapIsbnCheckSuggestion(bibResult.status));
      record.set("isbnCheckRetryCount", 0);
      if (bibResult.status === "found") {
        flagMultiplePolarisMatches(app, record, bibResult);
      }
      records.appendSystemNote(record, "Identifier number check result: " + bibResult.status + (bibResult.bibId ? " (BIB " + bibResult.bibId + ")" : "") + ".");
      app.save(record);
      return;
    }

    retryCount += 1;
    record.set("isbnCheckRetryCount", retryCount);
    record.set("isbnCheckStatus", retryCount >= maxRetries ? "error_max_retries" : "pending");
    records.appendSystemNote(record, "Identifier number check transient error" + (bibResult.error ? ": " + bibResult.error : "") + ".");

    if (retryCount >= maxRetries) {
      records.appendSystemNote(record, "Identifier number check reached max retries; admin follow-up required.");
    }
    app.save(record);
    if (result) result.errors++;
  });
}

function processPendingSuggestionIsbnChecks(app, staff, result) {
  processPagedQueue(app, result, {
    queueName: "pending_suggestion_isbn_checks",
    collection: "title_requests",
    filter: "status = {:status} && isbnCheckStatus = {:isbnCheckStatus}",
    sortField: "created",
    params: { status: records.STATUS.SUGGESTION, isbnCheckStatus: "pending" },
  }, function (record) {
    var identifier = String(record.get("identifier") || "").trim();
    var now = new Date().toISOString();

    if (!identifier) {
      record.set("isbnCheckStatus", "skipped_no_isbn");
      record.set("lastChecked", now);
      record.set("updated", now);
      record.set("editedBy", "system");
      records.appendSystemNote(record, "Identifier number verification skipped: no identifier provided.");
      app.save(record);
      result.skipped++;
      return;
    }

    try {
      var bibResult = polaris.searchBib(app, staff, identifier);
      var bibId = bibResult && bibResult.status === "found" ? String(bibResult.bibId || "").trim() : "";
      var found = bibResult && bibResult.status === "found" && !!bibId;

      record.set("isbnCheckStatus", found ? "found" : "not_found");
      record.set("isbnCheckResult", mapIsbnCheckSuggestion(found ? "found" : "not_found"));
      record.set("lastChecked", now);
      record.set("updated", now);
      record.set("editedBy", "system");

      if (found) {
        record.set("bibid", bibId);
        polaris.reconcileRecord(app, staff, record, bibId);
        records.addWorkflowTagForRequest(app, record, POLARIS_TAG_FOUND);
        flagMultiplePolarisMatches(app, record, bibResult);
        records.appendSystemNote(record, "Identifier number verification found a Polaris bibliographic match (BIB ID " + bibId + ").");
        result.isbnChecksFound++;
      } else {
        records.addWorkflowTagForRequest(app, record, POLARIS_TAG_NOT_FOUND);
        records.appendSystemNote(record, "Identifier number verification completed: no Polaris bibliographic match found.");
        result.isbnChecksNotFound++;
      }
      app.save(record);
    } catch (err) {
      result.errors++;
      app.logger().error("Pending suggestion identifier number check failed", "recordId", record.id, "error", String(err));
    }
  });
}

module.exports = {
  processPendingIsbnChecks: processPendingIsbnChecks,
  processPendingSuggestionIsbnChecks: processPendingSuggestionIsbnChecks,
};
