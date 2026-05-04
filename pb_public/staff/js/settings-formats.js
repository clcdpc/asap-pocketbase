import { formatMap, availableFormats, setAvailableFormats } from './state.js';
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

  container.innerHTML = `
    <p class="small text-muted mb-2">Drag rows to reorder. The <strong>Show</strong> checkbox controls whether the format appears in the patron dropdown. The <strong>Patron #</strong> column shows its position in the patron dropdown.</p>
    <div class="table-responsive">
      <table class="table table-sm mb-0">
        <thead>
          <tr>
            <th class="format-drag-col"></th>
            <th class="format-show-col" title="Check to show this format in the patron dropdown">Show</th>
            <th style="width: 60px; text-align: center;" title="Position in the patron format dropdown">Patron&nbsp;#</th>
            <th class="format-key-col">Format key</th>
            <th>Display label</th>
          </tr>
        </thead>
        <tbody id="format-settings-body">
          ${allKeys.map(key => {
            const isEnabled = availableFormats.includes(key);
            const pos = patronPositions[key];
            return `
            <tr class="format-setting-row${isEnabled ? '' : ' text-muted'}" data-key="${escapeAttr(key)}" draggable="true">
              <td class="align-middle text-muted format-drag-handle">&#8597;</td>
              <td class="align-middle">
                <div class="custom-control custom-checkbox">
                  <input type="checkbox" class="custom-control-input format-enabled-check" id="fmt-chk-${key}" ${isEnabled ? 'checked' : ''}>
                  <label class="custom-control-label" for="fmt-chk-${key}"></label>
                </div>
              </td>
              <td class="align-middle text-center">
                ${pos ? `<span class="badge badge-primary">${pos}</span>` : '<span class="text-muted">&mdash;</span>'}
              </td>
              <td class="align-middle"><code>${escapeAttr(key)}</code></td>
              <td>
                <div class="d-flex align-items-center">
                  <input type="text" class="form-control form-control-sm format-label-input w-100" value="${escapeAttr(formatMap[key] || key)}">
                  ${['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'].includes(key) ? '' : '<button type="button" class="btn btn-sm btn-outline-danger btn-remove-format text-nowrap ml-2">Remove Format</button>'}
                </div>
              </td>
            </tr>
          `}).join('')}
        </tbody>
      </table>
    </div>
  `;

  initFormatDragSort();
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
  });
}

// Keep duplicate sender fields in sync between Email Settings and SMTP Settings
