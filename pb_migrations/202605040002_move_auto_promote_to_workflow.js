/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  // Add autoPromote field to workflow_settings
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  workflowSettings.fields.add(new Field({
    name: "autoPromote",
    type: "bool"
  }));
  app.save(workflowSettings);

  // Copy the current autoPromote value from polaris_settings into the system workflow_settings record
  var currentValue = true; // default if polaris record not found
  try {
    var polarisRecord = app.findRecordById("polaris_settings", "polaris00000010");
    currentValue = polarisRecord.getBool("autoPromote");
  } catch (err) { }

  try {
    var systemWorkflowRecord = app.findRecordById("workflow_settings", "workflow0000010");
    systemWorkflowRecord.set("autoPromote", currentValue);
    app.save(systemWorkflowRecord);
  } catch (err) { }

  // Initialize all existing library workflow_settings records with the same value
  // so they don't read as false (unset) after the migration adds the column.
  try {
    var libraryRecords = app.findRecordsByFilter("workflow_settings", "scope = 'library'", "", 1000, 0);
    for (var i = 0; i < libraryRecords.length; i++) {
      libraryRecords[i].set("autoPromote", currentValue);
      app.save(libraryRecords[i]);
    }
  } catch (err) { }
}, (app) => {
  // Rollback: remove autoPromote from workflow_settings
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  workflowSettings.fields.removeByName("autoPromote");
  app.save(workflowSettings);
});
