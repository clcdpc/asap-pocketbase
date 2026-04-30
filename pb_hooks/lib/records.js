const config = require(`${__hooks}/lib/config.js`);
const identity = require(`${__hooks}/lib/identity.js`);

const STATUS = {
  SUGGESTION: "suggestion",
  PENDING_HOLD: "pending_hold",
  HOLD_PLACED: "hold_placed",
  OUTSTANDING_PURCHASE: "outstanding_purchase",
  CLOSED: "closed",
};

const FORMAT = {
  "0": "book",
  "1": "ebook",
  "2": "audiobook_cd",
  "3": "eaudiobook",
  "4": "dvd",
  "5": "music_cd",
  book: "book",
  ebook: "ebook",
  audiobook_cd: "audiobook_cd",
  eaudiobook: "eaudiobook",
  dvd: "dvd",
  music_cd: "music_cd",
};

const AGEGROUP = {
  "0": "adult",
  "1": "teen",
  "2": "children",
  adult: "adult",
  teen: "teen",
  children: "children",
};

const CLOSE_REASON = {
  REJECTED: "rejected",
  SILENT: "Silently Closed",
  HOLD_COMPLETED: "hold_completed",
  HOLD_NOT_PICKED_UP: "hold_not_picked_up",
  MANUAL: "manual",
};

function normalizeStatus(value) {
  var map = {
    "0": STATUS.SUGGESTION,
    "1": STATUS.PENDING_HOLD,
    "2": STATUS.HOLD_PLACED,
    "3": STATUS.OUTSTANDING_PURCHASE,
    "4": STATUS.CLOSED,
    "5": STATUS.PENDING_HOLD,
    suggestion: STATUS.SUGGESTION,
    pending_hold: STATUS.PENDING_HOLD,
    hold_placed: STATUS.HOLD_PLACED,
    outstanding_purchase: STATUS.OUTSTANDING_PURCHASE,
    closed: STATUS.CLOSED,
    pendingHold: STATUS.PENDING_HOLD,
    holdPlaced: STATUS.HOLD_PLACED,
    outstandingPurchase: STATUS.OUTSTANDING_PURCHASE,
  };
  return map[String(value)] || STATUS.SUGGESTION;
}

function getStatusLabel(status) {
  var map = {};
  map[STATUS.SUGGESTION] = "Suggestions";
  map[STATUS.PENDING_HOLD] = "Pending Hold";
  map[STATUS.HOLD_PLACED] = "Hold Placed";
  map[STATUS.OUTSTANDING_PURCHASE] = "Pending Purchase";
  map[STATUS.CLOSED] = "Closed";
  return map[status] || status;
}

function normalizeCloseReason(value) {
  var map = {
    rejected: CLOSE_REASON.REJECTED,
    reject: CLOSE_REASON.REJECTED,
    hold_completed: CLOSE_REASON.HOLD_COMPLETED,
    holdCompleted: CLOSE_REASON.HOLD_COMPLETED,
    hold_placed: CLOSE_REASON.HOLD_COMPLETED,
    checkout: CLOSE_REASON.HOLD_COMPLETED,
    checked_out: CLOSE_REASON.HOLD_COMPLETED,
    manual: CLOSE_REASON.MANUAL,
    silent: CLOSE_REASON.SILENT,
    "Silently Closed": CLOSE_REASON.SILENT,
  };
  return map[String(value || "").trim()] || "";
}

function normalizeFormat(value) {
  return FORMAT[String(value)] || "book";
}

function normalizeAgegroup(value) {
  return AGEGROUP[String(value)] || "adult";
}

function findFirstByData(app, collection, field, value) {
  try {
    return app.findFirstRecordByData(collection, field, value);
  } catch (err) {
    return null;
  }
}

function lookupByCode(app, collection, code) {
  code = String(code || "").trim();
  if (!code) return null;
  try {
    return app.findFirstRecordByData(collection, "code", code);
  } catch (err) {
    return null;
  }
}

function organizationByPolarisId(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId) return null;
  try {
    return app.findFirstRecordByData("polaris_organizations", "organizationId", orgId);
  } catch (err) {
    return null;
  }
}

function setRelation(record, fieldName, relatedRecord) {
  record.set(fieldName, relatedRecord ? relatedRecord.id : "");
}

