import { additionalFieldDefinitions, currentAdditionalFieldDefinitions, currentFormatRules, setAdditionalFieldDefinitions } from './state.js';
import { collectPatronFormatRules, renderPatronFormatRulesEditor } from './settings-ui.js';

function option(value, label) {
  return new Option(label || value, value);
}

function syncFromDom() {
  setAdditionalFieldDefinitions(collectAdditionalFieldDefinitions());
}

function notifySettingsDirty() {
  const editor = document.getElementById('additional-fields-editor');
  if (editor) editor.dispatchEvent(new Event('input', { bubbles: true }));
}

function rerenderAndDirty() {
  renderAdditionalFieldsEditor(additionalFieldDefinitions);
  notifySettingsDirty();
}

function button(className, label, onClick) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = className;
  btn.textContent = label;
  btn.addEventListener('click', onClick);
  return btn;
}

export function renderAdditionalFieldsEditor(definitions = additionalFieldDefinitions) {
  const editor = document.getElementById('additional-fields-editor');
  if (!editor) return;
  setAdditionalFieldDefinitions(Array.isArray(definitions) ? definitions : []);

  const table = document.createElement('table');
  table.className = 'table table-sm mb-0';

  const thead = document.createElement('thead');
  const headRow = document.createElement('tr');
  [['', 'format-drag-col'], ['Enabled', 'additional-field-enabled-col'], ['Label', ''], ['Type', ''], ['', 'text-right']].forEach(([label, className]) => {
    const th = document.createElement('th');
    th.textContent = label;
    if (className) th.className = className;
    headRow.appendChild(th);
  });
  thead.appendChild(headRow);
  table.appendChild(thead);

  const tbody = document.createElement('tbody');
  tbody.id = 'additional-fields-body';

  additionalFieldDefinitions.forEach((def, index) => {
    const tr = document.createElement('tr');
    tr.className = 'additional-field-row';
    tr.setAttribute('data-index', index);
    tr.setAttribute('data-original-index', String(index));
    tr.setAttribute('data-field-id', def.id || '');
    tr.setAttribute('data-field-key', def.key || '');
    tr.setAttribute('data-help-text', def.helpText || '');
    tr.setAttribute('data-sort-order', String(def.sortOrder ?? ((index + 1) * 10)));
    tr.draggable = true;

    const dragTd = document.createElement('td');
    dragTd.className = 'format-drag-col align-middle';
    const dragHandle = document.createElement('span');
    dragHandle.className = 'text-muted additional-field-drag-handle';
    dragHandle.textContent = '\u2195';
    dragTd.appendChild(dragHandle);
    tr.appendChild(dragTd);

    const enabledTd = document.createElement('td');
    enabledTd.className = 'align-middle additional-field-enabled-col';
    const checkWrap = document.createElement('div');
    checkWrap.className = 'custom-control custom-checkbox';
    const check = document.createElement('input');
    check.type = 'checkbox';
    check.className = 'custom-control-input additional-field-enabled-check';
    check.id = `additional-field-enabled-${index}`;
    check.checked = def.enabled !== false;
    const checkLabel = document.createElement('label');
    checkLabel.className = 'custom-control-label';
    checkLabel.setAttribute('for', check.id);
    checkWrap.append(check, checkLabel);
    enabledTd.appendChild(checkWrap);
    tr.appendChild(enabledTd);

    const labelTd = document.createElement('td');
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-control form-control-sm additional-field-label-input';
    input.value = def.label || '';
    input.setAttribute('aria-label', 'Field label');
    labelTd.appendChild(input);
    tr.appendChild(labelTd);

    const typeTd = document.createElement('td');
    const select = document.createElement('select');
    select.className = 'form-control form-control-sm additional-field-type-select';
    [['text', 'Text'], ['textarea', 'Paragraph'], ['select', 'Drop-down']].forEach(([value, label]) => select.appendChild(option(value, label)));
    select.value = def.type || 'text';
    typeTd.appendChild(select);
    tr.appendChild(typeTd);

    const actionTd = document.createElement('td');
    actionTd.className = 'text-right';
    actionTd.appendChild(button('btn btn-sm btn-outline-danger', 'Remove', () => {
      syncFromDom();
      setAdditionalFieldDefinitions(additionalFieldDefinitions.filter((_, i) => i !== index));
      rerenderAndDirty();
    }));
    tr.appendChild(actionTd);

    tbody.appendChild(tr);

    if (def.type === 'select') {
      const optionsTr = document.createElement('tr');
      optionsTr.className = 'additional-field-options-row';
      optionsTr.setAttribute('data-parent-index', index);

      const spacerTd = document.createElement('td');
      spacerTd.colSpan = 2;
      optionsTr.appendChild(spacerTd);

      const optionsTd = document.createElement('td');
      optionsTd.colSpan = 3;
      optionsTd.className = 'pb-2';

      const options = document.createElement('div');
      options.className = 'additional-field-options d-flex flex-wrap align-items-center gap-1';
      (def.options || []).forEach((opt, optIndex) => options.appendChild(renderOptionRow(opt, optIndex)));

      const addBtn = button('btn btn-sm btn-outline-secondary additional-field-add-option ml-1', 'Add option', () => {
        syncFromDom();
        const next = additionalFieldDefinitions.slice();
        const current = Object.assign({}, next[index] || {});
        current.options = Array.isArray(current.options) ? current.options.slice() : [];
        current.options.push({ label: '', enabled: true, sortOrder: (current.options.length + 1) * 10 });
        next[index] = current;
        setAdditionalFieldDefinitions(next);
        rerenderAndDirty();
      });

      optionsTd.append(options, addBtn);
      optionsTr.appendChild(optionsTd);
      tbody.appendChild(optionsTr);
    }
  });

  table.appendChild(tbody);
  editor.replaceChildren(table);
}

