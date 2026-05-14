function startJobRun(app, name) {
  try {
    var record = new Record(app.findCollectionByNameOrId("job_runs"));
    record.set("jobName", name);
    record.set("status", "running");
    record.set("startedAt", new Date().toISOString());
    app.save(record);
    return record;
  } catch (err) {
    return null;
  }
}

function finishJobRun(app, record, status, summary, error) {
  if (!record) return;
  try {
    record.set("status", status);
    record.set("finishedAt", new Date().toISOString());
    record.set("summary", summary || {});
    record.set("error", error || "");
    app.save(record);
  } catch (err) { }
}

module.exports = {
  startJobRun: startJobRun,
  finishJobRun: finishJobRun,
};
