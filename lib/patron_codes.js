const polaris = require("./polaris.js");

function normalizeId(value) {
  value = String(value === undefined || value === null ? "" : value).trim();
  return value;
}

function normalizeRow(row) {
  row = row || {};
  var id = normalizeId(row.PatronCodeID || row.patronCodeId || row.id);
  if (!id) return null;
  return {
    patronCodeId: id,
    description: String(row.Description || row.description || row.PatronCode || row.PatronCodeDescription || "").trim(),
    raw: row
  };
}

function findPatronCode(app, patronCodeId) {
  patronCodeId = normalizeId(patronCodeId);
  if (!patronCodeId) return null;
  try {
    return app.findFirstRecordByData("polaris_patron_codes", "patronCodeId", patronCodeId);
  } catch (err) {
    return null;
  }
}

function upsertPatronCode(app, row) {
  var code = normalizeRow(row);
  if (!code) return null;
  var record = findPatronCode(app, code.patronCodeId);
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("polaris_patron_codes"));
  }
  record.set("patronCodeId", code.patronCodeId);
  record.set("description", code.description);
  record.set("raw", code.raw);
  record.set("lastSynced", new Date().toISOString());
  app.save(record);
  return record;
}

function setSyncStatus(app, status, message, error) {
  try {
    var settings = app.findRecordById("system_settings", "settings0000001");
    settings.set("patronCodesSyncStatus", status);
    settings.set("patronCodesSyncMessage", message || "");
    settings.set("patronCodesSyncError", error || "");
    if (status === "loaded") settings.set("patronCodesLastSynced", new Date().toISOString());
    app.save(settings);
  } catch (err) {}
}

function syncPatronCodes(app, staffAuth, polarisConfig) {
  staffAuth = staffAuth || polaris.adminStaffAuth(polarisConfig || app);
  setSyncStatus(app, "loading", "Patron codes loading from Polaris.", "");
  var count = 0;
  try {
    var rows = polaris.patronCodes(staffAuth, polarisConfig || app);
    var seen = {};
    for (var i = 0; i < rows.length; i++) {
      var code = normalizeRow(rows[i]);
      if (!code || seen[code.patronCodeId]) continue;
      seen[code.patronCodeId] = true;
      if (upsertPatronCode(app, rows[i])) count++;
    }
    setSyncStatus(app, "loaded", "Polaris patron codes loaded successfully.", "");
    return { synced: count };
  } catch (err) {
    setSyncStatus(app, "error", "Polaris connected, but patron codes could not be loaded.", err.message || String(err));
    throw err;
  }
}

function patronCodeDescription(app, patronCodeId) {
  var record = findPatronCode(app, patronCodeId);
  return record ? String(record.get("description") || "").trim() : "";
}

function splitAllowedIds(value) {
  return String(value || "")
    .split(",")
    .map(function (part) { return normalizeId(part); })
    .filter(function (part) { return !!part; });
}

function isEligible(workflow, patron, logger) {
  workflow = workflow || {};
  patron = patron || {};
  if (!workflow.patronCodeEligibilityEnabled) return { allowed: true };
  var allowedIds = splitAllowedIds(workflow.allowedPatronCodeIds);
  if (!allowedIds.length) return { allowed: true };
  var patronCodeId = normalizeId(patron.PatronCodeID || patron.patronCodeId);
  if (!patronCodeId) {
    if (logger && logger.warn) {
      logger.warn("Patron code eligibility enabled, but Polaris did not return PatronCodeID.");
    }
    return { allowed: true };
  }
  if (allowedIds.indexOf(patronCodeId) >= 0) {
    return { allowed: true };
  }
  return {
    allowed: false,
    patronCodeId: patronCodeId,
    message: workflow.patronCodeEligibilityMessage || "Your library card is not eligible to use this suggestion service."
  };
}

module.exports = {
  findPatronCode: findPatronCode,
  isEligible: isEligible,
  normalizeRow: normalizeRow,
  patronCodeDescription: patronCodeDescription,
  setSyncStatus: setSyncStatus,
  splitAllowedIds: splitAllowedIds,
  syncPatronCodes: syncPatronCodes,
  upsertPatronCode: upsertPatronCode
};
