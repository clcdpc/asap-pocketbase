/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  let collection;
  try {
    collection = app.findCollectionByNameOrId("patron_settings_overrides");
  } catch (err) {
    collection = new Collection({
      type: "base",
      name: "patron_settings_overrides",
      listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && orgId = @request.auth.libraryOrgId))",
      viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && orgId = @request.auth.libraryOrgId))",
      createRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && orgId = @request.auth.libraryOrgId))",
      updateRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && orgId = @request.auth.libraryOrgId))",
      deleteRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && orgId = @request.auth.libraryOrgId))",
      fields: [
        field("orgId", "text", { required: true, max: 64 }),
        field("duplicateStatusLabels", "json"),
        field("publicationOptions", "json"),
        field("ageGroups", "json"),
        field("patronFormatRules", "json"),
        field("ebookMessage", "editor", { maxSize: 10000, convertURLs: false }),
        field("eaudiobookMessage", "editor", { maxSize: 10000, convertURLs: false })
      ],
      indexes: ["CREATE UNIQUE INDEX idx_patron_settings_overrides_org ON patron_settings_overrides (orgId)"]
    });
    app.save(collection);
  }

  try {
    const legacy = app.findRecordsByFilter("patron_library_settings", "id != ''", "", 1000, 0);
    legacy.forEach(function (row) {
      let orgId = "";
      try {
        const orgRel = row.get("libraryOrganization");
        const orgRecordId = Array.isArray(orgRel) ? orgRel[0] : orgRel;
        if (orgRecordId) {
          const org = app.findRecordById("polaris_organizations", orgRecordId);
          orgId = String(org.get("organizationId") || "").trim();
        }
      } catch (err2) {}
      if (!orgId) return;
      try {
        app.findFirstRecordByFilter("patron_settings_overrides", "orgId = {:orgId}", { orgId: orgId });
        return;
      } catch (err3) {}
      const record = new Record(collection);
      record.set("orgId", orgId);
      record.set("duplicateStatusLabels", row.get("duplicateRequestStatusLabels") || {});
      app.save(record);
    });
  } catch (err4) {}
}, (app) => {
  try {
    const collection = app.findCollectionByNameOrId("patron_settings_overrides");
    app.delete(collection);
  } catch (err) {}
});
