
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

module.exports = {
  ...auth,
  ...users,
  ...lookup,
  ...list,
  ...claims,
  ...actions,
  ...additionalCopies,
  ...analytics,
  ...settings,
  ...settingsSave,
  ...settingsUi,
  ...settingsEmail,
  ...admin,
  ...logo,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE: settingsEmail.TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE: settingsEmail.TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE
};
