const config = require("../config.js");
const helpers = require("./helpers.js");
const tags = require("./tags.js");

const WORKFLOW_TAG_DUPLICATE_SUGGESTION = "Duplicate suggestion";

function enforceWeeklyLimit(app, barcode, libraryOrgId) {
  let since = new Date();
  since.setDate(since.getDate() - 7);
  let cfg = config.suggestionLimit(app, libraryOrgId);
  let limit = cfg.suggestionLimit || cfg.limit || 5;
  let recent = app.findRecordsByFilter(
    "title_requests",
    libraryOrgId ? "barcode = {:barcode} && libraryOrgId = {:libraryOrgId} && created >= {:since}" : "barcode = {:barcode} && created >= {:since}",
    "-created",
    limit,
    0,
    libraryOrgId ? { barcode: barcode, libraryOrgId: String(libraryOrgId), since: since.toISOString() } : { barcode: barcode, since: since.toISOString() }
  );

  if (recent.length >= limit) {
    let oldest = recent[recent.length - 1];
    let createdStr = String(oldest.get("created") || oldest.created || "");
    let oldestDate = new Date(createdStr.replace(" ", "T"));
    let nextAvailable = new Date(oldestDate.getTime() + (7 * 24 * 60 * 60 * 1000));
    
    let msg = cfg.suggestionLimitMessage || cfg.message || "Weekly suggestion limit reached. You can try again after {{next_available_date}}.";
    let dateStr = nextAvailable.toLocaleDateString("en-US", {
      weekday: 'long', 
      year: 'numeric', 
      month: 'long', 
      day: 'numeric', 
      hour: '2-digit', 
      minute: '2-digit' 
    });
    msg = msg.replace("{{next_available_date}}", dateStr);

    let err = new Error(msg);
    err.code = 406;
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

function enforceDuplicate(app, barcode, data) {
  let title = helpers.titleCase(data.title);
  let identifier = String(data.identifier || data.isbn || "").trim();
  let bibid = String(data.bibid || "").trim();
  if (!title && !identifier && !bibid) {
    return;
  }

  let params = { barcode: barcode };

  if (identifier) {
    let isbnExisting = app.findRecordsByFilter(
      "title_requests",
      "barcode = {:barcode} && identifier = {:identifier}",
      "-created",
      1,
      0,
      { barcode: barcode, identifier: identifier }
    );
    if (isbnExisting.length) {
      let isbnErr = new Error("This patron already has a suggestion for this identifier number.");
      isbnErr.code = 409;
      isbnErr.duplicate = duplicateContext(isbnExisting[0], "identifier");
      throw isbnErr;
    }
  }

  let filter = "barcode = {:barcode} && ((title = {:title} && format = {:format})";
  params.title = title || "";
  params.format = helpers.normalizeFormat(data.format);

  if (bibid) {
    filter += " || (bibid = {:bibid})";
    params.bibid = bibid;
  }
  filter += ")";

  let existing = app.findRecordsByFilter("title_requests", filter, "-created", 1, 0, params);
  if (existing.length) {
    let err = new Error("This patron already has this suggestion.");
    err.code = 409;
    err.duplicate = duplicateContext(existing[0], duplicateMatchType(existing[0], title, helpers.normalizeFormat(data.format), bibid));
    throw err;
  }
}

function flagCrossPatronDuplicateSuggestion(app, record) {
  let identifier = String(record.get("identifier") || "").trim();
  if (!identifier) {
    return false;
  }
  try {
    let existing = app.findRecordsByFilter(
      "title_requests",
      "identifier = {:identifier} && barcode != {:barcode} && id != {:id} && libraryOrgId = {:libraryOrgId}",
      "-created",
      1,
      0,
      { identifier: identifier, barcode: record.get("barcode") || "", id: record.id, libraryOrgId: record.get("libraryOrgId") || "" }
    );
    if (!existing.length) {
      return false;
    }
    let added = tags.addWorkflowTagForRequest(app, record, WORKFLOW_TAG_DUPLICATE_SUGGESTION);
    helpers.appendSystemNote(record, "Tagged as a duplicate suggestion because another patron has a suggestion with the same identifier number.");
    app.save(record);
    return added;
  } catch (err) {
    try { app.logger().warn("Cross-patron duplicate tagging failed", "recordId", record && record.id, "error", String(err)); } catch (logErr) {}
    return false;
  }
}

module.exports = {
  enforceWeeklyLimit: enforceWeeklyLimit,
  enforceDuplicate: enforceDuplicate,
  flagCrossPatronDuplicateSuggestion: flagCrossPatronDuplicateSuggestion,
  duplicateMatchType: duplicateMatchType,
  duplicateContext: duplicateContext,
};
