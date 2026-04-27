const config = require(`${__hooks}/lib/config.js`);
const mail = require(`${__hooks}/lib/mail.js`);
const orgs = require(`${__hooks}/lib/orgs.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);
const records = require(`${__hooks}/lib/records.js`);

function runScheduledHoldCheck(app) {
  var result = {
    holdsPlaced: 0,
    checkoutClosures: 0,
    holdPickupTimeouts: 0,
    promoted: 0,
    timedOut: 0,
    skipped: 0,
    errors: 0,
  };

  var staff = polaris.adminStaffAuth();
  processOutstandingTimeout(app, result);
  processHoldPickupTimeout(app, result);
  processOutstandingPurchases(app, staff, result);
  processPendingHolds(app, staff, result);
  processCheckedOut(app, staff, result);
  app.logger().info("ASAP hold check completed", "result", JSON.stringify(result));
  return result;
}

function runScheduledOrganizationSync(app) {
  try {
    var result = orgs.syncOrganizations(app, polaris.adminStaffAuth());
    result.success = true;
    app.logger().info("ASAP Polaris organization sync completed", "result", JSON.stringify(result));
    return result;
  } catch (err) {
    var failed = {
      success: false,
      synced: 0,
      error: err.message || String(err),
    };
    app.logger().error("ASAP Polaris organization sync failed", "error", String(err));
    return failed;
  }
}

function processOutstandingPurchases(app, staff, result) {
  const settings = app.findFirstRecordByFilter("app_settings", "id = 'settings0000001'");
  const autoPromote = settings.get("polaris").autoPromote !== false;

  if (!autoPromote) {
    app.logger().info("ASAP Auto-Promoter is disabled in settings. Skipping.");
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

  for (var i = 0; i < items.length; i++) {
    var record = items[i];
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
      records.appendSystemNote(record, "Promoted to Pending Hold (Manual BIB ID found)");
      app.save(record);
      result.promoted++;
      continue;
    }

    // Only attempt auto-promotion search if an ISBN/Identifier is present
    if (!identifier) {
      continue;
    }

    try {
      var bibId = polaris.searchBib(staff, identifier);
      if (bibId) {
        record.set("bibid", bibId);
        polaris.reconcileRecord(app, staff, record, bibId);
        record.set("status", records.STATUS.PENDING_HOLD);
        record.set("editedBy", "system");
        record.set("updated", new Date().toISOString());
        records.appendSystemNote(record, "Automated promoter found BIB ID: " + bibId);
        app.save(record);
        result.promoted++;
      }
    } catch (err) {
      app.logger().error("Outstanding purchase promoter failed", "recordId", record.id, "error", String(err));
    }
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
      record.set("status", records.STATUS.CLOSED);
      record.set("closeReason", records.CLOSE_REASON.REJECTED);
      record.set("editedBy", "system");
      record.set("updated", new Date().toISOString());
      records.appendSystemNote(record, "Auto-rejected due to " + cfg.days + " day timeout in Suggestions.");
      app.save(record);
      try {
        mail.autoRejected(app, record);
      } catch (mailErr) {
        app.logger().error("Auto-reject email failed", "recordId", record.id, "error", String(mailErr));
      }
      result.timedOut++;
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
        records.appendSystemNote(record, "Hold was not picked up by patron within " + cfg.days + " days. Auto-closed.");
        app.save(record);
        result.holdPickupTimeouts++;
      } catch (err) {
        result.errors++;
        app.logger().error("ASAP hold pickup timeout failed", "recordId", record.id, "error", String(err));
      }
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

  for (var i = 0; i < pending.length; i++) {
    var record = pending[i];
    try {
      var bibId = String(record.get("bibid") || "").trim();
      if (!bibId) {
        bibId = polaris.searchBib(staff, record.get("identifier"));
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
      app.save(record);
      try {
        mail.holdPlaced(app, record, patron);
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
          app.save(record);
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
  runScheduledHoldCheck: runScheduledHoldCheck,
  runScheduledOrganizationSync: runScheduledOrganizationSync,
  processOutstandingPurchases: processOutstandingPurchases,
};
