migrate((app) => {
  const collection = app.findCollectionByNameOrId("title_requests");
  const field = collection.fields.getByName("closeReason");
  
  field.values = [
    "rejected",
    "hold_completed",
    "manual",
    "Silently Closed",
    "hold_not_picked_up"
  ];
  
  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("title_requests");
  const field = collection.fields.getByName("closeReason");
  
  field.values = [
    "rejected",
    "hold_completed",
    "manual",
    "silent"
  ];
  
  app.save(collection);
});
