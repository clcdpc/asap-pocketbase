
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);

const orgs = require(`${__hooks}/../lib/orgs.js`);

const claimsRoutes = require(`${__hooks}/../lib/staff/title_request_claims.js`);
const staffClaimDisplayName = claimsRoutes.staffClaimDisplayName;

const settingsSave = require(`${__hooks}/../lib/staff/settings_save.js`);


function staffEmailStatus(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var orgId = String(routeUtils.queryValue(e, "orgId") || "").trim();
  if (!orgId) {
    orgId = routeUtils.isSuperAdmin(staff) ? "system" : String(staff.get("libraryOrgId") || "").trim();
  }
  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to this library email status." });
  }
  return e.json(200, config.emailStatus(e.app, orgId === "system" ? "" : orgId));
}










function staffTitleRequestAction(e) {
  try {
    var context = titleRequestActionContext(e);
    if (context.response) {
      return context.response;
    }

    var additionalCopyTask = null;

    if (context.nextStatus === records.STATUS.PENDING_HOLD && !String(context.data.bibid || "").trim()) {
      return e.json(400, { message: "BIB ID is required before moving this suggestion to Pending hold." });
    }

    var bibActionResponse = prepareTitleRequestBibAction(e, context);
    if (bibActionResponse) {
      return bibActionResponse;
    }

    finalizeTitleRequestCloseReason(e.app, context);
    context.record = records.updateTitleRequest(e.app, context.id, context.data, context.staff.get("username"));
    if (context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD) {
      records.appendSystemNote(context.record, "Suggestion approved for purchase; moving directly to Pending hold as BIB ID is present.");
      e.app.save(context.record);
    }
    if (context.action === "additionalCopy") {
      additionalCopyTask = additionalCopies.createFromTitleRequest(e.app, context.record, context.staff, context.data);
      var holdVerb = records.normalizeStatus(context.record.get("status")) === records.STATUS.HOLD_PLACED ? "placed" : "queued";
      records.appendSystemNote(
        context.record,
        "Additional copy request created for BIB " + String(context.data.bibid || "").trim() + ". Patron hold was " + holdVerb + " for the same BIB."
      );
      e.app.save(context.record);
    }
    if (context.formatChanged) {
      formatClaimRules.applyFormatClaimRule(e.app, context.record, {
        trigger: "format_changed",
        previousFormat: context.originalFormat,
        actorName: context.staff.get("username") || "system"
      });
      context.record = e.app.findRecordById("title_requests", context.id);
    }
    applyCatalogFoundWorkflow(e.app, context.record, context.data, context.staff);
    maybeRunImmediatePromoter(e.app, context);
    handleAlreadyOwnOrRejectSideEffects(e.app, context);

    var purchaseReminderEmail = sendPurchaseReminderIfRequested(e.app, context);
    var response = records.titleRequestToJson(context.record, e.app);
    response.purchaseReminderEmail = purchaseReminderEmail;
    if (additionalCopyTask) {
      response.additionalCopyRequest = additionalCopies.toJson(additionalCopyTask, e.app);
    }

    e.app.logger().info("Staff action succeeded", "recordId", context.id, "action", context.action, "nextStatus", context.nextStatus);
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err), "recordId", e.request.pathValue("id"));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function applyCatalogFoundWorkflow(app, record, data, staff) {
  var action = String(data && data.action || "").trim();
  var bibId = String(data && data.bibid || "").trim();
  if (action !== "catalogFound" || !bibId) {
    return;
  }

  records.addWorkflowTagForRequest(app, record, "Identifier found");
  records.appendSystemNote(
    record,
    "Staff selected Polaris BIB " + bibId + "; request queued for hold placement."
  );

  records.setCanonicalRefs(app, record);
  app.save(record);
}

function titleRequestActionContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = e.request.pathValue("id");
  var data = routeUtils.body(e);
  var action = String(data.action || "");
  var nextStatus = records.normalizeStatus(data.status);
  var record;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (findErr) {
    return {
      response: e.json(404, { message: "Suggestion not found: " + id })
    };
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }

  var oldStatus = records.normalizeStatus(record.get("status"));
  var originalIdentifier = String(record.get("identifier") || "").trim();
  var nextIdentifier = data.identifier !== undefined && data.identifier !== null
    ? String(data.identifier).trim()
    : originalIdentifier;
  var originalFormat = records.normalizeFormat(record.get("format"));
  var nextFormat = data.format !== undefined && data.format !== null
    ? records.normalizeFormat(data.format)
    : originalFormat;
  var originalBibId = String(record.get("bibid") || "").trim();
  var nextBibId = data.bibid !== undefined && data.bibid !== null
    ? String(data.bibid).trim()
    : originalBibId;

  return {
    response: null,
    staff: staff,
    id: id,
    data: data,
    action: action,
    record: record,
    oldStatus: oldStatus,
    nextStatus: nextStatus,
    isClosingRequest: nextStatus === records.STATUS.CLOSED,
    isDuplicateClose: action === "closeDuplicate",
    isActiveHoldTarget: nextStatus === records.STATUS.PENDING_HOLD || nextStatus === records.STATUS.HOLD_PLACED || action === "alreadyOwn" || action === "additionalCopy",
    duplicateCloseNoteAdded: false,
    originalFormat: originalFormat,
    formatChanged: nextFormat !== originalFormat,
    shouldRunImmediatePromoter: (!!nextIdentifier && nextIdentifier !== originalIdentifier) || (!!nextBibId && nextBibId !== originalBibId)
  };
}

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








function staffDeleteClosedRequest(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var id = e.request.pathValue("id");
  var record;
  var isAdditionalCopy = false;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    try {
      record = e.app.findRecordById("additional_copy_requests", id);
      isAdditionalCopy = true;
    } catch (err2) {
      return e.json(404, { message: "Closed request not found." });
    }
  }

  // Common access check (additional copy uses same library logic)
  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return accessError;
  }

  if (records.normalizeStatus(record.get("status")) !== records.STATUS.CLOSED) {
    return e.json(400, { message: "Only closed requests can be deleted." });
  }

  try {
    if (isAdditionalCopy) {
      records.deleteAdditionalCopyRequestWithAudit(e.app, record, staff, "single");
    } else {
      records.deleteTitleRequestWithAudit(e.app, record, staff, "single");
    }
    return e.json(200, { success: true });
  } catch (err3) {
    return e.json(400, { message: err3.message || "Could not delete closed request." });
  }
}

function staffDeleteClosedRequestsBulk(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var data = routeUtils.body(e);
  if (String(data.confirm || "") !== "DELETE") {
    return e.json(400, { message: "Type DELETE to confirm bulk deletion." });
  }

  try {
    var deleted = records.deleteClosedRequestsBulk(e.app, staff, data.confirm);
    return e.json(200, { success: true, deleted: deleted });
  } catch (err) {
    return e.json(400, { message: err.message || "Could not delete closed requests." });
  }
}

function staffTestPolaris(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  var data = routeUtils.body(e);
  var polarisData = data && data.polaris ? routeUtils.buildPolarisData(data) : config.polaris();
  return routeUtils.testPolarisConnection(e, polarisData);
}

