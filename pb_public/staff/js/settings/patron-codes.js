import { pb } from '../state.js';

let patronCodesStatus = 'not_loaded';
let patronCodesMessage = '';
let lastAllowedPatronCodeIds = [];

function selectedIdList(value) {
  return String(value || '')
    .split(',')
    .map(part => part.trim())
    .filter(Boolean);
}

function renderMessage(container, className, text) {
  const div = document.createElement('div');
  div.className = className;
  div.textContent = text;
  container.replaceChildren(div);
}

function patronCodeLabel(row) {
  const id = String(row.patronCodeId || '');
  const description = String(row.description || '').trim();
  return description ? `${description} (ID ${id})` : `Patron code ${id}`;
}

function updateRestrictionSummary() {
  const count = lastAllowedPatronCodeIds.length;
  const accordionSummary = document.getElementById('patron-code-accordion-summary');
  const status = document.getElementById('patron-code-restriction-status');

  if (accordionSummary) {
    accordionSummary.textContent = count
      ? `Limited to ${count} patron code${count === 1 ? '' : 's'}`
      : 'Select at least one patron code';
  }
  if (status) {
    status.className = count ? 'alert alert-success small' : 'alert alert-warning small';
    status.textContent = count
      ? `Access is limited to ${count} selected patron code${count === 1 ? '' : 's'}.`
      : 'Access is limited, but no patron codes are selected. Select at least one before saving.';
  }
}

function updateAccessModeUi() {
  const restrictionSettings = document.getElementById('patron-code-restriction-settings');
  if (restrictionSettings) {
    restrictionSettings.classList.toggle('hidden', !getPatronCodeEligibilityEnabled());
  }
  updateRestrictionSummary();
}

function updateSelectionFromChecklist() {
  const container = document.getElementById('allowed-patron-code-container');
  if (!container) return;
  lastAllowedPatronCodeIds = Array.from(container.querySelectorAll('input[type="checkbox"]:checked'))
    .map(checkbox => checkbox.value);
  const hidden = document.getElementById('allowed-patron-code-ids');
  if (hidden) hidden.value = lastAllowedPatronCodeIds.join(',');
  updateRestrictionSummary();
}

function renderPatronCodeChecklist(container, rows) {
  const fragment = document.createDocumentFragment();
  const hidden = document.createElement('input');
  hidden.type = 'hidden';
  hidden.id = 'allowed-patron-code-ids';
  hidden.value = lastAllowedPatronCodeIds.join(',');
  fragment.appendChild(hidden);

  rows.forEach((row, index) => {
    const id = String(row.patronCodeId || '');
    const wrapper = document.createElement('div');
    wrapper.className = 'patron-code-checklist-option';
    wrapper.setAttribute('data-search', patronCodeLabel(row).toLowerCase());

    const control = document.createElement('div');
    control.className = 'custom-control custom-checkbox';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'custom-control-input';
    checkbox.id = `patron-code-choice-${index}`;
    checkbox.value = id;
    checkbox.checked = lastAllowedPatronCodeIds.includes(id);
    checkbox.addEventListener('change', updateSelectionFromChecklist);

    const label = document.createElement('label');
    label.className = 'custom-control-label';
    label.setAttribute('for', checkbox.id);
    label.textContent = patronCodeLabel(row);

    control.append(checkbox, label);
    wrapper.appendChild(control);
    fragment.appendChild(wrapper);
  });

  container.replaceChildren(fragment);
  container.setAttribute('data-loaded', 'true');
  updateRestrictionSummary();
}

function setVisibleChecklistSelection(selectedIds) {
  const container = document.getElementById('allowed-patron-code-container');
  if (!container) return;
  container.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
    checkbox.checked = selectedIds.includes(checkbox.value);
  });
  const hidden = document.getElementById('allowed-patron-code-ids');
  if (hidden) hidden.value = selectedIds.join(',');
}

function filterChecklist() {
  const search = document.getElementById('patron-code-search');
  const container = document.getElementById('allowed-patron-code-container');
  if (!search || !container) return;
  const query = search.value.trim().toLowerCase();
  container.querySelectorAll('.patron-code-checklist-option').forEach(option => {
    option.classList.toggle('hidden', !!query && !option.getAttribute('data-search').includes(query));
  });
}

function setAllChecklistOptions(checked) {
  const container = document.getElementById('allowed-patron-code-container');
  if (!container) return;
  container.querySelectorAll('.patron-code-checklist-option:not(.hidden) input[type="checkbox"]').forEach(checkbox => {
    checkbox.checked = checked;
  });
  updateSelectionFromChecklist();
  const hidden = document.getElementById('allowed-patron-code-ids');
  if (hidden) hidden.dispatchEvent(new Event('change', { bubbles: true }));
}

