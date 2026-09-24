function element(tag, attributes = {}, children = []) {
  const value = document.createElement(tag);
  for (const [name, attribute] of Object.entries(attributes)) {
    if (attribute === null || attribute === undefined) continue;
    if (name === 'className') value.className = attribute;
    else if (name === 'text') value.textContent = attribute;
    else if (name === 'checked') value.checked = Boolean(attribute);
    else if (name === 'disabled') value.disabled = Boolean(attribute);
    else if (name === 'value') value.value = attribute;
    else value.setAttribute(name, String(attribute));
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child !== null && child !== undefined) value.append(
      child instanceof globalThis.Node ? child : document.createTextNode(String(child))
    );
  }
  return value;
}

function property(value, key) {
  if (!value || typeof value !== 'object') return undefined;
  if (Object.prototype.hasOwnProperty.call(value, key)) return value[key];
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  return value[pascal];
}

function clean(value) {
  if (value === null || value === undefined) return null;
  const text = String(value).trim();
  return text || null;
}

function stringId(value) {
  const result = clean(value);
  return result;
}

function clone(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

function array(value) {
  return Array.isArray(value) ? value : [];
}

function bool(value, fallback = false) {
  return value === undefined || value === null ? fallback : Boolean(value);
}

function numeric(value, fallback = 0) {
  const result = Number(value);
  return Number.isFinite(result) ? result : fallback;
}

function checkbox(label, checked, className = 'settings-inline-check') {
  const input = element('input', { type: 'checkbox', checked });
  return { input, wrapper: element('label', { className }, [input, element('span', { text: label })]) };
}

function select(options, value, attributes = {}) {
  const control = element('select', attributes);
  for (const option of options) {
    control.append(element('option', {
      value: option.value,
      text: option.label,
      selected: String(option.value) === String(value ?? '')
    }));
  }
  control.value = value === null || value === undefined ? '' : String(value);
  return control;
}

function field(label, control, className = 'settings-domain-field') {
  return element('label', { className }, [element('span', { text: label }), control]);
}

function command(iconName, label, handler, disabled = false) {
  const button = element('button', {
    type: 'button',
    className: 'settings-icon-button',
    title: label,
    'aria-label': label,
    disabled
  }, [element('i', { className: `fa fa-${iconName}`, 'aria-hidden': 'true' })]);
  button.addEventListener('click', handler);
  return button;
}

function actions(index, total, move, remove) {
  const buttons = [
    command('chevron-up', 'Move up', () => move(index, -1), index === 0),
    command('chevron-down', 'Move down', () => move(index, 1), index === total - 1)
  ];
  if (remove) buttons.push(command('trash-o', 'Delete', () => remove(index)));
  return element('div', { className: 'settings-row-actions' }, buttons);
}

function normalizeOption(value, index) {
  if (typeof value === 'string') {
    const label = clean(value) || `Option ${index + 1}`;
    return { id: label.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') || `option_${index + 1}`, label, enabled: true, sortOrder: (index + 1) * 10 };
  }
  const label = clean(property(value, 'label') ?? property(value, 'value') ?? property(value, 'name')) || `Option ${index + 1}`;
  return {
    id: stringId(property(value, 'id') ?? property(value, 'key')) || `option_${index + 1}`,
    label,
    enabled: bool(property(value, 'enabled'), true),
    sortOrder: numeric(property(value, 'sortOrder'), (index + 1) * 10)
  };
}

function normalizeFormats(values) {
  return array(values).map((value, index) => {
    const fields = {};
    for (const name of ['title', 'author', 'identifier', 'publication']) {
      const nested = property(value, name);
      fields[name] = {
        mode: clean(property(nested, 'mode') ?? property(value, `${name}Mode`)) || (name === 'title' ? 'required' : 'optional'),
        label: clean(property(nested, 'label') ?? property(value, `${name}Label`)) || (name === 'title' ? 'Title' : name === 'identifier' ? 'Identifier number' : name === 'publication' ? 'Publication Timing' : 'Author')
      };
    }
    return {
      id: stringId(property(value, 'id') ?? property(value, 'materialFormatId')),
      version: stringId(property(value, 'version')),
      code: clean(property(value, 'code')) || `format_${index + 1}`,
      ownerOrganizationId: numeric(property(value, 'ownerOrganizationId'), 1),
      label: clean(property(value, 'label')) || clean(property(value, 'code')) || `Format ${index + 1}`,
      sortOrder: numeric(property(value, 'sortOrder'), (index + 1) * 10),
      isEnabled: bool(property(value, 'isEnabled') ?? property(value, 'enabled'), true),
      messageBehavior: clean(property(value, 'messageBehavior')) || 'none',
      message: property(value, 'message') ?? null,
      fields,
      overridden: bool(property(value, 'overridden'))
    };
  });
}

function normalizeRule(value, fallback = {}) {
  const result = {
    code: clean(property(value, 'code') ?? property(value, 'format')) || clean(property(fallback, 'code')) || '',
    messageBehavior: clean(property(value, 'messageBehavior')) || clean(property(fallback, 'messageBehavior')) || 'none',
    message: property(value, 'message') ?? property(fallback, 'message') ?? null,
    customFields: {}
  };
  for (const name of ['title', 'author', 'identifier', 'publication']) {
    const source = property(value, name) || property(fallback, name) || {};
    result[name] = {
      mode: clean(property(source, 'mode')) || (name === 'title' ? 'required' : 'optional'),
      label: clean(property(source, 'label')) || (name === 'title' ? 'Title' : name === 'identifier' ? 'Identifier number' : name === 'publication' ? 'Publication Timing' : 'Author')
    };
  }
  const custom = property(value, 'customFields') || property(fallback, 'customFields');
  if (custom && typeof custom === 'object' && !Array.isArray(custom)) {
    for (const [key, item] of Object.entries(custom)) {
      result.customFields[key] = {
        mode: clean(property(item, 'mode')) || 'hidden',
        labelOverride: property(item, 'labelOverride') ?? property(item, 'label') ?? null
      };
    }
  }
  return result;
}

function normalizeCustomFields(values) {
  return array(values).map((value, index) => ({
    id: stringId(property(value, 'id')),
    key: clean(property(value, 'key') ?? property(value, 'fieldKey')) || `field_${index + 1}`,
    type: clean(property(value, 'type') ?? property(value, 'fieldType')) || 'text',
    label: clean(property(value, 'label')) || `Field ${index + 1}`,
    helpText: property(value, 'helpText') ?? null,
    enabled: bool(property(value, 'enabled'), true),
    sortOrder: numeric(property(value, 'sortOrder'), (index + 1) * 10),
    options: array(property(value, 'options')).map(normalizeOption)
  }));
}

const BUILTIN_TEMPLATE_KEYS = new Set([
  'suggestion_submitted',
  'purchase_approved',
  'already_owned',
  'hold_placed',
  'rejected'
]);

function normalizeTemplate(value, index) {
  return {
    id: stringId(property(value, 'id')),
    version: stringId(property(value, 'version')),
    organizationId: stringId(property(value, 'organizationId')),
    sourceTemplateId: stringId(property(value, 'sourceTemplateId')),
    templateKey: clean(property(value, 'templateKey') ?? property(value, 'key')) || `rejection:template_${index + 1}`,
    displayName: clean(property(value, 'displayName')),
    subject: property(value, 'subject') ?? property(value, 'subjectTemplate') ?? null,
    body: property(value, 'body') ?? property(value, 'bodyTemplate') ?? null,
    enabled: property(value, 'enabled') !== false && property(value, 'isHidden') !== true,
    isCustom: Boolean(property(value, 'isCustom') ?? property(value, 'custom')),
    overridden: Boolean(property(value, 'overridden')),
    subjectBaseline: property(value, 'subjectBaseline') ?? property(value, 'subject') ?? null,
    bodyBaseline: property(value, 'bodyBaseline') ?? property(value, 'body') ?? null,
    displayNameBaseline: property(value, 'displayNameBaseline') ?? property(value, 'displayName') ?? null,
    enabledBaseline: property(value, 'enabledBaseline') ?? (property(value, 'enabled') !== false),
    rawSubject: property(value, 'rawSubject') ?? null,
    rawBody: property(value, 'rawBody') ?? null,
    rawDisplayName: property(value, 'rawDisplayName') ?? null,
    hadOverride: Boolean(property(value, 'hadOverride') ?? property(value, 'overridden'))
  };
}

function snapshotSet(value) {
  return value && value.exists ? array(value.values) : [];
}

function rawSnapshot(configuredSystem, libraryOverride, key, system) {
  if (system) return property(configuredSystem, key);
  return property(libraryOverride, key);
}

export function createSettingsDomainEditors({ root, onChange = () => {} }) {
  const dom = {
    publication: root.querySelector('#publication-options-editor'),
    publicationUseSystem: root.querySelector('#publication-options-use-system'),
    publicationInheritField: root.querySelector('#publication-options-inherit-field'),
    creators: root.querySelector('#common-creators-editor'),
    creatorsUseSystem: root.querySelector('#common-creators-use-system'),
    creatorsInheritField: root.querySelector('#common-creators-inherit-field'),
    codes: root.querySelector('#patron-codes-editor'),
    codesUseSystem: root.querySelector('#patron-codes-use-system'),
    codesInheritField: root.querySelector('#patron-codes-inherit-field'),
    providers: root.querySelector('#external-search-provider-editor'),
    formats: root.querySelector('#material-formats-editor'),
    fields: root.querySelector('#additional-fields-editor'),
    rules: root.querySelector('#format-rules-editor'),
    claims: root.querySelector('#format-claim-rules-editor'),
    templates: root.querySelector('#email-templates-editor')
  };
  const addButtons = {
    publication: root.querySelector('#add-publication-option'),
    creators: root.querySelector('#add-common-creator'),
    codes: root.querySelector('#add-patron-code'),
    providers: root.querySelector('#add-provider'),
    formats: root.querySelector('#add-material-format'),
    fields: root.querySelector('#add-custom-field'),
    rules: root.querySelector('#add-format-rule'),
    claims: root.querySelector('#add-auto-claim-rule'),
    templates: root.querySelector('#add-email-template')
  };
  const state = {
    system: false,
    data: null,
    models: {},
    baseline: null,
    originalTemplates: [],
    deletedFormats: []
  };

  function currentOrganizationId() {
    const value = property(state.data, 'orgId');
    return value === 'system' || value === undefined ? 1 : numeric(value, 1);
  }

  function systemConfig(data) {
    return property(property(data, 'stored'), 'configuredSystem') || {};
  }

  function libraryConfig(data) {
    return property(property(data, 'stored'), 'libraryOverride') || {};
  }

  function effective(data, key, fallback = []) {
    return property(property(data, 'effective'), key) ?? fallback;
  }

  function useSet(key, system) {
    const systemValue = property(systemConfig(state.data), key);
    const libraryValue = property(libraryConfig(state.data), key);
    return system ? snapshotSet(systemValue) :
      libraryValue?.exists ? snapshotSet(libraryValue) : snapshotSet(systemValue);
  }

  function setValues(name, system) {
    const key = name === 'publication' ? 'publicationOptions' :
      name === 'creators' ? 'commonCreators' : 'allowedPatronCodeIds';
    const values = useSet(key, system);
    if (name === 'publication') return values.map(normalizeOption);
    if (name === 'creators') {
      return values.map(item => clean(typeof item === 'string' ? item : property(item, 'value'))).filter(Boolean);
    }
    return values.map(item => stringId(typeof item === 'string' ? item : property(item, 'id'))).filter(Boolean);
  }

  function setOverrideUi(input, wrapper, overridden) {
    if (!input || !wrapper) return;
    input.checked = Boolean(overridden);
    wrapper.hidden = state.system;
    for (const control of wrapper.closest('.settings-domain-block')?.querySelectorAll('[data-domain-editable]') || []) {
      control.disabled = !state.system && !input.checked;
    }
  }

  function rowControls(row) {
    return [...row.querySelectorAll('[data-domain-editable]')];
  }

  function updateRowDisabled(row, disabled) {
    for (const control of rowControls(row)) control.disabled = disabled;
    for (const button of row.querySelectorAll('.settings-row-actions button')) button.disabled = disabled;
  }

  function updateSetDisabled(container, input, addButton) {
    const disabled = !state.system && Boolean(input?.checked);
    updateRowDisabled(container, disabled);
    if (addButton) addButton.disabled = disabled;
  }

  function setEditorControls(type, container) {
    const input = type === 'publication'
      ? dom.publicationUseSystem
      : type === 'creator' ? dom.creatorsUseSystem : dom.codesUseSystem;
    const addButton = type === 'publication'
      ? addButtons.publication
      : type === 'creator' ? addButtons.creators : addButtons.codes;
    updateSetDisabled(container, input, addButton);
  }

  function renderSetRows(container, values, type) {
    if (!container) return;
    container.replaceChildren();
    if (values.length === 0) {
      container.append(element('p', { className: 'settings-empty', text: 'No values configured.' }));
      updateSetDisabled(
        container,
        type === 'code' ? dom.codesUseSystem : dom.creatorsUseSystem,
        type === 'code' ? addButtons.codes : addButtons.creators
      );
      return;
    }
    const choices = type === 'code'
      ? array(property(state.data, 'patronCodeChoices')).map((choice) => ({
        value: stringId(property(choice, 'id')),
        label: `${clean(property(choice, 'description')) || 'Patron code'} (${stringId(property(choice, 'id')) || '?'})`
      })).filter(choice => choice.value)
      : [];
    for (const [index, value] of values.entries()) {
      const currentValue = type === 'creator' ? value : property(value, 'id') ?? value;
      const currentId = stringId(currentValue);
      const options = [...choices];
      if (type === 'code' && currentId && !options.some(option => option.value === currentId)) {
        options.unshift({ value: currentId, label: `Unavailable code (${currentId})` });
      }
      const input = type === 'code'
        ? select([{ value: '', label: 'Select a Polaris patron code' }, ...options], currentId, { 'data-domain-editable': 'true' })
        : element('input', {
          type: 'text',
          value: currentValue,
          'data-domain-editable': 'true'
        });
      const row = element('div', { className: 'settings-editor-row', 'data-domain-row': 'true' }, [
        field(type === 'creator' ? 'Creator' : 'Patron-code ID', input),
        actions(index, values.length, (from, offset) => {
          const next = from + offset;
          if (next < 0 || next >= values.length) return;
          const current = readSetRows(container, type);
          [current[from], current[next]] = [current[next], current[from]];
          renderSetRows(container, current, type);
          onChange();
        }, from => {
          const current = readSetRows(container, type);
          current.splice(from, 1);
          renderSetRows(container, current, type);
          onChange();
        })
      ]);
      container.append(row);
    }
    setEditorControls(type === 'code' ? 'code' : 'creator', container);
  }

  function readSetRows(container, type) {
    return [...(container?.querySelectorAll('[data-domain-row]') || [])]
      .map(row => clean(row.querySelector('select, input')?.value))
      .filter(Boolean);
  }

  function renderPublication(values) {
    if (!dom.publication) return;
    dom.publication.replaceChildren();
    if (values.length === 0) dom.publication.append(element('p', { className: 'settings-empty', text: 'No publication options configured.' }));
    for (const [index, value] of values.entries()) {
      const key = element('input', { type: 'text', value: value.id, 'data-domain-editable': 'true' });
      const label = element('input', { type: 'text', value: value.label, 'data-domain-editable': 'true' });
      const enabled = element('input', { type: 'checkbox', checked: value.enabled, 'data-domain-editable': 'true' });
      const row = element('div', { className: 'settings-editor-row settings-option-row', 'data-domain-row': 'true' }, [
        field('Stable ID', key),
        field('Label', label),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Enabled' })]),
        actions(index, values.length, (from, offset) => reorder('publication', from, offset), from => remove('publication', from))
      ]);
      dom.publication.append(row);
    }
    setEditorControls('publication', dom.publication);
  }

  function readPublication() {
    return [...(dom.publication?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => ({
      id: stringId(row.querySelector('input')?.value) || `option_${index + 1}`,
      label: clean(row.querySelectorAll('input')[1]?.value) || `Option ${index + 1}`,
      enabled: Boolean(row.querySelector('input[type="checkbox"]')?.checked),
      sortOrder: (index + 1) * 10
    }));
  }

  function renderProviders(values) {
    if (!dom.providers) return;
    dom.providers.replaceChildren();
    if (values.length === 0) dom.providers.append(element('p', { className: 'settings-empty', text: 'No providers configured.' }));
    for (const [index, value] of values.entries()) {
      const id = stringId(value.id);
      const key = element('input', { type: 'text', value: value.key || '', readOnly: !state.system && Boolean(id), 'data-domain-editable': 'true' });
      const enabled = element('input', { type: 'checkbox', checked: value.isEnabled, 'data-domain-editable': 'true' });
      const override = checkbox('Override', state.system || value.overridden, 'settings-inline-check settings-domain-override');
      override.input.className = 'settings-domain-override-toggle';
      const row = element('div', {
        className: 'settings-editor-row settings-provider-row',
        'data-domain-row': 'true',
        'data-provider-id': id || '',
        'data-provider-key': value.key || ''
      }, [
        field('Provider key', key),
        field('Label', element('input', { type: 'text', value: value.label || '', 'data-domain-editable': 'true' })),
        field('URL template', element('input', { type: 'url', value: value.urlTemplate || '', 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Enabled' })]),
        state.system ? null : override.wrapper,
        actions(index, values.length, (from, offset) => reorder('providers', from, offset), from => remove('providers', from))
      ]);
      override.input.addEventListener('change', () => {
        updateRowDisabled(row, !state.system && !override.input.checked);
        onChange();
      });
      dom.providers.append(row);
      updateRowDisabled(row, !state.system && !override.input.checked);
    }
  }

  function readProviders() {
    return [...(dom.providers?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => {
      const inputs = [...row.querySelectorAll('input')];
      const override = row.querySelector('.settings-domain-override-toggle');
      return {
        id: stringId(row.dataset.providerId),
        key: clean(inputs[0]?.value),
        label: clean(inputs[1]?.value),
        urlTemplate: clean(inputs[2]?.value),
        isEnabled: Boolean(row.querySelector('input[type="checkbox"]:not(.settings-domain-override-toggle)')?.checked),
        sortOrder: (index + 1) * 10,
        overridden: state.system || Boolean(override?.checked),
        reset: !state.system && Boolean(override) && !override.checked
      };
    });
  }

  function fieldPair(name, value) {
    const group = element('div', { className: 'settings-format-field-group' });
    const readable = name.charAt(0).toUpperCase() + name.slice(1);
    const mode = select([
      { value: 'required', label: 'Required' },
      { value: 'optional', label: 'Optional' },
      { value: 'hidden', label: 'Hidden' }
    ], value.mode, {
      'data-format-field': name,
      'data-domain-editable': 'true',
      'aria-label': `${readable} mode`
    });
    const label = element('input', {
      type: 'text',
      value: value.label || '',
      'data-format-label': name,
      'data-domain-editable': 'true',
      'aria-label': `${readable} label`
    });
    group.append(element('span', { className: 'settings-format-field-name', text: name }), mode, label);
    return group;
  }

  function renderFormats(values) {
    if (!dom.formats) return;
    dom.formats.replaceChildren();
    if (values.length === 0) dom.formats.append(element('p', { className: 'settings-empty', text: 'No material formats configured.' }));
    for (const [index, value] of values.entries()) {
      const custom = value.ownerOrganizationId === currentOrganizationId() && !state.system;
      const code = element('input', { type: 'text', value: value.code, readOnly: Boolean(value.id) && !custom, 'data-domain-editable': 'true' });
      const enabled = element('input', { type: 'checkbox', checked: value.isEnabled, 'data-domain-editable': 'true' });
      const override = checkbox('Override', state.system || value.overridden, 'settings-inline-check settings-domain-override');
      override.input.className = 'settings-domain-override-toggle';
      const row = element('div', {
        className: 'settings-editor-row settings-format-row',
        'data-domain-row': 'true',
        'data-format-id': value.id || '',
        'data-format-owner': value.ownerOrganizationId,
        'data-format-version': value.version || ''
      }, [
        field('Code', code),
        field('Label', element('input', { type: 'text', value: value.label, 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Shown' })]),
        custom || state.system ? null : override.wrapper,
        actions(
          index,
          values.length,
          (from, offset) => reorder('formats', from, offset),
          custom ? from => removeFormat(from) : null
        )
      ]);
      if (!state.system && !custom) {
        override.input.addEventListener('change', () => {
          updateRowDisabled(row, !override.input.checked);
          onChange();
        });
      }
      dom.formats.append(row);
      if (!state.system && !custom) updateRowDisabled(row, !override.input.checked);
    }
  }

  function removeFormat(index) {
    const values = readFormats();
    const item = values[index];
    if (!item || !item.id || state.system || String(item.ownerOrganizationId) !== String(currentOrganizationId())) return;
    state.deletedFormats.push({ id: item.id, version: item.version || '' });
    values.splice(index, 1);
    renderFormats(values);
    onChange();
  }

  function readFormats() {
    return [...(dom.formats?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => {
      const inputs = [...row.querySelectorAll('input')];
      const override = row.querySelector('.settings-domain-override-toggle');
      const owner = numeric(row.dataset.formatOwner, 1);
      const code = clean(inputs[0]?.value);
      const result = {
        id: stringId(row.dataset.formatId),
        version: stringId(row.dataset.formatVersion),
        code,
        ownerOrganizationId: owner,
        label: clean(inputs[1]?.value) || code,
        sortOrder: (index + 1) * 10,
        isEnabled: Boolean(row.querySelector('input[type="checkbox"]:not(.settings-domain-override-toggle)')?.checked),
        overridden: state.system || owner === currentOrganizationId() || Boolean(override?.checked),
        reset: !state.system && owner !== currentOrganizationId() && Boolean(override) && !override.checked
      };
      return result;
    });
  }

  function renderFields(values) {
    if (!dom.fields) return;
    dom.fields.replaceChildren();
    if (values.length === 0) dom.fields.append(element('p', { className: 'settings-empty', text: 'No custom fields configured.' }));
    for (const [index, value] of values.entries()) {
      const row = element('div', {
        className: 'settings-editor-row settings-custom-field-row',
        'data-domain-row': 'true',
        'data-field-id': value.id || ''
      }, [
        field('Stable key', element('input', { type: 'text', value: value.key, 'data-domain-editable': 'true' })),
        field('Label', element('input', { type: 'text', value: value.label, 'data-domain-editable': 'true' })),
        field('Type', select([
          { value: 'text', label: 'Text' },
          { value: 'textarea', label: 'Long text' },
          { value: 'select', label: 'Select' }
        ], value.type, { 'data-domain-editable': 'true' })),
        field('Help text', element('input', { type: 'text', value: value.helpText || '', 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [element('input', { type: 'checkbox', checked: value.enabled, 'data-domain-editable': 'true' }), element('span', { text: 'Enabled' })]),
        element('div', { className: 'settings-custom-options', 'data-options-editor': 'true' }),
        actions(index, values.length, (from, offset) => reorder('fields', from, offset), from => remove('fields', from))
      ]);
      const optionsEditor = row.querySelector('[data-options-editor]');
      renderFieldOptions(optionsEditor, value.options, value.type === 'select');
      row.querySelector('select')?.addEventListener('change', event => {
        optionsEditor.hidden = event.target.value !== 'select';
        onChange();
      });
      dom.fields.append(row);
    }
  }

  function renderFieldOptions(container, values, visible) {
    if (!container) return;
    container.hidden = !visible;
    container.replaceChildren(element('strong', { text: 'Select options' }));
    for (const [index, value] of values.entries()) {
      const row = element('div', { className: 'settings-option-subrow', 'data-option-row': 'true' }, [
        element('input', { type: 'text', value: value.id, 'data-domain-editable': 'true', 'aria-label': 'Option stable ID' }),
        element('input', { type: 'text', value: value.label, 'data-domain-editable': 'true', 'aria-label': 'Option label' }),
        element('label', { className: 'settings-domain-check' }, [element('input', { type: 'checkbox', checked: value.enabled, 'data-domain-editable': 'true' }), element('span', { text: 'Enabled' })]),
        command('trash-o', 'Delete option', () => {
          const current = readFields();
          current.find(item => item.options[index] === value);
          const parent = container.closest('[data-domain-row]');
          const fieldIndex = [...dom.fields.querySelectorAll('[data-domain-row]')].indexOf(parent);
          current[fieldIndex].options.splice(index, 1);
          renderFields(current);
          onChange();
        })
      ]);
      container.append(row);
    }
    const add = element('button', { type: 'button', className: 'secondary-button' }, [element('i', { className: 'fa fa-plus', 'aria-hidden': 'true' }), ' Add option']);
    add.addEventListener('click', () => {
      const current = readFields();
      const parent = container.closest('[data-domain-row]');
      const fieldIndex = [...dom.fields.querySelectorAll('[data-domain-row]')].indexOf(parent);
      current[fieldIndex].options.push({ id: `option_${current[fieldIndex].options.length + 1}`, label: 'New option', enabled: true, sortOrder: (current[fieldIndex].options.length + 1) * 10 });
      renderFields(current);
      onChange();
    });
    container.append(add);
  }

  function readFields() {
    return [...(dom.fields?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => ({
      id: stringId(row.dataset.fieldId),
      key: clean(row.querySelectorAll('input')[0]?.value) || `field_${index + 1}`,
      label: clean(row.querySelectorAll('input')[1]?.value) || `Field ${index + 1}`,
      type: row.querySelector('select')?.value || 'text',
      helpText: clean(row.querySelectorAll('input')[2]?.value),
      enabled: Boolean(row.querySelector('input[type="checkbox"]')?.checked),
      sortOrder: (index + 1) * 10,
      options: [...(row.querySelectorAll('[data-option-row]') || [])].map((option, optionIndex) => ({
        id: stringId(option.querySelectorAll('input')[0]?.value) || `option_${optionIndex + 1}`,
        label: clean(option.querySelectorAll('input')[1]?.value) || `Option ${optionIndex + 1}`,
        enabled: Boolean(option.querySelector('input[type="checkbox"]')?.checked),
        sortOrder: (optionIndex + 1) * 10
      }))
    }));
  }

  function renderRules(values) {
    if (!dom.rules) return;
    dom.rules.replaceChildren();
    if (values.length === 0) dom.rules.append(element('p', { className: 'settings-empty', text: 'No format rules configured.' }));
    const customFields = readFields();
    for (const [index, value] of values.entries()) {
      const row = element('div', { className: 'settings-editor-row settings-rule-row', 'data-domain-row': 'true', 'data-rule-code': value.code }, [
        element('div', { className: 'settings-rule-heading' }, [element('strong', { text: value.code }), actions(index, values.length, (from, offset) => reorder('rules', from, offset), from => remove('rules', from))]),
        field('Message behavior', select([
          { value: 'none', label: 'No message' },
          { value: 'message', label: 'Use message' },
          { value: 'ebookMessage', label: 'eBook message' },
          { value: 'eaudiobookMessage', label: 'eAudiobook message' }
        ], value.messageBehavior, { 'data-rule-property': 'messageBehavior', 'data-domain-editable': 'true' })),
        field('Message', element('textarea', { rows: '3', 'data-rule-property': 'message', 'data-domain-editable': 'true' }, [value.message || ''])),
        element('div', { className: 'settings-rule-fields' }, ['title', 'author', 'identifier', 'publication'].map(name => fieldPair(name, value[name]))),
        element('div', { className: 'settings-rule-custom-fields' }, [
          element('strong', { text: 'Custom field rules' }),
          ...customFields.map(fieldValue => {
            const current = value.customFields[fieldValue.key] || { mode: 'hidden', labelOverride: null };
            return element('div', { className: 'settings-custom-rule-row', 'data-custom-rule-key': fieldValue.key }, [
              element('span', { text: fieldValue.label }),
              select([
                { value: 'hidden', label: 'Hidden' },
                { value: 'optional', label: 'Optional' },
                { value: 'required', label: 'Required' }
              ], current.mode, { 'data-custom-rule-property': 'mode', 'data-domain-editable': 'true' }),
              element('input', { type: 'text', value: current.labelOverride || '', placeholder: 'Label override', 'data-custom-rule-property': 'labelOverride', 'data-domain-editable': 'true' })
            ]);
          })
        ])
      ]);
      dom.rules.append(row);
    }
  }

  function readRules() {
    return [...(dom.rules?.querySelectorAll('[data-domain-row]') || [])].map(row => {
      const result = { code: row.dataset.ruleCode, customFields: {} };
      for (const control of row.querySelectorAll('[data-rule-property]')) result[control.dataset.ruleProperty] = control.value;
      for (const group of row.querySelectorAll('[data-format-field]')) {
        const name = group.dataset.formatField;
        const label = row.querySelector(`[data-format-label="${name}"]`)?.value || '';
        result[name] = { mode: group.value, label };
      }
      for (const group of row.querySelectorAll('[data-custom-rule-key]')) {
        const key = group.dataset.customRuleKey;
        result.customFields[key] = {
          mode: group.querySelector('[data-custom-rule-property="mode"]')?.value || 'hidden',
          labelOverride: clean(group.querySelector('[data-custom-rule-property="labelOverride"]')?.value)
        };
      }
      return result;
    });
  }

  function renderClaims(values) {
    if (!dom.claims) return;
    dom.claims.replaceChildren();
    const formats = readFormats().filter(item => item.id);
    const staff = array(property(state.data, 'autoClaimStaff') ?? property(state.data, 'staffUsers'));
    const formatOptions = formats.map(item => ({ value: item.id, label: `${item.label} (${item.code})` }));
    for (const [index, value] of values.entries()) {
      const formatId = stringId(property(value, 'materialFormatId') ?? property(value, 'formatId'));
      const staffId = stringId(property(value, 'staffUserId') ?? property(value, 'staffId'));
      const staffOptions = staff.map(item => ({ value: stringId(property(item, 'id')), label: property(item, 'label') || property(item, 'displayName') || `Staff ${property(item, 'id')}` })).filter(item => item.value);
      if (staffId && !staffOptions.some(item => item.value === staffId)) staffOptions.unshift({ value: staffId, label: `Staff ${staffId}` });
      const row = element('div', { className: 'settings-editor-row', 'data-domain-row': 'true' }, [
        field('Format', select(formatOptions, formatId, { 'data-domain-editable': 'true' })),
        field('Auto-claim staff', select(staffOptions, staffId, { 'data-domain-editable': 'true' })),
        actions(index, values.length, (from, offset) => reorder('claims', from, offset), from => remove('claims', from))
      ]);
      dom.claims.append(row);
    }
    if (values.length === 0) dom.claims.append(element('p', { className: 'settings-empty', text: 'No active auto-claim rules configured.' }));
  }

  function readClaims() {
    return [...(dom.claims?.querySelectorAll('[data-domain-row]') || [])].map(row => {
      const selects = [...row.querySelectorAll('select')];
      return { materialFormatId: stringId(selects[0]?.value), staffUserId: stringId(selects[1]?.value), active: true };
    }).filter(item => item.materialFormatId && item.staffUserId);
  }

  function templateEditorValues(data) {
    const raw = array(state.system
      ? property(systemConfig(data), 'templates')
      : property(property(data, 'stored'), 'templates'));
    const systemRows = raw
      .filter(item => stringId(property(item, 'organizationId')) === '1')
      .filter(item => !BUILTIN_TEMPLATE_KEYS.has(clean(property(item, 'templateKey'))));
    if (state.system) return systemRows.map(normalizeTemplate);

    const organizationId = String(currentOrganizationId());
    const libraryRows = raw.filter(item => stringId(property(item, 'organizationId')) === organizationId);
    const lineage = systemRows.map((source, index) => {
      const system = normalizeTemplate(source, index);
      const overrideRaw = libraryRows.find(item =>
        !Boolean(property(item, 'isCustom')) &&
        stringId(property(item, 'sourceTemplateId')) === system.id
      );
      const override = overrideRaw ? normalizeTemplate(overrideRaw, index) : null;
      return {
        ...system,
        id: override?.id || system.id,
        version: override?.version || system.version,
        sourceTemplateId: system.id,
        displayName: override?.displayName ?? system.displayName,
        subject: clean(override?.subject) ?? system.subject,
        body: clean(override?.body) ?? system.body,
        enabled: system.enabled && (!override || override.enabled),
        isCustom: false,
        overridden: Boolean(override),
        hadOverride: Boolean(override),
        rawSubject: override?.subject ?? null,
        rawBody: override?.body ?? null,
        rawDisplayName: override?.displayName ?? null,
        subjectBaseline: system.subject,
        bodyBaseline: system.body,
        displayNameBaseline: system.displayName,
        enabledBaseline: system.enabled
      };
    });
    const custom = libraryRows
      .filter(item => Boolean(property(item, 'isCustom')))
      .map(normalizeTemplate);
    return lineage.concat(custom);
  }

  function renderTemplates(values) {
    if (!dom.templates) return;
    dom.templates.replaceChildren();
    if (values.length === 0) dom.templates.append(element('p', { className: 'settings-empty', text: 'No named templates configured.' }));
    for (const [index, value] of values.entries()) {
      const lineage = !value.isCustom && !state.system;
      const override = lineage
        ? checkbox('Override', value.overridden, 'settings-inline-check settings-template-override-control')
        : null;
      if (override) override.input.className = 'settings-template-override';
      const row = element('div', {
        className: 'settings-editor-row settings-template-row',
        'data-domain-row': 'true',
        'data-template-id': stringId(value.id) || '',
        'data-template-version': stringId(value.version) || '',
        'data-template-source-id': stringId(value.sourceTemplateId) || '',
        'data-template-kind': value.isCustom ? 'custom' : lineage ? 'lineage' : 'system',
        'data-template-had-override': value.hadOverride ? 'true' : 'false',
        'data-template-baseline-subject': value.subjectBaseline || '',
        'data-template-baseline-body': value.bodyBaseline || '',
        'data-template-baseline-display-name': value.displayNameBaseline || '',
        'data-template-baseline-enabled': value.enabledBaseline ? 'true' : 'false'
      }, [
        field('Template key', element('input', {
          type: 'text',
          className: 'template-key',
          value: value.templateKey || '',
          readOnly: lineage || Boolean(value.id),
          'data-domain-editable': 'true'
        })),
        field('Display name', element('input', {
          type: 'text',
          className: 'template-display-name',
          value: value.displayName || '',
          'data-domain-editable': 'true'
        })),
        field('Subject', element('input', {
          type: 'text',
          className: 'template-subject',
          value: value.subject || '',
          'data-domain-editable': 'true'
        })),
        field('Body', element('textarea', {
          rows: '5',
          className: 'template-body',
          'data-domain-editable': 'true'
        }, [value.body || ''])),
        element('label', { className: 'settings-domain-check' }, [element('input', {
          type: 'checkbox',
          className: 'template-enabled',
          checked: value.enabled !== false,
          'data-domain-editable': 'true'
        }), element('span', { text: 'Enabled' })]),
        override?.wrapper,
        actions(index, values.length, (from, offset) => reorder('templates', from, offset), value.isCustom ? from => remove('templates', from) : null)
      ]);
      if (override) {
        override.input.addEventListener('change', () => {
          updateRowDisabled(row, !override.input.checked);
          onChange();
        });
        updateRowDisabled(row, !override.input.checked);
      }
      dom.templates.append(row);
    }
  }

  function readTemplates() {
    return [...(dom.templates?.querySelectorAll('[data-domain-row]') || [])].map(row => {
      const lineage = row.dataset.templateKind === 'lineage';
      const currentSubject = clean(row.querySelector('.template-subject')?.value);
      const currentBody = clean(row.querySelector('.template-body')?.value);
      const currentDisplayName = clean(row.querySelector('.template-display-name')?.value);
      const currentEnabled = Boolean(row.querySelector('.template-enabled')?.checked);
      const baselineSubject = clean(row.dataset.templateBaselineSubject);
      const baselineBody = clean(row.dataset.templateBaselineBody);
      const baselineDisplayName = clean(row.dataset.templateBaselineDisplayName);
      const baselineEnabled = row.dataset.templateBaselineEnabled === 'true';
      const override = row.querySelector('.settings-template-override');
      return {
        id: stringId(row.dataset.templateId),
        version: stringId(row.dataset.templateVersion),
        sourceTemplateId: stringId(row.dataset.templateSourceId),
        templateKey: clean(row.querySelector('.template-key')?.value),
        displayName: currentDisplayName,
        subject: currentSubject,
        body: currentBody,
        enabled: currentEnabled,
        isCustom: row.dataset.templateKind === 'custom',
        overridden: lineage ? Boolean(override?.checked) : !lineage,
        hadOverride: row.dataset.templateHadOverride === 'true',
        reset: lineage && row.dataset.templateHadOverride === 'true' && !override?.checked,
        subjectChanged: !lineage || currentSubject !== baselineSubject,
        bodyChanged: !lineage || currentBody !== baselineBody,
        displayNameChanged: !lineage || currentDisplayName !== baselineDisplayName,
        enabledChanged: !lineage || currentEnabled !== baselineEnabled
      };
    }).filter(item => item.templateKey);
  }

  function readSnapshot() {
    return JSON.stringify({
      publication: { useSystem: state.system || !dom.publicationUseSystem || dom.publicationUseSystem.checked, values: readPublication() },
      creators: { useSystem: state.system || !dom.creatorsUseSystem || dom.creatorsUseSystem.checked, values: readSetRows(dom.creators, 'creator') },
      codes: { useSystem: state.system || !dom.codesUseSystem || dom.codesUseSystem.checked, values: readSetRows(dom.codes, 'code') },
      providers: readProviders(),
      formats: readFormats(),
      fields: readFields(),
      rules: readRules(),
      claims: readClaims(),
      templates: readTemplates()
    });
  }

  function reorder(name, index, offset) {
    const values = readDomain(name);
    const next = index + offset;
    if (next < 0 || next >= values.length) return;
    [values[index], values[next]] = [values[next], values[index]];
    renderDomain(name, values);
    onChange();
  }

  function remove(name, index) {
    const values = readDomain(name);
    values.splice(index, 1);
    renderDomain(name, values);
    onChange();
  }

  function readDomain(name) {
    return {
      publication: readPublication,
      creators: () => readSetRows(dom.creators, 'creator'),
      codes: () => readSetRows(dom.codes, 'code'),
      providers: readProviders,
      formats: readFormats,
      fields: readFields,
      rules: readRules,
      claims: readClaims,
      templates: readTemplates
    }[name]();
  }

  function renderDomain(name, values) {
    if (name === 'publication') renderPublication(values);
    else if (name === 'creators') renderSetRows(dom.creators, values, 'creator');
    else if (name === 'codes') renderSetRows(dom.codes, values, 'code');
    else if (name === 'providers') renderProviders(values);
    else if (name === 'formats') renderFormats(values);
    else if (name === 'fields') renderFields(values);
    else if (name === 'rules') renderRules(values);
    else if (name === 'claims') renderClaims(values);
    else if (name === 'templates') renderTemplates(values);
  }

  function bindSetToggle(input, name, render) {
    if (!input) return;
    input.addEventListener('change', () => {
      render(setValues(name, state.system || input.checked));
      onChange();
    });
  }

  function bindAdd(name, factory) {
    addButtons[name]?.addEventListener('click', () => {
      const values = readDomain(name);
      values.push(factory(values));
      renderDomain(name, values);
      onChange();
    });
  }

  bindSetToggle(dom.publicationUseSystem, 'publication', renderPublication);
  bindSetToggle(dom.creatorsUseSystem, 'creators', values => renderSetRows(dom.creators, values, 'creator'));
  bindSetToggle(dom.codesUseSystem, 'codes', values => renderSetRows(dom.codes, values, 'code'));
  bindAdd('publication', values => ({ id: `option_${values.length + 1}`, label: 'New option', enabled: true, sortOrder: (values.length + 1) * 10 }));
  bindAdd('creators', values => 'New creator');
  bindAdd('codes', values => stringId(property(array(property(state.data, 'patronCodeChoices'))[0], 'id')) || '');
  bindAdd('providers', values => ({ id: null, key: `provider_${values.length + 1}`, label: 'New provider', urlTemplate: '', isEnabled: false, overridden: true }));
  bindAdd('formats', values => ({ id: null, code: `custom_${values.length + 1}`, ownerOrganizationId: currentOrganizationId(), label: 'New format', isEnabled: true, overridden: true }));
  bindAdd('fields', values => ({ id: null, key: `field_${values.length + 1}`, type: 'text', label: 'New field', enabled: true, options: [] }));
  bindAdd('rules', values => {
    const format = readFormats()[0];
    return normalizeRule({ code: format?.code || '' }, format || {});
  });
  bindAdd('claims', values => ({ materialFormatId: readFormats()[0]?.id || '', staffUserId: '' }));
  bindAdd('templates', values => ({
    id: null,
    templateKey: state.system
      ? `rejection:template_${values.length + 1}`
      : `rejection:custom_template_${values.length + 1}`,
    displayName: 'New rejection template',
    subject: '',
    body: '',
    enabled: true,
    isCustom: !state.system
  }));

  function populate(data, system) {
    state.data = data || {};
    state.system = Boolean(system);
    const configured = systemConfig(data);
    const library = libraryConfig(data);
    const publication = useSet('publicationOptions', state.system).map(normalizeOption);
    const creators = useSet('commonCreators', state.system).map(item => clean(typeof item === 'string' ? item : property(item, 'value'))).filter(Boolean);
    const codes = useSet('allowedPatronCodeIds', state.system).map(item => stringId(typeof item === 'string' ? item : property(item, 'id'))).filter(Boolean);
    const providerValues = state.system
      ? array(property(configured, 'providers'))
      : array(effective(data, 'externalSearchProviders')).map(item => ({ ...item, id: stringId(property(item, 'id')), overridden: bool(property(item, 'overridden')) }));
    const formats = normalizeFormats(state.system ? property(configured, 'formats') : effective(data, 'formats'));
    const effectiveFormats = normalizeFormats(effective(data, 'formats'));
    const fields = normalizeCustomFields(property(property(data, 'stored'), 'customFields'));
    const rules = effectiveFormats.map(format => normalizeRule({
      code: format.code,
      messageBehavior: format.messageBehavior,
      message: format.message,
      ...format.fields
    }, format));
    const claims = state.system ? [] : array(property(property(data, 'stored'), 'autoClaimRules')).filter(item => property(item, 'active') !== false);
    const templates = templateEditorValues(data);
    state.originalTemplates = clone(templates.filter(item => item.isCustom));
    state.deletedFormats = [];
    state.models = { publication, creators, codes, providers: providerValues, formats, fields, rules, claims, templates };

    if (addButtons.providers) addButtons.providers.hidden = !state.system;
    if (addButtons.formats) addButtons.formats.hidden = state.system;
    if (addButtons.fields) addButtons.fields.hidden = state.system;
    if (addButtons.claims) addButtons.claims.hidden = state.system;
    if (addButtons.templates) addButtons.templates.hidden = false;

    const libraryPublication = property(library, 'publicationOptions');
    const libraryCreators = property(library, 'commonCreators');
    const libraryCodes = property(library, 'allowedPatronCodeIds');
    if (dom.publicationUseSystem) dom.publicationUseSystem.checked = state.system || !libraryPublication?.exists;
    if (dom.creatorsUseSystem) dom.creatorsUseSystem.checked = state.system || !libraryCreators?.exists;
    if (dom.codesUseSystem) dom.codesUseSystem.checked = state.system || !libraryCodes?.exists;
    for (const [input, wrapper] of [
      [dom.publicationUseSystem, dom.publicationInheritField],
      [dom.creatorsUseSystem, dom.creatorsInheritField],
      [dom.codesUseSystem, dom.codesInheritField]
    ]) {
      if (wrapper) wrapper.hidden = state.system;
      if (input) input.disabled = state.system;
    }
    renderPublication(publication);
    renderSetRows(dom.creators, creators, 'creator');
    renderSetRows(dom.codes, codes, 'code');
    renderProviders(providerValues);
    renderFormats(formats);
    renderFields(fields);
    renderRules(rules);
    renderClaims(claims);
    renderTemplates(templates);
    state.baseline = readSnapshot();
  }

  function collect() {
    const values = {
      publication: readPublication(),
      creators: readSetRows(dom.creators, 'creator'),
      codes: readSetRows(dom.codes, 'code'),
      providers: readProviders(),
      formats: readFormats(),
      fields: readFields(),
      rules: readRules(),
      claims: readClaims(),
      templates: readTemplates()
    };
    const deletedFormats = state.deletedFormats.map(item => ({ ...item }));
    const current = JSON.parse(readSnapshot());
    const baseline = state.baseline ? JSON.parse(state.baseline) : {};
    const domainsChanged = {};
    for (const name of ['publication', 'creators', 'codes', 'providers', 'formats', 'fields', 'rules', 'claims', 'templates']) {
      domainsChanged[name] = JSON.stringify(current[name]) !== JSON.stringify(baseline[name]);
    }
    const changed = Object.values(domainsChanged).some(Boolean);
    const collectedValues = {
      ...values,
      publicationUseSystem: Boolean(current.publication?.useSystem),
      creatorsUseSystem: Boolean(current.creators?.useSystem),
      codesUseSystem: Boolean(current.codes?.useSystem)
    };
    if (!changed) return {
      changed: deletedFormats.length > 0,
      domainsChanged,
      values: collectedValues,
      templates: [],
      deletedFormats
    };
    const deletedTemplates = state.originalTemplates
      .filter(original => !values.templates.some(current => current.id && current.id === stringId(property(original, 'id'))))
      .map(original => ({ templateKey: property(original, 'templateKey'), isCustom: true, reset: true }));
    return {
      changed: true,
      domainsChanged,
      values: collectedValues,
      templates: domainsChanged.templates ? values.templates.concat(deletedTemplates) : [],
      deletedFormats
    };
  }

  return {
    populate,
    collect,
    setBaseline: () => { state.baseline = readSnapshot(); },
    snapshot: () => readSnapshot()
  };
}
