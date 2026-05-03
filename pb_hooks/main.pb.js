routerAdd("POST", "/api/asap/staff/login", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffLogin(e);
});

routerAdd("GET", "/api/asap/setup/status", (e) => {
  const setupRoutes = require(`${__hooks}/lib/setup_routes.js`);
  return setupRoutes.setupStatus(e);
});

routerAdd("POST", "/api/asap/setup", (e) => {
  const setupRoutes = require(`${__hooks}/lib/setup_routes.js`);
  return setupRoutes.initialSetup(e);
});

routerAdd("POST", "/api/asap/setup/test-polaris", (e) => {
  const setupRoutes = require(`${__hooks}/lib/setup_routes.js`);
  return setupRoutes.setupTestPolaris(e);
});

routerAdd("POST", "/api/asap/patron/login", (e) => {
  const patronRoutes = require(`${__hooks}/lib/patron_routes.js`);
  return patronRoutes.patronLogin(e);
});

routerAdd("POST", "/api/asap/patron/suggestions", (e) => {
  const patronRoutes = require(`${__hooks}/lib/patron_routes.js`);
  return patronRoutes.createSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/suggestions", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffCreateSuggestion(e);
});

routerAdd("POST", "/api/asap/staff/patron-lookup", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffLookupPatron(e);
});

routerAdd("POST", "/api/asap/staff/bib-lookup", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffBibLookup(e);
});

routerAdd("POST", "/api/asap/staff/test-polaris", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffTestPolaris(e);
});

routerAdd("POST", "/api/asap/staff/test-smtp", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffTestSmtp(e);
});

routerAdd("POST", "/api/asap/staff/profile", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffProfileUpdate(e);
});

routerAdd("GET", "/api/asap/staff/email-status", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffEmailStatus(e);
});

routerAdd("GET", "/api/asap/staff/users", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffUsersList(e);
});

routerAdd("POST", "/api/asap/staff/users", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffUserCreate(e);
});

routerAdd("POST", "/api/asap/staff/users/{id}/role", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffUserRoleUpdate(e);
});

routerAdd("DELETE", "/api/asap/staff/users/{id}", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffUserDelete(e);
});

routerAdd("GET", "/api/asap/staff/settings/library", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.getLibrarySettings(e);
});

routerAdd("POST", "/api/asap/staff/settings/library", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.updateLibrarySettings(e);
});

routerAdd("GET", "/api/asap/staff/title-requests", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffTitleRequestsList(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/action", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffTitleRequestAction(e);
});

routerAdd("DELETE", "/api/asap/staff/requests/{id}", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffDeleteClosedRequest(e);
});

routerAdd("POST", "/api/asap/staff/requests/delete-closed", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffDeleteClosedRequestsBulk(e);
});

routerAdd("POST", "/api/asap/staff/organizations/sync", (e) => {
  const staffRoutes = require(`${__hooks}/lib/staff_routes.js`);
  return staffRoutes.staffSyncOrganizations(e);
});

routerAdd("POST", "/api/asap/jobs/hold-check", (e) => {
  const jobRoutes = require(`${__hooks}/lib/job_routes.js`);
  return jobRoutes.runHoldCheck(e);
});

routerAdd("POST", "/api/asap/jobs/promoter-check", (e) => {
  const jobRoutes = require(`${__hooks}/lib/job_routes.js`);
  return jobRoutes.staffRunPromoterCheck(e);
});

routerAdd("POST", "/api/asap/jobs/weekly-staff-action-summary", (e) => {
  const jobRoutes = require(`${__hooks}/lib/job_routes.js`);
  return jobRoutes.runWeeklyStaffActionSummary(e);
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

  // Settings are seeded by the collapsed 1.0 migration into normalized collections.
});

cronAdd("asap-hold-check", $os.getenv("ASAP_CRON_SCHEDULE") || "0 * * * *", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
  jobs.runScheduledHoldCheck($app);
});

cronAdd("asap-organization-sync", $os.getenv("ASAP_ORG_SYNC_CRON_SCHEDULE") || "0 2 * * *", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
  jobs.runScheduledOrganizationSync($app);
});

cronAdd("asap-weekly-staff-action-summary", $os.getenv("ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE") || "0 20 * * 0", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
  jobs.runWeeklyStaffActionSummary($app);
});


cronAdd("asap-isbn-check", $os.getenv("ASAP_ISBN_CHECK_CRON_SCHEDULE") || "*/5 * * * *", () => {
  const jobs = require(`${__hooks}/lib/jobs.js`);
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
  jobs.processPendingSuggestionIsbnChecks($app, require(`${__hooks}/lib/polaris.js`).adminStaffAuth(), result);
});

onRecordViewRequest((e) => {
  const authRecord = e.httpContext.get("authRecord");
  if (authRecord && authRecord.collection().name === "patron_users") {
    // Redact internal/staff fields for patrons
    e.record.set("notes", "");
    e.record.set("editedBy", "");
    e.record.set("staffLibraryOrgIdCreatedBy", "");
  }
}, "title_requests");

onRecordsListRequest((e) => {
  const authRecord = e.httpContext.get("authRecord");
  if (authRecord && authRecord.collection().name === "patron_users") {
    // Redact internal/staff fields for patrons in list view
    e.records.forEach((record) => {
      record.set("notes", "");
      record.set("editedBy", "");
      record.set("staffLibraryOrgIdCreatedBy", "");
    });
  }
}, "title_requests");
