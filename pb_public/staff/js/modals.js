import * as state from './state.js';
import { workflowStatusLabel, staffProfileEmail, polarisSearchValueForRow } from './modals/utils.js';
import { renderPatronContext, renderEditPatronContext } from './modals/patron-context.js';
import { confirmDuplicateOpenRequestClose } from './modals/confirm-duplicate.js';
import { confirmAdditionalCopyAction } from './modals/additional-copy.js';
import { refreshCurrentStaffView } from './grid.js';
import { createModalContext } from './modals/context.js';
import { buildPendingAuditPreview as auditPreviewBuild, renderPendingAuditPreview as auditPreviewRender } from './modals/audit-preview.js';
import { renderRejectionTemplateSelector as rejectionRender } from './modals/rejection-templates.js';
import { openEdit as editFormOpen, getExistingHistory as editFormGetHistory, getDraftCommentValue as editFormGetDraft, setBibIdRequirement as editFormSetBibId, renderEditLeapBibLink as editFormRenderBibLink, renderExternalSearchButton as editFormRenderExtSearch, renderEditMetadata as editFormRenderMetadata, renderPurchaseReminderOption as editFormRenderPurchaseReminder } from './modals/edit-form.js';
import { renderEditClaimState as claimRenderClaim, renderEditWorkflowTags as claimRenderTags, reactiveCleanupWorkflowFlags as claimCleanupFlags } from './modals/claim-tags.js';
import { openPolarisSearch as polarisSearchOpen, renderPolarisSearchButtonMarkup as polarisSearchBtnMarkup } from './modals/polaris-search.js';
import { initModalEvents } from './modals/events.js';

const ctx = createModalContext(state);
initModalEvents(ctx, { onRefresh: refreshCurrentStaffView });

export function openEdit(id, nextStatus, dialogTitle, actionStr, buttonLabel) {
  return editFormOpen(id, nextStatus, dialogTitle, actionStr, buttonLabel, ctx);
}

export function renderEditClaimState(row) {
  return claimRenderClaim(row, ctx);
}

export function getExistingHistory(row) {
  return editFormGetHistory(row);
}

export function getDraftCommentValue() {
  return editFormGetDraft(ctx);
}

export function buildPendingAuditPreview(row, nextStatus, actionStr) {
  return auditPreviewBuild(row, nextStatus, actionStr, ctx);
}

export function renderPendingAuditPreview(row, nextStatus, actionStr) {
  return auditPreviewRender(row, nextStatus, actionStr, ctx);
}

export function renderRejectionTemplateSelector(actionStr) {
  return rejectionRender(actionStr, ctx);
}

export function renderPurchaseReminderOption(actionStr) {
  return editFormRenderPurchaseReminder(actionStr, ctx);
}

export function renderEditWorkflowTags(tags, row) {
  return claimRenderTags(tags, row, ctx);
}

export function renderEditMetadata(row) {
  return editFormRenderMetadata(row, ctx);
}

export function renderEditLeapBibLink(bibId) {
  return editFormRenderBibLink(bibId, ctx);
}
export function renderExternalSearchButton(title, identifier) {
  return editFormRenderExtSearch(title, identifier, ctx);
}

export function setBibIdRequirement(nextStatus) {
  return editFormSetBibId(nextStatus, ctx);
}

export function reactiveCleanupWorkflowFlags(rowId) {
  return claimCleanupFlags(rowId, ctx);
}

export function openPolarisSearch(row, mode, options = {}) {
  return polarisSearchOpen(row, mode, options, ctx);
}

export { polarisSearchBtnMarkup as renderPolarisSearchButtonMarkup };
// Direct re-exports
export { workflowStatusLabel, staffProfileEmail, polarisSearchValueForRow };
export { renderPatronContext, renderEditPatronContext };
export { confirmDuplicateOpenRequestClose };
export { confirmAdditionalCopyAction };
