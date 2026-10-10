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
    const customFields = {};
    const rawCustomFields = property(value, 'customFields');
    if (rawCustomFields && typeof rawCustomFields === 'object' && !Array.isArray(rawCustomFields)) {
      for (const [key, rule] of Object.entries(rawCustomFields)) {
        customFields[key] = {
          mode: clean(property(rule, 'mode')) || 'hidden',
          labelOverride: property(rule, 'labelOverride') ?? property(rule, 'label') ?? null
        };
      }
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
      customFields,
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

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function validateCustomRuleMap(value, description) {
  if (!isRecord(value)) throw new Error(`${description} must be an object.`);
  for (const [key, rule] of Object.entries(value)) {
    if (!key || !isRecord(rule) || !['required', 'optional', 'hidden'].includes(property(rule, 'mode'))) {
      throw new Error(`${description} contains an unsupported field rule.`);
    }
    const labelOverride = property(rule, 'labelOverride') ?? property(rule, 'label');
    if (labelOverride !== undefined && labelOverride !== null && typeof labelOverride !== 'string') {
      throw new Error(`${description} contains an invalid label override.`);
    }
  }
}

function validateSnapshotOptions(values, description) {
  const keys = new Set();
  for (const value of values) {
    const key = clean(property(value, 'id') ?? property(value, 'key'));
    const label = clean(property(value, 'label'));
    if (!isRecord(value) || !key || !label || keys.has(key.toLowerCase()) ||
        property(value, 'enabled') !== undefined && typeof property(value, 'enabled') !== 'boolean') {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
    keys.add(key.toLowerCase());
  }
}

function validateCreatorSnapshot(values, description) {
  for (const value of values) {
    const creator = typeof value === 'string' ? value : isRecord(value) ? property(value, 'value') : null;
    if (typeof creator !== 'string' || !clean(creator)) {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
  }
}

function validateProviders(values, description) {
  const identities = new Set();
  for (const provider of values) {
    if (!isRecord(provider)) throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    const id = property(provider, 'id');
    const key = clean(property(provider, 'key'));
    if (id !== undefined && id !== null && !validPositiveIdentity(id) ||
        id === undefined && !key ||
        property(provider, 'label') !== undefined && property(provider, 'label') !== null && typeof property(provider, 'label') !== 'string' ||
        property(provider, 'urlTemplate') !== undefined && property(provider, 'urlTemplate') !== null && typeof property(provider, 'urlTemplate') !== 'string' ||
        property(provider, 'isEnabled') !== undefined && property(provider, 'isEnabled') !== null && typeof property(provider, 'isEnabled') !== 'boolean') {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
    const identity = id !== undefined && id !== null ? `id:${String(id)}` : `key:${key.toLowerCase()}`;
    if (identities.has(identity)) throw new Error(`The ${description} snapshot contains duplicate providers. Reload settings before saving.`);
    identities.add(identity);
  }
}

function validateFormats(values, description) {
  const codes = new Set();
  for (const format of values) {
    const code = clean(property(format, 'code'));
    const id = property(format, 'id');
    if (!isRecord(format) || !code || codes.has(code) ||
        id !== undefined && id !== null && !validPositiveIdentity(id) ||
        property(format, 'isEnabled') !== undefined && typeof property(format, 'isEnabled') !== 'boolean') {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
    codes.add(code);
    if (property(format, 'customFields') !== undefined) {
      validateCustomRuleMap(property(format, 'customFields'), `${description} format ${code} customFields`);
    }
  }
}

function validateRawFormats(values, description) {
  const overrides = values.filter(value => property(value, 'kind') === 'systemOverride');
  validateFormats(values.filter(value => property(value, 'kind') !== 'systemOverride'), description);
  const identities = new Set();
  for (const value of overrides) {
    const id = property(value, 'materialFormatId');
    if (!validPositiveIdentity(id) || identities.has(String(id)) ||
        String(property(value, 'ownerOrganizationId')) !== '1' ||
        typeof property(value, 'version') !== 'string' || !clean(property(value, 'version')) ||
        property(value, 'code') !== null) {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
    identities.add(String(id));
  }
}

function validateCollectionMetadata(stored, configured, libraryOverride, effective, system, organizationId) {
  const providerOverrides = new Set(array(property(libraryOverride, 'providers')).map(value => String(property(value, 'id'))));
  const storedProviders = new Map(property(stored, 'providers').map(value => [String(property(value, 'id')), value]));
  for (const value of property(effective, 'externalSearchProviders')) {
    const id = String(property(value, 'id'));
    const storedValue = storedProviders.get(id);
    const overridden = property(value, 'overridden');
    if (!storedValue || typeof overridden !== 'boolean' ||
        overridden !== (!system && providerOverrides.has(id)) ||
        property(storedValue, 'overridden') !== overridden) {
      throw new Error('The effective provider override metadata snapshot is malformed. Reload settings before saving.');
    }
  }
  if (storedProviders.size !== property(effective, 'externalSearchProviders').length) {
    throw new Error('The effective provider override metadata snapshot is incomplete. Reload settings before saving.');
  }
  const owned = new Map(property(configured, 'formats').map(value => [String(property(value, 'id')), value]));
  const overrides = new Map();
  for (const value of array(property(libraryOverride, 'formats'))) {
    if (property(value, 'kind') === 'systemOverride') overrides.set(String(property(value, 'materialFormatId')), value);
    else owned.set(String(property(value, 'id')), value);
  }
  const storedFormats = new Map(property(stored, 'formats').map(value => [String(property(value, 'id')), value]));
  for (const value of property(effective, 'formats')) {
    const id = String(property(value, 'id'));
    const original = owned.get(id);
    const storedValue = storedFormats.get(id);
    const override = overrides.get(id);
    const owner = String(property(value, 'ownerOrganizationId'));
    const version = property(value, 'version');
    const overridden = property(value, 'overridden');
    if (!original || !storedValue || !['1', String(organizationId)].includes(owner) ||
        owner !== String(property(original, 'ownerOrganizationId')) ||
        owner !== String(property(storedValue, 'ownerOrganizationId')) ||
        typeof overridden !== 'boolean' || overridden !== Boolean(override) ||
        property(storedValue, 'overridden') !== overridden ||
        typeof version !== 'string' || !clean(version) ||
        version !== property(override || original, 'version') || version !== property(storedValue, 'version')) {
      throw new Error('The effective format owner, version, or override metadata snapshot is malformed. Reload settings before saving.');
    }
  }
  if (storedFormats.size !== property(effective, 'formats').length ||
      [...overrides.keys()].some(id => !storedFormats.has(id))) {
    throw new Error('The effective format metadata snapshot is incomplete. Reload settings before saving.');
  }
}

function validateTemplates(values, description) {
  const keys = new Set();
  for (const template of values) {
    const key = clean(property(template, 'templateKey') ?? property(template, 'key'));
    const id = property(template, 'id');
    const organizationId = stringId(property(template, 'organizationId'));
    const identity = `${organizationId}:${key}`;
    if (!isRecord(template) || !key || keys.has(identity) ||
        id !== undefined && id !== null && !validPositiveIdentity(id) ||
        property(template, 'enabled') !== undefined && typeof property(template, 'enabled') !== 'boolean') {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
    keys.add(identity);
  }
}

function validateAutoClaimRules(values, description) {
  for (const rule of values) {
    if (!isRecord(rule) || !validPositiveIdentity(property(rule, 'materialFormatId') ?? property(rule, 'formatId')) ||
        property(rule, 'staffUserId') !== null && property(rule, 'staffUserId') !== undefined &&
          !validPositiveIdentity(property(rule, 'staffUserId')) ||
        property(rule, 'active') !== undefined && typeof property(rule, 'active') !== 'boolean') {
      throw new Error(`The ${description} snapshot is malformed. Reload settings before saving.`);
    }
  }
}

function validateEditorSnapshot(data, system) {
  const stored = property(data, 'stored');
  const effective = property(data, 'effective');
  const configured = property(stored, 'configuredSystem');
  if (!isRecord(stored) || !isRecord(effective) || !isRecord(configured) ||
      !Array.isArray(property(effective, 'formats')) ||
      !Array.isArray(property(stored, 'customFields')) ||
      !Array.isArray(property(stored, 'formatRules')) ||
      !Array.isArray(property(configured, 'formats'))) {
    throw new Error('The settings snapshot is incomplete. Reload settings before saving.');
  }
  const libraryOverride = property(stored, 'libraryOverride');
  if (!system && (!isRecord(libraryOverride) || !Array.isArray(property(libraryOverride, 'formats')))) {
    throw new Error('The selected library settings snapshot is incomplete. Reload settings before saving.');
  }
  if (!Array.isArray(property(configured, 'providers')) ||
      !Array.isArray(property(stored, 'providers')) ||
      !Array.isArray(property(effective, 'externalSearchProviders'))) {
    throw new Error('The provider settings snapshot is incomplete. Reload settings before saving.');
  }
  if (!Array.isArray(property(configured, 'templates')) || !Array.isArray(property(stored, 'templates'))) {
    throw new Error('The template settings snapshot is incomplete. Reload settings before saving.');
  }
  if (!system && (!Array.isArray(property(libraryOverride, 'providers')) ||
      !Array.isArray(property(libraryOverride, 'templates')))) {
    throw new Error('The selected library provider or template snapshot is incomplete. Reload settings before saving.');
  }
  for (const key of ['publicationOptions', 'commonCreators', 'allowedPatronCodeIds']) {
    const systemSet = property(configured, key);
    let librarySet = null;
    if (!isRecord(systemSet) || typeof property(systemSet, 'exists') !== 'boolean' ||
        !Array.isArray(property(systemSet, 'values'))) {
      throw new Error(`The system ${key} snapshot is incomplete. Reload settings before saving.`);
    }
    if (!system) {
      librarySet = property(libraryOverride, key);
      if (!isRecord(librarySet) || typeof property(librarySet, 'exists') !== 'boolean' ||
          !Array.isArray(property(librarySet, 'values'))) {
        throw new Error(`The selected library ${key} snapshot is incomplete. Reload settings before saving.`);
      }
    }
    if (key === 'publicationOptions') {
      validateSnapshotOptions(property(systemSet, 'values'), `system ${key}`);
      if (!system) validateSnapshotOptions(property(librarySet, 'values'), `library ${key}`);
    } else if (key === 'commonCreators') {
      validateCreatorSnapshot(property(systemSet, 'values'), `system ${key}`);
      if (!system) validateCreatorSnapshot(property(librarySet, 'values'), `library ${key}`);
    }
  }
  for (const key of ['providers', 'formats', 'templates']) {
    if (!Array.isArray(property(stored, key))) {
      throw new Error(`The stored ${key} snapshot is incomplete. Reload settings before saving.`);
    }
  }
  if (!Array.isArray(property(effective, 'customFields'))) {
    throw new Error('The effective custom-field snapshot is incomplete. Reload settings before saving.');
  }
  for (const [collection, description] of [
    [property(configured, 'providers'), 'system provider'],
    [property(stored, 'providers'), 'stored provider'],
    [property(effective, 'externalSearchProviders'), 'effective provider']
  ]) validateProviders(collection, description);
  validateProviders(property(libraryOverride, 'providers') || [], 'library override provider');
  for (const [collection, description] of [
    [property(stored, 'formats'), 'stored format'],
    [property(effective, 'formats'), 'effective format']
  ]) validateFormats(collection, description);
  validateRawFormats(property(configured, 'formats'), 'system format');
  if (!system) validateRawFormats(property(libraryOverride, 'formats'), 'library override format');
  validateCollectionMetadata(stored, configured, libraryOverride, effective, system,
    system ? 1 : property(data, 'orgId'));
  for (const [collection, description] of [
    [property(configured, 'templates'), 'system template'],
    [property(stored, 'templates'), 'stored template']
  ]) validateTemplates(collection, description);
  if (!system) validateTemplates(property(libraryOverride, 'templates'), 'library override template');
  validateAutoClaimRules(property(stored, 'autoClaimRules'), 'auto-claim rule');
  const origins = property(stored, 'origins');
  if (!Array.isArray(origins)) throw new Error('The embed-origin snapshot is incomplete. Reload settings before saving.');
  for (const value of origins) {
    const origin = typeof value === 'string' ? value : isRecord(value) ? property(value, 'origin') : null;
    if (typeof origin !== 'string' || !clean(origin)) {
      throw new Error('The embed-origin snapshot is malformed. Reload settings before saving.');
    }
  }
  const patronCodeChoices = property(data, 'patronCodeChoices');
  if (!Array.isArray(patronCodeChoices)) {
    throw new Error('The patron-code choices snapshot is incomplete. Reload settings before saving.');
  }
  for (const choice of patronCodeChoices) {
    if (!isRecord(choice) || !normalizePatronCodeId(property(choice, 'id')) ||
        typeof property(choice, 'description') !== 'string') {
      throw new Error('The patron-code choices snapshot is malformed. Reload settings before saving.');
    }
  }

  const autoClaimStaff = property(data, 'autoClaimStaff');
  if (!Array.isArray(autoClaimStaff)) {
    throw new Error('The auto-claim staff snapshot is incomplete. Reload settings before saving.');
  }
  for (const staff of autoClaimStaff) {
    const id = property(staff, 'id');
    if (!isRecord(staff) || typeof id !== 'string' || !/^[1-9]\d*$/.test(id) ||
        !validPositiveIdentity(id) || typeof property(staff, 'label') !== 'string') {
      throw new Error('The auto-claim staff snapshot is malformed. Reload settings before saving.');
    }
  }

  const definitions = property(stored, 'customFields');
  const fieldKeys = new Set();
  const validateFields = (values, keys) => {
    for (const fieldValue of values) {
      const key = clean(property(fieldValue, 'key') ?? property(fieldValue, 'fieldKey'));
      const type = clean(property(fieldValue, 'type') ?? property(fieldValue, 'fieldType'));
      const options = property(fieldValue, 'options');
      if (!isRecord(fieldValue) || !key || keys.has(key) ||
          !['text', 'textarea', 'select'].includes(type) || !Array.isArray(options)) {
        throw new Error('The custom-field snapshot is malformed. Reload settings before saving.');
      }
      keys.add(key);
      const optionKeys = new Set();
      for (const option of options) {
        const optionKey = clean(property(option, 'id') ?? property(option, 'key'));
        if (!isRecord(option) || !optionKey || optionKeys.has(optionKey) ||
            typeof property(option, 'label') !== 'string' || !clean(property(option, 'label')) ||
            property(option, 'enabled') !== undefined && typeof property(option, 'enabled') !== 'boolean') {
          throw new Error('A custom-field option snapshot is malformed. Reload settings before saving.');
        }
        optionKeys.add(optionKey);
      }
    }
  };
  validateFields(definitions, fieldKeys);
  validateFields(property(effective, 'customFields'), new Set());

  const effectiveFormatCodes = new Set();
  for (const format of property(effective, 'formats')) {
    const code = clean(property(format, 'code'));
    if (!isRecord(format) || !code || !isRecord(property(format, 'customFields'))) {
      throw new Error('The effective format snapshot is incomplete. Reload settings before saving.');
    }
    effectiveFormatCodes.add(code);
    validateCustomRuleMap(property(format, 'customFields'), `Format ${code} customFields`);
  }
  const ruleCodes = new Set();
  for (const rule of property(stored, 'formatRules')) {
    const code = clean(property(rule, 'code'));
    if (!isRecord(rule) || !code || ruleCodes.has(code)) {
      throw new Error('The stored format-rule snapshot is malformed. Reload settings before saving.');
    }
    if (!system && !effectiveFormatCodes.has(code)) {
      throw new Error('The effective format snapshot is incomplete. Reload settings before saving.');
    }
    ruleCodes.add(code);
    const customFields = property(rule, 'customFields');
    validateCustomRuleMap(customFields, `Format ${code} customFields`);
    if (Object.keys(customFields).some(key => !fieldKeys.has(key))) {
      throw new Error('A stored format rule refers to a missing custom field. Reload settings before saving.');
    }
  }

  const codeSnapshots = [
    property(configured, 'allowedPatronCodeIds'),
    property(libraryOverride, 'allowedPatronCodeIds')
  ];
  for (const [index, snapshot] of codeSnapshots.entries()) {
    if (index === 1 && system) continue;
    if (!isRecord(snapshot) || typeof property(snapshot, 'exists') !== 'boolean' ||
        !Array.isArray(property(snapshot, 'values'))) {
      throw new Error('The patron-code snapshot is malformed. Reload settings before saving.');
    }
    const ids = new Set();
    let idKind = null;
    for (const value of property(snapshot, 'values')) {
      const raw = typeof value === 'object' && value !== null ? property(value, 'id') : value;
      const currentKind = typeof raw;
      const id = normalizePatronCodeId(raw);
      if (!id || ids.has(id) || idKind !== null && idKind !== currentKind) {
        throw new Error('The patron-code snapshot contains an invalid or duplicate ID. Reload settings before saving.');
      }
      idKind = currentKind;
      ids.add(id);
    }
  }
}

function normalizePatronCodeId(value) {
  if (typeof value === 'number') {
    return Number.isInteger(value) && value > 0 && value <= 2147483647 ? String(value) : null;
  }
  if (typeof value !== 'string') return null;
  const text = value.trim();
  if (!/^\d+$/.test(text)) return null;
  const numericValue = Number(text);
  return Number.isInteger(numericValue) && numericValue > 0 && numericValue <= 2147483647
    ? String(numericValue)
    : null;
}

function validPositiveIdentity(value) {
  if (typeof value === 'number') return Number.isSafeInteger(value) && value > 0;
  if (typeof value !== 'string' || !/^\d+$/.test(value.trim())) return false;
  try {
    return BigInt(value.trim()) > 0n && BigInt(value.trim()) <= 9223372036854775807n;
  } catch {
    return false;
  }
}

function rawSnapshot(configuredSystem, libraryOverride, key, system) {
  if (system) return property(configuredSystem, key);
  return property(libraryOverride, key);
}

export function createSettingsDomainEditors({ root, onChange = () => {}, canRemoveTemplate = () => true }) {
  const events = new window.AbortController();
  let disposed = false;
  function listen(target, name, handler) {
    target?.addEventListener(name, event => { if (!disposed && target.isConnected !== false) return handler(event); }, { signal: events.signal });
  }
  function command(iconName, label, handler, disabled = false) {
    const button = element('button', {
      type: 'button',
      className: 'settings-icon-button',
      title: label,
      'aria-label': label,
      disabled
    }, [element('i', { className: `fa fa-${iconName}`, 'aria-hidden': 'true' })]);
    listen(button, 'click', handler);
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

  const dom = {
    publication: root.querySelector('#publication-options-editor'),
    publicationUseSystem: root.querySelector('#publication-options-use-system'),
    publicationInheritField: root.querySelector('#publication-options-inherit-field'),
    creators: root.querySelector('#common-creators-editor'),
    creatorsUseSystem: root.querySelector('#common-creators-use-system'),
    creatorsInheritField: root.querySelector('#common-creators-inherit-field'),
    codes: root.querySelector('#patron-codes-editor'),
    codeSearch: root.querySelector('#patron-code-search'),
    codeSelectAll: root.querySelector('#patron-codes-select-all'),
    codeClearAll: root.querySelector('#patron-codes-clear-all'),
    codeWarning: root.querySelector('#patron-code-warning'),
    codeEligibilityEnabled: root.querySelector('#patron-code-eligibility-enabled'),
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
    ruleRosterCodes: [],
    baseline: null,
    originalTemplates: [],
    deletedFormats: [],
    validationError: null
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
    return values.map(item => stringId(typeof item === 'string' || typeof item === 'number' ? item : property(item, 'id'))).filter(Boolean);
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
    if (type === 'code') {
      const disabled = !state.system && Boolean(input?.checked);
      dom.codeSelectAll.disabled = disabled;
      dom.codeClearAll.disabled = disabled;
    }
  }

  function updateCodeWarning() {
    if (disposed) return;
    const empty = readSetRows(dom.codes, 'code').length === 0;
    dom.codeWarning.hidden = !dom.codeEligibilityEnabled.checked || !empty;
    dom.codeWarning.textContent = empty && dom.codeEligibilityEnabled.checked
      ? 'No patron codes are selected. The current server policy allows all patron codes until at least one ID is selected.'
      : '';
  }

  function renderSetRows(container, values, type) {
    if (!container) return;
    container.replaceChildren();
    if (values.length === 0) {
      container.append(element('p', { className: 'settings-empty', text: 'No values configured.' }));
      setEditorControls(type === 'code' ? 'code' : 'creator', container);
      if (type === 'code') updateCodeWarning();
      return;
    }
    const allChoices = type === 'code'
      ? array(property(state.data, 'patronCodeChoices')).map((choice) => ({
        value: stringId(property(choice, 'id')),
        label: `${clean(property(choice, 'description')) || 'Patron code'} (${stringId(property(choice, 'id')) || '?'})`
      })).filter(choice => choice.value)
      : [];
    const choices = allChoices.filter(choice => !dom.codeSearch.value.trim() ||
      `${choice.label} ${choice.value}`.toLocaleLowerCase().includes(dom.codeSearch.value.trim().toLocaleLowerCase()));
    for (const [index, value] of values.entries()) {
      const currentValue = type === 'creator' ? value : property(value, 'id') ?? value;
      const currentId = stringId(currentValue);
      const options = [...choices];
      if (type === 'code' && currentId && !options.some(option => option.value === currentId)) {
        options.unshift(allChoices.find(option => option.value === currentId) ||
          { value: currentId, label: `Unavailable code (${currentId})` });
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
    if (type === 'code') updateCodeWarning();
  }

  function readSetRows(container, type) {
    return [...(container?.querySelectorAll('[data-domain-row]') || [])]
      .map(row => clean(row.querySelector('select, input')?.value));
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
      id: stringId(row.querySelector('input')?.value),
      label: clean(row.querySelectorAll('input')[1]?.value),
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
        'data-provider-key': value.key || '',
        'data-provider-sort-order': numeric(value.sortOrder, (index + 1) * 10)
      }, [
        field('Provider key', key),
        field('Label', element('input', { type: 'text', value: value.label || '', 'data-domain-editable': 'true' })),
        field('URL template', element('input', { type: 'url', value: value.urlTemplate || '', 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Enabled' })]),
        state.system ? null : override.wrapper,
        actions(index, values.length, (from, offset) => reorder('providers', from, offset), from => remove('providers', from))
      ]);
      listen(override.input, 'change', () => {
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
        sortOrder: numeric(row.dataset.providerSortOrder, (index + 1) * 10),
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
        'data-format-version': value.version || '',
        'data-format-sort-order': value.sortOrder
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
        listen(override.input, 'change', () => {
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
        sortOrder: numeric(row.dataset.formatSortOrder, (index + 1) * 10),
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
      const keyInput = element('input', { type: 'text', value: value.key, 'data-domain-editable': 'true' });
      const row = element('div', {
        className: 'settings-editor-row settings-custom-field-row',
        'data-domain-row': 'true',
        'data-field-id': value.id || ''
      }, [
        field('Stable key', keyInput),
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
      const originalKey = value.key;
      listen(keyInput, 'change', event => {
        const nextKey = clean(event.target.value);
        if (!originalKey || !nextKey || nextKey === originalKey) return;
        const rules = readRules();
        if (rules.some(rule => Object.prototype.hasOwnProperty.call(rule.customFields, nextKey))) {
          state.validationError = 'Custom field keys must remain unique across saved rules.';
          return;
        }
        for (const rule of rules) {
          if (Object.prototype.hasOwnProperty.call(rule.customFields, originalKey)) {
            rule.customFields[nextKey] = rule.customFields[originalKey];
            delete rule.customFields[originalKey];
          }
        }
        state.models.rules = clone(rules);
        renderRules(rules);
        onChange();
      });
      listen(row.querySelector('select'), 'change', event => {
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
    listen(add, 'click', () => {
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
      key: clean(row.querySelectorAll('input')[0]?.value),
      label: clean(row.querySelectorAll('input')[1]?.value),
      type: row.querySelector('select')?.value || 'text',
      helpText: clean(row.querySelectorAll('input')[2]?.value),
      enabled: Boolean(row.querySelector('input[type="checkbox"]')?.checked),
      sortOrder: (index + 1) * 10,
      options: [...(row.querySelectorAll('[data-option-row]') || [])].map((option, optionIndex) => ({
        id: stringId(option.querySelectorAll('input')[0]?.value),
        label: clean(option.querySelectorAll('input')[1]?.value),
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
      const customFieldsByKey = new Map(customFields.map(item => [item.key, item]));
      const customFieldKeys = [...customFields.map(item => item.key)];
      for (const key of Object.keys(value.customFields || {})) {
        if (!customFieldsByKey.has(key)) customFieldKeys.push(key);
      }
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
          ...customFieldKeys.map(key => {
            const fieldValue = customFieldsByKey.get(key);
            const fieldLabel = fieldValue?.label || `Retired field (${key})`;
            const current = value.customFields[key] || { mode: 'hidden', labelOverride: null };
            return element('div', { className: 'settings-custom-rule-row', 'data-custom-rule-key': key }, [
              element('span', { text: fieldLabel }),
              select([
                { value: 'hidden', label: 'Hidden' },
                { value: 'optional', label: 'Optional' },
                { value: 'required', label: 'Required' }
              ], current.mode, { 'data-custom-rule-property': 'mode', 'data-domain-editable': 'true',
                'aria-label': `${fieldLabel} mode for ${value.code}` }),
              element('input', { type: 'text', value: current.labelOverride || '', placeholder: 'Label override',
                'data-custom-rule-property': 'labelOverride', 'data-domain-editable': 'true',
                'aria-label': `${fieldLabel} label override for ${value.code}` })
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
      return {
        materialFormatId: stringId(selects[0]?.value) || '',
        staffUserId: stringId(selects[1]?.value) || '',
        active: true
      };
    });
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
        subject: override?.subject?.trim() ? override.subject : system.subject,
        body: override?.body?.trim() ? override.body : system.body,
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
        actions(index, values.length, (from, offset) => reorder('templates', from, offset), value.isCustom ? from => {
          if (canRemoveTemplate(value)) remove('templates', from);
        } : null)
      ]);
      if (override) {
        listen(override.input, 'change', () => {
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
      const currentSubject = row.querySelector('.template-subject')?.value ?? null;
      const currentBody = row.querySelector('.template-body')?.value ?? null;
      const currentDisplayName = clean(row.querySelector('.template-display-name')?.value);
      const currentEnabled = Boolean(row.querySelector('.template-enabled')?.checked);
      const baselineSubject = row.dataset.templateBaselineSubject;
      const baselineBody = row.dataset.templateBaselineBody;
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
    });
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
    const rules = name === 'fields' ? readRules() : null;
    const values = readDomain(name);
    const next = index + offset;
    if (next < 0 || next >= values.length) return;
    [values[index], values[next]] = [values[next], values[index]];
    if (name === 'formats' || name === 'providers') {
      values.forEach((value, order) => { value.sortOrder = (order + 1) * 10; });
    }
    renderDomain(name, values);
    if (name === 'fields') renderRules(rules);
    onChange();
  }

  function remove(name, index) {
    const values = readDomain(name);
    const rules = name === 'fields' ? readRules() : null;
    const removedFieldKey = name === 'fields' ? values[index]?.key : null;
    const [removed] = values.splice(index, 1);
    if (name === 'rules' && removed) {
      state.ruleRosterCodes = state.ruleRosterCodes.filter(code => code !== removed.code);
    }
    renderDomain(name, values);
    if (name === 'fields') {
      for (const rule of rules || []) delete rule.customFields[removedFieldKey];
      state.models.rules = clone(rules);
      renderRules(rules);
    }
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

  function validateRenderedRules() {
    const fieldKeys = new Set(readFields().map(item => item.key));
    const expectedCodes = new Set(state.ruleRosterCodes);
    const renderedCodes = new Set();
    for (const row of dom.rules.querySelectorAll('[data-domain-row]')) {
      const code = clean(row.dataset.ruleCode);
      if (!code || renderedCodes.has(code) || !row.querySelector('[data-rule-property="messageBehavior"]') ||
          !row.querySelector('[data-rule-property="message"]')) {
        throw new Error('A format-rule editor is incomplete. Reload settings before saving.');
      }
      renderedCodes.add(code);
      for (const name of ['title', 'author', 'identifier', 'publication']) {
        if (!row.querySelector(`[data-format-field="${name}"]`) ||
            !row.querySelector(`[data-format-label="${name}"]`)) {
          throw new Error('A format-rule editor is missing a required control. Reload settings before saving.');
        }
      }
      const baselineRule = state.models.rules.find(item => item.code === code);
      const expectedKeys = new Set([
        ...fieldKeys,
        ...Object.keys(baselineRule?.customFields || {})
      ]);
      const actualKeys = new Set();
      for (const customRow of row.querySelectorAll('[data-custom-rule-key]')) {
        const key = customRow.dataset.customRuleKey;
        if (!key || actualKeys.has(key) ||
            !customRow.querySelector('[data-custom-rule-property="mode"]') ||
            !customRow.querySelector('[data-custom-rule-property="labelOverride"]')) {
          throw new Error('A custom field rule editor is incomplete. Reload settings before saving.');
        }
        actualKeys.add(key);
      }
      if ([...expectedKeys].some(key => !actualKeys.has(key))) {
        throw new Error('A custom field rule control is unavailable. Reload settings before saving.');
      }
    }
    if (renderedCodes.size !== expectedCodes.size ||
        [...expectedCodes].some(code => !renderedCodes.has(code))) {
      throw new Error('The format-rule editor is incomplete. Reload settings before saving.');
    }
  }

  function validateCurrentFields() {
    if (readSetRows(dom.creators, 'creator').some(value => !value) ||
        readSetRows(dom.codes, 'code').some(value => !normalizePatronCodeId(value))) {
      throw new Error('Remove or complete blank creator and patron-code rows before saving.');
    }
    if (readPublication().some(option => !option.id || !option.label)) {
      throw new Error('Publication options require stable IDs and labels. Delete a row to remove it.');
    }
    if (readTemplates().some(template => !template.templateKey)) {
      throw new Error('Email templates require a stable key. Delete a row to remove it.');
    }
    const fields = readFields();
    const keys = new Set();
    for (const fieldValue of fields) {
      if (!fieldValue.key || !fieldValue.label ||
          !['text', 'textarea', 'select'].includes(fieldValue.type) || keys.has(fieldValue.key)) {
        throw new Error('Custom fields require unique stable keys, labels, and supported types.');
      }
      keys.add(fieldValue.key);
      const optionKeys = new Set();
      for (const option of fieldValue.options) {
        if (!option.id || !option.label || optionKeys.has(option.id)) {
          throw new Error('Custom-field options require unique stable IDs and labels.');
        }
        optionKeys.add(option.id);
      }
    }
  }

  function bindSetToggle(input, name, render) {
    if (!input) return;
    listen(input, 'change', () => {
      render(setValues(name, state.system || input.checked));
      onChange();
    });
  }

  function bindAdd(name, factory) {
    listen(addButtons[name], 'click', () => {
      const rules = name === 'fields' ? readRules() : null;
      const values = readDomain(name);
      const added = factory(values);
      values.push(added);
      if (name === 'rules' && !state.ruleRosterCodes.includes(added.code)) {
        state.ruleRosterCodes = [...state.ruleRosterCodes, added.code];
      }
      renderDomain(name, values);
      if (name === 'fields') renderRules(rules);
      onChange();
    });
  }

  bindSetToggle(dom.publicationUseSystem, 'publication', renderPublication);
  bindSetToggle(dom.creatorsUseSystem, 'creators', values => renderSetRows(dom.creators, values, 'creator'));
  bindSetToggle(dom.codesUseSystem, 'codes', values => renderSetRows(dom.codes, values, 'code'));
  bindAdd('publication', values => ({ id: `option_${values.length + 1}`, label: 'New option', enabled: true, sortOrder: (values.length + 1) * 10 }));
  bindAdd('creators', values => 'New creator');
  bindAdd('codes', values => stringId(property(array(property(state.data, 'patronCodeChoices'))[0], 'id')) || '');
  listen(dom.codeSearch, 'input', () => renderSetRows(dom.codes, readSetRows(dom.codes, 'code'), 'code'));
  listen(dom.codeSelectAll, 'click', () => {
    const selected = readSetRows(dom.codes, 'code');
    const term = dom.codeSearch.value.trim().toLocaleLowerCase();
    for (const choice of array(property(state.data, 'patronCodeChoices'))) {
      const id = stringId(property(choice, 'id'));
      const label = `${clean(property(choice, 'description')) || ''} ${id || ''}`.toLocaleLowerCase();
      if (id && (!term || label.includes(term)) && !selected.includes(id)) selected.push(id);
    }
    renderSetRows(dom.codes, selected, 'code');
    onChange();
  });
  listen(dom.codeClearAll, 'click', () => {
    renderSetRows(dom.codes, [], 'code');
    onChange();
  });
  listen(dom.codeEligibilityEnabled, 'change', updateCodeWarning);
  listen(dom.codes, 'change', updateCodeWarning);
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
    if (disposed) return;
    state.data = data || {};
    state.system = Boolean(system);
    state.validationError = null;
    try {
      validateEditorSnapshot(state.data, state.system);
    } catch (error) {
      state.validationError = error instanceof Error ? error.message : 'The settings snapshot cannot be safely edited.';
    }
    dom.codeSearch.value = '';
    const configured = systemConfig(data);
    const library = libraryConfig(data);
    const publication = useSet('publicationOptions', state.system).map(normalizeOption);
    const creators = useSet('commonCreators', state.system).map(item => clean(typeof item === 'string' ? item : property(item, 'value'))).filter(Boolean);
    const codes = useSet('allowedPatronCodeIds', state.system)
      .map(item => stringId(typeof item === 'string' || typeof item === 'number' ? item : property(item, 'id')))
      .filter(Boolean);
    const providerValues = state.system
      ? array(property(configured, 'providers'))
      : array(effective(data, 'externalSearchProviders')).map(item => ({ ...item, id: stringId(property(item, 'id')), overridden: bool(property(item, 'overridden')) }));
    const formats = normalizeFormats(state.system ? property(configured, 'formats') : effective(data, 'formats'));
    const effectiveFormats = normalizeFormats(effective(data, 'formats'));
    const fields = normalizeCustomFields(property(property(data, 'stored'), 'customFields'));
    const storedFormatRules = array(property(property(data, 'stored'), 'formatRules'));
    const storedRulesByCode = new Map(storedFormatRules.map(item => [
      clean(property(item, 'code')),
      property(item, 'customFields') || {}
    ]).filter(([code]) => code));
    const rules = effectiveFormats.map(format => {
      const persistedCustomFields = storedRulesByCode.get(format.code) || {};
      const customFields = { ...format.customFields, ...persistedCustomFields };
      return normalizeRule({
        code: format.code,
        messageBehavior: format.messageBehavior,
        message: format.message,
        customFields,
        ...format.fields
      }, format);
    });
    const claims = state.system ? [] : array(property(property(data, 'stored'), 'autoClaimRules')).filter(item => property(item, 'active') !== false);
    const templates = templateEditorValues(data);
    state.originalTemplates = clone(templates.filter(item => item.isCustom));
    state.deletedFormats = [];
    state.models = { publication, creators, codes, providers: providerValues, formats, fields, rules, claims, templates };
    state.ruleRosterCodes = rules.map(item => item.code);

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
    if (!state.data || !state.baseline) {
      throw new Error('Settings are still loading. Reload settings before saving.');
    }
    if ([
      dom.publication,
      dom.creators,
      dom.codes,
      dom.providers,
      dom.formats,
      dom.fields,
      dom.rules,
      dom.claims,
      dom.templates
    ].some(container => !container)) {
      throw new Error('The settings editor is incomplete. Reload settings before saving.');
    }
    if (state.validationError) throw new Error(state.validationError);
    validateCurrentFields();
    validateRenderedRules();

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
    if (values.claims.some(item => !item.materialFormatId || !item.staffUserId)) {
      throw new Error('Each auto-claim rule needs a format and an eligible staff user.');
    }
    const normalizedCodes = values.codes.map(normalizePatronCodeId);
    if (normalizedCodes.some(value => value === null) || new Set(normalizedCodes).size !== normalizedCodes.length) {
      throw new Error('Patron-code IDs must be unique positive Polaris integers.');
    }
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
      rules: state.system
        ? values.rules.map(rule => {
            const serialized = { ...rule };
            delete serialized.customFields;
            return serialized;
          })
        : values.rules,
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
    dispose() { if (disposed) return; disposed = true; events.abort(); },
    populate,
    collect,
    setBaseline: () => { state.baseline = readSnapshot(); },
    snapshot: () => readSnapshot(),
    refreshCodeWarning: updateCodeWarning
  };
}
