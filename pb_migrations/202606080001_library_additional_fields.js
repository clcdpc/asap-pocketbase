/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function hasField(collection, name) {
  var fields = collection.fields || [];
  for (var i = 0; i < fields.length; i++) {
    if (fields[i].name === name) return true;
  }
  return false;
}

function addField(collection, spec) {
  try {
    collection.fields.add(new Field(spec));
  } catch (err) {
    collection.fields.add(spec);
  }
}

migrate((app) => {
  var patronOverrides = app.findCollectionByNameOrId("patron_settings_overrides");
  if (!hasField(patronOverrides, "additionalFieldDefinitions")) {
    addField(patronOverrides, field("additionalFieldDefinitions", "json"));
    app.save(patronOverrides);
  }

  var titleRequests = app.findCollectionByNameOrId("title_requests");
  if (!hasField(titleRequests, "customFields")) {
    addField(titleRequests, field("customFields", "json"));
    app.save(titleRequests);
  }
}, (app) => {
  try {
    var titleRequests = app.findCollectionByNameOrId("title_requests");
    titleRequests.fields.removeByName("customFields");
    app.save(titleRequests);
  } catch (err) {}

  try {
    var patronOverrides = app.findCollectionByNameOrId("patron_settings_overrides");
    patronOverrides.fields.removeByName("additionalFieldDefinitions");
    app.save(patronOverrides);
  } catch (err2) {}
});
