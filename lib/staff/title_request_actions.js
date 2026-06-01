
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const pickupPreference = require(`${__hooks}/../lib/polaris/pickup_preference_context.js`);
const resolvePolarisUpdateActor = require(`${__hooks}/../lib/staff/polaris_actor.js`).resolvePolarisUpdateActor;

const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const titleRequestActionContext = require(`${__hooks}/../lib/staff/title_request_action_context.js`).titleRequestActionContext;
const finalizeTitleRequestCloseReason = require(`${__hooks}/../lib/staff/title_request_bib_actions.js`).finalizeTitleRequestCloseReason;

const bibActions = require(`${__hooks}/../lib/staff/title_request_bib_actions.js`);
const prepareTitleRequestBibAction = bibActions.prepareTitleRequestBibAction;

const sideEffects = require(`${__hooks}/../lib/staff/title_request_side_effects.js`);
const handleAlreadyOwnOrRejectSideEffects = sideEffects.handleAlreadyOwnOrRejectSideEffects;
const sendPurchaseReminderIfRequested = sideEffects.sendPurchaseReminderIfRequested;
const maybeRunImmediatePromoter = sideEffects.maybeRunImmediatePromoter;

function loadAdditionalCopySourceContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var record;
  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    return { response: e.json(404, { message: "Title request not found." }) };
  }
  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }
  var status = records.normalizeStatus(record.get("status"));
  if (status !== records.STATUS.PENDING_HOLD && status !== records.STATUS.HOLD_PLACED) {
    return { response: e.json(400, { message: "Additional-copy tasks can only be created from open pending-hold or hold-placed rows." }) };
  }
  var bibid = String(record.get("bibid") || "").trim();
  if (!bibid) {
    return { response: e.json(400, { message: "BIB ID is required before creating an additional-copy task." }) };
  }
  var openCount = additionalCopies.countOpenForLibraryBib(e.app, record.get("libraryOrgId"), bibid);
  return { staff: staff, record: record, status: status, bibid: bibid, openCount: openCount };
}