function staffTestSmtp(e) {
  var staff = routeUtils.requireSuperAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    config.applyMailSettings(e.app);

    var d = routeUtils.body(e);
    var email = String(d.email || "").trim() || staff.get("email");
    if (!email) {
      return e.json(400, { success: false, message: "No recipient email address specified (and your staff account has no email)." });
    }
    var subject = "Test SMTP Connection";
    var text = "This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.";
    var html = "<p>This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.</p>";
    var ok = mail.send(e.app, email, subject, text, html);

    if (ok) {
      return e.json(200, { success: true, message: "Test email sent to " + email + "!" });
    }
    return e.json(400, { success: false, message: "Mailer failed. Please check your from address and SMTP settings." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

function staffSyncOrganizations(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    var result = orgs.syncOrganizations(e.app, polaris.adminStaffAuth());
    return e.json(200, {
      success: true,
      synced: result.synced || 0,
      message: "Organization hierarchy synced."
    });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}


function getLibraryOverridesSummary(e) {
  try {
    var staff = routeUtils.requireSuperAdminStaff(e);
    if (!staff) {
      return e.json(403, { message: "Super admin access required." });
    }

    var orgs = e.app.findRecordsByFilter("polaris_organizations", "organizationCodeId = '2'", "", 0, 0);
    var idToOrgId = {};
    if (orgs) {
      for (var i = 0; i < orgs.length; i++) {
        var o = orgs[i];
        idToOrgId[o.id] = String(o.get("organizationId") || "").trim();
      }
    }

    var summary = {};

    function addEntry(orgId, section) {
      if (!orgId || orgId === "system") return;
      if (!summary[orgId]) summary[orgId] = [];
      if (summary[orgId].indexOf(section) === -1) {
        summary[orgId].push(section);
      }
    }

    function processList(list, section, useOrgIdField) {
      if (!list) return;
      for (var i = 0; i < list.length; i++) {
        var row = list[i];
        var orgId = "";
        if (useOrgIdField) {
          orgId = String(row.get("orgId") || row.get("libraryOrgId") || "").trim();
        } else {
          var relId = String(row.get("libraryOrganization") || "").trim();
          orgId = idToOrgId[relId];
        }
        addEntry(orgId, section);
      }
    }

    // Workflow
    processList(e.app.findRecordsByFilter("workflow_settings", "scope = 'library'", "", 0, 0), "workflow");

    // Patron Experience
    processList(e.app.findRecordsByFilter("ui_settings", "scope = 'library'", "", 0, 0), "patron");
    processList(e.app.findRecordsByFilter("material_formats", "scope = 'library'", "", 0, 0), "patron");
    processList(e.app.findRecordsByFilter("patron_settings_overrides", "", "", 0, 0), "patron", true);
    processList(e.app.findRecordsByFilter("patron_library_settings", "", "", 0, 0), "patron");

    // Templates
    processList(e.app.findRecordsByFilter("email_templates", "scope = 'library'", "", 0, 0), "templates");
    processList(e.app.findRecordsByFilter("rejection_templates", "scope = 'library'", "", 0, 0), "templates");

    // Staff Access (show which libraries have users)
    processList(e.app.findRecordsByFilter("staff_users", "libraryOrgId != 'system'", "", 0, 0), "staff", true);

    return e.json(200, summary);
  } catch (err) {
    if ($app && $app.logger()) {
      $app.logger().error("Overrides summary failed", "error", String(err));
    }
    return e.json(400, { message: String(err) });
  }
}


function getLibrarySettings(e) {
  try {
    var staff = routeUtils.requireAdminStaff(e);
    if (!staff) {
      return e.json(403, { message: "Admin access required to view settings." });
    }
    var orgId = String(routeUtils.queryValue(e, "orgId") || "").trim();

    if (!orgId) {
      orgId = String(staff.get("libraryOrgId") || "").trim();
    }

    if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
      return e.json(403, { message: "Access denied to these library settings." });
    }

    if (orgId === "system") {
      if (!routeUtils.isSuperAdmin(staff)) {
        return e.json(403, { message: "Only super admins can view system settings." });
      }
      var s = config.getSettings();
      var wf = config.suggestionLimit(e.app, "");
      return e.json(200, {
        orgId: orgId,
        emails: s.emails,
        ui_text: s.ui_text,
        workflow: workflowWithEnabled(e.app, wf),
        formatClaimRules: [],
        formatClaimStaffOptions: [],
        polaris: s.polaris,
        smtp: s.smtp,
        staffUrl: s.staffUrl,
        leapBibUrlPattern: s.leapBibUrlPattern || "",
        emailStatus: config.emailStatus(e.app, ""),
        organizationSync: organizationSyncStatus(e.app),
        isOverride: false
      });
    }

    var ls = config.librarySettings(e.app, orgId);
    return e.json(200, {
      orgId: orgId,
      emails: ls.emails,
      ui_text: ls.ui_text,
      workflow: workflowWithEnabled(e.app, ls.workflow),
      formatClaimRules: formatClaimRulesForLibrary(e.app, orgId),
      formatClaimStaffOptions: formatClaimStaffOptions(e.app, orgId),
      leapBibUrlPattern: ls.leapBibUrlPattern || "",
      emailStatus: config.emailStatus(e.app, orgId === "system" ? "" : orgId),
      organizationSync: organizationSyncStatus(e.app),
      isOverride: hasLibraryOverride(e.app, orgId)
    });
  } catch (err) {
    e.app.logger().error("Failed to load library settings", "error", String(err));
    return e.json(500, { message: err.message || String(err) });
  }
}

function formatClaimRulesForLibrary(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId || orgId === "system") return [];
  try {
    var rows = app.findRecordsByFilter("format_claim_rules", "libraryOrgId = {:libraryOrgId} && active = true", "format", 500, 0, { libraryOrgId: orgId });
    return rows.map(function (row) {
      var staffUserId = normalizeRelationId(row.get("staffUserId")) || normalizeRelationId(row.get("staffUser"));
      return {
        id: row.id,
        libraryOrgId: row.get("libraryOrgId") || "",
        format: row.get("format") || "",
        staffUserId: staffUserId || "",
        active: row.getBool("active")
      };
    });
  } catch (err) {
    return [];
  }
}

function normalizeRelationId(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (Array.isArray(value)) {
    if (!value.length) return "";
    return normalizeRelationId(value[0]);
  }
  if (typeof value === "object") {
    return String(value.id || value.recordId || value.value || "").trim();
  }
  return String(value || "").trim();
}

function formatClaimStaffOptions(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId || orgId === "system") return [];
  try {
    var rows = app.findRecordsByFilter("staff_users", "(libraryOrgId = {:libraryOrgId} || role = 'super_admin') && active = true", "displayName,username", 500, 0, { libraryOrgId: orgId });
    return rows.map(function (row) {
      return {
        id: row.id,
        displayName: staffClaimDisplayName(row),
        username: row.get("username") || "",
        role: row.get("role") || "staff",
        libraryOrgId: row.get("libraryOrgId") || ""
      };
    });
  } catch (err) {
    return [];
  }
}

