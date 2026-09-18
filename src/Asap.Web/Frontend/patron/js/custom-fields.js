import { byId } from './dom.js';

export function renderCustomFields(definitions, rules) {
  const container = byId('additional-fields-container');
  if (!container) return;
  const nodes = [];
  (definitions || []).forEach(def => {
    const rule = rules && rules[def.key] ? rules[def.key] : {};
    const mode = rule.mode || 'hidden';
    if (mode === 'hidden' || def.enabled === false) return;
    nodes.push(renderField(def, mode === 'required', rule.label));
  });
  container.replaceChildren(...nodes);
}

function renderField(def, required, labelOverride) {
  const row = document.createElement('div');
  row.className = 'form-group row reqAuth custom-field-row';
  row.setAttribute('data-custom-field-key', def.key);

  const label = document.createElement('label');
  label.className = 'col-5 col-form-label';
  label.setAttribute('for', `custom-field-${def.key}`);
  label.textContent = (labelOverride || def.label) + (required ? ' *' : '');

  const col = document.createElement('div');
  col.className = 'col-7';
  let input;
  if (def.type === 'textarea') {
    input = document.createElement('textarea');
  } else if (def.type === 'select') {
    input = document.createElement('select');
    (def.options || []).filter(opt => opt.enabled !== false).forEach(opt => {
      input.appendChild(new Option(opt.label, opt.id));
    });
  } else {
    input = document.createElement('input');
    input.type = 'text';
  }
  input.id = `custom-field-${def.key}`;
  input.name = `customFields.${def.key}`;
  input.className = def.type === 'select' ? 'custom-select custom-field-input' : 'form-control custom-field-input';
  input.setAttribute('data-custom-field-key', def.key);
  input.required = required;
  input.setAttribute('aria-required', required ? 'true' : 'false');
  col.appendChild(input);
  if (def.helpText) {
    const help = document.createElement('small');
    help.className = 'form-text text-muted';
    help.textContent = def.helpText;
    col.appendChild(help);
  }
  row.append(label, col);
  return row;
}

export function collectCustomFieldValues() {
  const values = {};
  document.querySelectorAll('.custom-field-input').forEach(input => {
    const key = input.getAttribute('data-custom-field-key');
    if (key) values[key] = input.value;
  });
  return values;
}
