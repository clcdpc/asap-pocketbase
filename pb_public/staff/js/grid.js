import { pb, gridContainer, staffGridFilterBar, tagFilterSelect, claimFilterSelect, similarRequestFilterSelect, additionalCopyStatusFilterSelect, closedTypeFilterSelect, gridSearchInput, settingsContainer, grid, formatMap, ageMap, closeReasonMap, descriptions, emptyStateMessages, statusStages, currentStatus, currentSuggestions, activeTagFilter, gridSearchKeyword, currentClaimFilter, currentSimilarRequestFilter, currentAdditionalCopyStatus, currentClosedTypeFilter, currentWorkflowOrgScopeId, allSuggestions, workflowSettings, currentSettingsSection, activeActionMenu, rowActionIdCounter, rowActionRegistry, setCurrentStatus, setCurrentSuggestions, setActiveTagFilter, setGridSearchKeyword, setCurrentClaimFilter, setCurrentWorkflowOrgScopeId, setCurrentClosedTypeFilter, setActiveActionMenu, setGrid, setAllSuggestions, incrementRowActionIdCounter } from './state.js';
import { openEdit, polarisSearchValueForRow, renderPolarisSearchButtonMarkup } from './modals.js';
import { leapBibUrl, leapPatronUrl } from './api.js';
import { escapeAttr, formatStandardDate, formatDateTime, formatPublication, formatNote } from './grid-utils.js';
import {
  duplicateStatusNames,
  getDuplicateSummary as getDuplicateSummaryWithContext,
  getDuplicateLabels as getDuplicateLabelsWithContext,
  applyTagFilter as applyTagFilterWithContext,
  applySimilarRequestFilter as applySimilarRequestFilterWithContext,
  applyClaimFilter as applyClaimFilterBase,
  applyTypeFilter as applyTypeFilterWithContext,
  isUnclaimed,
  getDuplicateBadgesHtml as getDuplicateBadgesHtmlWithContext,
  hasWorkflowTag,
  getWorkflowTagPresentation,
  renderWorkflowTags as renderWorkflowTagsWithContext,
  getIsbnCheckBadgesHtml as getIsbnCheckBadgesHtmlWithContext,
  renderDuplicateSummary as renderDuplicateSummaryWithContext
} from './grid-filters.js';
import {
  hideTagFilter as hideTagFilterWithContext,
  hideClaimFilter as hideClaimFilterWithContext,
  updateTagFilter as updateTagFilterWithContext,
  updateClaimFilter as updateClaimFilterWithContext,
  toggleTagFilter as toggleTagFilterWithContext
} from './grid-filter-controls.js';
import {
  renderBibIdCell as renderBibIdCellWithContext,
  rowMarker,
  renderBarcodeCell as renderBarcodeCellWithContext,
  renderAdditionalCopySourceCell,
  renderPolarisRowSearchButton as renderPolarisRowSearchButtonWithContext,
  renderTitleCell as renderTitleCellWithContext,
  renderAuthorCell as renderAuthorCellWithContext,
  renderClaimCell as renderClaimCellWithContext,
  renderCurrentGrid as renderCurrentGridWithContext
} from './grid-rendering.js';
import { setupGridEvents } from './grid-events.js';
import { getActionsColumnWidth as getActionsColumnWidthWithContext, getGridColumns as getGridColumnsWithContext } from './grid-columns.js';
import {
  loadTab as loadTabWithContext,
  resetGrid as resetGridWithContext,
  refreshCurrentStaffView as refreshCurrentStaffViewWithContext,
  refreshStaffStatus as refreshStaffStatusWithContext,
  updateTabCounts as updateTabCountsWithContext
} from './grid-data.js';
import {
  formatCloseReason as formatCloseReasonWithContext,
  getRowActions as getRowActionsWithContext,
  openAssignDialog as openAssignDialogWithContext,
  claimActionsForRow as claimActionsForRowWithContext,
  claimRequest as claimRequestWithContext,
  unclaimRequest as unclaimRequestWithContext,
  runRowAction as runRowActionWithContext,
  registerRowAction as registerRowActionWithContext,
  getRegisteredRowAction as getRegisteredRowActionWithContext,
  renderRowActions as renderRowActionsWithContext,
  openActionMenu as openActionMenuWithContext,
  positionActionMenu as positionActionMenuWithContext,
  closeActionMenu as closeActionMenuWithContext,
  currentStaffId as currentStaffIdWithContext,
  isClaimedByCurrentUser as isClaimedByCurrentUserWithContext
} from './grid-actions.js';

