/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  var collection = app.findCollectionByNameOrId("request_close_reasons");
  var record;
  try {
    record = app.findFirstRecordByData("request_close_reasons", "code", "duplicate_hold");
  } catch (err) {
    record = new Record(collection);
    record.set("code", "duplicate_hold");
  }
  record.set("label", "Duplicate hold / request");
  record.set("sortOrder", 60);
  app.save(record);
}, (app) => {
  return null;
});
