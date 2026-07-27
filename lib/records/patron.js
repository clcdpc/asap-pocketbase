const helpers = require("./helpers.js");
const patronCodes = require("../patron_codes.js");

function safeEmail(value) {
  value = String(value || "").trim();
  return value.indexOf("@") > 0 ? value : "";
}

function upsertPatronUser(app, patron, options) {
  options = options || {};
  let barcode = String(patron.Barcode || patron.barcode || "").trim();
  let email = safeEmail(patron.EmailAddress);

  let record = helpers.findFirstByData(app, "patron_users", "barcode", barcode);

  if (!record && email) {
    let existingByEmail = helpers.findFirstByData(app, "patron_users", "email", email);
    if (!existingByEmail) {
        try {
            existingByEmail = app.findFirstRecordByData("patron_users", "notificationEmail", email);
        } catch (e) {}
    }

    if (existingByEmail) {
      let existingFirst = String(existingByEmail.get("nameFirst") || "").toLowerCase().trim();
      let existingLast = String(existingByEmail.get("nameLast") || "").toLowerCase().trim();
      let newFirst = String(patron.NameFirst || "").toLowerCase().trim();
      let newLast = String(patron.NameLast || "").toLowerCase().trim();

      if (existingFirst === newFirst && existingLast === newLast) {
        record = existingByEmail;
      }
    }
  }

  if (!record) {
    record = new Record(app.findCollectionByNameOrId("patron_users"));
    record.setRandomPassword();
  }

  record.setEmail(barcode + "@patron.asap.local");
  record.set("emailVisibility", false);

  record.set("barcode", barcode);
  record.set("polarisPatronId", String(patron.PatronID || patron.patronId || ""));
  var patronCodeId = String(patron.PatronCodeID || patron.patronCodeId || "").trim();
  record.set("patronCodeId", patronCodeId);
  record.set("patronCodeDescription", String(patron.PatronCode || patron.patronCodeDescription || patronCodes.patronCodeDescription(app, patronCodeId) || ""));
  record.set("nameFirst", String(patron.NameFirst || ""));
  record.set("nameLast", String(patron.NameLast || ""));
  record.set("patronOrgId", String(patron.PatronOrgID || patron.patronOrgId || ""));
  var patronHomeLibraryOrgId = String(patron.LibraryOrgID || patron.libraryOrgId || "");
  var patronHomeLibraryOrgName = String(patron.LibraryOrgName || patron.libraryOrgName || "");
  // libraryOrgId remains the Polaris patron home library for backwards compatibility.
  record.set("libraryOrgId", patronHomeLibraryOrgId);
  record.set("libraryOrgName", patronHomeLibraryOrgName);
  record.set("patronHomeLibraryOrgId", patronHomeLibraryOrgId);
  record.set("patronHomeLibraryOrgName", patronHomeLibraryOrgName);
  record.set("preferredPickupBranchId", String(patron.PreferredPickupBranchID || patron.preferredPickupBranchId || ""));
  record.set("preferredPickupBranchName", String(patron.PreferredPickupBranchName || patron.preferredPickupBranchName || ""));
  helpers.setRelation(record, "patronOrganization", helpers.organizationByPolarisId(app, patron.PatronOrgID || patron.patronOrgId));
  helpers.setRelation(record, "libraryOrganization", helpers.organizationByPolarisId(app, patron.LibraryOrgID || patron.libraryOrgId));
  
  if (email) {
    record.set("notificationEmail", email);
  }

  record.set("lastOrgSync", new Date().toISOString());
  record.set("lastPolarisLogin", new Date().toISOString());
  record.setVerified(true);
  app.save(record);
  return record;
}

function cachePolarisPatronId(app, patron) {
  if (!app || !patron) return null;
  let barcode = String(patron.Barcode || patron.barcode || "").trim();
  let patronId = String(patron.PatronID || patron.patronId || "").trim();
  if (!barcode || !patronId) return null;

  let record = helpers.findFirstByData(app, "patron_users", "barcode", barcode);
  if (!record) return null;
  if (String(record.get("polarisPatronId") || "").trim() === patronId) return record;

  record.set("polarisPatronId", patronId);
  var patronCodeId = String(patron.PatronCodeID || patron.patronCodeId || "").trim();
  if (patronCodeId) {
    record.set("patronCodeId", patronCodeId);
    record.set("patronCodeDescription", String(patron.PatronCode || patron.patronCodeDescription || patronCodes.patronCodeDescription(app, patronCodeId) || ""));
  }
  app.save(record);
  return record;
}

function cachedPolarisPatronIdForTitleRequest(app, titleRequest) {
  if (!app || !titleRequest) return "";
  let patronRecordId = String(titleRequest.get("patron") || "").trim();
  if (!patronRecordId) return "";
  try {
    let patronRecord = app.findRecordById("patron_users", patronRecordId);
    return String(patronRecord.get("polarisPatronId") || "").trim();
  } catch (err) {
    return "";
  }
}

module.exports = {
  safeEmail: safeEmail,
  upsertPatronUser: upsertPatronUser,
  cachePolarisPatronId: cachePolarisPatronId,
  cachedPolarisPatronIdForTitleRequest: cachedPolarisPatronIdForTitleRequest,
};
