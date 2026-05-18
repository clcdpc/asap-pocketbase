const config = require("../config.js");
const additionalCopies = require("../additional_copies.js");
const mail = require("../mail.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");

const processPagedQueue = jobQueue.processPagedQueue;

function processOutstandingTimeout(app, result) {
  var cfgCache = {};

  processPagedQueue(app, result, {
    queueName: "outstanding_timeout",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "created",
    params: { status: records.STATUS.SUGGESTION },
  }, function (record) {
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.outstandingTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];
    
    if (!cfg.enabled) return;

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
  });
}

function processPendingHoldTimeout(app, result) {
  var cfgCache = {};

  processPagedQueue(app, result, {
    queueName: "pending_hold_timeout",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "updated",
    params: { status: records.STATUS.PENDING_HOLD },
  }, function (record) {
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.pendingHoldTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];

    if (!cfg.enabled) return;

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
  });
}

function processHoldPickupTimeout(app, result) {
  var cfgCache = {};

  processPagedQueue(app, result, {
    queueName: "hold_pickup_timeout",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "updated",
    params: { status: records.STATUS.HOLD_PLACED },
  }, function (record) {
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.holdPickupTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];
    
    if (!cfg.enabled) return;

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
  });
}

function processAdditionalCopyTimeout(app, result) {
  var cfgCache = {};

  processPagedQueue(app, result, {
    queueName: "additional_copy_timeout",
    collection: "additional_copy_requests",
    filter: "status = 'open'",
    sortField: "updated",
  }, function (record) {
    var orgId = record.get("libraryOrgId");

    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.additionalCopyTimeout(app, orgId);
    }
    var cfg = cfgCache[orgId];

    if (!cfg.enabled) return;

    var cutoff = new Date();
    cutoff.setDate(cutoff.getDate() - cfg.days);
    var updated = new Date(record.get("updated") || record.get("created"));

    if (updated < cutoff) {
      try {
        additionalCopies.closeTask(app, record);
        records.appendSystemNote(record, "Auto-closed because it remained open for more than " + cfg.days + " days.");
        app.save(record);
        result.timedOut++;
      } catch (err) {
        result.errors++;
        app.logger().error("ASAP additional copy timeout failed", "recordId", record.id, "error", String(err));
      }
    }
  });
}

module.exports = {
  processOutstandingTimeout: processOutstandingTimeout,
  processPendingHoldTimeout: processPendingHoldTimeout,
  processHoldPickupTimeout: processHoldPickupTimeout,
  processAdditionalCopyTimeout: processAdditionalCopyTimeout,
};
