/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  try {
    const existing = app.findCollectionByNameOrId("polaris_pickup_branch_cache");
    if (existing) return;
  } catch (err) {}

  const collection = new Collection({
    type: "base",
    name: "polaris_pickup_branch_cache",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    createRule: null,
    updateRule: null,
    deleteRule: null,
    fields: [
      field("patronOrgId", "text", { required: true, max: 32 }),
      field("branches", "json", { required: true }),
      field("refreshedAt", "date", { required: true }),
      field("sourceKey", "text", { max: 512 })
    ],
    indexes: [
      "CREATE UNIQUE INDEX idx_polaris_pickup_branch_cache_org ON polaris_pickup_branch_cache (patronOrgId)",
      "CREATE INDEX idx_polaris_pickup_branch_cache_refreshed ON polaris_pickup_branch_cache (refreshedAt)"
    ]
  });

  app.save(collection);
}, (app) => {
  try {
    const collection = app.findCollectionByNameOrId("polaris_pickup_branch_cache");
    app.delete(collection);
  } catch (err) {}
});
