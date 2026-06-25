const config = require(`${__hooks}/../lib/config.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const splitList = require(`${__hooks}/../lib/split-list.js`);

function resolveEffectiveStaffLibraryContext(e, staff, data) {
  data = data || {};
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  var role = String(staff.get("role") || "").toLowerCase();
  var requestedOrgId = String(data.libraryOrgId || data.effectiveLibraryOrgId || "").trim();
  var effectiveLibraryOrgId = (role === "super_admin" && requestedOrgId) ? requestedOrgId : staffLibraryOrgId;
  var effectiveLibraryOrgName = "";
  if (effectiveLibraryOrgId) {
    var org = orgs.findOrganization ? orgs.findOrganization(e.app, effectiveLibraryOrgId) : null;
    effectiveLibraryOrgName = org ? String(org.get("displayName") || org.get("name") || "") : "";
  }
  if (!effectiveLibraryOrgName && effectiveLibraryOrgId === staffLibraryOrgId) {
    effectiveLibraryOrgName = String(staff.get("libraryOrgName") || "");
  }
  return { libraryOrgId: effectiveLibraryOrgId, libraryOrgName: effectiveLibraryOrgName };
}

function allowCrossLibraryPatronLookup(e, effectiveLibraryOrgId) {
  if (!effectiveLibraryOrgId) return false;
  var wf = config.workflowSettings ? config.workflowSettings(e.app, effectiveLibraryOrgId) : {};
  return !!wf.allowAnyRegisteredCardLogin;
}

function patronMatchesStaffLookupScope(staff, patronData, effectiveLibraryOrgId, allowAnyRegisteredCardLogin) {
  if (allowAnyRegisteredCardLogin) return true;
  return String(patronData && patronData.LibraryOrgID || "").trim() === String(effectiveLibraryOrgId || staff.get("libraryOrgId") || "").trim();
}

function staffPatronLookupScopeMeta(e, effectiveLibraryOrgId, effectiveLibraryOrgName, allowAnyRegisteredCardLogin) {
  return {
    patronSearchScope: allowAnyRegisteredCardLogin ? "system" : "library",
    patronSearchLimitedToLibrary: !allowAnyRegisteredCardLogin,
    effectiveLibraryOrgId: String(effectiveLibraryOrgId || ""),
    effectiveLibraryOrgName: String(effectiveLibraryOrgName || "")
  };
}

function enabledLibraryList(appSettings) {
  return splitList.split(String(appSettings.enabledLibraryOrgIds || ""));
}

function effectiveLibraryParticipates(app, libraryOrgId) {
  var list = enabledLibraryList(config.getSettings(app));
  if (!list.length) return true;
  return list.indexOf(String(libraryOrgId || "").trim()) >= 0;
}

module.exports = {
  resolveEffectiveStaffLibraryContext: resolveEffectiveStaffLibraryContext,
  allowCrossLibraryPatronLookup: allowCrossLibraryPatronLookup,
  patronMatchesStaffLookupScope: patronMatchesStaffLookupScope,
  staffPatronLookupScopeMeta: staffPatronLookupScopeMeta,
  effectiveLibraryParticipates: effectiveLibraryParticipates,
};