function setCanonicalRefs(app, record) {
  setRelation(record, "statusRef", lookupByCode(app, "request_statuses", normalizeStatus(record.get("status"))));
  setRelation(record, "formatRef", lookupByCode(app, "material_formats", normalizeFormat(record.get("format"))));
  var age = String(record.get("agegroup") || "").trim();
  setRelation(record, "audienceGroup", age ? lookupByCode(app, "audience_groups", normalizeAgegroup(age)) : null);
  var reason = normalizeCloseReason(record.get("closeReason"));
  setRelation(record, "closeReasonRef", reason ? lookupByCode(app, "request_close_reasons", reason) : null);
  setRelation(record, "patronOrganization", organizationByPolarisId(app, record.get("patronOrgId")));
  setRelation(record, "libraryOrganization", organizationByPolarisId(app, record.get("libraryOrgId")));
  setRelation(record, "staffLibraryOrganizationCreatedBy", organizationByPolarisId(app, record.get("staffLibraryOrgIdCreatedBy")));
}

function recordEvent(app, record, type, message, options) {
  options = options || {};
  try {
    var collection = app.findCollectionByNameOrId("title_request_events");
    var event = new Record(collection);
    event.set("titleRequest", record.id);
    event.set("eventType", type || "system_note");
    event.set("actorType", options.actorType || (options.actorName ? "staff" : "system"));
    event.set("actorName", options.actorName || options.editedBy || "system");
    event.set("message", String(message || ""));
    event.set("metadata", options.metadata || {});
    if (options.fromStatus) setRelation(event, "fromStatus", lookupByCode(app, "request_statuses", normalizeStatus(options.fromStatus)));
    if (options.toStatus) setRelation(event, "toStatus", lookupByCode(app, "request_statuses", normalizeStatus(options.toStatus)));
    if (options.closeReason) setRelation(event, "closeReason", lookupByCode(app, "request_close_reasons", normalizeCloseReason(options.closeReason)));
    app.save(event);
  } catch (err) {
    try { app.logger().warn("Failed to record title request event", "recordId", record && record.id, "error", String(err)); } catch (logErr) {}
  }
}

function hasStaffUsers(app) {
  try {
    return app.countRecords("staff_users") > 0;
  } catch (err) {
    try {
      var records = app.findRecordsByFilter("staff_users", "id != ''", "", 1, 0);
      return records.length > 0;
    } catch (innerErr) {
      app.logger().error("Staff user count failed", "error", String(innerErr));
      return false;
    }
  }
}

function listStaffUsers(app) {
  var users = [];
  var limit = 100;
  var offset = 0;
  while (true) {
    var page = app.findRecordsByFilter("staff_users", "id != ''", "username", limit, offset);
    if (!page.length) {
      break;
    }
    users = users.concat(page);
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }
  return users;
}

function countAdminUsers(app) {
  var admins = app.findRecordsByFilter("staff_users", "role = 'admin'", "", 2, 0);
  return admins.length;
}

function countSuperAdminUsers(app) {
  var admins = app.findRecordsByFilter("staff_users", "role = 'super_admin'", "", 2, 0);
  return admins.length;
}

function findStaffByIdentity(app, identityKey) {
  return findFirstByData(app, "staff_users", "identityKey", identityKey);
}

function upsertStaffUser(app, staffIdentity, displayName, options) {
  options = options || {};
  if (typeof staffIdentity === "string") {
    staffIdentity = identity.parseStaffIdentity(staffIdentity, options.defaultDomain || "");
  }
  var username = identity.normalizeUsername(staffIdentity.username || "");
  var domain = identity.normalizeDomain(staffIdentity.domain || "");
  var identityKey = staffIdentity.identityKey || identity.buildIdentityKey(domain, username);
  var record = findStaffByIdentity(app, identityKey);
  var existingRole = "";
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("staff_users"));
    record.setEmail(identityKey.replace(/[^a-z0-9._-]+/g, ".") + "@staff.asap.local");
    record.setRandomPassword();
  } else {
    existingRole = String(record.get("role") || "").trim();
  }
  record.set("username", username);
  record.set("domain", domain);
  record.set("identityKey", identityKey);
  record.set("displayName", displayName || username);
  record.set("role", existingRole || options.defaultRole || "staff");
  record.set("active", true);
  if (options.polarisUserId !== undefined) {
    record.set("polarisUserId", String(options.polarisUserId || ""));
  }
  if (options.branchOrgId !== undefined) {
    record.set("branchOrgId", String(options.branchOrgId || ""));
    setRelation(record, "branchOrganization", organizationByPolarisId(app, options.branchOrgId));
  }
  if (options.libraryOrgId !== undefined) {
    record.set("libraryOrgId", String(options.libraryOrgId || ""));
    setRelation(record, "libraryOrganization", organizationByPolarisId(app, options.libraryOrgId));
  }
  if (options.libraryOrgName !== undefined) {
    record.set("libraryOrgName", String(options.libraryOrgName || ""));
  }
  if (options.scope !== undefined) {
    record.set("scope", String(options.scope || "library"));
  } else if (!record.get("scope")) {
    record.set("scope", options.defaultRole === "super_admin" ? "system" : "library");
  }
  if (options.lastOrgSync !== false) {
    record.set("lastOrgSync", new Date().toISOString());
  }
  record.set("lastPolarisLogin", new Date().toISOString());
  record.setVerified(true);
  app.save(record);
  return record;
}

