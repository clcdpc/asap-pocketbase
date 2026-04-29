migrate((app) => {
  const collection = app.findCollectionByNameOrId("title_requests");

  collection.fields.add(new Field({
    "system": false,
    "id": "json_workflow_tags",
    "name": "workflowTags",
    "type": "json",
    "required": false,
    "presentable": false,
    "maxSize": 0
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("title_requests");
  collection.fields.removeByName("workflowTags");
  app.save(collection);
});
