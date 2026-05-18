function safeRecord(app, collection, field, value) {
  try {
    return app.findFirstRecordByData(collection, field, value);
  } catch (err) {
    return null;
  }
}

function safeCollection(app, name) {
  try {
    return app.findCollectionByNameOrId(name);
  } catch (err) {
    return null;
  }
}

function systemRecord(app, collectionName, id, defaults) {
  var collection = safeCollection(app, collectionName);
  if (!collection) return null;
  try {
    return app.findRecordById(collectionName, id);
  } catch (err) {
    var record = new Record(collection);
    record.set("id", id);
    Object.keys(defaults || {}).forEach(function (key) {
      record.set(key, defaults[key]);
    });
    app.save(record);
    return record;
  }
}

function findOrganization(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId) return null;
  return safeRecord(app, "polaris_organizations", "organizationId", orgId);
}

function orgIdForSettings(app, orgId) {
  var org = findOrganization(app, orgId);
  return org ? org.id : "";
}

function scopedRows(app, collectionName, orgId) {
  app = app || $app;
  var rows = [];
  function read(scope, orgRecordId) {
    try {
      var filter = scope === "system" ? "scope = 'system'" : "scope = 'library' && libraryOrganization = {:org}";
      var params = scope === "system" ? {} : { org: orgRecordId };
      rows = rows.concat(app.findRecordsByFilter(collectionName, filter, "sortOrder", 200, 0, params));
    } catch (err) {
      if (scope === "system") {
        try {
          rows = rows.concat(app.findRecordsByFilter(collectionName, "id != ''", "sortOrder", 200, 0));
        } catch (err2) {
          if (app && app.logger) {
            app.logger().warn("Swallowed error", "error", String(err2));
          }
        }
      }
    }
  }
  read("system", "");
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) read("library", orgRecordId);
  return rows;
}

module.exports = {
  safeRecord: safeRecord,
  safeCollection: safeCollection,
  systemRecord: systemRecord,
  findOrganization: findOrganization,
  orgIdForSettings: orgIdForSettings,
  scopedRows: scopedRows,
};
