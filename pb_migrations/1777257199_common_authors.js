migrate((app) => {
  const collection = app.findCollectionByNameOrId("app_settings");

  collection.fields.add(new Field({
    "system": false,
    "id": "bool_common_authors_enabled",
    "name": "commonAuthorsEnabled",
    "type": "bool",
    "required": false,
    "presentable": false
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "text_common_authors_list",
    "name": "commonAuthorsList",
    "type": "text",
    "required": false,
    "presentable": false,
    "max": 0
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "text_common_authors_message",
    "name": "commonAuthorsMessage",
    "type": "text",
    "required": false,
    "presentable": false,
    "max": 0
  }));

  app.save(collection);

  try {
    const record = app.findRecordById("app_settings", "settings0000001");
    record.set("commonAuthorsEnabled", false);
    record.set("commonAuthorsList", "");
    record.set("commonAuthorsMessage", "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items.");
    app.save(record);
  } catch (err) {
    // record might not exist
  }

}, (app) => {
  const collection = app.findCollectionByNameOrId("app_settings");

  collection.fields.removeByName("commonAuthorsEnabled");
  collection.fields.removeByName("commonAuthorsList");
  collection.fields.removeByName("commonAuthorsMessage");

  app.save(collection);
});
