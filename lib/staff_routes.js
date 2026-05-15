var _hooksDir = typeof __hooks !== "undefined" ? __hooks : __dirname + "/../pb_hooks";

const auth = require(`${_hooksDir}/../lib/staff/auth.js`);
const profile = require(`${_hooksDir}/../lib/staff/profile.js`);
const users = require(`${_hooksDir}/../lib/staff/users.js`);
const patron = require(`${_hooksDir}/../lib/staff/patron.js`);
const title_requests = require(`${_hooksDir}/../lib/staff/title_requests.js`);
const analytics = require(`${_hooksDir}/../lib/staff/analytics.js`);
const tools = require(`${_hooksDir}/../lib/staff/tools.js`);
const settings = require(`${_hooksDir}/../lib/staff/settings.js`);

module.exports = {
  staffLogin: auth.staffLogin,
  staffProfileUpdate: profile.staffProfileUpdate,
  looksLikeBarcodeCandidate: patron.looksLikeBarcodeCandidate,
  staffLookupPatron: patron.staffLookupPatron,
  staffUsersList: users.staffUsersList,
  staffUserRoleUpdate: users.staffUserRoleUpdate,
  staffUserCreate: users.staffUserCreate,
  staffUserDelete: users.staffUserDelete,
  staffDeleteClosedRequest: title_requests.staffDeleteClosedRequest,
  staffDeleteClosedRequestsBulk: title_requests.staffDeleteClosedRequestsBulk,
  staffTitleRequestsList: title_requests.staffTitleRequestsList,
  staffAnalytics: analytics.staffAnalytics,
  staffClaimTitleRequest: title_requests.staffClaimTitleRequest,
  staffUnclaimTitleRequest: title_requests.staffUnclaimTitleRequest,
  staffTitleRequestAction: title_requests.staffTitleRequestAction,
  staffCreateSuggestion: patron.staffCreateSuggestion,
  staffSyncOrganizations: tools.staffSyncOrganizations,
  staffTestPolaris: tools.staffTestPolaris,
  staffTestSmtp: tools.staffTestSmtp,
  staffEmailStatus: tools.staffEmailStatus,
  staffBibLookup: tools.staffBibLookup,
  getLibrarySettings: settings.getLibrarySettings,
  updateLibrarySettings: settings.updateLibrarySettings,
  getLibraryOverridesSummary: settings.getLibraryOverridesSummary,
  saveWorkflowSettings: settings.saveWorkflowSettings,
  saveRejectionTemplates: settings.saveRejectionTemplates,
  staffSaveLogo: settings.staffSaveLogo,
  staffResetLogo: settings.staffResetLogo,
  assertRejectionTemplateNotUsedByAutoReject: settings.assertRejectionTemplateNotUsedByAutoReject,
  titleRequestListScope: title_requests.titleRequestListScope,
  resolveAnalyticsScope: analytics.resolveAnalyticsScope,
  resolveAnalyticsDateRange: analytics.resolveAnalyticsDateRange,
  loadAnalyticsSummary: analytics.loadAnalyticsSummary,
  loadFirstHoldPlacedEventTimes: analytics.loadFirstHoldPlacedEventTimes,
  loadStageCounts: analytics.loadStageCounts,
  loadClosedReasonBreakdown: analytics.loadClosedReasonBreakdown,
  loadAgingMetrics: analytics.loadAgingMetrics,
  loadExceptionCounts: analytics.loadExceptionCounts,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE: settings.TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE: settings.TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE,
  validateMaterialFormatsDeletion: settings.validateMaterialFormatsDeletion,
};
