/// <reference path="../pb_data/types.d.ts" />
migrate((app) => {
  const collection = app.findCollectionByNameOrId("polaris_settings");

  collection.fields.add(new Field({
    name: "materialTypesCache",
    type: "json",
  }));

  collection.fields.add(new Field({
    name: "materialTypesCacheUpdated",
    type: "date",
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("polaris_settings");
  collection.fields.removeByName("materialTypesCache");
  collection.fields.removeByName("materialTypesCacheUpdated");
  app.save(collection);
});