function upsertPatronUser(app, patron) {
  var barcode = String(patron.Barcode || patron.barcode || "").trim();
  var record = findFirstByData(app, "patron_users", "barcode", barcode);
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("patron_users"));
    record.setEmail(safeEmail(patron.EmailAddress) || barcode + "@patron.asap.local");
    record.setRandomPassword();
  }
  record.set("barcode", barcode);
  record.set("nameFirst", String(patron.NameFirst || ""));
  record.set("nameLast", String(patron.NameLast || ""));
  record.set("patronOrgId", String(patron.PatronOrgID || patron.patronOrgId || ""));
  record.set("libraryOrgId", String(patron.LibraryOrgID || patron.libraryOrgId || ""));
  record.set("libraryOrgName", String(patron.LibraryOrgName || patron.libraryOrgName || ""));
  setRelation(record, "patronOrganization", organizationByPolarisId(app, patron.PatronOrgID || patron.patronOrgId));
  setRelation(record, "libraryOrganization", organizationByPolarisId(app, patron.LibraryOrgID || patron.libraryOrgId));
  if (safeEmail(patron.EmailAddress)) {
    record.setEmail(safeEmail(patron.EmailAddress));
  }
  record.set("lastOrgSync", new Date().toISOString());
  record.set("lastPolarisLogin", new Date().toISOString());
  record.setVerified(true);
  app.save(record);
  return record;
}

function safeEmail(value) {
  value = String(value || "").trim();
  return value.indexOf("@") > 0 ? value : "";
}

function createSuggestion(app, patronRecord, data) {
  var barcode = patronRecord.get("barcode");
  enforceWeeklyLimit(app, barcode, patronRecord.get("libraryOrgId"));
  enforceDuplicate(app, barcode, data);

  var now = new Date().toISOString();
  var record = new Record(app.findCollectionByNameOrId("title_requests"));
  record.set("patron", patronRecord.id);
  record.set("barcode", barcode);
  record.set("email", patronRecord.email());
  record.set("nameFirst", patronRecord.get("nameFirst"));
  record.set("nameLast", patronRecord.get("nameLast"));
  record.set("patronOrgId", patronRecord.get("patronOrgId") || "");
  record.set("libraryOrgId", patronRecord.get("libraryOrgId") || "");
  record.set("libraryOrgName", patronRecord.get("libraryOrgName") || "");
  record.set("staffLibraryOrgIdCreatedBy", String(data.staffLibraryOrgIdCreatedBy || ""));
  record.set("title", titleCase(data.title));
  record.set("author", String(data.author || "").trim());
  var identifier = String(data.identifier || data.isbn || "").trim();
  record.set("identifier", identifier);
  record.set("publication", String(data.publication || "").trim());
  record.set("exactPublicationDate", normalizeDateOnly(data.exactPublicationDate));
  record.set("autohold", true);
  record.set("status", STATUS.SUGGESTION);
  record.set("isbnCheckStatus", String(data.isbnCheckStatus || "skipped_no_isbn"));
  record.set("lastChecked", String(data.lastChecked || ""));
  record.set("agegroup", String(data.agegroup || "").trim() ? normalizeAgegroup(data.agegroup) : "");
  record.set("format", normalizeFormat(data.format));
  record.set("editedBy", "system");
  record.set("notes", String(data.notes || ""));
  record.set("bibid", "");
  record.set("closeReason", "");
  record.set("created", now);
  record.set("updated", now);
  setCanonicalRefs(app, record);
  app.save(record);
  recordEvent(app, record, "created", "Suggestion submitted.", { actorType: data.staffLibraryOrgIdCreatedBy ? "staff" : "patron" });
  return record;
}