export { normalizeLabel, flagDisplayMap, getFlagDisplay, getIsbnCheckLabel, effectiveWorkflowFlagsForRow, getFilterableLabelsForRow, normalizeWorkflowTagLabel, cleanWorkflowTags, tagCountsForRecords, normalizeStatus } from './grid-policy.mjs';
export { escapeAttr, formatStandardDate, formatDateTime, formatPublication, formatNote, sanitizeHtml } from './grid-utils.js';
export { duplicateStatusNames, hasWorkflowTag, getWorkflowTagPresentation, isUnclaimed } from './grid-filters.js';
export { rowMarker, renderAdditionalCopySourceCell } from './grid-rendering.js';
function currentGridDataContext() {
  return {
    pb,
    gridContainer,
    staffGridFilterBar,
    additionalCopyStatusFilterSelect,
    settingsContainer,
    descriptions,
    emptyStateMessages,
    statusStages,
    get grid() { return grid; },
    get currentStatus() { return currentStatus; },
    get currentSuggestions() { return currentSuggestions; },
    get allSuggestions() { return allSuggestions; },
    get currentWorkflowOrgScopeId() { return currentWorkflowOrgScopeId; },
    get workflowSettings() { return workflowSettings; },
    get currentSettingsSection() { return currentSettingsSection; },
    setCurrentStatus,
    setCurrentSuggestions,
    setGridSearchKeyword,
    setCurrentWorkflowOrgScopeId,
    setGrid,
    setAllSuggestions,
    hideTagFilter,
    hideClaimFilter,
    updateTagFilter,
    updateClaimFilter,
    renderCurrentGrid,
    closeActionMenu
  };
}

export function loadTab(status) {
  return loadTabWithContext(status, currentGridDataContext());
}

export function resetGrid() {
  return resetGridWithContext(currentGridDataContext());
}

export function refreshCurrentStaffView() {
  return refreshCurrentStaffViewWithContext(currentGridDataContext());
}

export function refreshStaffStatus(status) {
  return refreshStaffStatusWithContext(status, currentGridDataContext());
}

function currentGridFilterContext() {
  return {
    staffGridFilterBar,
    tagFilterSelect,
    claimFilterSelect,
    similarRequestFilterSelect,
    additionalCopyStatusFilterSelect,
    closedTypeFilterSelect,
    allSuggestions,
    activeTagFilter,
    currentSimilarRequestFilter,
    currentStatus,
    currentClosedTypeFilter,
    currentClaimFilter,
    setActiveTagFilter
  };
}

function currentGridRenderingContext() {
  return {
    ...currentGridFilterContext(),
    gridContainer,
    gridSearchInput,
    currentSuggestions,
    gridSearchKeyword,
    formatMap,
    closeReasonMap,
    leapBibUrl,
    leapPatronUrl,
    polarisSearchValueForRow,
    renderPolarisSearchButtonMarkup,
    isClaimedByCurrentUser,
    currentStaffId,
    resetGrid,
    setGrid,
    renderBarcodeCell,
    renderTitleCell,
    renderAuthorCell,
    renderClaimCell,
    renderBibIdCell,
    renderRowActions
  };
}

export function getDuplicateSummary(row) {
  return getDuplicateSummaryWithContext(row, currentGridFilterContext());
}

export function getDuplicateLabels(row) {
  return getDuplicateLabelsWithContext(row, currentGridFilterContext());
}

export function hideTagFilter() {
  return hideTagFilterWithContext(currentGridFilterContext());
}

export function hideClaimFilter() {
  return hideClaimFilterWithContext(currentGridFilterContext());
}

export function updateTagFilter(records) {
  return updateTagFilterWithContext(records, currentGridFilterContext());
}

export function updateClaimFilter() {
  return updateClaimFilterWithContext(currentGridFilterContext());
}

export function applyTagFilter(records) {
  return applyTagFilterWithContext(records, currentGridFilterContext());
}

export function applySimilarRequestFilter(records) {
  return applySimilarRequestFilterWithContext(records, currentGridFilterContext());
}

export function currentStaffId() {
  return String((pb.authStore.model && pb.authStore.model.id) || '').trim();
}

export function isClaimedByCurrentUser(row) {
  const staffId = currentStaffId();
  return !!staffId && String(row?.claimedByStaffUserId || '').trim() === staffId;
}

export function applyClaimFilter(records, filter = currentClaimFilter, staffId = currentStaffId()) {
  return applyClaimFilterBase(records, filter, staffId);
}

export function toggleTagFilter(tagName) {
  return toggleTagFilterWithContext(tagName, currentGridFilterContext(), () => renderCurrentGrid(currentStatus));
}

