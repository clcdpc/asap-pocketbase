migrate((app) => {
  const collection = app.findCollectionByNameOrId("title_requests");

  collection.fields.add(new Field({
    "system": false,
    "id": "select_isbn_check_status",
    "name": "isbnCheckStatus",
    "type": "select",
    "required": false,
    "presentable": false,
    "maxSelect": 1,
    "values": ["pending", "found", "not_found", "error", "error_max_retries", "skipped_no_isbn", "found_in_polaris" ]
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "text_isbn_check_result",
    "name": "isbnCheckResult",
    "type": "text",
    "required": false,
    "presentable": false,
    "min": 0,
    "max": 0,
    "pattern": "",
    "autogeneratePattern": ""
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "number_isbn_check_retry",
    "name": "isbnCheckRetryCount",
    "type": "number",
    "required": false,
    "presentable": false,
    "min": null,
    "max": null,
    "onlyInt": true
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "date_last_checked",
    "name": "lastChecked",
    "type": "date",
    "required": false,
    "presentable": false,
    "min": "",
    "max": ""
  }));

  app.save(collection);
}, (app) => {
  const collection = app.findCollectionByNameOrId("title_requests");
  collection.fields.removeByName("isbnCheckStatus");
  collection.fields.removeByName("isbnCheckResult");
  collection.fields.removeByName("isbnCheckRetryCount");
  collection.fields.removeByName("lastChecked");
  app.save(collection);
});
