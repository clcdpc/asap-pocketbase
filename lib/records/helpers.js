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

const WORKFLOW_TAG_DUPLICATE_SUGGESTION = "Duplicate suggestion";

const CLOSE_REASON = {
  REJECTED: "rejected",
  SILENT: "Silently Closed",
  HOLD_COMPLETED: "hold_completed",
  HOLD_NOT_PICKED_UP: "hold_not_picked_up",
  HOLD_UNCLAIMED: "hold_unclaimed",
  HOLD_CANCELLED: "hold_cancelled",
  HOLD_EXPIRED: "hold_expired",
  DUPLICATE_HOLD: "duplicate_hold",
  MANUAL: "manual",
  PURCHASED_NO_HOLD: "purchased_no_hold",
};

function normalizeStatus(value) {
  let map = {
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
  let map = {};
  map[STATUS.SUGGESTION] = "Suggestions";
  map[STATUS.PENDING_HOLD] = "Pending Hold";
  map[STATUS.HOLD_PLACED] = "Hold Placed";
  map[STATUS.OUTSTANDING_PURCHASE] = "Pending Purchase";
  map[STATUS.CLOSED] = "Closed";
  return map[status] || status;
}

function normalizeCloseReason(value) {
  let map = {
    rejected: CLOSE_REASON.REJECTED,
    reject: CLOSE_REASON.REJECTED,
    hold_completed: CLOSE_REASON.HOLD_COMPLETED,
    holdCompleted: CLOSE_REASON.HOLD_COMPLETED,
    hold_placed: CLOSE_REASON.HOLD_COMPLETED,
    checkout: CLOSE_REASON.HOLD_COMPLETED,
    checked_out: CLOSE_REASON.HOLD_COMPLETED,
    unclaimed: CLOSE_REASON.HOLD_UNCLAIMED,
    cancelled: CLOSE_REASON.HOLD_CANCELLED,
    expired: CLOSE_REASON.HOLD_EXPIRED,
    duplicate_hold: CLOSE_REASON.DUPLICATE_HOLD,
    manual: CLOSE_REASON.MANUAL,
    purchased_no_hold: CLOSE_REASON.PURCHASED_NO_HOLD,
    purchased_no_hold_purchase_outcome: CLOSE_REASON.PURCHASED_NO_HOLD,
    silent: CLOSE_REASON.SILENT,
    "Silently Closed": CLOSE_REASON.SILENT,
  };
  return map[String(value || "").trim()] || "";
}

function normalizeFormat(value) {
  let key = String(value || "").trim();
  if (!key) return "book";
  return FORMAT[key] || key;
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

function lookupScopedByCode(app, collection, code, libraryOrgId) {
  code = String(code || "").trim();
  if (!code) return null;
  let org = organizationByPolarisId(app, libraryOrgId);
  if (org) {
    try {
      return app.findFirstRecordByFilter(collection, "scope = 'library' && libraryOrganization = {:org} && code = {:code}", { org: org.id, code: code });
    } catch (err) {}
  }
  try {
    return app.findFirstRecordByFilter(collection, "scope = 'system' && code = {:code}", { code: code });
  } catch (err2) {}
  return lookupByCode(app, collection, code);
}

function lookupScopedByLabel(app, collection, label, libraryOrgId) {
  label = String(label || "").trim();
  if (!label) return null;
  let org = organizationByPolarisId(app, libraryOrgId);
  if (org) {
    try {
      return app.findFirstRecordByFilter(collection, "scope = 'library' && libraryOrganization = {:org} && label = {:label}", { org: org.id, label: label });
    } catch (err) {}
  }
  try {
    return app.findFirstRecordByFilter(collection, "scope = 'system' && label = {:label}", { label: label });
  } catch (err2) {}
  try {
    return app.findFirstRecordByData(collection, "label", label);
  } catch (err3) {
    return null;
  }
}

function setRelation(record, fieldName, relatedRecord) {
  record.set(fieldName, relatedRecord ? relatedRecord.id : "");
}

function setCanonicalRefs(app, record) {
  setRelation(record, "statusRef", lookupByCode(app, "request_statuses", normalizeStatus(record.get("status"))));
  setRelation(record, "formatRef", lookupScopedByCode(app, "material_formats", normalizeFormat(record.get("format")), record.get("libraryOrgId")));
  let reason = normalizeCloseReason(record.get("closeReason"));
  setRelation(record, "closeReasonRef", reason ? lookupByCode(app, "request_close_reasons", reason) : null);
  setRelation(record, "patronOrganization", organizationByPolarisId(app, record.get("patronOrgId")));
  setRelation(record, "libraryOrganization", organizationByPolarisId(app, record.get("libraryOrgId")));
  setRelation(record, "staffLibraryOrganizationCreatedBy", organizationByPolarisId(app, record.get("staffLibraryOrgIdCreatedBy")));
}

function recordEvent(app, record, type, message, options) {
  options = options || {};
  try {
    let collection = app.findCollectionByNameOrId("title_request_events");
    let event = new Record(collection);
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

function looksLikeCatalogWrappedValue(prefix) {
  prefix = String(prefix || "").trim();
  if (!prefix) return false;
  return prefix.indexOf(" / ") >= 0 ||
    /[.;:]$/.test(prefix) ||
    /,\s*\d{4}/.test(prefix) ||
    /\b(author|editor|illustrator|director|producer)\.?$/i.test(prefix);
}

function basicSearchText(value) {
  return String(value || "")
    .replace(/\([^()]*\)/g, " ")
    .replace(/[\u201c\u201d]/g, '"')
    .replace(/[\u2018\u2019]/g, "'")
    .replace(/[\/:;,.]+/g, " ")
    .replace(/\s+-\s+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function polarisSubmittedSearchValue(value) {
  let text = String(value || "").replace(/\s+/g, " ").trim();
  if (!text) return "";

  let wrapped = text.match(/^(.*)\(([^()]*)\)\s*$/);
  if (wrapped) {
    let prefix = String(wrapped[1] || "").trim();
    let original = String(wrapped[2] || "").trim();
    if (looksLikeCatalogWrappedValue(prefix)) {
      return basicSearchText(original);
    }
  }

  return basicSearchText(text);
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

function formatDate(d) {
  return (d.getMonth() + 1) + "/" + d.getDate() + "/" + d.getFullYear();
}

function appendSystemNote(record, note) {
  let today = formatDate(new Date());
  let notesStr = String(record.get("notes") || "").trim();
  let lines = notesStr ? notesStr.split("\n") : [];
  let firstLine = lines.length > 0 ? lines[0] : "";
  
  let cleanNote = note.trim();
  if (!cleanNote.endsWith(".") && !cleanNote.endsWith("!") && !cleanNote.endsWith("?")) {
    cleanNote += ".";
  }

  let rangeRegex = /^(\d{1,2}\/\d{1,2}\/\d{4}) to (\d{1,2}\/\d{1,2}\/\d{4}) \(Count: (\d+)\) (.*)$/;
  let match = firstLine.match(rangeRegex);
  
  if (match) {
    let startDate = match[1];
    let count = parseInt(match[3], 10);
    let existingMsg = match[4].replace(/^\*\*\*ALERT\*\*\* /, "");
    
    if (existingMsg === cleanNote) {
      count++;
      let alertPrefix = count >= 50 ? "***ALERT*** " : "";
      lines[0] = startDate + " to " + today + " (Count: " + count + ") " + alertPrefix + cleanNote;
      record.set("notes", lines.join("\n"));
      return;
    }
  } else {
    let oldRegex = /^(\d{1,2}\/\d{1,2}\/\d{4}) (.*)$/;
    let oldMatch = firstLine.match(oldRegex);
    if (oldMatch) {
      let existingMsg = oldMatch[2].replace(/^\*\*\*ALERT\*\*\* /, "");
      if (existingMsg === cleanNote) {
        let count = 2;
        let alertPrefix = count >= 50 ? "***ALERT*** " : "";
        lines[0] = oldMatch[1] + " to " + today + " (Count: " + count + ") " + alertPrefix + cleanNote;
        lines[0] = oldMatch[1] + " to " + today + " (Count: " + count + ") " + alertPrefix + cleanNote;
        record.set("notes", lines.join("\n"));
        return;
      }
    }
  }
  
  let newEntry = today + " to " + today + " (Count: 1) " + cleanNote;
  if (notesStr) {
    record.set("notes", newEntry + "\n" + notesStr);
  } else {
    record.set("notes", newEntry);
  }
}

module.exports = {
  STATUS: STATUS,
  FORMAT: FORMAT,
  WORKFLOW_TAG_DUPLICATE_SUGGESTION: WORKFLOW_TAG_DUPLICATE_SUGGESTION,
  CLOSE_REASON: CLOSE_REASON,
  normalizeStatus: normalizeStatus,
  getStatusLabel: getStatusLabel,
  normalizeCloseReason: normalizeCloseReason,
  normalizeFormat: normalizeFormat,
  findFirstByData: findFirstByData,
  lookupByCode: lookupByCode,
  organizationByPolarisId: organizationByPolarisId,
  lookupScopedByCode: lookupScopedByCode,
  lookupScopedByLabel: lookupScopedByLabel,
  setRelation: setRelation,
  setCanonicalRefs: setCanonicalRefs,
  recordEvent: recordEvent,
  polarisSubmittedSearchValue: polarisSubmittedSearchValue,
  titleCase: titleCase,
  normalizeDateOnly: normalizeDateOnly,
  formatDate: formatDate,
  appendSystemNote: appendSystemNote,
};