function renderOptionRow(opt, optIndex) {
  const row = document.createElement('span');
  row.className = 'additional-field-option-row d-inline-flex align-items-center mr-2 mb-1';
  row.setAttribute('data-option-id', opt.id || opt.key || '');
  row.setAttribute('data-original-index', String(optIndex));
  row.setAttribute('data-sort-order', String(opt.sortOrder ?? ((optIndex + 1) * 10)));
  row.setAttribute('data-enabled', opt.enabled === false ? 'false' : 'true');

  const input = document.createElement('input');
  input.className = 'form-control form-control-sm additional-field-option-label-input';
  input.value = opt.label || '';
  input.setAttribute('aria-label', 'Option label');
  input.style.width = '160px';

  const remove = button('btn btn-sm btn-outline-danger additional-field-remove-option ml-1', '\u00D7', (event) => {
    const optionsRow = event.target.closest('.additional-field-options-row');
    const parentIndex = optionsRow ? parseInt(optionsRow.getAttribute('data-parent-index'), 10) : -1;
    if (parentIndex < 0) return;
    syncFromDom();
    const next = additionalFieldDefinitions.slice();
    const current = Object.assign({}, next[parentIndex] || {});
    current.options = (current.options || []).filter((_, i) => i !== optIndex);
    next[parentIndex] = current;
    setAdditionalFieldDefinitions(next);
    rerenderAndDirty();
  });

  row.append(input, remove);
  return row;
}

