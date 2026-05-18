// We use lazy requires inside functions to avoid circular dependency issues
// and ensure globals like __hooks are available when needed.

function clean(value) {
  return String(value === undefined || value === null ? "" : value).trim();
}

function organizationByPolarisId(app, orgId) {
  orgId = clean(orgId);
  if (!orgId) return null;
  try {
    return app.findFirstRecordByData("polaris_organizations", "organizationId", orgId);
  } catch (err) {
    return null;
  }
}

function setRelation(record, fieldName, relatedRecord) {
  record.set(fieldName, relatedRecord && relatedRecord.id ? relatedRecord.id : "");
}

function staffName(staff) {
  return clean(staff && (staff.get("username") || staff.get("displayName") || staff.id));
}

function createFromTitleRequest(app, sourceRequest, staff, payload) {
  payload = payload || {};
  var bibid = clean(payload.bibid || payload.selectedPolarisBibId || sourceRequest.get("bibid"));
  if (!bibid) {
    throw new Error("BIB ID is required before creating an additional-copy request.");
  }

  var task = new Record(app.findCollectionByNameOrId("additional_copy_requests"));
  task.set("sourceTitleRequest", sourceRequest.id);
  setRelation(task, "libraryOrganization", organizationByPolarisId(app, sourceRequest.get("libraryOrgId")));
  task.set("libraryOrgId", clean(sourceRequest.get("libraryOrgId")));
  task.set("libraryOrgName", clean(sourceRequest.get("libraryOrgName")));
  task.set("bibid", bibid);
  task.set("title", clean(payload.selectedPolarisTitle || payload.title || sourceRequest.get("title")));
  task.set("author", clean(payload.selectedPolarisAuthor || payload.author || sourceRequest.get("author")));
  task.set("format", clean(payload.selectedPolarisFormat || payload.format || sourceRequest.get("format")));
  task.set("identifier", clean(payload.selectedPolarisIdentifier || payload.identifier || sourceRequest.get("identifier")));
  task.set("publication", clean(payload.publication || sourceRequest.get("publication")));
  task.set("status", "open");
  task.set("notes", "Created from request " + sourceRequest.id + " by " + (staffName(staff) || "staff") + ".");
  if (staff && staff.id) task.set("createdByStaff", staff.id);
  task.set("createdByUsername", staffName(staff));

  var now = new Date().toISOString();
  task.set("created", now);
  task.set("updated", now);


  // Inherit claimant from source request if exists
  if (sourceRequest.get("claimedByStaffUserId")) {
    task.set("claimedByStaffUserId", sourceRequest.get("claimedByStaffUserId"));
    task.set("claimedByDisplayName", sourceRequest.get("claimedByDisplayName"));
    task.set("claimedAt", sourceRequest.get("claimedAt"));
    // Also set the relation if the ID looks like a valid staff user ID (starts with 'r')
    if (String(sourceRequest.get("claimedByStaffUserId")).startsWith('r')) {
       task.set("claimedByStaff", sourceRequest.get("claimedByStaffUserId"));
    }
  }

  app.save(task);
  return task;
}

function scopeForStaff(staff, selectedOrgId) {
  const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
  var isSuper = routeUtils.isSuperAdmin(staff);
  var staffLibraryOrgId = clean(staff.get("libraryOrgId"));
  var cleanSelected = clean(selectedOrgId);
  if (isSuper && (cleanSelected === "all" || cleanSelected === "system" || !cleanSelected)) {
    return { canList: true, mode: "all", libraryOrgId: "", filter: "id != ''", params: {} };
  }
  var libraryOrgId = isSuper ? cleanSelected : staffLibraryOrgId;
  if (!libraryOrgId) {
    return { canList: false, mode: "library", libraryOrgId: "", filter: "", params: {} };
  }
  return {
    canList: true,
    mode: "library",
    libraryOrgId: libraryOrgId,
    filter: "libraryOrgId = {:libraryOrgId}",
    params: { libraryOrgId: libraryOrgId }
  };
}

function statusFilter(status) {
  status = clean(status || "open").toLowerCase();
  return status === "closed" ? "closed" : "open";
}

