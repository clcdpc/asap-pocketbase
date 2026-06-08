const polaris = require("../polaris.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");

const processPagedQueue = jobQueue.processPagedQueue;

var HOLD_STATUS_CHECKS = [
  { statusFilter: "unclaimed", closeReason: records.CLOSE_REASON.HOLD_UNCLAIMED, note: "HOLD NOT CLAIMED BY PATRON" },
  { statusFilter: "cancelled", closeReason: records.CLOSE_REASON.HOLD_CANCELLED, note: "HOLD CANCELLED IN POLARIS" },
  { statusFilter: "expired", closeReason: records.CLOSE_REASON.HOLD_EXPIRED, note: "HOLD EXPIRED IN POLARIS" },
];

function closeRecordForHoldStatus(app, record, closeReason, note, resultField) {
  record.set("status", records.STATUS.CLOSED);
  record.set("closeReason", closeReason);
  record.set("editedBy", "system");
  record.set("updated", new Date().toISOString());
  records.appendSystemNote(record, note);
  records.setCanonicalRefs(app, record);
  app.save(record);
  records.recordEvent(app, record, "fulfilled", note, { toStatus: records.STATUS.CLOSED, closeReason: closeReason });
}

function processCheckedOut(app, staff, result) {
  var checkoutsCache = {};

  processPagedQueue(app, result, {
    queueName: "checked_out",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "created",
    params: { status: records.STATUS.HOLD_PLACED },
  }, function (record) {
    try {
      var barcode = record.get("barcode");
      var bibId = String(record.get("bibid") || "");

      if (!barcode || !bibId) return;

      if (checkoutsCache[barcode] === undefined) {
        checkoutsCache[barcode] = polaris.checkPatronCheckouts(staff, barcode);
      }
      var checkouts = checkoutsCache[barcode];
      for (var j = 0; j < checkouts.length; j++) {
        if (String(checkouts[j].BibID) === bibId) {
          closeRecordForHoldStatus(app, record, records.CLOSE_REASON.HOLD_COMPLETED, "ITEM CHECKED OUT BY PATRON", "checkoutClosures");
          result.checkoutClosures = (result.checkoutClosures || 0) + 1;
          return;
        }
      }
    } catch (err) {
      result.errors = (result.errors || 0) + 1;
      app.logger().error("ASAP checkout check failed", "recordId", record.id, "error", String(err));
    }

    // Check unclaimed, cancelled, and expired hold request statuses
    for (var k = 0; k < HOLD_STATUS_CHECKS.length; k++) {
      var check = HOLD_STATUS_CHECKS[k];
      try {
        var rows = polaris.getPatronHoldRequestsByStatus(staff, barcode, check.statusFilter);
        for (var r = 0; r < rows.length; r++) {
          if (String(rows[r].BibID) === bibId) {
            closeRecordForHoldStatus(app, record, check.closeReason, check.note, "holdStatusClosures");
            result.holdStatusClosures = (result.holdStatusClosures || 0) + 1;
            return;
          }
        }
      } catch (err) {
        result.errors = (result.errors || 0) + 1;
        app.logger().error("ASAP hold status check failed", "recordId", record.id, "statusFilter", check.statusFilter, "error", String(err));
      }
    }
  });
}

module.exports = {
  processCheckedOut: processCheckedOut,
};
