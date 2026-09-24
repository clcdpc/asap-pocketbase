import { staffSession, currentSuggestions, allSuggestions } from './state.js';
import { isAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { refreshCurrentStaffView, escapeAttr } from './grid.js';
import { findWorkflowRow } from './request-identity.mjs';
import { beginRowAction } from './row-action-ownership.mjs';

export function undoConfirmMessage(type) {
  if (type === 'additional_copy') {
    return 'Undo action and return this request to Additional Copies?';
  }
  return 'Undo action and return this suggestion to Suggestions?';
}

export async function undoRow(identity) {
  if (!['title_request', 'additional_copy'].includes(identity?.type) || !String(identity.id ?? '').trim()) return;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row || row.type !== identity.type) return;
  const ownership = beginRowAction(row);
  const isCurrent = ownership.ownsUi;
  const id = row.id;

  if (!await showConfirm('Undo action', undoConfirmMessage(row.type)) || !isCurrent()) return;

  try {
    const url = row.type === 'additional_copy'
      ? `/api/asap/staff/additional-copies/${encodeURIComponent(id)}/reopen`
      : `/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`;
    const body = row.type === 'additional_copy'
      ? { version: row.version }
      : {
          version: row.version,
          action: 'reopen',
          status: 'suggestion',
          editedBy: staffSession.staff?.username
        };

    await authorizedJson(url, {
      method: 'POST',
      body
    });
    if (ownership.sessionCurrent()) await refreshCurrentStaffView({ silent: !isCurrent() });
  } catch (err) {
    const uncertain = !err?.status || err.status >= 500;
    if (uncertain && ownership.sessionCurrent()) await refreshCurrentStaffView({ silent: true });
    if (isCurrent()) await showAlert(uncertain
      ? 'Could not confirm whether Undo was saved. Refresh before retrying.'
      : err.message || 'Error undoing action');
  }
}

export async function deleteClosedRequest(identity) {
  if (!isAdminStaff()) return;
  if (!['title_request', 'additional_copy'].includes(identity?.type) || !String(identity.id ?? '').trim()) return;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row || row.type !== identity.type) return;
  const ownership = beginRowAction(row);
  const isCurrent = ownership.ownsUi;
  const confirmed = await showConfirm('Delete this closed request?', 'This cannot be undone.');
  if (!confirmed || !isCurrent()) return;
  try {
    const url = row.type === 'additional_copy'
      ? `/api/asap/staff/additional-copies/${encodeURIComponent(row.id)}`
      : `/api/asap/staff/requests/${encodeURIComponent(row.id)}`;
    await authorizedJson(url, { method: 'DELETE', body: { version: row.version } });
    if (isCurrent()) showToast('Closed request deleted.', 'success');
    if (ownership.sessionCurrent()) await refreshCurrentStaffView({ silent: !isCurrent() });
  } catch (err) {
    const uncertain = !err?.status || err.status >= 500;
    if (uncertain && ownership.sessionCurrent()) await refreshCurrentStaffView({ silent: true });
    if (isCurrent()) await showAlert(uncertain
      ? 'Could not confirm whether deletion was saved. Refresh before retrying.'
      : err.message || 'Could not delete closed request.');
  }
}

export async function closeDuplicateRequest(identity, options = {}) {
  const { alreadyConfirmed = false, isCurrent = () => true, refresh = true } = options;
  if (identity?.type !== 'title_request' || !String(identity.id ?? '').trim()) return false;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row || row.type !== 'title_request') return false;
  const ownership = beginRowAction(row);
  const ownsRow = ownership.ownsUi;
  const id = row.id;
  if (!alreadyConfirmed && !await showConfirm('Close this duplicate request?',
    'The patron already has an open request or hold for this BIB ID.')) return false;
  if (!isCurrent() || !ownsRow()) return false;
  try {
    await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      body: {
        version: row.version,
        action: 'closeDuplicate',
        status: 'closed',
        title: row.title || '',
        author: row.author || '',
        identifier: row.identifier || '',
        bibid: row.bibid || '',
        format: row.format || '',
        publication: row.publication || '',
        exactPublicationDate: row.exactPublicationDate || null,
        notes: row.notes || '',
        editedBy: staffSession.staff?.username
      }
    });
    if (isCurrent() && ownsRow()) showToast('Duplicate request closed.', 'success');
    if (refresh && ownership.sessionCurrent()) {
      await refreshCurrentStaffView({ silent: !isCurrent() || !ownsRow() });
    }
    return true;
  } catch (err) {
    const uncertain = !err?.status || err.status >= 500;
    if (uncertain && ownership.sessionCurrent()) await refreshCurrentStaffView({ silent: true });
    if (isCurrent() && ownsRow()) await showAlert(uncertain
      ? 'Could not confirm whether the duplicate was closed. Refresh before retrying.'
      : err.message || 'Could not close duplicate request.');
    return false;
  }
}

function closeEditModal() {
  document.getElementById('editModal').close();
  const url = new URL(window.location.href);
  url.searchParams.delete('request');
  url.searchParams.delete('requestType');
  window.history.replaceState(null, '', url.pathname + url.search + url.hash);
}

document.getElementById('close-modal-x').addEventListener('click', closeEditModal);
document.getElementById('close-modal-btn').addEventListener('click', closeEditModal);
