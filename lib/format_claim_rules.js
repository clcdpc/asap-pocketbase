const records = require(`${__hooks}/../lib/records.js`);

const CLAIM_TYPE_MANUAL = "manual";
const CLAIM_TYPE_AUTOMATIC_FORMAT_RULE = "automatic_format_rule";

function claimDisplayName(staff) {
  return String(staff.get("displayName") || staff.get("username") || staff.get("identityKey") || "Staff").trim();
}

function claimType(record) {
  return String(record.get("claimType") || "").trim();
}

function claimantId(record) {
  return String(record.get("claimedByStaffUserId") || "").trim();
}

function hasManualOrLegacyClaim(record) {
  var claimedBy = claimantId(record);
  if (!claimedBy) return false;
  return claimType(record) !== CLAIM_TYPE_AUTOMATIC_FORMAT_RULE;
}

function findActiveRule(app, libraryOrgId, format) {
  libraryOrgId = String(libraryOrgId || "").trim();
  format = records.normalizeFormat(format);
  if (!libraryOrgId || !format) return null;
  try {
    return app.findFirstRecordByFilter("format_claim_rules", "libraryOrgId = {:libraryOrgId} && format = {:format} && active = true", {
      libraryOrgId: libraryOrgId,
      format: format
    });
  } catch (err) {
    return null;
  }
}

function staffForRule(app, rule) {
  if (!rule) return null;
  var staffId = String(rule.get("staffUserId") || rule.get("staffUser") || "").trim();
  if (!staffId) return null;
  try {
    var staff = app.findRecordById("staff_users", staffId);
    if (!staff || staff.getBool("active") === false) return null;
    return staff;
  } catch (err) {
    return null;
  }
}

function sameLibrary(staff, libraryOrgId) {
  var role = String(staff.get("role") || "").trim();
  if (role === "super_admin") return true;
  return String(staff.get("libraryOrgId") || "").trim() === String(libraryOrgId || "").trim();
}

function setManualClaim(record, staff) {
  record.set("claimedByStaffUserId", String(staff.id || "").trim());
  record.set("claimedByDisplayName", claimDisplayName(staff));
  record.set("claimedAt", new Date().toISOString());
  record.set("claimType", CLAIM_TYPE_MANUAL);
  record.set("claimRuleId", "");
  record.set("updated", new Date().toISOString());
}

function clearClaim(record) {
  record.set("claimedByStaffUserId", "");
  record.set("claimedByDisplayName", "");
  record.set("claimedAt", "");
  record.set("claimType", "");
  record.set("claimRuleId", "");
  record.set("updated", new Date().toISOString());
}

function setAutomaticClaim(record, staff, rule) {
  record.set("claimedByStaffUserId", String(staff.id || "").trim());
  record.set("claimedByDisplayName", claimDisplayName(staff));
  record.set("claimedAt", new Date().toISOString());
  record.set("claimType", CLAIM_TYPE_AUTOMATIC_FORMAT_RULE);
  record.set("claimRuleId", String(rule.id || "").trim());
  record.set("updated", new Date().toISOString());
}

function claimChangeMessage(previousName, nextName, previousFormat, nextFormat) {
  if (previousName && previousName !== nextName) {
    return "Auto-claim changed from " + previousName + " to " + nextName + " because format changed from " + (previousFormat || "unknown") + " to " + (nextFormat || "unknown") + ".";
  }
  return "Auto-claimed by " + nextName + " because " + (nextFormat || "this format") + " is assigned to " + nextName + " for this library.";
}

function applyFormatClaimRule(app, titleRequest, options) {
  options = options || {};
  if (!titleRequest) return null;
  if (hasManualOrLegacyClaim(titleRequest)) {
    return { changed: false, reason: "manual_claim" };
  }

  var libraryOrgId = String(titleRequest.get("libraryOrgId") || "").trim();
  var format = records.normalizeFormat(titleRequest.get("format"));
  var previousClaimantName = String(titleRequest.get("claimedByDisplayName") || "").trim();
  var wasAutomatic = claimType(titleRequest) === CLAIM_TYPE_AUTOMATIC_FORMAT_RULE;
  var rule = findActiveRule(app, libraryOrgId, format);

  if (!rule) {
    if (wasAutomatic || claimantId(titleRequest)) {
      clearClaim(titleRequest);
      app.save(titleRequest);
      records.recordEvent(app, titleRequest, "claim_auto_cleared", "Auto-claim cleared because no automatic claimant is configured for " + format + ".", {
        actorName: options.actorName || "system",
        metadata: { trigger: options.trigger || "", previousFormat: options.previousFormat || "", format: format }
      });
      return { changed: true, action: "cleared" };
    }
    return { changed: false, reason: "no_rule" };
  }

  var staff = staffForRule(app, rule);
  if (!staff || !sameLibrary(staff, libraryOrgId)) {
    records.recordEvent(app, titleRequest, "claim_auto_skipped", "Auto-claim skipped because the configured claimant is unavailable or outside this library.", {
      actorName: options.actorName || "system",
      metadata: { trigger: options.trigger || "", ruleId: String(rule.id || "") }
    });
    return { changed: false, reason: "invalid_staff" };
  }

  var nextName = claimDisplayName(staff);
  setAutomaticClaim(titleRequest, staff, rule);
  app.save(titleRequest);
  records.recordEvent(app, titleRequest, previousClaimantName ? "claim_auto_reassigned" : "claim_auto_assigned", claimChangeMessage(previousClaimantName, nextName, options.previousFormat, format), {
    actorName: options.actorName || "system",
    metadata: { trigger: options.trigger || "", ruleId: String(rule.id || ""), previousFormat: options.previousFormat || "", format: format }
  });
  return { changed: true, action: previousClaimantName ? "reassigned" : "assigned" };
}

module.exports = {
  CLAIM_TYPE_AUTOMATIC_FORMAT_RULE: CLAIM_TYPE_AUTOMATIC_FORMAT_RULE,
  CLAIM_TYPE_MANUAL: CLAIM_TYPE_MANUAL,
  applyFormatClaimRule: applyFormatClaimRule,
  clearClaim: clearClaim,
  claimDisplayName: claimDisplayName,
  claimType: claimType,
  findActiveRule: findActiveRule,
  hasManualOrLegacyClaim: hasManualOrLegacyClaim,
  setManualClaim: setManualClaim,
};
