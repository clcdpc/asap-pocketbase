/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  const systemSettings = app.findCollectionByNameOrId("system_settings");
  try {
    systemSettings.fields.add(new Field(field("leapBibUrlPattern", "text", { max: 2048 })));
    app.save(systemSettings);
  } catch (err) {}
}, (app) => {
  return null;
});
