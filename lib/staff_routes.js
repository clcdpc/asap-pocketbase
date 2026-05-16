
const auth = require(`${__hooks}/../lib/staff/auth_routes.js`);
const users = require(`${__hooks}/../lib/staff/users_routes.js`);
const lookup = require(`${__hooks}/../lib/staff/lookup_routes.js`);
const list = require(`${__hooks}/../lib/staff/title_request_list.js`);
const claims = require(`${__hooks}/../lib/staff/title_request_claims.js`);
const actions = require(`${__hooks}/../lib/staff/title_request_actions.js`);
const additionalCopies = require(`${__hooks}/../lib/staff/additional_copy_routes.js`);
const analytics = require(`${__hooks}/../lib/staff/analytics_routes.js`);
const settings = require(`${__hooks}/../lib/staff/settings_routes.js`);
const settingsSave = require(`${__hooks}/../lib/staff/settings_save.js`);
const settingsUi = require(`${__hooks}/../lib/staff/settings_ui.js`);
const settingsEmail = require(`${__hooks}/../lib/staff/settings_email.js`);
const admin = require(`${__hooks}/../lib/staff/admin_routes.js`);
const logo = require(`${__hooks}/../lib/staff/settings_logo_routes.js`);
const composeExports = require(`${__hooks}/../lib/staff/compose_exports.js`).composeExports;

module.exports = composeExports([
  { name: "auth_routes", exports: auth },
  { name: "users_routes", exports: users },
  { name: "lookup_routes", exports: lookup },
  { name: "title_request_list", exports: list },
  { name: "title_request_claims", exports: claims },
  { name: "title_request_actions", exports: actions },
  { name: "additional_copy_routes", exports: additionalCopies },
  { name: "analytics_routes", exports: analytics },
  { name: "settings_routes", exports: settings },
  { name: "settings_save", exports: settingsSave },
  { name: "settings_ui", exports: settingsUi },
  { name: "settings_email", exports: settingsEmail },
  { name: "admin_routes", exports: admin },
  { name: "settings_logo_routes", exports: logo },
]);

