const records = require(`${__hooks}/lib/records.js`);

function parseCsv(text) {
  var rows = [];
  var row = [];
  var field = "";
  var quoted = false;

  for (var i = 0; i < text.length; i++) {
    var ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') {
        field += '"';
        i++;
      } else if (ch === '"') {
        quoted = false;
      } else {
        field += ch;
      }
    } else if (ch === '"') {
      quoted = true;
    } else if (ch === ",") {
      row.push(field);
      field = "";
    } else if (ch === "\n") {
      row.push(field);
      rows.push(row);
      row = [];
      field = "";
    } else if (ch !== "\r") {
      field += ch;
    }
  }
  row.push(field);
  rows.push(row);

  var headers = rows.shift().map((h) => h.trim());
  return rows
    .filter((r) => r.join("").trim() !== "")
    .map((r) => {
      var item = {};
      for (var i = 0; i < headers.length; i++) {
        item[headers[i]] = r[i] || "";
      }
      return item;
    });
}

function importRows(app, rows) {
  var imported = 0;
  var skipped = 0;
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    try {
      upsertLegacyRow(app, row);
      imported++;
    } catch (err) {
      skipped++;
      app.logger().error("Legacy import row skipped", "error", String(err), "row", JSON.stringify(row));
    }
  }
  return { imported: imported, skipped: skipped };
}

function upsertLegacyRow(app, row) {
  var legacyId = parseInt(row.id || row.legacyId || "0", 10);
  var record = null;
  if (legacyId) {
    var found = app.findRecordsByFilter("title_requests", "legacyId = {:legacyId}", "", 1, 0, { legacyId: legacyId });
    if (found.length) {
      record = found[0];
    }
  }
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("title_requests"));
  }

  var barcode = String(row.barcode || "").trim();
  var email = String(row.email || "").trim();
  var patronOrgId = String(row.patronOrgId || row.PatronOrgID || "").trim();
  var libraryOrgId = String(row.libraryOrgId || row.LibraryOrgID || "").trim();
  var libraryOrgName = String(row.libraryOrgName || row.LibraryOrgName || "").trim();
  var patron = upsertImportPatron(app, barcode, email, row.nameFirst || "", row.nameLast || "", {
    patronOrgId: patronOrgId,
    libraryOrgId: libraryOrgId,
    libraryOrgName: libraryOrgName,
  });

  if (patron) {
    record.set("patron", patron.id);
  }
  record.set("barcode", barcode);
  record.set("email", email);
  record.set("nameFirst", String(row.nameFirst || ""));
  record.set("nameLast", String(row.nameLast || ""));
  record.set("patronOrgId", patronOrgId);
  record.set("libraryOrgId", libraryOrgId);
  record.set("libraryOrgName", libraryOrgName);
  record.set("title", String(row.title || "").trim());
  record.set("author", String(row.author || "").trim());
  record.set("identifier", String(row.identifier || row.isbn || "").trim());
  var publication = String(row.publication || "").trim();
  var exactPublicationDate = normalizeDate(row.exactPublicationDate || row.publicationDate || "");
  if (!exactPublicationDate && /^\d{4}-\d{2}-\d{2}/.test(publication)) {
    exactPublicationDate = normalizeDate(publication);
    publication = inferPublicationTiming(exactPublicationDate);
  }
  record.set("publication", publication);
  record.set("exactPublicationDate", exactPublicationDate);
  record.set("autohold", String(row.autohold || "1") !== "0");
  record.set("status", records.normalizeStatus(row.status));
  record.set("agegroup", records.normalizeAgegroup(row.agegroup));
  record.set("format", records.normalizeFormat(row.format));
  record.set("editedBy", String(row.editedBy || "legacy-import"));
  record.set("notes", String(row.notes || ""));
  record.set("bibid", String(row.bibid || ""));
  record.set("closeReason", records.normalizeCloseReason(row.closeReason || inferCloseReason(row)));
  records.setCanonicalRefs(app, record);
  var created = normalizeDate(row.created || row.createdAt || row.submittedAt || row.dateSubmitted);
  var updated = normalizeDate(row.updated || row.updatedAt || "") || created;
  record.set("created", created || new Date().toISOString());
  record.set("updated", updated || new Date().toISOString());
  if (legacyId) {
    record.set("legacyId", legacyId);
  }
  app.save(record);
  records.recordEvent(app, record, "legacy_import", "Imported legacy suggestion.", { actorType: "system", actorName: "legacy-import" });
}

function inferCloseReason(row) {
  if (records.normalizeStatus(row.status) !== records.STATUS.CLOSED) {
    return "";
  }
  var notes = String(row.notes || "").toLowerCase();
  if (notes.indexOf("reject") >= 0) {
    return records.CLOSE_REASON.REJECTED;
  }
  if (notes.indexOf("checked out") >= 0 || notes.indexOf("hold placed") >= 0) {
    return records.CLOSE_REASON.HOLD_COMPLETED;
  }
  return records.CLOSE_REASON.MANUAL;
}

function inferPublicationTiming(value) {
  var date = new Date(value);
  var today = new Date();
  if (!isNaN(date.getTime()) && date.getTime() > today.getTime()) {
    return "Coming soon";
  }
  if (!isNaN(date.getTime()) && today.getFullYear() - date.getFullYear() >= 2) {
    return "Published a while back";
  }
  return "Already published";
}

function normalizeDate(value) {
  value = String(value || "").trim();
  if (!value) {
    return "";
  }
  var date = new Date(value);
  if (isNaN(date.getTime())) {
    return "";
  }
  return date.toISOString();
}

function upsertImportPatron(app, barcode, email, firstName, lastName, scope) {
  scope = scope || {};
  if (!barcode) {
    return null;
  }
  var patron = null;
  try {
    patron = app.findFirstRecordByData("patron_users", "barcode", barcode);
  } catch (err) {
    patron = new Record(app.findCollectionByNameOrId("patron_users"));
    patron.set("barcode", barcode);
    patron.setEmail(email && email.indexOf("@") > 0 ? email : barcode + "@patron.asap.local");
    patron.setRandomPassword();
    patron.setVerified(true);
  }
  patron.set("nameFirst", String(firstName || ""));
  patron.set("nameLast", String(lastName || ""));
  patron.set("patronOrgId", String(scope.patronOrgId || ""));
  patron.set("libraryOrgId", String(scope.libraryOrgId || ""));
  patron.set("libraryOrgName", String(scope.libraryOrgName || ""));
  try {
    var patronOrg = app.findFirstRecordByData("polaris_organizations", "organizationId", String(scope.patronOrgId || ""));
    patron.set("patronOrganization", patronOrg.id);
  } catch (err) {}
  try {
    var libraryOrg = app.findFirstRecordByData("polaris_organizations", "organizationId", String(scope.libraryOrgId || ""));
    patron.set("libraryOrganization", libraryOrg.id);
  } catch (err) {}
  if (email && email.indexOf("@") > 0) {
    patron.setEmail(email);
  }
  app.save(patron);
  return patron;
}

module.exports = {
  importRows: importRows,
  parseCsv: parseCsv,
};
