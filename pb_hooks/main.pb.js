// Routes and handlers
// Note: Library files are located in ../lib/ to prevent the macOS file watcher
// from triggering infinite restart loops on every require() access.

routerAdd("POST", "/api/asap/staff/login", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffLogin(e);
});

require(`${__hooks}/../lib/route_registry.js`).registerRoutes([
  { method: "GET", path: "/api/asap/setup/status", module: "setup_routes.js", handler: "setupStatus" },
  { method: "POST", path: "/api/asap/setup", module: "setup_routes.js", handler: "initialSetup" },
  { method: "POST", path: "/api/asap/setup/test-polaris", module: "setup_routes.js", handler: "setupTestPolaris" },
  { method: "POST", path: "/api/asap/patron/login", module: "patron_routes.js", handler: "patronLogin" },
  { method: "POST", path: "/api/asap/patron/suggestions", module: "patron_routes.js", handler: "createSuggestion" },
  { method: "POST", path: "/api/asap/staff/suggestions", module: "staff_routes.js", handler: "staffCreateSuggestion" },
  { method: "POST", path: "/api/asap/staff/patron-lookup", module: "staff_routes.js", handler: "staffLookupPatron" },
  { method: "POST", path: "/api/asap/staff/bib-lookup", module: "staff_routes.js", handler: "staffBibLookup" },
  { method: "POST", path: "/api/asap/staff/test-polaris", module: "staff_routes.js", handler: "staffTestPolaris" },
  { method: "POST", path: "/api/asap/staff/test-smtp", module: "staff_routes.js", handler: "staffTestSmtp" },
  { method: "POST", path: "/api/asap/staff/profile", module: "staff_routes.js", handler: "staffProfileUpdate" },
  { method: "GET", path: "/api/asap/staff/email-status", module: "staff_routes.js", handler: "staffEmailStatus" },
  { method: "GET", path: "/api/asap/staff/users", module: "staff_routes.js", handler: "staffUsersList" },
  { method: "POST", path: "/api/asap/staff/users", module: "staff_routes.js", handler: "staffUserCreate" },
  { method: "POST", path: "/api/asap/staff/users/{id}/role", module: "staff_routes.js", handler: "staffUserRoleUpdate" },
  { method: "DELETE", path: "/api/asap/staff/users/{id}", module: "staff_routes.js", handler: "staffUserDelete" },
  { method: "GET", path: "/api/asap/staff/settings/library", module: "staff_routes.js", handler: "getLibrarySettings" },
  { method: "POST", path: "/api/asap/staff/settings/library", module: "staff_routes.js", handler: "updateLibrarySettings" },
  { method: "GET", path: "/api/asap/staff/settings/overrides-summary", module: "staff_routes.js", handler: "getLibraryOverridesSummary" },
  { method: "POST", path: "/api/asap/staff/settings/logo", module: "staff_routes.js", handler: "staffSaveLogo" },
  { method: "DELETE", path: "/api/asap/staff/settings/logo", module: "staff_routes.js", handler: "staffResetLogo" },
  { method: "GET", path: "/api/asap/staff/title-requests", module: "staff_routes.js", handler: "staffTitleRequestsList" },
  { method: "GET", path: "/api/asap/staff/additional-copies", module: "staff_routes.js", handler: "staffAdditionalCopiesList" },
  { method: "POST", path: "/api/asap/staff/additional-copies/{id}/close", module: "staff_routes.js", handler: "staffAdditionalCopyClose" },
  { method: "POST", path: "/api/asap/staff/additional-copies/{id}/reopen", module: "staff_routes.js", handler: "staffAdditionalCopyReopen" },
  { method: "POST", path: "/api/asap/staff/additional-copies/{id}/claim", module: "staff_routes.js", handler: "staffAdditionalCopyClaim" },
  { method: "POST", path: "/api/asap/staff/additional-copies/{id}/unclaim", module: "staff_routes.js", handler: "staffAdditionalCopyUnclaim" },
  { method: "GET", path: "/api/asap/staff/analytics", module: "staff_routes.js", handler: "staffAnalytics" },
  { method: "POST", path: "/api/asap/staff/title-requests/{id}/claim", module: "staff_routes.js", handler: "staffClaimTitleRequest" },
  { method: "POST", path: "/api/asap/staff/title-requests/{id}/unclaim", module: "staff_routes.js", handler: "staffUnclaimTitleRequest" },
  { method: "POST", path: "/api/asap/staff/title-requests/{id}/action", module: "staff_routes.js", handler: "staffTitleRequestAction" },
  { method: "GET", path: "/api/asap/staff/title-requests/{id}/additional-copy", module: "staff_routes.js", handler: "staffTitleRequestAdditionalCopyPreview" },
  { method: "POST", path: "/api/asap/staff/title-requests/{id}/additional-copy", module: "staff_routes.js", handler: "staffTitleRequestAdditionalCopyCreate" },
  { method: "DELETE", path: "/api/asap/staff/requests/{id}", module: "staff_routes.js", handler: "staffDeleteClosedRequest" },
  { method: "POST", path: "/api/asap/staff/requests/delete-closed", module: "staff_routes.js", handler: "staffDeleteClosedRequestsBulk" },
  { method: "POST", path: "/api/asap/staff/organizations/sync", module: "staff_routes.js", handler: "staffSyncOrganizations" },
  { method: "POST", path: "/api/asap/staff/material-types/sync", module: "staff_routes.js", handler: "staffMaterialTypesSync" },
  {
    method: "GET",
    path: "/api/asap/staff/material-types/sync",
    customHandler: function (e) {
      return e.json(405, { message: "Method Not Allowed. Use POST to sync material types." });
    }
  },
  { method: "POST", path: "/api/asap/jobs/hold-check", module: "job_routes.js", handler: "runHoldCheck" },
  { method: "POST", path: "/api/asap/jobs/promoter-check", module: "job_routes.js", handler: "staffRunPromoterCheck" },
  { method: "POST", path: "/api/asap/jobs/weekly-staff-action-summary", module: "job_routes.js", handler: "runWeeklyStaffActionSummary" },
  { method: "GET", path: "/api/asap/config", module: "config_routes.js", handler: "publicConfig" }
]);

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
  e.next();
  try {
    const authRecord = e.httpContext.get("authRecord");
    if (authRecord && authRecord.collection && authRecord.collection().name === "patron_users") {
      if (e.record) {
        e.record.set("notes", "");
        e.record.set("editedBy", "");
        e.record.set("staffLibraryOrgIdCreatedBy", "");
      }
    }
  } catch (err) {
    e.app.logger().error("Record view hook error", "error", String(err));
  }
}, "title_requests");

onRecordsListRequest((e) => {
  e.next();
  try {
    const authRecord = e.httpContext.get("authRecord");
    if (authRecord && authRecord.collection && authRecord.collection().name === "patron_users") {
      (e.records || []).forEach((record) => {
        if (record) {
          record.set("notes", "");
          record.set("editedBy", "");
          record.set("staffLibraryOrgIdCreatedBy", "");
        }
      });
    }
  } catch (err) {
    e.app.logger().error("Records list hook error", "error", String(err));
  }
}, "title_requests");
