import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, normalizeLeapPatronUrlPattern, setVisible, isSuperAdminStaff } from '../api.js';
import { currentLibraryContextOrgId, currentRejectionTemplates, deletedSettingsFormats, deletedSettingsTemplates, leapBibUrlPattern, leapPatronUrlPattern, initialSettingsSnapshot, defaultPublicationOptions, emailTemplateDefaults, setInitialSettingsSnapshot, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId, currentLegacySettingsFormModel } from '../state.js';
import { normalizeExternalSearchUrlTemplate } from './utils.js';
import { collectFormatLabels, collectAvailableFormats, collectFormatOrder, collectFormatClaimRules } from '../settings-formats.js';
import { collectDuplicateStatusLabels } from './duplicate-labels.js';
import { collectSettingsPolaris, collectEnabledLibraryIds } from './polaris-fields.js';
import { collectAllowedPatronCodeIds, getPatronCodeEligibilityEnabled } from './patron-codes.js';
import { collectOptionList, collectPatronFormatRules } from '../settings-ui.js';
import { collectAdditionalFieldDefinitions } from '../settings-additional-fields.js';

export function cloneLibrarySettingsSnapshot(settings) {
  return JSON.parse(JSON.stringify(settings || {}));
}

export function rememberLastSavedLibrarySettings(settings) {
  setLastSavedLibrarySettingsSnapshot(cloneLibrarySettingsSnapshot(settings));
  setLastSavedLibrarySettingsOrgId(currentLibraryContextOrgId || 'system');
}

