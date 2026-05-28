const helpers = require("./records/helpers.js");

function clean(value) {
  return String(value || "").trim();
}

function createPatronSessionContext(app, patronRecord, context) {
  context = context || {};
  var collection = app.findCollectionByNameOrId("patron_session_contexts");
  var record = new Record(collection);
  helpers.setRelation(record, "patron", patronRecord);
  record.set("patronUserId", patronRecord.id || "");
  record.set("experienceLibraryOrgId", clean(context.experienceLibraryOrgId));
  record.set("experienceLibraryOrgName", clean(context.experienceLibraryOrgName));
  record.set("effectiveLibraryOrgId", clean(context.effectiveLibraryOrgId));
  record.set("effectiveLibraryOrgName", clean(context.effectiveLibraryOrgName));
  record.set("patronHomeLibraryOrgId", clean(context.patronHomeLibraryOrgId));
  record.set("patronHomeLibraryOrgName", clean(context.patronHomeLibraryOrgName));
  var now = new Date().toISOString();
  record.set("created", now);
  record.set("updated", now);
  app.save(record);
  return record;
}

function contextPayload(record) {
  if (!record) return null;
  return {
    id: record.id || "",
    patronUserId: clean(record.get("patronUserId")),
    experienceLibraryOrgId: clean(record.get("experienceLibraryOrgId")),
    experienceLibraryOrgName: clean(record.get("experienceLibraryOrgName")),
    effectiveLibraryOrgId: clean(record.get("effectiveLibraryOrgId")),
    effectiveLibraryOrgName: clean(record.get("effectiveLibraryOrgName")),
    patronHomeLibraryOrgId: clean(record.get("patronHomeLibraryOrgId")),
    patronHomeLibraryOrgName: clean(record.get("patronHomeLibraryOrgName")),
    expiresAt: clean(record.get("expiresAt"))
  };
}

function patronMatches(record, patronRecord) {
  var patronId = patronRecord && patronRecord.id ? String(patronRecord.id) : "";
  if (!patronId) return false;
  var textId = clean(record.get("patronUserId"));
  if (textId && textId === patronId) return true;
  var relation = record.get("patron");
  if (String(relation || "") === patronId) return true;
  if (Array.isArray(relation) && relation.indexOf(patronId) >= 0) return true;
  return false;
}

function isExpired(record) {
  var expiresAt = clean(record.get("expiresAt"));
  if (!expiresAt) return false;
  var date = new Date(expiresAt.replace(" ", "T"));
  return !isNaN(date.getTime()) && date.getTime() < Date.now();
}

function getPatronSessionContext(app, patronRecord, contextId) {
  contextId = clean(contextId);
  if (!contextId) return null;
  var record = app.findRecordById("patron_session_contexts", contextId);
  if (!patronMatches(record, patronRecord)) {
    var mismatch = new Error("Patron session context does not match the authenticated patron.");
    mismatch.code = 403;
    throw mismatch;
  }
  if (isExpired(record)) {
    var expired = new Error("Your session has expired. Please log in again.");
    expired.code = 401;
    throw expired;
  }
  var payload = contextPayload(record);
  if (!payload.effectiveLibraryOrgId) {
    var invalid = new Error("Your library could not be determined. Please log out and log back in before submitting a suggestion.");
    invalid.code = 403;
    throw invalid;
  }
  return payload;
}

module.exports = {
  createPatronSessionContext: createPatronSessionContext,
  getPatronSessionContext: getPatronSessionContext,
  contextPayload: contextPayload,
};
