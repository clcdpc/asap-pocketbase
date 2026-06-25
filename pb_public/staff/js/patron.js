import { pb, verifiedNewSuggestionBarcode, setVerifiedNewSuggestionBarcode, currentWorkflowOrgScopeId, workflowSettings } from './state.js';
import { setFieldChecked, getFieldChecked, isSuperAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { loadTab, escapeAttr } from './grid.js';
import { renderPatronContext } from './modals.js';
import { populateLibrarySelector } from './settings.js';
let selectedStaffPatronPickupContext = null;

async function populateNewSuggestionLibrarySelector() {
  const select = document.getElementById('new-suggestion-library');
  const group = document.getElementById('new-suggestion-library-group');
  if (!select || !group) return;

  if (!isSuperAdminStaff()) {
    group.classList.add('hidden');
    select.value = '';
    select.disabled = true;
    return;
  }

  group.classList.remove('hidden');
  select.disabled = false;
  select.replaceChildren();
  const placeholder = document.createElement('option');
  placeholder.value = '';
  placeholder.textContent = 'Select a library...';
  select.appendChild(placeholder);

  try {
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-suggestion-modal'
    });

    orgs.forEach(org => {
      const opt = document.createElement('option');
      opt.value = org.organizationId;
      opt.textContent = `${org.displayName || org.name} (ID ${org.organizationId})`;
      select.appendChild(opt);
    });

    if (currentWorkflowOrgScopeId && currentWorkflowOrgScopeId !== 'all' && currentWorkflowOrgScopeId !== 'system') {
      select.value = currentWorkflowOrgScopeId;
    }

    if (!select.dataset.suggestionLibraryBound) {
      select.addEventListener('change', () => {
        const btn = document.getElementById('btn-submit-new');
        if (btn) btn.disabled = staffSuggestionRequiresLibrarySelection() || !document.getElementById('new-pickup-branch').value;
        if (!staffSuggestionRequiresLibrarySelection()) {
          clearNewSuggestionError();
        } else {
          showNewSuggestionError('Select a servicing library before creating a staff suggestion.');
        }
      });
      select.dataset.suggestionLibraryBound = 'true';
    }
  } catch (err) {
    console.error('Failed to load libraries for suggestion modal:', err);
  }
}

document.getElementById('btn-new-suggestion').addEventListener('click', async () => {
  document.getElementById('new-suggestion-form').reset();
  setFieldChecked('new-autohold', true);
  setFieldChecked('staff-new-suggestion-email-patron', false);
  document.getElementById('new-exact-publication-date').value = '';
  clearNewSuggestionError();
  resetStaffPatronLookup();
  document.getElementById('newSuggestionModal').showModal();
  document.getElementById('close-new-modal-btn').focus();
  document.getElementById('new-barcode').focus();
});

document.getElementById('close-new-modal-x').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});
document.getElementById('close-new-modal-btn').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});
document.getElementById('close-patron-search-x').addEventListener('click', () => {
  document.getElementById('patronSearchDialog').close();
});
document.getElementById('close-patron-search-btn').addEventListener('click', () => {
  document.getElementById('patronSearchDialog').close();
});

document.getElementById('new-barcode').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    document.getElementById('btn-lookup-patron').click();
  }
});

document.getElementById('new-barcode').addEventListener('input', () => {
  const patronQuery = document.getElementById('new-barcode').value.trim();
  if (verifiedNewSuggestionBarcode && patronQuery !== verifiedNewSuggestionBarcode) {
    resetStaffPatronLookup();
    showLookupResult('Patron lookup changed. Look up the patron again before entering suggestion details.', 'warning');
  }
});

document.getElementById('btn-lookup-patron').addEventListener('click', async () => {
  const patronQuery = document.getElementById('new-barcode').value.trim();
  const btn = document.getElementById('btn-lookup-patron');
  clearNewSuggestionError();
  resetStaffPatronLookup();

  if (!patronQuery) {
    showLookupResult('Enter a patron barcode or name before lookup.', 'danger');
    document.getElementById('new-barcode').focus();
    return;
  }

  btn.disabled = true;
  btn.textContent = 'Looking up...';
  try {
    const data = await authorizedJson('/api/asap/staff/patron-lookup', {
      method: 'POST',
      body: staffSuggestionLibraryPayload({ query: patronQuery })
    });

    if (data.status === 'multiple' && Array.isArray(data.results)) {
      updatePatronSearchScopeNotice(data);
      openPatronSearchDialog(patronQuery, data.results, data);
      return;
    }

    updatePatronSearchScopeNotice(data);
    applySelectedPatron(data);
  } catch (err) {
    updatePatronSearchScopeNotice(err.response || err.lookupResponse || null);
    showLookupResult(err.message || 'No patron found. Try barcode, name, or first name then last name.', 'danger');
    document.getElementById('new-barcode').focus();
  } finally {
    btn.disabled = false;
    btn.textContent = 'Lookup Patron';
  }
});

