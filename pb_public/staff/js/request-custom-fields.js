function inputId(key) {
  return 'edit-custom-field-' + String(key || '').replace(/[^a-z0-9_]/g, '_');
}

export function renderEditCustomFields(row, definitions, rules) {
  const container = document.getElementById('edit-custom-fields');
  if (!container) return;
  const existing = row && row.customFields ? row.customFields : {};
  const nodes = [];
  (definitions || []).forEach(def => {
    const mode = rules && rules[def.key] ? rules[def.key].mode : 'hidden';
    const historical = existing[def.key];
    if (mode === 'hidden' && !historical) return;
    nodes.push(renderEditCustomField(def, mode === 'required', historical));
  });
  Object.keys(existing).forEach(key => {
    if ((definitions || []).some(def => def.key === key)) return;
    nodes.push(renderHistoricalCustomField(key, existing[key]));
  });
  container.replaceChildren(...nodes);
}

function renderEditCustomField(def, required, historical) {
  const row = document.createElement('div');
  row.className = 'form-group row custom-field-row';
  row.setAttribute('data-custom-field-key', def.key);

  const label = document.createElement('label');
  label.className = 'col-5 col-form-label small font-weight-bold';
  label.setAttribute('for', inputId(def.key));
  label.textContent = def.label + (required ? ' *' : '');

  const col = document.createElement('div');
  col.className = 'col-7';
  let input;
  if (def.type === 'textarea') {
    input = document.createElement('textarea');
  } else if (def.type === 'select') {
    input = document.createElement('select');
    input.appendChild(new Option('', ''));
    (def.options || []).filter(opt => opt.enabled !== false).forEach(opt => {
      input.appendChild(new Option(opt.label, opt.id));
    });
  } else {
    input = document.createElement('input');
    input.type = 'text';
  }
  input.id = inputId(def.key);
  input.className = def.type === 'select' ? 'custom-select custom-field-input' : 'form-control custom-field-input';
  input.setAttribute('data-custom-field-key', def.key);
  input.required = required;
  input.setAttribute('aria-required', required ? 'true' : 'false');
  input.value = historical ? String(historical.value || '') : '';
  col.appendChild(input);

  row.append(label, col);
  return row;
}

function renderHistoricalCustomField(key, value) {
  const row = document.createElement('div');
  row.className = 'form-group row custom-field-row custom-field-historical';
  const label = document.createElement('div');
  label.className = 'col-5 col-form-label small font-weight-bold';
  label.textContent = value && value.label ? value.label : key;
  const col = document.createElement('div');
  col.className = 'col-7 small text-muted';
  col.textContent = value && value.displayValue ? value.displayValue : String(value && value.value || '');
  row.append(label, col);
  return row;
}

export function collectEditCustomFieldValues() {
  const values = {};
  document.querySelectorAll('#edit-custom-fields .custom-field-input').forEach(input => {
    const key = input.getAttribute('data-custom-field-key');
    if (key) values[key] = input.value;
  });
  return values;
}

export function renderEditCustomFieldsForTest(row, definitions, rules) {
  renderEditCustomFields(row, definitions, rules);
}
