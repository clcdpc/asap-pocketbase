/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  const collection = app.findCollectionByNameOrId("additional_copy_requests");

  collection.fields.add(new Field({
    name: "claimedByStaff",
    type: "relation",
    collectionId: "pbc_338227426",
    maxSelect: 1,
  }));

  collection.fields.add(new Field({
    name: "claimedByStaffUserId",
    type: "text",
    max: 32,
  }));

  collection.fields.add(new Field({
    name: "claimedByDisplayName",
    type: "text",
    max: 256,
  }));

  collection.fields.add(new Field({
    name: "claimedAt",
    type: "date",
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("additional_copy_requests");
  collection.fields.removeByName("claimedByStaff");
  collection.fields.removeByName("claimedByStaffUserId");
  collection.fields.removeByName("claimedByDisplayName");
  collection.fields.removeByName("claimedAt");
  app.save(collection);
});