document.getElementById('new-suggestion-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const barcode = document.getElementById('new-barcode').value.trim();
  if (!verifiedNewSuggestionBarcode || barcode !== verifiedNewSuggestionBarcode) {
    showNewSuggestionError('Look up and verify the patron before submitting a suggestion.');
    document.getElementById('new-barcode').focus();
    return;
  }

  if (staffSuggestionRequiresLibrarySelection()) {
    showNewSuggestionError('Select a servicing library before creating a staff suggestion.');
    return;
  }

  const payload = staffSuggestionLibraryPayload({
    barcode: barcode,
    title: document.getElementById('new-title').value,
    author: document.getElementById('new-author').value,
    identifier: document.getElementById('new-identifier').value,
    format: document.getElementById('new-format').value,
    publication: document.getElementById('new-publication').value,
    preferredPickupBranchId: document.getElementById('new-pickup-branch').value,
    exactPublicationDate: document.getElementById('new-exact-publication-date').value,
    notes: document.getElementById('new-notes').value,
    autohold: getFieldChecked('new-autohold'),
    emailPatronConfirmation: getFieldChecked('staff-new-suggestion-email-patron')
  });
  if (!payload.preferredPickupBranchId) {
    showNewSuggestionError('Choose a preferred pickup location before submitting.');
    return;
  }

  clearNewSuggestionError();
  const btn = document.getElementById('btn-submit-new');
  btn.disabled = true;
  btn.textContent = 'Submitting...';

  try {
    await authorizedJson('/api/asap/staff/suggestions', {
      method: 'POST',
      body: payload
    });

    document.getElementById('newSuggestionModal').close();
    loadTab('suggestion');
  } catch (err) {
    showNewSuggestionError(err.message || 'Failed to create suggestion');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Submit';
  }
});

function staffSuggestionRequiresLibrarySelection() {
  if (!isSuperAdminStaff()) return false;
  const select = document.getElementById('new-suggestion-library');
  if (staffSuggestionLibrarySelectorVisible() && !select.value) {
    return true;
  }
  const scopeId = String(currentWorkflowOrgScopeId || '').trim();
  return (!scopeId || scopeId === 'all' || scopeId === 'system') && (!staffSuggestionLibrarySelectorVisible() || !select.value);
}

function staffSuggestionLibrarySelectorVisible() {
  const select = document.getElementById('new-suggestion-library');
  const group = document.getElementById('new-suggestion-library-group');
  return !!(select && group && !group.classList.contains('hidden'));
}

function staffSuggestionLibraryPayload(payload) {
  const next = Object.assign({}, payload || {});
  const select = document.getElementById('new-suggestion-library');
  if (isSuperAdminStaff() && staffSuggestionLibrarySelectorVisible() && select.value) {
    next.libraryOrgId = select.value;
  } else {
    const scopeId = String(currentWorkflowOrgScopeId || '').trim();
    if (isSuperAdminStaff() && scopeId && scopeId !== 'all' && scopeId !== 'system') {
      next.libraryOrgId = scopeId;
    }
  }
  return next;
}

function updatePatronSearchScopeNotice(data) {
  const notice = document.getElementById('new-patron-search-scope-notice');
  if (!notice) return;

  const isLimited = data && typeof data.patronSearchLimitedToLibrary === 'boolean'
    ? data.patronSearchLimitedToLibrary
    : !((workflowSettings || {}).allowAnyRegisteredCardLogin);

  if (isLimited) {
    notice.textContent = 'Patron search is limited to patrons registered at your library because of your login settings.';
    notice.className = 'mt-2 alert alert-light border py-2 small';
    notice.classList.remove('hidden');
    return;
  }

  notice.textContent = 'Patron search includes any registered Polaris card. Suggestions will be created for your library.';
  notice.className = 'mt-2 alert alert-info py-2 small';
  notice.classList.remove('hidden');
}

