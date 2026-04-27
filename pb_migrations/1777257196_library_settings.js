migrate((app) => {
  const collection = app.findCollectionByNameOrId("library_email_settings");
  
  collection.name = "library_settings";
  
  collection.fields.add(new Field({
    "system": false,
    "id": "json_ui_text_lib",
    "name": "ui_text",
    "type": "json",
    "required": false,
    "presentable": false
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "json_workflow_lib",
    "name": "workflow",
    "type": "json",
    "required": false,
    "presentable": false
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("library_settings");
  
  collection.name = "library_email_settings";
  
  collection.fields.removeById("json_ui_text_lib");
  collection.fields.removeById("json_workflow_lib");

  app.save(collection);
});
