import { byDisplayLabel } from './display-order.js';
import { inheritanceStatus } from './settings-inheritance.js';
import { clean, clone } from './settings-values.js';

let nextRulePanelId = 0;
let nextTemplateRowId = 0;

function element(tag, attributes = {}, children = []) {
  const value = document.createElement(tag);
  for (const [name, attribute] of Object.entries(attributes)) {
    if (attribute === null || attribute === undefined) continue;
    if (name === 'className') value.className = attribute;
    else if (name === 'text') value.textContent = attribute;
    else if (name === 'checked') value.checked = Boolean(attribute);
    else if (name === 'disabled') value.disabled = Boolean(attribute);
    else if (name === 'readOnly') value.readOnly = Boolean(attribute);
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

function stringId(value) {
  const result = clean(value);
  return result;
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
      const rawLabel = property(nested, 'label') ?? property(value, `${name}Label`);
      fields[name] = {
        mode: clean(property(nested, 'mode') ?? property(value, `${name}Mode`)) || (name === 'title' ? 'required' : 'optional'),
        label: typeof rawLabel === 'string' && rawLabel.trim() ? rawLabel : (name === 'title' ? 'Title' : name === 'identifier' ? 'Identifier number' : name === 'publication' ? 'Publication Timing' : 'Author')
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
      label: property(value, 'label') ?? 'Unresolved material format',
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
  'rejected',
  'staff_title_assignment',
  'staff_new_patron_request',
  'staff_purchase_reminder',
  'staff_additional_copy_reminder',
  'staff_additional_copy_assignment',
  'staff_weekly_summary'
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
    sortOrder: numeric(property(value, 'sortOrder'), 0),
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


export function createSettingsDomainEditors({ root, onChange = () => {}, onRetryCodes = () => {},
  canRemoveTemplate = () => true, onTemplatesRendered = () => {}, onInvalidTemplate = () => {},
  confirmReset = async () => true }) {
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

  function actions(index, total, move, remove, context = '', kind = 'format rule') {
    const buttons = [
      command('chevron-up', context ? `Move ${context} up` : 'Move up', () => move(index, -1), index === 0),
      command('chevron-down', context ? `Move ${context} down` : 'Move down', () => move(index, 1), index === total - 1)
    ];
    if (remove) buttons.push(command('trash-o', context ? `Delete ${context}${kind ? ` ${kind}` : ''}` : 'Delete', () => remove(index)));
    buttons.forEach((button, position) => { button.dataset.rowAction = ['up', 'down', 'delete'][position]; });
    return element('div', { className: 'settings-row-actions' }, buttons);
  }

  const dom = {
    publication: root.querySelector('#publication-options-editor'),
    publicationInheritField: root.querySelector('#publication-options-inherit-field'),
    creators: root.querySelector('#common-creators-editor'),
    creatorsOptions: root.querySelector('#common-authors-options'),
    creatorsInheritField: root.querySelector('#common-creators-inherit-field'),
    codes: root.querySelector('#patron-codes-editor'),
    codeSearch: root.querySelector('#patron-code-picker'),
    codeOptions: root.querySelector('#patron-code-options'),
    saveBar: root.querySelector('.settings-save-bar'),
    codeSelection: root.querySelector('#patron-code-selection'),
    codeCount: root.querySelector('#patron-code-count'),
    codeAvailability: root.querySelector('#patron-code-availability'),
    codeRetry: root.querySelector('#patron-code-retry'),
    codeWarning: root.querySelector('#patron-code-warning'),
    codeEligibilityEnabled: root.querySelector('#patron-code-eligibility-enabled'),
    codesInheritField: root.querySelector('#patron-codes-inherit-field'),
    providers: root.querySelector('#external-search-provider-editor'),
    formats: root.querySelector('#material-formats-editor'),
    fields: root.querySelector('#additional-fields-editor'),
    rules: root.querySelector('#format-rules-editor'),
    ruleAvailability: root.querySelector('#format-rule-availability'),
    claims: root.querySelector('#format-claim-rules-editor'),
    templates: root.querySelector('#email-templates-editor')
  };
  const addButtons = {
    publication: root.querySelector('#add-publication-option'),
    creators: root.querySelector('#add-common-creator'),
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
    templateOrderBaseline: [],
    deletedFormats: [],
    validationError: null,
    selectedCodes: [],
    codeHighlight: -1,
    codeLookupPending: false
  };
  // Disclosure state never participates in the settings snapshot or payload.
  const expandedRuleCodes = new Set();
  const rulePanelIds = new Map();
  // Reserve allocations until this accepted snapshot is replaced, including
  // deleted draft rows. A rename, redraw, failed save or retry never allocates.
  const reservedDraftKeys = new Set();
  const groupIntents = new Map(), groupOwned = new Map();
  const providerIntents = new Map(), formatIntents = new Map(), templateIntents = new Map();
  const displayed = new WeakMap();
  const formatColumns = ['label', 'sortOrder', 'isEnabled', 'messageBehavior', 'message',
    ...['title', 'author', 'identifier', 'publication'].flatMap(name => [`${name}.mode`, `${name}.label`])];

  function pinItem(intents, id, key) {
    const old = intents.get(id);
    const fields = old?.reset ? Object.fromEntries((intents === formatIntents ? formatColumns : ['label', 'urlTemplate', 'isEnabled'])
      .map(name => [name, 'reset'])) : { ...old?.fields };
    fields[key] = 'pin';
    intents.set(id, { reset: false, fields });
  }

  function itemOwned(accepted, intent, keys) {
    if (intent?.reset) return false;
    if (!intent) return Boolean(accepted);
    return keys.some(key => {
      const action = intent.fields[key];
      if (action) return action === 'pin';
      const [group, part] = key.split('.');
      const rawKey = part ? `${group}${part[0].toUpperCase()}${part.slice(1)}` : key;
      return property(accepted, rawKey) !== null && property(accepted, rawKey) !== undefined;
    });
  }

  function controlStatus(control, status) {
    let wrapper = control.closest('.settings-inheritance-field');
    if (!wrapper) {
      const label = control.closest('label');
      wrapper = element('div', { className: 'settings-inheritance-field' });
      wrapper.hidden = label.hidden;
      label.hidden = false;
      label.before(wrapper); wrapper.append(label);
    }
    const previous = wrapper.querySelector(':scope > .settings-inheritance');
    const comparison = status.querySelector('details.settings-system-comparison');
    if (comparison) comparison.open = Boolean(previous?.querySelector('details.settings-system-comparison')?.open);
    previous?.remove();
    wrapper.append(status);
  }

  function watchControl(control, changed) {
    const read = () => control.type === 'checkbox' ? control.checked : control.value;
    displayed.set(control, read());
    const edit = () => {
      const value = read();
      if (displayed.get(control) === value) return;
      displayed.set(control, value);
      changed();
      onChange();
    };
    listen(control, 'input', edit); listen(control, 'change', edit);
  }

  function systemFormat(code) {
    return normalizeFormats(property(systemConfig(state.data), 'formats')).find(value => value.code === code);
  }

  function rawFormat(code) {
    const source = systemFormat(code);
    return array(property(libraryConfig(state.data), 'formats')).find(value =>
      property(value, 'kind') === 'systemOverride' && stringId(property(value, 'materialFormatId')) === source?.id);
  }

  function formatRuleValue(code) {
    const source = systemFormat(code);
    return source ? normalizeRule({ ...source.fields, messageBehavior: source.messageBehavior, message: source.message, code }) : null;
  }

  function resetFormat(code, focusRule = false) {
    if (disposed || !systemFormat(code)) return;
    formatIntents.set(code, { reset: true, fields: {} });
    const formats = readFormats().map(value => value.code === code ? { ...value, ...systemFormat(code), version: value.version } : value);
    const rules = readRules().map(value => value.code === code ? { ...formatRuleValue(code), customFields: value.customFields } : value);
    renderFormats(formats); renderRules(rules); onChange({ resetLabel: `${ruleFormatLabel(code)} format and built-in rules` });
    if (focusRule) {
      [...dom.rules.querySelectorAll('[data-rule-code]')].find(row => row.dataset.ruleCode === code)?.querySelector('[data-rule-toggle]')?.focus();
    } else {
      [...dom.formats.querySelectorAll('[data-format-code]')].find(row => row.dataset.formatCode === code)
        ?.querySelector('[data-material-field="label"]')?.focus();
    }
  }

  function formatComparison(code, system = false) {
    const material = system ? systemFormat(code) : readFormats().find(value => value.code === code);
    const rule = system ? formatRuleValue(code) : readRules().find(value => value.code === code) || state.models.rules.find(value => value.code === code);
    return [material?.label, material?.isEnabled, material?.sortOrder, rule?.messageBehavior, rule?.message ?? '',
      ...['title', 'author', 'identifier', 'publication'].flatMap(name => [rule?.[name]?.mode, rule?.[name]?.label])];
  }

  function refreshFormatStatus(row, code) {
    row.querySelector(':scope > .settings-inheritance')?.remove();
    if (state.system || !systemFormat(code)) return;
    const intent = formatIntents.get(code), source = systemFormat(code);
    const accepted = rawFormat(code);
    const status = inheritanceStatus({ owned: itemOwned(accepted, intent, formatColumns),
      value: formatComparison(code), systemValue: formatComparison(code, true), pending: Boolean(intent), label: ruleFormatLabel(code), showComparison: false,
      onReset: () => resetFormat(code) });
    row.append(status);
    for (const [selector, key] of [['[data-material-field="label"]', 'label'], ['[data-material-field="enabled"]', 'isEnabled']]) {
      const control = row.querySelector(selector), local = intent?.fields?.[key];
      const columnStatus = inheritanceStatus({ owned: intent?.reset ? false : local ? local === 'pin' : property(accepted, key) !== null && property(accepted, key) !== undefined,
        value: key === 'isEnabled' ? control.checked : control.value, systemValue: source[key], pending: Boolean(intent?.reset || local),
        label: `${ruleFormatLabel(code)} ${key === 'label' ? 'label' : 'visibility'}`, onReset: () => {
          const old = formatIntents.get(code);
          formatIntents.set(code, { reset: false, fields: { ...old?.fields, [key]: 'reset' } });
          if (key === 'isEnabled') control.checked = source[key]; else control.value = source[key];
          displayed.set(control, key === 'isEnabled' ? control.checked : control.value);
          refreshFormatStatus(row, code);
          for (const rule of dom.rules.querySelectorAll('[data-rule-code]')) if (rule.dataset.ruleCode === code) attachRuleInheritance(rule, readRules().find(value => value.code === code));
          onChange({ target: control, resetLabel: `${ruleFormatLabel(code)} ${key}` }); control.focus();
        } });
      columnStatus.dataset.materialStatus = key; controlStatus(control, columnStatus);
    }
  }

  function attachRuleInheritance(row, value) {
    if (state.system || !systemFormat(value.code)) return;
    const source = formatRuleValue(value.code), accepted = rawFormat(value.code), intent = formatIntents.get(value.code);
    for (const control of row.querySelectorAll('[data-rule-property], [data-format-field], [data-format-label]')) {
      const name = control.dataset.ruleProperty || `${control.dataset.formatField || control.dataset.formatLabel}.${control.dataset.formatField ? 'mode' : 'label'}`;
      const [group, propertyName] = name.split('.');
      const inherited = propertyName ? source[group][propertyName] : source[group];
      const rawKey = propertyName ? `${group}${propertyName[0].toUpperCase()}${propertyName.slice(1)}` : group;
      const local = intent?.fields?.[name];
      const owned = intent?.reset ? false : local ? local === 'pin' : property(accepted, rawKey) !== null && property(accepted, rawKey) !== undefined;
      const status = inheritanceStatus({ owned, value: control.value, systemValue: inherited ?? '',
        pending: Boolean(intent?.reset || local), label: control.getAttribute('aria-label'), onReset: () => {
          const old = formatIntents.get(value.code);
          formatIntents.set(value.code, { reset: false, fields: { ...old?.fields, [name]: 'reset' } });
          control.value = inherited ?? ''; displayed.set(control, control.value);
          attachRuleInheritance(row, value);
          for (const formatRow of dom.formats.querySelectorAll('[data-format-code]')) refreshFormatStatus(formatRow, formatRow.dataset.formatCode);
          row.querySelector('[data-rule-property="messageBehavior"]')?.dispatchEvent(new window.Event('change', { bubbles: true }));
          onChange({ target: control, resetLabel: control.getAttribute('aria-label') }); control.focus();
        } });
      status.dataset.ruleInheritance = name;
      controlStatus(control, status);
      if (!displayed.has(control)) watchControl(control, () => {
        pinItem(formatIntents, value.code, name);
        attachRuleInheritance(row, value);
        for (const formatRow of dom.formats.querySelectorAll('[data-format-code]')) refreshFormatStatus(formatRow, formatRow.dataset.formatCode);
      });
    }
    row.querySelector(':scope > .settings-inheritance')?.remove();
    const status = inheritanceStatus({ owned: itemOwned(accepted, intent, formatColumns),
      value: formatComparison(value.code), systemValue: formatComparison(value.code, true), pending: Boolean(intent), label: `${ruleFormatLabel(value.code)} format and built-in rules`, showComparison: false,
      onReset: () => resetFormat(value.code, true) });
    row.append(status);
  }

  function inheritedGroup(name) {
    return groupIntents.has(name) ? groupIntents.get(name) === 'reset' : !groupOwned.get(name);
  }

  function markGroup(name) {
    if (state.system) return;
    groupIntents.set(name, 'pin');
    refreshGroupStatus(name);
  }

  function refreshGroupStatus(name) {
    const wrapper = name === 'publication' ? dom.publicationInheritField : name === 'creators' ? dom.creatorsInheritField : dom.codesInheritField;
    if (!wrapper) return;
    wrapper.hidden = state.system;
    if (state.system) { wrapper.replaceChildren(); return; }
    const current = name === 'publication' ? readPublication() : name === 'creators' ? readSetRows(dom.creators, 'creator') : readSelectedCodes();
    const label = name === 'publication' ? 'Publication options' : name === 'creators' ? 'Common creators' : 'Allowed patron codes';
    const comparison = values => name === 'publication' ? values.map(item => `${item.label}${item.enabled ? '' : ' (disabled)'}`) : values;
    const comparable = values => name === 'publication' ? values.map(item => [item.id, item.label, item.enabled])
      : name === 'codes' ? [...values].sort((left, right) => Number(left) - Number(right)) : values;
    const status = inheritanceStatus({ owned: !inheritedGroup(name), value: comparison(current),
      systemValue: comparison(setValues(name, true)), pending: groupIntents.has(name), label,
      comparisonOpen: wrapper.querySelector('details.settings-system-comparison')?.open,
      matchesSystem: JSON.stringify(comparable(current)) === JSON.stringify(comparable(setValues(name, true))),
      onReset: async () => {
        const owner = state.data, stamp = readSnapshot();
        if (!await confirmReset(`${label}: replace the entire library selection with the System selection when you save. Library choices in this group will be removed; historical request selections are preserved.`) ||
            disposed || state.data !== owner || readSnapshot() !== stamp) return;
        groupIntents.set(name, 'reset');
        renderDomain(name, setValues(name, true));
        refreshGroupStatus(name);
        onChange({ resetLabel: label });
        (name === 'codes' ? dom.codeSearch : addButtons[name])?.focus();
      } });
    wrapper.replaceChildren(status);
  }

  function draftKey(prefix, existing) {
    for (const value of existing) reservedDraftKeys.add(String(value || '').toLowerCase());
    let key;
    do {
      key = `${prefix}${window.crypto.randomUUID().replaceAll('-', '')}`;
    } while (reservedDraftKeys.has(key.toLowerCase()));
    reservedDraftKeys.add(key.toLowerCase());
    return key;
  }

  function currentOrganizationId() {
    const value = property(state.data, 'orgId');
    return value === 'system' || value === undefined ? 1 : numeric(value, 1);
  }

  function creatorsOptionsHidden() {
    return Boolean(dom.creatorsOptions?.hidden);
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
    return values.map(item => {
      const id = typeof item === 'string' || typeof item === 'number' ? item : property(item, 'id');
      return normalizePatronCodeId(id) ?? stringId(id);
    }).filter(Boolean);
  }

  function rowControls(row) {
    return [...row.querySelectorAll('[data-domain-editable]')];
  }

  function updateRowDisabled(row, disabled) {
    for (const control of rowControls(row)) control.disabled = disabled;
    for (const button of row.querySelectorAll('.settings-row-actions button')) button.disabled = disabled;
  }

  function updateCreatorRowsDisabled(container, disabled) {
    const rows = [...(container?.querySelectorAll('[data-domain-row]') || [])];
    for (const [index, row] of rows.entries()) {
      for (const control of rowControls(row)) control.disabled = disabled;
      for (const button of row.querySelectorAll('.settings-row-actions button')) {
        const action = button.dataset.rowAction;
        const atBoundary = (action === 'up' && index === 0) || (action === 'down' && index === rows.length - 1);
        button.disabled = disabled || atBoundary;
      }
    }
  }

  function setEditorControls(type, container) {
    const addButton = type === 'publication' ? addButtons.publication : addButtons.creators;
    if (type === 'creator') {
      updateCreatorRowsDisabled(container, false);
      if (addButton) addButton.disabled = false;
      return;
    }
    updateRowDisabled(container, false);
    if (addButton) addButton.disabled = false;
  }

  function focusSetRow(index, action) {
    const row = dom.creators.querySelectorAll('[data-domain-row]')[index];
    const target = action ? row?.querySelector(`[data-row-action="${action}"]:not(:disabled)`) : null;
    const input = row?.querySelector('input');
    const focusTarget = target || input || addButtons.creators;
    focusTarget?.focus();
    if (!row || focusTarget !== input) return;

    const view = root.ownerDocument.defaultView;
    const style = view.getComputedStyle(input);
    const clearance = Math.max(6, (parseFloat(style.outlineWidth) || 0) + (parseFloat(style.outlineOffset) || 0)) + 1;
    const viewport = view.visualViewport;
    const top = (viewport?.offsetTop || 0) + clearance;
    let bottom = (viewport?.offsetTop || 0) + (viewport?.height || view.innerHeight) - clearance;
    const saveBar = root.querySelector('.settings-save-bar');
    if (saveBar && view.getComputedStyle(saveBar).position === 'sticky') {
      bottom = Math.min(bottom, saveBar.getBoundingClientRect().top - clearance);
    }

    const bounds = row.getBoundingClientRect();
    if (!bounds.height || bounds.height > bottom - top) return;
    const scroll = bounds.top < top ? bounds.top - top : Math.max(0, bounds.bottom - bottom);
    if (scroll) view.scrollBy({ top: scroll, behavior: 'instant' });
  }

  function updateCodeWarning() {
    if (disposed) return;
    const empty = readSelectedCodes().length === 0;
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
      setEditorControls('creator', container);
      refreshGroupStatus('creators');
      return;
    }
    for (const [index, value] of values.entries()) {
      const input = element('input', {
          type: 'text',
          value,
          'data-domain-editable': 'true'
        });
      const row = element('div', {
        className: type === 'creator' ? 'settings-editor-row settings-creator-row' : 'settings-editor-row',
        'data-domain-row': 'true'
      }, [
        field('Creator', input),
        actions(index, values.length, (from, offset) => {
          if (type === 'creator' && creatorsOptionsHidden()) return;
          const next = from + offset;
          if (next < 0 || next >= values.length) return;
          const current = readSetRows(container, type);
          markGroup('creators');
          [current[from], current[next]] = [current[next], current[from]];
          renderSetRows(container, current, type);
          if (type === 'creator') focusSetRow(next, offset < 0 ? 'up' : 'down');
          onChange();
        }, from => {
          if (type === 'creator' && creatorsOptionsHidden()) return;
          const current = readSetRows(container, type);
          markGroup('creators');
          current.splice(from, 1);
          renderSetRows(container, current, type);
          if (type === 'creator') focusSetRow(Math.min(from, current.length - 1));
          onChange();
        })
      ]);
      container.append(row);
      watchControl(input, () => markGroup('creators'));
    }
    setEditorControls('creator', container);
    refreshGroupStatus('creators');
  }

  function readSetRows(container, type) {
    return [...(container?.querySelectorAll('[data-domain-row]') || [])]
      .map(row => clean(row.querySelector('select, input')?.value));
  }

  function readSelectedCodes() {
    return [...state.selectedCodes];
  }

  function codesInherited() {
    return !state.system && inheritedGroup('codes');
  }

  function codeChoices() {
    const choices = new Map();
    for (const choice of array(property(state.data, 'patronCodeChoices'))) {
      const id = normalizePatronCodeId(property(choice, 'id'));
      if (id && !choices.has(id)) {
        choices.set(id, { id, label: `${clean(property(choice, 'description')) || 'Patron code'} (${id})` });
      }
    }
    return byDisplayLabel([...choices.values()], choice => choice.label, choice => choice.id);
  }

  function availableCodes() {
    const term = dom.codeSearch.value.trim().toLocaleLowerCase();
    return codeChoices().filter(choice => !state.selectedCodes.includes(choice.id) &&
      choice.label.toLocaleLowerCase().includes(term));
  }

  function closeCodePicker() {
    dom.codeOptions.hidden = true;
    dom.codeSearch.setAttribute('aria-expanded', 'false');
    dom.codeSearch.removeAttribute('aria-activedescendant');
    state.codeHighlight = -1;
  }

  function scrollActiveCode() {
    const active = document.getElementById(dom.codeSearch.getAttribute('aria-activedescendant'));
    if (!active || dom.codeOptions.hidden) return;
    // scrollIntoView also scrolls the page, where it cannot account for the
    // sticky save bar. Keyboard navigation must scroll only the listbox.
    const list = dom.codeOptions.getBoundingClientRect();
    const option = active.getBoundingClientRect();
    const top = list.top + dom.codeOptions.clientTop;
    const bottom = top + dom.codeOptions.clientHeight;
    if (option.top < top) dom.codeOptions.scrollTop += option.top - top;
    else if (option.bottom > bottom) dom.codeOptions.scrollTop += option.bottom - bottom;
  }

  function positionCodePicker(reveal = false) {
    if (disposed || document.activeElement !== dom.codeSearch || !dom.codeSearch.getClientRects().length) return;
    const viewport = window.visualViewport;
    // Allow room for the input's focus ring, independent of zoom or footer wrapping.
    const gap = 6;
    const top = (viewport?.offsetTop || 0) + gap;
    let bottom = (viewport?.offsetTop || 0) + (viewport?.height || window.innerHeight) - gap;
    if (dom.saveBar?.classList.contains('attention')) {
      bottom = Math.min(bottom, dom.saveBar.getBoundingClientRect().top - gap);
    }
    let input = dom.codeSearch.getBoundingClientRect();
    if (!reveal && (input.top < top - 1 || input.bottom > bottom + 1)) {
      // Scrolling away dismisses the list instead of pulling the page back to
      // the focused input. Opening/focusing explicitly reveals it again.
      closeCodePicker();
      return;
    }
    const scroll = input.top < top ? input.top - top : Math.max(0, input.bottom - bottom);
    if (scroll) {
      window.scrollBy({ top: scroll, behavior: 'instant' });
      input = dom.codeSearch.getBoundingClientRect();
    }
    if (dom.codeOptions.hidden) return;
    const above = Math.max(0, input.top - top - gap);
    const below = Math.max(0, bottom - input.bottom - gap);
    const upwards = above > below;
    const space = upwards ? above : below;
    dom.codeOptions.classList.toggle('opens-up', upwards);
    dom.codeOptions.style.setProperty('--patron-code-space', `${space}px`);
    scrollActiveCode();
  }

  function highlightCode(index) {
    const options = [...dom.codeOptions.querySelectorAll('[role="option"]')];
    state.codeHighlight = options.length && index >= 0 ? index % options.length : -1;
    for (const [position, option] of options.entries()) {
      option.setAttribute('aria-selected', String(position === state.codeHighlight));
    }
    const active = options[state.codeHighlight];
    if (active) {
      dom.codeSearch.setAttribute('aria-activedescendant', active.id);
      scrollActiveCode();
    } else {
      dom.codeSearch.removeAttribute('aria-activedescendant');
    }
  }

  function renderCodeOptions() {
    if (dom.codeSearch.disabled) return;
    dom.codeOptions.replaceChildren();
    const choices = availableCodes();
    for (const choice of choices) {
      const option = element('div', { id: `patron-code-option-${choice.id}`, role: 'option',
        'aria-selected': 'false', text: choice.label });
      // Keep DOM focus on the combobox for mouse and touch selection.
      listen(option, 'pointerdown', event => event.preventDefault());
      listen(option, 'click', () => {
        if (dom.codeSearch.disabled || !availableCodes().some(value => value.id === choice.id)) return;
        markGroup('codes');
        renderCodes([...state.selectedCodes, choice.id]);
        dom.codeSearch.value = '';
        closeCodePicker();
        dom.codeSearch.focus({ preventScroll: true });
        onChange();
        positionCodePicker(true);
      });
      dom.codeOptions.append(option);
    }
    dom.codeAvailability.textContent = choices.length ? `${choices.length} available patron codes.` : 'No matching patron codes.';
    dom.codeOptions.hidden = choices.length === 0;
    dom.codeSearch.setAttribute('aria-expanded', String(choices.length > 0));
    highlightCode(-1);
    positionCodePicker(true);
  }

  function renderCodes(values) {
    state.selectedCodes = [...values];
    if (!dom.codes || !dom.codeSelection) return;
    const focusedChip = dom.codes.contains(document.activeElement)
      ? document.activeElement.closest('[data-code-id]')?.dataset.codeId : null;
    // The form's existing draft tracking observes this value, never picker text or focus.
    dom.codeSelection.value = JSON.stringify([...values].sort((left, right) => Number(left) - Number(right)));
    dom.codes.replaceChildren();
    const choices = codeChoices();
    const inherited = codesInherited();
    for (const [index, id] of values.entries()) {
      const label = choices.find(choice => choice.id === id)?.label || `Unavailable code (${id})`;
      const remove = element('button', { type: 'button', className: 'patron-code-remove',
        'aria-label': `Remove ${label}`, text: '×' });
      listen(remove, 'click', () => {
        if (!state.selectedCodes.includes(id)) return;
        markGroup('codes');
        renderCodes(state.selectedCodes.filter(value => value !== id));
        const buttons = [...dom.codes.querySelectorAll('button')];
        const fallback = !dom.codeSearch.disabled ? dom.codeSearch
          : !dom.codeRetry.disabled ? dom.codeRetry
          : dom.codeEligibilityEnabled;
        (buttons[Math.min(index, buttons.length - 1)] || fallback).focus();
        onChange();
      });
      dom.codes.append(element('li', { 'data-code-id': id }, [element('span', { text: label }), remove]));
    }
    dom.codeCount.textContent = `${values.length} patron ${values.length === 1 ? 'code' : 'codes'} selected${inherited ? ' (using system IDs)' : ''}.`;
    dom.codeSearch.disabled = state.codeLookupPending || Boolean(property(state.data, 'patronCodeChoicesUnavailable'));
    dom.codeRetry.hidden = !property(state.data, 'patronCodeChoicesUnavailable');
    dom.codeRetry.disabled = state.codeLookupPending;
    dom.codeAvailability.textContent = state.codeLookupPending ? 'Loading patron codes…' : property(state.data, 'patronCodeChoicesUnavailable')
      ? 'Patron-code lookup unavailable. Saved selections are preserved. Retry to add codes.'
      : '';
    closeCodePicker();
    updateCodeWarning();
    refreshGroupStatus('codes');
    if (focusedChip) {
      const button = [...dom.codes.children].find(chip => chip.dataset.codeId === focusedChip)?.querySelector('button');
      if (button && !button.disabled) button.focus();
    }
  }

  function renderPublication(values) {
    if (!dom.publication) return;
    dom.publication.replaceChildren();
    if (values.length === 0) dom.publication.append(element('p', { className: 'settings-empty', text: 'No publication options configured.' }));
    for (const [index, value] of values.entries()) {
      const label = element('input', { type: 'text', value: value.label, 'data-publication-field': 'label', 'data-domain-editable': 'true' });
      const enabled = element('input', { type: 'checkbox', checked: value.enabled, 'data-publication-field': 'enabled', 'data-domain-editable': 'true' });
      const row = element('div', { className: 'settings-editor-row settings-option-row', 'data-domain-row': 'true', 'data-option-id': value.id }, [
        field('Label', label),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Enabled' })]),
        actions(index, values.length, (from, offset) => reorder('publication', from, offset), from => remove('publication', from), 'publication option', '')
      ]);
      const refreshNames = () => {
        const context = `${clean(label.value) || 'Untitled'}, publication option ${index + 1}`;
        label.setAttribute('aria-label', `Label for ${context}`);
        enabled.setAttribute('aria-label', `Enabled for ${context}`);
        const names = [`Move ${context} up`, `Move ${context} down`, `Delete ${context}`];
        [...row.querySelectorAll('.settings-row-actions button')].forEach((button, position) => {
          button.title = names[position];
          button.setAttribute('aria-label', names[position]);
        });
      };
      listen(label, 'input', refreshNames);
      listen(label, 'change', refreshNames);
      refreshNames();
      dom.publication.append(row);
      watchControl(label, () => markGroup('publication'));
      watchControl(enabled, () => markGroup('publication'));
    }
    setEditorControls('publication', dom.publication);
    const inherited = false;
    [...dom.publication.querySelectorAll('[data-domain-row]')].forEach((row, index) => {
      row.querySelector('[data-row-action="up"]').disabled = inherited || index === 0;
      row.querySelector('[data-row-action="down"]').disabled = inherited || index === values.length - 1;
    });
    refreshGroupStatus('publication');
  }

  function readPublication() {
    return [...(dom.publication?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => ({
      id: stringId(row.dataset.optionId),
      label: clean(row.querySelector('[data-publication-field="label"]')?.value),
      enabled: Boolean(row.querySelector('[data-publication-field="enabled"]')?.checked),
      sortOrder: (index + 1) * 10
    }));
  }

  function renderProviders(values) {
    if (!dom.providers) return;
    dom.providers.replaceChildren();
    if (values.length === 0) dom.providers.append(element('p', { className: 'settings-empty', text: 'No providers configured.' }));
    for (const [index, value] of values.entries()) {
      const id = stringId(value.id);
      const enabled = element('input', { type: 'checkbox', checked: value.isEnabled, 'data-domain-editable': 'true' });
      const row = element('div', {
        className: 'settings-editor-row settings-provider-row',
        'data-domain-row': 'true',
        'data-provider-id': id || '',
        'data-provider-key': value.key || '',
        'data-provider-sort-order': numeric(value.sortOrder, (index + 1) * 10)
      }, [
        field('Label', element('input', { type: 'text', value: value.label || '', 'data-domain-editable': 'true' })),
        field('URL template', element('input', { type: 'url', value: value.urlTemplate || '', 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Enabled' })]),
        actions(index, values.length, (from, offset) => reorder('providers', from, offset), from => remove('providers', from))
      ]);
      dom.providers.append(row);
      if (!state.system) {
        const inherited = array(property(systemConfig(state.data), 'providers')).find(item => stringId(property(item, 'id')) === id);
        const accepted = array(property(libraryConfig(state.data), 'providers')).find(item => stringId(property(item, 'id')) === id);
        const refresh = () => {
          row.querySelector(':scope > .settings-inheritance')?.remove();
          const intent = providerIntents.get(id);
          const current = readProviders().find(item => item.id === id);
          const compare = item => [property(item, 'label'), property(item, 'urlTemplate'), property(item, 'isEnabled')];
          row.append(inheritanceStatus({ owned: itemOwned(accepted, intent, ['label', 'urlTemplate', 'isEnabled']),
            value: compare(current), systemValue: compare(inherited), pending: Boolean(intent), label: value.label || value.key, showComparison: false,
            onReset: () => {
              providerIntents.set(id, { reset: true, fields: {} });
              const currentValues = readProviders().map(item => item.id === id ? { ...item, ...inherited } : item);
              renderProviders(currentValues); onChange({ resetLabel: value.label || 'Provider' });
              [...dom.providers.querySelectorAll('[data-provider-id]')].find(item => item.dataset.providerId === id)?.querySelector('input')?.focus();
            } }));
          for (const [position, key] of ['label', 'urlTemplate', 'isEnabled'].entries()) {
            const control = row.querySelectorAll('input')[position], local = intent?.fields?.[key];
            const status = inheritanceStatus({ owned: intent?.reset ? false : local ? local === 'pin' : property(accepted, key) !== null && property(accepted, key) !== undefined,
              value: key === 'isEnabled' ? control.checked : control.value, systemValue: property(inherited, key), pending: Boolean(intent?.reset || local),
              label: `${value.label || value.key} ${key}`, onReset: () => {
                const old = providerIntents.get(id);
                providerIntents.set(id, { reset: false, fields: { ...old?.fields, [key]: 'reset' } });
                if (key === 'isEnabled') control.checked = property(inherited, key); else control.value = property(inherited, key) ?? '';
                displayed.set(control, key === 'isEnabled' ? control.checked : control.value);
                refresh(); onChange({ target: control, resetLabel: `${value.label || 'Provider'} ${key}` }); control.focus();
              } });
            status.dataset.providerStatus = key; controlStatus(control, status);
          }
        };
        [...row.querySelectorAll('input')].forEach((control, position) => watchControl(control, () => {
          pinItem(providerIntents, id, ['label', 'urlTemplate', 'isEnabled'][position]); refresh();
        }));
        refresh();
        // Provider ordering and creation belong to System; nullable overrides own content only.
        for (const button of row.querySelectorAll('.settings-row-actions button')) button.disabled = true;
      }
    }
  }

  function readProviders() {
    return [...(dom.providers?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => {
      const inputs = [...row.querySelectorAll('input')];
      return {
        id: stringId(row.dataset.providerId),
        key: row.dataset.providerKey,
        label: inputs[0]?.value ?? '',
        urlTemplate: inputs[1]?.value ?? '',
        isEnabled: Boolean(row.querySelector('input[type="checkbox"]')?.checked),
        sortOrder: numeric(row.dataset.providerSortOrder, (index + 1) * 10),
        overridden: state.system || !providerIntents.get(row.dataset.providerId)?.reset &&
          (Boolean(providerIntents.get(row.dataset.providerId)) || Boolean(state.models.providers.find(value => value.id === row.dataset.providerId)?.overridden)),
        reset: !state.system && Boolean(providerIntents.get(row.dataset.providerId)?.reset)
      };
    });
  }

  function fieldPair(name, value, format) {
    const group = element('div', { className: 'settings-format-field-group' });
    const readable = name.charAt(0).toUpperCase() + name.slice(1);
    const mode = select([
      { value: 'required', label: 'Required' },
      { value: 'optional', label: 'Optional' },
      { value: 'hidden', label: 'Hidden' }
    ], value.mode, {
      'data-format-field': name,
      'data-domain-editable': 'true',
      'aria-label': `${readable} requirement for ${format}`
    });
    const label = element('input', {
      type: 'text',
      value: value.label || '',
      'data-format-label': name,
      'data-domain-editable': 'true',
      'aria-label': `${readable} patron-facing label for ${format}`
    });
    group.append(element('strong', { className: 'settings-format-field-name', text: readable }),
      field('Requirement', mode), field('Patron-facing label', label));
    return group;
  }

  function renderFormats(values) {
    if (!dom.formats) return;
    dom.formats.replaceChildren();
    if (values.length === 0) dom.formats.append(element('p', { className: 'settings-empty', text: 'No material formats configured.' }));
    for (const [index, value] of values.entries()) {
      const custom = value.ownerOrganizationId === currentOrganizationId() && !state.system;
      const label = element('input', { type: 'text', value: value.label, 'data-material-field': 'label', 'data-domain-editable': 'true' });
      const enabled = element('input', { type: 'checkbox', checked: value.isEnabled, 'data-material-field': 'enabled', 'data-domain-editable': 'true' });
      const row = element('div', {
        className: 'settings-editor-row settings-format-row',
        'data-domain-row': 'true',
        'data-format-id': value.id || '',
        'data-format-code': value.code,
        'data-format-owner': value.ownerOrganizationId,
        'data-format-version': value.version || '',
        'data-format-sort-order': value.sortOrder
      }, [
        field('Label', label),
        element('label', { className: 'settings-domain-check' }, [enabled, element('span', { text: 'Shown' })]),
        actions(
          index,
          values.length,
          (from, offset) => reorder('formats', from, offset),
          custom ? from => removeFormat(from) : null
        )
      ]);
      const refreshNames = () => {
        for (const rule of dom.rules?.querySelectorAll('[data-rule-code]') || []) refreshRuleFormatName(rule);
      };
      listen(label, 'input', refreshNames);
      listen(label, 'change', refreshNames);
      dom.formats.append(row);
      if (!state.system && !custom) {
        for (const [control, key] of [[label, 'label'], [enabled, 'isEnabled']]) watchControl(control, () => {
          pinItem(formatIntents, value.code, key); refreshFormatStatus(row, value.code);
          for (const rule of dom.rules.querySelectorAll('[data-rule-code]')) if (rule.dataset.ruleCode === value.code) attachRuleInheritance(rule, readRules().find(item => item.code === value.code));
        });
        refreshFormatStatus(row, value.code);
      }
    }
    updateRuleAvailability();
  }

  function eligibleRuleFormats(values = readRules()) {
    const represented = new Set(values.map(rule => rule.code));
    return readFormats().filter(format => !represented.has(format.code));
  }

  function updateRuleAvailability() {
    if (!addButtons.rules || !dom.ruleAvailability) return;
    const formats = readFormats();
    const available = eligibleRuleFormats();
    const message = formats.length === 0
      ? 'Add a material format before adding a rule.'
      : available.length === 0
        ? 'All available formats already have rules.'
        : '';
    addButtons.rules.disabled = available.length === 0;
    dom.ruleAvailability.textContent = message;
    dom.ruleAvailability.hidden = !message;
  }

  function removeFormat(index) {
    const values = readFormats();
    const item = values[index];
    if (!item || state.system || String(item.ownerOrganizationId) !== String(currentOrganizationId())) return;
    if (!item.id && !canRedrawRules()) return;
    if (item.id) state.deletedFormats.push({ id: item.id, version: item.version || '' });
    values.splice(index, 1);
    renderFormats(values);
    if (!item.id) {
      const rules = readRules().filter(rule => rule.code !== item.code);
      state.ruleRosterCodes = state.ruleRosterCodes.filter(code => code !== item.code);
      renderRules(rules);
    }
    (dom.formats.querySelectorAll('[data-domain-row]')[Math.min(index, values.length - 1)]
      ?.querySelector('[data-material-field="label"]') || addButtons.formats)?.focus();
    onChange();
  }

  function readFormats() {
    return [...(dom.formats?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => {
      const owner = numeric(row.dataset.formatOwner, 1);
      const code = clean(row.dataset.formatCode);
      return {
        id: stringId(row.dataset.formatId),
        version: stringId(row.dataset.formatVersion),
        code,
        ownerOrganizationId: owner,
        label: row.querySelector('[data-material-field="label"]')?.value ?? '',
        sortOrder: numeric(row.dataset.formatSortOrder, (index + 1) * 10),
        isEnabled: Boolean(row.querySelector('[data-material-field="enabled"]')?.checked),
        overridden: state.system || owner === currentOrganizationId() || !formatIntents.get(code)?.reset &&
          (Boolean(formatIntents.get(code)) || Boolean(rawFormat(code))),
        reset: !state.system && owner !== currentOrganizationId() && Boolean(formatIntents.get(code)?.reset)
      };
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
        'data-field-id': value.id || '',
        'data-field-key': value.key
      }, [
        field('Label', element('input', { type: 'text', value: value.label, 'data-custom-field': 'label', 'data-domain-editable': 'true' })),
        field('Type', select([
          { value: 'text', label: 'Text' },
          { value: 'textarea', label: 'Long text' },
          { value: 'select', label: 'Select' }
        ], value.type, { 'data-custom-field': 'type', 'data-domain-editable': 'true' })),
        field('Help text', element('input', { type: 'text', value: value.helpText || '', 'data-custom-field': 'helpText', 'data-domain-editable': 'true' })),
        element('label', { className: 'settings-domain-check' }, [element('input', { type: 'checkbox', checked: value.enabled, 'data-custom-field': 'enabled', 'data-domain-editable': 'true' }), element('span', { text: 'Enabled' })]),
        element('div', { className: 'settings-custom-options', 'data-options-editor': 'true' }),
        actions(index, values.length, (from, offset) => reorder('fields', from, offset), from => remove('fields', from))
      ]);
      const optionsEditor = row.querySelector('[data-options-editor]');
      renderFieldOptions(optionsEditor, value.options, value.type === 'select');
      listen(row.querySelector('select'), 'change', event => {
        optionsEditor.hidden = event.target.value !== 'select';
        onChange();
      });
      // Definition labels and enabled status affect the draft rule summary too.
      for (const control of [row.querySelector('[data-custom-field="label"]'), row.querySelector('[data-custom-field="enabled"]')]) {
        listen(control, 'input', () => refreshRuleDefinitions());
        listen(control, 'change', () => refreshRuleDefinitions());
      }
      dom.fields.append(row);
    }
  }

  function renderFieldOptions(container, values, visible) {
    if (!container) return;
    container.hidden = !visible;
    container.replaceChildren(element('strong', { text: 'Select options' }));
    for (const [index, value] of values.entries()) {
      const row = element('div', { className: 'settings-option-subrow', 'data-option-row': 'true', 'data-option-id': value.id }, [
        element('input', { type: 'text', value: value.label, 'data-option-field': 'label', 'data-domain-editable': 'true', 'aria-label': 'Option label' }),
        element('label', { className: 'settings-domain-check' }, [element('input', { type: 'checkbox', checked: value.enabled, 'data-option-field': 'enabled', 'data-domain-editable': 'true' }), element('span', { text: 'Enabled' })]),
        command('trash-o', 'Delete option', () => {
          const current = readFields();
          const parent = container.closest('[data-domain-row]');
          const fieldIndex = [...dom.fields.querySelectorAll('[data-domain-row]')].indexOf(parent);
          current[fieldIndex].options.splice(index, 1);
          renderFields(current);
          focusFieldOption(current[fieldIndex].key, Math.min(index, current[fieldIndex].options.length - 1));
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
      const fieldValue = current[fieldIndex];
      const accepted = state.models.fields.find(item => item.key === fieldValue.key)?.options || [];
      fieldValue.options.push({ id: draftKey('option_', [...accepted, ...fieldValue.options].map(item => item.id)), label: 'New option', enabled: true, sortOrder: (fieldValue.options.length + 1) * 10 });
      renderFields(current);
      focusFieldOption(fieldValue.key, fieldValue.options.length - 1);
      onChange();
    });
    container.append(add);
  }

  function focusFieldOption(key, index) {
    const row = [...dom.fields.querySelectorAll('[data-domain-row]')].find(item => item.dataset.fieldKey === key);
    (row?.querySelectorAll('[data-option-field="label"]')[index] ||
      row?.querySelector('[data-options-editor] > button'))?.focus();
  }

  function readFields() {
    return [...(dom.fields?.querySelectorAll('[data-domain-row]') || [])].map((row, index) => ({
      id: stringId(row.dataset.fieldId),
      key: clean(row.dataset.fieldKey),
      label: clean(row.querySelector('[data-custom-field="label"]')?.value),
      type: row.querySelector('[data-custom-field="type"]')?.value || 'text',
      helpText: clean(row.querySelector('[data-custom-field="helpText"]')?.value),
      enabled: Boolean(row.querySelector('[data-custom-field="enabled"]')?.checked),
      sortOrder: (index + 1) * 10,
      options: [...(row.querySelectorAll('[data-option-row]') || [])].map((option, optionIndex) => ({
        id: stringId(option.dataset.optionId),
        label: clean(option.querySelector('[data-option-field="label"]')?.value),
        enabled: Boolean(option.querySelector('[data-option-field="enabled"]')?.checked),
        sortOrder: (optionIndex + 1) * 10
      }))
    }));
  }

  function updateRuleSummary(row) {
    const enabledKeys = new Set(readFields().filter(item => item.enabled).map(item => item.key));
    const controls = [...row.querySelectorAll('[data-format-field]'),
      ...[...row.querySelectorAll('[data-custom-rule-key]')]
        .filter(group => enabledKeys.has(group.dataset.customRuleKey))
        .map(group => group.querySelector('[data-custom-rule-property="mode"]'))];
    const counts = { required: 0, optional: 0, hidden: 0 };
    for (const control of controls) {
      if (control && Object.hasOwn(counts, control.value)) counts[control.value] += 1;
    }
    const summary = row.querySelector('[data-rule-summary]');
    const text = `${counts.required} required · ${counts.optional} optional · ${counts.hidden} hidden`;
    if (summary.textContent !== text) summary.textContent = text;
  }

  function refreshRuleDefinitions() {
    const fields = new Map(readFields().map(item => [item.key, item]));
    for (const row of dom.rules?.querySelectorAll('[data-rule-code]') || []) {
      for (const group of row.querySelectorAll('[data-custom-rule-key]')) {
        const definition = fields.get(group.dataset.customRuleKey);
        const label = definition?.label || `Retired field (${group.dataset.customRuleKey})`;
        group.querySelector('.settings-custom-rule-name').textContent = label;
        group.querySelector('.settings-custom-rule-status').textContent = !definition
          ? 'Retired field — saved rule retained' : !definition.enabled ? 'Disabled field — not shown to patrons' : '';
      }
      refreshRuleFormatName(row);
      updateRuleSummary(row);
    }
  }

  function ruleFormatLabel(code) {
    const format = readFormats().find(item => item.code === code);
    const label = format?.reset
      ? property(array(property(systemConfig(state.data), 'formats')).find(item => property(item, 'code') === code), 'label')
      : format?.label;
    return clean(label) || 'Unresolved material format';
  }

  function refreshRuleFormatName(row) {
    const label = ruleFormatLabel(row.dataset.ruleCode);
    const rows = [...dom.rules.querySelectorAll('[data-rule-code]')];
    const duplicates = rows.filter(item => ruleFormatLabel(item.dataset.ruleCode) === label);
    const context = duplicates.length > 1 ? `${label}, format ${rows.indexOf(row) + 1}` : label;
    row.querySelector('.settings-rule-identity strong').textContent = label;
    const toggle = row.querySelector('[data-rule-toggle]');
    toggle.setAttribute('aria-label', `${toggle.getAttribute('aria-expanded') === 'true' ? 'Collapse' : 'Expand'} ${context} format rules`);
    for (const [property, name] of [['messageBehavior', 'Message behavior'], ['message', 'Message']]) {
      row.querySelector(`[data-rule-property="${property}"]`)?.setAttribute('aria-label', `${name} for ${context}`);
    }
    for (const name of ['title', 'author', 'identifier', 'publication']) {
      const readable = name.charAt(0).toUpperCase() + name.slice(1);
      row.querySelector(`[data-format-field="${name}"]`)?.setAttribute('aria-label', `${readable} requirement for ${context}`);
      row.querySelector(`[data-format-label="${name}"]`)?.setAttribute('aria-label', `${readable} patron-facing label for ${context}`);
    }
    for (const group of row.querySelectorAll('[data-custom-rule-key]')) {
      const fieldLabel = group.querySelector('.settings-custom-rule-name').textContent;
      for (const [property, name] of [['mode', 'mode'], ['labelOverride', 'label override']]) {
        group.querySelector(`[data-custom-rule-property="${property}"]`)
          ?.setAttribute('aria-label', `${fieldLabel} ${name} for ${context}`);
      }
    }
    const actions = [`Move ${context} up`, `Move ${context} down`, `Delete ${context} format rule`];
    [...row.querySelectorAll('.settings-row-actions button')].forEach((button, index) => {
      button.title = actions[index];
      button.setAttribute('aria-label', actions[index]);
    });
  }

  function renderRules(values) {
    if (!dom.rules) return;
    const focused = document.activeElement;
    const focusedRow = dom.rules.contains(focused) ? focused.closest('[data-rule-code]') : null;
    const focusedIndex = [...dom.rules.children].indexOf(focusedRow);
    const focusedCode = focusedRow?.dataset.ruleCode;
    const focusedName = focused?.getAttribute('aria-label');
    const focusedAction = focused?.dataset.rowAction;
    const codes = new Set(values.map(value => value.code));
    for (const code of expandedRuleCodes) {
      if (!codes.has(code)) expandedRuleCodes.delete(code);
    }
    for (const code of rulePanelIds.keys()) {
      if (!codes.has(code)) rulePanelIds.delete(code);
    }
    dom.rules.replaceChildren();
    if (values.length === 0) dom.rules.append(element('p', { className: 'settings-empty', text: 'No format rules configured.' }));
    const customFields = readFields();
    for (const [index, value] of values.entries()) {
      const formatLabel = ruleFormatLabel(value.code);
      const formatContext = formatLabel;
      if (!rulePanelIds.has(value.code)) rulePanelIds.set(value.code, `format-rule-panel-${++nextRulePanelId}`);
      const panelId = rulePanelIds.get(value.code);
      const toggle = element('button', { type: 'button', className: 'settings-rule-toggle',
        'aria-controls': panelId, 'aria-describedby': `${panelId}-summary`, 'data-rule-toggle': 'true' }, [
        element('span', { className: 'settings-rule-chevron', 'aria-hidden': 'true', text: '›' }),
        element('span', { className: 'settings-rule-identity' }, [
          element('strong', { text: formatLabel })
        ]),
        element('span', { id: `${panelId}-summary`, className: 'settings-rule-summary', 'data-rule-summary': 'true',
          role: 'status', 'aria-live': 'polite', 'aria-atomic': 'true' })
      ]);
      const customFieldsByKey = new Map(customFields.map(item => [item.key, item]));
      const customFieldKeys = [...customFields.map(item => item.key)];
      for (const key of Object.keys(value.customFields || {})) {
        if (!customFieldsByKey.has(key)) customFieldKeys.push(key);
      }
      // Keep the entire panel mounted, including while hidden: collection and
      // fail-closed completeness validation read these exact draft controls.
      const panel = element('div', { id: panelId, className: 'settings-rule-details' }, [
        element('fieldset', { className: 'settings-rule-section' }, [
          element('legend', { text: 'Message behavior' }),
          field('Message behavior', select([
            { value: 'none', label: 'No message' },
            { value: 'message', label: 'Use message' },
            { value: 'ebookMessage', label: 'eBook message' },
            { value: 'eaudiobookMessage', label: 'eAudiobook message' }
          ], value.messageBehavior, { 'data-rule-property': 'messageBehavior', 'data-domain-editable': 'true', 'aria-label': `Message behavior for ${formatContext}` })),
          field('Message', element('textarea', { rows: '3', 'data-rule-property': 'message', 'data-domain-editable': 'true', 'aria-label': `Message for ${formatContext}` }, [value.message || '']))
        ]),
        element('fieldset', { className: 'settings-rule-section' }, [element('legend', { text: 'Field requirements' }),
          element('div', { className: 'settings-rule-fields' }, ['title', 'author', 'identifier', 'publication'].map(name => fieldPair(name, value[name], formatContext)))
        ]),
        element('fieldset', { className: 'settings-rule-section' }, [
          element('legend', { text: 'Custom field rules' }),
          element('div', { className: 'settings-rule-custom-fields' }, customFieldKeys.map(key => {
            const fieldValue = customFieldsByKey.get(key);
            const fieldLabel = fieldValue?.label || `Retired field (${key})`;
            const current = value.customFields[key] || { mode: 'hidden', labelOverride: null };
            return element('div', { className: 'settings-custom-rule-row', 'data-custom-rule-key': key }, [
              element('strong', { className: 'settings-custom-rule-name', text: fieldLabel }),
              element('span', { className: 'settings-custom-rule-status' }),
              field('Requirement', select([
                { value: 'hidden', label: 'Hidden' },
                { value: 'optional', label: 'Optional' },
                { value: 'required', label: 'Required' }
              ], current.mode, { 'data-custom-rule-property': 'mode', 'data-domain-editable': 'true',
                'aria-label': `${fieldLabel} mode for ${formatContext}` })),
              field('Patron-facing label override', element('input', { type: 'text', value: current.labelOverride || '', placeholder: 'Label override',
                'data-custom-rule-property': 'labelOverride', 'data-domain-editable': 'true',
                'aria-label': `${fieldLabel} label override for ${formatContext}` }))
            ]);
          }))
        ])
      ]);
      const row = element('div', { className: 'settings-editor-row settings-rule-row', 'data-domain-row': 'true', 'data-rule-code': value.code }, [
        element('div', { className: 'settings-rule-heading' }, [toggle,
          actions(index, values.length, (from, offset) => reorder('rules', from, offset), from => remove('rules', from), formatContext)]), panel
      ]);
      const updateExpansion = () => {
        const expanded = expandedRuleCodes.has(row.dataset.ruleCode);
        panel.hidden = !expanded;
        toggle.setAttribute('aria-expanded', String(expanded));
        refreshRuleFormatName(row);
      };
      updateExpansion();
      listen(toggle, 'click', () => {
        const code = row.dataset.ruleCode;
        if (expandedRuleCodes.has(code)) expandedRuleCodes.delete(code);
        else expandedRuleCodes.add(code);
        updateExpansion();
      });
      const behavior = row.querySelector('[data-rule-property="messageBehavior"]');
      const updateMessage = () => {
        const control = row.querySelector('[data-rule-property="message"]');
        const usesMessage = behavior.value !== 'none';
        (control.closest('.settings-inheritance-field') || control.closest('label')).hidden = !usesMessage;
        control.disabled = !usesMessage;
      };
      updateMessage();
      listen(behavior, 'change', updateMessage);
      dom.rules.append(row);
      attachRuleInheritance(row, value);
    }
    refreshRuleDefinitions();
    for (const row of dom.formats.querySelectorAll('[data-format-code]')) refreshFormatStatus(row, row.dataset.formatCode);
    updateRuleAvailability();
    if (focusedRow) {
      const rows = [...dom.rules.querySelectorAll('[data-rule-code]')];
      const retainedRow = rows.find(item => item.dataset.ruleCode === focusedCode);
      const row = retainedRow || rows[Math.min(focusedIndex, rows.length - 1)];
      const action = [...(retainedRow?.querySelectorAll('.settings-row-actions button') || [])]
        .find(button => !button.disabled && (focusedAction
          ? button.dataset.rowAction === focusedAction : button.getAttribute('aria-label') === focusedName));
      const target = action || row?.querySelector('[data-rule-toggle]') || addButtons.rules;
      if (target?.disabled) dom.ruleAvailability?.focus();
      else target?.focus();
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
    const formatLabels = formats.map(item => item.label || 'Unresolved material format');
    const formatOptions = formats.map((item, index) => ({
      value: item.id,
      label: formatLabels.filter(label => label === formatLabels[index]).length > 1
        ? formatLabels[index] + ', format ' + (index + 1) : formatLabels[index]
    }));
    for (const [index, value] of values.entries()) {
      const formatId = stringId(property(value, 'materialFormatId') ?? property(value, 'formatId'));
      const staffId = stringId(property(value, 'staffUserId') ?? property(value, 'staffId'));
      const staffOptions = byDisplayLabel(staff.map(item => ({ value: stringId(property(item, 'id')), label: property(item, 'label') || property(item, 'displayName') || `Staff ${property(item, 'id')}` })).filter(item => item.value), item => item.label, item => item.value);
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
    if (state.system) return systemRows.map(normalizeTemplate).sort(compareTemplateOrder);

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
        inheritedSubject: system.subject,
        inheritedBody: system.body,
        inheritedDisplayName: system.displayName,
        inheritedEnabled: system.enabled,
        subjectBaseline: override?.subject?.trim() ? override.subject : system.subject,
        bodyBaseline: override?.body?.trim() ? override.body : system.body,
        displayNameBaseline: override?.displayName ?? system.displayName,
        enabledBaseline: system.enabled && (!override || override.enabled)
      };
    });
    const custom = libraryRows
      .filter(item => Boolean(property(item, 'isCustom')))
      .map(normalizeTemplate);
    // Sending choices order System sources and Library custom records together.
    // A Library content override does not own its source's order.
    return lineage.concat(custom).sort(compareTemplateOrder);
  }

  function compareTemplateIds(left, right) {
    const leftId = left.sourceTemplateId || left.id;
    const rightId = right.sourceTemplateId || right.id;
    if (!leftId || !rightId) return leftId ? -1 : rightId ? 1 : 0;
    const first = BigInt(leftId), second = BigInt(rightId);
    return first < second ? -1 : first > second ? 1 : 0;
  }

  function compareTemplateOrder(left, right) {
    return left.sortOrder - right.sortOrder || compareTemplateIds(left, right);
  }

  function movedTemplateSortOrder(values, index, offset) {
    const next = index + offset;
    if (next < 0 || next >= values.length) return null;
    const ordered = [...values];
    [ordered[index], ordered[next]] = [ordered[next], ordered[index]];
    const moved = ordered[next], before = ordered[next - 1], after = ordered[next + 1];
    const minimum = before ? before.sortOrder + (compareTemplateIds(moved, before) < 0 ? 1 : 0) : -2147483648;
    const maximum = after ? after.sortOrder - (compareTemplateIds(moved, after) > 0 ? 1 : 0) : 2147483647;
    return minimum <= maximum ? Math.max(minimum, Math.min(moved.sortOrder, maximum)) : null;
  }

  function orderSystemTemplateBlock(values, first, last) {
    // Tied System rows may need more than one changed rank. Expand only as
    // far as necessary; unrelated records outside this block remain untouched.
    for (let size = last - first + 1; size <= values.length; size++) {
      for (let start = Math.max(0, last - size + 1); start <= Math.min(first, values.length - size); start++) {
        const end = start + size - 1;
        const minimum = start ? values[start - 1].sortOrder +
          (compareTemplateIds(values[start], values[start - 1]) < 0 ? 1 : 0) : -2147483648;
        const maximum = end < values.length - 1 ? values[end + 1].sortOrder -
          (compareTemplateIds(values[end], values[end + 1]) > 0 ? 1 : 0) : 2147483647;
        if (maximum - minimum < size - 1) continue;
        const base = Math.max(minimum, Math.min(values[start].sortOrder, maximum - size + 1));
        for (let offset = 0; offset < size; offset++) values[start + offset].sortOrder = base + offset;
        return;
      }
    }
  }

  function renderTemplates(values) {
    if (!dom.templates) return;
    const existing = new Map([...dom.templates.querySelectorAll('[data-domain-row]')]
      .map(row => [row.dataset.templateUiId, row]));
    const focused = dom.templates.contains(document.activeElement) ? document.activeElement : null;
    dom.templates.querySelector('.settings-empty')?.remove();
    if (values.length === 0) dom.templates.append(element('p', { className: 'settings-empty', text: 'No named templates configured.' }));
    for (const [index, value] of values.entries()) {
      const uiId = value.uiId || `template-row-${++nextTemplateRowId}`;
      if (existing.has(uiId)) {
        const row = existing.get(uiId);
        existing.delete(uiId);
        row.dataset.templateSortOrder = String(value.sortOrder ?? 0);
        updateTemplateRowActions(row, index, values);
        dom.templates.append(row);
        continue;
      }
      const lineage = !value.isCustom && !state.system;
      const row = element('div', {
        className: 'settings-editor-row settings-template-row',
        'data-domain-row': 'true',
        'data-template-ui-id': uiId,
        'data-template-sort-order': value.sortOrder ?? 0,
        'data-template-baseline-sort-order': value.sortOrder ?? 0,
        'data-template-id': stringId(value.id) || '',
        'data-template-key': value.templateKey,
        'data-template-version': stringId(value.version) || '',
        'data-template-source-id': stringId(value.sourceTemplateId) || '',
        'data-template-kind': value.isCustom ? 'custom' : lineage ? 'lineage' : 'system',
        'data-template-had-override': value.hadOverride ? 'true' : 'false',
        'data-template-baseline-subject': value.subjectBaseline || '',
        'data-template-baseline-body': value.bodyBaseline || '',
        'data-template-baseline-display-name': value.displayNameBaseline || '',
        'data-template-baseline-enabled': value.enabledBaseline ? 'true' : 'false',
        'data-template-inherited-subject': value.inheritedSubject || '',
        'data-template-inherited-body': value.inheritedBody || ''
      }, [
        field('Display name', element('input', {
          type: 'text',
          className: 'template-display-name',
          value: value.displayName || '',
          'data-domain-editable': 'true'
        })),
        field('Subject', element('input', {
          type: 'text',
          className: 'template-subject',
          'data-template-field': 'subject',
          value: value.subject || '',
          'data-domain-editable': 'true'
        })),
        field('Body', element('textarea', {
          rows: '5',
          className: 'template-body',
          'data-template-field': 'body',
          'data-domain-editable': 'true'
        }, [value.body || ''])),
        element('label', { className: 'settings-domain-check' }, [element('input', {
          type: 'checkbox',
          className: 'template-enabled',
          checked: value.enabled !== false,
          'data-domain-editable': 'true'
        }), element('span', { text: 'Enabled' })]),
        element('p', { className: 'field-help settings-template-order-help', hidden: 'true' }),
        actions(index, values.length, (_, offset) => reorder('templates', templateRowIndex(row), offset), value.isCustom ? () => {
          const currentIndex = templateRowIndex(row);
          if (canRemoveTemplate(readTemplates()[currentIndex])) remove('templates', currentIndex);
        } : !value.id ? () => remove('templates', templateRowIndex(row)) : null)
      ]);
      row.dataset.templateBaselineSubject = row.querySelector('.template-subject').value;
      row.dataset.templateBaselineBody = row.querySelector('.template-body').value;
      row.dataset.templateBaselineDisplayName = clean(row.querySelector('.template-display-name').value) || '';
      row.dataset.templateBaselineEnabled = String(row.querySelector('.template-enabled').checked);
      dom.templates.append(row);
      if (lineage) attachTemplateInheritance(row, value);
      updateTemplateRowActions(row, index, values);
    }
    for (const row of existing.values()) row.remove();
    onTemplatesRendered();
    if (focused?.isConnected) {
      const target = focused.disabled ? focused.closest('[data-domain-row]')?.querySelector('[data-template-toggle]') : focused;
      target?.focus({ preventScroll: true });
    }
  }

  function attachTemplateInheritance(row, value) {
    const id = row.dataset.templateUiId;
    const fields = ['displayName', 'subject', 'body', 'enabled'];
    const selectors = ['.template-display-name', '.template-subject', '.template-body', '.template-enabled'];
    const intent = templateIntents.get(id);
    const inheritedValue = name => value[`inherited${name[0].toUpperCase()}${name.slice(1)}`] ?? '';
    const inheritedDisplay = name => {
      if (name === 'enabled') return Boolean(inheritedValue(name));
      const control = row.querySelector(selectors[fields.indexOf(name)]).cloneNode(false);
      control.value = inheritedValue(name);
      return control.value;
    };
    const preview = name => {
      const control = row.querySelector(selectors[fields.indexOf(name)]);
      if (name === 'enabled') control.checked = Boolean(inheritedValue(name));
      else control.value = inheritedValue(name);
      displayed.set(control, control.type === 'checkbox' ? control.checked : control.value);
    };
    for (const [index, name] of fields.entries()) {
      const control = row.querySelector(selectors[index]);
      const local = intent?.fields?.[name];
      const comparisonOpen = row.querySelector(`[data-template-status="${name}"] .settings-system-comparison`)?.open;
      row.querySelector(`[data-template-status="${name}"]`)?.remove();
      const raw = value[`raw${name[0].toUpperCase()}${name.slice(1)}`];
      const owned = intent?.reset ? false : local ? local === 'pin' : name === 'enabled' ? value.hadOverride : raw !== null && raw !== undefined;
      const status = inheritanceStatus({ owned,
        value: name === 'enabled' ? control.checked : control.value,
        systemValue: inheritedDisplay(name), comparisonOpen,
        pending: Boolean(intent?.reset || local), label: `${value.displayName} ${name}`, onReset: () => {
          const old = templateIntents.get(id);
          templateIntents.set(id, { reset: false, fields: { ...old?.fields, [name]: 'reset' } });
          preview(name); attachTemplateInheritance(row, value); onTemplatesRendered(); onChange({ target: control, resetLabel: `${value.displayName} ${name}` }); control.focus();
        } });
      status.dataset.templateStatus = name;
      const field = control.closest('.settings-template-field');
      if (field) field.append(status);
      else control.closest('label')?.after(status);
      if (!displayed.has(control)) watchControl(control, () => {
        const old = templateIntents.get(id);
        const next = old?.reset ? Object.fromEntries(fields.map(key => [key, 'reset'])) : { ...old?.fields };
        next[name] = 'pin';
        templateIntents.set(id, { reset: false, fields: next });
        attachTemplateInheritance(row, value);
      });
    }
    row.querySelector('[data-template-item-status]')?.remove();
    const current = fields.map((name, index) => {
      const control = row.querySelector(selectors[index]); return name === 'enabled' ? control.checked : control.value;
    });
    const status = inheritanceStatus({ owned: intent?.reset ? false : value.hadOverride || Object.values(intent?.fields || {}).includes('pin'),
      value: current, systemValue: fields.map(inheritedDisplay), pending: Boolean(intent), label: `${value.displayName} template`, showComparison: false,
      onReset: () => {
        templateIntents.set(id, { reset: true, fields: {} });
        fields.forEach(preview); attachTemplateInheritance(row, value); onTemplatesRendered(); onChange({ resetLabel: `${value.displayName} template` });
        row.querySelector('.template-display-name')?.focus();
      } });
    status.dataset.templateItemStatus = 'true';
    (row.querySelector('.settings-template-admin') || row).append(status);
  }

  function templateRowIndex(row) {
    return [...dom.templates.querySelectorAll('[data-domain-row]')].indexOf(row);
  }

  function updateTemplateRowActions(row, index, values) {
    const inherited = row.dataset.templateKind === 'lineage';
    const buttons = row.querySelectorAll('.settings-row-actions button');
    const blockedUp = !state.system && !inherited && index > 0 && movedTemplateSortOrder(values, index, -1) === null;
    const blockedDown = !state.system && !inherited && index < values.length - 1 && movedTemplateSortOrder(values, index, 1) === null;
    buttons[0].disabled = inherited || index === 0 || blockedUp;
    buttons[1].disabled = inherited || index === values.length - 1 || blockedDown;
    const help = row.querySelector('.settings-template-order-help');
    help.hidden = !inherited && !blockedUp && !blockedDown;
    help.textContent = inherited ? 'Template order is managed at System level.'
      : 'Some positions follow inherited template order. Change that order at System level.';
  }

  function readTemplates() {
    const rows = [...(dom.templates?.querySelectorAll('[data-domain-row]') || [])];
    return rows.map(row => {
      const lineage = row.dataset.templateKind === 'lineage';
      const currentSubject = row.querySelector('.template-subject')?.value ?? null;
      const currentBody = row.querySelector('.template-body')?.value ?? null;
      const currentDisplayName = clean(row.querySelector('.template-display-name')?.value);
      const currentEnabled = Boolean(row.querySelector('.template-enabled')?.checked);
      const baselineSubject = row.dataset.templateBaselineSubject;
      const baselineBody = row.dataset.templateBaselineBody;
      const baselineDisplayName = clean(row.dataset.templateBaselineDisplayName);
      const baselineEnabled = row.dataset.templateBaselineEnabled === 'true';
      const intent = templateIntents.get(row.dataset.templateUiId);
      const persisted = Boolean(row.dataset.templateId);
      const sortOrder = numeric(row.dataset.templateSortOrder, 0);
      return {
        uiId: row.dataset.templateUiId,
        id: stringId(row.dataset.templateId),
        version: stringId(row.dataset.templateVersion),
        sourceTemplateId: stringId(row.dataset.templateSourceId),
        templateKey: clean(row.dataset.templateKey),
        displayName: currentDisplayName,
        subject: currentSubject,
        body: currentBody,
        enabled: currentEnabled,
        sortOrder,
        sortOrderBaseline: numeric(row.dataset.templateBaselineSortOrder, 0),
        sortOrderChanged: !persisted || sortOrder !== numeric(row.dataset.templateBaselineSortOrder, 0),
        isCustom: row.dataset.templateKind === 'custom',
        overridden: lineage ? !intent?.reset && (row.dataset.templateHadOverride === 'true' || Object.values(intent?.fields || {}).includes('pin')) : !lineage,
        hadOverride: row.dataset.templateHadOverride === 'true',
        reset: lineage && Boolean(intent?.reset),
        fieldIntents: intent?.fields || {},
        subjectBaseline: baselineSubject,
        bodyBaseline: baselineBody,
        displayNameBaseline: baselineDisplayName,
        enabledBaseline: baselineEnabled,
        subjectChanged: Boolean(intent?.fields?.subject) || !persisted || currentSubject !== baselineSubject,
        bodyChanged: Boolean(intent?.fields?.body) || !persisted || currentBody !== baselineBody,
        displayNameChanged: Boolean(intent?.fields?.displayName) || !persisted || currentDisplayName !== baselineDisplayName,
        enabledChanged: Boolean(intent?.fields?.enabled) || !persisted || currentEnabled !== baselineEnabled
      };
    });
  }

  function readSnapshot() {
    return JSON.stringify({
      publication: { useSystem: inheritedGroup('publication'), intent: groupIntents.get('publication'), values: readPublication() },
      creators: { useSystem: inheritedGroup('creators'), intent: groupIntents.get('creators'), values: readSetRows(dom.creators, 'creator') },
      codes: { useSystem: inheritedGroup('codes'), intent: groupIntents.get('codes'),
        values: readSelectedCodes().sort((left, right) => Number(left) - Number(right)) },
      providers: { values: readProviders(), intents: [...providerIntents] },
      formats: { values: readFormats(), intents: [...formatIntents] },
      fields: readFields(),
      rules: readRules(),
      claims: readClaims(),
      templates: { values: readTemplates(), intents: [...templateIntents] }
    });
  }

  function draftDestinations() {
    if (disposed || !state.baseline) return [];
    const current = JSON.parse(readSnapshot()), baseline = JSON.parse(state.baseline);
    const definitions = [
      ['publication', 'patron', 'Publication options'], ['creators', 'workflow', 'Common creator list'],
      ['codes', 'workflow', 'Allowed patron codes'], ['providers', 'workflow', 'External search providers'],
      ['formats', 'patron', 'Material formats'], ['fields', 'patron', 'Custom fields'],
      ['rules', 'patron', 'Format rules'], ['claims', 'patron', 'Auto-claim rules'],
      ['templates', 'templates', 'Additional templates']
    ];
    return definitions.flatMap(([name, panel, label]) => {
      // Equal-display resets still own a draft, including built-in rule
      // columns sharing the MaterialFormatOverride row with Material formats.
      const ruleIntent = name === 'rules' && [...formatIntents.entries()].find(([, intent]) =>
        intent.reset || Object.keys(intent.fields).some(key => !['label', 'isEnabled', 'sortOrder'].includes(key)));
      if (JSON.stringify(current[name]) === JSON.stringify(baseline[name]) && !ruleIntent) return [];
      const values = Array.isArray(current[name]) ? current[name] : current[name]?.values;
      const original = Array.isArray(baseline[name]) ? baseline[name] : baseline[name]?.values;
      let index = Array.isArray(values) ? values.findIndex((value, index) => JSON.stringify(value) !== JSON.stringify(original?.[index])) : -1;
      const intents = current[name]?.intents;
      if (index < 0 && intents?.length) {
        const id = intents[0][0];
        index = values.findIndex(value => [value.id, value.code, value.uiId].includes(id));
      }
      if (index < 0 && ruleIntent) index = values.findIndex(value => value.code === ruleIntent[0]);
      const row = dom[name]?.querySelectorAll(':scope > [data-domain-row]')[Math.max(0, index)];
      return [{ key: name, panel, label, reveal() {
        if (!dom[name]?.isConnected || !root.contains(dom[name])) return null;
        // Use the existing disclosures; opening them changes presentation only.
        const toggle = row?.querySelector('[data-rule-toggle], [data-template-toggle]');
        if (toggle?.getAttribute('aria-expanded') === 'false') toggle.click();
        return toggle || (name === 'codes' ? dom.codeSearch : row?.querySelector('input, select, textarea')) ||
          addButtons[name] || dom[name].closest('.settings-fieldset');
      } }];
    });
  }

  function reorder(name, index, offset) {
    if (['fields', 'rules'].includes(name) && !canRedrawRules()) return;
    const rules = name === 'fields' ? readRules() : null;
    const values = readDomain(name);
    const next = index + offset;
    if (next < 0 || next >= values.length) return;
    if (name === 'publication') markGroup(name);
    const sortOrder = name === 'templates' ? movedTemplateSortOrder(values, index, offset) : null;
    if (name === 'templates' && !state.system && (!values[index].isCustom || sortOrder === null)) return;
    [values[index], values[next]] = [values[next], values[index]];
    if (name === 'templates') {
      if (sortOrder !== null) values[next].sortOrder = sortOrder;
      else orderSystemTemplateBlock(values, Math.min(index, next), Math.max(index, next));
      if (values.length === state.templateOrderBaseline.length &&
          values.every((value, position) => value.uiId === state.templateOrderBaseline[position])) {
        for (const value of values) value.sortOrder = value.sortOrderBaseline;
      }
    }
    if (name === 'formats' || name === 'providers') {
      values.forEach((value, order) => { value.sortOrder = (order + 1) * 10; });
      if (!state.system && name === 'formats') {
        for (const value of values) pinItem(formatIntents, value.code, 'sortOrder');
      }
    }
    const focusedAction = document.activeElement?.getAttribute('aria-label');
    const publicationAction = name === 'publication' ? document.activeElement?.dataset.rowAction : null;
    renderDomain(name, values);
    if (name === 'publication') focusPublicationRow(next, publicationAction);
    if (name === 'fields') {
      renderRules(rules);
      focusFieldRow(next, focusedAction);
    }
    onChange();
  }

  function remove(name, index) {
    if (['fields', 'rules'].includes(name) && !canRedrawRules()) return;
    const values = readDomain(name);
    if (name === 'publication') markGroup(name);
    const rules = name === 'fields' ? readRules() : null;
    const removedFieldKey = name === 'fields' ? values[index]?.key : null;
    const [removed] = values.splice(index, 1);
    if (name === 'rules' && removed) {
      state.ruleRosterCodes = state.ruleRosterCodes.filter(code => code !== removed.code);
    }
    renderDomain(name, values);
    if (name === 'publication') focusPublicationRow(Math.min(index, values.length - 1));
    if (name === 'templates') {
      const rows = dom.templates.querySelectorAll('[data-domain-row]');
      (rows[Math.min(index, rows.length - 1)]?.querySelector('[data-template-toggle]') || addButtons.templates)?.focus();
    }
    if (name === 'fields') {
      for (const rule of rules || []) delete rule.customFields[removedFieldKey];
      state.models.rules = clone(rules);
      renderRules(rules);
      focusFieldRow(Math.min(index, values.length - 1));
    }
    onChange();
  }

  function readDomain(name) {
    return {
      publication: readPublication,
      creators: () => readSetRows(dom.creators, 'creator'),
      codes: readSelectedCodes,
      providers: readProviders,
      formats: readFormats,
      fields: readFields,
      rules: readRules,
      claims: readClaims,
      templates: readTemplates
    }[name]();
  }

  function focusFieldRow(index, actionName) {
    const row = dom.fields.querySelectorAll('[data-domain-row]')[index];
    const action = [...(row?.querySelectorAll('.settings-row-actions button') || [])]
      .find(button => !button.disabled && button.getAttribute('aria-label') === actionName);
    (action || row?.querySelector('input') || addButtons.fields)?.focus();
  }

  function focusPublicationRow(index, action) {
    const row = dom.publication.querySelectorAll('[data-domain-row]')[index];
    const target = action ? row?.querySelector(`[data-row-action="${action}"]:not(:disabled)`) : null;
    (target || row?.querySelector('[data-publication-field="label"]') || addButtons.publication)?.focus();
  }

  function renderDomain(name, values) {
    if (name === 'publication') renderPublication(values);
    else if (name === 'creators') renderSetRows(dom.creators, values, 'creator');
    else if (name === 'codes') renderCodes(values);
    else if (name === 'providers') renderProviders(values);
    else if (name === 'formats') renderFormats(values);
    else if (name === 'fields') renderFields(values);
    else if (name === 'rules') renderRules(values);
    else if (name === 'claims') renderClaims(values);
    else if (name === 'templates') renderTemplates(values);
  }

  function canRedrawRules(fieldKeys) {
    try {
      // Validate before reading/defaulting and rebuilding controls. A redraw
      // cannot turn an incomplete draft into an apparently valid replacement.
      validateRenderedRules(fieldKeys);
      return true;
    } catch (error) {
      state.validationError = error instanceof Error ? error.message : 'The format-rule editor is incomplete. Reload settings before saving.';
      return false;
    }
  }

  function validateRenderedRules(fieldKeys = new Set(readFields().map(item => item.key))) {
    const modes = ['required', 'optional', 'hidden'];
    const behaviors = ['none', 'message', 'ebookMessage', 'eaudiobookMessage'];
    const hasControl = (container, selector, tag, values) => {
      const controls = container.querySelectorAll(selector);
      return controls.length === 1 && controls[0].matches(tag) &&
        (!values || values.includes(controls[0].value));
    };
    const expectedCodes = new Set(state.ruleRosterCodes);
    const renderedCodes = new Set();
    for (const row of dom.rules.querySelectorAll('[data-domain-row]')) {
      const code = clean(row.dataset.ruleCode);
      if (!code || renderedCodes.has(code) ||
          !hasControl(row, '[data-rule-property="messageBehavior"]', 'select', behaviors) ||
          !hasControl(row, '[data-rule-property="message"]', 'textarea')) {
        throw new Error('A format-rule editor is incomplete. Reload settings before saving.');
      }
      renderedCodes.add(code);
      for (const name of ['title', 'author', 'identifier', 'publication']) {
        if (!hasControl(row, `[data-format-field="${name}"]`, 'select', modes) ||
            !hasControl(row, `[data-format-label="${name}"]`, 'input[type="text"]')) {
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
            !hasControl(customRow, '[data-custom-rule-property="mode"]', 'select', modes) ||
            !hasControl(customRow, '[data-custom-rule-property="labelOverride"]', 'input[type="text"]')) {
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
    const requireControls = (row, selectors) => {
      if (selectors.some(selector => row.querySelectorAll(selector).length !== 1)) {
        throw new Error('The settings editor is incomplete. Reload settings before saving.');
      }
    };
    for (const row of dom.providers.querySelectorAll('[data-domain-row]')) {
      requireControls(row, ['input[type="text"]', 'input[type="url"]', 'input[type="checkbox"]']);
      if (!clean(row.dataset.providerKey)) throw new Error('The provider identity is unavailable. Reload settings before saving.');
    }
    for (const row of dom.publication.querySelectorAll('[data-domain-row]')) {
      requireControls(row, ['input[data-publication-field="label"]', 'input[type="checkbox"][data-publication-field="enabled"]']);
    }
    for (const row of dom.formats.querySelectorAll('[data-domain-row]')) {
      requireControls(row, ['input[data-material-field="label"]', 'input[type="checkbox"][data-material-field="enabled"]']);
      if (!clean(row.dataset.formatCode)) throw new Error('The material-format identity is unavailable. Reload settings before saving.');
    }
    for (const row of dom.fields.querySelectorAll('[data-domain-row]')) {
      requireControls(row, ['input[data-custom-field="label"]', 'select[data-custom-field="type"]',
        'input[data-custom-field="helpText"]', 'input[type="checkbox"][data-custom-field="enabled"]']);
      for (const option of row.querySelectorAll('[data-option-row]')) {
        requireControls(option, ['input[data-option-field="label"]', 'input[type="checkbox"][data-option-field="enabled"]']);
      }
    }
    if (readSetRows(dom.creators, 'creator').some(value => !value)) {
      throw new Error('Remove or complete blank creator rows before saving.');
    }
    if (readPublication().some(option => !option.id || !option.label)) {
      throw new Error('Publication options require stable IDs and labels. Delete a row to remove it.');
    }
    const templateKeys = new Set(state.system ? BUILTIN_TEMPLATE_KEYS : []);
    for (const template of state.models.templates) {
      if (template.id && (state.system || template.isCustom)) templateKeys.add(template.templateKey.toLowerCase());
    }
    for (const row of dom.templates.querySelectorAll('[data-domain-row]')) {
      requireControls(row, ['input.template-display-name', 'input.template-subject', 'textarea.template-body', 'input[type="checkbox"].template-enabled']);
      const control = row.querySelector('.template-display-name');
      const key = clean(row.dataset.templateKey)?.toLowerCase();
      if (!key) {
        const message = 'The email-template identity is unavailable. Reload settings before saving.';
        onInvalidTemplate(control, message);
        throw new Error(message);
      }
      if (row.dataset.templateId) continue;
      if (templateKeys.has(key)) {
        const message = 'The email-template identity is already used in this settings scope. Reload settings before saving.';
        onInvalidTemplate(control, message);
        throw new Error(message);
      }
      templateKeys.add(key);
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

  function bindAdd(name, factory) {
    listen(addButtons[name], 'click', () => {
      if (name === 'creators' && creatorsOptionsHidden()) return;
      if (['fields', 'rules'].includes(name) && !canRedrawRules()) return;
      const rules = name === 'fields' ? readRules() : null;
      const values = readDomain(name);
      if (name === 'rules' && eligibleRuleFormats(values).length === 0) {
        updateRuleAvailability();
        return;
      }
      const added = factory(values);
      if (!added) return;
      if (['creators', 'publication'].includes(name)) markGroup(name);
      values.push(added);
      if (name === 'rules' && !state.ruleRosterCodes.includes(added.code)) {
        state.ruleRosterCodes = [...state.ruleRosterCodes, added.code];
      }
      if (name === 'rules') expandedRuleCodes.add(added.code);
      renderDomain(name, values);
      if (name === 'creators') focusSetRow(values.length - 1);
      if (name === 'publication') focusPublicationRow(values.length - 1);
      if (name === 'formats') dom.formats.lastElementChild?.querySelector('[data-material-field="label"]')?.focus();
      if (name === 'templates') {
        const row = dom.templates.lastElementChild;
        row?.querySelector('[data-template-toggle]')?.click();
        row?.querySelector('.template-display-name')?.focus();
      }
      if (name === 'rules') dom.rules.lastElementChild?.querySelector('[data-rule-toggle]')?.focus();
      if (name === 'fields') {
        renderRules(rules);
        focusFieldRow(values.length - 1);
      }
      onChange();
    });
  }

  listen(dom.rules, 'change', event => {
    const row = event.target.closest('[data-rule-code]');
    if (row && event.target.matches('[data-format-field], [data-custom-rule-property="mode"]')) updateRuleSummary(row);
  });
  bindAdd('publication', values => ({
    id: draftKey('option_', [...state.models.publication, ...values].map(item => item.id)),
    label: 'New option', enabled: true, sortOrder: (values.length + 1) * 10
  }));
  bindAdd('creators', values => 'New creator');
  listen(dom.codeSearch, 'input', renderCodeOptions);
  listen(dom.codeSearch, 'click', renderCodeOptions);
  listen(dom.codeSearch, 'focus', () => positionCodePicker(true));
  listen(window, 'scroll', () => positionCodePicker());
  listen(window, 'resize', () => positionCodePicker(true));
  listen(window.visualViewport, 'resize', () => positionCodePicker(true));
  listen(window.visualViewport, 'scroll', () => positionCodePicker());
  // Dirty-state status and responsive footer wrapping can change the usable
  // viewport while the combobox keeps focus (including immediately after selection).
  const codeLayoutObserver = window.ResizeObserver ? new window.ResizeObserver(() => positionCodePicker(true)) : null;
  codeLayoutObserver?.observe(dom.saveBar);
  codeLayoutObserver?.observe(dom.codeSearch);
  const codeFooterObserver = new window.MutationObserver(() => positionCodePicker(true));
  codeFooterObserver.observe(dom.saveBar, { attributes: true, attributeFilter: ['class'], childList: true, subtree: true, characterData: true });
  events.signal.addEventListener('abort', () => {
    codeLayoutObserver?.disconnect();
    codeFooterObserver.disconnect();
  }, { once: true });
  listen(dom.codeSearch, 'keydown', event => {
    if (event.key === 'Escape') {
      if (!dom.codeOptions.hidden) event.stopPropagation();
      closeCodePicker();
    } else if (event.key === 'Tab') {
      closeCodePicker();
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (dom.codeSearch.disabled) return;
      event.preventDefault();
      if (dom.codeOptions.hidden) renderCodeOptions();
      const total = dom.codeOptions.querySelectorAll('[role="option"]').length;
      highlightCode(event.key === 'ArrowDown' ? state.codeHighlight + 1
        : state.codeHighlight <= 0 ? total - 1 : state.codeHighlight - 1);
    } else if (event.key === 'Enter') {
      // Enter in a picker must never submit the Settings form or arbitrary typed text.
      event.preventDefault();
      if (!dom.codeOptions.hidden) dom.codeOptions.querySelectorAll('[role="option"]')[state.codeHighlight]?.click();
    }
  });
  listen(dom.codeSearch, 'blur', closeCodePicker);
  listen(dom.codeRetry, 'click', async () => {
    if (state.codeLookupPending) return;
    const owner = state.data;
    const restoreFocus = document.activeElement === dom.codeRetry;
    state.codeLookupPending = true;
    renderCodes(readSelectedCodes());
    try { await onRetryCodes(); }
    finally {
      if (!disposed && state.data === owner) {
        state.codeLookupPending = false;
        renderCodes(readSelectedCodes());
        if (restoreFocus && dom.codeRetry.hidden &&
            [dom.codeRetry, document.body].includes(document.activeElement)) {
          dom.codeSearch.focus();
        }
      }
    }
  });
  listen(dom.codeEligibilityEnabled, 'change', updateCodeWarning);
  listen(dom.codes, 'change', updateCodeWarning);
  bindAdd('providers', values => {
    // A removed draft row still owns its persisted identity until save/reload.
    const accepted = array(property(systemConfig(state.data), 'providers'));
    const keys = new Set([...accepted, ...values].map(value => String(value.key || '').toLowerCase()));
    let index = 1;
    while (keys.has(`provider_${index}`)) index += 1;
    return { id: null, key: `provider_${index}`, label: 'New provider', urlTemplate: '', isEnabled: false, overridden: true };
  });
  bindAdd('formats', values => {
    const codes = [...state.models.formats, ...values].map(value => value.code).concat(state.ruleRosterCodes);
    return { id: null, code: draftKey('custom_', codes), ownerOrganizationId: currentOrganizationId(), label: 'New format', isEnabled: true, overridden: true };
  });
  bindAdd('fields', values => {
    const keys = [...state.models.fields, ...values].map(value => value.key);
    for (const rule of state.models.rules) {
      keys.push(...Object.keys(rule.customFields));
    }
    return { id: null, key: draftKey('field_', keys), type: 'text', label: 'New field', enabled: true, options: [] };
  });
  bindAdd('rules', values => {
    const format = eligibleRuleFormats(values)[0];
    if (!format) return null;
    return normalizeRule({ code: format.code }, format);
  });
  bindAdd('claims', values => ({ materialFormatId: readFormats()[0]?.id || '', staffUserId: '' }));
  function unusedTemplateKey(values) {
    // The DACPAC uses a case-insensitive collation for unique template keys.
    const keys = [...values, ...state.models.templates].map(value => value.templateKey);
    const prefix = state.system ? 'rejection:template_' : 'rejection:custom_template_';
    return draftKey(prefix, keys);
  }

  bindAdd('templates', values => ({
    id: null,
    templateKey: unusedTemplateKey(values),
    displayName: 'New rejection template',
    subject: '',
    body: '',
    sortOrder: Math.min(2147483647, Math.max(0, ...values.map(value => value.sortOrder ?? 0)) + 10),
    enabled: true,
    isCustom: !state.system
  }));

  function populate(data, system) {
    if (disposed) return;
    state.data = data || {};
    state.system = Boolean(system);
    groupIntents.clear(); groupOwned.clear(); providerIntents.clear(); formatIntents.clear(); templateIntents.clear();
    reservedDraftKeys.clear();
    expandedRuleCodes.clear();
    state.codeLookupPending = false;
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
    const codes = setValues('codes', state.system);
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
    groupOwned.set('publication', Boolean(libraryPublication?.exists));
    groupOwned.set('creators', Boolean(libraryCreators?.exists));
    groupOwned.set('codes', Boolean(libraryCodes?.exists));
    renderPublication(publication);
    renderSetRows(dom.creators, creators, 'creator');
    renderCodes(codes);
    renderProviders(providerValues);
    renderFormats(formats);
    renderFields(fields);
    renderRules(rules);
    renderClaims(claims);
    dom.templates?.replaceChildren();
    renderTemplates(templates);
    state.templateOrderBaseline = [...dom.templates.querySelectorAll('[data-domain-row]')].map(row => row.dataset.templateUiId);
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
      dom.codeSelection,
      dom.codeSearch,
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
      codes: readSelectedCodes(),
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
    if ([...formatIntents.values()].some(intent => Object.keys(intent.fields).some(key => !['label', 'isEnabled', 'sortOrder'].includes(key)))) {
      domainsChanged.rules = true;
    }
    const sparse = (value, intent, identity) => {
      if (intent.reset) return { ...identity, reset: true };
      const result = { ...identity, pinOverrides: true };
      for (const [key, action] of Object.entries(intent.fields)) {
        const [group, propertyName] = key.split('.');
        const currentValue = propertyName ? value[group]?.[propertyName] : value[group];
        const desired = action === 'reset' ? null : currentValue;
        if (propertyName) { result[group] ||= {}; result[group][propertyName] = desired; }
        else result[group] = desired;
      }
      return result;
    };
    const writes = {
      providers: state.system ? values.providers.map(value => {
        const original = baseline.providers?.values.find(item => item.id === value.id);
        const result = { id: value.id, key: value.key };
        for (const key of ['label', 'urlTemplate', 'isEnabled', 'sortOrder']) {
          if (!original || original[key] !== value[key]) result[key] = value[key];
        }
        return result;
      }) : values.providers.filter(value => providerIntents.has(value.id))
        .map(value => sparse(value, providerIntents.get(value.id), { id: value.id, key: value.key })),
      formats: state.system ? values.formats.map(value => {
        const original = baseline.formats?.values.find(item => item.id === value.id);
        const result = { id: value.id, code: value.code, version: value.version };
        for (const key of ['label', 'isEnabled', 'sortOrder']) if (!original || original[key] !== value[key]) result[key] = value[key];
        return result;
      }) : values.formats.flatMap(value => {
        if (value.ownerOrganizationId === currentOrganizationId()) {
          const original = baseline.formats?.values.find(item => item.id && item.id === value.id);
          if (!original) return [value];
          const result = { id: value.id, code: value.code, version: value.version, ownerOrganizationId: value.ownerOrganizationId };
          for (const key of ['label', 'isEnabled', 'sortOrder']) if (original[key] !== value[key]) result[key] = value[key];
          return [result];
        }
        const intent = formatIntents.get(value.code);
        if (!intent) return [];
        const fields = Object.fromEntries(Object.entries(intent.fields).filter(([key]) => ['label', 'isEnabled', 'sortOrder'].includes(key)));
        return intent.reset || Object.keys(fields).length ? [sparse(value, { ...intent, fields },
          { id: value.id, code: value.code, version: value.version })] : [];
      }),
      rules: values.rules.map(value => {
        const format = values.formats.find(item => item.code === value.code);
        if (state.system || format?.ownerOrganizationId === currentOrganizationId()) {
          const original = baseline.rules?.find(item => item.code === value.code);
          const result = { code: value.code };
          for (const key of ['messageBehavior', 'message', 'title', 'author', 'identifier', 'publication']) {
            if (!original || JSON.stringify(original[key]) !== JSON.stringify(value[key])) result[key] = value[key];
          }
          if (!state.system) result.customFields = value.customFields;
          return result;
        }
        const intent = formatIntents.get(value.code);
        const fields = Object.fromEntries(Object.entries(intent?.fields || {}).filter(([key]) => !['label', 'isEnabled', 'sortOrder'].includes(key)));
        return { ...sparse(value, { reset: false, fields }, { code: value.code }), customFields: value.customFields };
      })
    };
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
      writes,
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
      writes,
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
    draftDestinations,
    setCodeChoices(choices) {
      if (disposed) return;
      if (!Array.isArray(choices) || choices.some(choice => !isRecord(choice) ||
          !normalizePatronCodeId(property(choice, 'id')) || typeof property(choice, 'description') !== 'string')) {
        throw new Error('Patron-code lookup returned invalid choices. Retry the lookup.');
      }
      state.data.patronCodeChoices = choices;
      state.data.patronCodeChoicesUnavailable = false;
      renderCodes(readSelectedCodes());
    },
    refreshCodeWarning: updateCodeWarning
  };
}
