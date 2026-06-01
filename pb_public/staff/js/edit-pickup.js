import { currentSuggestions, allSuggestions } from './state.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert } from './dialogs.js';
import { updateRecentSuggestion } from './recent-suggestions.js';

let editPickupContext = null;
let editPickupRequestId = '';

function pickupEls() {
  return {
    group: document.getElementById('edit-pickup-branch-group'),
    select: document.getElementById('edit-pickup-branch'),
    save: document.getElementById('edit-pickup-save-btn'),
    warning: document.getElementById('edit-pickup-warning'),
    status: document.getElementById('edit-pickup-status')
  };
}

function resetEditPickupUi() {
  editPickupContext = null;
  editPickupRequestId = '';
  const els = pickupEls();
  if (!els.select) return;
  els.select.replaceChildren();
  const opt = document.createElement('option');
  opt.value = '';
  opt.textContent = 'Loading pickup locations...';
  els.select.appendChild(opt);
  els.select.disabled = true;
  if (els.save) els.save.disabled = true;
  if (els.warning) {
    els.warning.textContent = '';
    els.warning.classList.add('hidden');
  }
  if (els.status) {
    els.status.textContent = '';
  }
}

async function fetchEditPickupOptions(id) {
  return authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/pickup-options`, {
    method: 'POST',
    body: JSON.stringify({})
  });
}

async function saveEditPickupPreference(id, selectedId, atLoadId) {
  return authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/pickup-preference`, {
    method: 'POST',
    body: JSON.stringify({
      preferredPickupBranchId: selectedId,
      currentPreferredPickupBranchIdAtLoad: atLoadId || ''
    })
  });
}

function renderEditPickupOptions(context) {
  const els = pickupEls();
  if (!els.select) return;

  editPickupContext = context || {};
  editPickupRequestId = String(context.requestId || editPickupRequestId || '').trim();

  els.select.replaceChildren();
  const blank = document.createElement('option');
  blank.value = '';
  blank.textContent = 'Select a pickup location...';
  els.select.appendChild(blank);

  (context.pickupBranches || []).forEach((branch) => {
    const opt = document.createElement('option');
    opt.value = String(branch.id || '');
    opt.textContent = String(branch.label || branch.id || '');
    els.select.appendChild(opt);
  });

  els.select.value = context.selectedPickupBranchId || '';
  els.select.disabled = !!context.readOnly || !(context.pickupBranches || []).length;
  if (els.save) {
    els.save.disabled = !!context.readOnly || !els.select.value;
  }

  const warnings = [];
  if (context.pickupBranchWarning) warnings.push(context.pickupBranchWarning);
  const snapshotId = String(context.requestSnapshotPickupBranchId || '');
  const liveId = String(context.currentPreferredPickupBranchId || '');
  if (snapshotId && liveId && snapshotId !== liveId) {
    warnings.push('Current Polaris preference differs from the pickup saved on this request.');
  }
  if (context.readOnly) {
    warnings.push('Pickup preference is read-only after the hold has been placed.');
  }

  if (els.warning) {
    els.warning.textContent = warnings.join(' ');
    els.warning.classList.toggle('hidden', warnings.length === 0);
  }
  if (els.status) {
    els.status.textContent = context.currentPreferredPickupBranchName
      ? `Current Polaris pickup: ${context.currentPreferredPickupBranchName}`
      : 'Current Polaris pickup is not set.';
  }
}

function updateRequestInMemory(updated) {
  [currentSuggestions, allSuggestions].forEach((list) => {
    const idx = list.findIndex((row) => row.id === updated.id);
    if (idx >= 0) {
      list[idx] = Object.assign({}, list[idx], updated);
    }
  });
}

export async function loadEditPickupForRequest(row) {
  resetEditPickupUi();
  const els = pickupEls();

  if (!row || row.type === 'additional_copy') {
    if (els.group) els.group.classList.add('hidden');
    return;
  }
  if (els.group) els.group.classList.remove('hidden');

  editPickupRequestId = String(row.id || '').trim();
  if (!editPickupRequestId) return;

  try {
    const context = await fetchEditPickupOptions(editPickupRequestId);
    renderEditPickupOptions(context || {});
  } catch (err) {
    if (els.warning) {
      els.warning.textContent = err.message || 'Could not load pickup locations.';
      els.warning.classList.remove('hidden');
    }
    if (els.select) {
      els.select.replaceChildren();
      const opt = document.createElement('option');
      opt.value = '';
      opt.textContent = 'Pickup locations unavailable';
      els.select.appendChild(opt);
      els.select.disabled = true;
    }
    if (els.save) els.save.disabled = true;
  }
}

async function handleEditPickupSave() {
  const els = pickupEls();
  if (!editPickupRequestId || !els.select || !els.select.value) return;

  if (els.save) {
    els.save.disabled = true;
    els.save.textContent = 'Saving...';
  }

  try {
    const result = await saveEditPickupPreference(
      editPickupRequestId,
      els.select.value,
      editPickupContext && editPickupContext.currentPreferredPickupBranchId
    );
    const updated = result && result.request ? result.request : null;
    if (updated && updated.id) {
      updateRequestInMemory(updated);
      updateRecentSuggestion(updated);
    }
    showToast(result && result.pickupChanged ? 'Pickup preference updated.' : 'Pickup preference saved.', 'success');
    await loadEditPickupForRequest(updated || { id: editPickupRequestId });
  } catch (err) {
    if (err.status === 409) {
      await showAlert(err.message || 'Pickup preference changed in Polaris. Reloading pickup options.');
      await loadEditPickupForRequest({ id: editPickupRequestId });
      return;
    }
    await showAlert(err.message || 'Could not save pickup preference.');
    if (els.save) els.save.disabled = !els.select.value;
  } finally {
    if (els.save) {
      els.save.textContent = 'Save pickup';
    }
  }
}

const selectEl = document.getElementById('edit-pickup-branch');
if (selectEl) {
  selectEl.addEventListener('change', () => {
    const els = pickupEls();
    if (els.save) {
      const readOnly = !!(editPickupContext && editPickupContext.readOnly);
      els.save.disabled = readOnly || !selectEl.value;
    }
  });
}

const saveBtn = document.getElementById('edit-pickup-save-btn');
if (saveBtn) {
  saveBtn.addEventListener('click', handleEditPickupSave);
}
