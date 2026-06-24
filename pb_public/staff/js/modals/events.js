import { refreshEditAuditPreview } from './audit-preview.js';
import { renderEditCustomFieldsForCurrentFormat } from './edit-form.js';
import { submitEditForm } from './edit-submit.js';
import { reactiveCleanupWorkflowFlags } from './claim-tags.js';
import { launchEditPolarisSearch, closePolarisSearchDialog } from './polaris-search.js';
import { openProfileDialog } from '../api.js';

let eventsBound = false;

export function initModalEvents(ctx, { onRefresh } = {}) {
  if (eventsBound) return;
  eventsBound = true;

  document.addEventListener('click', (e) => {
    if (e.target.closest('.js-open-profile-dialog')) {
      e.preventDefault();
      openProfileDialog();
    }
  });

  document.getElementById('edit-form').addEventListener('submit', (e) => {
    submitEditForm(e, ctx, { onRefresh });
  });

  const refreshPreview = () => refreshEditAuditPreview(ctx);
  const refreshFields = () => {
    const id = ctx.id.value;
    const row = ctx.currentSuggestions.find(r => r.id === id) || ctx.allSuggestions.find(r => r.id === id);
    if (row) renderEditCustomFieldsForCurrentFormat(row, ctx);
  };

  ['edit-format', 'edit-publication', 'edit-autohold'].forEach(id => {
    document.getElementById(id)?.addEventListener('change', refreshPreview);
  });
  document.getElementById('edit-format')?.addEventListener('change', refreshFields);

  document.getElementById('edit-bibid')?.addEventListener('input', refreshPreview);

  window.addEventListener('asap-bib-verified', (e) => {
    const { rowId } = e.detail;
    reactiveCleanupWorkflowFlags(rowId, ctx);
    refreshEditAuditPreview(ctx);
  });

  document.getElementById('close-polaris-search-x')?.addEventListener('click', closePolarisSearchDialog);
  document.getElementById('close-polaris-search-btn')?.addEventListener('click', closePolarisSearchDialog);
  document.getElementById('edit-title-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('title', e.currentTarget, 'edit', ctx, onRefresh));
  document.getElementById('edit-author-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('author', e.currentTarget, 'edit', ctx, onRefresh));
  document.getElementById('edit-identifier-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('identifier', e.currentTarget, 'edit', ctx, onRefresh));

  document.getElementById('new-title-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('title', e.currentTarget, 'new', ctx, onRefresh));
  document.getElementById('new-author-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('author', e.currentTarget, 'new', ctx, onRefresh));
  document.getElementById('new-identifier-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('identifier', e.currentTarget, 'new', ctx, onRefresh));
}
