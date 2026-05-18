const polaris = require("../polaris.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");

const processPagedQueue = jobQueue.processPagedQueue;

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
  });
}

module.exports = {
  processCheckedOut: processCheckedOut,
};
