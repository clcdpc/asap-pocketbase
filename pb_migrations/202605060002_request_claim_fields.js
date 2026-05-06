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
  const titleRequests = app.findCollectionByNameOrId("title_requests");
  addField(titleRequests, field("claimedByStaffUserId", "text", { max: 64 }));
  addField(titleRequests, field("claimedByDisplayName", "text", { max: 256 }));
  addField(titleRequests, field("claimedAt", "date"));
  titleRequests.indexes = [
    ...(titleRequests.indexes || []),
    "CREATE INDEX IF NOT EXISTS idx_title_requests_claimed_by ON title_requests (claimedByStaffUserId)"
  ];
  app.save(titleRequests);
}, (app) => {
  return null;
});
