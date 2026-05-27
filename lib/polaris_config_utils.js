const polaris = require(`${__hooks}/../lib/polaris.js`);
const httpUtils = require(`${__hooks}/../lib/http_utils.js`);

function buildPolarisData(data) {
  var source = data && data.polaris ? data.polaris : (data || {});
  return {
    host: String(httpUtils.firstValue(source, ["host", "polarisHost"], "") || "").trim(),
    accessId: String(httpUtils.firstValue(source, ["accessId", "polarisAccessId"], "") || "").trim(),
    apiKey: String(httpUtils.firstValue(source, ["apiKey", "polarisApiKey"], "") || ""),
    staffDomain: String(httpUtils.firstValue(source, ["staffDomain", "polarisStaffDomain"], "") || "").trim(),
    adminUser: String(httpUtils.firstValue(source, ["adminUser", "polarisAdminUser"], "") || "").trim(),
    adminPassword: String(httpUtils.firstValue(source, ["adminPassword", "polarisAdminPassword"], "") || ""),
    overridePassword: String(httpUtils.firstValue(source, ["overridePassword", "polarisOverridePassword"], "") || ""),
    langId: String(httpUtils.firstValue(source, ["langId"], "1033") || "1033"),
    appId: String(httpUtils.firstValue(source, ["appId"], "100") || "100"),
    orgId: String(httpUtils.firstValue(source, ["orgId"], "1") || "1"),
    pickupOrgId: String(httpUtils.firstValue(source, ["pickupOrgId"], "0") || "0"),
    requestingOrgId: String(httpUtils.firstValue(source, ["requestingOrgId"], "3") || "3"),
    workstationId: String(httpUtils.firstValue(source, ["workstationId"], "1") || "1"),
    userId: String(httpUtils.firstValue(source, ["userId"], "1") || "1")
  };
}

function missingPolarisTestFields(polarisData) {
  var missing = [];
  if (!polarisData.host) missing.push("host");
  if (!polarisData.accessId) missing.push("access ID");
  if (!polarisData.apiKey) missing.push("API key");
  if (!polarisData.adminUser) missing.push("system staff username");
  if (!polarisData.adminPassword) missing.push("system staff password");
  return missing;
}

function testPolarisConnection(e, polarisData) {
  var missing = missingPolarisTestFields(polarisData);
  if (missing.length) {
    return e.json(400, {
      success: false,
      message: "Missing Polaris " + missing.join(", ") + "."
    });
  }

  try {
    var auth = polaris.adminStaffAuth(polarisData);
    if (auth && auth.AccessToken) {
      return e.json(200, { success: true, message: "Polaris API connection successful!" });
    }
    return e.json(400, { success: false, message: "Authentication failed without an explicit error." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

module.exports = {
  buildPolarisData: buildPolarisData,
  missingPolarisTestFields: missingPolarisTestFields,
  testPolarisConnection: testPolarisConnection
};
