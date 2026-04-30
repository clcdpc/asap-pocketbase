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

function ensureScopeFields(app, collectionName, organizations) {
  const collection = app.findCollectionByNameOrId(collectionName);
  try {
    collection.fields.add(new Field(field("scope", "select", { maxSelect: 1, values: ["system", "library"] })));
  } catch (err) {}
  try {
    collection.fields.add(new Field(rel("libraryOrganization", organizations)));
  } catch (err2) {}
  app.save(collection);
}

function setSystemScope(app, collectionName) {
  try {
    const rows = app.findRecordsByFilter(collectionName, "id != ''", "", 1000, 0);
    rows.forEach(function (row) {
      if (!String(row.get("scope") || "").trim()) {
        row.set("scope", "system");
      }
      if (collectionName === "material_formats" && String(row.get("identifierLabel") || "") === "ISBN") {
        row.set("identifierLabel", "Identifier number");
      }
      app.save(row);
    });
  } catch (err) {}
}

function setScopedIndexes(app, collectionName, indexName) {
  const collection = app.findCollectionByNameOrId(collectionName);
  collection.indexes = [
    "CREATE UNIQUE INDEX " + indexName + " ON " + collectionName + " (scope, libraryOrganization, code)"
  ];
  app.save(collection);
}

migrate((app) => {
  const organizations = app.findCollectionByNameOrId("polaris_organizations");

  ensureScopeFields(app, "material_formats", organizations);
  ensureScopeFields(app, "audience_groups", organizations);

  setSystemScope(app, "material_formats");
  setSystemScope(app, "audience_groups");

  setScopedIndexes(app, "material_formats", "idx_material_formats_scope_org_code");
  setScopedIndexes(app, "audience_groups", "idx_audience_groups_scope_org_code");
}, (app) => {
  return null;
});
