/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  const collection = app.findCollectionByNameOrId("additional_copy_requests");
  
  // Add missing system fields if they don't exist
  // Note: PocketBase usually adds these automatically, but they seem to be missing from the table
  // We'll add them as date fields to match the pattern in other collections
  collection.fields.add(new Field({
    name: "created",
    type: "date",
  }));
  collection.fields.add(new Field({
    name: "updated",
    type: "date",
  }));
  
  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("additional_copy_requests");
  collection.fields.removeByName("created");
  collection.fields.removeByName("updated");
  app.save(collection);
});
