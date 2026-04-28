migrate((app) => {
  const collection = app.findCollectionByNameOrId("library_settings");

  collection.fields.add(new Field({
    "system": false,
    "id": "bool_pending_hold_timeout_enabled",
    "name": "pendingHoldTimeoutEnabled",
    "type": "bool",
    "required": false,
    "presentable": false
  }));

  collection.fields.add(new Field({
    "system": false,
    "id": "number_pending_hold_timeout_days",
    "name": "pendingHoldTimeoutDays",
    "type": "number",
    "required": false,
    "presentable": false
  }));

  app.save(collection);

  try {
    const defaultSettings = app.findFirstRecordByData("library_settings", "libraryOrgId", "system");
    defaultSettings.set("pendingHoldTimeoutEnabled", false);
    defaultSettings.set("pendingHoldTimeoutDays", 14);
    app.save(defaultSettings);
  } catch (err) {
    // default settings might not exist yet if this is a fresh install
  }

}, (app) => {
  const collection = app.findCollectionByNameOrId("library_settings");

  collection.fields.removeByName("pendingHoldTimeoutEnabled");
  collection.fields.removeByName("pendingHoldTimeoutDays");

  app.save(collection);
});
