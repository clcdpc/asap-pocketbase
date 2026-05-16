
const records = require(`${__hooks}/../lib/records.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);

function prepareTitleRequestBibAction(e, context) {
  if (!context.data.bibid) {
    return null;
  }

  var bibid = String(context.data.bibid).trim();
  var barcode = context.record.get("barcode");
  var staffAuth = staffActionPolarisAuth(e.app);
  var duplicateResponse = handleDuplicateBibRequest(e, context, bibid);
  if (duplicateResponse) {
    return duplicateResponse;
  }

  reconcileBibAction(e.app, context, staffAuth, bibid);
  handleHoldTransitionForBibAction(e.app, context, staffAuth, bibid, barcode);
  return null;
}

function staffActionPolarisAuth(app) {
  try {
    return polaris.adminStaffAuth();
  } catch (err) {
    app.logger().warn("Polaris auth failed", "error", String(err));
  }
}

function handleDuplicateBibRequest(e, context, bibid) {
  var existing = e.app.findRecordsByFilter("title_requests",
    "barcode = {:barcode} && bibid = {:bibid} && id != {:id} && status != 'closed'",
    "", 1, 0, { barcode: context.record.get("barcode"), bibid: bibid, id: context.id });
  if (!existing || !existing.length) {
    return null;
  }

  records.addWorkflowTagForRequest(e.app, context.record, "Hold exists (same patron)");
  if (context.isDuplicateClose) {
    markDuplicateClose(context);
    return null;
  }

  if (wouldCreateActiveDuplicate(context, bibid)) {
    e.app.save(context.record);
    return e.json(409, {
      code: "duplicate_open_request",
      message: "This patron already has an open request for this BIB ID. This request was flagged; close it as a duplicate if it should not continue.",
      duplicate: records.duplicateContext(existing[0], "bibid")
    });
  }

  return null;
}

function markDuplicateClose(context) {
  context.data.status = records.STATUS.CLOSED;
  context.nextStatus = records.STATUS.CLOSED;
  context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
  records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
  context.duplicateCloseNoteAdded = true;
}

function wouldCreateActiveDuplicate(context, bibid) {
  var oldIsActiveHold = context.oldStatus === records.STATUS.PENDING_HOLD || context.oldStatus === records.STATUS.HOLD_PLACED;
  var bibidChanged = String(context.record.get("bibid") || "").trim() !== bibid;
  return context.isActiveHoldTarget && (!oldIsActiveHold || bibidChanged || context.action === "alreadyOwn");
}

function reconcileBibAction(app, context, staffAuth, bibid) {
  if (context.isDuplicateClose || context.isClosingRequest) {
    return;
  }

  var beforeTitle = String(context.record.get("title") || "");
  var beforeAuthor = String(context.record.get("author") || "");
  polaris.reconcileRecord(app, staffAuth, context.record, bibid, {
    bibId: context.data.selectedPolarisBibId,
    title: context.data.selectedPolarisTitle,
    author: context.data.selectedPolarisAuthor,
    identifier: context.data.selectedPolarisIdentifier,
    publication: context.data.selectedPolarisPublication,
    format: context.data.selectedPolarisFormat
  });
  var reconciledTitle = String(context.record.get("title") || "");
  var reconciledAuthor = String(context.record.get("author") || "");
  if (reconciledTitle !== beforeTitle) {
    context.data.title = reconciledTitle;
  }
  if (reconciledAuthor !== beforeAuthor) {
    context.data.author = reconciledAuthor;
  }
}

function handleHoldTransitionForBibAction(app, context, staffAuth, bibid, barcode) {
  if (context.nextStatus !== records.STATUS.PENDING_HOLD && !(context.oldStatus === records.STATUS.OUTSTANDING_PURCHASE && context.data.bibid)) {
    return;
  }

  if (context.record.getBool("autohold") === false) {
    closeAutoholdOptOutBibAction(context);
    return;
  }

  if (context.nextStatus === records.STATUS.PENDING_HOLD) {
    maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode);
  }
}

function closeAutoholdOptOutBibAction(context) {
  if (context.action === "additionalCopy") {
    return;
  }
  context.nextStatus = records.STATUS.CLOSED;
  context.data.status = records.STATUS.CLOSED;
  context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  var optOutReason = (context.action === "alreadyOwn")
    ? "Closed without hold because 'Already Own' was selected and patron opted out of automatic hold placement."
    : "Closed without hold because BIB ID was entered and patron opted out of automatic hold placement.";
  records.appendSystemNote(context.record, optOutReason);
}

function maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode) {
  try {
    var pPatron = polaris.lookupPatron(staffAuth, barcode);
    if (pPatron && pPatron.PatronID) {
      if (polaris.patronHasHoldForBib(staffAuth, barcode, bibid)) {
        context.nextStatus = records.STATUS.HOLD_PLACED;
        context.data.status = context.nextStatus;
        records.appendSystemNote(context.record, "Patron already has a hold in Polaris for this BIB ID. Moving directly to Hold placed.");
      }
    }
  } catch (polarisErr) {
    app.logger().warn("Polaris duplicate hold check failed during staff action", "error", String(polarisErr));
  }
}

function finalizeTitleRequestCloseReason(app, context) {
  if (context.nextStatus === records.STATUS.CLOSED && (context.action === "reject" || context.action === "silentClose")) {
    context.data.closeReason = (context.action === "silentClose") ? records.CLOSE_REASON.SILENT : records.CLOSE_REASON.REJECTED;
  }
  if (context.nextStatus === records.STATUS.CLOSED && context.isDuplicateClose) {
    context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
    records.addWorkflowTagForRequest(app, context.record, "Hold exists (same patron)");
    if (!context.duplicateCloseNoteAdded) {
      records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
    }
  }
  if (context.nextStatus === records.STATUS.CLOSED && !context.data.closeReason && context.record.getBool("autohold") === false && context.data.bibid) {
    context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  }
  if (context.nextStatus !== records.STATUS.CLOSED) {
    context.data.closeReason = "";
  }
}

module.exports = {
  prepareTitleRequestBibAction,
  staffActionPolarisAuth,
  handleDuplicateBibRequest,
  markDuplicateClose,
  wouldCreateActiveDuplicate,
  reconcileBibAction,
  handleHoldTransitionForBibAction,
  closeAutoholdOptOutBibAction,
  maybePromoteExistingPolarisHold,
  finalizeTitleRequestCloseReason
};

