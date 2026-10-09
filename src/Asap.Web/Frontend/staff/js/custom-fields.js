import { element, labeledInput } from './ui.js';

export function customFieldValue(value) {
  if (value === null || value === undefined) return '';
  if (typeof value === 'object' && !Array.isArray(value)) return String(value.value ?? '');
  return String(value);
}

export function renderCustomFieldEditor(container, request, configuration, formatCode) {
  const controls = new Map();
  const definitions = Array.isArray(configuration.additionalFieldDefinitions)
    ? configuration.additionalFieldDefinitions
    : [];
  const existing = request.customFields && typeof request.customFields === 'object'
    ? request.customFields
    : {};
  const customRules = configuration.formatRules?.[formatCode]?.customFields || {};
  const fields = [];

  for (const definition of definitions) {
    const key = String(definition.key || definition.id || '');
    if (!key) continue;
    const rule = customRules[key] || { mode: 'hidden' };
    const hasHistorical = Object.prototype.hasOwnProperty.call(existing, key);
    if (rule.mode === 'hidden' && !hasHistorical) continue;
    const currentValue = customFieldValue(existing[key]);
    let input;
    if (definition.type === 'textarea') {
      input = element('textarea', { maxlength: '2000' });
      input.value = currentValue;
    } else if (definition.type === 'select') {
      input = element('select');
      input.append(element('option', { value: '', text: '' }));
      const knownValues = [];
      for (const option of definition.options || []) {
        if (option.enabled === false) continue;
        const value = String(option.id || option.key || '');
        if (!value || knownValues.includes(value)) continue;
        knownValues.push(value);
        input.append(element('option', { value, text: option.label || value }));
      }
      if (currentValue && !knownValues.includes(currentValue)) {
        const historicalLabel = existing[key] && typeof existing[key] === 'object'
          ? existing[key].displayValue
          : null;
        input.append(element('option', { value: currentValue, text: historicalLabel || currentValue }));
      }
      input.value = currentValue;
    } else {
      input = element('input', { type: 'text', maxlength: '250', value: currentValue });
    }
    const required = rule.mode === 'required';
    const configuredLabel = rule.label || definition.label || key;
    const label = `${configuredLabel}${required ? ' *' : ''}`;
    input.required = required;
    input.setAttribute('aria-required', String(required));
    input.setAttribute('aria-label', label);
    controls.set(key, { definition, input, mode: rule.mode, label: configuredLabel });
    const field = labeledInput(label, input);
    if (definition.helpText) field.append(element('small', { text: definition.helpText }));
    fields.push(field);
  }
  container.replaceChildren(...fields);
  container.hidden = fields.length === 0;
  return controls;
}

export function collectCustomFields(request, controls) {
  const existing = request.customFields && typeof request.customFields === 'object'
    ? request.customFields
    : {};
  const result = { ...existing };
  for (const [key, value] of controls) {
    if (value.mode === 'hidden') continue;
    const normalized = value.input.value.trim();
    if (!normalized) {
      result[key] = null;
      continue;
    }
    result[key] = {
      label: value.label || value.definition.label || key,
      type: value.definition.type || 'text',
      value: normalized
    };
    if (value.definition.type === 'select') {
      result[key].displayValue = value.input.selectedOptions[0]?.textContent || normalized;
    }
  }
  return result;
}