function workflowWithEnabled(app, workflow) {
  var copy = Object.assign({}, workflow || {});
  copy.enabledLibraryOrgIds = config.enabledLibraryOrgIds(app);
  return copy;
}

function organizationSyncStatus(app) {
  var sys = config.getSystemSettings(app);
  return {
    status: sys ? sys.get("organizationsSyncStatus") || "not_loaded" : "not_loaded",
    message: sys ? sys.get("organizationsSyncMessage") || "" : "",
    error: sys ? sys.get("organizationsSyncError") || "" : "",
    lastSynced: sys ? sys.get("organizationsLastSynced") || "" : ""
  };
}

function hasLibraryOverride(app, orgId) {
  var org = config.findOrganization(app, orgId);
  if (!org) return false;
  var filters = [
    ["workflow_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["ui_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["email_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["rejection_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["material_formats", "scope = 'library' && libraryOrganization = {:org}"],
    ["patron_settings_overrides", "orgId = {:orgId}"],
    ["patron_library_settings", "libraryOrganization = {:org}"]
  ];
  for (var i = 0; i < filters.length; i++) {
    try {
      app.findFirstRecordByFilter(filters[i][0], filters[i][1], { org: org.id, orgId: String(orgId || "").trim() });
      return true;
    } catch (err) { }
  }
  return false;
}

function updateLibrarySettings(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required to update settings." });
  }
  var payload = routeUtils.body(e);
  payload._staffUser = staff;
  var orgId = String(payload.orgId || "").trim();
  var action = String(payload.action || "save").toLowerCase();

  if (!orgId) {
    return e.json(400, { message: "orgId is required." });
  }

  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to these library settings." });
  }

  // For library-scoped saves by non-super-admins, strip global-only fields
  // so even if the frontend sends them, they cannot affect system settings.
  if (orgId !== "system" && !routeUtils.isSuperAdmin(staff)) {
    delete payload.polaris;
    delete payload.smtp;
    delete payload.staffUrl;
    delete payload.leapBibUrlPattern;
    if (payload.workflow) {
      delete payload.workflow.enabledLibraryOrgIds;
    }
  }

  if (orgId === "system") {
    if (!routeUtils.isSuperAdmin(staff)) {
      return e.json(403, { message: "Only super admins can update system settings." });
    }
    try {
      settingsSave.saveSystemSettingsPayload(e.app, payload);
    } catch (err) {
      var systemErrorPayload = { message: err.message || String(err) };
      if (err.code) systemErrorPayload.code = err.code;
      return e.json(400, systemErrorPayload);
    }
  } else {
    try {
      if (action === "reset") {
        settingsSave.settingsEmail.resetLibrarySettings(e.app, orgId);
      } else {
        settingsSave.saveLibraryScopedSettings(e.app, orgId, payload);
      }
    } catch (err) {
      var errorPayload = { message: err.message || String(err) };
      if (err.code) errorPayload.code = err.code;
      return e.json(400, errorPayload);
    }
  }

  return e.json(200, { success: true });
}



module.exports = {
  staffEmailStatus,
  getLibraryOverridesSummary,
  getLibrarySettings,
  formatClaimRulesForLibrary,
  normalizeRelationId,
  formatClaimStaffOptions,
  workflowWithEnabled,
  organizationSyncStatus,
  hasLibraryOverride,
  updateLibrarySettings
};