function bindPatronCodeControls() {
  document.querySelectorAll('input[name="patron-code-access-mode"]').forEach(radio => {
    radio.addEventListener('change', updateAccessModeUi);
  });
  document.getElementById('patron-code-search')?.addEventListener('input', filterChecklist);
  document.getElementById('patron-code-select-all')?.addEventListener('click', () => setAllChecklistOptions(true));
  document.getElementById('patron-code-clear-all')?.addEventListener('click', () => setAllChecklistOptions(false));
}

export function setPatronCodeEligibilityMode(enabled) {
  const all = document.getElementById('patron-code-access-all');
  const restricted = document.getElementById('patron-code-access-restricted');
  if (all) all.checked = !enabled;
  if (restricted) restricted.checked = !!enabled;
  updateAccessModeUi();
}

export function getPatronCodeEligibilityEnabled() {
  const restricted = document.getElementById('patron-code-access-restricted');
  return !!(restricted && restricted.checked);
}

export function updatePatronCodesStatusUi(status, message) {
  patronCodesStatus = status || 'not_loaded';
  patronCodesMessage = message || '';
  const statusEl = document.getElementById('patron-codes-status-message');
  const container = document.getElementById('allowed-patron-code-container');
  if (container && patronCodesStatus !== 'loaded') {
    container.removeAttribute('data-loaded');
  }

  if (statusEl) {
    const classMap = {
      not_loaded: 'alert alert-info small mt-3 mb-0',
      loading: 'alert alert-info small mt-3 mb-0',
      loaded: 'alert alert-success small mt-3 mb-0',
      error: 'alert alert-warning small mt-3 mb-0'
    };
    statusEl.className = classMap[patronCodesStatus] || classMap.not_loaded;
    statusEl.textContent = patronCodesMessage || 'Polaris patron code sync status is unknown.';
  }

  if (container && patronCodesStatus !== 'loaded') {
    const messages = {
      loading: ['p-3 text-muted', 'Patron codes loading...'],
      error: ['p-3 text-warning', 'Polaris connected, but patron codes could not be loaded. Eligibility options may be unavailable until this sync succeeds.'],
      not_loaded: ['p-3 text-muted', 'Patron codes not loaded yet.']
    };
    const entry = messages[patronCodesStatus] || messages.not_loaded;
    renderMessage(container, entry[0], entry[1]);
  }
}

export async function renderPatronCodeEligibilityOptions(allowedIds) {
  const container = document.getElementById('allowed-patron-code-container');
  if (!container) return;
  lastAllowedPatronCodeIds = selectedIdList(allowedIds);
  if (container.getAttribute('data-loaded') === 'true') {
    setVisibleChecklistSelection(lastAllowedPatronCodeIds);
    updateRestrictionSummary();
    return;
  }

  if (patronCodesStatus === 'loading') {
    renderMessage(container, 'p-3 text-muted', 'Patron codes loading...');
    return;
  }

  if (patronCodesStatus === 'error') {
    renderMessage(container, 'p-3 text-warning', 'Polaris connected, but patron codes could not be loaded. Eligibility options may be unavailable until this sync succeeds.');
    return;
  }

  try {
    const rows = await pb.collection('polaris_patron_codes').getFullList({
      sort: 'description,patronCodeId',
      requestKey: 'polaris-patron-codes'
    });
    if (!rows.length) {
      renderMessage(container, 'p-3 text-muted', 'Patron codes have not been synced yet.');
      return;
    }
    updatePatronCodesStatusUi('loaded', `Polaris patron codes loaded. ${rows.length} code${rows.length === 1 ? '' : 's'} available.`);
    renderPatronCodeChecklist(container, rows);
  } catch (err) {
    console.error('Failed to load patron codes', err);
    updatePatronCodesStatusUi('error', 'Polaris connected, but patron codes could not be loaded. Eligibility options may be unavailable until this sync succeeds.');
    renderMessage(container, 'p-3 text-warning', 'Polaris connected, but patron codes could not be loaded. Eligibility options may be unavailable until this sync succeeds.');
  }
}

export function collectAllowedPatronCodeIds() {
  const hidden = document.getElementById('allowed-patron-code-ids');
  if (!hidden) return lastAllowedPatronCodeIds.join(',');
  return selectedIdList(hidden.value).join(',');
}

bindPatronCodeControls();
