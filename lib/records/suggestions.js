const config = require("../config.js");
const helpers = require("./helpers.js");
const tags = require("./tags.js");
const duplicates = require("./duplicates.js");

function createSuggestion(app, patronRecord, data, options) {
  options = options || {};
  let barcode = String(patronRecord.get("barcode") || "").trim();
  if (barcode.length > 50) {
    throw new Error("Invalid patron barcode length.");
  }

  if (!options.skipLimits) {
    duplicates.enforceWeeklyLimit(app, barcode, patronRecord.get("libraryOrgId"));
  }
  duplicates.enforceDuplicate(app, barcode, data);

  let now = new Date().toISOString();
  let record = new Record(app.findCollectionByNameOrId("title_requests"));
  record.set("patron", patronRecord.id);
  record.set("barcode", barcode);
  
  let patronEmail = options.email || (typeof patronRecord.get === "function" ? patronRecord.get("notificationEmail") : "") || patronRecord.email();
  record.set("email", patronEmail);
  record.set("nameFirst", patronRecord.get("nameFirst"));
  record.set("nameLast", patronRecord.get("nameLast"));
  record.set("patronOrgId", patronRecord.get("patronOrgId") || "");
  record.set("libraryOrgId", patronRecord.get("libraryOrgId") || "");
  record.set("libraryOrgName", patronRecord.get("libraryOrgName") || "");
  record.set("preferredPickupBranchId", patronRecord.get("preferredPickupBranchId") || "");
  record.set("preferredPickupBranchName", patronRecord.get("preferredPickupBranchName") || "");
  record.set("staffLibraryOrgIdCreatedBy", String(data.staffLibraryOrgIdCreatedBy || ""));
  
  record.set("title", helpers.titleCase(String(data.title || "").slice(0, 500)));
  record.set("author", String(data.author || "").trim().slice(0, 500));
  
  let identifier = String(data.identifier || data.isbn || "").trim().slice(0, 100);
  record.set("identifier", identifier);
  record.set("publication", String(data.publication || "").trim().slice(0, 200));
  record.set("exactPublicationDate", helpers.normalizeDateOnly(data.exactPublicationDate));
  
  let wf = config.suggestionLimit(app, patronRecord.get("libraryOrgId"));
  let autohold = true;
  if (data.autohold !== undefined) {
    if (data.staffLibraryOrgIdCreatedBy || wf.allowPatronAutoholdOptOut) {
      autohold = !!data.autohold;
    }
  }
  record.set("autohold", autohold);
  record.set("status", helpers.STATUS.SUGGESTION);
  record.set("isbnCheckStatus", String(data.isbnCheckStatus || "skipped_no_isbn"));
  record.set("lastChecked", String(data.lastChecked || ""));
  record.set("format", helpers.normalizeFormat(data.format));
  record.set("editedBy", "system");
  
  record.set("notes", String(data.notes || "").trim().slice(0, 2000));
  
  record.set("bibid", "");
  record.set("closeReason", "");
  record.set("created", now);
  record.set("updated", now);
  helpers.setCanonicalRefs(app, record);
  app.save(record);
  duplicates.flagCrossPatronDuplicateSuggestion(app, record);
  helpers.recordEvent(app, record, "created", "Suggestion submitted.", { actorType: data.staffLibraryOrgIdCreatedBy ? "staff" : "patron" });
  return record;
}

