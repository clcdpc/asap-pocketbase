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

migrate((app) => {
  const staffUsers = app.findCollectionByNameOrId("staff_users");
  try {
    staffUsers.fields.add(new Field(field("weekly_action_summary_enabled", "bool")));
    app.save(staffUsers);
  } catch (err) {}
  try {
    staffUsers.fields.add(new Field(field("weekly_action_summary_email", "email")));
    app.save(staffUsers);
  } catch (err2) {}

  var scheduledEmailRuns;
  try {
    scheduledEmailRuns = app.findCollectionByNameOrId("scheduled_email_runs");
  } catch (err) {
    scheduledEmailRuns = new Collection({
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
    app.save(scheduledEmailRuns);
  }

  try {
    app.findCollectionByNameOrId("scheduled_email_deliveries");
  } catch (err) {
    app.save(new Collection({
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
    }));
  }
}, (app) => {
  return null;
});
