function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  const collection = app.findCollectionByNameOrId("staff_users");

  collection.fields.add(new Field(field("additional_copy_reminder_default", "bool", { required: false })));

  return app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("staff_users");

  collection.fields.removeByName("additional_copy_reminder_default");

  return app.save(collection);
})
