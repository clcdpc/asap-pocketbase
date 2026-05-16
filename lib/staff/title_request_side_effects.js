
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);

const polaris = require(`${__hooks}/../lib/polaris.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const jobs = require(`${__hooks}/../lib/jobs.js`);
const bibActions = require(`${__hooks}/../lib/staff/title_request_bib_actions.js`);
const staffActionPolarisAuth = bibActions.staffActionPolarisAuth;


function maybeRunImmediatePromoter(app, context) {
  if (!context.shouldRunImmediatePromoter) {
    return;
  }

  try {
    var updatedStatus = records.normalizeStatus(context.record.get("status"));
    if (updatedStatus === records.STATUS.SUGGESTION || config.suggestionLimit(app, "").autoPromote !== false) {
      jobs.promoteRequestNow(app, polaris.adminStaffAuth(), context.record);
      context.record = app.findRecordById("title_requests", context.record.id);
    }
  } catch (promoteErr) {
    app.logger().error("Immediate identifier promotion failed", "recordId", context.record.id, "error", String(promoteErr));
  }
}

function handleAlreadyOwnOrRejectSideEffects(app, context) {
  if (context.action !== "alreadyOwn" && context.action !== "reject" && !(context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD)) {
    return;
  }

  var patron = refreshedActionPatron(app, context.record);
  if (context.action === "alreadyOwn") {
    handleAlreadyOwnSideEffects(app, context, patron);
  } else if (context.action === "reject") {
    sendRejectedActionEmail(app, context, patron);
  } else if (context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD) {
    handleAlreadyOwnSideEffects(app, context, patron);
  }
}

function refreshedActionPatron(app, record) {
  try {
    return polaris.lookupPatron(polaris.adminStaffAuth(), record.get("barcode"));
  } catch (err) {
    app.logger().warn("Could not refresh patron data for staff action email", "recordId", record.id, "error", String(err));
  }
  return null;
}

function handleAlreadyOwnSideEffects(app, context, patron) {
  var bibid = String(context.data.bibid || "").trim();
  if (bibid && patron && patron.PatronID) {
    placeAlreadyOwnedHold(app, context.record, bibid, patron);
  }
  sendAlreadyOwnedActionEmail(app, context.record, patron);
}

function placeAlreadyOwnedHold(app, record, bibid, patron) {
  var localStaffAuth;
  try {
    localStaffAuth = polaris.adminStaffAuth();
  } catch (e) { }

  try {
    polaris.reconcileRecord(app, localStaffAuth, record, bibid);
  } catch (reconcileErr) {
    app.logger().error("Already-owned reconcile failed during staff action", "recordId", record.id, "bibid", bibid, "error", String(reconcileErr));
  }

  if (record.getBool("autohold") === false) {
    records.appendSystemNote(record, "Skipped auto-hold for 'Already Own' action because patron opted out of automatic hold placement.");
    app.save(record);
    return;
  }

  try {
    polaris.placeHold(localStaffAuth, bibid, patron.PatronID, false);
    records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
  } catch (holdErr) {
    app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
  }
}

function sendAlreadyOwnedActionEmail(app, record, patron) {
  try {
    if (!mail.alreadyOwned(app, record, patron)) {
      routeUtils.noteSkippedEmail(app, record);
    }
  } catch (mailErr) {
    app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
  }
}

function sendRejectedActionEmail(app, context, patron) {
  try {
    if (!mail.rejected(app, context.record, patron, context.data.rejectionTemplateId)) {
      routeUtils.noteSkippedEmail(app, context.record);
    }
  } catch (mailErr) {
    app.logger().error("Rejected suggestion email failed", "recordId", context.record.id, "error", String(mailErr));
  }
}



function sendPurchaseReminderIfRequested(app, context) {
  var isAdditionalCopy = context.action === "additionalCopy";
  var purchaseReminderEmail = {
    requested: (context.action === "purchase" || isAdditionalCopy) && context.data.emailPurchaseReminder === true,
    sent: false,
    message: ""
  };

  if (!purchaseReminderEmail.requested) {
    return purchaseReminderEmail;
  }

  var staffEmail = String(context.staff.get("weekly_action_summary_email") || "").trim();
  if (!staffEmail) {
    purchaseReminderEmail.message = "Purchase saved. Add an email address to your staff profile to email yourself purchase reminders.";
    return purchaseReminderEmail;
  }

  try {
    var itemUrl = isAdditionalCopy
      ? routeUtils.appendQuery(config.staffUrl(app), { stage: "additional_copies", request: context.record.id })
      : routeUtils.staffRequestUrl(app, context.record);
    purchaseReminderEmail.sent = isAdditionalCopy
      ? !!mail.additionalCopyReminder(app, context.record, context.staff, staffEmail, itemUrl)
      : !!mail.purchaseReminder(app, context.record, context.staff, staffEmail, itemUrl);
    purchaseReminderEmail.message = purchaseReminderEmail.sent
      ? (isAdditionalCopy ? "Additional copy saved and reminder email sent." : "Purchase saved and reminder email sent.")
      : (isAdditionalCopy ? "Additional copy saved, but email notifications are not configured." : "Purchase saved, but email notifications are not configured.");
  } catch (mailErr) {
    app.logger().error("Purchase reminder email failed", "recordId", context.record.id, "staffUserId", context.staff.id, "error", String(mailErr));
    purchaseReminderEmail.message = isAdditionalCopy ? "Additional copy saved, but the reminder email could not be sent." : "Purchase saved, but the reminder email could not be sent.";
  }
  return purchaseReminderEmail;
}


function maybeRunImmediatePromoter(app, context) {
  if (!context.shouldRunImmediatePromoter) {
    return;
  }

  try {
    var updatedStatus = records.normalizeStatus(context.record.get("status"));
    if (updatedStatus === records.STATUS.SUGGESTION || config.suggestionLimit(app, "").autoPromote !== false) {
      jobs.promoteRequestNow(app, polaris.adminStaffAuth(), context.record);
      context.record = app.findRecordById("title_requests", context.record.id);
    }
  } catch (promoteErr) {
    app.logger().error("Immediate identifier promotion failed", "recordId", context.record.id, "error", String(promoteErr));
  }
}

function handleAlreadyOwnOrRejectSideEffects(app, context) {
  if (context.action !== "alreadyOwn" && context.action !== "reject" && !(context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD)) {
    return;
  }

  var patron = refreshedActionPatron(app, context.record);
  if (context.action === "alreadyOwn") {
    handleAlreadyOwnSideEffects(app, context, patron);
  } else if (context.action === "reject") {
    sendRejectedActionEmail(app, context, patron);
  } else if (context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD) {
    handleAlreadyOwnSideEffects(app, context, patron);
  }
}

function refreshedActionPatron(app, record) {
  try {
    return polaris.lookupPatron(polaris.adminStaffAuth(), record.get("barcode"));
  } catch (err) {
    app.logger().warn("Could not refresh patron data for staff action email", "recordId", record.id, "error", String(err));
  }
  return null;
}

function handleAlreadyOwnSideEffects(app, context, patron) {
  var bibid = String(context.data.bibid || "").trim();
  if (bibid && patron && patron.PatronID) {
    placeAlreadyOwnedHold(app, context.record, bibid, patron);
  }
  sendAlreadyOwnedActionEmail(app, context.record, patron);
}

function placeAlreadyOwnedHold(app, record, bibid, patron) {
  var localStaffAuth;
  try {
    localStaffAuth = polaris.adminStaffAuth();
  } catch (e) { }

  try {
    polaris.reconcileRecord(app, localStaffAuth, record, bibid);
  } catch (reconcileErr) {
    app.logger().error("Already-owned reconcile failed during staff action", "recordId", record.id, "bibid", bibid, "error", String(reconcileErr));
  }

  if (record.getBool("autohold") === false) {
    records.appendSystemNote(record, "Skipped auto-hold for 'Already Own' action because patron opted out of automatic hold placement.");
    app.save(record);
    return;
  }

  try {
    polaris.placeHold(localStaffAuth, bibid, patron.PatronID, false);
    records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
  } catch (holdErr) {
    app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
  }
}

function sendAlreadyOwnedActionEmail(app, record, patron) {
  try {
    if (!mail.alreadyOwned(app, record, patron)) {
      routeUtils.noteSkippedEmail(app, record);
    }
  } catch (mailErr) {
    app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
  }
}

function sendRejectedActionEmail(app, context, patron) {
  try {
    if (!mail.rejected(app, context.record, patron, context.data.rejectionTemplateId)) {
      routeUtils.noteSkippedEmail(app, context.record);
    }
  } catch (mailErr) {
    app.logger().error("Rejected suggestion email failed", "recordId", context.record.id, "error", String(mailErr));
  }
}

function sendPurchaseReminderIfRequested(app, context) {
  var isAdditionalCopy = context.action === "additionalCopy";
  var purchaseReminderEmail = {
    requested: (context.action === "purchase" || isAdditionalCopy) && context.data.emailPurchaseReminder === true,
    sent: false,
    message: ""
  };

  if (!purchaseReminderEmail.requested) {
    return purchaseReminderEmail;
  }

  var staffEmail = String(context.staff.get("weekly_action_summary_email") || "").trim();
  if (!staffEmail) {
    purchaseReminderEmail.message = "Purchase saved. Add an email address to your staff profile to email yourself purchase reminders.";
    return purchaseReminderEmail;
  }

  try {
    var itemUrl = isAdditionalCopy
      ? routeUtils.appendQuery(config.staffUrl(app), { stage: "additional_copies", request: context.record.id })
      : routeUtils.staffRequestUrl(app, context.record);
    purchaseReminderEmail.sent = isAdditionalCopy
      ? !!mail.additionalCopyReminder(app, context.record, context.staff, staffEmail, itemUrl)
      : !!mail.purchaseReminder(app, context.record, context.staff, staffEmail, itemUrl);
    purchaseReminderEmail.message = purchaseReminderEmail.sent
      ? (isAdditionalCopy ? "Additional copy saved and reminder email sent." : "Purchase saved and reminder email sent.")
      : (isAdditionalCopy ? "Additional copy saved, but email notifications are not configured." : "Purchase saved, but email notifications are not configured.");
  } catch (mailErr) {
    app.logger().error("Purchase reminder email failed", "recordId", context.record.id, "staffUserId", context.staff.id, "error", String(mailErr));
    purchaseReminderEmail.message = isAdditionalCopy ? "Additional copy saved, but the reminder email could not be sent." : "Purchase saved, but the reminder email could not be sent.";
  }
  return purchaseReminderEmail;
}





module.exports = {
  maybeRunImmediatePromoter,
  handleAlreadyOwnOrRejectSideEffects,
  refreshedActionPatron,
  handleAlreadyOwnSideEffects,
  placeAlreadyOwnedHold,
  sendAlreadyOwnedActionEmail,
  sendRejectedActionEmail,
  sendPurchaseReminderIfRequested,
  maybeRunImmediatePromoter,
  handleAlreadyOwnOrRejectSideEffects,
  refreshedActionPatron,
  handleAlreadyOwnSideEffects,
  placeAlreadyOwnedHold,
  sendAlreadyOwnedActionEmail,
  sendRejectedActionEmail,
  sendPurchaseReminderIfRequested
};