function sameArray(left, right) {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

function hasOwn(value, key) {
  return !!value && Object.prototype.hasOwnProperty.call(value, key);
}

function comparable(value) {
  if (value === undefined) return '__missing__';
  if (value === null) return '__null__';
  if (Array.isArray(value) || (value && typeof value === 'object')) return JSON.stringify(value);
  return String(value);
}

function sameValue(left, right) {
  return comparable(left) === comparable(right);
}

function explicitOverride(override, key) {
  return hasOwn(override, key) && override[key] !== null && override[key] !== undefined;
}

function scopedFieldShouldSave(model, section, key, value, baselineFallback) {
  const provenance = model?.provenance || {};
  const override = provenance[`${section}Override`] || {};
  const baselineSection = provenance[`system${section.charAt(0).toUpperCase()}${section.slice(1)}`] || {};
  const baseline = hasOwn(baselineSection, key) ? baselineSection[key] : baselineFallback;
  if (model?.isSystem) return baseline !== undefined && !sameValue(value, baseline);
  if (provenance.authoritative === false) return true;
  return explicitOverride(override, key) || (baseline !== undefined && !sameValue(value, baseline));
}

function scopedSetShouldSave(model, key, value) {
  if (model?.provenance?.authoritative === false) return false;
  const provenance = model?.provenance || {};
  const baseline = provenance.systemSets?.[key];
  const comparableSet = setValue => {
    if (key === 'commonCreators') {
      if (typeof setValue === 'string') {
        return setValue
          .split(/\r?\n/)
          .map(valueItem => valueItem.trim())
          .filter(Boolean)
          .join('\n');
      }
      return (Array.isArray(setValue) ? setValue : [])
        .map(item => typeof item === 'string' ? item : item?.value)
        .filter(valueItem => valueItem !== undefined && valueItem !== null)
        .map(String)
        .join('\n');
    }
    if (key === 'allowedPatronCodeIds') {
      return (Array.isArray(setValue) ? setValue : []).map(String);
    }
    return setValue;
  };
  if (model?.isSystem) {
    return !Array.isArray(baseline) || !sameValue(comparableSet(value), comparableSet(baseline));
  }

  const raw = provenance.librarySets?.[key];
  const hasOverride = raw?.exists === true && Array.isArray(raw.values) && raw.values.length > 0;
  const fallback = model?.uiText?.publicationOptions;
  const baselineValue = Array.isArray(baseline) ? baseline : (key === 'publicationOptions' ? fallback : undefined);
  return hasOverride || (baselineValue !== undefined && !sameValue(comparableSet(value), comparableSet(baselineValue)));
}

function templateChanged(template, subject, body) {
  const storedTemplate = template?.overridden === true || template?.hadOverride === true ||
    (template?.isCustom === true && template?.isNew !== true);
  const baselineSubject = storedTemplate ? (template?.loadedSubject ?? template?.subject) : (template?.subjectBaseline ?? template?.subject);
  const baselineBody = storedTemplate ? (template?.loadedBody ?? template?.body) : (template?.bodyBaseline ?? template?.body);
  const baselineName = storedTemplate
    ? (template?.loadedName ?? template?.nameBaseline ?? template?.displayNameBaseline ?? template?.displayName ?? template?.name ?? '')
    : (template?.nameBaseline ?? template?.displayNameBaseline ?? template?.displayName ?? template?.name ?? '');
  const name = template?.name ?? template?.displayName ?? '';
  return !sameValue(subject, baselineSubject) || !sameValue(body, baselineBody) || !sameValue(name, baselineName);
}

function templatePayload(template, subject, body, isSystem) {
  const key = template?.templateKey;
  if (!key) return null;
  if (template.missingBackendTemplate === true) {
    const changed = !sameValue(subject, template.subjectBaseline) || !sameValue(body, template.bodyBaseline);
    if (!changed) return null;
    if (!isSystem) {
      throw new Error(`The system has no ${key} template to inherit. Ask a system administrator to create it first.`);
    }
    return { templateKey: key, subject, body };
  }
  const overridden = template.overridden === true || template.hadOverride === true;
  const storedTemplate = isSystem || overridden ||
    (template.isCustom === true && template.isNew !== true);
  const subjectBaseline = storedTemplate ? (template.loadedSubject ?? template.subject) : (template.subjectBaseline ?? template.subject);
  const bodyBaseline = storedTemplate ? (template.loadedBody ?? template.body) : (template.bodyBaseline ?? template.body);
  const subjectChanged = !sameValue(subject, subjectBaseline);
  const bodyChanged = !sameValue(body, bodyBaseline);
  const name = template?.name ?? template?.displayName ?? '';
  const nameBaseline = storedTemplate
    ? (template.loadedName ?? template.nameBaseline ?? template.displayNameBaseline ?? template.displayName ?? template.name ?? '')
    : (template.nameBaseline ?? template.displayNameBaseline ?? template.displayName ?? template.name ?? '');
  const nameChanged = !sameValue(name, nameBaseline);
  if (isSystem) {
    if (template.isNew !== true && !subjectChanged && !bodyChanged && !nameChanged) return null;
    return {
      templateKey: key,
      ...(template.isNew === true || subjectChanged ? { subject } : {}),
      ...(template.isNew === true || bodyChanged ? { body } : {}),
      ...(nameChanged || template.isNew === true ? { displayName: name || 'Rejection template' } : {}),
      ...(template.enabled !== undefined ? { enabled: template.enabled } : {})
    };
  }

  const lineage = template.isCustom !== true;
  const changed = templateChanged(template, subject, body);
  if (!template.isNew && !changed) return null;
  if (lineage && !overridden && !changed) return null;

  const result = {
    templateKey: key,
    ...(lineage && template.sourceTemplateId ? { sourceTemplateId: String(template.sourceTemplateId) } : {}),
    ...(template.isCustom === true ? { isCustom: true } : {})
  };
  if (template.isNew) {
    result.displayName = name || 'Rejection template';
    result.subject = subject;
    result.body = body;
    if (template.enabled !== undefined) result.enabled = template.enabled;
    return result;
  }
  const baselineSubject = storedTemplate ? (template.loadedSubject ?? template.subject) : (template.subjectBaseline ?? template.subject);
  const baselineBody = storedTemplate ? (template.loadedBody ?? template.body) : (template.bodyBaseline ?? template.body);
  const baselineName = storedTemplate
    ? (template.loadedName ?? template.nameBaseline ?? template.displayNameBaseline ?? template.displayName ?? template.name ?? '')
    : (template.nameBaseline ?? template.displayNameBaseline ?? template.displayName ?? template.name ?? '');
  if (!sameValue(subject, baselineSubject)) result.subject = subject;
  if (!sameValue(body, baselineBody)) result.body = body;
  if (!sameValue(name, baselineName)) result.displayName = name;
  if (template.enabled !== undefined && template.isCustom === true) result.enabled = template.enabled;
  return result;
}

const PATRON_TEXT_FIELDS = [
  ['pageTitle', 'ui-patron-page-title'],
  ['barcodeLabel', 'ui-barcode-label'],
  ['pinLabel', 'ui-pin-label'],
  ['loginPrompt', 'ui-login-prompt'],
  ['loginNote', 'ui-login-note'],
  ['suggestionFormNote', 'ui-suggestion-note'],
  ['noEmailMessage', 'ui-no-email-msg'],
  ['successTitle', 'ui-success-title'],
  ['successMessage', 'ui-success-msg'],
  ['alreadySubmittedMessage', 'ui-already-submitted-msg'],
  ['ebookMessage', 'ui-ebook-msg'],
  ['eaudiobookMessage', 'ui-eaudiobook-msg']
];

const DUPLICATE_LABEL_FIELDS = {
  suggestion: 'suggestionStatusLabel',
  outstanding_purchase: 'outstandingPurchaseStatusLabel',
  pending_hold: 'pendingHoldStatusLabel',
  hold_placed: 'holdPlacedStatusLabel',
  closed: 'closedStatusLabel',
  rejected: 'rejectedStatusLabel',
  hold_completed: 'holdCompletedStatusLabel',
  hold_not_picked_up: 'holdNotPickedUpStatusLabel',
  manual: 'manualStatusLabel',
  silent: 'silentStatusLabel'
};

const WORKFLOW_TEXT_FIELDS = [
  ['suggestionLimitMessage', 'suggestion-limit-msg'],
  ['commonAuthorsLabel', 'wf-common-authors-label'],
  ['commonAuthorsHelp', 'wf-common-authors-help'],
  ['commonAuthorsMessage', 'wf-common-authors-message'],
  ['patronCodeEligibilityMessage', 'patron-code-eligibility-message']
];

const WORKFLOW_BOOL_FIELDS = [
  ['outstandingTimeoutEnabled', 'outstanding-timeout-enabled'],
  ['outstandingTimeoutSendEmail', 'outstanding-timeout-send-email'],
  ['holdPickupTimeoutEnabled', 'hold-pickup-timeout-enabled'],
  ['pendingHoldTimeoutEnabled', 'pending-hold-timeout-enabled'],
  ['additionalCopyTimeoutEnabled', 'additional-copy-timeout-enabled'],
  ['commonAuthorsEnabled', 'wf-common-authors-enabled'],
  ['autoPromote', 'polaris-auto-promote'],
  ['allowPatronAutoholdOptOut', 'allow-patron-autohold-opt-out'],
  ['allowAnyRegisteredCardLogin', 'allow-any-registered-card-login'],
  ['patronCodeEligibilityEnabled', null]
];

const WORKFLOW_INT_FIELDS = [
  ['suggestionLimit', 'suggestion-limit'],
  ['outstandingTimeoutDays', 'outstanding-timeout-days'],
  ['holdPickupTimeoutDays', 'hold-pickup-timeout-days'],
  ['pendingHoldTimeoutDays', 'pending-hold-timeout-days'],
  ['additionalCopyTimeoutDays', 'additional-copy-timeout-days']
];

function collectTemplatePayload(model, isSystem) {
  const fields = [
    ['suggestion_submitted', 'email-submit-subject', 'email-submit-body'],
    ['purchase_approved', 'email-purchase-approved-subject', 'email-purchase-approved-body'],
    ['already_owned', 'email-owned-subject', 'email-owned-body'],
    ['rejected', 'email-rejected-subject', 'email-rejected-body'],
    ['hold_placed', 'email-hold-subject', 'email-hold-body']
  ];
  const records = Array.isArray(model?.templates) ? model.templates : [];
  const templates = [];
  fields.forEach(([key, subjectId, bodyId]) => {
    const record = records.find(item => item.templateKey === key && item.isCustom !== true);
    const subject = getFieldValue(subjectId);
    const body = getFieldValue(bodyId);
    const defaults = emailTemplateDefaults[key] || {};
    const value = templatePayload(record || {
      templateKey: key,
      subject: defaults.subject ?? subject,
      body: defaults.body ?? body,
      subjectBaseline: defaults.subject ?? subject,
      bodyBaseline: defaults.body ?? body,
      missingBackendTemplate: true
    }, subject, body, isSystem);
    if (value) templates.push([key, value]);
  });

  const rejectionTemplates = [];
  currentRejectionTemplates.forEach(template => {
    const value = templatePayload(template, String(template.subject ?? ''), String(template.body ?? ''), isSystem);
    if (value) rejectionTemplates.push(value);
  });
  deletedSettingsTemplates.forEach(template => rejectionTemplates.push({
    templateKey: template.templateKey,
    ...(template.sourceTemplateId ? { sourceTemplateId: String(template.sourceTemplateId) } : {}),
    ...(template.isCustom ? { isCustom: true } : {}),
    reset: true
  }));

  const result = {};
  templates.forEach(([key, value]) => { result[key] = value; });
  if (rejectionTemplates.length > 0) result.rejection_templates = rejectionTemplates;
  return result;
}

export function buildEmailSettingsPayload({ includeTemplates = true, useSmtpFields = false, allowIncomplete = false } = {}) {
  const model = currentLegacySettingsFormModel;
  if (!allowIncomplete && model && !model.isSystem && model.provenance?.authoritative === false) {
    throw new Error('Library settings could not be loaded completely. Reload settings before saving.');
  }
  const isSystem = model?.isSystem || (isSuperAdminStaff() && currentLibraryContextOrgId === 'system');
  const fromAddress = getFieldValue(useSmtpFields ? 'smtp-from' : 'email-from-address');
  const fromName = getFieldValue(useSmtpFields ? 'smtp-from-name' : 'email-from-name');
  const result = {
    postmarkToken: getFieldValue('postmark-token').trim(),
    clearPostmarkToken: getFieldChecked('postmark-clear-token')
  };
  if (scopedFieldShouldSave(model, 'email', 'fromAddress', fromAddress, model?.provenance?.systemEmail?.fromAddress)) {
    result.fromAddress = fromAddress;
  }
  if (scopedFieldShouldSave(model, 'email', 'fromName', fromName, model?.provenance?.systemEmail?.fromName)) {
    result.fromName = fromName;
  }
  if (includeTemplates && model?.templateStateTrusted) {
    Object.assign(result, collectTemplatePayload(model, isSystem));
  }
  return result;
}

export function collectExternalSearchProviders(validate = false) {
  const model = currentLegacySettingsFormModel;
  if (!model?.providerStateTrusted) return undefined;

  return model.providers.map(provider => {
    const match = /^external_search_([1-4])$/.exec(provider.key || '');
    if (!match) return null;
    const index = match[1];
    const label = getFieldValue(`wf-external-search-${index}-label`).trim();
    const urlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue(`wf-external-search-${index}-url-template`).trim());
    const isEnabled = getFieldChecked(`wf-external-search-${index}-enabled`);
    if (validate) {
      if (!label) throw new Error(`External search provider ${index} requires a label.`);
      if (!urlTemplate) throw new Error(`External search provider ${index} requires a URL template.`);
      setFieldValue(`wf-external-search-${index}-url-template`, urlTemplate);
    }
    const changed = isEnabled !== !!provider.isEnabled || label !== String(provider.label || '') ||
      urlTemplate !== String(provider.urlTemplate || '');
    return {
      id: provider.id,
      key: provider.key,
      isEnabled,
      label,
      urlTemplate,
      sortOrder: provider.sortOrder,
      ...(!changed && provider.overridden !== undefined ? { overridden: !!provider.overridden } : {})
    };
  }).filter(Boolean);
}

