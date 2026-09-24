import { staffSession, currentStatus, currentSuggestions, allSuggestions } from './state.js';
import { isAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { refreshCurrentStaffView, escapeAttr } from './grid.js';
import { findWorkflowRow, requestIdentity } from './request-identity.mjs';

export function undoConfirmMessage(type) {
  if (type === 'additional_copy') {
    return 'Undo action and return this request to Additional Copies?';
  }
  return 'Undo action and return this suggestion to Suggestions?';
}

export async function undoRow(identity) {
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row) return;
  const id = row.id;

  if (!await showConfirm('Undo action', undoConfirmMessage(row.type))) return;

  try {
    const url = row.type === 'additional_copy'
      ? `/api/asap/staff/additional-copies/${encodeURIComponent(id)}/reopen`
      : `/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`;
    const body = row.type === 'additional_copy'
      ? {}
      : {
          ...row,
          status: 'suggestion',
          editedBy: staffSession.staff?.username
        };

    await authorizedJson(url, {
      method: 'POST',
      body
    });
    refreshCurrentStaffView();
  } catch (err) {
    await showAlert(err.message || 'Error undoing action');
  }
}

export async function deleteClosedRequest(identity) {
  if (!isAdminStaff()) return;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row) return;
  const confirmed = await showConfirm('Delete this closed request?', 'This cannot be undone.');
  if (!confirmed) return;
  try {
    const url = row.type === 'additional_copy'
      ? `/api/asap/staff/additional-copies/${encodeURIComponent(row.id)}`
      : `/api/asap/staff/requests/${encodeURIComponent(row.id)}`;
    await authorizedJson(url, { method: 'DELETE' });
    showToast('Closed request deleted.', 'success');
    refreshCurrentStaffView();
  } catch (err) {
    await showAlert(err.message || 'Could not delete closed request.');
  }
}

export async function closeDuplicateRequest(identity, options = {}) {
  const { alreadyConfirmed = false, isCurrent = () => true, refresh = true } = options;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row || requestIdentity(row).type !== 'title_request') return;
  const id = row.id;
  if (!alreadyConfirmed && !await showConfirm('Close this duplicate request?',
    'The patron already has an open request or hold for this BIB ID.')) return false;
  if (!isCurrent()) return false;
  try {
    await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      body: {
        action: 'closeDuplicate',
        status: 'closed',
        title: row.title || '',
        author: row.author || '',
        identifier: row.identifier || '',
        bibid: row.bibid || '',
        format: row.format || '',
        publication: row.publication || '',
        exactPublicationDate: row.exactPublicationDate || '',
        notes: row.notes || '',
        editedBy: staffSession.staff?.username
      }
    });
    if (isCurrent()) showToast('Duplicate request closed.', 'success');
    if (refresh) await refreshCurrentStaffView({ silent: !isCurrent() });
    return true;
  } catch (err) {
    if (isCurrent()) await showAlert(err.message || 'Could not close duplicate request.');
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
