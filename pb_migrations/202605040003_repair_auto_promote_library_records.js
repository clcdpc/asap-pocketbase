/// <reference path="../pb_data/types.d.ts" />

// Data repair: initialize autoPromote on any library workflow_settings records
// that still have it null (migration 202605040002 added the column but may not
// have seeded existing records if it already ran before this fix).
migrate((app) => {
  var systemValue = true;
  try {
    var systemRecord = app.findRecordById("workflow_settings", "workflow0000010");
    // Read raw value — if getBool returns false and the system record was seeded true,
    // we need to check the actual stored value.
    systemValue = systemRecord.getBool("autoPromote");
  } catch (err) { }

  try {
    var libraryRecords = app.findRecordsByFilter("workflow_settings", "scope = 'library'", "", 1000, 0);
    for (var i = 0; i < libraryRecords.length; i++) {
      var rec = libraryRecords[i];
      // get() returns null for unset fields; getBool() coerces to false.
      // Only update records where the field hasn't been explicitly set yet.
      var raw = rec.get("autoPromote");
      if (raw === null || raw === undefined || raw === "" || raw === false) {
        rec.set("autoPromote", systemValue);
        app.save(rec);
      }
    }
  } catch (err) { }
}, (app) => {
  // No rollback needed — this is a data repair, not a schema change.
  return null;
});