export function collectMaterialFormats() {
  const model = currentLegacySettingsFormModel;
  if (!model?.formatStateTrusted) return undefined;
  const labels = collectFormatLabels();
  const order = collectFormatOrder();
  const available = new Set(collectAvailableFormats());
  const rules = collectPatronFormatRules();
  const existingByCode = new Map(model.formats.map(format => [format.code, format]));
  const pendingDeleteIds = new Set(deletedSettingsFormats.map(format => String(format.id)));
  const baselineOrder = model.formats.map(format => format.code);
  const orderChanged = !sameArray(order, baselineOrder);

  function fieldSignature(format, name) {
    const nested = format?.[name] || {};
    return [nested.mode ?? format?.[`${name}Mode`], nested.label ?? format?.[`${name}Label`]];
  }

  function customFieldSignature(format) {
    return Object.fromEntries(Object.entries(format?.customFields || {}).map(([key, value]) => [key, {
      mode: value?.mode || 'hidden',
      labelOverride: value?.labelOverride ?? value?.label ?? null
    }]));
  }

  function formatChanged(existing, next, rule) {
    if (!existing) return true;
    if (orderChanged || !sameValue(existing.label, next.label) ||
        !sameValue(existing.sortOrder, next.sortOrder) ||
        !sameValue(existing.isEnabled, next.isEnabled) ||
        !sameValue(existing.messageBehavior || 'none', next.messageBehavior) ||
        !sameValue(existing.message || '', next.message || '')) return true;
    for (const name of ['title', 'author', 'identifier', 'publication']) {
      if (!sameValue(fieldSignature(existing, name), fieldSignature(rule, name))) return true;
    }
    return !sameValue(customFieldSignature(existing), customFieldSignature(rule));
  }

  const current = order.map((code, index) => {
    const existing = existingByCode.get(code) || {};
    const rule = rules[code] || {};
    const result = {
      ...(existing.id ? { id: existing.id } : {}),
      code,
      ownerOrganizationId: existing.ownerOrganizationId || (model.isSystem ? '1' : model.contextOrgId),
      label: labels[code] || existing.label || code,
      sortOrder: orderChanged ? (index + 1) * 10 : (existing.sortOrder ?? ((index + 1) * 10)),
      isEnabled: available.has(code),
      messageBehavior: rule.messageBehavior || 'none',
      message: rule.message || '',
      title: rule.fields?.title,
      author: rule.fields?.author,
      identifier: rule.fields?.identifier,
      publication: rule.fields?.publication,
      customFields: rule.customFields || {}
    };
    if (!model.isSystem && String(existing.ownerOrganizationId || '') === String(model.contextOrgId)) {
      result.ownerOrganizationId = model.contextOrgId;
    } else if (!model.isSystem) {
      result.overridden = existing.overridden === true || formatChanged(existing, result, rule);
    }
    return result;
  });

  model.formats.forEach(existing => {
    if (order.includes(existing.code)) return;
    if (pendingDeleteIds.has(String(existing.id))) return;
    const removed = {
      id: existing.id,
      code: existing.code,
      ownerOrganizationId: existing.ownerOrganizationId || (model.isSystem ? '1' : model.contextOrgId),
      label: existing.label || existing.code,
      sortOrder: existing.sortOrder,
      isEnabled: false
    };
    if (!model.isSystem && String(existing.ownerOrganizationId || '') !== String(model.contextOrgId)) {
      removed.overridden = true;
    }
    current.push(removed);
  });
  return current;
}

