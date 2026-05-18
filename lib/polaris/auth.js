const helpers = require("./helpers.js");

function staffAuth(username, password, polarisConfig, domainOverride) {
  var c = helpers.cfg(polarisConfig);
  var ep = helpers.endpoint("protected", "authenticator/staff", c);
  return helpers.send("POST", ep, JSON.stringify({
    Domain: domainOverride !== undefined && domainOverride !== null ? String(domainOverride) : c.staffDomain,
    Username: username,
    Password: password,
  }), null, null, c);
}

function adminStaffAuth(polarisConfig) {
  var c = helpers.cfg(polarisConfig);
  return staffAuth(c.adminUser, c.adminPassword, c, c.staffDomain);
}

function authenticatePatron(barcode, password, staffAuthToken) {
  var staff = staffAuthToken || adminStaffAuth();
  if (!staff || !staff.AccessToken) {
    throw new Error("Admin staff authentication failed - check your Polaris settings.");
  }
  
  var ep = helpers.endpoint("public", "authenticator/patron");
  if ($app.logger) {
    $app.logger().info("Authenticating patron", "barcode", barcode);
  }
  
  helpers.send("POST", ep, JSON.stringify({
    Barcode: barcode,
    Password: password,
  }), staff);
  
  // Resolve cyclical dependency dynamically
  const patron = require("./patron.js");
  return patron.getPatronBasic(staff, barcode);
}

module.exports = {
  staffAuth: staffAuth,
  adminStaffAuth: adminStaffAuth,
  authenticatePatron: authenticatePatron,
};
