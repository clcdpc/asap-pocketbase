import { setFieldValue, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, normalizeLeapPatronUrlPattern } from '../api.js';
import { currentLibraryContextOrgId, currentRejectionTemplates, deletedSettingsTemplates, leapBibUrlPattern, leapPatronUrlPattern, initialSettingsSnapshot, defaultPublicationOptions, setInitialSettingsSnapshot, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId } from '../state.js';
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

const patronFields = [
  ['pageTitle', 'ui-patron-page-title'], ['barcodeLabel', 'ui-barcode-label'],
  ['pinLabel', 'ui-pin-label'], ['loginPrompt', 'ui-login-prompt'],
  ['loginNote', 'ui-login-note'], ['suggestionFormNote', 'ui-suggestion-note'],
  ['noEmailMessage', 'ui-no-email-msg'], ['successTitle', 'ui-success-title'],
  ['successMessage', 'ui-success-msg'], ['alreadySubmittedMessage', 'ui-already-submitted-msg'],
  ['ebookMessage', 'ui-ebook-msg'], ['eaudiobookMessage', 'ui-eaudiobook-msg']
];
const workflowText = [
  ['suggestionLimitMessage', 'suggestion-limit-msg'], ['commonAuthorsLabel', 'wf-common-authors-label'],
  ['commonAuthorsHelp', 'wf-common-authors-help'], ['commonAuthorsMessage', 'wf-common-authors-message'],
  ['patronCodeEligibilityMessage', 'patron-code-eligibility-message']
];
const workflowBool = [
  ['outstandingTimeoutEnabled', 'outstanding-timeout-enabled'],
  ['outstandingTimeoutSendEmail', 'outstanding-timeout-send-email'],
  ['holdPickupTimeoutEnabled', 'hold-pickup-timeout-enabled'],
  ['pendingHoldTimeoutEnabled', 'pending-hold-timeout-enabled'],
  ['additionalCopyTimeoutEnabled', 'additional-copy-timeout-enabled'],
  ['commonAuthorsEnabled', 'wf-common-authors-enabled'], ['autoPromote', 'polaris-auto-promote'],
  ['allowPatronAutoholdOptOut', 'allow-patron-autohold-opt-out'],
  ['allowAnyRegisteredCardLogin', 'allow-any-registered-card-login']
];
const workflowInt = [
  ['suggestionLimit', 'suggestion-limit', 5], ['outstandingTimeoutDays', 'outstanding-timeout-days', 30],
  ['holdPickupTimeoutDays', 'hold-pickup-timeout-days', 14],
  ['pendingHoldTimeoutDays', 'pending-hold-timeout-days', 14],
  ['additionalCopyTimeoutDays', 'additional-copy-timeout-days', 14]
];
const standardTemplates = [
  ['suggestion_submitted', 'email-submit-subject', 'email-submit-body'],
  ['purchase_approved', 'email-purchase-approved-subject', 'email-purchase-approved-body'],
  ['already_owned', 'email-owned-subject', 'email-owned-body'],
  ['rejected', 'email-rejected-subject', 'email-rejected-body'],
  ['hold_placed', 'email-hold-subject', 'email-hold-body']
];

function positiveInt(id, fallback, validate, label) {
  const raw = getFieldValue(id, String(fallback)).trim();
  if (!raw) return fallback;
  const number = Number(raw);
  if (validate && (!Number.isInteger(number) || number < 1)) {
    throw new Error(`${label} must be a number greater than 0.`);
  }
  return number;
}

