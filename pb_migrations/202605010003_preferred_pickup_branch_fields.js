/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function addField(collection, spec) {
  try {
    collection.fields.add(new Field(spec));
  } catch (err) {}
}

migrate((app) => {
  const patronUsers = app.findCollectionByNameOrId("patron_users");
  addField(patronUsers, field("preferredPickupBranchId", "text", { max: 32 }));
  addField(patronUsers, field("preferredPickupBranchName", "text", { max: 256 }));
  app.save(patronUsers);

  const titleRequests = app.findCollectionByNameOrId("title_requests");
  addField(titleRequests, field("preferredPickupBranchId", "text", { max: 32 }));
  addField(titleRequests, field("preferredPickupBranchName", "text", { max: 256 }));
  app.save(titleRequests);
}, (app) => {
  return null;
});
