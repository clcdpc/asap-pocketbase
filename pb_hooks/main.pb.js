routerAdd("POST", "/api/asap/staff/login", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffLogin(e);
});

routerAdd("GET", "/api/asap/setup/status", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.setupStatus(e);
});

routerAdd("POST", "/api/asap/setup", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.initialSetup(e);
});

routerAdd("POST", "/api/asap/setup/test-polaris", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.setupTestPolaris(e);
});

routerAdd("POST", "/api/asap/patron/login", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.patronLogin(e);
});

routerAdd("POST", "/api/asap/patron/suggestions", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.createSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/suggestions", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffCreateSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/patron-lookup", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffLookupPatron(e);
});

routerAdd("POST", "/api/asap/staff/bib-lookup", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffBibLookup(e);
});

routerAdd("POST", "/api/asap/staff/test-polaris", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffTestPolaris(e);
});

routerAdd("POST", "/api/asap/staff/test-smtp", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffTestSmtp(e);
});

routerAdd("GET", "/api/asap/staff/email-status", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffEmailStatus(e);
});

routerAdd("GET", "/api/asap/staff/users", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffUsersList(e);
});

routerAdd("POST", "/api/asap/staff/users/{id}/role", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffUserRoleUpdate(e);
});

routerAdd("GET", "/api/asap/staff/settings/library", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.getLibrarySettings(e);
});

routerAdd("POST", "/api/asap/staff/settings/library", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.updateLibrarySettings(e);
});

routerAdd("GET", "/api/asap/staff/title-requests", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffTitleRequestsList(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/action", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffTitleRequestAction(e);
});

routerAdd("POST", "/api/asap/staff/organizations/sync", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffSyncOrganizations(e);
});

routerAdd("POST", "/api/asap/jobs/hold-check", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.runHoldCheck(e);
});

routerAdd("POST", "/api/asap/jobs/promoter-check", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.staffRunPromoterCheck(e);
});

routerAdd("POST", "/api/asap/import/title-requests", (e) => {
  const routes = require(`${__hooks}/lib/routes.js`);
  return routes.importTitleRequests(e);
});

routerAdd("GET", "/api/asap/config", (e) => {
  const config = require(`${__hooks}/lib/config.js`);
  const orgId = e.request.url.query().get("libraryOrgId") || "";
  var settings = orgId ? config.librarySettings(e.app, orgId) : config.getSettings();
  
  var response = settings.ui_text || {};
  var wf = settings.workflow || settings; // librarySettings has .workflow, getSettings has top-level
  
  response.commonAuthorsEnabled = !!wf.commonAuthorsEnabled;
  response.commonAuthorsList = wf.commonAuthorsList || "";
  response.commonAuthorsMessage = wf.commonAuthorsMessage || "";
  
  return e.json(200, response);
});

onBootstrap((e) => {
  e.next();
  const config = require(`${__hooks}/lib/config.js`);
  config.applyMailSettings(e.app);

  // Initialize or Scrub settings record
  try {
    let record;
    try {
      record = e.app.findRecordById("app_settings", "settings0000001");
    } catch (findErr) {
      // Record missing, create it
      const collection = e.app.findCollectionByNameOrId("app_settings");
      record = new Record(collection);
      record.setId("settings0000001");
      record.set("allowedStaffUsers", "");
      e.app.save(record);
    }

    let uiText = {};
    try { uiText = JSON.parse(record.getString("ui_text") || "{}"); } catch (pe) { }

    const oldJplMsg = 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using this free service of the Jacksonville Public Library.</div>';
    const oldJplFormNote = "If the Jacksonville Public Library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email based on the above form. Make sure to check your spam folder if you don't see the email.";
    const oldJplLoginNote = "Use of this service requires a Jacksonville Public Library card. If you are a Duval County resident and don't already have one, <a href=\"https://jaxpubliclibrary.org/services/get-library-card\">Sign up today</a>.<div>If you cannot remember your library card pin, <a href=\"https://auth.na4.iiivega.com/auth/realms/jaxpl/login-actions/reset-credentials?client_id=convergence&redirect_uri=https%3A%2F%2Fjaxpl.na4.iiivega.com\">you can reset your pin here</a>.</div>";

    let changed = false;
    if (uiText.successMessage === oldJplMsg) { delete uiText.successMessage; changed = true; }
    if (uiText.suggestionFormNote === oldJplFormNote) { delete uiText.suggestionFormNote; changed = true; }
    if (uiText.loginNote === oldJplLoginNote) { delete uiText.loginNote; changed = true; }

    if (changed) {
      record.set("ui_text", JSON.stringify(uiText));
      e.app.save(record);
    }
  } catch (err) { }
});

cronAdd("asap-hold-check", $os.getenv("ASAP_CRON_SCHEDULE") || "0 * * * *", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
  jobs.runScheduledHoldCheck($app);
});

cronAdd("asap-organization-sync", $os.getenv("ASAP_ORG_SYNC_CRON_SCHEDULE") || "0 2 * * *", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
  jobs.runScheduledOrganizationSync($app);
});
