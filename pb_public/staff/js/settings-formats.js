import { formatMap, availableFormats, setAvailableFormats, currentFormatClaimRules, formatClaimStaffOptions } from './state.js';
import { setInlineStatus, showConfirm, markSettingsDirty } from './api.js';
import { escapeAttr } from './grid.js';
import { renderPatronFormatRulesEditor, collectPatronFormatRules } from './settings-ui.js';

export function renderFormatSettings() {
  const container = document.getElementById('format-settings-container');
  if (!container) return;

  // Use formatMap key order (reflects saved sortOrder from the backend)
  const allKeys = Object.keys(formatMap);

  // Compute patron position for enabled formats
  let patronPos = 0;
  const patronPositions = {};
  allKeys.forEach(key => {
    if (availableFormats.includes(key)) {
      patronPos++;
      patronPositions[key] = patronPos;
    }
  });

  container.replaceChildren();

  const help = document.createElement('p');
  help.className = 'small text-muted mb-2';
  help.textContent = 'Drag rows to reorder. Show controls whether the format appears in the patron dropdown. Auto-claim is library-only and does not inherit from system defaults.';
  container.appendChild(help);

  const tableWrap = document.createElement('div');
  tableWrap.className = 'table-responsive';
  const table = document.createElement('table');
  table.className = 'table table-sm mb-0';
  const thead = document.createElement('thead');
  const headRow = document.createElement('tr');
  [
    ['', 'format-drag-col'],
    ['Show', 'format-show-col'],
    ['Patron #', ''],
    ['Format key', 'format-key-col'],
    ['Display label', ''],
    ['Auto-claim staff', '']
  ].forEach(([label, className]) => {
    const th = document.createElement('th');
    if (className) th.className = className;
    th.textContent = label;
    headRow.appendChild(th);
  });
  thead.appendChild(headRow);
  table.appendChild(thead);

  const tbody = document.createElement('tbody');
  tbody.id = 'format-settings-body';
  const claimByFormat = formatClaimRulesByFormat();

  allKeys.forEach(key => {
    const isEnabled = availableFormats.includes(key);
    const pos = patronPositions[key];
    const tr = document.createElement('tr');
    tr.className = `format-setting-row${isEnabled ? '' : ' text-muted'}`;
    tr.setAttribute('data-key', key);
    tr.draggable = true;

    const dragTd = document.createElement('td');
    dragTd.className = 'align-middle text-muted format-drag-handle';
    dragTd.textContent = '\u2195';
    tr.appendChild(dragTd);

    const showTd = document.createElement('td');
    showTd.className = 'align-middle';
    const checkWrap = document.createElement('div');
    checkWrap.className = 'custom-control custom-checkbox';
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'custom-control-input format-enabled-check';
    check.id = `fmt-chk-${key}`;
    check.checked = isEnabled;
    const checkLabel = document.createElement('label');
    checkLabel.className = 'custom-control-label';
    checkLabel.setAttribute('for', check.id);
    checkWrap.append(check, checkLabel);
    showTd.appendChild(checkWrap);
    tr.appendChild(showTd);

    const posTd = document.createElement('td');
    posTd.className = 'align-middle text-center';
    const posSpan = document.createElement('span');
    posSpan.className = pos ? 'badge badge-primary' : 'text-muted';
    posSpan.textContent = pos ? String(pos) : '-';
    posTd.appendChild(posSpan);
    tr.appendChild(posTd);

    const keyTd = document.createElement('td');
    keyTd.className = 'align-middle';
    const code = document.createElement('code');
    code.textContent = key;
    keyTd.appendChild(code);
    tr.appendChild(keyTd);

    const labelTd = document.createElement('td');
    const labelWrap = document.createElement('div');
    labelWrap.className = 'd-flex align-items-center';
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-control form-control-sm format-label-input w-100';
    input.value = formatMap[key] || key;
    labelWrap.appendChild(input);
    if (!['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'].includes(key)) {
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'btn btn-sm btn-outline-danger btn-remove-format text-nowrap ml-2';
      remove.textContent = 'Remove Format';
      labelWrap.appendChild(remove);
    }
    labelTd.appendChild(labelWrap);
    tr.appendChild(labelTd);

    const claimTd = document.createElement('td');
    const select = document.createElement('select');
    select.className = 'form-control form-control-sm format-claim-staff-select';
    select.appendChild(new Option('No automatic claimant', ''));
    formatClaimStaffOptions.forEach(staff => {
      select.appendChild(new Option(staff.displayName || staff.username || 'Staff', staff.id || ''));
    });
    select.value = claimByFormat[key] || '';
    claimTd.appendChild(select);
    tr.appendChild(claimTd);

    tbody.appendChild(tr);
  });

  table.appendChild(tbody);
  tableWrap.appendChild(table);
  container.appendChild(tableWrap);

  initFormatDragSort();
}

function formatClaimRulesByFormat() {
  const byFormat = {};
  (currentFormatClaimRules || []).forEach(rule => {
    if (rule && rule.format) byFormat[rule.format] = rule.staffUserId || '';
  });
  return byFormat;
}

