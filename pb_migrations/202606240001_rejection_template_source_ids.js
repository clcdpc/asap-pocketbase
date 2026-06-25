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
  var rejectionTemplates = app.findCollectionByNameOrId("rejection_templates");
  if (!hasField(rejectionTemplates, "sourceTemplateId")) {
    addField(rejectionTemplates, field("sourceTemplateId", "text", { max: 64 }));
    app.save(rejectionTemplates);
  }
}, (app) => {
  try {
    var rejectionTemplates = app.findCollectionByNameOrId("rejection_templates");
    rejectionTemplates.fields.removeByName("sourceTemplateId");
    app.save(rejectionTemplates);
  } catch (err) {}
});
