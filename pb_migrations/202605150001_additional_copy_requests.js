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
  const titleRequests = app.findCollectionByNameOrId("title_requests");
  const organizations = app.findCollectionByNameOrId("polaris_organizations");
  const staffUsers = app.findCollectionByNameOrId("staff_users");

  const collection = new Collection({
    type: "base",
    name: "additional_copy_requests",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (libraryOrgId != '' && libraryOrgId = @request.auth.libraryOrgId))",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (libraryOrgId != '' && libraryOrgId = @request.auth.libraryOrgId))",
    fields: [
      rel("sourceTitleRequest", titleRequests),
      rel("libraryOrganization", organizations),
      field("libraryOrgId", "text", { required: true, max: 32 }),
      field("libraryOrgName", "text", { max: 256 }),
      field("bibid", "text", { required: true, max: 128 }),
      field("title", "text", { max: 256 }),
      field("author", "text", { max: 256 }),
      field("format", "text", { max: 64 }),
      field("identifier", "text", { max: 64 }),
      field("publication", "text", { max: 128 }),
      field("status", "select", { required: true, maxSelect: 1, values: ["open", "closed"] }),
      field("notes", "editor", { maxSize: 5000, convertURLs: false }),
      rel("createdByStaff", staffUsers),
      field("createdByUsername", "text", { max: 256 }),
      rel("closedByStaff", staffUsers),
      field("closedByUsername", "text", { max: 256 }),
      field("closedAt", "date"),
    ],
    indexes: [
      "CREATE INDEX idx_additional_copy_library_status ON additional_copy_requests (libraryOrgId, status)",
      "CREATE INDEX idx_additional_copy_source_request ON additional_copy_requests (sourceTitleRequest)",
      "CREATE INDEX idx_additional_copy_bibid ON additional_copy_requests (bibid)"
    ]
  });
  app.save(collection);
}, (app) => {
  try {
    app.delete(app.findCollectionByNameOrId("additional_copy_requests"));
  } catch (err) {}
});
