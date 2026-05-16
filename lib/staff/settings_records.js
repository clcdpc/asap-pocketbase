const config = require(`${__hooks}/../lib/config.js`);

function libraryOrganizationForSettings(app, orgId) {
  var org = config.findOrganization(app, orgId);
  if (!org) {
    throw new Error("Library organization must be synced before saving library-specific settings.");
  }
  return org;
}

function recordForScope(app, collectionName, scope, orgId) {
  var collection = app.findCollectionByNameOrId(collectionName);

  if (scope === "system") {
    try {
      return app.findFirstRecordByFilter(collectionName, "scope = 'system'");
    } catch (err) {
      var sys = new Record(collection);
      sys.set("scope", "system");
      return sys;
    }
  }

  var org = libraryOrganizationForSettings(app, orgId);

  try {
    return app.findFirstRecordByFilter(
      collectionName,
      "scope = 'library' && libraryOrganization = {:org}",
      { org: org.id }
    );
  } catch (err) {
    var rec = new Record(collection);
    rec.set("scope", "library");
    rec.set("libraryOrganization", org.id);
    return rec;
  }
}

module.exports = {
  libraryOrganizationForSettings: libraryOrganizationForSettings,
  recordForScope: recordForScope,
};
