const helpers = require("./helpers.js");

function safeEmail(value) {
  value = String(value || "").trim();
  return value.indexOf("@") > 0 ? value : "";
}

function upsertPatronUser(app, patron) {
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
  record.set("nameFirst", String(patron.NameFirst || ""));
  record.set("nameLast", String(patron.NameLast || ""));
  record.set("patronOrgId", String(patron.PatronOrgID || patron.patronOrgId || ""));
  record.set("libraryOrgId", String(patron.LibraryOrgID || patron.libraryOrgId || ""));
  record.set("libraryOrgName", String(patron.LibraryOrgName || patron.libraryOrgName || ""));
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

module.exports = {
  safeEmail: safeEmail,
  upsertPatronUser: upsertPatronUser,
};
