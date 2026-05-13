/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function removeField(app, collectionName, fieldName) {
  try {
    const collection = app.findCollectionByNameOrId(collectionName);
    collection.fields.removeByName(fieldName);
    app.save(collection);
  } catch (err) {}
}

function clearFieldValues(app, collectionName, fieldName, value) {
  try {
    let offset = 0;
    const limit = 500;
    while (true) {
      const rows = app.findRecordsByFilter(collectionName, "id != ''", "", limit, offset);
      rows.forEach(function (row) {
        row.set(fieldName, value);
        app.save(row);
      });
      if (rows.length < limit) break;
      offset += limit;
    }
  } catch (err) {}
}

migrate((app) => {
  clearFieldValues(app, "title_requests", "audienceGroup", null);
  clearFieldValues(app, "title_requests", "agegroup", "");
  clearFieldValues(app, "ui_settings", "ageGroups", "");
  clearFieldValues(app, "patron_settings_overrides", "ageGroups", null);

  removeField(app, "title_requests", "audienceGroup");
  removeField(app, "title_requests", "agegroup");
  removeField(app, "ui_settings", "ageGroups");
  removeField(app, "patron_settings_overrides", "ageGroups");

  removeField(app, "material_formats", "audienceMode");
  removeField(app, "material_formats", "audienceLabel");

  try {
    const collection = app.findCollectionByNameOrId("audience_groups");
    app.delete(collection);
  } catch (err) {}
}, (app) => {
  try {
    const uiSettings = app.findCollectionByNameOrId("ui_settings");
    uiSettings.fields.add(new Field(field("ageGroups", "text")));
    app.save(uiSettings);
  } catch (err) {}

  try {
    const overrides = app.findCollectionByNameOrId("patron_settings_overrides");
    overrides.fields.add(new Field(field("ageGroups", "json")));
    app.save(overrides);
  } catch (err) {}

  try {
    const materialFormats = app.findCollectionByNameOrId("material_formats");
    materialFormats.fields.add(new Field(field("audienceMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] })));
    materialFormats.fields.add(new Field(field("audienceLabel", "text", { max: 128 })));
    app.save(materialFormats);
  } catch (err) {}
});
