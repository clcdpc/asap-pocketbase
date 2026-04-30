/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function rel(name, collection, options) {
  options = options || {};
  options.collectionId = collection.id;
  options.maxSelect = options.maxSelect || 1;
  return field(name, "relation", options);
}

function saveCollection(app, data) {
  var collection = new Collection(data);
  app.save(collection);
  return collection;
}

function saveRecord(app, collection, id, values) {
  var record = new Record(collection);
  if (id) record.set("id", id);
  Object.keys(values || {}).forEach(function (key) {
    record.set(key, values[key]);
  });
  app.save(record);
  return record;
}

function seedLookup(app, collection, rows) {
  for (var i = 0; i < rows.length; i++) {
    saveRecord(app, collection, rows[i].id, rows[i]);
  }
}

migrate((app) => {
  const requestStatuses = saveCollection(app, {
    type: "base",
    name: "request_statuses",
    listRule: "",
    viewRule: "",
    fields: [
      field("code", "text", { required: true, max: 64 }),
      field("label", "text", { required: true, max: 128 }),
      field("sortOrder", "number", { required: true, onlyInt: true }),
      field("isOpen", "bool"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_request_statuses_code ON request_statuses (code)"]
  });

  const closeReasons = saveCollection(app, {
    type: "base",
    name: "request_close_reasons",
    listRule: "",
    viewRule: "",
    fields: [
      field("code", "text", { required: true, max: 64 }),
      field("label", "text", { required: true, max: 128 }),
      field("sortOrder", "number", { required: true, onlyInt: true }),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_request_close_reasons_code ON request_close_reasons (code)"]
  });

  const materialFormats = saveCollection(app, {
    type: "base",
    name: "material_formats",
    listRule: "",
    viewRule: "",
    fields: [
      field("code", "text", { required: true, max: 64 }),
      field("label", "text", { required: true, max: 128 }),
      field("enabled", "bool"),
      field("sortOrder", "number", { required: true, onlyInt: true }),
      field("messageBehavior", "select", { maxSelect: 1, values: ["none", "ebookMessage", "eaudiobookMessage"] }),
      field("titleMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] }),
      field("titleLabel", "text", { max: 128 }),
      field("authorMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] }),
      field("authorLabel", "text", { max: 128 }),
      field("identifierMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] }),
      field("identifierLabel", "text", { max: 128 }),
      field("audienceMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] }),
      field("audienceLabel", "text", { max: 128 }),
      field("publicationMode", "select", { maxSelect: 1, values: ["required", "optional", "hidden"] }),
      field("publicationLabel", "text", { max: 128 }),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_material_formats_code ON material_formats (code)"]
  });

  const audienceGroups = saveCollection(app, {
    type: "base",
    name: "audience_groups",
    listRule: "",
    viewRule: "",
    fields: [
      field("code", "text", { required: true, max: 64 }),
      field("label", "text", { required: true, max: 128 }),
      field("sortOrder", "number", { required: true, onlyInt: true }),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_audience_groups_code ON audience_groups (code)"]
  });

  const organizations = saveCollection(app, {
    type: "base",
    name: "polaris_organizations",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    fields: [
      field("organizationId", "text", { required: true, max: 32 }),
      field("organizationCodeId", "text", { max: 8 }),
      field("name", "text", { max: 256 }),
      field("abbreviation", "text", { max: 64 }),
      field("displayName", "text", { max: 256 }),
      field("parentOrganizationId", "text", { max: 32 }),
      field("enabledForPatrons", "bool"),
      field("raw", "json"),
      field("lastSynced", "date"),
    ],
    indexes: [
      "CREATE UNIQUE INDEX idx_polaris_organizations_org_id ON polaris_organizations (organizationId)",
      "CREATE INDEX idx_polaris_organizations_parent_id ON polaris_organizations (parentOrganizationId)",
      "CREATE INDEX idx_polaris_organizations_enabled ON polaris_organizations (enabledForPatrons)"
    ]
  });
  organizations.fields.add(new Field({
    name: "parentOrganization",
    type: "relation",
    collectionId: organizations.id,
    maxSelect: 1
  }));
  app.save(organizations);

  const staffUsers = saveCollection(app, {
    type: "auth",
    name: "staff_users",
    listRule: "id = @request.auth.id",
    viewRule: "id = @request.auth.id",
    passwordAuth: { enabled: false, identityFields: ["email"] },
    fields: [
      field("username", "text", { required: true, max: 128 }),
      field("displayName", "text", { max: 256 }),
      field("role", "select", { maxSelect: 1, values: ["staff", "admin", "super_admin"] }),
      field("active", "bool"),
      field("lastPolarisLogin", "date"),
      field("domain", "text", { max: 128 }),
      field("identityKey", "text", { max: 260 }),
      field("polarisUserId", "text", { max: 64 }),
      field("branchOrgId", "text", { max: 32 }),
      field("libraryOrgId", "text", { max: 32 }),
      field("libraryOrgName", "text", { max: 256 }),
      rel("branchOrganization", organizations),
      rel("libraryOrganization", organizations),
      field("scope", "select", { maxSelect: 1, values: ["library", "system"] }),
      field("lastOrgSync", "date"),
      field("weekly_action_summary_enabled", "bool"),
      field("weekly_action_summary_email", "email"),
    ],
    indexes: [
      "CREATE UNIQUE INDEX idx_staff_users_identity_key ON staff_users (identityKey)",
      "CREATE INDEX idx_staff_users_library_org ON staff_users (libraryOrgId)"
    ]
  });

  const patronUsers = saveCollection(app, {
    type: "auth",
    name: "patron_users",
    listRule: "id = @request.auth.id",
    viewRule: "id = @request.auth.id",
    passwordAuth: { enabled: false, identityFields: ["email"] },
    fields: [
      field("barcode", "text", { required: true, max: 64 }),
      field("nameFirst", "text", { max: 128 }),
      field("nameLast", "text", { max: 128 }),
      field("lastPolarisLogin", "date"),
      field("patronOrgId", "text", { max: 32 }),
      field("libraryOrgId", "text", { max: 32 }),
      field("libraryOrgName", "text", { max: 256 }),
      rel("patronOrganization", organizations),
      rel("libraryOrganization", organizations),
      field("lastOrgSync", "date"),
    ],
    indexes: [
      "CREATE UNIQUE INDEX idx_patron_users_barcode ON patron_users (barcode)",
      "CREATE INDEX idx_patron_users_library_org ON patron_users (libraryOrgId)"
    ]
  });

  const titleRequests = saveCollection(app, {
    type: "base",
    name: "title_requests",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (libraryOrgId != '' && libraryOrgId = @request.auth.libraryOrgId))",
    viewRule: "(@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || (libraryOrgId != '' && libraryOrgId = @request.auth.libraryOrgId))) || patron = @request.auth.id",
    fields: [
      rel("patron", patronUsers),
      rel("patronOrganization", organizations),
      rel("libraryOrganization", organizations),
      rel("staffLibraryOrganizationCreatedBy", organizations),
      rel("statusRef", requestStatuses, { required: true }),
      rel("formatRef", materialFormats),
      rel("audienceGroup", audienceGroups),
      rel("closeReasonRef", closeReasons),
      field("barcode", "text", { required: true, max: 64 }),
      field("email", "email"),
      field("nameFirst", "text", { max: 128 }),
      field("nameLast", "text", { max: 128 }),
      field("patronOrgId", "text", { max: 32 }),
      field("libraryOrgId", "text", { max: 32 }),
      field("libraryOrgName", "text", { max: 256 }),
      field("staffLibraryOrgIdCreatedBy", "text", { max: 32 }),
      field("title", "text", { required: true, max: 256 }),
      field("author", "text", { max: 256 }),
      field("identifier", "text", { max: 64 }),
      field("publication", "text", { max: 128 }),
      field("exactPublicationDate", "date"),
      field("autohold", "bool"),
      field("status", "text", { required: true, max: 64 }),
      field("agegroup", "text", { max: 64 }),
      field("format", "text", { max: 64 }),
      field("closeReason", "text", { max: 64 }),
      field("editedBy", "text", { max: 256 }),
      field("notes", "editor", { maxSize: 5000, convertURLs: false }),
      field("bibid", "text", { max: 128 }),
      field("legacyId", "number"),
      field("lastPromoterCheck", "date"),
      field("isbnCheckStatus", "select", { maxSelect: 1, values: ["pending", "found", "not_found", "error", "error_max_retries", "skipped_no_isbn", "found_in_polaris"] }),
      field("isbnCheckResult", "text"),
      field("isbnCheckRetryCount", "number", { onlyInt: true }),
      field("lastChecked", "date"),
      field("created", "date"),
      field("updated", "date"),
    ],
    indexes: [
      "CREATE INDEX idx_title_requests_status ON title_requests (status)",
      "CREATE INDEX idx_title_requests_status_ref ON title_requests (statusRef)",
      "CREATE INDEX idx_title_requests_barcode ON title_requests (barcode)",
      "CREATE INDEX idx_title_requests_identifier ON title_requests (identifier)",
      "CREATE INDEX idx_title_requests_legacy_id ON title_requests (legacyId)",
      "CREATE INDEX idx_title_requests_created ON title_requests (created)",
      "CREATE INDEX idx_title_requests_close_reason ON title_requests (closeReason)",
      "CREATE INDEX idx_title_requests_library_org_status ON title_requests (libraryOrgId, status)",
      "CREATE INDEX idx_title_requests_library_org_created ON title_requests (libraryOrgId, created)"
    ]
  });

  const workflowTags = saveCollection(app, {
    type: "base",
    name: "workflow_tags",
    listRule: "@request.auth.collectionName = 'staff_users'",
    viewRule: "@request.auth.collectionName = 'staff_users'",
    fields: [
      field("code", "text", { required: true, max: 128 }),
      field("label", "text", { required: true, max: 128 }),
      field("description", "text"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_workflow_tags_code ON workflow_tags (code)"]
  });

  saveCollection(app, {
    type: "base",
    name: "title_request_tags",
    listRule: "@request.auth.collectionName = 'staff_users'",
    viewRule: "@request.auth.collectionName = 'staff_users'",
    fields: [
      rel("titleRequest", titleRequests, { required: true }),
      rel("tag", workflowTags, { required: true }),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_title_request_tags_pair ON title_request_tags (titleRequest, tag)"]
  });

  const emailTemplates = saveCollection(app, {
    type: "base",
    name: "email_templates",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    fields: [
      field("scope", "select", { maxSelect: 1, values: ["system", "library"] }),
      rel("libraryOrganization", organizations),
      field("templateKey", "text", { required: true, max: 64 }),
      field("name", "text", { max: 128 }),
      field("subject", "text"),
      field("body", "editor", { maxSize: 20000, convertURLs: false }),
      field("fromAddress", "email"),
      field("fromName", "text", { max: 128 }),
      field("enabled", "bool"),
    ],
    indexes: [
      "CREATE UNIQUE INDEX idx_email_templates_scope_key ON email_templates (scope, libraryOrganization, templateKey)"
    ]
  });

  const rejectionTemplates = saveCollection(app, {
    type: "base",
    name: "rejection_templates",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    fields: [
      field("scope", "select", { maxSelect: 1, values: ["system", "library"] }),
      rel("libraryOrganization", organizations),
      field("name", "text", { required: true, max: 128 }),
      field("subject", "text"),
      field("body", "editor", { maxSize: 20000, convertURLs: false }),
      field("enabled", "bool"),
      field("sortOrder", "number", { onlyInt: true }),
    ]
  });

  const systemSettings = saveCollection(app, {
    type: "base",
    name: "system_settings",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    updateRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      field("settingsKey", "text", { required: true, max: 64 }),
      field("allowedStaffUsers", "text"),
      field("staffUrl", "text", { max: 2048 }),
      rel("enabledLibraries", organizations, { maxSelect: 999 }),
      field("organizationsSyncStatus", "select", { maxSelect: 1, values: ["not_loaded", "loading", "loaded", "error"] }),
      field("organizationsLastSynced", "date"),
      field("organizationsSyncMessage", "text"),
      field("organizationsSyncError", "text"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_system_settings_key ON system_settings (settingsKey)"]
  });

  const polarisSettings = saveCollection(app, {
    type: "base",
    name: "polaris_settings",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    updateRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      field("settingsKey", "text", { required: true, max: 64 }),
      field("host", "text"),
      field("accessId", "text"),
      field("apiKey", "text"),
      field("staffDomain", "text"),
      field("adminUser", "text"),
      field("adminPassword", "text"),
      field("overridePassword", "text"),
      field("langId", "text"),
      field("appId", "text"),
      field("orgId", "text"),
      field("pickupOrgId", "text"),
      field("requestingOrgId", "text"),
      field("workstationId", "text"),
      field("userId", "text"),
      field("autoPromote", "bool"),
      field("firstSuccessfulSaveAt", "date"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_polaris_settings_key ON polaris_settings (settingsKey)"]
  });

  const smtpSettings = saveCollection(app, {
    type: "base",
    name: "smtp_settings",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    updateRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      field("settingsKey", "text", { required: true, max: 64 }),
      field("host", "text"),
      field("port", "number", { onlyInt: true }),
      field("username", "text"),
      field("password", "text"),
      field("tls", "bool"),
      field("fromAddress", "email"),
      field("fromName", "text"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_smtp_settings_key ON smtp_settings (settingsKey)"]
  });

  const workflowSettings = saveCollection(app, {
    type: "base",
    name: "workflow_settings",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    fields: [
      field("scope", "select", { maxSelect: 1, values: ["system", "library"] }),
      rel("libraryOrganization", organizations),
      field("suggestionLimit", "number", { onlyInt: true }),
      field("suggestionLimitMessage", "text"),
      field("outstandingTimeoutEnabled", "bool"),
      field("outstandingTimeoutDays", "number", { onlyInt: true }),
      field("outstandingTimeoutSendEmail", "bool"),
      rel("outstandingTimeoutRejectionTemplate", rejectionTemplates),
      field("holdPickupTimeoutEnabled", "bool"),
      field("holdPickupTimeoutDays", "number", { onlyInt: true }),
      field("pendingHoldTimeoutEnabled", "bool"),
      field("pendingHoldTimeoutDays", "number", { onlyInt: true }),
      field("commonAuthorsEnabled", "bool"),
      field("commonAuthorsList", "text"),
      field("commonAuthorsMessage", "text"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_workflow_settings_scope ON workflow_settings (scope, libraryOrganization)"]
  });

  const uiSettings = saveCollection(app, {
    type: "base",
    name: "ui_settings",
    listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
    fields: [
      field("scope", "select", { maxSelect: 1, values: ["system", "library"] }),
      rel("libraryOrganization", organizations),
      field("logo", "file", { maxSelect: 1, maxSize: 5242880, mimeTypes: ["image/jpeg", "image/png", "image/svg+xml", "image/gif"] }),
      field("logoAlt", "text"),
      field("pageTitle", "text"),
      field("barcodeLabel", "text"),
      field("pinLabel", "text"),
      field("loginPrompt", "editor", { maxSize: 10000, convertURLs: false }),
      field("loginNote", "editor", { maxSize: 10000, convertURLs: false }),
      field("suggestionFormNote", "editor", { maxSize: 10000, convertURLs: false }),
      field("successTitle", "text"),
      field("successMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("alreadySubmittedMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("duplicateLabelSuggestion", "text"),
      field("duplicateLabelOutstandingPurchase", "text"),
      field("duplicateLabelPendingHold", "text"),
      field("duplicateLabelHoldPlaced", "text"),
      field("duplicateLabelClosed", "text"),
      field("duplicateLabelRejected", "text"),
      field("duplicateLabelHoldCompleted", "text"),
      field("duplicateLabelHoldNotPickedUp", "text"),
      field("duplicateLabelManual", "text"),
      field("duplicateLabelSilent", "text"),
      field("noEmailMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("systemNotEnabledMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("ebookMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("eaudiobookMessage", "editor", { maxSize: 10000, convertURLs: false }),
      field("publicationOptions", "text"),
      field("ageGroups", "text"),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_ui_settings_scope ON ui_settings (scope, libraryOrganization)"]
  });

  saveCollection(app, {
    type: "base",
    name: "title_request_events",
    listRule: "@request.auth.collectionName = 'staff_users'",
    viewRule: "@request.auth.collectionName = 'staff_users'",
    fields: [
      rel("titleRequest", titleRequests, { required: true }),
      field("eventType", "text", { required: true, max: 64 }),
      rel("fromStatus", requestStatuses),
      rel("toStatus", requestStatuses),
      rel("closeReason", closeReasons),
      field("actorType", "text", { max: 64 }),
      field("actorName", "text", { max: 256 }),
      field("message", "text"),
      field("metadata", "json"),
    ],
    indexes: ["CREATE INDEX idx_title_request_events_request ON title_request_events (titleRequest)"]
  });

  saveCollection(app, {
    type: "base",
    name: "email_delivery_events",
    listRule: "@request.auth.collectionName = 'staff_users'",
    viewRule: "@request.auth.collectionName = 'staff_users'",
    fields: [
      rel("titleRequest", titleRequests),
      rel("emailTemplate", emailTemplates),
      field("templateKey", "text", { max: 64 }),
      field("recipient", "email"),
      field("subject", "text"),
      field("status", "select", { maxSelect: 1, values: ["sent", "skipped", "failed"] }),
      field("error", "text"),
      field("metadata", "json"),
    ],
    indexes: ["CREATE INDEX idx_email_delivery_events_request ON email_delivery_events (titleRequest)"]
  });

  saveCollection(app, {
    type: "base",
    name: "job_runs",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      field("jobName", "text", { required: true, max: 128 }),
      field("status", "select", { maxSelect: 1, values: ["running", "success", "failed"] }),
      field("startedAt", "date"),
      field("finishedAt", "date"),
      field("summary", "json"),
      field("error", "text"),
    ],
    indexes: ["CREATE INDEX idx_job_runs_name ON job_runs (jobName)"]
  });

  const scheduledEmailRuns = saveCollection(app, {
    type: "base",
    name: "scheduled_email_runs",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      field("job_key", "text", { required: true, max: 128 }),
      field("period_start", "date"),
      field("period_end", "date"),
      field("started_at", "date"),
      field("completed_at", "date"),
      field("status", "select", { maxSelect: 1, values: ["running", "success", "partial_failure", "failed", "skipped"] }),
      field("error", "text"),
      field("recipient_count", "number", { onlyInt: true }),
    ],
    indexes: ["CREATE UNIQUE INDEX idx_scheduled_email_runs_job_key ON scheduled_email_runs (job_key)"]
  });

  saveCollection(app, {
    type: "base",
    name: "scheduled_email_deliveries",
    listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
    fields: [
      rel("run", scheduledEmailRuns, { required: true }),
      rel("staff_user", staffUsers),
      field("email", "email"),
      field("status", "select", { maxSelect: 1, values: ["sent", "failed", "skipped"] }),
      field("error", "text"),
      field("sent_at", "date"),
    ],
    indexes: [
      "CREATE INDEX idx_scheduled_email_deliveries_run ON scheduled_email_deliveries (run)",
      "CREATE INDEX idx_scheduled_email_deliveries_staff ON scheduled_email_deliveries (staff_user)"
    ]
  });

  seedLookup(app, requestStatuses, [
    { id: "rqstsuggest0001", code: "suggestion", label: "Suggestions", sortOrder: 10, isOpen: true },
    { id: "rqstoutpur00020", code: "outstanding_purchase", label: "Pending Purchase", sortOrder: 20, isOpen: true },
    { id: "rqstpendhold030", code: "pending_hold", label: "Pending Hold", sortOrder: 30, isOpen: true },
    { id: "rqstholdplc0400", code: "hold_placed", label: "Hold Placed", sortOrder: 40, isOpen: true },
    { id: "rqstclosed00050", code: "closed", label: "Closed", sortOrder: 50, isOpen: false },
  ]);

  seedLookup(app, closeReasons, [
    { id: "closereject0010", code: "rejected", label: "Rejected by staff", sortOrder: 10 },
    { id: "closeheldone020", code: "hold_completed", label: "Hold placed / completed", sortOrder: 20 },
    { id: "closemanual0030", code: "manual", label: "Manually closed", sortOrder: 30 },
    { id: "closesilent0040", code: "Silently Closed", label: "Silently closed", sortOrder: 40 },
    { id: "closenotpick050", code: "hold_not_picked_up", label: "Hold not picked up", sortOrder: 50 },
  ]);

  seedLookup(app, materialFormats, [
    { id: "fmtbook00000010", code: "book", label: "Book", enabled: true, sortOrder: 10, messageBehavior: "none", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Author", identifierMode: "optional", identifierLabel: "ISBN", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
    { id: "fmtaudiocd00200", code: "audiobook_cd", label: "Audiobook (Physical CD)", enabled: true, sortOrder: 20, messageBehavior: "none", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Author", identifierMode: "optional", identifierLabel: "ISBN", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
    { id: "fmtdvd000000300", code: "dvd", label: "DVD", enabled: true, sortOrder: 30, messageBehavior: "none", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Director/Actors/Producer", identifierMode: "hidden", identifierLabel: "UPC", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
    { id: "fmtmusiccd00400", code: "music_cd", label: "Music CD", enabled: true, sortOrder: 40, messageBehavior: "none", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Artist", identifierMode: "hidden", identifierLabel: "UPC", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
    { id: "fmtebook0000500", code: "ebook", label: "eBook", enabled: true, sortOrder: 50, messageBehavior: "ebookMessage", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Author", identifierMode: "optional", identifierLabel: "ISBN", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
    { id: "fmteaudio006000", code: "eaudiobook", label: "eAudiobook", enabled: true, sortOrder: 60, messageBehavior: "eaudiobookMessage", titleMode: "required", titleLabel: "Title", authorMode: "required", authorLabel: "Author", identifierMode: "optional", identifierLabel: "ISBN", audienceMode: "required", audienceLabel: "Age Group", publicationMode: "required", publicationLabel: "Publication Timing" },
  ]);

  seedLookup(app, audienceGroups, [
    { id: "audadult0000100", code: "adult", label: "Adult", sortOrder: 10 },
    { id: "audteen00000200", code: "teen", label: "Young Adult / Teen", sortOrder: 20 },
    { id: "audchild0000300", code: "children", label: "Children", sortOrder: 30 },
  ]);

  seedLookup(app, workflowTags, [
    { id: "tagpolarfound10", code: "dupe found in Polaris", label: "Dupe found in Polaris", description: "ISBN/BIB lookup found a Polaris bibliographic record." },
    { id: "tagpolarnot0200", code: "ISBN not found in system", label: "ISBN not found in system", description: "ISBN/BIB lookup did not find a Polaris bibliographic record." },
  ]);

  saveRecord(app, systemSettings, "settings0000001", {
    settingsKey: "system",
    allowedStaffUsers: "",
    staffUrl: "",
    enabledLibraries: [],
    organizationsSyncStatus: "not_loaded",
    organizationsSyncMessage: "Polaris organizations have not been loaded yet.",
  });

  saveRecord(app, polarisSettings, "polaris00000010", {
    settingsKey: "system",
    host: "",
    accessId: "SuggestAPI",
    apiKey: "",
    staffDomain: "",
    adminUser: "",
    adminPassword: "",
    overridePassword: "admin",
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: "1",
    userId: "1",
    autoPromote: true,
  });

  saveRecord(app, smtpSettings, "smtp00000000100", {
    settingsKey: "system",
    host: "",
    port: 587,
    username: "",
    password: "",
    tls: true,
    fromAddress: "",
    fromName: "Library Collection Development",
  });

  saveRecord(app, workflowSettings, "workflow0000010", {
    scope: "system",
    suggestionLimit: 5,
    suggestionLimitMessage: "Weekly suggestion limit reached. You can try again after {{next_available_date}}.",
    outstandingTimeoutEnabled: false,
    outstandingTimeoutDays: 30,
    outstandingTimeoutSendEmail: false,
    holdPickupTimeoutEnabled: false,
    holdPickupTimeoutDays: 14,
    pendingHoldTimeoutEnabled: false,
    pendingHoldTimeoutDays: 14,
    commonAuthorsEnabled: false,
    commonAuthorsList: "",
    commonAuthorsMessage: "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items.",
  });

  saveRecord(app, uiSettings, "uisettings00010", {
    scope: "system",
    logoAlt: "Library Logo",
    pageTitle: "Material Suggestion",
    barcodeLabel: "Library Card",
    pinLabel: "Pin",
    loginPrompt: "Please enter your information below to start the suggestion process.",
    suggestionFormNote: "If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don't see the email.",
    loginNote: "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.",
    successTitle: "Suggestion Submitted",
    successMessage: "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
    alreadySubmittedMessage: "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>",
    duplicateLabelSuggestion: "Received",
    duplicateLabelOutstandingPurchase: "Under review",
    duplicateLabelPendingHold: "Being prepared",
    duplicateLabelHoldPlaced: "Hold placed",
    duplicateLabelClosed: "Completed",
    duplicateLabelRejected: "Not selected for purchase",
    duplicateLabelHoldCompleted: "Completed",
    duplicateLabelHoldNotPickedUp: "Closed",
    duplicateLabelManual: "Closed",
    duplicateLabelSilent: "Closed",
    noEmailMessage: "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
    systemNotEnabledMessage: "Your library does not currently participate in this suggestion service.",
    ebookMessage: "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    eaudiobookMessage: "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    publicationOptions: "Already published\nComing soon\nPublished a while back",
    ageGroups: "Adult\nYoung Adult / Teen\nChildren",
  });

  saveRecord(app, emailTemplates, "emailsubmit0010", { scope: "system", templateKey: "suggestion_submitted", name: "Submission confirmation", subject: "Suggestion received: {{title}}", body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.", enabled: true });
  saveRecord(app, emailTemplates, "emailowned00020", { scope: "system", templateKey: "already_owned", name: "Already owned", subject: "{{title}} is already available", body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library's suggestion service.", enabled: true });
  saveRecord(app, emailTemplates, "emailreject0030", { scope: "system", templateKey: "rejected", name: "Rejected", subject: "Update on your suggestion: {{title}}", body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community.", enabled: true });
  saveRecord(app, emailTemplates, "emailhold000040", { scope: "system", templateKey: "hold_placed", name: "Hold placed", subject: "Hold placed for {{title}}", body: "Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.", enabled: true });
}, (app) => {
  return null;
});
