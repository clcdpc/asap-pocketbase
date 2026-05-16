
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);

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



module.exports = {
  staffTitleRequestAction,
  applyCatalogFoundWorkflow
};