function collectEmails(useSmtpFields = false) {
  const value = {
    fromAddress: getFieldValue(useSmtpFields ? 'smtp-from' : 'email-from-address'),
    fromName: getFieldValue(useSmtpFields ? 'smtp-from-name' : 'email-from-name'),
    postmarkToken: getFieldValue('postmark-token').trim(),
    clearPostmarkToken: getFieldChecked('postmark-clear-token')
  };
  standardTemplates.forEach(([key, subjectId, bodyId]) => {
    value[key] = { subject: getFieldValue(subjectId), body: getFieldValue(bodyId) };
  });
  value.rejection_templates = currentRejectionTemplates.map(template => ({
    templateKey: template.templateKey,
    name: String(template.name ?? template.displayName ?? ''),
    subject: String(template.subject ?? ''),
    body: String(template.body ?? ''),
    enabled: template.enabled !== false
  }));
  deletedSettingsTemplates.forEach(template => value.rejection_templates.push({
    templateKey: template.templateKey,
    reset: true
  }));
  return value;
}

export function collectExternalSearchProviders(validate = false) {
  return [1, 2, 3, 4].map(index => {
    const isEnabled = getFieldChecked(`wf-external-search-${index}-enabled`);
    const label = getFieldValue(`wf-external-search-${index}-label`).trim();
    const urlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue(`wf-external-search-${index}-url-template`).trim());
    if (validate && (isEnabled || label || urlTemplate) && (!label || !urlTemplate)) {
      throw new Error(`External search provider ${index} requires a label and URL template.`);
    }
    if (validate) setFieldValue(`wf-external-search-${index}-url-template`, urlTemplate);
    return {
      key: `external_search_${index}`,
      isEnabled,
      label,
      urlTemplate
    };
  });
}

export function collectMaterialFormats() {
  const labels = collectFormatLabels();
  const available = new Set(collectAvailableFormats());
  const rows = Array.from(document.querySelectorAll('.format-setting-row'));
  const orderChanged = rows.some((row, index) => Number(row.getAttribute('data-original-index')) !== index);
  return collectFormatOrder().map((code, index) => ({
    code,
    label: labels[code] || code,
    sortOrder: orderChanged ? (index + 1) * 10 : Number(rows[index]?.getAttribute('data-sort-order') || ((index + 1) * 10)),
    isEnabled: available.has(code)
  }));
}

function collectForm(validate = false) {
  const system = currentLibraryContextOrgId === 'system';
  const workflow = {};
  workflowText.forEach(([key, id]) => { workflow[key] = getFieldValue(id); });
  workflowBool.forEach(([key, id]) => { workflow[key] = getFieldChecked(id); });
  workflowInt.forEach(([key, id, fallback]) => { workflow[key] = positiveInt(id, fallback, validate, key); });
  workflow.outstandingTimeoutRejectionTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');
  workflow.patronCodeEligibilityEnabled = getPatronCodeEligibilityEnabled();
  workflow.commonAuthorsList = getFieldValue('wf-common-authors-list').split(/\r?\n/)
    .map(value => value.trim()).filter(Boolean).join('\n');
  workflow.allowedPatronCodeIds = collectAllowedPatronCodeIds().split(',')
    .map(value => value.trim()).filter(Boolean).join(',');
  if (validate && workflow.patronCodeEligibilityEnabled && !workflow.allowedPatronCodeIds) {
    throw new Error('Select at least one allowed patron code when patron code access is limited.');
  }

  const uiText = {};
  patronFields.forEach(([key, id]) => { uiText[key] = getFieldValue(id); });
  uiText.duplicateStatusLabels = collectDuplicateStatusLabels();
  uiText.logoAlt = getFieldValue('ui-logo-alt');
  uiText.publicationOptions = collectOptionList('ui-publication-options-editor', defaultPublicationOptions);
  if (system) {
    uiText.systemNotEnabledMessage = getFieldValue('ui-system-not-enabled-msg');
    uiText.misconfiguredMessage = getFieldValue('ui-misconfigured-msg');
  }

  const value = {
    workflow,
    ui_text: uiText,
    emails: collectEmails(),
    providers: collectExternalSearchProviders(validate),
    formats: collectMaterialFormats(),
    formatRules: collectPatronFormatRules(),
    ...(system ? {} : {
      customFields: collectAdditionalFieldDefinitions(),
      formatClaimRules: collectFormatClaimRules()
    })
  };
  if (system) {
    let staffUrl = getFieldValue('system-staff-url').trim();
    if (validate) {
      const error = validateStaffUrl(staffUrl);
      if (error) throw new Error(error);
      staffUrl = normalizeStaffUrl(staffUrl);
      setFieldValue('system-staff-url', staffUrl);
    }
    let bibPattern = getFieldValue('leap-bib-url-pattern').trim() || leapBibUrlPattern || '';
    let patronPattern = getFieldValue('leap-patron-url-pattern').trim() || leapPatronUrlPattern || '';
    if (validate) {
      bibPattern = normalizeLeapBibUrlPattern(bibPattern);
      patronPattern = normalizeLeapPatronUrlPattern(patronPattern);
      setFieldValue('leap-bib-url-pattern', bibPattern);
      setFieldValue('leap-patron-url-pattern', patronPattern);
    }
    value.polaris = collectSettingsPolaris(validate);
    value.staffUrl = staffUrl;
    value.leapBibUrlPattern = bibPattern;
    value.leapPatronUrlPattern = patronPattern;
    value.formatIconUrlPattern = getFieldValue('format-icon-url-pattern').trim();
    value.patronEmbedAllowedOrigins = getFieldValue('patron-embed-allowed-origins').trim();
    value.enabledLibraryOrgIds = collectEnabledLibraryIds();
  }
  return value;
}

