const helpers = require("./records/helpers.js");
const staff = require("./records/staff.js");
const patron = require("./records/patron.js");
const tags = require("./records/tags.js");
const duplicates = require("./records/duplicates.js");
const suggestions = require("./records/suggestions.js");

module.exports = {
  CLOSE_REASON: helpers.CLOSE_REASON,
  FORMAT: helpers.FORMAT,
  STATUS: helpers.STATUS,
  appendSystemNote: helpers.appendSystemNote,
  formatDate: helpers.formatDate,
  createSuggestion: suggestions.createSuggestion,
  hasStaffUsers: staff.hasStaffUsers,
  countAdminUsers: staff.countAdminUsers,
  countSuperAdminUsers: staff.countSuperAdminUsers,
  createStaffUser: staff.createStaffUser,
  findStaffByIdentity: staff.findStaffByIdentity,
  findStaffByEmail: staff.findStaffByEmail,
  normalizeCloseReason: helpers.normalizeCloseReason,
  normalizeFormat: helpers.normalizeFormat,
  normalizeStatus: helpers.normalizeStatus,
  getStatusLabel: helpers.getStatusLabel,
  duplicateContext: duplicates.duplicateContext,
  listStaffUsers: staff.listStaffUsers,
  listScopedStaffUsers: staff.listScopedStaffUsers,
  setStatusWithNote: suggestions.setStatusWithNote,
  addWorkflowTagForRequest: tags.addWorkflowTagForRequest,
  removeWorkflowTagForRequest: tags.removeWorkflowTagForRequest,
  auditDeletedRequest: suggestions.auditDeletedRequest,
  deleteClosedRequestsBulk: suggestions.deleteClosedRequestsBulk,
  deleteRelatedRows: suggestions.deleteRelatedRows,
  deleteTitleRequestWithAudit: suggestions.deleteTitleRequestWithAudit,
  deleteAdditionalCopyRequestWithAudit: suggestions.deleteAdditionalCopyRequestWithAudit,
  recordEvent: helpers.recordEvent,
  setCanonicalRefs: helpers.setCanonicalRefs,
  polarisSubmittedSearchValue: helpers.polarisSubmittedSearchValue,
  titleRequestToJson: suggestions.titleRequestToJson,
  updateTitleRequest: suggestions.updateTitleRequest,
  upsertPatronUser: patron.upsertPatronUser,
  cachePolarisPatronId: patron.cachePolarisPatronId,
  cachedPolarisPatronIdForTitleRequest: patron.cachedPolarisPatronIdForTitleRequest,
  upsertStaffUser: staff.upsertStaffUser,
  safeEmail: patron.safeEmail,
  workflowTagsForRequest: tags.workflowTagsForRequest,
  enforceWeeklyLimit: duplicates.enforceWeeklyLimit,
  setRelation: helpers.setRelation,
};