function titleRequestToJson(record, app, options) {
  options = options || {};
  let title = record.get("title") || "";
  let author = record.get("author") || "";
  return {
    id: record.id,
    type: "title_request",
    barcode: record.get("barcode") || "",
    email: record.get("email") || "",
    nameFirst: record.get("nameFirst") || "",
    nameLast: record.get("nameLast") || "",
    title: title,
    author: author,
    polarisSearchTitle: helpers.polarisSubmittedSearchValue(title),
    polarisSearchAuthor: helpers.polarisSubmittedSearchValue(author),
    identifier: record.get("identifier") || "",
    publication: record.get("publication") || "",
    exactPublicationDate: record.get("exactPublicationDate") || "",
    autohold: !!record.getBool("autohold"),
    status: record.get("status") || "",
    isbnCheckStatus: record.get("isbnCheckStatus") || "",
    lastChecked: record.get("lastChecked") || "",
    format: record.get("format") || "",
    editedBy: record.get("editedBy") || "",
    notes: options.redactNotes ? "" : (record.get("notes") || ""),
    workflowTags: options.workflowTags !== undefined ? options.workflowTags : (app ? tags.workflowTagsForRequest(app, record) : []),
    bibid: record.get("bibid") || "",
    legacyId: record.get("legacyId") || "",
    closeReason: record.get("closeReason") || "",
    claimedByStaffUserId: record.get("claimedByStaffUserId") || "",
    claimedByDisplayName: record.get("claimedByDisplayName") || "",
    claimedAt: record.get("claimedAt") || "",
    claimType: record.get("claimType") || "",
    claimRuleId: record.get("claimRuleId") || "",
    lastPromoterCheck: record.get("lastPromoterCheck") || "",
    patronOrgId: record.get("patronOrgId") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    preferredPickupBranchId: record.get("preferredPickupBranchId") || "",
    preferredPickupBranchName: record.get("preferredPickupBranchName") || "",
    staffLibraryOrgIdCreatedBy: record.get("staffLibraryOrgIdCreatedBy") || "",
    created: record.get("created") || record.created || "",
    updated: record.get("updated") || record.updated || "",
  };
}

function updateTitleRequest(app, id, data, editedBy) {
  let record = app.findRecordById("title_requests", id);
  let fields = ["title", "author", "identifier", "publication", "notes", "bibid", "exactPublicationDate", "autohold"];
  for (let i = 0; i < fields.length; i++) {
    if (data[fields[i]] !== undefined && data[fields[i]] !== null) {
      if (fields[i] === "exactPublicationDate") {
        record.set(fields[i], helpers.normalizeDateOnly(data[fields[i]]));
      } else if (typeof data[fields[i]] === "boolean") {
        record.set(fields[i], data[fields[i]]);
      } else {
        record.set(fields[i], String(data[fields[i]]));
      }
    }
  }
  if (data.status !== undefined) {
    let oldStatus = record.get("status");
    let nextStatus = helpers.normalizeStatus(data.status);
    if (nextStatus !== oldStatus) {
      record.set("status", nextStatus);
      if (nextStatus !== helpers.STATUS.CLOSED) {
        record.set("closeReason", "");
      }
      helpers.appendSystemNote(record, "Moved from " + helpers.getStatusLabel(oldStatus) + " to " + helpers.getStatusLabel(nextStatus) + " by " + (editedBy || "system"));
      helpers.recordEvent(app, record, "status_changed", "Moved from " + helpers.getStatusLabel(oldStatus) + " to " + helpers.getStatusLabel(nextStatus) + ".", { fromStatus: oldStatus, toStatus: nextStatus, actorName: editedBy || "system" });
    }
  }
  if (data.closeReason !== undefined) {
    record.set("closeReason", helpers.normalizeCloseReason(data.closeReason));
  }
  if (data.format !== undefined) {
    record.set("format", helpers.normalizeFormat(data.format));
  }
  record.set("editedBy", editedBy || "system");
  record.set("updated", new Date().toISOString());
  helpers.setCanonicalRefs(app, record);
  app.save(record);
  return record;
}

function setStatusWithNote(app, record, status, note, editedBy) {
  let oldStatus = record.get("status");
  record.set("status", status);
  record.set("editedBy", editedBy || "system");
  record.set("updated", new Date().toISOString());
  if (note) {
    helpers.appendSystemNote(record, note);
  }
  helpers.setCanonicalRefs(app, record);
  app.save(record);
  helpers.recordEvent(app, record, "status_changed", note || "Status changed.", { fromStatus: oldStatus, toStatus: status, actorName: editedBy || "system" });
  return record;
}