function staffTitleRequestAdditionalCopyPreview(e) {
  try {
    var context = loadAdditionalCopySourceContext(e);
    if (context.response) return context.response;
    return e.json(200, {
      bibid: context.bibid,
      openCount: context.openCount,
      emailPurchaseReminderDefault: !!context.staff.getBool("purchase_reminder_default")
    });
  } catch (err) {
    e.app.logger().error("Additional-copy preview failed", "error", String(err), "recordId", e.request.pathValue("id"));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function staffTitleRequestAdditionalCopyCreate(e) {
  try {
    var context = loadAdditionalCopySourceContext(e);
    if (context.response) return context.response;
    var data = routeUtils.body(e);
    var task = additionalCopies.createFromTitleRequest(e.app, context.record, context.staff, { bibid: context.bibid });
    var openCountAfter = context.openCount + 1;
    var holdText = context.status === records.STATUS.HOLD_PLACED ? "placed" : "queued";
    records.appendSystemNote(
      context.record,
      "Additional copy request created for BIB " + context.bibid + ". Patron hold remains " + holdText + " for the same BIB. Open additional-copy tasks for this library/BIB: " + openCountAfter + "."
    );
    e.app.save(context.record);

    var purchaseReminderEmail = sendPurchaseReminderIfRequested(e.app, {
      action: "additionalCopy",
      data: { emailPurchaseReminder: routeUtils.boolValue(data.emailPurchaseReminder, false) },
      record: context.record,
      staff: context.staff
    });
    return e.json(200, {
      additionalCopyRequest: additionalCopies.toJson(task, e.app),
      openCountBefore: context.openCount,
      openCountAfter: openCountAfter,
      purchaseReminderEmail: purchaseReminderEmail
    });
  } catch (err) {
    e.app.logger().error("Additional-copy create failed", "error", String(err), "recordId", e.request.pathValue("id"));
    return e.json(400, { message: "System error: " + err.message });
  }
}


function staffTitleRequestAction(e) {
  try {
    var context = titleRequestActionContext(e);
    if (context.response) {
      return context.response;
    }

    var validationResponse = validateTitleRequestAction(context, e);
    if (validationResponse) {
      return validationResponse;
    }

    var bibActionResponse = applyBibAction(e, context);
    if (bibActionResponse) {
      return bibActionResponse;
    }

    persistTitleRequestAction(e, context);
    autoClaimTitleRequestAction(e.app, context);
    var additionalCopyTask = handleAdditionalCopyAction(e, context);
    applyPostUpdateRules(e, context);
    runTitleRequestSideEffects(e, context);
    var response = buildTitleRequestActionResponse(e, context, additionalCopyTask);

    e.app.logger().info("Staff action succeeded", "recordId", context.id, "action", context.action, "nextStatus", context.nextStatus);
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err), "recordId", e.request.pathValue("id"));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function validateTitleRequestAction(context, e) {
  if (context.nextStatus === records.STATUS.PENDING_HOLD && !String(context.data.bibid || "").trim()) {
    return e.json(400, { message: "BIB ID is required before moving this suggestion to Pending hold." });
  }
  return null;
}

function applyBibAction(e, context) {
  if (context.action === "additionalCopy") {
    context.data.publication = context.record.get("publication");
    context.data.selectedPolarisPublication = "";
  }
  return prepareTitleRequestBibAction(e, context);
}

function persistTitleRequestAction(e, context) {
  finalizeTitleRequestCloseReason(e.app, context);
  context.record = records.updateTitleRequest(e.app, context.id, context.data, context.staff.get("username"));
  if (context.action === "purchase" && context.nextStatus === records.STATUS.PENDING_HOLD) {
    records.appendSystemNote(context.record, "Suggestion approved for purchase; moving directly to Pending hold as BIB ID is present.");
    e.app.save(context.record);
  }
}

function autoClaimTitleRequestAction(app, context) {
  if (context.action === "assign") {
    return;
  }

  var staffId = String(context.staff.id || "").trim();
  if (!staffId) {
    return;
  }

  var currentClaimantId = String(context.record.get("claimedByStaffUserId") || "").trim();
  if (currentClaimantId === staffId) {
    return;
  }

  formatClaimRules.setManualClaim(context.record, context.staff);
  app.save(context.record);

  records.recordEvent(
    app,
    context.record,
    currentClaimantId ? "claim_manual_transferred" : "claim_manual_assigned",
    "Claim automatically set to " + formatClaimRules.claimDisplayName(context.staff) + " after staff action.",
    {
      actorName: context.staff.get("username") || formatClaimRules.claimDisplayName(context.staff),
      metadata: {
        trigger: "staff_action",
        action: context.action || ""
      }
    }
  );
}

function handleAdditionalCopyAction(e, context) {
  if (context.action !== "additionalCopy") {
    return null;
  }
  var additionalCopyTask = additionalCopies.createFromTitleRequest(e.app, context.record, context.staff, context.data);
  var holdVerb = records.normalizeStatus(context.record.get("status")) === records.STATUS.HOLD_PLACED ? "placed" : "queued";
  records.appendSystemNote(
    context.record,
    "Additional copy request created for BIB " + String(context.data.bibid || "").trim() + ". Patron hold was " + holdVerb + " for the same BIB."
  );
  e.app.save(context.record);
  return additionalCopyTask;
}

function applyPostUpdateRules(e, context) {
  if (context.formatChanged) {
    formatClaimRules.applyFormatClaimRule(e.app, context.record, {
      trigger: "format_changed",
      previousFormat: context.originalFormat,
      actorName: context.staff.get("username") || "system"
    });
    context.record = e.app.findRecordById("title_requests", context.id);
  }
}

function runTitleRequestSideEffects(e, context) {
  applyCatalogFoundWorkflow(e.app, context.record, context.data, context.staff);
  maybeRunImmediatePromoter(e.app, context);
  handleAlreadyOwnOrRejectSideEffects(e.app, context);
}

function buildTitleRequestActionResponse(e, context, additionalCopyTask) {
  var purchaseReminderEmail = sendPurchaseReminderIfRequested(e.app, context);
  var response = records.titleRequestToJson(context.record, e.app);
  response.purchaseReminderEmail = purchaseReminderEmail;
  if (additionalCopyTask) {
    response.additionalCopyRequest = additionalCopies.toJson(additionalCopyTask, e.app);
  }
  return response;
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

function loadEditablePickupRequestContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var record;
  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    return { response: e.json(404, { message: "Suggestion not found." }) };
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }

  return {
    staff: staff,
    record: record,
    id: id,
    status: records.normalizeStatus(record.get("status"))
  };
}

function pickupReadOnlyForStatus(status) {
  return status === records.STATUS.HOLD_PLACED || status === records.STATUS.CLOSED;
}

function buildPickupContextForEditableRequest(app, staffAuth, livePatron, options) {
  return pickupPreference.buildAvailablePickupPreferenceContext(app, staffAuth, livePatron, options || {});
}

function branchNameFromContext(pickupContext, id, fallback) {
  var branch = pickupPreference.findBranch(pickupContext.pickupBranches || [], id);
  return branch ? branch.label : (fallback || id || "");
}

function staffTitleRequestPickupOptions(e) {
  try {
    var context = loadEditablePickupRequestContext(e);
    if (context.response) return context.response;

    var barcode = String(context.record.get("barcode") || "").trim();
    if (!barcode) {
      return e.json(400, { message: "Request does not have a patron barcode." });
    }

    var data = routeUtils.body(e) || {};
    var forceRefresh = data.forceRefresh === true;

    var staffAuth = polaris.adminStaffAuth();
    var livePatron = polaris.lookupPatron(staffAuth, barcode);
    livePatron = orgs.attachPatronScope(e.app, livePatron, staffAuth, e.app.logger());
    var pickupContext = buildPickupContextForEditableRequest(e.app, staffAuth, livePatron, {
      forceRefresh: forceRefresh
    });

    return e.json(200, {
      requestId: context.record.id,
      status: context.status,
      readOnly: pickupReadOnlyForStatus(context.status),
      requestSnapshotPickupBranchId: context.record.get("preferredPickupBranchId") || "",
      requestSnapshotPickupBranchName: context.record.get("preferredPickupBranchName") || "",
      pickupBranches: pickupContext.pickupBranches || [],
      pickupBranchesRefreshedAt: pickupContext.pickupBranchesRefreshedAt || "",
      currentPreferredPickupBranchId: pickupContext.currentPreferredPickupBranchId || "",
      currentPreferredPickupBranchName: pickupContext.currentPreferredPickupBranchName || "",
      selectedPickupBranchId: pickupContext.selectedPickupBranchId || "",
      selectedPickupBranchName: pickupContext.selectedPickupBranchName || "",
      currentPreferenceAllowed: !!pickupContext.currentPreferenceAllowed,
      pickupBranchWarning: pickupContext.pickupBranchWarning || "",
      pickupOptionsUnavailable: !(pickupContext.pickupBranches || []).length
    });
  } catch (err) {
    e.app.logger().error("Staff pickup options failed", "recordId", e.request.pathValue("id"), "error", String(err));
    return e.json(400, { message: "Could not load pickup locations: " + (err.message || String(err)) });
  }
}

function staffTitleRequestPickupPreferenceUpdate(e) {
  try {
    var context = loadEditablePickupRequestContext(e);
    if (context.response) return context.response;

    if (pickupReadOnlyForStatus(context.status)) {
      return e.json(400, {
        message: "Pickup preference cannot be changed after the hold has been placed."
      });
    }

    var data = routeUtils.body(e) || {};
    var selectedId = String(data.preferredPickupBranchId || "").trim();
    var atLoad = String(data.currentPreferredPickupBranchIdAtLoad || "").trim();
    if (!selectedId) {
      return e.json(400, { message: "Choose a preferred pickup location." });
    }

    var barcode = String(context.record.get("barcode") || "").trim();
    if (!barcode) {
      return e.json(400, { message: "Request does not have a patron barcode." });
    }

    var staffAuth = polaris.adminStaffAuth();
    var livePatron = polaris.lookupPatron(staffAuth, barcode);
    livePatron = orgs.attachPatronScope(e.app, livePatron, staffAuth, e.app.logger());

    var pickupContext = buildPickupContextForEditableRequest(e.app, staffAuth, livePatron);
    var selectedBranch;
    try {
      selectedBranch = pickupPreference.validateSelectedPickupBranch(pickupContext, selectedId);
    } catch (err) {
      pickupContext = pickupPreference.buildAvailablePickupPreferenceContext(e.app, staffAuth, livePatron, { forceRefresh: true });
      selectedBranch = pickupPreference.validateSelectedPickupBranch(pickupContext, selectedId);
    }

    var liveCurrentId = pickupPreference.currentPreferredId(livePatron);
    if (atLoad && liveCurrentId !== atLoad && selectedBranch.id !== liveCurrentId) {
      return e.json(409, {
        message: "The patron’s preferred pickup location changed in Polaris. Reload pickup options and try again."
      });
    }

    var oldName = branchNameFromContext(
      pickupContext,
      liveCurrentId,
      context.record.get("preferredPickupBranchName") || liveCurrentId
    );
    var pickupChanged = selectedBranch.id !== liveCurrentId;

    if (pickupChanged) {
      var actor = resolvePolarisUpdateActor(context.staff, config.polaris(e.app));
      try {
        polaris.updatePatronPreferredPickupBranch(staffAuth, barcode, selectedBranch.id, actor);
      } catch (updateErr) {
        e.app.logger().error("Staff edit pickup preference update failed", "recordId", context.record.id, "error", String(updateErr));
        return e.json(502, {
          message: "Preferred pickup location could not be updated in Polaris. The request was not changed."
        });
      }

      var note = "Preferred pickup location changed from " +
        (oldName || liveCurrentId || "not set") +
        " to " +
        selectedBranch.label +
        " by " +
        (context.staff.get("username") || "staff") +
        (actor.fallbackUsed ? " using configured system Polaris user ID." : ".");
      records.appendSystemNote(context.record, note);
      records.recordEvent(e.app, context.record, "pickup_preference_changed", note, {
        actorName: context.staff.get("username") || "staff",
        actorType: "staff",
        metadata: {
          fromPickupBranchId: liveCurrentId,
          fromPickupBranchName: oldName || "",
          toPickupBranchId: selectedBranch.id,
          toPickupBranchName: selectedBranch.label,
          polarisUserIdFallbackUsed: !!actor.fallbackUsed
        }
      });
    }

    var snapshotChanged =
      String(context.record.get("preferredPickupBranchId") || "") !== selectedBranch.id ||
      String(context.record.get("preferredPickupBranchName") || "") !== selectedBranch.label;
    if (snapshotChanged || pickupChanged) {
      context.record.set("preferredPickupBranchId", selectedBranch.id);
      context.record.set("preferredPickupBranchName", selectedBranch.label);
      context.record.set("editedBy", context.staff.get("username") || "staff");
      context.record.set("updated", new Date().toISOString());
      records.setCanonicalRefs(e.app, context.record);
      e.app.save(context.record);
    }

    return e.json(200, {
      request: records.titleRequestToJson(context.record, e.app),
      pickupChanged: pickupChanged,
      snapshotChanged: snapshotChanged
    });
  } catch (err) {
    e.app.logger().error("Staff pickup preference save failed", "recordId", e.request.pathValue("id"), "error", String(err));
    return e.json(err.code || 400, { message: err.message || String(err) });
  }
}



module.exports = {
  staffTitleRequestAction,
  staffTitleRequestAdditionalCopyPreview,
  staffTitleRequestAdditionalCopyCreate,
  staffTitleRequestPickupOptions,
  staffTitleRequestPickupPreferenceUpdate,
  applyCatalogFoundWorkflow,
  autoClaimTitleRequestAction
};
