import { publicationOptions, defaultPublicationOptions } from '../state.js';
import { markSettingsDirty } from '../api.js';
import { showToast } from '../dialogs.js';

export function optionIdFromLabel(label, fallback = 'option') {
  const id = String(label || '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
  return id || fallback;
}

function isByteArray(value) {
  return Array.isArray(value) && value.length > 0 && value.every(item => Number.isInteger(item) && item >= 0 && item <= 255);
}

function decodeByteArray(value) {
  if (!isByteArray(value)) return null;
  for (let i = 0; i < value.length; i += 1) {
    if ([9, 10, 13, 32].includes(value[i])) continue;
    if (value[i] !== 91 && value[i] !== 123) return null;
    break;
  }
  if (typeof TextDecoder !== 'undefined') {
    try {
      return new TextDecoder('utf-8').decode(new Uint8Array(value));
    } catch (err) {
      // Fall through to the small decoder below for older embedded browsers.
    }
  }
  let output = '';
  for (let i = 0; i < value.length;) {
    const b1 = value[i++];
    if (b1 < 0x80) {
      output += String.fromCharCode(b1);
    } else if (b1 >= 0xC2 && b1 < 0xE0 && i < value.length) {
      const b2 = value[i++];
      output += String.fromCharCode(((b1 & 0x1F) << 6) | (b2 & 0x3F));
    } else if (b1 >= 0xE0 && b1 < 0xF0 && i + 1 < value.length) {
      const b2 = value[i++];
      const b3 = value[i++];
      output += String.fromCharCode(((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F));
    } else if (b1 >= 0xF0 && b1 < 0xF5 && i + 2 < value.length) {
      const b2 = value[i++];
      const b3 = value[i++];
      const b4 = value[i++];
      let codePoint = ((b1 & 0x07) << 18) | ((b2 & 0x3F) << 12) | ((b3 & 0x3F) << 6) | (b4 & 0x3F);
      codePoint -= 0x10000;
      output += String.fromCharCode(0xD800 + (codePoint >> 10), 0xDC00 + (codePoint & 0x3FF));
    } else {
      output += '\uFFFD';
    }
  }
  return output;
}

export function normalizeOptionList(options, fallbackLabels) {
  const fallback = (fallbackLabels || []).map((label, index) => ({ id: optionIdFromLabel(label, `option_${index + 1}`), label, enabled: true, sortOrder: (index + 1) * 10 }));
  let raw = [];
  const decoded = decodeByteArray(options);
  if (decoded !== null) options = decoded;
  if (Array.isArray(options)) {
    raw = options;
  } else {
    const text = String(options || '').trim();
    if (text.charAt(0) === '[') {
      try {
        const parsed = JSON.parse(text);
        raw = Array.isArray(parsed) ? parsed : [];
      } catch (err) {
        raw = [];
      }
    } else {
      raw = String(options || '').split(/\r?\n/).map(label => ({ label }));
    }
  }
  const seenLabels = new Set();
  const seenIds = new Set();
  const normalized = [];
  raw.forEach((item, index) => {
    const obj = item && typeof item === 'object' ? item : { label: item };
    const label = String(obj.label || obj.value || obj.name || '').trim();
    if (!label || seenLabels.has(label.toLowerCase())) return;
    seenLabels.add(label.toLowerCase());
    let id = String(obj.id || '').trim() || optionIdFromLabel(label, `option_${index + 1}`);
    const baseId = id;
    let suffix = 2;
    while (seenIds.has(id)) id = `${baseId}_${suffix++}`;
    seenIds.add(id);
    normalized.push({ id, label, enabled: obj.enabled !== false, sortOrder: Number(obj.sortOrder || ((index + 1) * 10)) });
  });
  normalized.sort((a, b) => (a.sortOrder || 0) - (b.sortOrder || 0));
  return normalized.length ? normalized : fallback;
}

export function enabledOptionLabels(options, fallbackLabels) {
  return normalizeOptionList(options, fallbackLabels).filter(option => option.enabled !== false).map(option => option.label);
}

export function updatePublicationOptionsUi(options) {
  const normalized = enabledOptionLabels(options, defaultPublicationOptions);
  document.querySelectorAll('.publication-options-select').forEach(select => {
    const val = select.value || normalized[0];
    select.innerHTML = '';
    normalized.forEach(opt => {
      const el = document.createElement('option');
      el.value = opt;
      el.textContent = opt;
      select.appendChild(el);
    });
    select.value = normalized.includes(val) ? val : normalized[0];
  });
}

export function setSelectValue(select, value) {
  if (!select) return;
  value = String(value || '').trim();
  if (value && !publicationOptions.includes(value)) {
    const item = document.createElement('option');
    item.value = value;
    item.textContent = value;
    select.appendChild(item);
  }
  select.value = value || publicationOptions[0] || '';
}

export function renderOptionListEditor(editorId, options, fallbackLabels) {
  const editor = document.getElementById(editorId);
  if (!editor) return;
  const list = normalizeOptionList(options, fallbackLabels);
  const rows = list.map(option => {
    const row = document.createElement('div');
    row.className = 'option-list-row';
    row.setAttribute('data-option-id', option.id);
    row.draggable = true;

    const dragHandle = document.createElement('div');
    dragHandle.className = 'text-muted option-drag-handle';
    dragHandle.setAttribute('aria-label', 'Drag to reorder');
    dragHandle.setAttribute('tabindex', '0');
    dragHandle.setAttribute('title', 'Drag to reorder');
    dragHandle.textContent = '\u2195';

    const labelInput = document.createElement('input');
    labelInput.type = 'text';
    labelInput.className = 'form-control form-control-sm option-list-label';
    labelInput.value = option.label;
    labelInput.setAttribute('aria-label', 'Option label');

    const checkboxWrap = document.createElement('div');
    checkboxWrap.className = 'form-check mb-0';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'form-check-input option-list-enabled';
    checkbox.id = `${editorId}-${option.id}`;
    if (option.enabled !== false) checkbox.checked = true;

    const checkboxLabel = document.createElement('label');
    checkboxLabel.className = 'form-check-label small';
    checkboxLabel.setAttribute('for', checkbox.id);
    checkboxLabel.textContent = 'Enabled';

    checkboxWrap.append(checkbox, checkboxLabel);

    const deleteButton = document.createElement('button');
    deleteButton.type = 'button';
    deleteButton.className = 'btn btn-sm btn-outline-danger option-list-delete';
    deleteButton.setAttribute('aria-label', 'Delete option');
    deleteButton.textContent = 'Delete';

    row.append(dragHandle, labelInput, checkboxWrap, deleteButton);
    return row;
  });

  editor.replaceChildren(...rows);
}

export function collectOptionList(editorId, fallbackLabels) {
  const editor = document.getElementById(editorId);
  if (!editor) return normalizeOptionList([], fallbackLabels);
  const seen = new Set();
  const rows = Array.from(editor.querySelectorAll('.option-list-row'));
  const options = rows.map((row, index) => {
    const input = row.querySelector('.option-list-label');
    const label = input ? input.value.trim() : '';
    if (!label) return null;
    const key = label.toLowerCase();
    if (seen.has(key)) throw new Error('Option labels must be unique within each list.');
    seen.add(key);
    const existingId = row.getAttribute('data-option-id') || '';
    return {
      id: existingId || optionIdFromLabel(label, `option_${index + 1}`),
      label,
      enabled: !!row.querySelector('.option-list-enabled')?.checked,
      sortOrder: (index + 1) * 10
    };
  }).filter(Boolean);
  if (!options.length) throw new Error('Each option list must include at least one label.');
  return options;
}

export function addOptionListRow(editorId, fallbackLabels) {
  const current = collectOptionList(editorId, fallbackLabels);
  let base = 'New option';
  let label = base;
  let i = 2;
  const labels = new Set(current.map(option => option.label.toLowerCase()));
  while (labels.has(label.toLowerCase())) label = `${base} ${i++}`;
  current.push({ id: optionIdFromLabel(label, `option_${current.length + 1}`), label, enabled: true, sortOrder: (current.length + 1) * 10 });
  renderOptionListEditor(editorId, current, fallbackLabels);
  markSettingsDirty();
}

export function handleOptionListClick(event) {
  const button = event.target.closest('button');
  if (!button) return;
  const editor = event.target.closest('.option-list-editor');
  if (!editor) return;
  const row = button.closest('.option-list-row');
  if (!row) return;
  const fallback = defaultPublicationOptions;
  const list = collectOptionList(editor.id, fallback);
  const index = Array.from(editor.querySelectorAll('.option-list-row')).indexOf(row);
  if (button.classList.contains('option-list-delete')) {
    if (list.length <= 1) {
      showToast('Each option list must include at least one label.', 'error');
      return;
    }
    list.splice(index, 1);
  } else {
    return;
  }
  list.forEach((option, nextIndex) => option.sortOrder = (nextIndex + 1) * 10);
  renderOptionListEditor(editor.id, list, fallback);
  markSettingsDirty();
}

let optionDraggingRow = null;

document.addEventListener('dragstart', (e) => {
  const row = e.target.closest('.option-list-row');
  if (row && e.target.closest('.option-list-editor')) {
    optionDraggingRow = row;
    row.classList.add('option-row-dragging');
    e.dataTransfer.effectAllowed = 'move';
  }
});

document.addEventListener('dragend', (e) => {
  if (optionDraggingRow) {
    optionDraggingRow.classList.remove('option-row-dragging');
    const editor = optionDraggingRow.closest('.option-list-editor');
    if (editor) {
      editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
    }
    optionDraggingRow = null;
  }
});

document.addEventListener('dragover', (e) => {
  const editor = e.target.closest('.option-list-editor');
  if (editor && optionDraggingRow && editor.contains(optionDraggingRow)) {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const target = e.target.closest('.option-list-row');
    if (target && target !== optionDraggingRow) {
      editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
      target.classList.add('option-row-drop-target');
    }
  }
});

document.addEventListener('drop', (e) => {
  const editor = e.target.closest('.option-list-editor');
  if (editor && optionDraggingRow && editor.contains(optionDraggingRow)) {
    e.preventDefault();
    const target = e.target.closest('.option-list-row');
    editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
    if (target && target !== optionDraggingRow) {
      editor.insertBefore(optionDraggingRow, target);
      const fallback = defaultPublicationOptions;
      const list = collectOptionList(editor.id, fallback);
      list.forEach((option, nextIndex) => option.sortOrder = (nextIndex + 1) * 10);
      renderOptionListEditor(editor.id, list, fallback);
      markSettingsDirty();
    }
  }
});
