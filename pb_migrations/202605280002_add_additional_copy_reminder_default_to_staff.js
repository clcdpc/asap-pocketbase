migrate((db) => {
  const dao = new Dao(db);
  const collection = dao.findCollectionByNameOrId("staff_users");

  collection.schema.addField(new SchemaField({
    "system": false,
    "id": "additional_copy_reminder_default",
    "name": "additional_copy_reminder_default",
    "type": "bool",
    "required": false,
    "presentable": false,
    "unique": false,
    "options": {}
  }));

  return dao.saveCollection(collection);
}, (db) => {
  const dao = new Dao(db);
  const collection = dao.findCollectionByNameOrId("staff_users");

  collection.schema.removeField("additional_copy_reminder_default");

  return dao.saveCollection(collection);
})
