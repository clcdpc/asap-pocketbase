const config = require("../config.js");
const polaris = require("../polaris.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");
const helpers = require("./helpers.js");

const processPagedQueue = jobQueue.processPagedQueue;
const mapIsbnCheckSuggestion = helpers.mapIsbnCheckSuggestion;
const flagMultiplePolarisMatches = helpers.flagMultiplePolarisMatches;
const POLARIS_TAG_FOUND = helpers.POLARIS_TAG_FOUND;
const POLARIS_TAG_NOT_FOUND = helpers.POLARIS_TAG_NOT_FOUND;

function evaluatePurchase(app, staff, record, bibCache, result) {
  var identifier = String(record.get("identifier") || "").trim();
  var existingBibId = String(record.get("bibid") || "").trim();

  record.set("lastPromoterCheck", new Date().toISOString());
  app.save(record);

  if (existingBibId) {
    if (record.getBool("autohold") === false) {
      record.set("status", records.STATUS.CLOSED);
      record.set("closeReason", records.CLOSE_REASON.PURCHASED_NO_HOLD);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(record, "Closed without hold because a manual BIB ID was found and patron opted out of automatic hold placement.");
      records.setCanonicalRefs(app, record);
      app.save(record);
      records.recordEvent(app, record, "status_changed", "Closed without hold because a manual BIB ID was found and patron opted out of automatic hold placement.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.PURCHASED_NO_HOLD });
    } else {
      record.set("status", records.STATUS.PENDING_HOLD);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(record, "Moved to Pending hold because a manual BIB ID was found.");
      records.setCanonicalRefs(app, record);
      app.save(record);
      records.recordEvent(app, record, "status_changed", "Moved to Pending hold because a manual BIB ID was found.", { toStatus: records.STATUS.PENDING_HOLD });
    }
    result.promoted++;
    return;
  }

  if (!identifier) {
    return;
  }

  try {
    if (bibCache[identifier] === undefined) {
      bibCache[identifier] = polaris.searchBib(app, staff, identifier);
    }
    var bibResult = bibCache[identifier];
    var bibId = bibResult && bibResult.status === "found" ? bibResult.bibId : "";

    if (bibId) {
      records.addWorkflowTagForRequest(app, record, POLARIS_TAG_FOUND);
      flagMultiplePolarisMatches(app, record, bibResult);
      record.set("bibid", bibId);
      polaris.reconcileRecord(app, staff, record, bibId);
      
      if (record.getBool("autohold") === false) {
        record.set("status", records.STATUS.CLOSED);
        record.set("closeReason", records.CLOSE_REASON.PURCHASED_NO_HOLD);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.appendSystemNote(record, "Auto-promoter found BIB ID: " + bibId + ". Closed without hold because patron opted out of automatic hold placement.");
        records.setCanonicalRefs(app, record);
        app.save(record);
        records.recordEvent(app, record, "promoted", "Auto-promoter found BIB ID: " + bibId + ". Closed without hold because patron opted out of automatic hold placement.", { toStatus: records.STATUS.CLOSED, closeReason: records.CLOSE_REASON.PURCHASED_NO_HOLD });
      } else {
        record.set("status", records.STATUS.PENDING_HOLD);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.appendSystemNote(record, "Automated promoter found BIB ID: " + bibId);
        records.setCanonicalRefs(app, record);
        app.save(record);
        records.recordEvent(app, record, "promoted", "Automated promoter found BIB ID: " + bibId, { toStatus: records.STATUS.PENDING_HOLD });
      }
      result.promoted++;
    } else if (records.addWorkflowTagForRequest(app, record, POLARIS_TAG_NOT_FOUND)) {
      app.save(record);
    }
  } catch (err) {
    app.logger().error("Outstanding purchase promoter failed", "recordId", record.id, "error", String(err));
  }
}

function promoteRequestNow(app, staff, record) {
  var target = record;
  if (!target || !target.get) {
    return { promoted: false, status: "skipped", reason: "invalid_record" };
  }

  var status = records.normalizeStatus(target.get("status"));
  if (status === records.STATUS.OUTSTANDING_PURCHASE) {
    var purchaseResult = { promoted: 0 };
    evaluatePurchase(app, staff, target, {}, purchaseResult);
    return { promoted: purchaseResult.promoted > 0, status: purchaseResult.promoted > 0 ? "promoted" : "checked" };
  }

  if (status === records.STATUS.SUGGESTION) {
    var now = new Date().toISOString();
    var identifier = String(target.get("identifier") || "").trim();
    if (!identifier) {
      target.set("isbnCheckStatus", "skipped_no_isbn");
      target.set("lastChecked", now);
      target.set("updated", now);
      target.set("editedBy", "system");
      records.appendSystemNote(target, "Identifier number verification skipped: no identifier provided.");
      app.save(target);
      return { promoted: false, status: "skipped", reason: "missing_identifier" };
    }

    var bibResult = polaris.searchBib(app, staff, identifier);
    var bibId = bibResult && bibResult.status === "found" ? String(bibResult.bibId || "").trim() : "";
    var found = bibResult && bibResult.status === "found" && !!bibId;

    target.set("isbnCheckStatus", found ? "found" : "not_found");
    target.set("isbnCheckResult", mapIsbnCheckSuggestion(found ? "found" : "not_found"));
    target.set("lastChecked", now);
    target.set("updated", now);
    target.set("editedBy", "system");

    if (found) {
      target.set("bibid", bibId);
      polaris.reconcileRecord(app, staff, target, bibId);
      records.addWorkflowTagForRequest(app, target, POLARIS_TAG_FOUND);
      flagMultiplePolarisMatches(app, target, bibResult);
      records.appendSystemNote(target, "Identifier number verification found a Polaris bibliographic match (BIB ID " + bibId + ").");
      app.save(target);
      return { promoted: true, status: "found", bibId: bibId };
    }

    records.addWorkflowTagForRequest(app, target, POLARIS_TAG_NOT_FOUND);
    records.appendSystemNote(target, "Identifier number verification completed: no Polaris bibliographic match found.");
    app.save(target);
    return { promoted: false, status: "not_found" };
  }

  return { promoted: false, status: "skipped", reason: "status_not_supported" };
}

function processOutstandingPurchases(app, staff, result) {
  const autoPromote = config.suggestionLimit(app, "").autoPromote !== false;

  if (!autoPromote) {
    app.logger().info("ASAP auto-promoter is disabled in settings. Skipping.");
    return;
  }

  var bibCache = {};

  processPagedQueue(app, result, {
    queueName: "outstanding_purchases",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "created",
    params: { status: records.STATUS.OUTSTANDING_PURCHASE },
  }, function (record) {
    evaluatePurchase(app, staff, record, bibCache, result);
  });
}

module.exports = {
  evaluatePurchase: evaluatePurchase,
  promoteRequestNow: promoteRequestNow,
  processOutstandingPurchases: processOutstandingPurchases,
};
