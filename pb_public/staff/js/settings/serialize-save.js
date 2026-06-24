import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, normalizeLeapPatronUrlPattern, setVisible, isSuperAdminStaff } from '../api.js';
import { currentLibraryContextOrgId, currentRejectionTemplates, leapBibUrlPattern, leapPatronUrlPattern, initialSettingsSnapshot, defaultPublicationOptions, setInitialSettingsSnapshot, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId } from '../state.js';
import { normalizeExternalSearchUrlTemplate, sortAuthorsByLastName } from './utils.js';
import { collectFormatLabels, collectAvailableFormats, collectFormatOrder, collectFormatClaimRules } from '../settings-formats.js';
import { collectDuplicateStatusLabels } from './duplicate-labels.js';
import { collectSettingsPolaris, collectEnabledLibraryIds } from './polaris-fields.js';
import { collectOptionList, collectPatronFormatRules } from '../settings-ui.js';
import { collectAdditionalFieldDefinitions } from '../settings-additional-fields.js';

export function cloneLibrarySettingsSnapshot(settings) {
  return JSON.parse(JSON.stringify(settings || {}));
}

export function rememberLastSavedLibrarySettings(settings) {
  setLastSavedLibrarySettingsSnapshot(cloneLibrarySettingsSnapshot(settings));
  setLastSavedLibrarySettingsOrgId(currentLibraryContextOrgId || 'system');
}

function _serializeSettingsState(validate = false) {
  const isSystemContext = isSuperAdminStaff() && currentLibraryContextOrgId === 'system';

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
    formatLabels: collectFormatLabels(),
    formatOrder: collectFormatOrder(),
    availableFormats: collectAvailableFormats(),
    publicationOptions: collectOptionList('ui-publication-options-editor', defaultPublicationOptions),
    formatRules: collectPatronFormatRules(),
    ...(isSystemContext ? {} : { additionalFieldDefinitions: collectAdditionalFieldDefinitions() })
  };

  const emails = {
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
  const externalSearch1UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-1-url-template').trim() || 'https://www.amazon.com/s?k={{title}}');
  const externalSearch2UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-2-url-template').trim() || 'https://www.goodreads.com/search?q={{title}}');
  const externalSearch3UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-3-url-template').trim() || 'https://www.worldcat.org/search?q={{title}}');
  const externalSearch4UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-4-url-template'));

  if (validate) {
    setFieldValue('wf-external-search-1-url-template', externalSearch1UrlTemplate);
    setFieldValue('wf-external-search-2-url-template', externalSearch2UrlTemplate);
    setFieldValue('wf-external-search-3-url-template', externalSearch3UrlTemplate);
    setFieldValue('wf-external-search-4-url-template', externalSearch4UrlTemplate);
  }

  const payload = {
    ui_text: uiText, emails,
    formatClaimRules: collectFormatClaimRules(),
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
    commonAuthorsList: sortAuthorsByLastName(getFieldValue('wf-common-authors-list')),
    commonAuthorsMessage: getFieldValue('wf-common-authors-message'),
    autoPromote: getFieldChecked('polaris-auto-promote'),
    allowPatronAutoholdOptOut: getFieldChecked('allow-patron-autohold-opt-out'),
    allowAnyRegisteredCardLogin: getFieldChecked('allow-any-registered-card-login'),
    externalSearch1Enabled: getFieldChecked('wf-external-search-1-enabled'),
    externalSearch1Label: getFieldValue('wf-external-search-1-label').trim() || 'Search Amazon',
    externalSearch1UrlTemplate: externalSearch1UrlTemplate,
    externalSearch2Enabled: getFieldChecked('wf-external-search-2-enabled'),
    externalSearch2Label: getFieldValue('wf-external-search-2-label').trim() || 'Search Goodreads',
    externalSearch2UrlTemplate: externalSearch2UrlTemplate,
    externalSearch3Enabled: getFieldChecked('wf-external-search-3-enabled'),
    externalSearch3Label: getFieldValue('wf-external-search-3-label').trim() || 'Search WorldCat',
    externalSearch3UrlTemplate: externalSearch3UrlTemplate,
    externalSearch4Enabled: getFieldChecked('wf-external-search-4-enabled'),
    externalSearch4Label: getFieldValue('wf-external-search-4-label').trim(),
    externalSearch4UrlTemplate: externalSearch4UrlTemplate
  };

  if (isSystemContext) {
    payload.smtp = {
      host: getFieldValue('smtp-host').trim(),
      port: positiveInt('smtp-port', 587, 'SMTP port'),
      username: getFieldValue('smtp-username').trim(),
      password: getFieldValue('smtp-password'),
      tls: getFieldChecked('smtp-tls', true)
    };
    payload.polaris = collectSettingsPolaris();
    payload.staffUrl = staffUrl;
    payload.leapBibUrlPattern = nextLeapBibUrlPattern;
    payload.leapPatronUrlPattern = nextLeapPatronUrlPattern;
    payload.enabledLibraryOrgIds = collectEnabledLibraryIds();
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
