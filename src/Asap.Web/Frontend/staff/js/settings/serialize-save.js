import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, normalizeLeapPatronUrlPattern, setVisible, isSuperAdminStaff } from '../api.js';
import { currentLibraryContextOrgId, currentRejectionTemplates, leapBibUrlPattern, leapPatronUrlPattern, initialSettingsSnapshot, defaultPublicationOptions, setInitialSettingsSnapshot, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId, currentLegacySettingsFormModel } from '../state.js';
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
  const baselineOrder = model.formats.map(format => format.code);
  const orderChanged = !sameArray(order, baselineOrder);

  const current = order.map((code, index) => {
    const existing = existingByCode.get(code) || {};
    const rule = rules[code] || {};
    return {
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
  });

  model.formats.forEach(existing => {
    if (order.includes(existing.code)) return;
    current.push({
      id: existing.id,
      code: existing.code,
      ownerOrganizationId: existing.ownerOrganizationId || (model.isSystem ? '1' : model.contextOrgId),
      label: existing.label || existing.code,
      sortOrder: existing.sortOrder,
      isEnabled: false
    });
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

  const publicationOptions = currentLegacySettingsFormModel?.publicationOptionStateTrusted
    ? collectOptionList('ui-publication-options-editor', defaultPublicationOptions)
    : undefined;
  const uiText = {
    logoAlt: getFieldValue('ui-logo-alt'),
    pageTitle: getFieldValue('ui-patron-page-title'),
    barcodeLabel: getFieldValue('ui-barcode-label'),
    pinLabel: getFieldValue('ui-pin-label'),
    loginPrompt: getFieldValue('ui-login-prompt'),
    loginNote: getFieldValue('ui-login-note'),
    suggestionFormNote: getFieldValue('ui-suggestion-note'),
    noEmailMessage: getFieldValue('ui-no-email-msg'),
    systemNotEnabledMessage: isSystemContext ? getFieldValue('ui-system-not-enabled-msg') : undefined,
    misconfiguredMessage: isSystemContext ? getFieldValue('ui-misconfigured-msg') : undefined,
    successTitle: getFieldValue('ui-success-title'),
    successMessage: getFieldValue('ui-success-msg'),
    alreadySubmittedMessage: getFieldValue('ui-already-submitted-msg'),
    duplicateStatusLabels: collectDuplicateStatusLabels(),
    ...(publicationOptions === undefined ? {} : { publicationOptions })
  };

  const emails = {
    postmarkToken: getFieldValue('postmark-token').trim(),
    clearPostmarkToken: getFieldChecked('postmark-clear-token'),
    fromAddress: getFieldValue('email-from-address'),
    fromName: getFieldValue('email-from-name'),
    suggestion_submitted: {
      subject: getFieldValue('email-submit-subject'),
      body: getFieldValue('email-submit-body')
    },
    purchase_approved: {
      subject: getFieldValue('email-purchase-approved-subject'),
      body: getFieldValue('email-purchase-approved-body')
    },
    already_owned: {
      subject: getFieldValue('email-owned-subject'),
      body: getFieldValue('email-owned-body')
    },
    rejected: {
      subject: getFieldValue('email-rejected-subject'),
      body: getFieldValue('email-rejected-body')
    },
    rejection_templates: JSON.parse(JSON.stringify(currentRejectionTemplates || [])),
    hold_placed: {
      subject: getFieldValue('email-hold-subject'),
      body: getFieldValue('email-hold-body')
    }
  };

  const sendAutoRejectEmail = getFieldChecked('outstanding-timeout-send-email');
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

  const payload = {
    ui_text: uiText, emails,
    ...(formatClaimRules === undefined ? {} : { formatClaimRules }),
    ...(providers === undefined ? {} : { providers }),
    ...(formats === undefined ? {} : { formats }),
    ...(customFields === undefined ? {} : { customFields }),
    suggestionLimit: positiveInt('suggestion-limit', 5, 'Suggestion limit'),
    suggestionLimitMessage: getFieldValue('suggestion-limit-msg'),
    outstandingTimeoutEnabled: getFieldChecked('outstanding-timeout-enabled'),
    outstandingTimeoutDays: positiveInt('outstanding-timeout-days', 30, 'Auto-reject stalled suggestions days'),
    outstandingTimeoutSendEmail: sendAutoRejectEmail,
    outstandingTimeoutRejectionTemplateId: nextAutoRejectTemplateId,
    holdPickupTimeoutEnabled: getFieldChecked('hold-pickup-timeout-enabled'),
    holdPickupTimeoutDays: positiveInt('hold-pickup-timeout-days', 14, 'Auto-close unpicked-up holds days'),
    pendingHoldTimeoutEnabled: getFieldChecked('pending-hold-timeout-enabled'),
    pendingHoldTimeoutDays: positiveInt('pending-hold-timeout-days', 14, 'Auto-close pending holds days'),
    additionalCopyTimeoutEnabled: getFieldChecked('additional-copy-timeout-enabled'),
    additionalCopyTimeoutDays: positiveInt('additional-copy-timeout-days', 14, 'Auto-close additional copies days'),
    commonAuthorsEnabled: getFieldChecked('wf-common-authors-enabled'),
    commonAuthorsLabel: getFieldValue('wf-common-authors-label').trim() || 'Popular Creators',
    commonAuthorsHelp: getFieldValue('wf-common-authors-help').trim() || 'See if this is a creator we already collect.',
    ...(currentLegacySettingsFormModel?.commonCreatorStateTrusted
      ? { commonAuthorsList: serializeCommonCreators(getFieldValue('wf-common-authors-list')) }
      : {}),
    commonAuthorsMessage: getFieldValue('wf-common-authors-message'),
    autoPromote: getFieldChecked('polaris-auto-promote'),
    allowPatronAutoholdOptOut: getFieldChecked('allow-patron-autohold-opt-out'),
    allowAnyRegisteredCardLogin: getFieldChecked('allow-any-registered-card-login'),
    patronCodeEligibilityEnabled: patronCodeEligibilityEnabled,
    ...(currentLegacySettingsFormModel?.patronCodeStateTrusted ? { allowedPatronCodeIds } : {}),
    patronCodeEligibilityMessage: getFieldValue('patron-code-eligibility-message').trim() || 'Your library card is not eligible to use this suggestion service.'
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
