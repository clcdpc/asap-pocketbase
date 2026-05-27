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

routerAdd("GET", "/api/asap/staff/settings/overrides-summary", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).getLibraryOverridesSummary(e);
});


routerAdd("POST", "/api/asap/staff/settings/logo", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffSaveLogo(e);
});

routerAdd("DELETE", "/api/asap/staff/settings/logo", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffResetLogo(e);
});

routerAdd("GET", "/api/asap/staff/title-requests", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestsList(e);
});

routerAdd("GET", "/api/asap/staff/additional-copies", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAdditionalCopiesList(e);
});

routerAdd("POST", "/api/asap/staff/additional-copies/{id}/close", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAdditionalCopyClose(e);
});

routerAdd("POST", "/api/asap/staff/additional-copies/{id}/reopen", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAdditionalCopyReopen(e);
});

routerAdd("POST", "/api/asap/staff/additional-copies/{id}/claim", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAdditionalCopyClaim(e);
});

routerAdd("POST", "/api/asap/staff/additional-copies/{id}/unclaim", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAdditionalCopyUnclaim(e);
});

routerAdd("GET", "/api/asap/staff/analytics", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffAnalytics(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/claim", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffClaimTitleRequest(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/unclaim", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffUnclaimTitleRequest(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/action", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestAction(e);
});

routerAdd("GET", "/api/asap/staff/title-requests/{id}/additional-copy", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestAdditionalCopyPreview(e);
});

routerAdd("POST", "/api/asap/staff/title-requests/{id}/additional-copy", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffTitleRequestAdditionalCopyCreate(e);
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

routerAdd("POST", "/api/asap/staff/material-types/sync", (e) => {
  return require(`${__hooks}/../lib/staff_routes.js`).staffMaterialTypesSync(e);
});

routerAdd("GET", "/api/asap/staff/material-types/sync", function (e) {
  return e.json(405, { message: "Method Not Allowed. Use POST to sync material types." });
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
  return require(`${__hooks}/../lib/config_routes.js`).publicConfig(e);
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