export function initFormatDragSort() {
  const tbody = document.getElementById('format-settings-body');
  if (!tbody) return;

  let draggingRow = null;

  tbody.addEventListener('dragstart', (e) => {
    draggingRow = e.target.closest('tr');
    if (draggingRow) {
      draggingRow.classList.add('format-row-dragging');
      e.dataTransfer.effectAllowed = 'move';
    }
  });

  tbody.addEventListener('dragend', () => {
    if (draggingRow) {
      draggingRow.classList.remove('format-row-dragging');
      draggingRow = null;
    }
    // Remove all drag-over highlights
    tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
  });

  tbody.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const target = e.target.closest('tr');
    if (target && target !== draggingRow) {
      tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
      target.classList.add('format-row-drop-target');
    }
  });

  tbody.addEventListener('drop', (e) => {
    e.preventDefault();
    const target = e.target.closest('tr');
    tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
    if (target && draggingRow && target !== draggingRow) {
      tbody.insertBefore(draggingRow, target);
      // Sync formatMap key order to match new DOM order
      syncFormatMapOrder();
      markSettingsDirty();
      // Re-render to update patron position numbers
      renderFormatSettings();
    }
  });
}

/**
 * Rebuild formatMap key order to match the current DOM row order.
 * This ensures that after drag-reorder, the in-memory formatMap
 * reflects the visual order so the next save preserves it.
 */
function syncFormatMapOrder() {
  const rows = document.querySelectorAll('.format-setting-row');
  const newMap = {};
  const newAvailable = [];
  rows.forEach(row => {
    const key = row.getAttribute('data-key');
    const label = row.querySelector('.format-label-input')?.value.trim() || formatMap[key] || key;
    const enabled = row.querySelector('.format-enabled-check')?.checked;
    newMap[key] = label;
    if (enabled) newAvailable.push(key);
  });
  // Clear and repopulate formatMap in new order
  Object.keys(formatMap).forEach(k => delete formatMap[k]);
  Object.assign(formatMap, newMap);
  // Update availableFormats
  availableFormats.length = 0;
  newAvailable.forEach(k => availableFormats.push(k));
}

export function collectFormatLabels() {
  const labels = {};
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const key = row.getAttribute('data-key');
    const label = row.querySelector('.format-label-input').value.trim();
    if (key && label) labels[key] = label;
  });
  return labels;
}

export function collectAvailableFormats() {
  const available = [];
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const key = row.getAttribute('data-key');
    const enabled = row.querySelector('.format-enabled-check').checked;
    if (key && enabled) available.push(key);
  });
  return available;
}

export function collectFormatOrder() {
  const order = [];
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const key = row.getAttribute('data-key');
    if (key) order.push(key);
  });
  return order;
}

export function collectFormatClaimRules() {
  const rules = [];
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const format = row.getAttribute('data-key');
    const staffUserId = row.querySelector('.format-claim-staff-select')?.value || '';
    if (format) rules.push({ format, staffUserId });
  });
  return rules;
}

export function updateModalFormatDropdowns() {
  ['edit-format', 'new-format'].forEach(id => {
    const select = document.getElementById(id);
    if (!select) return;

    // Keep current value
    const val = select.value;

    // Only include availableFormats
    select.innerHTML = availableFormats.map(k => `
      <option value="${escapeAttr(k)}">${escapeAttr(formatMap[k] || k)}</option>
    `).join('');

    // Try to restore value, or fallback to first
    select.value = availableFormats.includes(val) ? val : (availableFormats[0] || '');
  });
}

const btnAddFormat = document.getElementById('btn-add-format');
if (btnAddFormat) {
  btnAddFormat.addEventListener('click', () => {
    const keyInput = document.getElementById('new-format-key');
    const labelInput = document.getElementById('new-format-label');
    const rawKey = keyInput ? keyInput.value.trim() : '';
    const rawLabel = labelInput ? labelInput.value.trim() : '';
    const key = rawKey.toLowerCase().replace(/[^a-z0-9_]/g, '_').replace(/^_+|_+$/g, '');
    if (!key) {
      setInlineStatus('new-format-error', 'Enter a short format key, such as videogame.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    if (!/^[a-z0-9_]+$/.test(key)) {
      setInlineStatus('new-format-error', 'Use only letters, numbers, and underscores for the format key.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    if (formatMap[key]) {
      setInlineStatus('new-format-error', 'This format key already exists.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    const label = rawLabel || key.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    formatMap[key] = label;
    availableFormats.push(key);
    if (keyInput) keyInput.value = '';
    if (labelInput) labelInput.value = '';
    setInlineStatus('new-format-error', `Added ${label}. Save settings to keep this format.`, 'success');
    renderFormatSettings();
    renderPatronFormatRulesEditor(collectPatronFormatRules());
    markSettingsDirty();
  });
}

const formatSettingsContainer = document.getElementById('format-settings-container');
if (formatSettingsContainer) {
  formatSettingsContainer.addEventListener('click', async (e) => {
    if (e.target.classList.contains('btn-remove-format')) {
      const row = e.target.closest('tr');
      const key = row.getAttribute('data-key');
      if (await showConfirm('Remove format', `Remove format "${key}"? This will only remove it from the settings list. Existing suggestions with this format will remain in the database.`)) {
        delete formatMap[key];
        setAvailableFormats(availableFormats.filter(k => k !== key));
        renderFormatSettings();
        renderPatronFormatRulesEditor(collectPatronFormatRules());
        markSettingsDirty();
      }
    }
  });

  // Re-render when "Show" checkbox changes to update patron position numbers
  formatSettingsContainer.addEventListener('change', (e) => {
    if (e.target.classList.contains('format-enabled-check')) {
      syncFormatMapOrder();
      renderFormatSettings();
      updateModalFormatDropdowns();
      markSettingsDirty();
    }
    if (e.target.classList.contains('format-claim-staff-select')) {
      markSettingsDirty();
    }
  });
}

// Keep duplicate sender fields in sync between Email Settings and SMTP Settings
