/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function rel(name, collection, options) {
  options = options || {};
  options.collectionId = collection.id;
  options.maxSelect = options.maxSelect || 1;
  return field(name, "relation", options);
}

migrate((app) => {
  try {
    const existing = app.findCollectionByNameOrId("library_settings");
    if (existing) return; // Already exists
  } catch (err) {}

  const organizations = app.findCollectionByNameOrId("polaris_organizations");

  const collection = new Collection({
    type: "base",
    name: "library_settings",
    // Only super admins can list/view all; library admins can view their own
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrganization = @request.auth.libraryOrganization))",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrganization = @request.auth.libraryOrganization))",
    // No create/update/delete rules because we'll handle this via custom API endpoints with role checks
    createRule: null,
    updateRule: null,
    deleteRule: null,
    fields: [
      rel("libraryOrganization", organizations, { required: true }),
      field("logo", "file", { maxSelect: 1, maxSize: 5242880, mimeTypes: ["image/jpeg", "image/png", "image/svg+xml", "image/gif"] }),
      field("logoAlt", "text")
    ],
    indexes: ["CREATE UNIQUE INDEX idx_library_settings_org ON library_settings (libraryOrganization)"]
  });

  app.save(collection);
}, (app) => {
  try {
    const collection = app.findCollectionByNameOrId("library_settings");
    app.delete(collection);
  } catch (err) {}
});