function titleRequestToJson(record, app) {
  return {
    id: record.id,
    barcode: record.get("barcode") || "",
    email: record.get("email") || "",
    nameFirst: record.get("nameFirst") || "",
    nameLast: record.get("nameLast") || "",
    title: record.get("title") || "",
    author: record.get("author") || "",
    identifier: record.get("identifier") || "",
    publication: record.get("publication") || "",
    exactPublicationDate: record.get("exactPublicationDate") || "",
    autohold: !!record.getBool("autohold"),
    status: record.get("status") || "",
    isbnCheckStatus: record.get("isbnCheckStatus") || "",
    lastChecked: record.get("lastChecked") || "",
    agegroup: record.get("agegroup") || "",
    format: record.get("format") || "",
    editedBy: record.get("editedBy") || "",
    notes: record.get("notes") || "",
    workflowTags: app ? workflowTagsForRequest(app, record) : [],
    bibid: record.get("bibid") || "",
    legacyId: record.get("legacyId") || "",
    closeReason: record.get("closeReason") || "",
    lastPromoterCheck: record.get("lastPromoterCheck") || "",
    patronOrgId: record.get("patronOrgId") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    staffLibraryOrgIdCreatedBy: record.get("staffLibraryOrgIdCreatedBy") || "",
    created: record.get("created") || record.created || "",
    updated: record.get("updated") || record.updated || "",
  };
}

function normalizeWorkflowTags(tags) {
  if (!Array.isArray(tags)) {
    return [];
  }
  var seen = {};
  var normalized = [];
  for (var i = 0; i < tags.length; i++) {
    var tag = String(tags[i] || "").trim();
    if (!tag || seen[tag]) {
      continue;
    }
    seen[tag] = true;
    normalized.push(tag);
  }
  return normalized;
}

function addWorkflowTagForRequest(app, record, tag) {
  var cleanTag = String(tag || "").trim();
  if (!cleanTag) return false;
  var tagRecord = lookupByCode(app, "workflow_tags", cleanTag);
  if (!tagRecord) {
    tagRecord = new Record(app.findCollectionByNameOrId("workflow_tags"));
    tagRecord.set("code", cleanTag);
    tagRecord.set("label", cleanTag);
    app.save(tagRecord);
  }
  try {
    app.findFirstRecordByFilter("title_request_tags", "titleRequest = {:request} && tag = {:tag}", { request: record.id, tag: tagRecord.id });
    return false;
  } catch (err) {
    var join = new Record(app.findCollectionByNameOrId("title_request_tags"));
    join.set("titleRequest", record.id);
    join.set("tag", tagRecord.id);
    app.save(join);
    return true;
  }
}

function workflowTagsForRequest(app, record) {
  var tags = [];
  try {
    var rows = app.findRecordsByFilter("title_request_tags", "titleRequest = {:request}", "", 100, 0, { request: record.id });
    for (var i = 0; i < rows.length; i++) {
      try {
        var tag = app.findRecordById("workflow_tags", rows[i].get("tag"));
        tags.push(tag.get("code") || tag.get("label") || "");
      } catch (err) {}
    }
  } catch (err) {}
  return normalizeWorkflowTags(tags);
}

function updateTitleRequest(app, id, data, editedBy) {
  var record = app.findRecordById("title_requests", id);
  var fields = ["title", "author", "identifier", "publication", "notes", "bibid", "exactPublicationDate"];
  for (var i = 0; i < fields.length; i++) {
    if (data[fields[i]] !== undefined && data[fields[i]] !== null) {
      if (fields[i] === "exactPublicationDate") {
        record.set(fields[i], normalizeDateOnly(data[fields[i]]));
      } else {
        record.set(fields[i], String(data[fields[i]]));
      }
    }
  }
  if (data.status !== undefined) {
    var oldStatus = record.get("status");
    var nextStatus = normalizeStatus(data.status);
    if (nextStatus !== oldStatus) {
      record.set("status", nextStatus);
      if (nextStatus !== STATUS.CLOSED) {
        record.set("closeReason", "");
      }
      appendSystemNote(record, "Moved from " + getStatusLabel(oldStatus) + " to " + getStatusLabel(nextStatus) + " by " + (editedBy || "system"));
      recordEvent(app, record, "status_changed", "Moved from " + getStatusLabel(oldStatus) + " to " + getStatusLabel(nextStatus) + ".", { fromStatus: oldStatus, toStatus: nextStatus, actorName: editedBy || "system" });
    }
  }
  if (data.closeReason !== undefined) {
    record.set("closeReason", normalizeCloseReason(data.closeReason));
  }
  if (data.format !== undefined) {
    record.set("format", normalizeFormat(data.format));
  }
  if (data.agegroup !== undefined) {
    record.set("agegroup", normalizeAgegroup(data.agegroup));
  }
  record.set("editedBy", editedBy || "system");
  record.set("updated", new Date().toISOString());
  setCanonicalRefs(app, record);
  app.save(record);
  return record;
}

