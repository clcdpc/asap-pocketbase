// Routes and handlers
// Note: Library files are located in ../lib/ to prevent the macOS file watcher
// from triggering infinite restart loops on every require() access.

routerAdd("POST", "/api/asap/staff/login", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffLogin(e);
});

routerAdd("GET", "/api/asap/setup/status", (e) => {
  return require(`${__hooks}/../lib/setup_routes.js`).setupStatus(e);
});

routerAdd("POST", "/api/asap/setup", (e) => {
  return require(`${__hooks}/../lib/setup_routes.js`).initialSetup(e);
});

routerAdd("POST", "/api/asap/setup/test-polaris", (e) => {
  return require(`${__hooks}/../lib/setup_routes.js`).setupTestPolaris(e);
});

routerAdd("POST", "/api/asap/patron/login", (e) => {
  return require(`${__hooks}/../lib/patron_routes.js`).patronLogin(e);
});

routerAdd("POST", "/api/asap/patron/suggestions", (e) => {
  return require(`${__hooks}/../lib/patron_routes.js`).createSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/suggestions", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffCreateSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/patron-lookup", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffLookupPatron(e);
});

routerAdd("POST", "/api/asap/staff/bib-lookup", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffBibLookup(e);
});

routerAdd("POST", "/api/asap/staff/test-polaris", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTestPolaris(e);
});

routerAdd("POST", "/api/asap/staff/test-smtp", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTestSmtp(e);
});

routerAdd("POST", "/api/asap/staff/profile", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffProfileUpdate(e);
});

routerAdd("GET", "/api/asap/staff/email-status", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffEmailStatus(e);
});

routerAdd("GET", "/api/asap/staff/users", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffUsersList(e);
});

routerAdd("POST", "/api/asap/staff/users", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffUserCreate(e);
});

routerAdd("POST", "/api/asap/staff/users/{id}/role", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffUserRoleUpdate(e);
});

routerAdd("DELETE", "/api/asap/staff/users/{id}", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffUserDelete(e);
});

routerAdd("GET", "/api/asap/staff/settings/library", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).getLibrarySettings(e);
});

routerAdd("POST", "/api/asap/staff/settings/library", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).updateLibrarySettings(e);
});

routerAdd("GET", "/api/asap/staff/title-requests", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestsList(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/action", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestAction(e);
});

routerAdd("DELETE", "/api/asap/staff/requests/{id}", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffDeleteClosedRequest(e);
});

routerAdd("POST", "/api/asap/staff/requests/delete-closed", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffDeleteClosedRequestsBulk(e);
});

routerAdd("POST", "/api/asap/staff/organizations/sync", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffSyncOrganizations(e);
});

routerAdd("POST", "/api/asap/jobs/hold-check", (e) => {
  return require(`${__hooks}/../lib/job_routes.js`).runHoldCheck(e);
});

routerAdd("POST", "/api/asap/jobs/promoter-check", (e) => {
  return require(`${__hooks}/../lib/job_routes.js`).staffRunPromoterCheck(e);
});

routerAdd("POST", "/api/asap/jobs/weekly-staff-action-summary", (e) => {
  return require(`${__hooks}/../lib/job_routes.js`).runWeeklyStaffActionSummary(e);
});

routerAdd("GET", "/api/asap/config", (e) => {
  try {
    const config = require(`${__hooks}/../lib/config.js`);
    const orgId = e.request.url.query().get("libraryOrgId") || "";
    var settings = orgId ? config.librarySettings(e.app, orgId) : config.getSettings();
    
    var response = settings.ui_text || {};
    var wf = settings.workflow || settings;
    
    response.commonAuthorsEnabled = !!wf.commonAuthorsEnabled;
    response.commonAuthorsList = wf.commonAuthorsList || "";
    response.commonAuthorsMessage = wf.commonAuthorsMessage || "";
    
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Config API Error", "error", String(err));
    return e.json(400, { message: String(err) });
  }
});


onBootstrap((e) => {
  e.next();
  require(`${__hooks}/../lib/config.js`).applyMailSettings(e.app);
});

cronAdd("asap-hold-check", $os.getenv("ASAP_CRON_SCHEDULE") || "0 * * * *", () => {
  require(`${__hooks}/../lib/jobs.js`).runScheduledHoldCheck($app);
});

cronAdd("asap-organization-sync", $os.getenv("ASAP_ORG_SYNC_CRON_SCHEDULE") || "0 2 * * *", () => {
  require(`${__hooks}/../lib/jobs.js`).runScheduledOrganizationSync($app);
});

cronAdd("asap-weekly-staff-action-summary", $os.getenv("ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE") || "0 20 * * 0", () => {
  require(`${__hooks}/../lib/jobs.js`).runWeeklyStaffActionSummary($app);
});

cronAdd("asap-isbn-check", $os.getenv("ASAP_ISBN_CHECK_CRON_SCHEDULE") || "*/5 * * * *", () => {
  const jobs = require(`${__hooks}/../lib/jobs.js`);
  const polaris = require(`${__hooks}/../lib/polaris.js`);
  const result = {
    holdsPlaced: 0,
    checkoutClosures: 0,
    holdPickupTimeouts: 0,
    promoted: 0,
    timedOut: 0,
    skipped: 0,
    isbnChecksFound: 0,
    isbnChecksNotFound: 0,
    errors: 0,
  };
  jobs.processPendingSuggestionIsbnChecks($app, polaris.adminStaffAuth(), result);
});

onRecordViewRequest((e) => {
  const authRecord = e.httpContext.get("authRecord");
  if (authRecord && authRecord.collection().name === "patron_users") {
    e.record.set("notes", "");
    e.record.set("editedBy", "");
    e.record.set("staffLibraryOrgIdCreatedBy", "");
  }
}, "title_requests");

onRecordsListRequest((e) => {
  const authRecord = e.httpContext.get("authRecord");
  if (authRecord && authRecord.collection().name === "patron_users") {
    e.records.forEach((record) => {
      record.set("notes", "");
      record.set("editedBy", "");
      record.set("staffLibraryOrgIdCreatedBy", "");
    });
  }
}, "title_requests");
