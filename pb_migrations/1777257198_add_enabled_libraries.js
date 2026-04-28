migrate((app) => {
  const collection = app.findCollectionByNameOrId("app_settings");

  collection.fields.add(new Field({
    "system": false,
    "id": "text_enabled_libraries",
    "name": "enabledLibraryOrgIds",
    "type": "text",
    "required": false,
    "presentable": false,
    "unique": false,
    "options": {
      "min": null,
      "max": null,
      "pattern": ""
    }
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("app_settings");
  collection.fields.removeById("text_enabled_libraries");
  app.save(collection);
});