function normalizeKey(value) {
  return String(value || '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
}

export function collectAdditionalFieldDefinitions() {
  const rows = Array.from(document.querySelectorAll('#additional-fields-body > tr.additional-field-row'));
  const orderChanged = rows.some((row, index) => Number(row.getAttribute('data-original-index')) !== index);
  return rows.map((row, index) => {
    const label = row.querySelector('.additional-field-label-input')?.value.trim() || '';
    const optionRows = Array.from(row.parentElement.querySelectorAll(`.additional-field-options-row[data-parent-index="${row.getAttribute('data-index')}"] .additional-field-option-row`));
    const optionOrderChanged = optionRows.some((optionRow, optionIndex) => Number(optionRow.getAttribute('data-original-index')) !== optionIndex);
    return {
      ...(row.getAttribute('data-field-id') ? { id: row.getAttribute('data-field-id') } : {}),
      key: row.getAttribute('data-field-key') || normalizeKey(label),
      label,
      type: row.querySelector('.additional-field-type-select')?.value || 'text',
      helpText: row.getAttribute('data-help-text') || null,
      enabled: !!row.querySelector('.additional-field-enabled-check')?.checked,
      sortOrder: orderChanged ? (index + 1) * 10 : Number(row.getAttribute('data-sort-order') || ((index + 1) * 10)),
      options: optionRows.map((optRow, optIndex) => {
        const optionLabel = optRow.querySelector('.additional-field-option-label-input')?.value.trim() || '';
        return {
          id: optRow.getAttribute('data-option-id') || normalizeKey(optionLabel),
          label: optionLabel,
          enabled: optRow.getAttribute('data-enabled') !== 'false',
          sortOrder: optionOrderChanged ? (optIndex + 1) * 10 : Number(optRow.getAttribute('data-sort-order') || ((optIndex + 1) * 10))
        };
      })
    };
  });
}

document.addEventListener('input', event => {
  if (event.target.closest && event.target.closest('#additional-fields-editor')) {
    syncFromDom();
  }
});

document.addEventListener('change', event => {
  if (event.target.closest && event.target.closest('#additional-fields-editor')) {
    const enabledField = event.target.classList.contains('additional-field-enabled-check')
      ? event.target.closest('.additional-field-row')?.getAttribute('data-field-key') : null;
    const wasDisabled = enabledField && currentAdditionalFieldDefinitions.find(field => field.key === enabledField)?.enabled === false;
    const editedRules = enabledField ? collectPatronFormatRules() : null;
    syncFromDom();
    if (enabledField) {
      if (wasDisabled && additionalFieldDefinitions.find(field => field.key === enabledField)?.enabled !== false) {
        Object.entries(editedRules).forEach(([format, rule]) => {
          const preserved = currentFormatRules?.[format]?.customFields?.[enabledField];
          if (preserved) rule.customFields[enabledField] = structuredClone(preserved);
        });
      }
      renderPatronFormatRulesEditor(editedRules);
    }
    if (event.target.classList.contains('additional-field-type-select')) {
      rerenderAndDirty();
    }
  }
});

document.addEventListener('click', event => {
  if (event.target && event.target.id === 'btn-add-additional-field') {
    syncFromDom();
    setAdditionalFieldDefinitions(additionalFieldDefinitions.concat([{
      label: '',
      type: 'text',
      enabled: true,
      sortOrder: (additionalFieldDefinitions.length + 1) * 10,
      options: []
    }]));
    rerenderAndDirty();
  }
});

function isInteractiveDragSource(target) {
  return !!(
    target &&
    target.closest &&
    target.closest('input, select, textarea, button, a, [contenteditable="true"]')
  );
}

function additionalFieldRowForDragTarget(tbody, target) {
  if (!target) return null;
  if (target.classList.contains('additional-field-row')) return target;
  if (target.classList.contains('additional-field-options-row')) {
    const parentIndex = target.getAttribute('data-parent-index');
    return tbody.querySelector(`.additional-field-row[data-index="${parentIndex}"]`);
  }
  return null;
}

function additionalFieldOptionsRowForField(tbody, fieldRow) {
  if (!fieldRow) return null;
  const index = fieldRow.getAttribute('data-index');
  return tbody.querySelector(`.additional-field-options-row[data-parent-index="${index}"]`);
}

function isLowerHalfDrop(event, row) {
  const rect = row.getBoundingClientRect();
  return event.clientY > rect.top + rect.height / 2;
}

function insertFieldRowWithOptions(tbody, fieldRow, optionsRow, targetFieldRow, insertAfter) {
  const targetOptionsRow = additionalFieldOptionsRowForField(tbody, targetFieldRow);
  const referenceNode = insertAfter
    ? (targetOptionsRow ? targetOptionsRow.nextSibling : targetFieldRow.nextSibling)
    : targetFieldRow;

  tbody.insertBefore(fieldRow, referenceNode);
  if (optionsRow) {
    tbody.insertBefore(optionsRow, fieldRow.nextSibling);
  }
}

let draggingFieldRow = null;

document.addEventListener('dragstart', event => {
  if (isInteractiveDragSource(event.target)) {
    event.preventDefault();
    return;
  }

  const row = event.target.closest('#additional-fields-body > tr.additional-field-row');
  if (!row) return;
  draggingFieldRow = row;
  row.classList.add('additional-field-row-dragging');
  event.dataTransfer.effectAllowed = 'move';
});

document.addEventListener('dragend', () => {
  document.querySelectorAll('#additional-fields-body > tr').forEach(r => r.classList.remove('additional-field-row-dragging', 'additional-field-row-drop-target'));
  draggingFieldRow = null;
});

document.addEventListener('dragover', event => {
  const tbody = document.getElementById('additional-fields-body');
  if (!tbody || !draggingFieldRow) return;

  const rawTarget = event.target.closest('#additional-fields-body > tr');
  const targetField = additionalFieldRowForDragTarget(tbody, rawTarget);
  if (!targetField || targetField === draggingFieldRow) return;

  const draggedIndex = draggingFieldRow.getAttribute('data-index');
  if (
    rawTarget &&
    rawTarget.classList.contains('additional-field-options-row') &&
    rawTarget.getAttribute('data-parent-index') === draggedIndex
  ) {
    return;
  }

  event.preventDefault();
  event.dataTransfer.dropEffect = 'move';
  tbody.querySelectorAll('tr').forEach(r => r.classList.remove('additional-field-row-drop-target'));

  targetField.classList.add('additional-field-row-drop-target');
});

document.addEventListener('drop', event => {
  event.preventDefault();

  const tbody = document.getElementById('additional-fields-body');
  if (!tbody || !draggingFieldRow) return;

  const rawTarget = event.target.closest('#additional-fields-body > tr');
  const targetField = additionalFieldRowForDragTarget(tbody, rawTarget);
  if (!targetField || targetField === draggingFieldRow) return;

  const draggedIndex = draggingFieldRow.getAttribute('data-index');
  if (
    rawTarget &&
    rawTarget.classList.contains('additional-field-options-row') &&
    rawTarget.getAttribute('data-parent-index') === draggedIndex
  ) {
    return;
  }

  tbody.querySelectorAll('tr').forEach(r => r.classList.remove('additional-field-row-drop-target'));

  syncFromDom();

  const draggedOptions = additionalFieldOptionsRowForField(tbody, draggingFieldRow);
  const insertAfter = isLowerHalfDrop(event, targetField);

  insertFieldRowWithOptions(tbody, draggingFieldRow, draggedOptions, targetField, insertAfter);

  setAdditionalFieldDefinitions(collectAdditionalFieldDefinitions());
  rerenderAndDirty();
});
