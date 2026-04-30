/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function envValue(name) {
  try {
    return String($os.getenv(name) || "").trim();
  } catch (err) {
    return "";
  }
}

function staffUrlFromEnv(value) {
  let text = String(value || "").trim();
  if (!text) return "";
  const hashIndex = text.indexOf("#");
  if (hashIndex >= 0) text = text.slice(0, hashIndex);
  const queryIndex = text.indexOf("?");
  let beforeQuery = queryIndex >= 0 ? text.slice(0, queryIndex) : text;
  const afterQuery = queryIndex >= 0 ? text.slice(queryIndex) : "";
  beforeQuery = beforeQuery.replace(/\/+$/, "");
  if (!/\/staff$/i.test(beforeQuery)) {
    beforeQuery += "/staff";
  }
  return beforeQuery + "/" + afterQuery;
}

migrate((app) => {
  const systemSettings = app.findCollectionByNameOrId("system_settings");
  try {
    systemSettings.fields.add(new Field(field("staffUrl", "text", { max: 2048 })));
    app.save(systemSettings);
  } catch (err) {}

  try {
    const record = app.findFirstRecordByFilter("system_settings", "settingsKey = 'system'");
    if (!String(record.get("staffUrl") || "").trim()) {
      record.set("staffUrl", staffUrlFromEnv(envValue("ASAP_STAFF_URL")));
      app.save(record);
    }
  } catch (err2) {}
}, (app) => {
  return null;
});
