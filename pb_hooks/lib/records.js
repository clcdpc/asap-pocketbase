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
  }
  if (options.libraryOrgId !== undefined) {
    record.set("libraryOrgId", String(options.libraryOrgId || ""));
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
  enforceWeeklyLimit(app, barcode);
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
  record.set("identifier", String(data.identifier || data.isbn || "").trim());
  record.set("publication", String(data.publication || "").trim());
  record.set("exactPublicationDate", normalizeDateOnly(data.exactPublicationDate));
  record.set("autohold", true);
  record.set("status", STATUS.SUGGESTION);
  record.set("agegroup", String(data.agegroup || "").trim() ? normalizeAgegroup(data.agegroup) : "");
  record.set("format", normalizeFormat(data.format));
  record.set("editedBy", "system");
  record.set("notes", String(data.notes || ""));
  record.set("bibid", "");
  record.set("closeReason", "");
  record.set("created", now);
  record.set("updated", now);
  app.save(record);
  return record;
}

function titleRequestToJson(record) {
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
    agegroup: record.get("agegroup") || "",
    format: record.get("format") || "",
    editedBy: record.get("editedBy") || "",
    notes: record.get("notes") || "",
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
  app.save(record);
  return record;
}

function formatDate(d) {
  return (d.getMonth() + 1) + "/" + d.getDate() + "/" + d.getFullYear();
}

function appendSystemNote(record, note) {
  var today = formatDate(new Date());
  var existing = String(record.get("notes") || "").trim();
  var newNote = today + " " + note.trim();
  if (!newNote.endsWith(".") && !newNote.endsWith("!") && !newNote.endsWith("?")) {
    newNote += ".";
  }
  if (existing) {
    record.set("notes", newNote + "\n" + existing);
  } else {
    record.set("notes", newNote);
  }
}

function setStatusWithNote(app, record, status, note, editedBy) {
  record.set("status", status);
  record.set("editedBy", editedBy || "system");
  record.set("updated", new Date().toISOString());
  if (note) {
    appendSystemNote(record, note);
  }
  app.save(record);
  return record;
}

function enforceWeeklyLimit(app, barcode) {
  var since = new Date();
  since.setDate(since.getDate() - 7);
  var cfg = config.suggestionLimit();
  var limit = cfg.limit || 5;
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
    
    var msg = cfg.message || "Weekly suggestion limit reached. You can try again after {{next_available_date}}.";
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

  var filter = "barcode = {:barcode} && (";
  var params = { barcode: barcode };

  // Check for Title + Format match
  filter += "(title = {:title} && format = {:format})";
  params.title = title || "";
  params.format = normalizeFormat(data.format);

  // Check for Identifier (ISBN) match if provided
  if (identifier) {
    filter += " || (identifier = {:identifier})";
    params.identifier = identifier;
  }

  // Check for BibID match if provided
  if (bibid) {
    filter += " || (bibid = {:bibid})";
    params.bibid = bibid;
  }

  filter += ")";

  var existing = app.findRecordsByFilter("title_requests", filter, "-created", 1, 0, params);
  if (existing.length) {
    var err = new Error("Duplicate suggestion");
    err.code = 409;
    throw err;
  }
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
  listStaffUsers: listStaffUsers,
  setStatusWithNote: setStatusWithNote,
  titleRequestToJson: titleRequestToJson,
  updateTitleRequest: updateTitleRequest,
  upsertPatronUser: upsertPatronUser,
  upsertStaffUser: upsertStaffUser,
};
