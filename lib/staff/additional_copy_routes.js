
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);
const listRoutes = require(`${__hooks}/../lib/staff/title_request_list.js`);
const analyticsRoutes = require(`${__hooks}/../lib/staff/analytics_routes.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);

const assignmentPolicy = require(`${__hooks}/../lib/staff/assignment_policy.js`);
const titleRequestClaims = require(`${__hooks}/../lib/staff/title_request_claims.js`);

function staffAdditionalCopiesList(e) {
  try {
    var staff = routeUtils.requireAuth(e, "staff_users");
    var selectedScope = String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim();
    var status = String(routeUtils.queryValue(e, "status") || "open").trim();
    var result = additionalCopies.listForStaff(e.app, staff, { scope: selectedScope, status: status });
    return e.json(200, {
      items: result.items,
      scope: safeAdditionalCopyResponseScope(e.app, staff, result.scope),
      availableLibraries: safeAnalyticsLibraryOptions(e.app, staff)
    });
  } catch (err) {
    e.app.logger().warn("Additional-copy list route failed", "error", String(err));
    var fallbackStaff = null;
    try {
      fallbackStaff = routeUtils.requireAuth(e, "staff_users");
    } catch (authErr) {
      return e.json(401, { message: "Authentication required." });
    }
    var fallbackScope = additionalCopies.scopeForStaff(fallbackStaff, String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim());
    return e.json(200, {
      items: [],
      scope: safeAdditionalCopyResponseScope(e.app, fallbackStaff, fallbackScope),
      availableLibraries: safeAnalyticsLibraryOptions(e.app, fallbackStaff)
    });
  }
}

function safeAdditionalCopyResponseScope(app, staff, scope) {
  try {
    return listRoutes.titleRequestListResponseScope(app, staff, scope);
  } catch (err) {
    return {
      mode: scope && scope.mode || "library",
      libraryOrgId: scope && scope.libraryOrgId || "",
      label: scope && scope.mode === "all" ? "All libraries" : (staff.get("libraryOrgName") || scope && scope.libraryOrgId || "Current library"),
      superAdmin: routeUtils.isSuperAdmin(staff)
    };
  }
}

function safeAnalyticsLibraryOptions(app, staff) {
  if (!routeUtils.isSuperAdmin(staff)) {
    return [];
  }
  try {
    return orgs.analyticsLibraryOptions(app);
  } catch (err) {
    return [];
  }
}

function staffAdditionalCopyClose(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var task;
  try {
    task = e.app.findRecordById("additional_copy_requests", id);
  } catch (err) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  if (!routeUtils.sameLibrary(staff, task.get("libraryOrgId"))) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  var closed = additionalCopies.closeTask(e.app, task, staff);
  return e.json(200, additionalCopies.toJson(closed, e.app));
}

function staffAdditionalCopyReopen(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var task;
  try {
    task = e.app.findRecordById("additional_copy_requests", id);
  } catch (err) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  if (!routeUtils.sameLibrary(staff, task.get("libraryOrgId"))) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  var reopened = additionalCopies.reopenTask(e.app, task, staff);
  return e.json(200, additionalCopies.toJson(reopened, e.app));
}

function staffAdditionalCopyClaim(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var task;
  try {
    task = e.app.findRecordById("additional_copy_requests", id);
  } catch (err) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  if (!routeUtils.sameLibrary(staff, task.get("libraryOrgId"))) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  var claimed = additionalCopies.claimTask(e.app, task, staff);
  return e.json(200, additionalCopies.toJson(claimed, e.app));
}

function staffAdditionalCopyUnclaim(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var task;
  try {
    task = e.app.findRecordById("additional_copy_requests", id);
  } catch (err) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  if (!routeUtils.sameLibrary(staff, task.get("libraryOrgId"))) {
    return e.json(404, { message: "Additional-copy request not found." });
  }
  var unclaimed = additionalCopies.unclaimTask(e.app, task);
  return e.json(200, additionalCopies.toJson(unclaimed, e.app));
}



function staffAdditionalCopyAssign(e) {
  var actor = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();
  var payload = routeUtils.body(e);
  var assigneeId = String(payload.assigneeId || "").trim();

  if (!assigneeId) {
    return e.json(400, { message: "Assignee ID is required." });
  }

  try {
    var result = titleRequestClaims.runClaimMutation(e.app, function (app) {
      var task = app.findRecordById("additional_copy_requests", id);
      if (!routeUtils.sameLibrary(actor, task.get("libraryOrgId"))) {
        throw new Error("Access denied.");
      }

      var assignee = app.findRecordById("staff_users", assigneeId);

      assignmentPolicy.assertActiveStaff(actor, "Actor");
      assignmentPolicy.assertActiveStaff(assignee, "Assignee");
      assignmentPolicy.assertOpenItem(task);
      assignmentPolicy.assertSameLibraryAssignment(actor, assignee, task.get("libraryOrgId"));

      var claimed = additionalCopies.claimTask(app, task, assignee);

      records.recordEvent(
        app,
        claimed,
        "claim_manual_assigned",
        "Claim transferred to " + titleRequestClaims.staffClaimDisplayName(assignee) + " by " + titleRequestClaims.staffClaimDisplayName(actor) + ".",
        {
          actorName: actor.get("username") || titleRequestClaims.staffClaimDisplayName(actor)
        }
      );

      return {
        record: app.findRecordById("additional_copy_requests", claimed.id),
        assigneeId: assignee.id,
        actor: actor,
        type: "additional_copy"
      };
    });

    titleRequestClaims.sendAssignmentNotificationAfterCommit(e.app, result);

    return e.json(200, additionalCopies.toJson(result.record, e.app));
  } catch (err) {
    var status = err && err.statusCode ? err.statusCode : 400;
    e.app.logger().error("Staff assign additional copy action failed", "requestId", id, "error", String(err));
    return e.json(status, { message: err.message || "Assignment failed." });
  }
}



module.exports = {
  staffAdditionalCopiesList,
  safeAdditionalCopyResponseScope,
  safeAnalyticsLibraryOptions,
  staffAdditionalCopyClose,
  staffAdditionalCopyReopen,
  staffAdditionalCopyClaim,
  staffAdditionalCopyUnclaim,
  staffAdditionalCopyAssign: staffAdditionalCopyAssign
};
