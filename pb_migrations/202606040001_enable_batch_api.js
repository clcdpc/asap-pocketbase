/// <reference path="../pb_data/types.d.ts" />

migrate((app) => {
  const settings = app.settings();
  settings.batch.enabled = true;
  if (!settings.batch.maxRequests) settings.batch.maxRequests = 100;
  if (!settings.batch.timeout) settings.batch.timeout = 60;
  app.save(settings);
}, (app) => {
  const settings = app.settings();
  settings.batch.enabled = false;
  app.save(settings);
});
