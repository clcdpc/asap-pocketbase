/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function addField(collection, spec) {
  try {
    collection.fields.add(new Field(spec));
  } catch (err) {}
}

migrate((app) => {
  const patronUsers = app.findCollectionByNameOrId("patron_users");
  
  addField(patronUsers, field("notificationEmail", "email", { required: false }));
  
  app.save(patronUsers);
}, (app) => {
  return null;
});
