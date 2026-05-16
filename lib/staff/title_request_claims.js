
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);

function staffClaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "claim");
}

function staffUnclaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "unclaim");
}

function mutateTitleRequestClaim(e, action) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();

  try {
    var updated = runClaimMutation(e.app, function (app) {
      var record = app.findRecordById("title_requests", id);
      var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
      if (accessError) {
        return { response: accessError };
      }

      if (action === "claim") {
        return claimTitleRequest(app, staff, record);
      }
      return unclaimTitleRequest(app, staff, record);
    });

    if (updated && updated.response) {
      return updated.response;
    }
    return e.json(200, records.titleRequestToJson(updated, e.app));
  } catch (err) {
    if (err && err.statusCode) {
      return e.json(err.statusCode, { message: err.message });
    }
    e.app.logger().error("Staff claim action failed", "requestId", id, "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function runClaimMutation(app, fn) {
  if (app && typeof app.runInTransaction === "function") {
    var result;
    app.runInTransaction(function (txApp) {
      result = fn(txApp);
    });
    return result;
  }
  return fn(app);
}

function claimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId) {
    throw claimConflictError(record);
  }

  formatClaimRules.setManualClaim(record, staff);
  app.save(record);
  records.recordEvent(app, record, "claim_manual_assigned", "Manually claimed by " + staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || staffClaimDisplayName(staff)
  });
  return app.findRecordById("title_requests", record.id);
}

function unclaimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId && !routeUtils.isAdminRole(staff)) {
    throw forbiddenClaimError("Only the staff member who claimed this request, an admin, or a super admin can unclaim it.");
  }

  formatClaimRules.clearClaim(record);
  app.save(record);
  records.recordEvent(app, record, "claim_manual_cleared", "Manual claim cleared by " + staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || staffClaimDisplayName(staff)
  });
  return app.findRecordById("title_requests", record.id);
}

function staffClaimDisplayName(staff) {
  return String(staff.get("displayName") || staff.get("username") || staff.get("identityKey") || "Staff").trim();
}

function claimConflictError(record) {
  var name = String(record.get("claimedByDisplayName") || "another staff member").trim();
  var err = new Error("This request is already claimed by " + name + ".");
  err.statusCode = 409;
  return err;
}

function forbiddenClaimError(message) {
  var err = new Error(message);
  err.statusCode = 403;
  return err;
}



module.exports = {
  staffClaimTitleRequest,
  staffUnclaimTitleRequest,
  mutateTitleRequestClaim,
  runClaimMutation,
  claimTitleRequest,
  unclaimTitleRequest,
  staffClaimDisplayName,
  claimConflictError,
  forbiddenClaimError
};