function applySelectedPatron(data) {
  const barcode = String(data.barcode || '').trim();
  if (!barcode) {
    showLookupResult('Could not verify selected patron.', 'danger');
    document.getElementById('new-barcode').focus();
    return;
  }

  setVerifiedNewSuggestionBarcode(barcode);
  document.getElementById('new-barcode').value = barcode;
  setNewSuggestionDetailsEnabled(true);
  populateStaffPickupSelector(data);

  // Match the layout of the edit modal, but expanded by default
  renderPatronContext(data, {
    containerSelector: '#newSuggestionModal .asap-dialog-edit-body',
    blockId: 'new-patron-context',
    expanded: true,
    anchorSelector: '#new-suggestion-details',
    insertAfter: false
  });

  // Hide the old simple lookup result
  const oldResult = document.getElementById('new-lookup-result');
  if (oldResult) {
    oldResult.classList.add('hidden');
    oldResult.textContent = '';
  }

  document.getElementById('new-title').focus();
}

function patronSearchElements() {
  return {
    dialog: document.getElementById('patronSearchDialog'),
    summary: document.getElementById('patron-search-summary'),
    status: document.getElementById('patron-search-status'),
    results: document.getElementById('patron-search-results')
  };
}

function openPatronSearchDialog(query, results, meta) {
  const els = patronSearchElements();
  if (!els.dialog) return;

  els.summary.textContent = `Multiple patrons matched "${query}". Choose the correct patron.`;
  els.status.className = 'alert alert-light border py-2 px-3 small';
  els.status.textContent = `${results.length} result${results.length === 1 ? '' : 's'} shown.`;
  updatePatronSearchScopeNotice(meta || null);

  els.results.replaceChildren();
  results.forEach((result, index) => {
    const name = patronLookupName(result) || result.name || 'Patron';
    const barcode = result.barcode || '';
    const library = result.libraryOrgName || 'Library not returned';
    const row = document.createElement('div');
    row.className = 'polaris-search-result';
    const title = document.createElement('div');
    title.className = 'polaris-search-result-title';
    title.textContent = name;
    const metaLine = document.createElement('div');
    metaLine.className = 'polaris-search-result-meta';
    metaLine.textContent = `Barcode: ${barcode} | Library: ${library}`;
    const actions = document.createElement('div');
    actions.className = 'polaris-search-result-actions';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'btn btn-sm btn-primary patron-search-select';
    button.setAttribute('data-result-index', String(index));
    button.textContent = 'Use this patron';
    actions.appendChild(button);
    row.append(title, metaLine, actions);
    els.results.appendChild(row);
  });

  els.results.querySelectorAll('.patron-search-select').forEach(button => {
    button.addEventListener('click', async () => {
      const index = parseInt(button.getAttribute('data-result-index') || '-1', 10);
      const result = results[index];
      if (!result || !result.barcode) return;
      try {
        const selected = await fetchSelectedPatronByBarcode(result.barcode);
        els.dialog.close();
        applySelectedPatron(selected);
      } catch (err) {
        showLookupResult(err.message || 'No patron found. Try barcode, name, or first name then last name.', 'danger');
      }
    });
  });

  if (!els.dialog.open) {
    els.dialog.showModal();
  }
}

export function resetStaffPatronLookup() {
  selectedStaffPatronPickupContext = null;
  setVerifiedNewSuggestionBarcode('');
  clearNewSuggestionError();
  clearNewSuggestionDetails();
  setNewSuggestionDetailsEnabled(false);
  document.getElementById('new-lookup-result').className = 'mt-2 hidden';
  document.getElementById('new-lookup-result').textContent = '';
  updatePatronSearchScopeNotice(null);
  const ctx = document.getElementById('new-patron-context');
  if (ctx) ctx.remove();
  resetPickupSelector();
}

export function clearNewSuggestionDetails() {
  document.getElementById('new-title').value = '';
  document.getElementById('new-author').value = '';
  document.getElementById('new-identifier').value = '';
  document.getElementById('new-format').selectedIndex = 0;
  document.getElementById('new-publication').selectedIndex = 0;
  document.getElementById('new-exact-publication-date').value = '';
  document.getElementById('new-notes').value = '';
  setFieldChecked('new-autohold', true);
}

