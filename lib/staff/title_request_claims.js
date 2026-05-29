
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);

const assignmentPolicy = require(`${__hooks}/../lib/staff/assignment_policy.js`);

const claimUtils = require(`${__hooks}/../lib/staff/claim_utils.js`);
const assignmentNotifications = require(`${__hooks}/../lib/staff/assignment_notifications.js`);

function staffClaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "claim");
}

function staffUnclaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "unclaim");
}

function staffAssignTitleRequest(e) {
  var actor = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var payload = routeUtils.body(e);
  var assigneeId = String(payload.assigneeId || "").trim();

  if (!assigneeId) {
    return e.json(400, { message: "Assignee ID is required." });
  }

  try {
    var result = claimUtils.runClaimMutation(e.app, function (app) {
      var record = app.findRecordById("title_requests", id);
      var accessError = routeUtils.requireTitleRequestAccess(e, actor, record);
      if (accessError) {
        return { response: accessError };
      }

      var assignee = app.findRecordById("staff_users", assigneeId);

      assignmentPolicy.assertActiveStaff(actor, "Actor");
      assignmentPolicy.assertActiveStaff(assignee, "Assignee");
      assignmentPolicy.assertOpenItem(record);
      assignmentPolicy.assertSameLibraryAssignment(actor, assignee, record.get("libraryOrgId"));

      formatClaimRules.setManualClaim(record, assignee);
      app.save(record);

      records.recordEvent(
        app,
        record,
        "claim_manual_assigned",
        "Claim transferred to " + claimUtils.staffClaimDisplayName(assignee) + " by " + claimUtils.staffClaimDisplayName(actor) + ".",
        {
          actorName: actor.get("username") || claimUtils.staffClaimDisplayName(actor)
        }
      );

      return {
        record: app.findRecordById("title_requests", record.id),
        assigneeId: assignee.id,
        actor: actor,
        type: "title_request"
      };
    });

    if (result && result.response) {
      return result.response;
    }

    assignmentNotifications.sendAssignmentNotificationAfterCommit(e.app, result);

    return e.json(200, records.titleRequestToJson(result.record, e.app));
  } catch (err) {
    var status = err && err.statusCode ? err.statusCode : 400;
    e.app.logger().error("Staff assign action failed", "requestId", id, "error", String(err));
    return e.json(status, { message: err.message || "Assignment failed." });
  }
}

function mutateTitleRequestClaim(e, action) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();

  try {
    var updated = claimUtils.runClaimMutation(e.app, function (app) {
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

function claimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId) {
    throw claimConflictError(record);
  }

  formatClaimRules.setManualClaim(record, staff);
  app.save(record);
  records.recordEvent(app, record, "claim_manual_assigned", "Manually claimed by " + claimUtils.staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || claimUtils.staffClaimDisplayName(staff)
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
  records.recordEvent(app, record, "claim_manual_cleared", "Manual claim cleared by " + claimUtils.staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || claimUtils.staffClaimDisplayName(staff)
  });
  return app.findRecordById("title_requests", record.id);
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

function runClaimMutation(app, fn) {
  return claimUtils.runClaimMutation(app, fn);
}

function staffClaimDisplayName(staff) {
  return claimUtils.staffClaimDisplayName(staff);
}



module.exports = {
  staffClaimTitleRequest: staffClaimTitleRequest,
  staffUnclaimTitleRequest: staffUnclaimTitleRequest,
  staffAssignTitleRequest: staffAssignTitleRequest,
  mutateTitleRequestClaim: mutateTitleRequestClaim,
  runClaimMutation: runClaimMutation,
  claimTitleRequest: claimTitleRequest,
  unclaimTitleRequest: unclaimTitleRequest,
  staffClaimDisplayName: staffClaimDisplayName,
  claimConflictError: claimConflictError,
  forbiddenClaimError: forbiddenClaimError
};
