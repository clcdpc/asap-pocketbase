migrate((app) => {
  const collection = app.findCollectionByNameOrId("system_settings");

  collection.fields.add(new Field({
    "system": false,
    "id": "formatIconUrlPattern",
    "name": "formatIconUrlPattern",
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

  return app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("system_settings");

  collection.fields.removeByName("formatIconUrlPattern");

  return app.save(collection);
})