// This compares only values shown in the form. The server resolves ownership,
// inheritance, source IDs, and the actual versioned settings patch.
function changedForm(current, baseline) {
  const changed = {};
  for (const [section, value] of Object.entries(current)) {
    if (section === 'workflow' || section === 'ui_text' || section === 'emails' || section === 'polaris') {
      const fields = {};
      for (const [key, fieldValue] of Object.entries(value)) {
        const previous = baseline?.[section]?.[key];
        if (section === 'emails' && key === 'postmarkToken' && !fieldValue) continue;
        if (section === 'emails' && key === 'clearPostmarkToken' && !fieldValue) continue;
        if (section === 'polaris' && ['apiKey', 'adminPassword'].includes(key) && !fieldValue) continue;
        if (section === 'polaris' && ['clearApiKey', 'clearAdminPassword'].includes(key) && !fieldValue) continue;
        if (JSON.stringify(fieldValue) !== JSON.stringify(previous)) fields[key] = fieldValue;
      }
      if (Object.keys(fields).length) changed[section] = fields;
    } else if (JSON.stringify(value) !== JSON.stringify(baseline?.[section])) {
      changed[section] = value;
    }
  }
  return changed;
}

export function buildEmailSettingsPayload({ includeTemplates = true, useSmtpFields = false } = {}) {
  const current = collectEmails(useSmtpFields);
  if (!includeTemplates) {
    standardTemplates.forEach(([key]) => { delete current[key]; });
    delete current.rejection_templates;
  }
  return changedForm({ emails: current }, JSON.parse(initialSettingsSnapshot || '{}')).emails || {};
}

export function serializeSettingsState() {
  return collectForm(false);
}

export function buildSettingsPayload() {
  return changedForm(collectForm(true), JSON.parse(initialSettingsSnapshot || '{}'));
}

export function captureSettingsBaseline() {
  setInitialSettingsSnapshot(JSON.stringify(serializeSettingsState()));
}

export function checkSettingsDirty() {
  if (!initialSettingsSnapshot) return false;
  return JSON.stringify(serializeSettingsState()) !== initialSettingsSnapshot;
}

export function hasUnrelatedSettingsDraft() {
  if (!initialSettingsSnapshot) return false;
  const baseline = JSON.parse(initialSettingsSnapshot);
  const current = serializeSettingsState();
  delete baseline.ui_text?.logoAlt;
  delete current.ui_text?.logoAlt;
  return JSON.stringify(current) !== JSON.stringify(baseline);
}