function auditDeletedRequest(app, record, staffUser, mode) {
  let audit = new Record(app.findCollectionByNameOrId("deleted_request_audit"));
  let snapshot = {
    id: record.id,
    title: record.get("title") || "",
    author: record.get("author") || "",
    identifier: record.get("identifier") || "",
    bibid: record.get("bibid") || "",
    barcode: record.get("barcode") || "",
    email: record.get("email") || "",
    nameFirst: record.get("nameFirst") || "",
    nameLast: record.get("nameLast") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    status: record.get("status") || "",
    closeReason: record.get("closeReason") || "",
    notes: record.get("notes") || "",
    created: record.get("created") || record.created || "",
    updated: record.get("updated") || record.updated || ""
  };

  audit.set("titleRequestId", record.id);
  audit.set("title", snapshot.title);
  audit.set("author", snapshot.author);
  audit.set("identifier", snapshot.identifier);
  audit.set("bibid", snapshot.bibid);
  audit.set("barcode", snapshot.barcode);
  audit.set("libraryOrgId", snapshot.libraryOrgId);
  audit.set("libraryOrgName", snapshot.libraryOrgName);
  audit.set("status", snapshot.status);
  audit.set("closeReason", snapshot.closeReason);
  audit.set("deletedByStaff", staffUser.id);
  audit.set("deletedByUsername", staffUser.get("username") || "");
  audit.set("deletedByRole", staffUser.get("role") || "");
  audit.set("deletedAt", new Date().toISOString());
  audit.set("deleteMode", mode || "single");
  audit.set("snapshot", snapshot);
  app.save(audit);
}

function deleteRelatedRows(app, collectionName, fieldName, value) {
  let params = { value: value };
  while (true) {
    let rows = app.findRecordsByFilter(collectionName, fieldName + " = {:value}", "", 200, 0, params);
    if (!rows.length) {
      break;
    }
    rows.forEach(function (row) {
      app.delete(row);
    });
  }
}

function deleteTitleRequestWithAudit(app, record, staffUser, mode) {
  auditDeletedRequest(app, record, staffUser, mode || "single");
  deleteRelatedRows(app, "title_request_tags", "titleRequest", record.id);
  deleteRelatedRows(app, "title_request_events", "titleRequest", record.id);
  deleteRelatedRows(app, "email_delivery_events", "titleRequest", record.id);
  app.delete(record);
}

function deleteAdditionalCopyRequestWithAudit(app, record, staffUser, mode) {
  auditDeletedRequest(app, record, staffUser, mode || "single");
  app.delete(record);
}

function deleteClosedRequestsBulk(app, staffUser, confirm) {
  if (confirm !== "DELETE") {
    throw new Error("Type DELETE to confirm bulk deletion.");
  }

  let role = String(staffUser.get("role") || "").toLowerCase();
  let staffLibrary = String(staffUser.get("libraryOrgId") || "").trim();
  if (role !== "admin" && role !== "super_admin") {
    throw new Error("Admin access required.");
  }

  let filter = "status = 'closed'";
  let params = {};
  if (role === "admin") {
    if (!staffLibrary) {
      return 0;
    }
    filter += " && libraryOrgId = {:libraryOrgId}";
    params.libraryOrgId = staffLibrary;
  }

  let suggestionsToDelete = [];
  let offset = 0;
  let limit = 200;
  while (true) {
    let page = app.findRecordsByFilter("title_requests", filter, "created", limit, offset, params);
    if (!page.length) break;
    suggestionsToDelete = suggestionsToDelete.concat(page);
    if (page.length < limit) break;
    offset += limit;
  }

  let copiesToDelete = [];
  offset = 0;
  while (true) {
    let page = app.findRecordsByFilter("additional_copy_requests", filter, "created", limit, offset, params);
    if (!page.length) break;
    copiesToDelete = copiesToDelete.concat(page);
    if (page.length < limit) break;
    offset += limit;
  }

  suggestionsToDelete.forEach(function (record) {
    deleteTitleRequestWithAudit(app, record, staffUser, "bulk");
  });
  
  copiesToDelete.forEach(function (record) {
    deleteAdditionalCopyRequestWithAudit(app, record, staffUser, "bulk");
  });

  return suggestionsToDelete.length + copiesToDelete.length;
}

module.exports = {
  createSuggestion: createSuggestion,
  titleRequestToJson: titleRequestToJson,
  updateTitleRequest: updateTitleRequest,
  setStatusWithNote: setStatusWithNote,
  auditDeletedRequest: auditDeletedRequest,
  deleteRelatedRows: deleteRelatedRows,
  deleteTitleRequestWithAudit: deleteTitleRequestWithAudit,
  deleteAdditionalCopyRequestWithAudit: deleteAdditionalCopyRequestWithAudit,
  deleteClosedRequestsBulk: deleteClosedRequestsBulk,
};
