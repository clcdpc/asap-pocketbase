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
    app.findCollectionByNameOrId("deleted_request_audit");
    return;
  } catch (err) {}

  const staffUsers = app.findCollectionByNameOrId("staff_users");
  app.save(new Collection({
    type: "base",
    name: "deleted_request_audit",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
    createRule: null,
    updateRule: null,
    deleteRule: null,
    fields: [
      field("titleRequestId", "text", { max: 64 }),
      field("title", "text"),
      field("author", "text"),
      field("identifier", "text"),
      field("bibid", "text", { max: 64 }),
      field("barcode", "text", { max: 64 }),
      field("libraryOrgId", "text", { max: 64 }),
      field("libraryOrgName", "text"),
      field("status", "text", { max: 64 }),
      field("closeReason", "text", { max: 64 }),
      rel("deletedByStaff", staffUsers),
      field("deletedByUsername", "text", { max: 256 }),
      field("deletedByRole", "text", { max: 64 }),
      field("deletedAt", "date"),
      field("deleteMode", "select", { maxSelect: 1, values: ["single", "bulk"] }),
      field("snapshot", "json"),
    ],
    indexes: [
      "CREATE INDEX idx_deleted_request_audit_title_request ON deleted_request_audit (titleRequestId)",
      "CREATE INDEX idx_deleted_request_audit_library ON deleted_request_audit (libraryOrgId)",
      "CREATE INDEX idx_deleted_request_audit_deleted_at ON deleted_request_audit (deletedAt)"
    ]
  }));
}, (app) => {
  return null;
});
