import { pb, currentStatus, currentSuggestions, allSuggestions } from './state.js';
import { isAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { refreshCurrentStaffView, escapeAttr } from './grid.js';

export function undoConfirmMessage(type) {
  if (type === 'additional_copy') {
    return 'Undo action and return this request to Additional Copies?';
  }
  return 'Undo action and return this suggestion to Suggestions?';
}

export async function undoRow(id) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;

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
          editedBy: pb.authStore.model.username
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

export async function deleteClosedRequest(id) {
  if (!isAdminStaff()) return;
  const confirmed = await showConfirm('Delete this closed request?', 'This cannot be undone.');
  if (!confirmed) return;
  try {
    await authorizedJson(`/api/asap/staff/requests/${encodeURIComponent(id)}`, { method: 'DELETE' });
    showToast('Closed request deleted.', 'success');
    refreshCurrentStaffView();
  } catch (err) {
    await showAlert(err.message || 'Could not delete closed request.');
  }
}

export async function closeDuplicateRequest(id) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;
  const confirmed = await showConfirm('Close this duplicate request?', 'The patron already has an open request or hold for this BIB ID.');
  if (!confirmed) return;
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
        editedBy: pb.authStore.model.username
      }
    });
    showToast('Duplicate request closed.', 'success');
    refreshCurrentStaffView();
  } catch (err) {
    await showAlert(err.message || 'Could not close duplicate request.');
  }
}

document.getElementById('close-modal-x').addEventListener('click', () => {
  document.getElementById('editModal').close();
});
document.getElementById('close-modal-btn').addEventListener('click', () => {
  document.getElementById('editModal').close();
});
