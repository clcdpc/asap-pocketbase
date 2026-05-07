/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  
  // Add new fields
  try {
    workflowSettings.fields.add(new Field(field("externalSearch1Enabled", "bool", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch1Label", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch1UrlTemplate", "text", { required: false })));
    
    workflowSettings.fields.add(new Field(field("externalSearch2Enabled", "bool", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch2Label", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch2UrlTemplate", "text", { required: false })));
    
    workflowSettings.fields.add(new Field(field("externalSearch3Enabled", "bool", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch3Label", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch3UrlTemplate", "text", { required: false })));
    
    app.save(workflowSettings);
  } catch (err) {
  }

  // Migrate existing data and set defaults
  const records = app.findRecordsByFilter("workflow_settings", "id != ''");
  records.forEach(record => {
    const oldLabel = record.get("externalSearchLabel");
    const oldUrl = record.get("externalSearchUrlTemplate");
    
    // Provider 1: Migrate from old fields
    record.set("externalSearch1Label", oldLabel || "Search Amazon");
    record.set("externalSearch1UrlTemplate", oldUrl || "https://www.amazon.com/s?k={{title}}");
    record.set("externalSearch1Enabled", !!oldUrl || !!oldLabel);
    
    // Provider 2: Goodreads
    record.set("externalSearch2Label", "Search Goodreads");
    record.set("externalSearch2UrlTemplate", "https://www.goodreads.com/search?q={{title}}");
    record.set("externalSearch2Enabled", false);
    
    // Provider 3: WorldCat
    record.set("externalSearch3Label", "Search WorldCat");
    record.set("externalSearch3UrlTemplate", "https://www.worldcat.org/search?q={{title}}");
    record.set("externalSearch3Enabled", false);
    
    app.save(record);
  });

  // Remove old fields
  try {
    workflowSettings.fields.removeByName("externalSearchLabel");
    workflowSettings.fields.removeByName("externalSearchUrlTemplate");
    app.save(workflowSettings);
  } catch (err) {
  }

}, (app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  
  // Re-add old fields
  try {
    workflowSettings.fields.add(new Field(field("externalSearchLabel", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearchUrlTemplate", "text", { required: false })));
    app.save(workflowSettings);
  } catch (err) {}

  // Rollback data
  const records = app.findRecordsByFilter("workflow_settings", "id != ''");
  records.forEach(record => {
    record.set("externalSearchLabel", record.get("externalSearch1Label"));
    record.set("externalSearchUrlTemplate", record.get("externalSearch1UrlTemplate"));
    app.save(record);
  });

  // Remove new fields
  try {
    workflowSettings.fields.removeByName("externalSearch1Enabled");
    workflowSettings.fields.removeByName("externalSearch1Label");
    workflowSettings.fields.removeByName("externalSearch1UrlTemplate");
    workflowSettings.fields.removeByName("externalSearch2Enabled");
    workflowSettings.fields.removeByName("externalSearch2Label");
    workflowSettings.fields.removeByName("externalSearch2UrlTemplate");
    workflowSettings.fields.removeByName("externalSearch3Enabled");
    workflowSettings.fields.removeByName("externalSearch3Label");
    workflowSettings.fields.removeByName("externalSearch3UrlTemplate");
    app.save(workflowSettings);
  } catch (err) {}
});
