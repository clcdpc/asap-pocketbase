import { additionalFieldDefinitions, setAdditionalFieldDefinitions } from './state.js';

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
  editor.replaceChildren(...additionalFieldDefinitions.map((def, index) => renderDefinitionRow(def, index)));
}

function renderDefinitionRow(def, index) {
  const row = document.createElement('div');
  row.className = 'additional-field-row border rounded p-2 mb-2';
  row.setAttribute('data-additional-field-key', def.key || '');

  const header = document.createElement('div');
  header.className = 'd-flex justify-content-between align-items-center mb-2';

  const title = document.createElement('strong');
  title.textContent = def.label || 'Additional field';

  const actions = document.createElement('div');
  actions.className = 'btn-group btn-group-sm';
  actions.append(
    button('btn btn-outline-secondary', 'Up', () => {
      syncFromDom();
      if (index > 0) {
        const next = additionalFieldDefinitions.slice();
        const tmp = next[index - 1];
        next[index - 1] = next[index];
        next[index] = tmp;
        setAdditionalFieldDefinitions(next);
        rerenderAndDirty();
      }
    }),
    button('btn btn-outline-secondary', 'Down', () => {
      syncFromDom();
      if (index < additionalFieldDefinitions.length - 1) {
        const next = additionalFieldDefinitions.slice();
        const tmp = next[index + 1];
        next[index + 1] = next[index];
        next[index] = tmp;
        setAdditionalFieldDefinitions(next);
        rerenderAndDirty();
      }
    }),
    button('btn btn-outline-danger', 'Remove', () => {
      syncFromDom();
      setAdditionalFieldDefinitions(additionalFieldDefinitions.filter((_, i) => i !== index));
      rerenderAndDirty();
    })
  );

  header.append(title, actions);

  const grid = document.createElement('div');
  grid.className = 'form-row';
  grid.append(
    fieldColumn('Label', textInput('additional-field-label-input', def.label || '', 'Additional field label')),
    fieldColumn('Key', textInput('additional-field-key-input', def.key || '', 'Additional field key')),
    fieldColumn('Type', typeSelect(def.type || 'text')),
    fieldColumn('Help text', textInput('additional-field-help-input', def.helpText || '', 'Additional field help text'))
  );

  const enabledWrap = document.createElement('div');
  enabledWrap.className = 'custom-control custom-checkbox mb-2';
  const enabled = document.createElement('input');
  enabled.type = 'checkbox';
  enabled.className = 'custom-control-input additional-field-enabled-check';
  enabled.id = `additional-field-enabled-${index}`;
  enabled.checked = def.enabled !== false;
  const enabledLabel = document.createElement('label');
  enabledLabel.className = 'custom-control-label';
  enabledLabel.setAttribute('for', enabled.id);
  enabledLabel.textContent = 'Enabled';
  enabledWrap.append(enabled, enabledLabel);

  const options = document.createElement('div');
  options.className = 'additional-field-options';
  (def.options || []).forEach((opt, optIndex) => options.appendChild(renderOptionRow(opt, optIndex)));

  const addOption = button('btn btn-sm btn-outline-secondary mt-1 additional-field-add-option', 'Add option', () => {
    syncFromDom();
    const next = additionalFieldDefinitions.slice();
    const current = Object.assign({}, next[index] || {});
    current.options = Array.isArray(current.options) ? current.options.slice() : [];
    current.options.push({ id: '', label: '', enabled: true, sortOrder: (current.options.length + 1) * 10 });
    next[index] = current;
    setAdditionalFieldDefinitions(next);
    rerenderAndDirty();
  });

  row.append(header, grid, enabledWrap, options, addOption);
  return row;
}

function fieldColumn(labelText, control) {
  const col = document.createElement('div');
  col.className = 'form-group col-md-3';
  const label = document.createElement('label');
  label.className = 'small text-muted mb-1';
  label.textContent = labelText;
  col.append(label, control);
  return col;
}

function textInput(className, value, ariaLabel) {
  const input = document.createElement('input');
  input.className = `form-control form-control-sm ${className}`;
  input.value = value;
  input.setAttribute('aria-label', ariaLabel);
  return input;
}

function typeSelect(value) {
  const select = document.createElement('select');
  select.className = 'form-control form-control-sm additional-field-type-select';
  ['text', 'textarea', 'select'].forEach(type => select.appendChild(option(type, type)));
  select.value = value;
  return select;
}

function renderOptionRow(opt, optIndex) {
  const row = document.createElement('div');
  row.className = 'additional-field-option-row d-flex mb-1';

  const id = document.createElement('input');
  id.className = 'form-control form-control-sm additional-field-option-id-input mr-1';
  id.value = opt.id || '';
  id.setAttribute('aria-label', 'Option id');

  const label = document.createElement('input');
  label.className = 'form-control form-control-sm additional-field-option-label-input mr-1';
  label.value = opt.label || '';
  label.setAttribute('aria-label', 'Option label');

  const remove = button('btn btn-sm btn-outline-danger additional-field-remove-option', 'Remove', (event) => {
    const defRow = event.target.closest('.additional-field-row');
    const defRows = Array.from(document.querySelectorAll('.additional-field-row'));
    const defIndex = defRows.indexOf(defRow);
    syncFromDom();
    const next = additionalFieldDefinitions.slice();
    const current = Object.assign({}, next[defIndex] || {});
    current.options = (current.options || []).filter((_, i) => i !== optIndex);
    next[defIndex] = current;
    setAdditionalFieldDefinitions(next);
    rerenderAndDirty();
  });

  row.append(id, label, remove);
  return row;
}

export function collectAdditionalFieldDefinitions() {
  return Array.from(document.querySelectorAll('.additional-field-row')).map((row, index) => ({
    key: row.querySelector('.additional-field-key-input')?.value.trim() || '',
    label: row.querySelector('.additional-field-label-input')?.value.trim() || '',
    type: row.querySelector('.additional-field-type-select')?.value || 'text',
    helpText: row.querySelector('.additional-field-help-input')?.value.trim() || '',
    enabled: !!row.querySelector('.additional-field-enabled-check')?.checked,
    sortOrder: (index + 1) * 10,
    options: Array.from(row.querySelectorAll('.additional-field-option-row')).map((optRow, optIndex) => ({
      id: optRow.querySelector('.additional-field-option-id-input')?.value.trim() || '',
      label: optRow.querySelector('.additional-field-option-label-input')?.value.trim() || '',
      enabled: true,
      sortOrder: (optIndex + 1) * 10
    }))
  }));
}

document.addEventListener('input', event => {
  if (event.target.closest && event.target.closest('#additional-fields-editor')) {
    syncFromDom();
  }
});

document.addEventListener('change', event => {
  if (event.target.closest && event.target.closest('#additional-fields-editor')) {
    syncFromDom();
  }
});

document.addEventListener('click', event => {
  if (event.target && event.target.id === 'btn-add-additional-field') {
    syncFromDom();
    setAdditionalFieldDefinitions(additionalFieldDefinitions.concat([{
      key: '',
      label: '',
      type: 'text',
      helpText: '',
      enabled: true,
      sortOrder: (additionalFieldDefinitions.length + 1) * 10,
      options: []
    }]));
    rerenderAndDirty();
  }
});