function _serializeSettingsState(validate = false) {
  const isSystemContext = isSuperAdminStaff() && currentLibraryContextOrgId === 'system';

  function serializeCommonCreators(value) {
    return String(value || '')
      .split('\n')
      .map(item => item.trim())
      .filter(Boolean)
      .join('\n');
  }

  function positiveInt(id, fallback, label) {
    const raw = getFieldValue(id, String(fallback)).trim();
    if (!raw) return fallback;
    const value = parseInt(raw, 10);
    if (!Number.isFinite(value) || value < 1) {
      if (validate) {
        throw new Error(`${label} must be a number greater than 0.`);
      }
      return value;
    }
    return value;
  }

  let staffUrl = '';
  let nextLeapBibUrlPattern = leapBibUrlPattern || '';
  let nextLeapPatronUrlPattern = leapPatronUrlPattern || '';
  if (isSystemContext) {
    staffUrl = getFieldValue('system-staff-url').trim();
    if (validate) {
      const staffUrlError = validateStaffUrl(staffUrl);
      if (staffUrlError) {
        throw new Error(staffUrlError);
      }
      staffUrl = normalizeStaffUrl(staffUrl);
      setFieldValue('system-staff-url', staffUrl);
    }
    nextLeapBibUrlPattern = getFieldValue('leap-bib-url-pattern').trim();
    if (validate) {
      nextLeapBibUrlPattern = normalizeLeapBibUrlPattern(nextLeapBibUrlPattern);
      setFieldValue('leap-bib-url-pattern', nextLeapBibUrlPattern);
    }
    nextLeapPatronUrlPattern = getFieldValue('leap-patron-url-pattern').trim();
    if (validate) {
      nextLeapPatronUrlPattern = normalizeLeapPatronUrlPattern(nextLeapPatronUrlPattern);
      setFieldValue('leap-patron-url-pattern', nextLeapPatronUrlPattern);
    }
  }

  const model = currentLegacySettingsFormModel;
  const publicationOptions = model?.publicationOptionStateTrusted
    ? collectOptionList('ui-publication-options-editor', defaultPublicationOptions)
    : undefined;
  const uiText = {};
  PATRON_TEXT_FIELDS.forEach(([key, id]) => {
    const value = getFieldValue(id);
    if (scopedFieldShouldSave(model, 'patron', key, value, model?.provenance?.systemPatron?.[key])) {
      uiText[key] = value;
    }
  });

  const duplicateLabels = collectDuplicateStatusLabels();
  const scopedDuplicateLabels = {};
  Object.entries(DUPLICATE_LABEL_FIELDS).forEach(([key, patronKey]) => {
    const value = duplicateLabels[key];
    const storedBaseline = model?.provenance?.systemPatron?.[patronKey];
    const baseline = typeof storedBaseline === 'string' && storedBaseline.trim()
      ? storedBaseline
      : model?.uiText?.duplicateStatusLabels?.[key];
    if (scopedFieldShouldSave(model, 'patron', patronKey, value, baseline)) {
      scopedDuplicateLabels[key] = value;
    }
  });
  if (Object.keys(scopedDuplicateLabels).length > 0) {
    uiText.duplicateStatusLabels = scopedDuplicateLabels;
  }

  const logoAlt = getFieldValue('ui-logo-alt');
  const currentLogoAlt = String(model?.uiText?.logoAlt ?? '');
  if (!sameValue(logoAlt, currentLogoAlt)) {
    uiText.logoAlt = logoAlt;
  }
  if (isSystemContext) {
    [
      ['systemNotEnabledMessage', 'ui-system-not-enabled-msg'],
      ['misconfiguredMessage', 'ui-misconfigured-msg']
    ].forEach(([key, id]) => {
      const value = getFieldValue(id);
      const baseline = model?.provenance?.systemMessageBaseline?.[key];
      if (baseline === undefined || !sameValue(value, baseline)) {
        uiText[key] = value;
      }
    });
  }
  if (publicationOptions !== undefined && scopedSetShouldSave(model, 'publicationOptions', publicationOptions)) {
    uiText.publicationOptions = publicationOptions;
  }

  const emails = buildEmailSettingsPayload({ allowIncomplete: true });

  const nextAutoRejectTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');
  const patronCodeEligibilityEnabled = getPatronCodeEligibilityEnabled();
  const allowedPatronCodeIds = collectAllowedPatronCodeIds()
    .split(',')
    .map(value => value.trim())
    .filter(Boolean);

  if (validate) {
    if (currentLegacySettingsFormModel?.patronCodeStateTrusted &&
        patronCodeEligibilityEnabled && allowedPatronCodeIds.length === 0) {
      throw new Error('Select at least one allowed patron code when patron code access is limited.');
    }
  }

  const providers = collectExternalSearchProviders(validate);
  const formats = collectMaterialFormats();
  const customFields = !isSystemContext && currentLegacySettingsFormModel?.customFieldStateTrusted
    ? collectAdditionalFieldDefinitions()
    : undefined;
  const formatClaimRules = collectFormatClaimRules();

  const workflow = {};
  WORKFLOW_TEXT_FIELDS.forEach(([key, id]) => {
    const value = getFieldValue(id);
    if (scopedFieldShouldSave(model, 'workflow', key, value, model?.provenance?.systemWorkflow?.[key])) {
      workflow[key] = value;
    }
  });
  WORKFLOW_BOOL_FIELDS.forEach(([key, id]) => {
    const value = key === 'patronCodeEligibilityEnabled'
      ? patronCodeEligibilityEnabled
      : getFieldChecked(id);
    if (scopedFieldShouldSave(model, 'workflow', key, value, model?.provenance?.systemWorkflow?.[key])) {
      workflow[key] = value;
    }
  });
  WORKFLOW_INT_FIELDS.forEach(([key, id]) => {
    const fallback = key === 'suggestionLimit' ? 5 : (key === 'outstandingTimeoutDays' ? 30 : 14);
    const value = positiveInt(id, fallback, key);
    if (scopedFieldShouldSave(model, 'workflow', key, value, model?.provenance?.systemWorkflow?.[key])) {
      workflow[key] = value;
    }
  });
  if (scopedFieldShouldSave(model, 'workflow', 'outstandingTimeoutRejectionTemplateId', nextAutoRejectTemplateId,
      model?.provenance?.systemWorkflow?.outstandingTimeoutRejectionTemplateId)) {
    workflow.outstandingTimeoutRejectionTemplateId = nextAutoRejectTemplateId;
  }
  if (model?.commonCreatorStateTrusted) {
    const commonAuthorsList = serializeCommonCreators(getFieldValue('wf-common-authors-list'));
    if (scopedSetShouldSave(model, 'commonCreators', commonAuthorsList)) {
      workflow.commonAuthorsList = commonAuthorsList;
    }
  }
  if (model?.patronCodeStateTrusted && scopedSetShouldSave(model, 'allowedPatronCodeIds', allowedPatronCodeIds)) {
    workflow.allowedPatronCodeIds = allowedPatronCodeIds;
  }

  const payload = {
    ui_text: uiText, emails,
    ...(formatClaimRules === undefined ? {} : { formatClaimRules }),
    ...(providers === undefined ? {} : { providers }),
    ...(formats === undefined ? {} : { formats }),
    ...(customFields === undefined ? {} : { customFields }),
    ...workflow
  };

  if (isSystemContext) {
    payload.polaris = collectSettingsPolaris(validate);
    payload.staffUrl = staffUrl;
    payload.leapBibUrlPattern = nextLeapBibUrlPattern;
    payload.leapPatronUrlPattern = nextLeapPatronUrlPattern;
    const enabledLibraryOrgIds = collectEnabledLibraryIds();
    if (enabledLibraryOrgIds !== undefined) {
      payload.enabledLibraryOrgIds = enabledLibraryOrgIds;
    }
    payload.formatIconUrlPattern = getFieldValue('format-icon-url-pattern').trim();
    payload.patronEmbedAllowedOrigins = getFieldValue('patron-embed-allowed-origins').trim();
  }

  return payload;
}

export function serializeSettingsState() {
  return _serializeSettingsState(false);
}

export function buildSettingsPayload() {
  const model = currentLegacySettingsFormModel;
  if (model && !model.isSystem && model.provenance?.authoritative === false) {
    throw new Error('Library settings could not be loaded completely. Reload settings before saving.');
  }
  return _serializeSettingsState(true);
}

export function captureSettingsBaseline() {
  setInitialSettingsSnapshot(JSON.stringify(serializeSettingsState()));
}

export function checkSettingsDirty() {
  if (!initialSettingsSnapshot) return false;
  const currentState = JSON.stringify(serializeSettingsState());
  return currentState !== initialSettingsSnapshot;
}

export function hasUnrelatedSettingsDraft() {
  if (!initialSettingsSnapshot) return false;
  const baseline = JSON.parse(initialSettingsSnapshot);
  const current = serializeSettingsState();
  delete baseline.ui_text?.logoAlt;
  delete current.ui_text?.logoAlt;
  return JSON.stringify(current) !== JSON.stringify(baseline);
}