function listForStaff(app, staff, options) {
  options = options || {};
  var scope = scopeForStaff(staff, options.scope || options.orgId || "");
  if (!scope.canList) {
    return { items: [], scope: scope };
  }
  var params = {};
  params.status = statusFilter(options.status);
  var filter = "status = {:status}";
  if (scope.mode === "library") {
    params.libraryOrgId = scope.libraryOrgId;
    filter += " && libraryOrgId = {:libraryOrgId}";
  }
  var rows = [];
  var limit = 200;
  var offset = 0;
  while (true) {
    var page;
    try {
      page = app.findRecordsByFilter("additional_copy_requests", filter, "-created", limit, offset, params);
    } catch (err) {
      try {
        app.logger().warn("Additional-copy list failed", "filter", filter, "error", String(err));
      } catch (logErr) {}
      return { items: [], scope: scope };
    }
    if (!page || !page.length) break;
    rows = rows.concat(page);
    if (page.length < limit) break;
    offset += limit;
  }
  return {
    items: rows.map(function (row) { return toJson(row, app); }),
    scope: scope
  };
}

function countOpenForLibraryBib(app, libraryOrgId, bibid) {
  libraryOrgId = clean(libraryOrgId);
  bibid = clean(bibid);
  if (!libraryOrgId || !bibid) {
    return 0;
  }
  var count = 0;
  var limit = 200;
  var offset = 0;
  var params = { libraryOrgId: libraryOrgId, bibid: bibid, status: "open" };
  var filter = "status = {:status} && libraryOrgId = {:libraryOrgId} && bibid = {:bibid}";
  while (true) {
    var page = app.findRecordsByFilter("additional_copy_requests", filter, "-created", limit, offset, params);
    if (!page || !page.length) break;
    count += page.length;
    if (page.length < limit) break;
    offset += limit;
  }
  return count;
}

function closeTask(app, task, staff) {
  if (clean(task.get("status")) === "closed") {
    return task;
  }
  task.set("status", "closed");
  if (staff && staff.id) task.set("closedByStaff", staff.id);
  task.set("closedByUsername", staffName(staff));
  task.set("closedAt", new Date().toISOString());
  task.set("updated", new Date().toISOString());
  app.save(task);
  return task;
}

function reopenTask(app, task, staff) {
  if (clean(task.get("status")) === "open") {
    return task;
  }
  task.set("status", "open");
  task.set("closedByStaff", "");
  task.set("closedByUsername", "");
  task.set("closedAt", "");
  task.set("updated", new Date().toISOString());
  app.save(task);
  return task;
}

function claimTask(app, task, staff) {
  task.set("claimedByStaff", staff.id);
  task.set("claimedByStaffUserId", staff.id);
  task.set("claimedByDisplayName", staff.get("displayName") || staff.get("username") || "Staff");
  task.set("claimedAt", new DateTime());
  task.set("updated", new Date().toISOString());
  app.save(task);
  return task;
}

function unclaimTask(app, task) {
  task.set("claimedByStaff", "");
  task.set("claimedByStaffUserId", "");
  task.set("claimedByDisplayName", "");
  task.set("claimedAt", "");
  task.set("updated", new Date().toISOString());
  app.save(task);
  return task;
}

function toJson(record, app) {
  const records = require(`${__hooks}/../lib/records.js`);
  var source = clean(record.get("sourceTitleRequest"));
  var sourceStatus = "";
  try {
    if (source && app) {
      sourceStatus = records.normalizeStatus(app.findRecordById("title_requests", source).get("status"));
    }
  } catch (err) {}
  var title = record.get("title") || "";
  var author = record.get("author") || "";
  return {
    id: record.id,
    type: "additional_copy",
    sourceTitleRequest: source,
    sourceStatus: sourceStatus,
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    bibid: record.get("bibid") || "",
    title: title,
    author: author,
    polarisSearchTitle: records.polarisSubmittedSearchValue(title),
    polarisSearchAuthor: records.polarisSubmittedSearchValue(author),
    format: record.get("format") || "",
    identifier: record.get("identifier") || "",
    publication: record.get("publication") || "",
    status: record.get("status") || "",
    notes: record.get("notes") || "",
    createdByUsername: record.get("createdByUsername") || "",
    closedByUsername: record.get("closedByUsername") || "",
    closedAt: record.get("closedAt") || "",
    created: record.get("created") || record.created || "",
    updated: record.get("updated") || record.updated || "",
    claimedByStaffUserId: record.get("claimedByStaffUserId") || "",
    claimedByDisplayName: record.get("claimedByDisplayName") || "",
    claimedAt: record.get("claimedAt") || ""
  };
}

module.exports = {
  createFromTitleRequest: createFromTitleRequest,
  countOpenForLibraryBib: countOpenForLibraryBib,
  listForStaff: listForStaff,
  closeTask: closeTask,
  reopenTask: reopenTask,
  claimTask: claimTask,
  unclaimTask: unclaimTask,
  toJson: toJson,
  scopeForStaff: scopeForStaff
};
