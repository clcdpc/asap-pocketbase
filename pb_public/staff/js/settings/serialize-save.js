import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, normalizeLeapPatronUrlPattern, isPocketBaseAutoCancelError, validateSmtpHostField, setVisible, isSuperAdminStaff, updateSaveBarState, markSettingsDirty, markSettingsClean } from '../api.js';
import { authorizedJson } from '../http.js';
import { showToast } from '../dialogs.js';
import { settingsForm, currentLibraryContextOrgId, currentRejectionTemplates, leapBibUrlPattern, leapPatronUrlPattern, initialSettingsSnapshot, settingsDirty, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, setSettingsSaving, setSettingsLoading, setInitialSettingsSnapshot, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId, defaultPublicationOptions } from '../state.js';
import { normalizeExternalSearchUrlTemplate, sortAuthorsByLastName } from './utils.js';
import { collectFormatLabels, collectAvailableFormats, collectFormatOrder, collectFormatClaimRules } from '../settings-formats.js';
import { collectDuplicateStatusLabels } from './duplicate-labels.js';
import { collectSettingsPolaris, collectEnabledLibraryIds } from './polaris-fields.js';
import { collectOptionList, collectPatronFormatRules } from '../settings-ui.js';
import { collectAdditionalFieldDefinitions } from '../settings-additional-fields.js';
import { refreshSettingsView, loadStaffConfig } from './loader.js';
import { loadStaffUsers } from '../settings-users.js';
import { applyLibrarySettingsToForm } from './form-population.js';

export function cloneLibrarySettingsSnapshot(settings) {
  return JSON.parse(JSON.stringify(settings || {}));
}

export function rememberLastSavedLibrarySettings(settings) {
  setLastSavedLibrarySettingsSnapshot(cloneLibrarySettingsSnapshot(settings));
  setLastSavedLibrarySettingsOrgId(currentLibraryContextOrgId || 'system');
}

export function discardLibrarySettingsChanges() {
  if (!lastSavedLibrarySettingsSnapshot || lastSavedLibrarySettingsOrgId !== (currentLibraryContextOrgId || 'system')) return;
  setSettingsLoading(true);
  try {
    applyLibrarySettingsToForm(cloneLibrarySettingsSnapshot(lastSavedLibrarySettingsSnapshot));
    captureSettingsBaseline();
    markSettingsClean('clean');
    const msg = document.getElementById('settings-msg');
    if (msg) {
      msg.textContent = '';
      msg.className = 'mt-2 font-weight-bold';
    }
  } finally {
    setSettingsLoading(false);
  }
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

export async function saveSettings(options = {}) {
  const submitBtn = settingsForm.querySelector('button[type="submit"]');
  const triggerBtn = options.button || null;
  const buttons = Array.from(new Set([submitBtn, triggerBtn].filter(Boolean)));
  const msg = document.getElementById('settings-msg');
  let saveHadError = false;
  let saveSucceeded = false;

  setSettingsSaving(true);
  updateSaveBarState('saving');
  buttons.forEach(button => {
    button.disabled = true;
  });
  msg.textContent = options.pendingText || 'Saving...';
  msg.className = 'mt-2 font-weight-bold text-info';

  try {
    if (isSuperAdminStaff() && currentLibraryContextOrgId === 'system') {
      if (!validateSmtpHostField(true)) {
        throw new Error('SMTP host is invalid.');
      }
    }
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();

    const isSystemSave = currentLibraryContextOrgId === 'system';
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      emails: payload.emails,
      ui_text: payload.ui_text,
      formatClaimRules: isSystemSave ? [] : payload.formatClaimRules,
      workflow: {
        suggestionLimit: payload.suggestionLimit,
        suggestionLimitMessage: payload.suggestionLimitMessage,
        outstandingTimeoutEnabled: payload.outstandingTimeoutEnabled,
        outstandingTimeoutDays: payload.outstandingTimeoutDays,
        outstandingTimeoutSendEmail: payload.outstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId: payload.outstandingTimeoutRejectionTemplateId,
        holdPickupTimeoutEnabled: payload.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: payload.holdPickupTimeoutDays,
        pendingHoldTimeoutEnabled: payload.pendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays: payload.pendingHoldTimeoutDays,
        additionalCopyTimeoutEnabled: payload.additionalCopyTimeoutEnabled,
        additionalCopyTimeoutDays: payload.additionalCopyTimeoutDays,
        enabledLibraryOrgIds: payload.enabledLibraryOrgIds,
        commonAuthorsEnabled: payload.commonAuthorsEnabled,
        commonAuthorsLabel: payload.commonAuthorsLabel,
        commonAuthorsHelp: payload.commonAuthorsHelp,
        commonAuthorsList: payload.commonAuthorsList,
        commonAuthorsMessage: payload.commonAuthorsMessage,
        autoPromote: payload.autoPromote,
        allowPatronAutoholdOptOut: payload.allowPatronAutoholdOptOut,
        allowAnyRegisteredCardLogin: payload.allowAnyRegisteredCardLogin,
        externalSearch1Enabled: payload.externalSearch1Enabled,
        externalSearch1Label: payload.externalSearch1Label,
        externalSearch1UrlTemplate: payload.externalSearch1UrlTemplate,
        externalSearch2Enabled: payload.externalSearch2Enabled,
        externalSearch2Label: payload.externalSearch2Label,
        externalSearch2UrlTemplate: payload.externalSearch2UrlTemplate,
        externalSearch3Enabled: payload.externalSearch3Enabled,
        externalSearch3Label: payload.externalSearch3Label,
        externalSearch3UrlTemplate: payload.externalSearch3UrlTemplate,
        externalSearch4Enabled: payload.externalSearch4Enabled,
        externalSearch4Label: payload.externalSearch4Label,
        externalSearch4UrlTemplate: payload.externalSearch4UrlTemplate
      }
    };

    if (isSystemSave) {
      libraryPayload.staffUrl = payload.staffUrl;
      libraryPayload.leapBibUrlPattern = payload.leapBibUrlPattern;
      libraryPayload.leapPatronUrlPattern = payload.leapPatronUrlPattern;
      libraryPayload.smtp = payload.smtp;
      libraryPayload.polaris = payload.polaris;
      libraryPayload.patronEmbedAllowedOrigins = payload.patronEmbedAllowedOrigins;
    }

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: libraryPayload
    });

    await libraryPromise;
    captureSettingsBaseline();
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    await refreshSettingsView({ showErrors: false });
    await loadStaffConfig();
    loadStaffUsers();
    saveSucceeded = true;
    showToast('Settings saved.', 'success');
    return true;
  } catch (err) {
    saveHadError = true;
    console.error(err);
    msg.textContent = err.message || 'Failed to save settings.';
    msg.className = 'mb-3 font-weight-bold text-danger';
    updateSaveBarState('error');
    return false;
  } finally {
    setSettingsSaving(false);
    buttons.forEach(button => {
      button.disabled = false;
    });
    updateSaveBarState(saveHadError ? 'error' : (saveSucceeded ? 'saved' : (settingsDirty ? 'dirty' : 'clean')));
  }
}

export function updateSaveButtonText() {
  const saveBtn = document.getElementById('settings-save-btn');
  if (saveBtn) {
    saveBtn.textContent = currentLibraryContextOrgId === 'system'
      ? 'Save System Defaults'
      : 'Save Library Settings';
  }
}
