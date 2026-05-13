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

function addField(collection, spec) {
  try {
    collection.fields.add(new Field(spec));
  } catch (err) {}
}

migrate((app) => {
  const organizations = app.findCollectionByNameOrId("polaris_organizations");
  const staffUsers = app.findCollectionByNameOrId("staff_users");
  const titleRequests = app.findCollectionByNameOrId("title_requests");

  addField(titleRequests, field("claimType", "select", { maxSelect: 1, values: ["manual", "automatic_format_rule"] }));
  addField(titleRequests, field("claimRuleId", "text", { max: 64 }));
  
  const idx1 = "CREATE INDEX IF NOT EXISTS idx_title_requests_claim_type ON title_requests (claimType)";
  const idx2 = "CREATE INDEX IF NOT EXISTS idx_title_requests_claim_rule ON title_requests (claimRuleId)";
  const existingIndexes = titleRequests.indexes || [];
  
  const hasIdx1 = existingIndexes.some(i => i.includes("idx_title_requests_claim_type"));
  const hasIdx2 = existingIndexes.some(i => i.includes("idx_title_requests_claim_rule"));

  if (!hasIdx1) {
    existingIndexes.push(idx1);
  }
  if (!hasIdx2) {
    existingIndexes.push(idx2);
  }
  titleRequests.indexes = existingIndexes;
  app.save(titleRequests);

  try {
    const existing = app.findCollectionByNameOrId("format_claim_rules");
    if (existing) {
      return;
    }
  } catch (err) {}

  try {
    const rules = new Collection({
      type: "base",
      name: "format_claim_rules",
      listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
      viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
      createRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
      updateRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
      deleteRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (@request.auth.role = 'admin' && libraryOrgId = @request.auth.libraryOrgId))",
      fields: [
        rel("libraryOrganization", organizations),
        field("libraryOrgId", "text", { required: true, max: 32 }),
        field("format", "text", { required: true, max: 64 }),
        rel("staffUser", staffUsers),
        field("staffUserId", "text", { required: true, max: 64 }),
        field("active", "bool"),
        rel("createdBy", staffUsers),
        rel("updatedBy", staffUsers),
      ],
      indexes: [
        "CREATE UNIQUE INDEX IF NOT EXISTS idx_format_claim_rules_library_format ON format_claim_rules (libraryOrgId, format)",
        "CREATE INDEX IF NOT EXISTS idx_format_claim_rules_library ON format_claim_rules (libraryOrgId)",
        "CREATE INDEX IF NOT EXISTS idx_format_claim_rules_staff ON format_claim_rules (staffUserId)"
      ]
    });
    app.save(rules);
  } catch (err) {}
}, (app) => {
  return null;
});
