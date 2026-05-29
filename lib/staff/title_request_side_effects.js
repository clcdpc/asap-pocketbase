
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

function finalStatus(context) {
  return records.normalizeStatus(context.record && context.record.get("status"));
}

function actionStartedAsPurchase(context) {
  return context.action === "purchase";
}

function purchaseSkippedBecauseBibResolved(context) {
  var status = finalStatus(context);
  return actionStartedAsPurchase(context) && status !== records.STATUS.OUTSTANDING_PURCHASE;
}

function handleAlreadyOwnOrRejectSideEffects(app, context) {
  if (context.action !== "alreadyOwn" && context.action !== "reject" && context.action !== "purchase") {
    return;
  }

  if (context.action === "alreadyOwn") {
    handleAlreadyOwnSideEffects(app, context, refreshedActionPatron(app, context.record));
  } else if (context.action === "reject") {
    sendRejectedActionEmail(app, context, refreshedActionPatron(app, context.record));
  } else if (context.action === "purchase") {
    handlePurchaseOutcomeSideEffects(app, context);
  }
}

function handlePurchaseOutcomeSideEffects(app, context) {
  var status = finalStatus(context);

  if (status === records.STATUS.PENDING_HOLD) {
    return;
  }

  if (status === records.STATUS.HOLD_PLACED) {
    var patron = refreshedActionPatron(app, context.record);
    sendAlreadyOwnedActionEmail(app, context.record, patron);
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
  var status = finalStatus(context);
  var isPurchaseReminderEligible = context.action === "purchase" && status === records.STATUS.OUTSTANDING_PURCHASE;
  var purchaseReminderEmail = {
    requested: (isPurchaseReminderEligible || isAdditionalCopy) && context.data.emailPurchaseReminder === true,
    sent: false,
    message: ""
  };

  if (purchaseSkippedBecauseBibResolved(context) && context.data.emailPurchaseReminder === true) {
    purchaseReminderEmail.requested = false;
    purchaseReminderEmail.message = "Purchase reminder not sent because this request skipped the purchase queue.";
    return purchaseReminderEmail;
  }

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
  finalStatus,
  actionStartedAsPurchase,
  purchaseSkippedBecauseBibResolved,
  handleAlreadyOwnOrRejectSideEffects,
  handlePurchaseOutcomeSideEffects,
  refreshedActionPatron,
  handleAlreadyOwnSideEffects,
  placeAlreadyOwnedHold,
  sendAlreadyOwnedActionEmail,
  sendRejectedActionEmail,
  sendPurchaseReminderIfRequested
};
