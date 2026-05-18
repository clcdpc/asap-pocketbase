const dbHelpers = require("./db_helpers.js");
const systemRecord = dbHelpers.systemRecord;

function polarisFromRecord(record) {
  record = record || {};
  function str(name, fallback) {
    return String(record.get ? record.get(name) || "" : fallback || "");
  }
  return {
    host: str("host"),
    accessId: str("accessId", "SuggestAPI") || "SuggestAPI",
    apiKey: str("apiKey"),
    staffDomain: str("staffDomain"),
    adminUser: str("adminUser"),
    adminPassword: str("adminPassword"),
    overridePassword: str("overridePassword"),
    langId: str("langId") || "1033",
    appId: str("appId") || "100",
    orgId: str("orgId") || "1",
    pickupOrgId: str("pickupOrgId") || "0",
    requestingOrgId: str("requestingOrgId") || "3",
    workstationId: str("workstationId") || "1",
    userId: str("userId") || "1"
  };
}

function getPolarisSettings(app) {
  app = app || $app;
  return systemRecord(app, "polaris_settings", "polaris00000010", {
    settingsKey: "system",
    accessId: "SuggestAPI",
    overridePassword: "admin",
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: "1",
    userId: "1"
  });
}

function polaris(app) {
  return polarisFromRecord(getPolarisSettings(app || $app));
}

function savePolarisSettings(app, data) {
  var record = getPolarisSettings(app);
  var allowed = {
    host: true, accessId: true, apiKey: true, staffDomain: true, adminUser: true,
    adminPassword: true, overridePassword: true, langId: true, appId: true, orgId: true,
    pickupOrgId: true, requestingOrgId: true, workstationId: true, userId: true
  };
  Object.keys(data || {}).forEach(function (key) {
    if (allowed[key]) record.set(key, data[key]);
  });
  if (!record.get("firstSuccessfulSaveAt") && data.host && data.accessId && data.apiKey) {
    record.set("firstSuccessfulSaveAt", new Date().toISOString());
  }
  app.save(record);
  return record;
}

module.exports = {
  polarisFromRecord: polarisFromRecord,
  getPolarisSettings: getPolarisSettings,
  polaris: polaris,
  savePolarisSettings: savePolarisSettings,
};