function formatDate(d) {
  return (d.getMonth() + 1) + "/" + d.getDate() + "/" + d.getFullYear();
}

function appendSystemNote(record, note) {
  var today = formatDate(new Date());
  var notesStr = String(record.get("notes") || "").trim();
  var lines = notesStr ? notesStr.split("\n") : [];
  var firstLine = lines.length > 0 ? lines[0] : "";
  
  var cleanNote = note.trim();
  if (!cleanNote.endsWith(".") && !cleanNote.endsWith("!") && !cleanNote.endsWith("?")) {
    cleanNote += ".";
  }

  // Regex to match our range format: [Date] to [Date] (Count: [X]) [Message]
  var rangeRegex = /^(\d{1,2}\/\d{1,2}\/\d{4}) to (\d{1,2}\/\d{1,2}\/\d{4}) \(Count: (\d+)\) (.*)$/;
  var match = firstLine.match(rangeRegex);
  
  if (match) {
    var startDate = match[1];
    var count = parseInt(match[3], 10);
    var existingMsg = match[4].replace(/^\*\*\*ALERT\*\*\* /, "");
    
    if (existingMsg === cleanNote) {
      count++;
      var alertPrefix = count >= 50 ? "***ALERT*** " : "";
      lines[0] = startDate + " to " + today + " (Count: " + count + ") " + alertPrefix + cleanNote;
      record.set("notes", lines.join("\n"));
      return;
    }
  } else {
    // Check for old format match: [Date] [Message]
    var oldRegex = /^(\d{1,2}\/\d{1,2}\/\d{4}) (.*)$/;
    var oldMatch = firstLine.match(oldRegex);
    if (oldMatch) {
      var existingMsg = oldMatch[2].replace(/^\*\*\*ALERT\*\*\* /, "");
      if (existingMsg === cleanNote) {
        var count = 2;
        var alertPrefix = count >= 50 ? "***ALERT*** " : "";
        lines[0] = oldMatch[1] + " to " + today + " (Count: " + count + ") " + alertPrefix + cleanNote;
        record.set("notes", lines.join("\n"));
        return;
      }
    }
  }
  
  // No match or different message, prepend new entry in range format
  var newEntry = today + " to " + today + " (Count: 1) " + cleanNote;
  if (notesStr) {
    record.set("notes", newEntry + "\n" + notesStr);
  } else {
    record.set("notes", newEntry);
  }
}

function setStatusWithNote(app, record, status, note, editedBy) {
  var oldStatus = record.get("status");
  record.set("status", status);
  record.set("editedBy", editedBy || "system");
  record.set("updated", new Date().toISOString());
  if (note) {
    appendSystemNote(record, note);
  }
  setCanonicalRefs(app, record);
  app.save(record);
  recordEvent(app, record, "status_changed", note || "Status changed.", { fromStatus: oldStatus, toStatus: status, actorName: editedBy || "system" });
  return record;
}

function enforceWeeklyLimit(app, barcode, libraryOrgId) {
  var since = new Date();
  since.setDate(since.getDate() - 7);
  var cfg = config.suggestionLimit(app, libraryOrgId);
  var limit = cfg.suggestionLimit || cfg.limit || 5;
  var recent = app.findRecordsByFilter(
    "title_requests",
    "barcode = {:barcode} && created >= {:since}",
    "-created",
    limit,
    0,
    { barcode: barcode, since: since.toISOString() }
  );

  if (recent.length >= limit) {
    // The next slot opens exactly 7 days after the oldest of the 'limit' records was created.
    var oldest = recent[recent.length - 1];
    var createdStr = String(oldest.get("created") || oldest.created || "");
    var oldestDate = new Date(createdStr.replace(" ", "T")); 
    var nextAvailable = new Date(oldestDate.getTime() + (7 * 24 * 60 * 60 * 1000));
    
    var msg = cfg.suggestionLimitMessage || cfg.message || "Weekly suggestion limit reached. You can try again after {{next_available_date}}.";
    var dateStr = nextAvailable.toLocaleDateString("en-US", { 
      weekday: 'long', 
      year: 'numeric', 
      month: 'long', 
      day: 'numeric', 
      hour: '2-digit', 
      minute: '2-digit' 
    });
    msg = msg.replace("{{next_available_date}}", dateStr);

    var err = new Error(msg);
    err.code = 406;
    throw err;
  }
}

