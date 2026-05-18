const dbHelpers = require("./db_helpers.js");
const systemRecord = dbHelpers.systemRecord;

function smtpFromRecord(record) {
  return {
    host: String(record && record.get ? record.get("host") || "" : "").trim(),
    port: parseInt(record && record.get ? record.get("port") : 587, 10) || 587,
    username: String(record && record.get ? record.get("username") || "" : "").trim(),
    password: record && record.get ? record.get("password") || "" : "",
    tls: record && record.getBool ? record.getBool("tls") !== false : true,
  };
}

function smtpPublicFromRecord(record) {
  return {
    enabled: !!String(record && record.get ? record.get("host") || "" : "").trim(),
    host: String(record && record.get ? record.get("host") || "" : "").trim(),
    port: parseInt(record && record.get ? record.get("port") : 587, 10) || 587,
    tls: record && record.getBool ? record.getBool("tls") !== false : true,
    fromAddress: String(record && record.get ? record.get("fromAddress") || "" : "").trim(),
    fromName: String(record && record.get ? record.get("fromName") || "" : "").trim(),
    usernameSet: !!String(record && record.get ? record.get("username") || "" : "").trim(),
    passwordSet: !!String(record && record.get ? record.get("password") || "" : "").trim()
  };
}

function getSmtpSettings(app) {
  app = app || $app;
  return systemRecord(app, "smtp_settings", "smtp00000000100", {
    settingsKey: "system",
    port: 587,
    tls: true,
    fromName: "Library Collection Development"
  });
}

function mail() {
  return smtpFromRecord(getSmtpSettings($app));
}

function applyMailSettings(app) {
  const emailsMod = require("./emails.js");
  var cfg = smtpFromRecord(getSmtpSettings(app || $app));
  var e = emailsMod.emailsFor(app || $app, "");
  var settings = app.settings();
  if (e.fromAddress) {
    settings.meta.senderAddress = e.fromAddress;
    settings.meta.senderName = e.fromName || "Library Collection Development";
  }
  if (cfg.host) {
    settings.smtp.enabled = true;
    settings.smtp.host = cfg.host;
    settings.smtp.port = cfg.port;
    settings.smtp.username = cfg.username;
    settings.smtp.password = cfg.password;
    settings.smtp.tls = cfg.tls;
    settings.smtp.authMethod = cfg.username ? "LOGIN" : "";
  }
  app.save(settings);
}

module.exports = {
  smtpFromRecord: smtpFromRecord,
  smtpPublicFromRecord: smtpPublicFromRecord,
  getSmtpSettings: getSmtpSettings,
  mail: mail,
  applyMailSettings: applyMailSettings,
};
