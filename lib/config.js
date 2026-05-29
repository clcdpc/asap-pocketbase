const normalization = require("./config/normalization.js");
const defaults = require("./config/defaults.js");
const dbHelpers = require("./config/db_helpers.js");
const polarisMod = require("./config/polaris.js");
const smtp = require("./config/smtp.js");
const workflows = require("./config/workflows.js");
const uiText = require("./config/ui_text.js");
const emails = require("./config/emails.js");
const settings = require("./config/settings.js");

module.exports = {
  // settings.js
  allowedStaffUsers: settings.allowedStaffUsers,
  enabledLibraryOrgIds: settings.enabledLibraryOrgIds,
  getSystemSettings: settings.getSystemSettings,
  getSettings: settings.getSettings,
  librarySettings: settings.librarySettings,
  saveSystemSettings: settings.saveSystemSettings,
  staffUrl: settings.staffUrl,
  formatIconUrlPattern: settings.formatIconUrlPattern,

  // smtp.js
  applyMailSettings: smtp.applyMailSettings,
  getSmtpSettings: smtp.getSmtpSettings,
  mail: smtp.mail,

  // defaults.js
  defaultDuplicateStatusLabels: defaults.defaultDuplicateStatusLabels,
  defaultEmailTemplates: defaults.defaultEmailTemplates,

  // ui_text.js
  duplicateStatusLabels: uiText.duplicateStatusLabels,
  mergeDuplicateStatusLabels: uiText.mergeDuplicateStatusLabels,
  uiText: uiText.uiText,

  // emails.js
  emailStatus: emails.emailStatus,
  emails: emails.emails,
  rejectionTemplates: emails.rejectionTemplates,

  // db_helpers.js
  findOrganization: dbHelpers.findOrganization,
  scopedRows: dbHelpers.scopedRows,

  // polaris.js
  getPolarisSettings: polarisMod.getPolarisSettings,
  polaris: polarisMod.polaris,
  savePolarisSettings: polarisMod.savePolarisSettings,

  // normalization.js
  normalizeLeapBibUrlPattern: normalization.normalizeLeapBibUrlPattern,
  normalizeFormatIconUrlPattern: normalization.normalizeFormatIconUrlPattern,
  normalizeStaffUrl: normalization.normalizeStaffUrl,
  parseJsonArray: normalization.parseJsonArray,
  parseJsonObject: normalization.parseJsonObject,

  // workflows.js
  jobLimits: workflows.jobLimits,
  holdPickupTimeout: workflows.holdPickupTimeout,
  pendingHoldTimeout: workflows.pendingHoldTimeout,
  additionalCopyTimeout: workflows.additionalCopyTimeout,
  outstandingTimeout: workflows.outstandingTimeout,
  outstandingTimeoutEmail: workflows.outstandingTimeoutEmail,
  suggestionLimit: workflows.suggestionLimit,
  workflowSettings: workflows.workflowSettings,
  defaultWorkflowValues: workflows.defaultWorkflowValues,
};