function enforceDuplicate(app, barcode, data) {
  var title = titleCase(data.title);
  var identifier = String(data.identifier || data.isbn || "").trim();
  var bibid = String(data.bibid || "").trim();
  if (!title && !identifier && !bibid) {
    return;
  }

  var params = { barcode: barcode };

  // Check for Identifier (ISBN) duplicate for the same patron only.
  if (identifier) {
    var isbnExisting = app.findRecordsByFilter(
      "title_requests",
      "barcode = {:barcode} && identifier = {:identifier}",
      "-created",
      1,
      0,
      { barcode: barcode, identifier: identifier }
    );
    if (isbnExisting.length) {
      var isbnErr = new Error("You have already submitted a suggestion for this ISBN.");
      isbnErr.code = 409;
      isbnErr.duplicate = duplicateContext(isbnExisting[0], "identifier");
      throw isbnErr;
    }
  }

  var filter = "barcode = {:barcode} && ((title = {:title} && format = {:format})";
  params.title = title || "";
  params.format = normalizeFormat(data.format);

  // Check for BibID match if provided
  if (bibid) {
    filter += " || (bibid = {:bibid})";
    params.bibid = bibid;
  }
  filter += ")";

  var existing = app.findRecordsByFilter("title_requests", filter, "-created", 1, 0, params);
  if (existing.length) {
    var err = new Error("You have already submitted this suggestion.");
    err.code = 409;
    err.duplicate = duplicateContext(existing[0], duplicateMatchType(existing[0], title, normalizeFormat(data.format), bibid));
    throw err;
  }
}

function duplicateMatchType(record, title, format, bibid) {
  if (bibid && String(record.get("bibid") || "").trim() === bibid) {
    return "bibid";
  }
  if (title && String(record.get("title") || "").trim() === title && String(record.get("format") || "").trim() === format) {
    return "title_format";
  }
  return "title_format";
}

function duplicateContext(record, matchType) {
  return {
    id: record.id || "",
    created: record.get("created") || record.created || "",
    status: record.get("status") || "",
    closeReason: record.get("closeReason") || "",
    title: record.get("title") || "",
    author: record.get("author") || "",
    format: record.get("format") || "",
    matchType: matchType || "title_format"
  };
}

function titleCase(value) {
  return String(value || "").trim().replace(/\w\S*/g, function (word) {
    return word.charAt(0).toUpperCase() + word.slice(1).toLowerCase();
  });
}

function normalizeDateOnly(value) {
  value = String(value || "").trim();
  if (!value) {
    return "";
  }
  return value.length === 10 ? value : value.split("T")[0];
}

module.exports = {
  AGEGROUP: AGEGROUP,
  CLOSE_REASON: CLOSE_REASON,
  FORMAT: FORMAT,
  STATUS: STATUS,
  appendSystemNote: appendSystemNote,
  formatDate: formatDate,
  createSuggestion: createSuggestion,
  hasStaffUsers: hasStaffUsers,
  countAdminUsers: countAdminUsers,
  countSuperAdminUsers: countSuperAdminUsers,
  findStaffByIdentity: findStaffByIdentity,
  normalizeAgegroup: normalizeAgegroup,
  normalizeCloseReason: normalizeCloseReason,
  normalizeFormat: normalizeFormat,
  normalizeStatus: normalizeStatus,
  duplicateContext: duplicateContext,
  listStaffUsers: listStaffUsers,
  setStatusWithNote: setStatusWithNote,
  addWorkflowTagForRequest: addWorkflowTagForRequest,
  recordEvent: recordEvent,
  setCanonicalRefs: setCanonicalRefs,
  titleRequestToJson: titleRequestToJson,
  updateTitleRequest: updateTitleRequest,
  upsertPatronUser: upsertPatronUser,
  upsertStaffUser: upsertStaffUser,
};
