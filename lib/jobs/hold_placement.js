const polaris = require("../polaris.js");
const records = require("../records.js");
const jobQueue = require("../job_queue.js");
const mail = require("../mail.js");
const helpers = require("./helpers.js");

const processPagedQueue = jobQueue.processPagedQueue;
const flagMultiplePolarisMatches = helpers.flagMultiplePolarisMatches;

function classifyPolarisHoldResult(hold) {
  var statusValue = String(hold && hold.statusValue || "");
  var message = String(hold && hold.payload && (hold.payload.Message || hold.payload.ErrorMessage) || "");
  var map = {
    "29": {
      ok: true,
      tag: "Hold exists (same patron)",
      note: "Polaris reported an existing duplicate hold request for this patron."
    },
    "6": {
      ok: false,
      tag: "No holdable items",
      note: "Polaris reported no items linked to this BIB; hold was not placed."
    },
    "-4001": { ok: false, tag: "Hold failed: patron", note: "Invalid patron ID supplied." },
    "-4002": { ok: false, tag: "Hold failed: workstation", note: "Invalid workstation ID supplied." },
    "-4004": { ok: false, tag: "Hold failed: org", note: "Invalid requesting org ID supplied." },
    "-4006": { ok: false, tag: "Hold failed: bib", note: "Invalid bibliographic record ID supplied." },
    "-4007": { ok: false, tag: "Hold failed: pickup", note: "Invalid requesting pickup branch supplied." },
    "-4020": { ok: false, tag: "Hold failed: pickup", note: "Hold pickup area invalid for pickup branch." },
    "-4021": { ok: false, tag: "Hold failed: pickup", note: "Hold pickup area ID invalid." },
    "-4022": { ok: false, tag: "Hold failed: pickup", note: "Hold pickup area not enabled for pickup branch." }
  };
  if (map[statusValue]) {
    return Object.assign({ statusValue: statusValue, message: message }, map[statusValue]);
  }
  if (hold && hold.ok) {
    return {
      ok: true,
      statusValue: statusValue,
      tag: "Hold placed",
      note: "Polaris hold placement succeeded.",
      message: message
    };
  }
  return {
    ok: false,
    statusValue: statusValue,
    tag: "Hold failed",
    note: message || ("Polaris hold placement failed with status " + statusValue + "."),
    message: message
  };
}

function noteNoHoldableItems(app, record, bibId, detail) {
  var added = records.addWorkflowTagForRequest(app, record, "No holdable items");
  if (added) {
    records.appendSystemNote(record, detail || ("Hold placement skipped: Polaris BIB " + bibId + " has no holdable items attached yet. ASAP will retry after item records are available."));
  }
}

function processPendingHolds(app, staff, result) {
  var patronCache = {};
  var bibCache = {};

  processPagedQueue(app, result, {
    queueName: "pending_holds",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "created",
    params: { status: records.STATUS.PENDING_HOLD },
  }, function (record) {
    try {
      var bibId = String(record.get("bibid") || "").trim();
      if (!bibId) {
        var identifier = String(record.get("identifier") || "").trim();
        if (bibCache[identifier] === undefined) {
          bibCache[identifier] = polaris.searchBib(app, staff, identifier);
        }
        var bibResult = bibCache[identifier];
        bibId = bibResult && bibResult.status === "found" ? bibResult.bibId : "";
        if (bibId) {
          flagMultiplePolarisMatches(app, record, bibResult);
        }
      }
      if (!bibId) {
        records.appendSystemNote(record, "SKIP: Could not find BIB ID in Polaris for hold placement.");
        app.save(record);
        result.skipped++;
        return;
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
        return;
      }

      if (!record.getBool("autohold")) {
        records.appendSystemNote(record, "SKIP: Patron opted out of automatic hold placement.");
        record.set("status", records.STATUS.HOLD_PLACED);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.addWorkflowTagForRequest(app, record, "No hold requested");
        records.setCanonicalRefs(app, record);
        app.save(record);
        records.recordEvent(app, record, "hold_skipped", "Automatic hold placement skipped by patron request.", { toStatus: records.STATUS.HOLD_PLACED });
        result.skipped++;
        return;
      }

      if (polaris.patronHasHoldForBib(staff, barcode, bibId)) {
        records.addWorkflowTagForRequest(app, record, "Hold exists (same patron)");
        records.appendSystemNote(record, "SKIP: Polaris already has an active hold for this patron and BIB ID.");
        app.save(record);
        result.skipped++;
        return;
      }

      var holdability = polaris.summarizeHoldability(polaris.getBibHoldings(staff, bibId));
      if (!holdability.hasHoldableItems) {
        noteNoHoldableItems(app, record, bibId);
        app.save(record);
        result.skipped++;
        return;
      }

      var hold = polaris.placeHold(staff, bibId, patron.PatronID, { noAutoReply: true });
      if (String(hold.statusValue || "") === "5" && hold.payload && hold.payload.RequestGUID) {
        polaris.replyToHold(staff, hold.payload, "3");
        hold.ok = true;
      }
      var holdClassification = classifyPolarisHoldResult(hold);

      if (!holdClassification.ok) {
        if (holdClassification.tag === "No holdable items") {
          noteNoHoldableItems(app, record, bibId, "Hold placement skipped: Polaris BIB " + bibId + " has no holdable items attached yet. ASAP will retry after item records are available.");
        } else {
          records.addWorkflowTagForRequest(app, record, holdClassification.tag);
          records.appendSystemNote(record, "SKIP: " + holdClassification.note);
        }
        app.save(record);
        app.logger().warn("ASAP hold placement skipped", "recordId", record.id, "statusValue", holdClassification.statusValue, "payload", JSON.stringify(hold && hold.payload));
        result.skipped++;
        return;
      }

      var note = holdClassification.note;
      
      record.set("bibid", bibId);
      polaris.reconcileRecord(app, staff, record, bibId);
      record.set("status", records.STATUS.HOLD_PLACED);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.addWorkflowTagForRequest(app, record, holdClassification.tag);
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
  });
}

module.exports = {
  classifyPolarisHoldResult: classifyPolarisHoldResult,
  noteNoHoldableItems: noteNoHoldableItems,
  processPendingHolds: processPendingHolds,
};