export async function setNewSuggestionDetailsEnabled(enabled) {
  document.getElementById('new-suggestion-details').classList.toggle('hidden', !enabled);
  document.querySelectorAll('.new-detail-field').forEach(field => {
    field.disabled = !enabled;
  });
  
  if (enabled) {
    await populateNewSuggestionLibrarySelector();
  }

  document.getElementById('btn-submit-new').disabled = !enabled || staffSuggestionRequiresLibrarySelection() || !document.getElementById('new-pickup-branch').value;
  if (enabled && staffSuggestionRequiresLibrarySelection()) {
    showNewSuggestionError('Select a servicing library before creating a staff suggestion.');
  }
}

async function fetchSelectedPatronByBarcode(barcode) {
  return authorizedJson('/api/asap/staff/patron-lookup', {
    method: 'POST',
    body: staffSuggestionLibraryPayload({ barcode: String(barcode || '').trim() })
  });
}

function resetPickupSelector() {
  const select = document.getElementById('new-pickup-branch');
  const warning = document.getElementById('new-pickup-branch-warning');
  if (!select) return;
  select.replaceChildren();
  const blank = document.createElement('option');
  blank.value = '';
  blank.textContent = 'Select a pickup location...';
  select.appendChild(blank);
  select.value = '';
  select.disabled = true;
  if (warning) {
    warning.textContent = '';
    warning.classList.add('hidden');
  }
}

function populateStaffPickupSelector(data) {
  const select = document.getElementById('new-pickup-branch');
  const warning = document.getElementById('new-pickup-branch-warning');
  const submit = document.getElementById('btn-submit-new');
  if (!select) return;

  selectedStaffPatronPickupContext = data || null;
  resetPickupSelector();

  const branches = Array.isArray(data.pickupBranches) ? data.pickupBranches : [];
  branches.forEach((branch) => {
    const option = document.createElement('option');
    option.value = String(branch.id || '');
    option.textContent = String(branch.label || branch.name || branch.id || '');
    select.appendChild(option);
  });
  select.disabled = branches.length === 0;
  select.value = String(data.selectedPickupBranchId || '');

  const message = String(data.pickupBranchWarning || (branches.length === 0 ? 'No pickup locations are currently available for this patron.' : ''));
  if (warning) {
    warning.textContent = message;
    warning.classList.toggle('hidden', !message);
  }

  if (submit) {
    submit.disabled = !select.value || staffSuggestionRequiresLibrarySelection();
  }

  if (!select.dataset.pickupBranchBound) {
    select.addEventListener('change', () => {
      if (submit) submit.disabled = !select.value || staffSuggestionRequiresLibrarySelection();
    });
    select.dataset.pickupBranchBound = 'true';
  }
}

export function showLookupResult(message, type) {
  const result = document.getElementById('new-lookup-result');
  result.className = 'mt-2 alert alert-' + type + ' py-2';
  result.textContent = message;
}

export function clearNewSuggestionError() {
  const el = document.getElementById('new-error-summary');
  if (!el) return;
  el.textContent = '';
  el.classList.add('hidden');
}

export function showNewSuggestionError(message) {
  const text = String(message || 'Failed to create suggestion');
  const el = document.getElementById('new-error-summary');
  if (!el) return;
  el.textContent = text;
  el.classList.remove('hidden');
}

export function patronLookupName(data) {
  const name = [data.nameFirst, data.nameLast].filter(Boolean).join(' ').trim();
  return name || 'barcode found';
}

export async function openNewSuggestionForPatron(barcode) {
  if (!barcode) return;
  document.getElementById('new-suggestion-form').reset();
  setFieldChecked('new-autohold', true);
  setFieldChecked('staff-new-suggestion-email-patron', false);
  document.getElementById('new-exact-publication-date').value = '';
  clearNewSuggestionError();
  resetStaffPatronLookup();
  
  document.getElementById('new-barcode').value = barcode;
  document.getElementById('newSuggestionModal').showModal();
  document.getElementById('close-new-modal-btn').focus();
  
  // Trigger lookup automatically
  document.getElementById('btn-lookup-patron').click();
}