export function applyTypeFilter(records) {
  return applyTypeFilterWithContext(records, currentGridFilterContext());
}

export function renderCurrentGrid(status = currentStatus) {
  return renderCurrentGridWithContext(status, currentGridRenderingContext());
}

export function updateTabCounts(records, openAdditionalCount = 0, closedAdditionalCount = 0) {
  return updateTabCountsWithContext(records, openAdditionalCount, closedAdditionalCount, currentGridDataContext());
}

export function renderBibIdCell(row) {
  return renderBibIdCellWithContext(row, currentGridRenderingContext());
}

export function renderBarcodeCell(row) {
  return renderBarcodeCellWithContext(row, currentGridRenderingContext());
}

export function getActionsColumnWidth(status) {
  return getActionsColumnWidthWithContext(status);
}

export function getGridColumns(status, rowById = new Map()) {
  return getGridColumnsWithContext(status, rowById, {
    formatMap,
    closeReasonMap,
    renderBarcodeCell,
    renderTitleCell,
    renderAuthorCell,
    renderClaimCell,
    renderBibIdCell,
    renderRowActions
  });
}

export function getDuplicateBadgesHtml(row) {
  return getDuplicateBadgesHtmlWithContext(row, currentGridFilterContext());
}

export function renderWorkflowTags(tags, row) {
  return renderWorkflowTagsWithContext(tags, row, currentGridFilterContext());
}

export function getIsbnCheckBadgesHtml(row) {
  return getIsbnCheckBadgesHtmlWithContext(row, currentGridFilterContext());
}

export function renderDuplicateSummary(row) {
  return renderDuplicateSummaryWithContext(row, currentGridFilterContext());
}

export function renderPolarisRowSearchButton(row, mode) {
  return renderPolarisRowSearchButtonWithContext(row, mode, currentGridRenderingContext());
}

export function renderTitleCell(row) {
  return renderTitleCellWithContext(row, currentGridRenderingContext());
}

export function renderAuthorCell(row) {
  return renderAuthorCellWithContext(row, currentGridRenderingContext());
}

export function renderClaimCell(row) {
  return renderClaimCellWithContext(row, currentGridRenderingContext());
}

function currentGridActionContext() {
  return {
    pb,
    closeReasonMap,
    get currentStatus() { return currentStatus; },
    get currentSuggestions() { return currentSuggestions; },
    get allSuggestions() { return allSuggestions; },
    get activeActionMenu() { return activeActionMenu; },
    rowActionRegistry,
    incrementRowActionIdCounter,
    setActiveActionMenu
  };
}

export function formatCloseReason(row) {
  return formatCloseReasonWithContext(row, currentGridActionContext());
}

export function getRowActions(row) {
  return getRowActionsWithContext(row, currentGridActionContext(), refreshCurrentStaffView);
}

export async function openAssignDialog(row) {
  await openAssignDialogWithContext(row, refreshCurrentStaffView);
}

export function claimActionsForRow(row) {
  return claimActionsForRowWithContext(row, currentGridActionContext(), refreshCurrentStaffView);
}

export async function claimRequest(requestId) {
  await claimRequestWithContext(requestId, currentGridActionContext(), refreshCurrentStaffView);
}

export async function unclaimRequest(requestId) {
  await unclaimRequestWithContext(requestId, currentGridActionContext(), refreshCurrentStaffView);
}

export async function runRowAction(action) {
  await runRowActionWithContext(action, currentGridActionContext());
}

export function registerRowAction(action) {
  return registerRowActionWithContext(action, currentGridActionContext());
}

export function getRegisteredRowAction(actionId) {
  return getRegisteredRowActionWithContext(actionId, currentGridActionContext());
}

export function renderRowActions(row) {
  return renderRowActionsWithContext(row, currentGridActionContext(), refreshCurrentStaffView);
}

export function openActionMenu(triggerButton, actionIds) {
  return openActionMenuWithContext(triggerButton, actionIds, currentGridActionContext());
}

export function positionActionMenu(triggerButton, menu) {
  return positionActionMenuWithContext(triggerButton, menu);
}

export function closeActionMenu() {
  return closeActionMenuWithContext(currentGridActionContext());
}

setupGridEvents({
  gridContainer,
  openEdit,
  get currentSuggestions() { return currentSuggestions; },
  get allSuggestions() { return allSuggestions; },
  get currentStatus() { return currentStatus; },
  get activeActionMenu() { return activeActionMenu; },
  getRegisteredRowAction,
  runRowAction,
  openActionMenu,
  closeActionMenu,
  toggleTagFilter,
  loadTab
});
