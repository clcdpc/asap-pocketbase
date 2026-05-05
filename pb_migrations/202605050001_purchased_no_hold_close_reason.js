/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  var collection = app.findCollectionByNameOrId("request_close_reasons");
  var record;
  try {
    record = app.findFirstRecordByData("request_close_reasons", "code", "purchased_no_hold");
  } catch (err) {
    record = new Record(collection);
    record.set("code", "purchased_no_hold");
  }
  record.set("label", "Purchased (no hold)");
  record.set("sortOrder", 70);
  app.save(record);
}, (app) => {
  return null;
});
