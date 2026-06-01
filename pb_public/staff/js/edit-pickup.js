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
    refresh: document.getElementById('edit-pickup-refresh-btn'),
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
  if (els.refresh) {
    els.refresh.disabled = true;
    els.refresh.textContent = 'Refresh';
  }
  if (els.warning) {
    els.warning.textContent = '';
    els.warning.classList.add('hidden');
  }
  if (els.status) {
    els.status.textContent = '';
  }
}

async function fetchEditPickupOptions(id, options = {}) {
  return authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/pickup-options`, {
    method: 'POST',
    body: JSON.stringify({
      forceRefresh: !!options.forceRefresh
    })
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

  const branches = context.pickupBranches || [];
  const unavailable = !!context.pickupOptionsUnavailable || branches.length === 0;

  els.select.replaceChildren();
  const blank = document.createElement('option');
  blank.value = '';
  blank.textContent = branches.length
    ? 'Select a pickup location...'
    : 'Pickup locations unavailable';
  els.select.appendChild(blank);

  branches.forEach((branch) => {
    const opt = document.createElement('option');
    opt.value = String(branch.id || '');
    opt.textContent = String(branch.label || branch.id || '');
    els.select.appendChild(opt);
  });

  els.select.value = context.selectedPickupBranchId || '';
  els.select.disabled = !!context.readOnly || unavailable;
  if (els.save) {
    els.save.disabled = !!context.readOnly || unavailable || !els.select.value;
  }
  if (els.refresh) {
    els.refresh.disabled = !!context.readOnly;
    els.refresh.classList.toggle('hidden', false);
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
    const parts = [];
    if (context.currentPreferredPickupBranchName) {
      parts.push(`Current Polaris pickup: ${context.currentPreferredPickupBranchName}`);
    } else {
      parts.push('Current Polaris pickup is not set.');
    }

    if (!branches.length && !context.readOnly) {
      parts.push('Use Refresh to try loading pickup options again.');
    }

    els.status.textContent = parts.join(' ');
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

function renderEditPickupLoadError(err) {
  const els = pickupEls();

  if (els.warning) {
    els.warning.textContent = err.message || 'Could not load pickup locations.';
    els.warning.classList.remove('hidden');
  }

  if (els.status) {
    els.status.textContent = '';
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

export async function loadEditPickupForRequest(row, options = {}) {
  resetEditPickupUi();
  const els = pickupEls();
  const requestedId = String((row && row.id) || '').trim();
  editPickupRequestId = requestedId;

  if (!row || row.type === 'additional_copy') {
    if (els.group) els.group.classList.add('hidden');
    return;
  }
  if (els.group) els.group.classList.remove('hidden');

  if (!requestedId) return;

  if (els.refresh) els.refresh.disabled = true;

  try {
    const context = await fetchEditPickupOptions(requestedId, options);
    if (editPickupRequestId !== requestedId) return;
    if (String((document.getElementById('edit-id') || {}).value || '').trim() !== requestedId) return;
    renderEditPickupOptions(context || {});
  } catch (err) {
    if (editPickupRequestId !== requestedId) return;
    if (String((document.getElementById('edit-id') || {}).value || '').trim() !== requestedId) return;
    renderEditPickupLoadError(err);
  } finally {
    if (editPickupRequestId === requestedId && els.refresh) {
      els.refresh.disabled = !!(editPickupContext && editPickupContext.readOnly);
    }
  }
}

async function handleEditPickupSave() {
  const els = pickupEls();
  const activeId = String(editPickupRequestId || '').trim();
  if (!activeId || !els.select || !els.select.value) return;

  if (els.save) {
    els.save.disabled = true;
    els.save.textContent = 'Saving...';
  }

  try {
    const result = await saveEditPickupPreference(
      activeId,
      els.select.value,
      editPickupContext && editPickupContext.currentPreferredPickupBranchId
    );
    const updated = result && result.request ? result.request : null;
    if (updated && updated.id) {
      updateRequestInMemory(updated);
      updateRecentSuggestion(updated);
    }
    showToast(result && result.pickupChanged ? 'Pickup preference updated.' : 'Pickup preference saved.', 'success');
    if (String(editPickupRequestId || '').trim() === activeId) {
      await loadEditPickupForRequest(updated || { id: activeId });
    }
  } catch (err) {
    if (err.status === 409) {
      await showAlert(err.message || 'Pickup preference changed in Polaris. Reloading pickup options.');
      if (String(editPickupRequestId || '').trim() === activeId) {
        await loadEditPickupForRequest({ id: activeId });
      }
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

async function handleEditPickupRefresh() {
  if (!editPickupRequestId) return;

  const els = pickupEls();
  if (els.refresh) {
    els.refresh.disabled = true;
    els.refresh.textContent = 'Refreshing...';
  }

  try {
    await loadEditPickupForRequest({ id: editPickupRequestId }, { forceRefresh: true });
  } finally {
    if (els.refresh) {
      els.refresh.textContent = 'Refresh';
      els.refresh.disabled = !!(editPickupContext && editPickupContext.readOnly);
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

const refreshBtn = document.getElementById('edit-pickup-refresh-btn');
if (refreshBtn) {
  refreshBtn.addEventListener('click', handleEditPickupRefresh);
}
