import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';
import { createSettingsDomainEditors } from './settings-domains.js';
import { actorKey } from './session-identity.js';
import { unconfirmedResponseError } from './mutation-outcome.js';
import { createEmailTemplatePlaceholders } from './email-template-placeholders.js';
import { createPatronTextPlaceholders } from './patron-text-placeholders.js';
import { insertPlaceholderText } from './placeholder-editing.js';
import { createEmailTestController } from './email-test.js';
import { confirmAction, controlSnapshot } from './confirmation.js';
import { byDisplayLabel } from './display-order.js';
import { inheritanceStatus } from './settings-inheritance.js';

const WORKFLOW_FIELDS = [
  ['suggestionLimit', 'suggestion-limit', 'number'],
  ['suggestionLimitMessage', 'suggestion-limit-message', 'text'],
  ['outstandingTimeoutEnabled', 'outstanding-timeout-enabled', 'boolean'],
  ['outstandingTimeoutDays', 'outstanding-timeout-days', 'number'],
  ['outstandingTimeoutSendEmail', 'outstanding-timeout-send-email', 'boolean'],
  ['outstandingTimeoutRejectionTemplateId', 'outstanding-timeout-rejection-template-id', 'id'],
  ['holdPickupTimeoutEnabled', 'hold-pickup-timeout-enabled', 'boolean'],
  ['holdPickupTimeoutDays', 'hold-pickup-timeout-days', 'number'],
  ['pendingHoldTimeoutEnabled', 'pending-hold-timeout-enabled', 'boolean'],
  ['pendingHoldTimeoutDays', 'pending-hold-timeout-days', 'number'],
  ['additionalCopyTimeoutEnabled', 'additional-copy-timeout-enabled', 'boolean'],
  ['additionalCopyTimeoutDays', 'additional-copy-timeout-days', 'number'],
  ['autoPromote', 'auto-promote', 'boolean'],
  ['commonAuthorsEnabled', 'common-authors-enabled', 'boolean'],
  ['commonAuthorsLabel', 'common-authors-label', 'text'],
  ['commonAuthorsHelp', 'common-authors-help', 'text'],
  ['commonAuthorsMessage', 'common-authors-message', 'text'],
  ['allowPatronAutoholdOptOut', 'allow-patron-autohold-opt-out', 'boolean'],
  ['allowAnyRegisteredCardLogin', 'allow-any-registered-card-login', 'boolean'],
  ['patronCodeEligibilityEnabled', 'patron-code-eligibility-enabled', 'boolean'],
  ['patronCodeEligibilityMessage', 'patron-code-eligibility-message', 'text']
];

const PATRON_FIELDS = [
  ['pageTitle', 'patron-page-title'],
  ['barcodeLabel', 'patron-barcode-label'],
  ['pinLabel', 'patron-pin-label'],
  ['loginPrompt', 'patron-login-prompt', 'message'],
  ['loginNote', 'patron-login-note', 'message'],
  ['suggestionFormNote', 'patron-suggestion-form-note', 'message'],
  ['noEmailMessage', 'patron-no-email-message', 'message'],
  ['successTitle', 'patron-success-title'],
  ['successMessage', 'patron-success-message', 'message'],
  ['alreadySubmittedMessage', 'patron-already-submitted-message', 'message'],
  ['ebookMessage', 'patron-ebook-message', 'message'],
  ['eaudiobookMessage', 'patron-eaudiobook-message', 'message'],
  ['suggestionStatusLabel', 'patron-suggestion-status-label'],
  ['outstandingPurchaseStatusLabel', 'patron-outstanding-purchase-status-label'],
  ['pendingHoldStatusLabel', 'patron-pending-hold-status-label'],
  ['holdPlacedStatusLabel', 'patron-hold-placed-status-label'],
  ['closedStatusLabel', 'patron-closed-status-label'],
  ['rejectedStatusLabel', 'patron-rejected-status-label'],
  ['holdCompletedStatusLabel', 'patron-hold-completed-status-label'],
  ['holdNotPickedUpStatusLabel', 'patron-hold-not-picked-up-status-label'],
  ['manualStatusLabel', 'patron-manual-status-label'],
  ['silentStatusLabel', 'patron-silent-status-label']
];

const EMAIL_FIELDS = [
  ['fromAddress', 'email-from-address'],
  ['fromName', 'email-from-name']
];

const SYSTEM_FIELDS = [
  ['staffUrl', 'system-staff-url'],
  ['leapBibUrlPattern', 'leap-bib-url-pattern'],
  ['leapPatronUrlPattern', 'leap-patron-url-pattern'],
  ['formatIconUrlPattern', 'format-icon-url-pattern'],
  ['systemNotEnabledMessage', 'ui-system-not-enabled-msg'],
  ['misconfiguredMessage', 'ui-misconfigured-msg']
];

const POLARIS_FIELDS = [
  ['host', 'polaris-host'],
  ['accessId', 'polaris-access-id'],
  ['staffDomain', 'polaris-domain'],
  ['adminUser', 'polaris-admin-user'],
  ['workstationId', 'polaris-workstation-id'],
  ['systemPolarisUserId', 'polaris-system-user-id']
];

const SETTINGS_OPERATION_SLOTS = ['administration-settings', 'administration-staff-access', 'administration-polaris-test', 'administration-scope', 'administration-patron-codes'];

const TEMPLATE_FIELDS = [
  ['suggestion_submitted', 'submit', 'email-submit-subject', 'email-submit-body'],
  ['purchase_approved', 'purchase-approved', 'email-purchase-approved-subject', 'email-purchase-approved-body'],
  ['already_owned', 'owned', 'email-owned-subject', 'email-owned-body'],
  ['hold_placed', 'hold', 'email-hold-subject', 'email-hold-body'],
  ['rejected', 'rejected', 'email-rejected-subject', 'email-rejected-body']
];

function node(tag, attributes = {}, children = []) {
  const value = document.createElement(tag);
  for (const [name, attribute] of Object.entries(attributes)) {
    if (attribute === null || attribute === undefined) continue;
    if (name === 'className') value.className = attribute;
    else if (name === 'text') value.textContent = attribute;
    else if (name === 'checked') value.checked = Boolean(attribute);
    else if (name === 'disabled') value.disabled = Boolean(attribute);
    else if (name === 'value') value.value = attribute;
    else {
      value.setAttribute(name, String(attribute));
    }
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child === null || child === undefined) continue;
    value.append(child instanceof globalThis.Node ? child : document.createTextNode(String(child)));
  }
  return value;
}

function clone(value) {
  return value === undefined ? undefined : JSON.parse(JSON.stringify(value));
}

function object(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function property(value, key) {
  const source = object(value);
  if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  const pascal = key.charAt(0).toUpperCase() + key.slice(1);
  return source[pascal];
}

function meaningful(value) {
  return value !== null && value !== undefined &&
    (typeof value !== 'string' || value.trim() !== '');
}

function isNonemptyString(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function isNonnegativeInt32(value) {
  return Number.isInteger(value) && value >= 0 && value <= 2147483647;
}

function isCanonicalPositiveInt64Id(value) {
  return typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
    (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
}

function clean(value) {
  if (value === null || value === undefined) return null;
  const result = String(value).trim();
  return result || null;
}

function stringValue(value) {
  return value === null || value === undefined ? '' : String(value);
}

function mergeConfigured(system, override, fallback = {}) {
  const result = { ...object(fallback) };
  for (const [key, value] of Object.entries(object(system))) {
    if (key !== 'version' && meaningful(value)) result[key] = value;
  }
  for (const [key, value] of Object.entries(object(override))) {
    if (key !== 'version' && meaningful(value)) result[key] = value;
  }
  return result;
}

function readControl(control, kind = 'text') {
  if (!control) return null;
  if (kind === 'message') return control.value.trim() ? control.value : null;
  if (kind === 'boolean' || control.type === 'checkbox') return control.checked;
  if (kind === 'id') {
    return clean(control.value);
  }
  if (kind === 'number' || control.type === 'number') {
    const value = control.value.trim();
    return value === '' ? null : Number(value);
  }
  return clean(control.value);
}

function writeControl(control, value) {
  if (!control) return;
  if (control.type === 'checkbox') {
    control.checked = Boolean(value);
  } else {
    control.value = value === null || value === undefined ? '' : String(value);
  }
}

function snapshotForm(form) {
  return [...form.elements].filter(control => control.matches('input, textarea, select') && !control.closest?.('[data-settings-ignore="true"]')).map(control => [
    control.id,
    control.type === 'checkbox' ? control.checked : control.value
  ]);
}

function sameSnapshot(first, second) {
  return JSON.stringify(first) === JSON.stringify(second);
}

function snapshotValues(rows) {
  return Array.isArray(rows) ? rows : [];
}

function snapshotSet(snapshot) {
  return snapshot && snapshot.exists ? snapshotValues(snapshot.values) : [];
}

function templateRows(data) {
  return Array.isArray(data?.stored?.templates) ? data.stored.templates : [];
}

function templateEffectiveRows(data, organizationId) {
  const rows = templateRows(data);
  const system = rows.filter(row => Number(row.organizationId) === 1);
  if (organizationId === 1) return system;
  const library = rows.filter(row => Number(row.organizationId) === organizationId);
  return system.map(row => {
    const override = library.find(item => String(item.sourceTemplateId || '') === String(row.id));
    return {
      ...row,
      organizationId,
      subject: meaningful(override?.subject) ? override.subject : row.subject,
      body: meaningful(override?.body) ? override.body : row.body,
      enabled: override?.enabled === false ? false : row.enabled,
      sourceTemplateId: override ? String(row.id) : null,
      overridden: Boolean(override)
    };
  }).concat(library.filter(row => row.isCustom));
}

function formatRulesFromEffective(formats) {
  const result = {};
  for (const format of Array.isArray(formats) ? formats : []) {
    const rule = {
      messageBehavior: format.messageBehavior,
      message: format.message,
      title: format.title,
      author: format.author,
      identifier: format.identifier,
      publication: format.publication
    };
    if (format.customFields && typeof format.customFields === 'object') rule.customFields = format.customFields;
    result[format.code] = rule;
  }
  return result;
}

export function mergeSettingsValues(system, override, fallback = {}) {
  return mergeConfigured(system, override, fallback);
}

export function createSettingsController({
  root,
  tab,
  announce,
  onPanelChange,
  onScopeChange,
  prepareDeparture,
  onCommitted = () => {},
  onConfigurationCommitted = () => {},
  onStaffAccessCommitted = () => {},
  onOrganizationCatalogCommitted = () => {},
  refreshCurrentStaff = async () => true,
  onRefreshed = () => {},
  onUnconfirmed = () => {},
  onEmailTestOperations = () => {}, onEmailTestSubmitted = () => {}, onStaffAuditHistory = () => {}
}) {
  const dom = {
    message: root.querySelector('#settings-message'),
    commonAuthorsEnabled: root.querySelector('#common-authors-enabled'),
    commonAuthorsOptions: root.querySelector('#common-authors-options'),
    scopeField: root.querySelector('#settings-scope-field'),
    scope: root.querySelector('#settings-scope'),
    systemStaffUrl: root.querySelector('#system-staff-url'),
    useCurrentStaffUrl: root.querySelector('#use-current-staff-url'),
    staffUrlCandidate: root.querySelector('#system-staff-url-candidate'),
    postmarkToken: root.querySelector('#email-postmark-token'),
    clearPostmarkToken: root.querySelector('#email-clear-postmark-token'),
    postmarkTokenStatus: root.querySelector('#email-token-status'),
    form: root.querySelector('#settings-form'),
    version: root.querySelector('#settings-version'),
    nav: [...root.querySelectorAll('#settings-nav [data-settings-panel]')],
    panels: [...root.querySelectorAll('[data-settings-panel-content]')],
    refresh: root.querySelector('#settings-refresh'),
    saveBar: root.querySelector('.settings-save-bar'),
    save: root.querySelector('#settings-save'),
    discard: root.querySelector('#settings-discard'),
    reset: root.querySelector('#settings-reset'),
    testPolaris: root.querySelector('#btn-test-polaris'),
    syncOrganizations: root.querySelector('#btn-sync-organizations'),
    polarisResult: root.querySelector('#polaris-test-result'),
    syncResult: root.querySelector('#organizations-sync-result'),
    polarisAnnouncement: root.querySelector('#polaris-action-announcement'),
    organizationStatus: root.querySelector('#organizations-status-message'),
    organizations: root.querySelector('#settings-organizations-list'),
    referenceOrganizations: root.querySelector('#settings-reference-organizations'),
    manageParticipation: root.querySelector('#manage-library-participation'),
    organizationCatalog: root.querySelector('#settings-organization-catalog'),
    staffStatus: root.querySelector('#staff-access-status'),
    staffRefresh: root.querySelector('#staff-refresh'),
    staffShowInactive: root.querySelector('#staff-show-inactive'),
    staffCounts: root.querySelector('#staff-roster-counts'),
    staffCreate: root.querySelector('#staff-add-submit'),
    staffCreateToggle: root.querySelector('#staff-add-toggle'),
    staffCreateForm: root.querySelector('#staff-add-form'),
    staffCreateCancel: root.querySelector('#staff-add-cancel'),
    staffEmail: root.querySelector('#staff-add-email'),
    staffRole: root.querySelector('#staff-add-role'),
    staffOrganization: root.querySelector('#staff-add-organization'),
    staffUsers: root.querySelector('#settings-staff-users-list'),
    staffAuditHistory: root.querySelector('#staff-audit-history'),
    saveLogo: root.querySelector('#save-branding-logo'),
    discardLogoDraft: root.querySelector('#discard-branding-draft'),
    clearLogo: root.querySelector('#clear-branding-logo'),
    logo: root.querySelector('#branding-logo'),
    brandingStatus: root.querySelector('#branding-status'),
    brandingPreview: root.querySelector('#branding-preview'),
    brandingPreviewSource: root.querySelector('#branding-preview-source'),
    systemContext: root.querySelector('#settings-system-context'),
    switchSystem: root.querySelector('#settings-switch-system'),
    publicLinks: root.querySelector('#patron-public-links'),
    publicStatus: root.querySelector('#patron-public-url-status'),
    publicUrl: root.querySelector('#patron-public-url'),
    iframeMarkup: root.querySelector('#patron-iframe-markup'),
    loaderMarkup: root.querySelector('#patron-loader-markup'),
    copyStatus: root.querySelector('#patron-copy-status'),
    crossLibraryUrl: root.querySelector('#cross-library-public-url'),
    saveTitle: root.querySelector('#settings-save-title'),
    saveDetail: root.querySelector('#settings-save-detail')
  };

  const state = {
    staff: null,
    data: null,
    organizations: [],
    organizationsReady: false,
    staffUsers: [],
    staffCanAssignSuperAdmin: false,
    staffAccessLoaded: false,
    lastStaffCleanup: null,
    scope: 'system',
    activePanel: 'start',
    baselineSnapshot: [],
    baselineEditors: new Map(),
    baselineTemplates: new Map(),
    scalarIntents: new Map(),
    templateIntents: new Map(),
    scalarDisplays: new Map(),
    baselineOwnership: '',
    pendingDeletedFormats: [],
    savedRejectionTemplateId: '',
    awaitingReload: false,
    awaitingReloadMutation: null,
    outcomeUncertain: false,
    unconfirmedMutation: null,
    saving: false,
    loading: false,
    pendingMutation: null,
    visible: false,
    bound: false
  };
  let currentStaffUrlSuggestion = null;
  let acceptedPostmarkToken = false;
  let pendingCommonAuthorsFocus = false;
  const reads = createLatestLoad(), events = new window.AbortController();
  let disposed = false, contextGeneration = 0, drafts = createDraftScope(), scopePreparation = null;
  const emailTest = createEmailTestController({ root: root.querySelector('#settings-email-test'),
    context: () => ({ ownerKey: actorKey(state.staff), version: state.data?.version,
      organizationId: organizationId(), active: state.visible && state.activePanel === 'smtp' && !disposed,
      ready: Boolean(state.data && String(state.data.orgId) === String(state.scope) && !state.awaitingReload && !state.loading),
      dirty: isDirty(), blocked: Boolean(state.pendingMutation || state.saving || state.outcomeUncertain || state.awaitingReload) }),
    onOperations: onEmailTestOperations, onSubmitted: onEmailTestSubmitted });
  let settingsDraft = drafts.register({ root: dom.form, isDirty: hasSettingsDraft });
  let staffCreateDraft = null;
  let polarisTest = null;
  const staffDrafts = new Map(), rosterDrafts = new Set(), staffRows = new Map();
  function listen(target, name, handler, capture = false) {
    target?.addEventListener(name, event => { if (!disposed && target.isConnected !== false) return handler(event); }, { signal: events.signal, capture });
  }
  let logoDraftUrl = null;
  let logoPreviewGeneration = 0;
  let copyGeneration = 0;
  let notificationGeneration = 0, staffStatusGeneration = 0;
  let blockedDraftNotice = null;
  let pendingInheritanceNotice = null;
  let templateValidationTarget = null;
  let staffEmailValidationNotice = null;
  let staffFocusSequence = 0;

  function releaseLogoDraft() {
    if (logoDraftUrl) URL.revokeObjectURL(logoDraftUrl);
    logoDraftUrl = null;
    logoPreviewGeneration += 1;
  }

  function renderPublicLinks(data) {
    const links = object(property(data, 'publicPatron'));
    const url = stringValue(property(links, 'url'));
    dom.publicLinks.hidden = !url;
    dom.publicStatus.textContent = url ? 'Public links for the selected active library.' :
      stringValue(property(data, 'publicPatronUnavailableReason'));
    dom.publicUrl.value = url;
    dom.iframeMarkup.value = stringValue(property(links, 'iframe'));
    dom.loaderMarkup.value = stringValue(property(links, 'autoResize'));
    dom.crossLibraryUrl.hidden = !url;
    if (url) dom.crossLibraryUrl.href = url;
    else dom.crossLibraryUrl.removeAttribute('href');
    dom.copyStatus.textContent = '';
    copyGeneration += 1;
  }

  function renderBrandingPreview(draft = false) {
    if (!state.data) return;
    const stored = object(property(state.data, 'stored'));
    const configured = object(property(stored, 'configuredSystem'));
    const library = object(property(stored, 'libraryOverride'));
    const systemBranding = object(property(configured, 'branding'));
    const libraryBranding = object(property(library, 'branding'));
    const effective = object(property(state.data, 'effective'));
    const hasLogo = Boolean(property(effective, 'hasLogo'));
    const imageSource = draft ? 'Unsaved image preview' : isSystem()
      ? (hasLogo ? 'Saved system logo' : 'No logo configured')
      : property(libraryBranding, 'hasLogo') ? 'Saved library logo'
        : property(systemBranding, 'hasLogo') ? 'Inherited system logo' : 'No logo configured';
    const altText = document.getElementById('branding-alt').value;
    const effectiveAlt = stringValue(property(effective, 'logoAltText'));
    const altDescription = altText === effectiveAlt
      ? `Effective alternate text: ${effectiveAlt || 'none'}.`
      : `Unsaved alternate text draft: ${altText || 'none'}. Effective alternate text: ${effectiveAlt || 'none'}.`;
    dom.brandingPreviewSource.textContent = `${imageSource}. ${draft ? 'Save the image to publish it. ' : ''}${altDescription}`;
    const generation = ++logoPreviewGeneration;
    dom.brandingPreview.hidden = true;
    dom.brandingPreview.alt = altText;
    if (!draft && !hasLogo) {
      dom.brandingPreview.removeAttribute('src');
      return;
    }
    dom.brandingPreview.onload = () => {
      if (generation === logoPreviewGeneration) dom.brandingPreview.hidden = false;
    };
    dom.brandingPreview.onerror = () => {
      if (generation === logoPreviewGeneration) dom.brandingPreviewSource.textContent += ' Preview image could not be loaded.';
    };
    dom.brandingPreview.src = draft && logoDraftUrl ? logoDraftUrl
      : `/api/asap/config/logo?libraryOrgId=${encodeURIComponent(organizationId())}&v=${encodeURIComponent(state.data.version || '')}`;
  }

  function previewLogoFile() {
    releaseLogoDraft();
    const file = dom.logo.files?.[0];
    if (file && ['image/png', 'image/jpeg', 'image/gif'].includes(file.type)) {
      logoDraftUrl = URL.createObjectURL(file);
    }
    renderBrandingPreview(Boolean(logoDraftUrl));
    dom.brandingStatus.textContent = file
      ? logoDraftUrl ? 'Unsaved image preview. Server validation happens when you save.' : 'This image type cannot be previewed or saved.'
      : '';
    dom.discardLogoDraft.hidden = !file;
    updateDirtyState();
  }

  async function copyPublicValue(value, label) {
    const context = captureSettingsContext();
    const generation = copyGeneration;
    try {
      await navigator.clipboard.writeText(value);
      if (!isSettingsContextCurrent(context) || generation !== copyGeneration) return;
      dom.copyStatus.textContent = `${label} copied.`;
      notify(`${label} copied.`, 'success');
    } catch (error) {
      if (!isSettingsContextCurrent(context) || generation !== copyGeneration) return;
      dom.copyStatus.textContent = `${label} could not be copied. Select the text and copy it manually.`;
      notify(dom.copyStatus.textContent, 'error');
    }
  }

  const domainEditors = createSettingsDomainEditors({
    root,
    onChange: draftChanged,
    onTemplatesRendered: () => placeholders.refresh(),
    onInvalidTemplate: (control, message) => placeholders.showError(control, message),
    onRetryCodes: retryPatronCodes,
    confirmReset: message => confirmSettings('Use system value?', message, 'Use system value'),
    canRemoveTemplate: template => {
      const id = stringValue(template.id);
      const savedReference = state.savedRejectionTemplateId;
      const draftReference = document.getElementById('outstanding-timeout-rejection-template-id').value;
      if (id && (id === savedReference || id === draftReference)) {
        notify('This rejection template is used by auto-rejection. Change and save the workflow setting before deleting it.', 'error');
        return false;
      }
      return true;
    }
  });

  const placeholders = createEmailTemplatePlaceholders({
    root, panel: root.querySelector('#settings-templates'), listen,
    onReveal: () => activatePanel('templates'),
    onValidationError: (control, message) => {
      if (templateValidationTarget) templateValidationTarget = { control, message };
    },
    captureContext: captureSettingsContext, isContextCurrent: isSettingsContextCurrent,
    isActive: () => state.visible && state.activePanel === 'templates' && Boolean(state.data) &&
      !state.loading && !state.saving && !state.pendingMutation && !state.awaitingReload && !state.outcomeUncertain
  });

  const patronPlaceholders = createPatronTextPlaceholders({
    root, panel: root.querySelector('#settings-patron'), listen,
    captureContext: captureSettingsContext, isContextCurrent: isSettingsContextCurrent,
    isActive: () => state.visible && state.activePanel === 'patron' && !state.pendingMutation &&
      !state.awaitingReload && !state.outcomeUncertain
  });

  function isSystem() {
    return state.scope === 'system';
  }

  function buildCurrentStaffUrlSuggestion() {
    try {
      const currentAddress = new URL(window.location.href);
      const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(currentAddress.hostname);
      if (currentAddress.protocol !== 'https:' &&
          !(currentAddress.protocol === 'http:' && loopback)) return null;
      const candidate = new URL('/staff/', currentAddress.origin);
      if (candidate.origin === 'null') return null;
      candidate.username = '';
      candidate.password = '';
      candidate.search = '';
      candidate.hash = '';
      return candidate.href;
    } catch {
      return null;
    }
  }

  function renderCurrentStaffUrlSuggestion(result = '') {
    currentStaffUrlSuggestion = buildCurrentStaffUrlSuggestion();
    dom.useCurrentStaffUrl.hidden = !currentStaffUrlSuggestion;
    dom.staffUrlCandidate.textContent = currentStaffUrlSuggestion
      ? result === 'applied'
        ? `Staff URL draft updated to ${currentStaffUrlSuggestion}. Save settings to persist it.`
        : result === 'unchanged'
          ? `Staff URL already matches the current address: ${currentStaffUrlSuggestion}.`
          : `Current address candidate: ${currentStaffUrlSuggestion}`
      : 'Current address unavailable. Use HTTPS or HTTP on localhost, 127.0.0.1, or [::1].';
  }

  function captureSettingsContext() {
    return { scope: String(state.scope), staff: actorKey(state.staff), generation: contextGeneration, owner: state.staff };
  }

  function isSettingsContextCurrent(context) {
    return !disposed && context && context.generation === contextGeneration && context.scope === String(state.scope) && context.staff === actorKey(state.staff);
  }

  function createSuggestionLimitPlaceholderPicker() {
    const field = root.querySelector('[data-setting-section="workflow"][data-setting-key="suggestionLimitMessage"]');
    const control = field?.querySelector('#suggestion-limit-message');
    const trigger = field?.querySelector('#suggestion-limit-placeholder-trigger');
    const popup = field?.querySelector('#suggestion-limit-placeholder-picker');
    const status = field?.querySelector('#suggestion-limit-placeholder-status');
    const view = root.ownerDocument.defaultView;
    let opened = null;

    if (!field || !control || !trigger || !popup || !status) return { clear() {}, refresh() {} };
    if (!popup.showPopover) popup.hidden = true;

    function overrideValue() {
      return state.scalarIntents.get('workflow.suggestionLimitMessage') ?? null;
    }

    function editable() {
      if (!state.visible || state.activePanel !== 'workflow' || !state.data || state.pendingMutation ||
          state.loading || state.saving || state.awaitingReload || state.outcomeUncertain || dom.form.inert ||
          dom.form.hidden || !root.contains(field) || !field.isConnected || !control.isConnected ||
          !field.contains(control) || control.disabled ||
          control.readOnly || control.matches(':disabled') || control.closest('[hidden], [inert]')) return false;
      for (let ancestor = control; ancestor; ancestor = ancestor.parentElement) {
        const style = view.getComputedStyle(ancestor);
        if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
      }
      return true;
    }

    function clear() {
      const prior = opened;
      opened = null;
      trigger.setAttribute('aria-expanded', 'false');
      status.textContent = '';
      if (popup.showPopover && popup.matches(':popover-open')) popup.hidePopover();
      else if (!popup.showPopover) popup.hidden = true;
      return prior;
    }

    function refresh() {
      clear();
      trigger.disabled = !editable();
    }

    listen(trigger, 'click', () => {
      if (opened) {
        clear();
        return;
      }
      if (!editable()) {
        refresh();
        return;
      }
      const start = control.selectionStart ?? control.value.length;
      const end = control.selectionEnd ?? control.value.length;
      opened = {
        context: captureSettingsContext(), value: control.value, start, end,
        override: overrideValue()
      };
      trigger.setAttribute('aria-expanded', 'true');
      if (popup.showPopover) popup.showPopover();
      else popup.hidden = false;
      const bounds = trigger.getBoundingClientRect();
      const width = Math.min(384, Math.max(160, view.innerWidth - 24));
      popup.style.width = `${width}px`;
      popup.style.maxHeight = `${Math.max(120, view.innerHeight - 24)}px`;
      popup.style.left = `${Math.max(12, Math.min(bounds.left, view.innerWidth - width - 12))}px`;
      const height = popup.getBoundingClientRect().height || popup.offsetHeight;
      popup.style.top = `${Math.max(12, Math.min(bounds.bottom + 4, view.innerHeight - height - 12))}px`;
      popup.querySelector('button[data-suggestion-placeholder]')?.focus({ preventScroll: true });
    });

    listen(popup, 'click', event => {
      const button = event.target.closest('[data-suggestion-placeholder]');
      const saved = opened;
      if (!button || !popup.contains(button) || !saved) return;
      if (button.dataset.suggestionPlaceholder !== 'next_available_date' ||
          !isSettingsContextCurrent(saved.context) || !editable() || control.value !== saved.value ||
          overrideValue() !== saved.override || !popup.contains(root.ownerDocument.activeElement)) {
        clear();
        return;
      }

      const token = '{{next_available_date}}';
      clear();
      insertPlaceholderText(control, token, saved.start, saved.end);
      status.textContent = `Inserted ${token}.`;
    });

    listen(field, 'change', refresh);
    listen(control, 'input', () => { if (opened) clear(); });
    listen(popup, 'toggle', event => { if (event.newState === 'closed' && opened) clear(); });
    listen(root.ownerDocument, 'focusin', event => {
      if (opened && event.target !== trigger && !popup.contains(event.target)) clear();
    });
    listen(root.ownerDocument, 'keydown', event => {
      if (event.key !== 'Escape' || !opened) return;
      event.preventDefault();
      event.stopPropagation();
      clear();
      trigger.focus({ preventScroll: true });
    });
    listen(view, 'resize', clear);
    listen(view, 'blur', clear);
    return { clear, refresh };
  }

  const suggestionLimitPlaceholders = createSuggestionLimitPlaceholderPicker();

  async function confirmSettings(title, message, confirmLabel, tone = 'normal') {
    const context = captureSettingsContext(), data = state.data, version = state.data?.version;
    const scope = drafts, stamp = drafts.stamp(), panel = state.activePanel;
    const snapshot = controlSnapshot(root), ownership = ownershipSnapshot(), awaitingReload = state.awaitingReload;
    const isCurrent = () => isSettingsContextCurrent(context) && state.visible && root.isConnected &&
      !state.pendingMutation && !state.loading && state.activePanel === panel && state.data === data &&
      state.data?.version === version && state.awaitingReload === awaitingReload && drafts === scope &&
      drafts.stamp() === stamp && snapshot === controlSnapshot(root) && ownership === ownershipSnapshot();
    return await confirmAction({ title, message, confirmLabel, tone, isCurrent }) && isCurrent();
  }

  function beginSettingsOperation(slot) {
    const operation = reads.begin(slot);
    const context = captureSettingsContext();
    return { ...operation, context };
  }

  function isSettingsOperationCurrent(operation) {
    return operation?.isCurrent() && isSettingsContextCurrent(operation.context);
  }

  function clearBlockedDraftNotice() {
    blockedDraftNotice?.clear();
    blockedDraftNotice = null;
  }

  function beginSettingsMutation(slot, consumes, preserves = []) {
    if (disposed || !state.staff || !state.visible || !root.isConnected || state.pendingMutation || state.awaitingReload) return null;
    const declaration = { consumes, preserves };
    if (!drafts.admit(declaration).allowed) {
      clearBlockedDraftNotice();
      blockedDraftNotice = { declaration, clear: notify('Save or discard the competing Settings draft before this action.', 'warning') };
      return null;
    }
    clearBlockedDraftNotice();
    const context = captureSettingsContext();
    const operation = { slot, context, owner: context.owner, outcome: 'pending', preserves,
      isCurrent: () => isSettingsContextCurrent(context) };
    holdSettingsMutation(operation);
    return operation;
  }

  function recordCommitted(operation, message, configurationScope = null) {
    operation.outcome = 'committed';
    onCommitted(message, operation.owner, operation);
    if (configurationScope !== null) onConfigurationCommitted(operation.owner, configurationScope);
    if (isSettingsOperationCurrent(operation)) {
      state.awaitingReload = true;
      state.awaitingReloadMutation = operation;
    }
  }

  function recordFailure(operation, error, message, report = notify) {
    if (operation.outcome === 'committed') return;
    operation.outcome = isUnconfirmedMutationFailure(error) ? 'uncertain' : 'rejected';
    if (operation.outcome !== 'uncertain') return;
    onUnconfirmed(message, operation.owner, operation);
    if (isSettingsOperationCurrent(operation)) { state.unconfirmedMutation = operation; markUnconfirmedMutation(message, report); }
  }

  function isUnconfirmedMutationFailure(error) {
    return error?.outcomeUnknown === true || !error?.status || error.status === 408 || error.status >= 500 || isAbortError(error);
  }

  function markUnconfirmedMutation(message, report = notify) {
    state.outcomeUncertain = true;
    state.awaitingReload = true;
    report(message, 'error');
  }

  function holdSettingsMutation(operation) {
    cancelPolarisTest();
    reads.begin('administration-settings').abort();
    reads.begin('administration-staff-access').abort();
    reads.begin('administration-patron-codes').abort();
    placeholders.clear();
    suggestionLimitPlaceholders.clear();
    state.pendingMutation = operation;
    dom.form.inert = true;
    dom.form.setAttribute('aria-busy', 'true');
    dom.refresh.disabled = false;
  }

  function finishSettingsMutation(operation) {
    if (state.pendingMutation !== operation) return;
    state.pendingMutation = null;
    if (!isSettingsContextCurrent(operation.context)) return;
    dom.form.inert = state.awaitingReload;
    dom.form.removeAttribute('aria-busy');
    dom.refresh.disabled = false;
    restoreCommonAuthorsFocus();
    placeholders.refresh();
    suggestionLimitPlaceholders.refresh();
  }

  function cancelSettingsOperations() {
    clearBlockedDraftNotice();
    emailTest.invalidate();
    state.loading = false;
    placeholders.clear(); patronPlaceholders.clear(); suggestionLimitPlaceholders.clear();
    placeholders.retireValidationNotice();
    scopePreparation = null;
    cancelPolarisTest();
    dom.syncOrganizations.disabled = false;
    contextGeneration += 1; drafts.dispose(); drafts = createDraftScope();
    settingsDraft = drafts.register({ root: dom.form, isDirty: hasSettingsDraft });
    staffDrafts.clear(); rosterDrafts.clear(); staffRows.clear(); staffCreateDraft = null;
    state.staffCanAssignSuperAdmin = false;
    dom.staffUsers.replaceChildren();
    dom.staffShowInactive.checked = false;
    dom.staffCounts.textContent = '';
    setStaffCreateOpen(false);
    for (const slot of SETTINGS_OPERATION_SLOTS) reads.begin(slot).abort();
  }

  function cancelPolarisTest() {
    reads.begin('administration-polaris-test').abort();
    polarisTest = null;
    dom.testPolaris.disabled = false;
    clearPolarisFeedback();
  }

  function clearPolarisFeedback() {
    for (const result of [dom.polarisResult, dom.syncResult]) {
      result.textContent = '';
      result.className = 'settings-result';
    }
    dom.polarisAnnouncement.textContent = '';
  }

  function setPolarisFeedback(result, message, kind = '') {
    clearPolarisFeedback();
    result.textContent = message;
    result.className = `settings-result${kind ? ` ${kind}` : ''}`;
    dom.polarisAnnouncement.textContent = message;
  }

  function organizationId() {
    return isSystem() ? 1 : Number(state.scope);
  }

  function isLibraryOrganization(organization) {
    return Number(organization?.id) > 1 && Number(organization?.organizationCodeId) === 2;
  }

  function validateOrganizationCatalog(response, scope, settingsData) {
    const values = response?.data ?? response;
    if (!Array.isArray(values) || values.length === 0) {
      throw new Error('The organization list is incomplete. Reload settings before saving.');
    }
    const ids = new Set();
    const rows = [];
    for (const value of values) {
      if (!value || typeof value !== 'object' || Array.isArray(value) ||
          !Number.isSafeInteger(value.id) || value.id < 1 || ids.has(value.id) ||
          typeof value.name !== 'string' || !value.name.trim() ||
          !Object.prototype.hasOwnProperty.call(value, 'organizationCodeId') ||
          value.organizationCodeId !== null && !Number.isSafeInteger(value.organizationCodeId) ||
          !Object.prototype.hasOwnProperty.call(value, 'parentOrganizationId') ||
          value.parentOrganizationId !== null && !Number.isSafeInteger(value.parentOrganizationId) ||
          typeof value.isActive !== 'boolean' || typeof value.version !== 'string' || !value.version.trim()) {
        throw new Error('The organization list is malformed or incomplete. Reload settings before saving.');
      }
      ids.add(value.id);
      rows.push(value);
    }

    if (state.staff?.role === 'super_admin') {
      if (!ids.has(1)) {
        throw new Error('The organization list is missing the system organization. Reload settings before saving.');
      }
      const systemSettings = property(property(settingsData, 'stored'), 'systemSettings');
      const expectedEnabled = property(systemSettings, 'enabledLibraryOrgIds');
      const expectedLibraries = property(systemSettings, 'libraryOrgIds');
      if (!Array.isArray(expectedEnabled) || expectedEnabled.some(id => !Number.isSafeInteger(id) || id <= 1) ||
          new Set(expectedEnabled).size !== expectedEnabled.length ||
          !Array.isArray(expectedLibraries) || expectedLibraries.some(id => !Number.isSafeInteger(id) || id <= 1) ||
          new Set(expectedLibraries).size !== expectedLibraries.length) {
        throw new Error('The saved participation snapshot is incomplete. Reload settings before saving.');
      }
      const libraryRows = rows.filter(isLibraryOrganization).map(item => item.id).sort((first, second) => first - second);
      const expectedLibraryRows = [...expectedLibraries].sort((first, second) => first - second);
      if (JSON.stringify(libraryRows) !== JSON.stringify(expectedLibraryRows)) {
        throw new Error('The organization list is missing library rows. Reload settings before saving.');
      }
      const activeRows = rows.filter(item => isLibraryOrganization(item) && item.isActive)
        .map(item => item.id).sort((first, second) => first - second);
      const expectedRows = [...expectedEnabled].sort((first, second) => first - second);
      if (JSON.stringify(activeRows) !== JSON.stringify(expectedRows)) {
        throw new Error('The organization list is missing active libraries. Reload settings before saving.');
      }
    } else if (scope !== 'system' && !ids.has(Number(scope))) {
      throw new Error('The organization list is missing the selected library. Reload settings before saving.');
    }

    return rows;
  }

  function acceptOrganizationCatalog(rows) {
    state.organizations = rows;
    state.organizationsReady = true;
  }

  function scopedStaffOrganizationId() {
    if (!state.staff) return null;
    if (state.staff.role !== 'super_admin') return clean(state.staff.organizationId);
    return isSystem() ? null : clean(state.scope);
  }

  function scopedStaffLabel() {
    const scope = scopedStaffOrganizationId();
    if (!scope) return 'all organizations';
    const organization = state.organizations.find(item => String(item.id) === String(scope));
    return organization?.name || `organization ${scope}`;
  }

  function roleLabel(role) {
    if (role === 'super_admin') return 'Super admin';
    if (role === 'admin') return 'Library admin';
    return 'Staff';
  }

  function roleChoices() {
    const roles = state.staffCanAssignSuperAdmin || state.staff?.role === 'super_admin'
      ? ['super_admin', 'admin', 'staff']
      : ['admin', 'staff'];
    return roles.map(role => ({ value: role, label: roleLabel(role) }));
  }

  function organizationChoices(role = 'staff') {
    if (role === 'super_admin') return [{ value: '1', label: 'System level' }];
    const staffScope = state.staff?.role === 'super_admin'
      ? null
      : clean(state.staff?.organizationId);
    const organizations = staffScope
      ? state.organizations.filter(item => isLibraryOrganization(item) && String(item.id) === String(staffScope))
      : state.organizations.filter(isLibraryOrganization);
    return byDisplayLabel(organizations, item => item.name || `Library ${item.id}`).map(item => ({
      value: String(item.id),
      label: item.name || `Library ${item.id}`
    }));
  }

  function replaceSelectOptions(select, options, selected) {
    if (!select) return;
    select.replaceChildren(...options.map(option => node('option', {
      value: option.value,
      text: option.label,
      selected: String(option.value) === String(selected ?? '')
    })));
    if (options.some(option => String(option.value) === String(selected ?? ''))) {
      select.value = String(selected);
    } else if (options.length > 0) {
      select.value = String(options[0].value);
    }
  }

  function cleanupSummary(cleanup) {
    const value = object(cleanup);
    return `Cleanup: ${Number(property(value, 'rulesDeactivated') || 0)} auto-claim rules deactivated; ` +
      `${Number(property(value, 'openTitleClaimsCleared') || 0)} open title claims cleared; ` +
      `${Number(property(value, 'openAdditionalCopyClaimsCleared') || 0)} open additional-copy claims cleared.`;
  }

  function validStaffLifecycleResponse(response, method, expectedStaffId) {
    const user = response?.user;
    const cleanup = response?.cleanup;
    const cleanupCounts = ['rulesDeactivated', 'openTitleClaimsCleared', 'openAdditionalCopyClaimsCleared'];
    return user !== null && typeof user === 'object' && !Array.isArray(user) &&
      isCanonicalPositiveInt64Id(user.id) &&
      typeof user.version === 'string' && user.version.length > 0 &&
      typeof user.active === 'boolean' && ['staff', 'admin', 'super_admin'].includes(user.role) &&
      Number.isSafeInteger(user.organizationId) && user.organizationId <= 2147483647 &&
      (user.role === 'super_admin' ? user.organizationId === 1 : user.organizationId > 1) &&
      ['userPrincipalName', 'displayName', 'notificationEmail'].every(key => user[key] === null || typeof user[key] === 'string') &&
      (expectedStaffId === null || user.id === String(expectedStaffId)) &&
      cleanup !== null && typeof cleanup === 'object' && !Array.isArray(cleanup) &&
      cleanupCounts.every(key => Number.isSafeInteger(cleanup[key]) && cleanup[key] >= 0) &&
      (method !== 'DELETE' || user.active === false) && (method !== 'POST' || user.active === true);
  }

  function setStaffStatus(message, kind = '') {
    if (!dom.staffStatus) return;
    const generation = ++staffStatusGeneration;
    dom.staffStatus.textContent = message || '';
    dom.staffStatus.className = `settings-result${kind ? ` ${kind}` : ''}`;
    return () => {
      if (staffStatusGeneration === generation) {
        dom.staffStatus.textContent = '';
        dom.staffStatus.className = 'settings-result';
      }
    };
  }

  function retireStaffEmailValidation(control) {
    const notice = staffEmailValidationNotice;
    if (!notice || notice.control !== control) return;
    staffEmailValidationNotice = null;
    if (isSettingsContextCurrent(notice.context)) notice.clear();
  }

  function revealStaffControl(control, context, sequence) {
    if (sequence !== staffFocusSequence || !isSettingsContextCurrent(context) || !state.visible || state.activePanel !== 'staff' ||
        state.pendingMutation || state.loading || dom.form.hidden || dom.form.inert || document.activeElement !== control ||
        !control.isConnected || !root.contains(control) || !control.closest('.settings-staff-roster') ||
        !control.matches('input, select, textarea, button') || control.matches(':disabled') || control.closest('[hidden], [inert]')) return;
    // A pointer is already over its button. Moving it during pointerdown can
    // lose the click; keyboard focus still needs the measured outline reveal.
    if (control.matches('button') && !control.matches(':focus-visible')) return;
    const view = root.ownerDocument.defaultView, bounds = control.getBoundingClientRect(), style = view.getComputedStyle(control);
    const clearance = Math.max(6, (parseFloat(style.outlineWidth) || 0) + (parseFloat(style.outlineOffset) || 0)) + 1;
    const viewport = view.visualViewport, top = (viewport?.offsetTop || 0) + clearance;
    const bottom = (viewport?.offsetTop || 0) + (viewport?.height || view.innerHeight) - clearance;
    if (!bounds.width || !bounds.height || bounds.height > bottom - top) return;
    const scroll = bounds.top < top ? bounds.top - top : Math.max(0, bounds.bottom - bottom);
    if (scroll) view.scrollBy({ top: scroll, behavior: 'instant' });
  }

  function queueStaffReveal(control) {
    if (!control?.matches?.('input, select, textarea, button') || !control.closest('.settings-staff-roster')) return;
    const context = captureSettingsContext(), sequence = ++staffFocusSequence;
    revealStaffControl(control, context, sequence);
    root.ownerDocument.defaultView.requestAnimationFrame?.(() => revealStaffControl(control, context, sequence));
  }

  function notify(message, kind = '') {
    const generation = ++notificationGeneration;
    dom.message.textContent = message || '';
    dom.message.className = `settings-message${kind ? ` ${kind}` : ''}`;
    const clearAnnouncement = announce(message || '', kind);
    return () => {
      if (notificationGeneration === generation) {
        dom.message.textContent = '';
        dom.message.className = 'settings-message';
      }
      if (typeof clearAnnouncement === 'function') clearAnnouncement();
    };
  }

  function hasSettingsDraft() {
    return state.data !== null && !state.awaitingReloadMutation &&
      (!sameSnapshot(state.baselineSnapshot, snapshotForm(dom.form)) || state.baselineOwnership !== ownershipSnapshot());
  }

  function ownershipSnapshot() {
    return JSON.stringify([[...state.scalarIntents], [...state.templateIntents], domainEditors.snapshot()]);
  }

  function isDirty() { return !disposed && drafts.isDirty(); }

  function hasStaffDraft() { return [...staffDrafts.values()].some(isDirty => isDirty()); }

  function registerStaffDraft(ownerRoot, controls, kind) {
    const baseline = controls.map(control => control.value);
    const dirty = () => ownerRoot.isConnected && state.staff !== null && !state.awaitingReloadMutation && controls.some((control, index) => control.value !== baseline[index]);
    const handle = drafts.register({ root: ownerRoot, kind, isDirty: dirty });
    staffDrafts.set(handle, dirty);
    return handle;
  }

  function resetStaffCreateDraft() {
    retireStaffEmailValidation(dom.staffEmail);
    drafts.release(staffCreateDraft); staffDrafts.delete(staffCreateDraft);
    dom.staffEmail.value = '';
    populateStaffCreateControls();
    staffCreateDraft = registerStaffDraft(dom.staffEmail.closest('fieldset'),
      [dom.staffEmail, dom.staffRole, dom.staffOrganization], 'new staff user');
  }

  function snapshotDrafts() {
    return ownershipSnapshot() + JSON.stringify([...dom.form.querySelectorAll('input, select, textarea')]
      .filter(control => control.id !== 'patron-code-picker' && control !== dom.staffShowInactive)
      .map(control => [control.value, control.checked]));
  }

  function updateDirtyState() {
    if (blockedDraftNotice) {
      const admission = drafts.admit(blockedDraftNotice.declaration);
      if (admission.allowed || admission.reason === 'invalid_owner' || admission.reason === 'invalid_preservation') clearBlockedDraftNotice();
    }
    const dirty = isDirty();
    const settingsDirty = hasSettingsDraft();
    const needsAttention = dirty || state.awaitingReload || state.outcomeUncertain || state.saving;
    dom.saveBar.classList.toggle('attention', needsAttention);
    dom.save.disabled = !settingsDirty || state.awaitingReload || state.saving || (isSystem() && !state.organizationsReady);
    dom.saveLogo.disabled = state.awaitingReload || Boolean(state.pendingMutation);
    dom.clearLogo.disabled = state.awaitingReload || Boolean(state.pendingMutation);
    dom.discard.hidden = !dirty;
    dom.saveTitle.textContent = state.outcomeUncertain ? 'Outcome uncertain; reload needed'
      : state.awaitingReload ? 'Saved; reload needed' : dirty ? 'Unsaved changes' : 'No changes';
    dom.saveDetail.textContent = state.outcomeUncertain
      ? 'The request may have committed. Reload current settings before retrying.'
      : state.awaitingReload
      ? 'The save committed. Reload current settings before editing again.'
      : isSystem() && !state.organizationsReady
      ? 'The organization list is unavailable or incomplete. Reload settings before saving.'
      : dirty
      ? settingsDirty ? 'Changes are local until you save this settings context.' : 'Save changes in each staff editor, or cancel the edits.'
      : 'Everything in this settings context is saved.';
    dom.reset.hidden = isSystem();
    dom.reset.disabled = state.awaitingReload || Boolean(state.pendingMutation);
    for (const row of staffRows.values()) row.sync();
    emailTest.sync();
  }

  function draftChanged(event) {
    if (event?.target === dom.staffShowInactive) return;
    drafts.touch(event?.target); updateDirtyState();
    if (event?.resetLabel) {
      drafts.touch();
      pendingInheritanceNotice?.();
      pendingInheritanceNotice = notify(`${event.resetLabel}: using the System value is pending. Save settings to apply.`, '');
    }
    queueStaffReveal(event?.target);
  }

  function rawSection(section) {
    const library = state.data?.stored?.libraryOverride;
    const system = state.data?.stored?.configuredSystem;
    if (section === 'workflow') return isSystem() ? system?.workflow : library?.workflow;
    if (section === 'patron') return isSystem() ? system?.patron : library?.patron;
    if (section === 'email') return isSystem() ? system?.email : library?.email;
    if (section === 'branding') return isSystem() ? system?.branding : library?.branding;
    return null;
  }

  function hasRawOverride(section, key) {
    return property(rawSection(section), key) !== null && property(rawSection(section), key) !== undefined;
  }

  function fieldInput(field) {
    return field.querySelector('input, textarea, select');
  }

  function applyTimeoutDependencies() {
    const outstandingEnabled = document.getElementById('outstanding-timeout-enabled').checked;
    const sendEmailEnabled = document.getElementById('outstanding-timeout-send-email').checked;
    const dependents = [
      ['outstanding-timeout-days', outstandingEnabled],
      ['outstanding-timeout-send-email', outstandingEnabled],
      ['outstanding-timeout-rejection-template-id', outstandingEnabled && sendEmailEnabled],
      ['hold-pickup-timeout-days', document.getElementById('hold-pickup-timeout-enabled').checked],
      ['pending-hold-timeout-days', document.getElementById('pending-hold-timeout-enabled').checked],
      ['additional-copy-timeout-days', document.getElementById('additional-copy-timeout-enabled').checked]
    ];
    for (const [id, ruleEnabled] of dependents) {
      const control = document.getElementById(id);
      const field = control.closest('[data-setting-section][data-setting-key]');
      control.disabled = !ruleEnabled;
    }
  }

  function commonAuthorsFocusTarget() {
    return !dom.commonAuthorsEnabled?.disabled ? dom.commonAuthorsEnabled : null;
  }

  function syncCommonAuthorsOptions(focusedBeforePopulate = false) {
    if (!dom.commonAuthorsEnabled || !dom.commonAuthorsOptions) return;
    const enabled = dom.commonAuthorsEnabled.checked;
    if (!enabled && (focusedBeforePopulate || dom.commonAuthorsOptions.contains(document.activeElement))) {
      if (dom.form.inert || dom.form.hidden) {
        pendingCommonAuthorsFocus = true;
      } else {
        commonAuthorsFocusTarget()?.focus({ preventScroll: true });
      }
    }
    dom.commonAuthorsOptions.disabled = !enabled;
    dom.commonAuthorsOptions.hidden = !enabled;
    if (enabled) pendingCommonAuthorsFocus = false;
  }

  function restoreCommonAuthorsFocus() {
    if (!pendingCommonAuthorsFocus || dom.form.inert || dom.form.hidden || !dom.commonAuthorsOptions.hidden) return;
    pendingCommonAuthorsFocus = false;
    commonAuthorsFocusTarget()?.focus({ preventScroll: true });
  }

  function inheritedScalar(section, key) {
    return (section === 'workflow' ? property(state.data?.systemValues?.workflowDefaults, key) : undefined) ??
      property(property(state.data?.systemValues, section), key) ??
      property(property(state.data?.stored?.configuredSystem, section), key);
  }

  function refreshScalarStatus(section, key) {
    const name = `${section}.${key}`;
    const intent = state.scalarIntents.get(name);
    for (const field of root.querySelectorAll(`[data-setting-section="${section}"][data-setting-key="${key}"]`)) {
      const comparisonOpen = field.querySelector('.settings-system-comparison')?.open;
      field.querySelector('.settings-inheritance')?.remove();
      const input = fieldInput(field);
      const fields = section === 'workflow' ? WORKFLOW_FIELDS : section === 'patron' ? PATRON_FIELDS : EMAIL_FIELDS;
      const kind = fields.find(item => item[0] === key)?.[2];
      const inherited = inheritedScalar(section, key);
      const normalize = value => kind === 'boolean' ? Boolean(value) : kind === 'number' ? value === null || value === undefined ? null : Number(value)
        : kind === 'message' ? (String(value ?? '').trim() ? String(value) : null)
        : typeof value === 'string' ? clean(value) : value ?? null;
      const inheritedTemplate = kind === 'id' ? templateRows(state.data).find(row => String(row.id) === String(inherited)) : null;
      const comparison = kind === 'id' ? [...input.options].find(option => option.value === stringValue(inherited))?.textContent ||
        inheritedTemplate?.displayName || (inherited ? 'Unavailable rejection template' : 'Use no rejection template') : normalize(inherited);
      field.append(inheritanceStatus({ owned: intent ? intent === 'pin' : hasRawOverride(section, key),
        value: readControl(input, kind), systemValue: comparison, pending: Boolean(intent), comparisonOpen,
        matchesSystem: kind === 'id' ? readControl(input, kind) === normalize(inherited) : undefined,
        label: field.querySelector('label, span')?.textContent || key,
        onReset: () => {
          if (!state.data || isSystem() || dom.form.inert || state.awaitingReload || !field.isConnected) return;
          state.scalarIntents.set(name, 'reset');
          if (kind === 'id') setRejectionTemplateOptions(state.data, { outstandingTimeoutRejectionTemplateId: inheritedScalar(section, key) });
          for (const sibling of root.querySelectorAll(`[data-setting-section="${section}"][data-setting-key="${key}"]`)) {
            const control = fieldInput(sibling);
            writeControl(control, inheritedScalar(section, key));
            state.scalarDisplays.set(control.id, control.type === 'checkbox' ? control.checked : control.value);
          }
          refreshScalarStatus(section, key);
          applyTimeoutDependencies();
          syncCommonAuthorsOptions();
          patronPlaceholders.refresh(); suggestionLimitPlaceholders.refresh(); placeholders.refresh();
          draftChanged({ target: input, resetLabel: field.querySelector('label, span')?.textContent || key }); input.focus();
        } }));
    }
  }

  function configureScalar(field, section, key) {
    if (field.localName === 'label') {
      // Reset is a separate action, outside the field's label.
      const control = fieldInput(field);
      const title = field.querySelector('span');
      const container = node('div');
      for (const attribute of field.attributes) container.setAttribute(attribute.name, attribute.value);
      container.append(...field.childNodes);
      field.replaceWith(container);
      const label = node('label', { for: control.id });
      title.before(label);
      label.append(title);
      field = container;
    }
    const control = fieldInput(field);
    if (control) {
      control.disabled = false;
      state.scalarDisplays.set(control.id, control.type === 'checkbox' ? control.checked : control.value);
    }
    refreshScalarStatus(section, key);
  }

  function configureScopedFields() {
    const system = isSystem();
    document.getElementById('email-clear-postmark-token').disabled = !system;
    for (const field of root.querySelectorAll('[data-setting-section][data-setting-key]')) {
      const section = field.dataset.settingSection;
      const key = field.dataset.settingKey;
      const input = fieldInput(field);
      const systemOnly = field.dataset.systemOnly === 'true';
      if (systemOnly) {
        if (input) input.disabled = !system;
        field.querySelector('.settings-inheritance')?.remove();
        continue;
      }
      if (!['workflow', 'patron', 'email', 'branding'].includes(section)) continue;
      if (system) {
        if (input) input.disabled = false;
        field.querySelector('.settings-inheritance')?.remove();
      } else {
        configureScalar(field, section, key);
      }
    }
    for (const fieldset of root.querySelectorAll('fieldset[data-system-only="true"]')) {
      fieldset.disabled = !system;
      const legend = fieldset.querySelector('legend');
      if (legend && !legend.querySelector('.settings-scope-badge')) {
        legend.append(node('span', { className: 'settings-scope-badge', text: 'System only' }));
      }
      let explanation = fieldset.previousElementSibling;
      if (!explanation?.classList.contains('settings-scope-explanation')) {
        explanation = node('p', { className: 'settings-callout settings-scope-explanation' });
        fieldset.before(explanation);
      }
      explanation.hidden = system;
      explanation.textContent = `System-level setting: ${legend?.firstChild?.textContent.trim() || 'These settings'} applies to all libraries and cannot be changed for ${state.data?.organization?.name || 'this library'}. ` +
        (state.staff?.role === 'super_admin' ? 'Use Switch to System level above to edit it.' : 'These settings are managed centrally.');
    }
    for (const button of dom.nav.filter(button => button.dataset.settingsSystemOnly === 'true')) {
      let badge = button.querySelector('.settings-scope-badge');
      if (!badge) {
        badge = node('span', { className: 'settings-scope-badge', text: 'System only' });
        button.append(badge);
      }
      badge.hidden = system;
    }
    applyTimeoutDependencies();
  }

  function setScopedFields(section, fields) {
    for (const [key, id, kind] of fields) {
      writeControl(document.getElementById(id), property(section, key));
    }
  }

  function setSystemFields(system) {
    for (const [key, id] of SYSTEM_FIELDS) writeControl(document.getElementById(id), property(system, key));
    const origins = property(system, 'patronEmbedAllowedOrigins');
    document.getElementById('patron-embed-allowed-origins').value = Array.isArray(origins) ? origins.join('\n') : '';
  }

  function setPolarisFields(polaris) {
    for (const [key, id] of POLARIS_FIELDS) writeControl(document.getElementById(id), property(polaris, key));
    const hasApiKey = property(polaris, 'hasApiKey') === true;
    const hasAdminPassword = property(polaris, 'hasAdminPassword') === true;
    document.getElementById('polaris-api-key').value = '';
    document.getElementById('polaris-api-key').placeholder = hasApiKey ? '********' : '';
    document.getElementById('polaris-admin-pass').value = '';
    document.getElementById('polaris-admin-pass').placeholder = hasAdminPassword ? '********' : '';
    document.getElementById('polaris-api-key-status').textContent = hasApiKey
      ? 'API key saved. Leave blank to keep the current key; enter a new key to replace it.'
      : 'No API key saved.';
    document.getElementById('polaris-admin-pass-status').textContent = hasAdminPassword
      ? 'System staff password saved. Leave blank to keep the current password; enter a new password to replace it.'
      : 'No system staff password saved.';
  }

  function renderPostmarkTokenStatus() {
    const clearSelected = dom.clearPostmarkToken.checked;
    dom.postmarkToken.placeholder = acceptedPostmarkToken && !clearSelected ? '********' : '';
    dom.postmarkTokenStatus.textContent = clearSelected
      ? acceptedPostmarkToken
        ? 'Postmark token removal is pending. Save settings to remove the stored token.'
        : 'Postmark token clear is selected. No token is currently stored; saving keeps it unconfigured.'
      : acceptedPostmarkToken
        ? 'A system Postmark token is stored. Leave blank to keep it; enter a new token to replace it.'
        : 'No system Postmark token is stored.';
  }

  function setSender(email) {
    const address = property(email, 'fromAddress');
    const name = property(email, 'fromName');
    writeControl(document.getElementById('email-from-address'), address);
    writeControl(document.getElementById('email-template-from-address'), address);
    writeControl(document.getElementById('email-from-name'), name);
    writeControl(document.getElementById('email-template-from-name'), name);
    document.getElementById('email-postmark-token').value = '';
    document.getElementById('email-clear-postmark-token').checked = false;
  }

  function setCollections(data) {
    domainEditors.populate(data, isSystem());
  }

  function findTemplate(rows, key) {
    return rows.find(row => row.templateKey === key);
  }

  function setTemplates(data) {
    const rows = templateEffectiveRows(data, organizationId());
    const raw = templateRows(data);
    const systemRows = raw.filter(row => Number(row.organizationId) === 1);
    const libraryRows = raw.filter(row => Number(row.organizationId) === organizationId());
    state.baselineTemplates.clear();
    for (const [key, prefix, subjectId, bodyId] of TEMPLATE_FIELDS) {
      const effective = findTemplate(rows, key) || {};
      writeControl(document.getElementById(subjectId), property(effective, 'subject'));
      writeControl(document.getElementById(bodyId), property(effective, 'body'));
      state.baselineTemplates.set(key, {
        // Native controls normalize line endings. Detect edits against what
        // staff actually see, so untouched raw SQL text is never rewritten.
        subject: document.getElementById(subjectId).value || null,
        body: document.getElementById(bodyId).value || null
      });
      const fieldset = root.querySelector(`[data-template-key="${key}"]`);
      const inherited = findTemplate(systemRows, key) || {};
      fieldset.dataset.templateInheritedSubject = inherited.subject || '';
      fieldset.dataset.templateInheritedBody = inherited.body || '';
      document.getElementById(subjectId).disabled = false;
      document.getElementById(bodyId).disabled = false;
      for (const [role, id] of [['subject', subjectId], ['body', bodyId]]) {
        const control = document.getElementById(id);
        state.scalarDisplays.set(control.id, control.value);
        fieldset.querySelector(`[data-template-status="${role}"]`)?.remove();
        if (!isSystem()) refreshBuiltinTemplateStatus(key, role, control);
      }
    }

  }

  function refreshBuiltinTemplateStatus(key, role, control) {
    const fieldset = control.closest('[data-template-key]');
    const comparisonOpen = fieldset.querySelector(`[data-template-status="${role}"] .settings-system-comparison`)?.open;
    fieldset.querySelector(`[data-template-status="${role}"]`)?.remove();
    const source = templateRows(state.data).find(row => Number(row.organizationId) === 1 && row.templateKey === key);
    const accepted = templateRows(state.data).find(row => Number(row.organizationId) === organizationId() && !row.isCustom &&
      String(row.sourceTemplateId) === String(source?.id));
    const intent = state.templateIntents.get(`${key}.${role}`);
    const inheritedControl = control.cloneNode(false);
    inheritedControl.value = source?.[role] ?? '';
    const status = inheritanceStatus({ owned: intent ? intent === 'pin' : accepted?.[role] !== null && accepted?.[role] !== undefined,
      value: control.value, systemValue: inheritedControl.value, comparisonOpen,
      pending: Boolean(intent), label: `${key} ${role}`, onReset: () => {
        if (dom.form.inert || state.awaitingReload || !control.isConnected) return;
        state.templateIntents.set(`${key}.${role}`, 'reset');
        control.value = source?.[role] ?? '';
        state.scalarDisplays.set(control.id, control.value);
        refreshBuiltinTemplateStatus(key, role, control);
        placeholders.refresh(); draftChanged({ target: control, resetLabel: `${fieldset.querySelector('legend')?.textContent || key} ${role}` }); control.focus();
      } });
    status.dataset.templateStatus = role;
    const wrapper = control.closest('.settings-template-field');
    if (wrapper) wrapper.append(status);
    else control.parentElement.after(status);
  }

  function setRejectionTemplateOptions(data, workflow) {
    const select = document.getElementById('outstanding-timeout-rejection-template-id');
    if (!select) return;
    const selected = clean(property(workflow, 'outstandingTimeoutRejectionTemplateId'));
    const raw = templateRows(data);
    const systemRows = raw.filter(row => Number(property(row, 'organizationId')) === 1 &&
      String(property(row, 'templateKey') || '').startsWith('rejection:'));
    const libraryRows = raw.filter(row => Number(property(row, 'organizationId')) === organizationId());
    const rows = [];
    for (const system of systemRows) {
      const override = organizationId() === 1 ? null : libraryRows.find(row =>
        property(row, 'isCustom') !== true &&
        String(property(row, 'sourceTemplateId') || '') === String(property(system, 'id') || ''));
      const subject = meaningful(property(override, 'subject')) ? property(override, 'subject') : property(system, 'subject');
      const body = meaningful(property(override, 'body')) ? property(override, 'body') : property(system, 'body');
      const enabled = property(system, 'enabled') !== false && property(override, 'enabled') !== false;
      if (enabled && meaningful(subject) && meaningful(body)) {
        rows.push({
          value: clean(property(system, 'id')),
          label: clean(property(override, 'displayName')) || clean(property(system, 'displayName')) || clean(property(system, 'templateKey'))
        });
      }
    }
    if (organizationId() !== 1) {
      for (const row of libraryRows.filter(item => property(item, 'isCustom') === true &&
        String(property(item, 'templateKey') || '').startsWith('rejection:'))) {
        if (property(row, 'enabled') === false || !meaningful(property(row, 'subject')) || !meaningful(property(row, 'body'))) continue;
        rows.push({
          value: clean(property(row, 'id')),
          label: clean(property(row, 'displayName')) || clean(property(row, 'templateKey'))
        });
      }
    }
    const eligibleRows = rows.filter(row => row.value && row.label);
    const unique = [];
    const seen = new Set();
    for (const row of eligibleRows) {
      if (seen.has(row.value)) continue;
      seen.add(row.value);
      unique.push(row);
    }
    select.replaceChildren(node('option', { value: '', text: 'Use no rejection template' }));
    for (const row of unique) select.append(node('option', { value: row.value, text: row.label }));
    if (selected && !seen.has(selected)) {
      select.append(node('option', { value: selected, text: `Unavailable rejection template (${selected})`, disabled: true }));
    }
    select.value = selected || '';
  }

  function renderOrganizations() {
    dom.organizations.replaceChildren();
    dom.referenceOrganizations.replaceChildren();
    const label = organization => `${organization.name}${organization.abbreviation ? ` (${organization.abbreviation})` : ''}`;
    const libraries = byDisplayLabel(state.organizations.filter(isLibraryOrganization), label);
    const activeCount = libraries.filter(organization => organization.isActive).length;
    dom.organizationStatus.textContent = libraries.length
      ? `${activeCount} of ${libraries.length} synchronized libraries active in ASAP; ${libraries.length - activeCount} inactive.`
      : 'No eligible libraries are synchronized. Sync organizations in Polaris to load library participation.';
    const organizationRow = (organization, parents = []) => {
      const isLibrary = isLibraryOrganization(organization);
      const active = isLibrary && Boolean(organization.isActive);
      const name = node('span', { className: 'settings-organization-name', text: label(organization) });
      if (parents.length) name.append(node('small', {
        className: 'settings-organization-parent', text: `Within: ${parents.map(label).join(' → ')}`
      }));
      const item = node('li', { className: 'settings-list-row', 'data-organization-id': organization.id }, [
        name,
        node('span', { className: `status-badge${active ? '' : ' blocked'}`, text: isLibrary ? active ? 'Active' : 'Inactive' : 'Reference only' })
      ]);
      if (isLibrary && state.staff?.role === 'super_admin') {
        const action = node('button', {
          type: 'button',
          className: 'secondary-button',
          title: active ? `Deactivate ${organization.name}` : `Activate ${organization.name}`,
          'aria-label': active ? `Deactivate ${organization.name}` : `Activate ${organization.name}`
        }, [node('i', { className: `fa fa-${active ? 'ban' : 'check'}`, 'aria-hidden': 'true' })]);
        listen(action, 'click', () => setOrganizationActive(organization, !active, action));
        item.append(action);
      }
      return item;
    };
    for (const library of libraries) dom.organizations.append(organizationRow(library));
    if (!libraries.length) {
      dom.organizations.append(node('li', { className: 'settings-empty', text: 'No eligible library organizations are synchronized.' }));
    }

    const organizationsById = new Map(state.organizations.map(organization => [organization.id, organization]));
    const groups = new Map(), unassociated = [];
    for (const organization of byDisplayLabel(state.organizations.filter(item => !isLibraryOrganization(item)), label)) {
      const visited = new Set([organization.id]), parents = [];
      let parentId = organization.parentOrganizationId, library = null;
      while (parentId !== null && !visited.has(parentId)) {
        visited.add(parentId);
        const parent = organizationsById.get(parentId);
        if (!parent) break;
        if (isLibraryOrganization(parent)) {
          library = parent;
          break;
        }
        parents.unshift(parent);
        parentId = parent.parentOrganizationId;
      }
      if (!library) {
        unassociated.push(organizationRow(organization));
        continue;
      }
      if (!groups.has(library.id)) groups.set(library.id, { library, rows: [] });
      groups.get(library.id).rows.push(organizationRow(organization, parents));
    }
    const appendReferenceGroup = (title, rows, libraryId) => {
      const headingId = `settings-reference-group-${libraryId || 'other'}`;
      dom.referenceOrganizations.append(node('section', {
        className: 'settings-organization-reference-group', 'aria-labelledby': headingId,
        'data-library-organization-id': libraryId || 'other'
      }, [
        node('h4', { id: headingId, text: title }),
        node('ul', { className: 'settings-list' }, rows)
      ]));
    };
    for (const group of byDisplayLabel([...groups.values()], group => label(group.library), group => group.library.id)) {
      appendReferenceGroup(label(group.library), group.rows, group.library.id);
    }
    if (unassociated.length) appendReferenceGroup('Other / unassociated organizations', unassociated);
    if (!groups.size && !unassociated.length) {
      dom.referenceOrganizations.append(node('p', { className: 'settings-empty', text: 'No other synchronized organizations.' }));
    }
  }

  function populateStaffCreateControls() {
    if (!dom.staffRole || !dom.staffOrganization) return;
    const currentRole = roleChoices().some(item => item.value === dom.staffRole.value)
      ? dom.staffRole.value
      : 'staff';
    replaceSelectOptions(dom.staffRole, roleChoices(), currentRole);
    const currentOrganization = dom.staffOrganization.value || scopedStaffOrganizationId();
    replaceSelectOptions(dom.staffOrganization, organizationChoices(dom.staffRole.value), currentOrganization);
    dom.staffOrganization.disabled = dom.staffRole.value === 'super_admin' ||
      state.staff?.role !== 'super_admin';
  }

  function staffUserDisplay(user) {
    return clean(property(user, 'displayName')) ||
      clean(property(user, 'userPrincipalName')) ||
      `Staff ${stringValue(property(user, 'id')) || '?'}`;
  }

  function staffUserOrganizationName(user) {
    const id = clean(property(user, 'organizationId'));
    const organization = state.organizations.find(item => String(item.id) === String(id));
    return organization?.name || (id === '1' ? 'System level' : `Library ${id || '?'}`);
  }

  function userRoleChoices(user) {
    const current = clean(property(user, 'role')) || 'staff';
    const choices = roleChoices();
    if (!choices.some(item => item.value === current)) choices.unshift({ value: current, label: roleLabel(current) });
    return choices;
  }

  // DisplayName is unstructured: use its final nonempty component, then the
  // complete name/email/opaque ID. This is a sort convention, not a name parser.
  function staffRosterOrder(first, second) {
    const key = user => {
      const name = clean(property(user, 'displayName')) || '';
      const email = clean(property(user, 'userPrincipalName')) || '';
      return [name.split(/\s+/).at(-1) || email, name || email, email, stringValue(property(user, 'id'))];
    };
    const activeOrder = Number(property(first, 'active') === false) - Number(property(second, 'active') === false);
    if (activeOrder) return activeOrder;
    const firstKey = key(first), secondKey = key(second);
    for (let index = 0; index < firstKey.length; index += 1) {
      const order = firstKey[index].localeCompare(secondKey[index], 'en', { sensitivity: 'base' }) ||
        firstKey[index].localeCompare(secondKey[index], 'en', { sensitivity: 'variant' });
      if (order) return order;
    }
    return 0;
  }

  function setStaffCreateOpen(open, focus = false) {
    if (!open) retireStaffEmailValidation(dom.staffEmail);
    dom.staffCreateForm.hidden = !open;
    dom.staffCreateForm.disabled = !open || state.activePanel !== 'staff';
    dom.staffCreateToggle.setAttribute('aria-expanded', String(open));
    if (focus) (open ? dom.staffEmail : dom.staffCreateToggle).focus();
  }

  function createStaffRow(user) {
    const id = stringValue(property(user, 'id'));
    const row = node('article', { className: 'settings-staff-row', 'data-staff-id': id });
    const name = node('strong'), emailLabel = node('span', { className: 'settings-staff-email' });
    const unsaved = node('span', { className: 'settings-staff-unsaved', text: 'Unsaved changes' });
    const roleLabelNode = node('span', { className: 'settings-staff-role' });
    const libraryLabel = node('span', { className: 'settings-staff-library' });
    const status = node('span');
    const summary = node('button', { type: 'button', className: 'settings-staff-summary settings-staff-columns',
      'aria-expanded': 'false', 'aria-controls': `staff-editor-${id}` }, [
      node('span', { className: 'settings-staff-heading' }, [name, emailLabel, unsaved]),
      roleLabelNode, libraryLabel, status,
      node('span', { className: 'settings-staff-chevron', 'aria-hidden': 'true', text: '⌄' })
    ]);
    const editor = node('fieldset', { className: 'settings-staff-editor', id: `staff-editor-${id}`, 'aria-label': 'Staff editor' });
    editor.hidden = true;
    editor.disabled = true;
    const email = node('input', { type: 'email', maxlength: '320', 'aria-describedby': `staff-email-help-${id}` });
    const displayName = node('input', { type: 'text', maxlength: '256' });
    const role = node('select'), organization = node('select');
    const controls = [email, displayName, role, organization];
    const cancel = node('button', { type: 'button', className: 'secondary-button', text: 'Cancel' });
    const save = node('button', { type: 'button', className: 'primary-button', text: 'Save changes' });
    const lifecycle = node('button', { type: 'button' });
    const notice = node('p', { className: 'settings-result', role: 'status', 'aria-live': 'polite' });
    const syncHelp = node('p', { className: 'settings-staff-sync-help',
      text: 'Save changes to also use this email for primary notifications.' });
    const cleanup = node('p', { className: 'settings-cleanup-result' });
    const field = (label, input, help = null) => node('label', { className: 'settings-field' },
      [node('span', { text: label }), input, help]);
    editor.append(node('h4', { text: 'Profile' }), node('div', { className: 'settings-staff-controls' }, [
      field('Email address', email, node('small', { id: `staff-email-help-${id}`, text: 'Used for sign-in and notifications.' })),
      field('Display name', displayName), field('Role', role), field('Library', organization)
    ]), syncHelp, node('div', { className: 'settings-staff-footer' }, [lifecycle,
      node('div', { className: 'settings-staff-actions' }, [cancel, save])]), notice, cleanup);
    row.append(summary, editor);
    const record = { id, row, summary, user, version: '', baseline: [], conflict: false,
      dirty: () => controls.some((control, index) => control.value !== record.baseline[index]),
      sync() {
        const dirty = record.dirty();
        const hidden = property(record.user, 'active') === false && !dom.staffShowInactive.checked;
        const returnToFilter = hidden && row.contains(document.activeElement);
        if (hidden) retireStaffEmailValidation(email);
        row.hidden = hidden;
        editor.disabled = editor.hidden || row.hidden || state.activePanel !== 'staff';
        unsaved.hidden = !dirty;
        syncHelp.hidden = clean(property(record.user, 'userPrincipalName')) === clean(property(record.user, 'notificationEmail'));
        save.disabled = (!dirty && syncHelp.hidden) || record.conflict || state.awaitingReload || Boolean(state.pendingMutation);
        cancel.disabled = !dirty && !record.conflict;
        if (returnToFilter) dom.staffShowInactive.focus();
      },
      reset() {
        retireStaffEmailValidation(email);
        email.value = property(record.user, 'userPrincipalName') || '';
        displayName.value = property(record.user, 'displayName') || '';
        replaceSelectOptions(role, userRoleChoices(record.user), property(record.user, 'role') || 'staff');
        replaceSelectOptions(organization, organizationChoices(role.value), property(record.user, 'organizationId'));
        organization.disabled = role.value === 'super_admin' || state.staff?.role !== 'super_admin';
        record.version = stringValue(property(record.user, 'version'));
        record.baseline = controls.map(control => control.value);
        record.conflict = false;
        notice.textContent = '';
        record.sync();
      },
      accept(next, discard = false) {
        const dirty = record.dirty();
        record.unavailable = false;
        record.user = next;
        const active = property(next, 'active') !== false;
        row.classList.toggle('inactive', !active);
        name.textContent = staffUserDisplay(next);
        emailLabel.textContent = stringValue(property(next, 'userPrincipalName'));
        roleLabelNode.textContent = roleLabel(property(next, 'role'));
        libraryLabel.textContent = staffUserOrganizationName(next);
        status.textContent = active ? 'Active' : 'Inactive';
        status.className = `status-badge${active ? '' : ' blocked'}`;
        lifecycle.textContent = active ? 'Deactivate' : 'Reactivate';
        lifecycle.className = active ? 'danger-button' : 'secondary-button';
        for (const [input, label] of [[email, 'Email address'], [displayName, 'Display name'], [role, 'Role'], [organization, 'Library']]) {
          input.setAttribute('aria-label', `${label} for ${staffUserDisplay(next)}`);
        }
        if (!dirty || discard) record.reset();
        else if (record.version !== stringValue(property(next, 'version'))) {
          record.conflict = true;
          notice.textContent = 'Saved values changed elsewhere. Your draft is retained. Cancel to load the saved values before editing again.';
        }
        cleanup.hidden = state.lastStaffCleanup?.staffId !== id;
        cleanup.textContent = cleanup.hidden ? '' : cleanupSummary(state.lastStaffCleanup.cleanup);
        record.sync();
      },
      unavailable: false,
      markUnavailable() {
        record.unavailable = true;
        record.conflict = true;
        notice.textContent = 'This account is no longer in the current roster. Your draft is retained. Cancel to discard this draft.';
        record.sync();
      }
    };
    const setOpen = open => {
      if (!open) retireStaffEmailValidation(email);
      const returnToSummary = !open && editor.contains(document.activeElement);
      editor.hidden = !open;
      summary.setAttribute('aria-expanded', String(open));
      record.sync();
      if (returnToSummary) summary.focus();
    };
    listen(summary, 'click', () => setOpen(editor.hidden));
    listen(editor, 'keydown', event => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      event.preventDefault(); event.stopPropagation(); setOpen(false);
    });
    listen(role, 'change', () => {
      replaceSelectOptions(organization, organizationChoices(role.value), organization.value);
      organization.disabled = role.value === 'super_admin' || state.staff?.role !== 'super_admin';
      record.sync();
    });
    listen(cancel, 'click', () => {
      if (record.unavailable) {
        drafts.release(record.draft); staffDrafts.delete(record.draft); rosterDrafts.delete(record.draft);
        staffRows.delete(id); row.remove();
        drafts.touch(); updateDirtyState(); dom.staffRefresh.focus();
        setStaffStatus('Draft discarded. The account is no longer in the current roster.');
        return;
      }
      record.reset(); drafts.touch(); updateDirtyState();
      notice.textContent = 'Unsaved edits discarded.';
      email.focus();
    });
    listen(save, 'click', () => {
      if (record.conflict || !validateStaffValues(email, role, organization)) return;
      void updateStaffUser(id, record.version, { email: clean(email.value), displayName: clean(displayName.value),
        role: role.value, organizationId: role.value === 'super_admin' ? 1 : Number(organization.value) }, record.draft);
    });
    listen(lifecycle, 'click', () => {
      if (property(record.user, 'active') !== false) void deactivateStaffUser(id, record.version, staffUserDisplay(record.user));
      else void reactivateStaffUser(record.user, record.version);
    });
    record.reset();
    // The baseline changes only after this account's accepted save/cancel/reload.
    record.draft = drafts.register({ root: row, kind: 'staff editor', isDirty: () =>
      row.isConnected && state.staff !== null && (!state.awaitingReloadMutation ||
        state.awaitingReloadMutation.preserves?.includes(record.draft)) && record.dirty() });
    staffDrafts.set(record.draft, () => row.isConnected && (!state.awaitingReloadMutation ||
      state.awaitingReloadMutation.preserves?.includes(record.draft)) && record.dirty());
    rosterDrafts.add(record.draft);
    record.accept(user, true);
    return record;
  }

  function renderStaffUsers({ discard = false } = {}) {
    if (!dom.staffUsers) return;
    const focused = dom.staffUsers.contains(document.activeElement) ? document.activeElement : null;
    const ids = new Set(state.staffUsers.map(user => stringValue(property(user, 'id'))));
    for (const [id, record] of staffRows) {
      if (ids.has(id)) continue;
      if (!discard && record.dirty()) {
        record.markUnavailable();
        continue;
      }
      drafts.release(record.draft); staffDrafts.delete(record.draft); rosterDrafts.delete(record.draft);
      record.row.remove(); staffRows.delete(id);
    }
    populateStaffCreateControls();
    if (!staffCreateDraft) resetStaffCreateDraft();
    for (const user of [...state.staffUsers].sort(staffRosterOrder)) {
      const id = stringValue(property(user, 'id'));
      const record = staffRows.get(id) || createStaffRow(user);
      staffRows.set(id, record);
      record.accept(user, discard);
    }
    const ordered = [...staffRows.values()].sort((first, second) => staffRosterOrder(first.user, second.user));
    ordered.forEach((record, index) => {
      if (dom.staffUsers.children[index] !== record.row) dom.staffUsers.insertBefore(record.row, dom.staffUsers.children[index] || null);
    });
    updateStaffVisibility();
    if (focused?.isConnected && !focused.closest('[hidden]') && document.activeElement !== focused) focused.focus({ preventScroll: true });
    else if (focused && !focused.isConnected) dom.staffRefresh.focus({ preventScroll: true });
  }

  function updateStaffVisibility() {
    for (const record of staffRows.values()) record.sync();
    const active = state.staffUsers.filter(user => property(user, 'active') !== false).length;
    const inactive = state.staffUsers.length - active;
    dom.staffCounts.textContent = state.staffAccessLoaded
      ? `${active} active ${active === 1 ? 'account' : 'accounts'} · ${inactive} inactive ${dom.staffShowInactive.checked
        ? `${inactive === 1 ? 'account' : 'accounts'} shown` : 'hidden'}` : '';
    dom.staffUsers.querySelector('.settings-empty')?.remove();
    if (state.staffAccessLoaded && state.staffUsers.length > 0 && (active > 0 || dom.staffShowInactive.checked)) return;
    const message = !state.staffAccessLoaded ? 'Open or refresh the roster to load staff users.'
      : state.staffUsers.length === 0 ? `No staff users found for ${scopedStaffLabel()}.`
      : `No active staff accounts for ${scopedStaffLabel()}. Select Show inactive accounts to view ${inactive === 1 ? 'the inactive account' : 'inactive accounts'}.`;
    dom.staffUsers.append(node('p', { className: 'settings-empty', text: message }));
  }

  function populate(data) {
    pendingInheritanceNotice?.(); pendingInheritanceNotice = null;
    const commonAuthorsOptionsHadFocus = dom.commonAuthorsOptions?.contains(document.activeElement) === true;
    pendingCommonAuthorsFocus = false;
    clearPolarisFeedback();
    releaseLogoDraft();
    dom.logo.value = '';
    dom.discardLogoDraft.hidden = true;
    state.data = data;
    state.scalarIntents.clear(); state.templateIntents.clear(); state.scalarDisplays.clear();
    state.scope = String(data.orgId || state.scope);
    const stored = object(data.stored);
    const configuredSystem = object(stored.configuredSystem);
    const libraryOverride = object(stored.libraryOverride);
    const systemSettings = object(stored.systemSettings);
    renderCurrentStaffUrlSuggestion();
    const effective = object(data.effective);
    const effectivePatron = object(data.ui_text || effective);
    const systemWorkflow = { ...mergeConfigured(configuredSystem.workflow, null), ...data.systemValues?.workflow,
      ...data.systemValues?.workflowDefaults };
    const workflow = isSystem()
      ? systemWorkflow
      : mergeConfigured(systemWorkflow, libraryOverride.workflow, data.workflow);
    state.savedRejectionTemplateId = stringValue(property(workflow, 'outstandingTimeoutRejectionTemplateId'));
    const systemPatron = data.systemValues?.patron || mergeConfigured(configuredSystem.patron, null, effectivePatron);
    const patron = isSystem()
      ? systemPatron
      : mergeConfigured(systemPatron, libraryOverride.patron, effectivePatron);
    const email = isSystem()
      ? mergeConfigured(configuredSystem.email, null, data.emails)
      : mergeConfigured(configuredSystem.email, libraryOverride.email, data.emails);

    dom.version.value = data.version || '';
    setSystemFields(systemSettings);
    setPolarisFields(configuredSystem.polaris || stored.polaris);
    for (const [key, id, kind] of WORKFLOW_FIELDS) writeControl(document.getElementById(id), property(workflow, key));
    setScopedFields(patron, PATRON_FIELDS);
    setSender(email);
    acceptedPostmarkToken = property(configuredSystem.email, 'hasPostmarkToken') === true;
    renderPostmarkTokenStatus();
    setCollections(data);
    renderPublicLinks(data);
    writeControl(document.getElementById('branding-alt'), property(effective, 'logoAltText'));
    setTemplates(data);
    placeholders.populate(data.templatePlaceholders);
    setRejectionTemplateOptions(data, workflow);

    configureScopedFields();
    syncCommonAuthorsOptions(commonAuthorsOptionsHadFocus);
    patronPlaceholders.refresh();
    suggestionLimitPlaceholders.refresh();
    dom.systemContext.hidden = isSystem();
    root.querySelector('#settings-library-context-title').textContent = `Editing ${data.organization?.name || 'this library'} settings`;
    root.querySelector('#settings-library-context-guidance').textContent =
      'Library-specific settings can be changed here. Settings marked System only apply to every library and can only be changed at System level.' +
      (state.staff?.role === 'super_admin' ? '' : ' System-only settings are managed centrally.');
    dom.switchSystem.hidden = state.staff?.role !== 'super_admin';
    dom.clearLogo.textContent = isSystem() ? 'Clear system image' : 'Clear image (use inherited image)';
    renderBrandingPreview();
    renderOrganizations();
    resetStaffCreateDraft();
    renderStaffUsers({ discard: true });
    dom.scope.value = state.scope;
    state.baselineSnapshot = snapshotForm(dom.form);
    state.baselineOwnership = ownershipSnapshot();
    updateDirtyState();
  }

  function currentScopeForRequest() {
    return encodeURIComponent(state.scope || 'system');
  }

  async function retryPatronCodes() {
    if (disposed || state.loading || state.pendingMutation || state.awaitingReload) return;
    const context = captureSettingsContext();
    const owner = state.data;
    const operation = reads.begin('administration-patron-codes');
    try {
      const response = await authorizedJson(`/api/asap/staff/polaris/patron-codes?orgId=${currentScopeForRequest()}`,
        { signal: operation.signal });
      if (!operation.isCurrent() || !isSettingsContextCurrent(context) || !state.visible ||
          state.data !== owner || state.loading || state.pendingMutation || state.awaitingReload) return;
      domainEditors.setCodeChoices(response?.data ?? response);
    } catch (error) {
      if (operation.isCurrent() && isSettingsContextCurrent(context) && state.data === owner &&
          !isAbortError(error) && error.status !== 401) notify(error.message || 'Patron-code lookup unavailable. Retry the lookup.', 'error');
    } finally {
      reads.finish('administration-patron-codes', operation.token);
    }
  }

  async function load(options = {}) {
    if (disposed || !state.staff || state.staff.role === 'staff') return;
    if (state.pendingMutation && state.pendingMutation !== options.owner) return false;
    if (options.owner !== polarisTest && options.owner?.slot !== 'administration-settings-organization-sync') cancelPolarisTest();
    const report = options.report || notify;
    reads.begin('administration-patron-codes').abort();
    const context = captureSettingsContext();
    state.loading = true;
    emailTest.invalidate();
    placeholders.clear(); patronPlaceholders.clear(); suggestionLimitPlaceholders.refresh();
    const snapshot = snapshotDrafts();
    const loadState = reads.begin('administration-settings');
    state.organizationsReady = false;
    updateDirtyState();
    dom.refresh.disabled = true;
    const clearLoading = !options.silent ? notify('Loading settings...') : null;
    try {
      const [settingsResponse, organizationsResponse, patronCodesResponse] = await Promise.all([
        authorizedJson(`/api/asap/staff/settings?orgId=${currentScopeForRequest()}`, { signal: loadState.signal }),
        authorizedJson('/api/asap/staff/organizations', { signal: loadState.signal }),
        authorizedJson(`/api/asap/staff/polaris/patron-codes?orgId=${currentScopeForRequest()}`, { signal: loadState.signal })
          .catch(error => {
            if (isAbortError(error) || error.status === 401) throw error;
            return null;
          })
      ]);
      if (!loadState.isCurrent() || !isSettingsContextCurrent(context) || !state.visible ||
          state.pendingMutation && state.pendingMutation !== options.owner) return false;
      if (snapshot !== snapshotDrafts()) {
        report('Settings edits changed during reload. Save or discard the edits before reloading again.', 'warning');
        return false;
      }
      const data = settingsResponse?.data && settingsResponse?.version === undefined
        ? settingsResponse.data
        : settingsResponse;
      const organizations = validateOrganizationCatalog(organizationsResponse, context.scope, data);
      acceptOrganizationCatalog(organizations);
      populateScopeOptions();
      const patronCodeChoices = patronCodesResponse?.data ?? patronCodesResponse;
      data.patronCodeChoices = Array.isArray(patronCodeChoices) ? patronCodeChoices : [];
      data.patronCodeChoicesUnavailable = !Array.isArray(patronCodeChoices);
      const wasHidden = dom.form.hidden;
      const review = state.unconfirmedMutation || state.awaitingReloadMutation;
      const staffReview = review?.slot === 'administration-staff-mutation';
      if (!staffReview) {
        state.awaitingReload = false; state.awaitingReloadMutation = null; state.outcomeUncertain = false; state.unconfirmedMutation = null;
      }
      populate(data || {});
      onRefreshed(context.owner, { kind: 'settings', scope: context.scope,
        changed: !staffReview && review?.outcome === 'uncertain',
        catalogChanged: review?.outcome === 'uncertain' &&
          ['administration-settings-organization-sync', 'administration-settings-participation'].includes(review.slot) });
      dom.form.hidden = false;
      dom.form.inert = Boolean(state.pendingMutation || state.awaitingReload);
      restoreCommonAuthorsFocus();
      patronPlaceholders.refresh();
      if (wasHidden) dom.panels.find(item => item.dataset.settingsPanelContent === state.activePanel)?.focus({ preventScroll: true });
      if (staffReview) {
        const reviewed = await loadStaffAccess({ silent: true, discard: true, owner: options.owner });
        if (!loadState.isCurrent() || !isSettingsContextCurrent(context)) return false;
        if (!reviewed) {
          notify(state.outcomeUncertain
            ? 'The staff change is still uncertain. Reload the roster to review saved staff access before retrying.'
            : 'The staff change committed. Reload the roster to review saved staff access before editing again.', 'warning');
          return false;
        }
      } else if (state.activePanel === 'staff') void loadStaffAccess({ silent: true });
      return true;
    } catch (error) {
      if (loadState.isCurrent() && isSettingsContextCurrent(context) && state.visible && !isAbortError(error) && error.status !== 401) {
        report(state.outcomeUncertain
          ? 'Settings change outcome is still uncertain. Current values could not be reloaded; try again before editing.'
          : error.message || 'Settings could not be loaded.', 'error');
      }
      return false;
    } finally {
      clearLoading?.();
      if (loadState.isCurrent() && isSettingsContextCurrent(context)) {
        state.loading = false; dom.refresh.disabled = false; emailTest.sync(); placeholders.refresh(); suggestionLimitPlaceholders.refresh();
      }
      reads.finish('administration-settings', loadState.token);
    }
  }

  function staffUsersUrl() {
    const scope = scopedStaffOrganizationId();
    return scope ? `/api/asap/staff/users?orgId=${encodeURIComponent(scope)}` : '/api/asap/staff/users';
  }

  async function loadStaffAccess(options = {}) {
    if (disposed || !state.staff || state.staff.role === 'staff') return false;
    if (state.pendingMutation && state.pendingMutation !== options.owner) return false;
    if (hasStaffDraft() && !options.discard && !options.preserve) return false;
    const snapshot = snapshotDrafts();
    const loadState = beginSettingsOperation('administration-staff-access');
    const clearLoading = !options.silent ? setStaffStatus(`Loading staff access for ${scopedStaffLabel()}...`) : null;
    if (dom.staffRefresh) dom.staffRefresh.disabled = true;
    try {
      const review = state.unconfirmedMutation || state.awaitingReloadMutation;
      if (review?.slot === 'administration-staff-mutation' && String(review.staffId) === String(loadState.context.owner.id)) {
        if (!await refreshCurrentStaff(loadState.context.owner) || !isSettingsOperationCurrent(loadState)) return false;
      }
      const usersResponse = await authorizedJson(staffUsersUrl(), { signal: loadState.signal });
      if (!isSettingsOperationCurrent(loadState)) return false;
      if (snapshot !== snapshotDrafts()) {
        setStaffStatus('Edits changed during roster reload. Save or discard the edits before reloading again.', 'warning');
        return false;
      }
      const users = usersResponse?.data ?? usersResponse ?? {};
      state.staffUsers = Array.isArray(users.users) ? users.users : [];
      state.staffCanAssignSuperAdmin = Boolean(users.canAssignSuperAdmin);
      state.staffAccessLoaded = true;
      if (review?.slot === 'administration-staff-mutation') {
        state.unconfirmedMutation = null; state.outcomeUncertain = false; state.awaitingReload = false; state.awaitingReloadMutation = null;
      }
      dom.form.inert = Boolean(state.pendingMutation || state.awaitingReload);
      if (options.discard) resetStaffCreateDraft();
      renderStaffUsers({ discard: options.discard === true });
      updateDirtyState();
      onRefreshed(loadState.context.owner, { kind: 'staff', scope: loadState.context.scope,
        changed: review?.slot === 'administration-staff-mutation' });
      return true;
    } catch (error) {
      if (isSettingsOperationCurrent(loadState) && !isAbortError(error) && error.status !== 401) {
        setStaffStatus(error.message || 'Staff access could not be loaded.', 'error');
      }
      return false;
    } finally {
      clearLoading?.();
      if (isSettingsOperationCurrent(loadState)) {
        if (dom.staffRefresh) dom.staffRefresh.disabled = false;
      }
      reads.finish('administration-staff-access', loadState.token);
    }
  }

  async function mutateStaffUser(path, options, successMessage, staffId, consumes) {
    const focused = document.activeElement;
    const expectedActive = property(staffRows.get(String(staffId))?.user, 'active') !== false;
    // Only a mounted roster PUT preserves other mounted roster drafts. A change
    // to the actor's session identity retires those owners, so it must wait.
    const keepsActor = String(staffId) !== String(state.staff?.id) ||
      (clean(options.body.email)?.toLowerCase() === clean(state.staff?.authenticationEmail)?.toLowerCase() &&
        options.body.role === state.staff?.role && Number(options.body.organizationId) === Number(state.staff?.organizationId));
    const preserves = options.method === 'PUT' && rosterDrafts.has(consumes) && keepsActor
      ? [...staffRows.values()].filter(record => record.row.isConnected && record.dirty() && record.draft !== consumes).map(record => record.draft) : [];
    const mutation = beginSettingsMutation('administration-staff-mutation', consumes, preserves);
    if (!mutation) return;
    mutation.staffId = staffId;
    setStaffStatus(options.method === 'PUT' ? 'Saving staff changes...' : options.method === 'DELETE' ? 'Deactivating staff user...' : 'Saving staff access...');
    try {
      const response = await authorizedJson(path, options);
      if (!validStaffLifecycleResponse(response, options.method, staffId)) throw unconfirmedResponseError();
      if (options.method === 'PUT' && !validStaffUpdateResponse(response.user, options.body, expectedActive)) throw unconfirmedResponseError();
      const cleanup = response.cleanup;
      const committedMessage = `${successMessage} ${cleanupSummary(cleanup)}`;
      recordCommitted(mutation, committedMessage);
      onStaffAccessCommitted(mutation.owner);
      if (!isSettingsOperationCurrent(mutation)) return null;
      if (consumes === staffCreateDraft) {
        resetStaffCreateDraft(); setStaffCreateOpen(false, true);
      }
      state.lastStaffCleanup = { staffId: stringValue(property(response?.user ?? response?.data?.user, 'id') || staffId), cleanup };
      const record = staffRows.get(String(staffId));
      if (record) record.accept(response.user, true);
      // The validated receipt is accepted state even if the following GET
      // fails. Keep its counts and membership within the current roster scope.
      state.staffUsers = state.staffUsers.filter(user => String(property(user, 'id')) !== response.user.id);
      const scope = scopedStaffOrganizationId();
      if (!scope || String(response.user.organizationId) === scope) state.staffUsers.push(response.user);
      renderStaffUsers();
      const refreshed = await loadStaffAccess({ silent: true, owner: mutation, preserve: true });
      if (isSettingsOperationCurrent(mutation)) setStaffStatus(refreshed
        ? committedMessage : `${committedMessage} Staff access could not be refreshed.`,
      refreshed ? 'success' : 'error');
      return isSettingsOperationCurrent(mutation) ? response : null;
    } catch (error) {
      const failureMessage = staffMutationFailure(error, options.method);
      recordFailure(mutation, error, failureMessage);
      if (!isSettingsOperationCurrent(mutation)) return null;
      if (mutation.outcome === 'committed') {
        setStaffStatus(`${successMessage} Current staff session and roster could not be refreshed. Reload before editing again.`, 'error');
        return null;
      }
      if (error.status === 409) {
        await loadStaffAccess({ silent: true, owner: mutation, preserve: true });
        if (!isSettingsOperationCurrent(mutation)) return null;
        setStaffStatus(failureMessage, 'error');
      } else if (error.status !== 401 && !isAbortError(error)) {
        setStaffStatus(failureMessage, 'error');
      }
      return null;
    } finally {
      finishSettingsMutation(mutation);
      if (isSettingsOperationCurrent(mutation)) {
        updateDirtyState();
        if (mutation.outcome === 'committed' && !state.awaitingReload && state.activePanel === 'staff' &&
            (document.activeElement === document.body || document.activeElement === focused)) {
          const record = staffRows.get(String(staffId));
          const fallback = staffId === null ? dom.staffCreateToggle
            : record?.row.hidden ? dom.staffShowInactive : record?.summary || dom.staffRefresh;
          const target = focused?.isConnected && focused !== document.body && !focused.matches(':disabled') &&
            !focused.closest('[hidden]') ? focused : fallback;
          target?.focus({ preventScroll: true });
        }
      }
    }
  }

  function staffMutationFailure(error, method) {
    const action = method === 'DELETE' ? 'Staff deactivation' : method === 'PUT' ? 'Staff save' : 'Staff access change';
    const status = Number.isInteger(error.status) && error.status >= 100 && error.status <= 599 ? ` (HTTP ${error.status})` : '';
    const details = {
      stale_version: 'Staff access changed elsewhere. Review the refreshed roster and your draft.',
      staff_scope_forbidden: 'Your current access does not permit this staff change.',
      active_super_admin_required: 'At least one currently usable super administrator must remain.',
      identity_already_exists: 'This staff identity already exists or is already active.',
      staff_invariant_busy: 'Another staff access change is in progress.',
      organization_inactive: 'Select an active library for this access change.',
      invalid_version: 'Refresh roster to obtain the current staff version.'
    };
    const detail = details[error.response?.code] || error.response?.message ||
      (error.status === 405 ? 'The server or hosting layer rejected this request method.' : 'The server did not confirm this staff change.');
    const reference = error.correlationId ? ` Reference: ${error.correlationId}.` : '';
    return isUnconfirmedMutationFailure(error)
      ? `${action} outcome is uncertain${status}. ${detail} Use Refresh settings above to reconcile current values before retrying.${reference}`
      : `${action} failed${status}. ${detail}${reference}`;
  }

  async function createStaffUser() {
    if (!validateStaffValues(dom.staffEmail, dom.staffRole, dom.staffOrganization)) return;
    const role = dom.staffRole.value || 'staff';
    const body = {
      email: clean(dom.staffEmail.value),
      role,
      organizationId: role === 'super_admin' ? 1 : Number(dom.staffOrganization.value)
    };
    await mutateStaffUser('/api/asap/staff/users', {
      method: 'POST',
      body
    }, 'Staff user saved.', null, staffCreateDraft);
  }

  function validateStaffValues(email, role, organization) {
    if (!clean(email.value)) {
      staffEmailValidationNotice = { control: email, context: captureSettingsContext(),
        clear: setStaffStatus('Email address is required.', 'error') };
      email.focus(); return false;
    }
    if (!email.reportValidity()) return false;
    if (!role.value || role.value !== 'super_admin' && (!organization.value || Number(organization.value) <= 1)) {
      setStaffStatus('Select a role and library.', 'error'); (role.value ? organization : role).focus(); return false;
    }
    return true;
  }

  function validStaffUpdateResponse(user, values, active) {
    return user.active === active && clean(user.userPrincipalName)?.toLowerCase() === values.email?.toLowerCase() &&
      user.notificationEmail === user.userPrincipalName && clean(user.displayName) === values.displayName &&
      user.role === values.role && user.organizationId === values.organizationId;
  }

  async function updateStaffUser(id, version, values, draft) {
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: {
        version: stringValue(version),
        email: values.email,
        displayName: values.displayName,
        role: values.role,
        organizationId: values.organizationId
      }
    }, 'Staff changes saved.', id, draft);
  }

  async function deactivateStaffUser(id, version, displayName) {
    if (!await confirmSettings(`Deactivate ${displayName}?`,
      `${displayName} will lose staff access. Existing claim cleanup follows the saved staff-access rules.`, 'Deactivate staff', 'danger')) return;
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      body: { version: stringValue(version) }
    }, 'Staff user deactivated.', id, null);
  }

  async function reactivateStaffUser(user, version) {
    const displayName = staffUserDisplay(user);
    if (!await confirmSettings(`Reactivate ${displayName}?`,
      `${displayName} will regain their saved staff access. Save or cancel unsaved edits separately.`, 'Reactivate staff')) return;
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(property(user, 'id'))}/reactivate`, {
      method: 'POST',
      body: { version: stringValue(version) }
    }, 'Staff user reactivated.', property(user, 'id'), null);
  }

  function collectScoped(section, fields) {
    const result = {};
    for (const [key, id, kind] of fields) {
      const input = document.getElementById(id);
      if (!input) continue;
      const field = input.closest('[data-setting-section]');
      if (isSystem()) {
        const current = input.type === 'checkbox' ? input.checked : input.value;
        if (state.baselineSnapshot.find(item => item[0] === id)?.[1] === current) continue;
      } else {
        const intent = state.scalarIntents.get(`${section}.${key}`);
        if (!intent) continue;
        if (intent === 'reset') { result[key] = null; continue; }
      }
      const value = readControl(input, kind);
      if (kind === 'text' && value === null && field?.dataset.secret === 'true') continue;
      result[key] = value;
    }
    return result;
  }

  function collectSystem() {
    const result = {};
    for (const [key, id] of SYSTEM_FIELDS) result[key] = readControl(document.getElementById(id));
    result.patronEmbedAllowedOrigins = document.getElementById('patron-embed-allowed-origins').value
      .split(/\r?\n/).map(clean).filter(Boolean);
    return result;
  }

  function collectPolaris() {
    const result = {};
    for (const [key, id] of POLARIS_FIELDS) result[key] = readControl(document.getElementById(id),
      document.getElementById(id).type === 'number' ? 'number' : 'text');
    const apiKey = clean(document.getElementById('polaris-api-key').value);
    const password = clean(document.getElementById('polaris-admin-pass').value);
    if (apiKey) result.apiKey = apiKey;
    if (password) result.adminPassword = password;
    return result;
  }

  function collectTemplates(domainData) {
    const rows = [];
    const append = (row, editor) => {
      placeholders.validate([row], isSystem(), editor);
      rows.push(row);
    };
    const raw = templateRows(state.data);
    const systemRows = raw.filter(row => Number(row.organizationId) === 1);
    const libraryRows = raw.filter(row => Number(row.organizationId) === organizationId());
    for (const [key, prefix, subjectId, bodyId] of TEMPLATE_FIELDS) {
      const subject = document.getElementById(subjectId).value;
      const body = document.getElementById(bodyId).value;
      const systemRow = findTemplate(systemRows, key);
      const baseline = state.baselineTemplates.get(key) || {};
      const fieldset = root.querySelector(`[data-template-key="${key}"]`);
      if (isSystem()) {
        const row = { templateKey: key };
        if (subject !== (baseline.subject ?? '')) row.subject = subject;
        if (body !== (baseline.body ?? '')) row.body = body;
        if (Object.keys(row).length > 1) append(row, fieldset);
        continue;
      }
      const libraryRow = libraryRows.find(row =>
        String(row.sourceTemplateId || '') === String(systemRow?.id || '') && !row.isCustom);
      const row = {
        templateKey: key,
        sourceTemplateId: String(systemRow?.id || libraryRow?.sourceTemplateId || '')
      };
      for (const [role, value] of [['subject', subject], ['body', body]]) {
        const intent = state.templateIntents.get(`${key}.${role}`);
        if (intent === 'pin' || libraryRow && intent === 'reset') row[role] = intent === 'reset' ? null : value;
      }
      if (Object.keys(row).length > 2) append(row, fieldset);
    }
    if (domainData?.domainsChanged?.templates) {
      for (const item of domainData.templates) {
        const editor = root.querySelector(`[data-template-ui-id="${item.uiId}"]`);
        if (item.isCustom) {
          if (item.reset) {
            append({ templateKey: item.templateKey, isCustom: true, reset: true }, editor);
            continue;
          }
          const row = { templateKey: item.templateKey, isCustom: true };
          for (const key of ['displayName', 'subject', 'body', 'enabled', 'sortOrder']) {
            if (item[`${key}Changed`]) row[key] = item[key];
          }
          if (Object.keys(row).length > 2) append(row, editor);
          continue;
        }
        const row = {
          templateKey: item.templateKey,
          sourceTemplateId: item.sourceTemplateId || undefined
        };
        if (item.reset) {
          row.reset = true;
        } else {
          // Resets remove accepted columns. Without a lineage row they have
          // nothing to remove; only a remaining explicit pin can create a row.
          const writesField = key => item[`${key}Changed`] &&
            (isSystem() || item.hadOverride || item.fieldIntents?.[key] === 'pin');
          if (writesField('subject')) row.subject = item.fieldIntents?.subject === 'reset' ? null : item.subject;
          if (writesField('body')) row.body = item.fieldIntents?.body === 'reset' ? null : item.body;
          if (writesField('displayName')) row.displayName = item.fieldIntents?.displayName === 'reset' ? null : item.displayName;
          if (writesField('enabled')) row.enabled = item.fieldIntents?.enabled === 'reset' ? true : item.enabled;
          if (isSystem() && item.sortOrderChanged) row.sortOrder = item.sortOrder;
        }
        if (row.reset || !isSystem() && item.overridden && !item.hadOverride || Object.keys(row).length > 2) append(row, editor);
      }
    }
    return rows;
  }

  function collectPayload() {
    state.scope = dom.scope.value;
    const payload = { orgId: state.scope, version: state.data.version };
    const workflow = collectScoped('workflow', WORKFLOW_FIELDS);
    const patron = collectScoped('patron', PATRON_FIELDS);
    const email = collectScoped('email', EMAIL_FIELDS);
    const domainData = domainEditors.collect();
    state.pendingDeletedFormats = Array.isArray(domainData.deletedFormats)
      ? domainData.deletedFormats.filter(item => item?.id)
      : [];
    const brandingInput = document.getElementById('branding-alt');
    const branding = {};
    if (isSystem()) {
      if (brandingInput.value !== state.baselineSnapshot.find(item => item[0] === 'branding-alt')?.[1]) {
        branding.altText = clean(brandingInput.value);
      }
    } else {
      const intent = state.scalarIntents.get('branding.altText');
      if (intent) branding.altText = intent === 'reset' ? null : clean(brandingInput.value);
    }

    payload.workflow = workflow;
    payload.patron = patron;
    payload.email = email;
    const domainValues = domainData.values;
    if (domainData.domainsChanged.publication) {
      payload.patron.publicationOptions = isSystem() || !domainValues.publicationUseSystem
        ? domainValues.publication
        : null;
    }
    if (domainData.domainsChanged.creators) {
      payload.workflow.commonAuthorsList = isSystem() || !domainValues.creatorsUseSystem
        ? domainValues.creators
        : null;
    }
    if (domainData.domainsChanged.codes) {
      payload.workflow.allowedPatronCodeIds = isSystem() || !domainValues.codesUseSystem
        ? domainValues.codes.map(value => Number(value))
        : null;
    }
    if (domainData.domainsChanged.providers) payload.providers = domainData.writes.providers;
    if (domainData.domainsChanged.formats) payload.formats = domainData.writes.formats;
    if (domainData.domainsChanged.rules) payload.formatRules = domainData.writes.rules;
    if (domainData.domainsChanged.fields && !isSystem()) payload.customFields = domainValues.fields;
    if (domainData.domainsChanged.claims && !isSystem()) payload.autoClaimRules = domainValues.claims;
    if (isSystem()) {
      const replacement = clean(document.getElementById('email-postmark-token').value);
      if (replacement) payload.email.postmarkToken = replacement;
      if (document.getElementById('email-clear-postmark-token').checked) {
        payload.email.clearPostmarkToken = true;
      }
    }
    if (Object.keys(branding).length > 0) payload.branding = branding;

    if (isSystem()) {
      payload.systemSettings = collectSystem();
      payload.polaris = collectPolaris();
    }

    const templateRowsToSave = collectTemplates(domainData);
    if (isSystem() || templateRowsToSave.length > 0) payload.templates = templateRowsToSave;
    if (!isSystem()) {
      delete payload.systemSettings;
      delete payload.polaris;
    }
    return payload;
  }

  async function saveSettings(event) {
    event?.preventDefault();
    if (!state.data || !isDirty() || state.awaitingReload || state.saving || state.pendingMutation) return;
    if (isSystem() && !state.organizationsReady) {
      notify('The organization list is unavailable or incomplete. Reload settings before saving.', 'error');
      return;
    }
    if (dom.logo.files?.length) {
      notify('The selected logo image is still a draft. Use Save logo or remove the image selection before saving settings.', 'error');
      dom.saveLogo.focus();
      return;
    }
    let payload;
    templateValidationTarget = {};
    try {
      payload = collectPayload();
    } catch (error) {
      const message = error.message || 'The settings are invalid. Reload settings before saving.';
      const clearNotice = notify(message, 'error');
      if (templateValidationTarget?.message === message) {
        placeholders.ownValidationNotice(templateValidationTarget.control, message, clearNotice);
      }
      return;
    } finally {
      templateValidationTarget = null;
    }
    const mutation = beginSettingsMutation('administration-settings-save', settingsDraft);
    if (!mutation) return;
    state.saving = true;
    dom.save.disabled = true;
    notify('Saving settings...');
    let committed = false;
    let deletedFormatCount = 0;
    let totalFormatDeletes = 0;
    const committedMessage = () => totalFormatDeletes > 0
      ? `Settings saved. Format deletions confirmed: ${deletedFormatCount} of ${totalFormatDeletes}.`
      : 'Settings saved.';
    try {
      const deletedFormats = state.pendingDeletedFormats.slice();
      totalFormatDeletes = deletedFormats.length;
      const response = await authorizedJson('/api/asap/staff/settings', {
        method: 'POST',
        body: payload
      });
      if (response?.code !== 'saved' || response.data?.orgId !== mutation.context.scope ||
          !isNonemptyString(response.data?.version)) {
        throw unconfirmedResponseError();
      }
      committed = true;
      recordCommitted(mutation, committedMessage(), mutation.context.scope);
      if (!isSettingsOperationCurrent(mutation)) return;
      state.data.version = response?.data?.version || state.data.version;
      if (payload.polaris?.apiKey) {
        document.getElementById('polaris-api-key-status').textContent =
          'API key replacement saved. Reload settings to refresh the credential status.';
      }
      if (payload.polaris?.adminPassword) {
        document.getElementById('polaris-admin-pass-status').textContent =
          'System staff password replacement saved. Reload settings to refresh the credential status.';
      }
      if (payload.email?.clearPostmarkToken) {
        dom.postmarkTokenStatus.textContent = 'Postmark token removal saved. Reload settings to refresh credential status.';
      } else if (payload.email?.postmarkToken) {
        dom.postmarkTokenStatus.textContent = 'Postmark token replacement saved. Reload settings to refresh credential status.';
      }
      state.baselineSnapshot = snapshotForm(dom.form);
      domainEditors.setBaseline();
      state.awaitingReload = true;
      notify('Settings saved. Refreshing current values...', 'success');
      for (const format of deletedFormats) {
        if (!isSettingsOperationCurrent(mutation)) return;
        const id = encodeURIComponent(String(format.id));
        const version = encodeURIComponent(String(format.version || ''));
        const deletion = await authorizedJson(`/api/asap/staff/settings/formats/${id}?version=${version}`, {
          method: 'DELETE'
        });
        if (deletion?.code !== 'format_deleted' || deletion.data?.formatId !== String(format.id)) {
          throw unconfirmedResponseError();
        }
        deletedFormatCount += 1;
        recordCommitted(mutation, committedMessage(), mutation.context.scope);
      }
      if (!isSettingsOperationCurrent(mutation)) return;
      const refreshed = await load({ silent: true, owner: mutation });
      if (isSettingsOperationCurrent(mutation)) {
        notify(refreshed ? 'Settings saved.' :
          'Settings saved, but current values could not be refreshed. Reload before editing again.',
        refreshed ? 'success' : 'error');
      }
    } catch (error) {
      recordFailure(mutation, error, 'Settings change outcome is uncertain. Reload current values before retrying.');
      if (!isSettingsOperationCurrent(mutation)) return;
      if (committed) {
        notify(totalFormatDeletes > 0
          ? `${committedMessage()} A follow-up action failed. Reload before editing again.`
          : 'Settings saved, but a follow-up action failed. Reload before editing again.', 'error');
        return;
      }
      if (isUnconfirmedMutationFailure(error, mutation)) {
        if (payload.email?.clearPostmarkToken) {
          dom.postmarkTokenStatus.textContent = 'Postmark token removal outcome is uncertain. Reload current settings before retrying.';
        } else if (payload.email?.postmarkToken) {
          dom.postmarkTokenStatus.textContent = 'Postmark token replacement outcome is uncertain. Reload current settings before retrying.';
        }
        markUnconfirmedMutation('Settings change outcome is uncertain. Reload current values before retrying.');
        return;
      }
      if (error.status === 409) {
        const stale = error.response?.code === 'stale_version';
        if (stale || error.response?.code === 'template_referenced') {
          await load({ silent: true, owner: mutation });
          if (isSettingsOperationCurrent(mutation)) {
            notify(stale
              ? 'These settings changed elsewhere. Review the refreshed values before saving again.'
              : 'This rejection template is used by auto-rejection. Change and save that workflow setting before deleting it.', 'error');
          }
        } else {
          notify(error.message || 'The settings could not be saved.', 'error');
        }
      } else if (error.status !== 401 && !isAbortError(error)) {
        notify(error.message || 'The settings could not be saved.', 'error');
      }
    } finally {
      if (isSettingsContextCurrent(mutation.context)) state.saving = false;
      finishSettingsMutation(mutation);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function resetSettings() {
    if (isSystem() || !state.data || state.awaitingReload || state.pendingMutation) return;
    if (!await confirmSettings('Reset inherited overrides?',
      `${isDirty() ? 'Discard unsaved settings changes and reset' : 'Reset'} this library's inherited overrides to the current system values? Library-owned custom fields, formats, rejection templates, auto-claim rules and their history are preserved.`,
      'Reset inherited overrides', 'danger')) return;
    const mutation = beginSettingsMutation('administration-settings-reset', settingsDraft);
    if (!mutation) return;
    let committed = false;
    try {
      const response = await authorizedJson(`/api/asap/staff/settings/reset?organizationId=${encodeURIComponent(organizationId())}`, {
        method: 'POST',
        body: { version: state.data.version }
      });
      if (response?.code !== 'reset' || response.data?.orgId !== mutation.context.scope ||
          !isNonemptyString(response.data?.version)) {
        throw unconfirmedResponseError();
      }
      committed = true;
      recordCommitted(mutation, 'Inherited overrides reset.', mutation.context.scope);
      if (!isSettingsOperationCurrent(mutation)) return;
      state.data.version = response?.data?.version || state.data.version;
      state.awaitingReload = true;
      notify('Inherited overrides reset. Refreshing current values...', 'success');
      const refreshed = await load({ silent: true, owner: mutation });
      if (isSettingsOperationCurrent(mutation)) notify(refreshed ? 'Inherited overrides reset.' :
        'Inherited overrides reset, but current values could not be refreshed. Reload before editing.',
      refreshed ? 'success' : 'error');
    } catch (error) {
      recordFailure(mutation, error, 'Settings change outcome is uncertain. Reload current values before retrying.');
      if (!isSettingsOperationCurrent(mutation)) return;
      if (committed) {
        notify('Inherited overrides reset, but current values could not be refreshed. Reload before editing.', 'error');
        return;
      }
      if (isUnconfirmedMutationFailure(error, mutation)) {
        markUnconfirmedMutation('Override reset outcome is uncertain. Reload current values before retrying.');
        return;
      }
      if (error.status === 409) {
        await load({ silent: true, owner: mutation });
        if (isSettingsOperationCurrent(mutation)) {
          notify(error.message || 'The settings changed elsewhere. Review the refreshed values.', 'error');
        }
      } else if (error.status !== 401) notify(error.message || 'The inherited overrides could not be reset.', 'error');
    } finally {
      finishSettingsMutation(mutation);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function saveLogo(clear = false) {
    if (!state.data || state.awaitingReload || state.pendingMutation) return;
    const withoutLogo = rows => rows.filter(([id]) => id !== 'branding-logo');
    const file = dom.logo.files?.[0];
    const hasOtherDraft = state.baselineOwnership !== ownershipSnapshot() ||
      !sameSnapshot(withoutLogo(state.baselineSnapshot), withoutLogo(snapshotForm(dom.form)));
    if ((hasOtherDraft || clear && file) &&
        !await confirmSettings('Update saved logo?', 'Discard unsaved settings changes or image selection before updating the saved logo?',
          clear ? 'Clear logo' : 'Save logo', 'danger')) return;
    if (!clear && !file) {
      notify('Choose an image before saving the logo.', 'error');
      return;
    }
    const body = new FormData();
    body.append('version', state.data.version || '');
    if (clear) body.append('clearLogo', 'true');
    else body.append('logo', file);
    dom.saveLogo.disabled = true;
    dom.clearLogo.disabled = true;
    const mutation = beginSettingsMutation('administration-settings-logo', settingsDraft);
    if (!mutation) return;
    let committed = false;
    try {
      const scope = encodeURIComponent(String(state.scope));
      const response = await authorizedJson(`/api/asap/staff/settings/logo?orgId=${scope}`, {
        method: 'POST',
        body
      });
      const mutationOrganizationId = mutation.context.scope === 'system' ? 1 : Number(mutation.context.scope);
      if (response?.code !== 'branding_saved' || response.data?.organizationId !== mutationOrganizationId ||
          response.data?.hasLogo !== !clear || !isNonemptyString(response.data?.version)) {
        throw unconfirmedResponseError();
      }
      committed = true;
      recordCommitted(mutation, clear ? 'Logo cleared.' : 'Logo saved.', mutation.context.scope);
      if (!isSettingsOperationCurrent(mutation)) return;
      state.data.version = response?.data?.version || state.data.version;
      state.awaitingReload = true;
      releaseLogoDraft();
      dom.logo.value = '';
      dom.brandingPreview.hidden = true;
      dom.brandingPreviewSource.textContent = 'Image change saved. Reloading the effective preview.';
      dom.brandingStatus.textContent = clear ? 'Image cleared; refreshing the effective logo.' : 'Logo saved; refreshing the effective logo.';
      notify(dom.brandingStatus.textContent, 'success');
      const refreshed = await load({ silent: true, owner: mutation });
      if (!isSettingsOperationCurrent(mutation)) return;
      dom.brandingStatus.textContent = refreshed
        ? clear ? 'Image cleared; effective logo refreshed.' : 'Logo saved; effective logo refreshed.'
        : 'Image change saved, but effective branding could not be refreshed. Reload before editing.';
      notify(dom.brandingStatus.textContent, refreshed ? 'success' : 'error');
    } catch (error) {
      recordFailure(mutation, error, 'Settings change outcome is uncertain. Reload current values before retrying.');
      if (!isSettingsOperationCurrent(mutation)) return;
      if (committed) {
        dom.brandingStatus.textContent = 'Image change saved, but effective branding could not be refreshed. Reload before editing.';
        notify(dom.brandingStatus.textContent, 'error');
        return;
      }
      if (isUnconfirmedMutationFailure(error, mutation)) {
        dom.brandingStatus.textContent = 'Image change outcome is uncertain. Reload effective branding before retrying.';
        markUnconfirmedMutation(dom.brandingStatus.textContent);
        return;
      }
      if (error.status !== 401 && !isAbortError(error)) {
        dom.brandingStatus.textContent = `${error.message || 'Logo could not be saved.'} The image preview is still unsaved.`;
        notify(dom.brandingStatus.textContent, 'error');
      }
    } finally {
      finishSettingsMutation(mutation);
      if (isSettingsContextCurrent(mutation.context)) {
        dom.saveLogo.disabled = false;
        dom.clearLogo.disabled = false;
        updateDirtyState();
      }
    }
  }

  async function testPolaris() {
    if (disposed || !state.staff || !state.visible || !root.isConnected || state.pendingMutation || state.awaitingReload || polarisTest) return;
    if (isDirty() && !await confirmSettings('Test saved Polaris configuration?',
      'Test the saved Polaris configuration and discard current unsaved edits?', 'Discard edits and test', 'danger')) return;
    if (polarisTest) return;
    const operation = beginSettingsOperation('administration-polaris-test');
    polarisTest = operation;
    dom.testPolaris.disabled = true;
    setPolarisFeedback(dom.polarisResult, 'Testing saved Polaris connection...');
    try {
      if (isDirty() && !await load({ silent: true, owner: operation,
        report: () => setPolarisFeedback(dom.polarisResult, 'The Polaris connection test could not start because saved settings could not be reloaded.', 'error') })) return;
      if (!isSettingsOperationCurrent(operation)) return;
      setPolarisFeedback(dom.polarisResult, 'Testing saved Polaris connection...');
      const response = await authorizedJson('/api/asap/staff/polaris/test', {
        method: 'POST',
        body: {},
        signal: operation.signal
      });
      if (!isSettingsOperationCurrent(operation)) return;
      if (response?.code !== 'polaris_connected' || response.data?.connected !== true ||
          !isNonnegativeInt32(response.data?.organizationCount)) {
        throw new Error('Polaris connection response is incomplete.');
      }
      const data = response.data;
      setPolarisFeedback(dom.polarisResult, `Connected; ${data.organizationCount} organizations available.`, 'success');
    } catch (error) {
      if (isSettingsOperationCurrent(operation) && error.status !== 401 && !isAbortError(error)) {
        const unavailable = error.status === 502 || error.response?.code === 'polaris_unavailable';
        setPolarisFeedback(dom.polarisResult, unavailable ? 'Polaris is unavailable.' : 'The Polaris connection test could not be completed.', 'error');
      }
    } finally {
      if (polarisTest === operation) { polarisTest = null; if (!disposed) dom.testPolaris.disabled = false; }
      reads.finish('administration-polaris-test', operation.token);
    }
  }

  async function syncOrganizations() {
    const mutation = beginSettingsMutation('administration-settings-organization-sync', null);
    if (!mutation) return;
    dom.syncOrganizations.disabled = true;
    setPolarisFeedback(dom.syncResult, 'Synchronizing Polaris organizations...');
    try {
      const response = await authorizedJson('/api/asap/staff/organizations/sync', {
        method: 'POST',
        body: {}
      });
      if (response?.code !== 'synced' || !isNonnegativeInt32(response.data?.received) ||
          !isNonnegativeInt32(response.data?.changed)) {
        throw unconfirmedResponseError();
      }
      recordCommitted(mutation, 'Polaris organizations synchronized.', 'system');
      onOrganizationCatalogCommitted(mutation.owner);
      if (!isSettingsOperationCurrent(mutation)) return;
      const summary = `Synchronized ${response.data.received} organizations.`;
      setPolarisFeedback(dom.syncResult, `${summary} Refreshing current values...`);
      const refreshed = await load({ silent: true, owner: mutation, report: () => {} });
      if (isSettingsOperationCurrent(mutation)) setPolarisFeedback(dom.syncResult, refreshed
        ? summary : `${summary} Current values could not be refreshed. Reload before editing.`, refreshed ? 'success' : 'error');
    } catch (error) {
      recordFailure(mutation, error, 'Organization synchronization outcome is uncertain. Reload current values before retrying.',
        (message, kind) => setPolarisFeedback(dom.syncResult, message, kind));
      if (isSettingsOperationCurrent(mutation) && error.status !== 401 && !isAbortError(error)) {
        if (mutation.outcome === 'committed') {
          setPolarisFeedback(dom.syncResult, 'Polaris organizations synchronized, but current values could not be refreshed. Reload before editing.', 'error');
        } else if (mutation.outcome !== 'uncertain') {
          setPolarisFeedback(dom.syncResult, error.message || 'Organizations could not be synchronized.', 'error');
        }
      }
    } finally {
      finishSettingsMutation(mutation);
      if (isSettingsContextCurrent(mutation.context)) {
        dom.syncOrganizations.disabled = false;
        updateDirtyState();
      }
    }
  }

  async function setOrganizationActive(organization, active, action) {
    const ownsFocus = document.activeElement === action;
    if (!await confirmSettings(`${active ? 'Activate' : 'Deactivate'} ${organization.name}?`,
      `${active ? 'Enable' : 'Disable'} participation for ${organization.name}? The server will apply its current participation and session rules.`,
      active ? 'Activate library' : 'Deactivate library', active ? 'normal' : 'danger')) return;
    const mutation = beginSettingsMutation('administration-settings-participation', null);
    if (!mutation) return;
    let focusMoved = false;
    const focusEvents = new window.AbortController();
    if (ownsFocus) {
      const relinquishFocus = () => { focusMoved = true; };
      document.addEventListener('focusin', event => {
        if (event.target !== action && event.target !== document.body) relinquishFocus();
      }, { signal: focusEvents.signal });
      for (const event of ['pointerdown', 'keydown']) {
        document.addEventListener(event, relinquishFocus, { signal: focusEvents.signal, capture: true });
      }
    }
    try {
      const response = await authorizedJson(`/api/asap/staff/organizations/${encodeURIComponent(organization.id)}/${active ? 'activate' : 'deactivate'}`, {
        method: 'POST',
        body: { version: organization.version }
      });
      if (response?.code !== (active ? 'activated' : 'deactivated') ||
          response.data?.organizationId !== Number(organization.id) ||
          !isNonnegativeInt32(response.data?.revokedPatronSessions) || !isNonemptyString(response.data?.version)) {
        throw unconfirmedResponseError();
      }
      const committedMessage = `${organization.name} ${active ? 'activated' : 'deactivated'}.`;
      recordCommitted(mutation, committedMessage, String(organization.id));
      onOrganizationCatalogCommitted(mutation.owner, { id: organization.id, active });
      if (!isSettingsOperationCurrent(mutation)) return;
      const refreshed = await load({ silent: true, owner: mutation });
      if (isSettingsOperationCurrent(mutation)) notify(refreshed
        ? committedMessage
        : `${committedMessage} Current values could not be refreshed.`,
      refreshed ? 'success' : 'error');
    } catch (error) {
      recordFailure(mutation, error, 'Settings change outcome is uncertain. Reload current values before retrying.');
      if (!isSettingsOperationCurrent(mutation)) return;
      if (error.status === 409) {
        await load({ silent: true, owner: mutation });
        if (!isSettingsOperationCurrent(mutation)) return;
      }
      if (error.status !== 401 && !isAbortError(error)) notify(error.message || 'Organization participation could not be changed.', 'error');
    } finally {
      focusEvents.abort();
      finishSettingsMutation(mutation);
      if (isSettingsContextCurrent(mutation.context)) {
        updateDirtyState();
        const focused = document.activeElement;
        if (ownsFocus && !focusMoved && state.visible && state.activePanel === 'polaris' && isSystem() &&
            (focused === action || focused === document.body || focused === document.documentElement)) {
          const updatedAction = dom.organizations.querySelector(`[data-organization-id="${organization.id}"] button`);
          const target = state.awaitingReload ? dom.refresh : updatedAction || dom.organizationCatalog;
          if (target?.isConnected && !target.matches(':disabled') && !target.closest('[hidden], [inert]') &&
              !(dom.form.contains(target) && dom.form.inert)) target.focus();
        }
      }
    }
  }

  async function changeScope() {
    const next = dom.scope.value;
    if (next === state.scope) return;
    if (state.pendingMutation) {
      dom.scope.value = state.scope;
      notify('Wait for the settings change to finish before switching scope.', 'warning');
      return;
    }
    if (state.outcomeUncertain) {
      dom.scope.value = state.scope;
      notify('Reload current settings to verify the uncertain change before switching scope.', 'warning');
      return;
    }
    dom.scope.value = state.scope;
    const departure = await prepareDeparture();
    if (!departure) return false;
    const target = await prepareScope(next);
    if (!target?.isCurrent() || !await departure.revalidate() || !target.isCurrent() || !departure.commit()) return false;
    target.accept(); onScopeChange?.(next);
    if (state.activePanel === 'staff') await loadStaffAccess({ silent: true });
    return true;
  }

  // Loading another scope must leave the current form, logo and roster owned
  // until Navigation commits consent for the still-current source drafts.
  async function prepareScope(scope) {
    if (disposed || !state.staff || state.staff.role === 'staff') return null;
    const operation = beginSettingsOperation('administration-scope');
    const current = () => !operation.signal.aborted && isSettingsContextCurrent(operation.context) &&
      scopePreparation === operation;
    scopePreparation = operation;
    try {
      const query = encodeURIComponent(scope);
      const [response, organizationsResponse, codesResponse] = await Promise.all([
        authorizedJson(`/api/asap/staff/settings?orgId=${query}`, { signal: operation.signal }),
        authorizedJson('/api/asap/staff/organizations', { signal: operation.signal }),
        authorizedJson(`/api/asap/staff/polaris/patron-codes?orgId=${query}`, { signal: operation.signal }).catch(error => {
          if (isAbortError(error) || error.status === 401) throw error;
          return null;
        })
      ]);
      if (!current()) return null;
      const data = response?.data && response.version === undefined ? response.data : response;
      const organizations = validateOrganizationCatalog(organizationsResponse, scope, data);
      return { isCurrent: current, accept() {
        const staffReview = scope === state.scope && state.awaitingReloadMutation?.slot === 'administration-staff-mutation';
        cancelSettingsOperations(); releaseLogoDraft(); copyGeneration++;
        state.scope = scope;
        if (!staffReview) { state.awaitingReload = false; state.awaitingReloadMutation = null; }
        acceptOrganizationCatalog(organizations);
        state.staffUsers = []; state.staffAccessLoaded = false; state.lastStaffCleanup = null;
        populateScopeOptions();
        const choices = codesResponse?.data ?? codesResponse;
        populate({ ...data, patronCodeChoices: Array.isArray(choices) ? choices : [],
          patronCodeChoicesUnavailable: !Array.isArray(choices) });
        renderStaffUsers(); dom.form.hidden = false; dom.form.inert = state.awaitingReload; dom.refresh.disabled = false;
        restoreCommonAuthorsFocus();
        onRefreshed(operation.context.owner, { kind: 'settings', scope });
      } };
    } catch (error) {
      if (current() && !isAbortError(error) && error.status !== 401) notify(error.message || 'Settings could not be loaded.', 'error');
      return null;
    } finally { reads.finish('administration-scope', operation.token); }
  }

  function activatePanel(name, updateUrl = false) {
    if (disposed || !dom.nav.some(button => button.dataset.settingsPanel === name) ||
        !state.staff || state.staff.role === 'staff') return false;
    if (name !== state.activePanel) { placeholders.clear(); patronPlaceholders.clear(); suggestionLimitPlaceholders.clear(); }
    state.activePanel = name;
    for (const button of dom.nav) {
      const active = button.dataset.settingsPanel === name;
      button.setAttribute('aria-selected', String(active));
      button.classList.toggle('active', active);
      button.tabIndex = active ? 0 : -1;
    }
    for (const panel of dom.panels) panel.hidden = panel.dataset.settingsPanelContent !== name;
    dom.staffCreateForm.disabled = dom.staffCreateForm.hidden || name !== 'staff';
    for (const record of staffRows.values()) record.sync();
    if (name === 'patron') patronPlaceholders.refresh();
    if (name === 'templates') placeholders.refresh();
    if (name === 'workflow') suggestionLimitPlaceholders.refresh();
    const panel = dom.panels.find(item => item.dataset.settingsPanelContent === name);
    panel?.focus({ preventScroll: true });
    if (name === 'staff') void loadStaffAccess();
    emailTest.sync();
    if (updateUrl) onPanelChange?.(name);
    return true;
  }

  function populateScopeOptions() {
    const staffScope = state.staff?.role === 'super_admin'
      ? null
      : clean(state.staff?.organizationId);
    const selected = staffScope || state.scope;
    dom.scope.replaceChildren(node('option', { value: 'system', text: 'System level' }));
    const staffLibrary = state.organizations.find(item => isLibraryOrganization(item) && String(item.id) === staffScope);
    if (staffScope) {
      dom.scope.append(node('option', {
        value: staffScope,
        text: staffLibrary?.name || `Library ${staffScope}`
      }));
    } else {
      for (const organization of byDisplayLabel(state.organizations.filter(isLibraryOrganization))) {
        dom.scope.append(node('option', { value: String(organization.id), text: organization.name }));
      }
    }
    dom.scope.value = [...dom.scope.options].some(option => option.value === selected)
      ? selected
      : 'system';
    state.scope = dom.scope.value;
    dom.scopeField.hidden = state.staff?.role !== 'super_admin';
  }

  function setStaff(staff) {
    if (disposed) return;
    const replaced = actorKey(state.staff) !== actorKey(staff);
    if (replaced) {
      cancelSettingsOperations();
      pendingCommonAuthorsFocus = false;
      state.pendingMutation = null; state.saving = false; state.outcomeUncertain = false; state.awaitingReload = false; state.awaitingReloadMutation = null; state.unconfirmedMutation = null;
      dom.form.inert = false; dom.form.removeAttribute('aria-busy');
      releaseLogoDraft();
      copyGeneration += 1;
      state.data = null;
      state.organizations = [];
      state.organizationsReady = false;
      dom.form.hidden = true;
      dom.staffRole.value = 'staff'; dom.staffOrganization.value = '';
    }
    state.staff = staff;
    emailTest.sync();
    tab.hidden = !staff || staff.role === 'staff';
    if (!replaced) return;
    if (staff?.role === 'super_admin') {
      if (!state.data) state.scope = 'system';
    } else if (staff) {
      state.scope = String(staff.organizationId);
    }
    state.staffUsers = [];
    state.staffAccessLoaded = false;
    state.lastStaffCleanup = null;
    populateScopeOptions();
    populateStaffCreateControls();
    renderStaffUsers();
  }

  function signedOut() {
    if (disposed) return;
    const message = state.pendingMutation && state.pendingMutation.outcome !== 'committed' || state.outcomeUncertain
      ? 'Settings change outcome is uncertain. Sign in again and check saved values before retrying.' : null;
    cancelSettingsOperations();
    pendingCommonAuthorsFocus = false;
    releaseLogoDraft();
    copyGeneration += 1;
    state.visible = false;
    dom.form.hidden = true;
    state.staff = null;
    state.data = null;
    state.organizations = [];
    state.organizationsReady = false;
    state.staffUsers = [];
    state.staffAccessLoaded = false;
    state.lastStaffCleanup = null;
    state.baselineSnapshot = [];
    renderStaffUsers();
    updateDirtyState();
    return message;
  }

  function bind() {
    if (disposed || state.bound) return;
    state.bound = true;
    listen(dom.form, 'submit', saveSettings);
    // Preserve the resolved footer position while pointer focus enters it.
    // Otherwise leaving a focused editor switches static -> sticky between
    // pointerdown and pointerup, moving the action away from the pointer.
    listen(dom.saveBar, 'pointerdown', event => {
      if (event.button !== 0) return;
      dom.saveBar.classList.toggle('retain-static', window.getComputedStyle(dom.saveBar).position === 'static');
    }, true);
    listen(dom.saveBar, 'focusout', event => {
      if (!dom.saveBar.contains(event.relatedTarget)) dom.saveBar.classList.remove('retain-static');
    });
    const trackInheritanceEdit = event => {
      const control = event.target;
      if (isSystem() || !state.data || !control.matches('input, textarea, select') || control.disabled) return;
      const value = control.type === 'checkbox' ? control.checked : control.value;
      if (!state.scalarDisplays.has(control.id) || state.scalarDisplays.get(control.id) === value) return;
      state.scalarDisplays.set(control.id, value);
      const template = control.closest('fieldset.settings-template[data-template-key]');
      if (template) {
        const role = control.matches('textarea') ? 'body' : 'subject';
        state.templateIntents.set(`${template.dataset.templateKey}.${role}`, 'pin');
        refreshBuiltinTemplateStatus(template.dataset.templateKey, role, control);
        return;
      }
      const field = control.closest('[data-setting-section][data-setting-key]');
      if (!field || field.dataset.systemOnly === 'true') return;
      const section = field.dataset.settingSection, key = field.dataset.settingKey;
      state.scalarIntents.set(`${section}.${key}`, 'pin');
      if (section === 'email') {
        for (const sibling of root.querySelectorAll(`[data-setting-section="email"][data-setting-key="${key}"]`)) {
          const input = fieldInput(sibling); writeControl(input, value); state.scalarDisplays.set(input.id, value);
        }
      }
      refreshScalarStatus(section, key);
    };
    listen(dom.form, 'input', trackInheritanceEdit, true);
    listen(dom.form, 'change', trackInheritanceEdit, true);
    listen(dom.form, 'input', draftChanged);
    listen(dom.form, 'change', draftChanged);
    listen(root.ownerDocument, 'focusin', event => {
      staffFocusSequence++;
      queueStaffReveal(event.target);
    });
    listen(root, 'keydown', event => {
      // Keyboard use can reveal focus on a pointer-focused button without a
      // new focusin event. Recheck after its native activation and layout.
      if (event.target.matches?.('button')) queueStaffReveal(event.target);
    });
    listen(dom.form, 'input', event => {
      const notice = staffEmailValidationNotice;
      if (!notice || notice.control !== event.target || !clean(event.target.value)) return;
      retireStaffEmailValidation(event.target);
    });
    const compactNavigation = window.matchMedia?.('(max-width: 760px)');
    const syncTabOrientation = () => dom.nav[0].parentElement.setAttribute('aria-orientation',
      (compactNavigation?.matches ?? window.innerWidth <= 760) ? 'horizontal' : 'vertical');
    syncTabOrientation();
    if (compactNavigation) listen(compactNavigation, 'change', syncTabOrientation);
    listen(dom.commonAuthorsEnabled, 'change', () => syncCommonAuthorsOptions());
    for (const id of [
      'outstanding-timeout-enabled', 'outstanding-timeout-send-email',
      'hold-pickup-timeout-enabled', 'pending-hold-timeout-enabled', 'additional-copy-timeout-enabled'
    ]) {
      listen(document.getElementById(id), 'change', applyTimeoutDependencies);
    }
    listen(dom.useCurrentStaffUrl, 'click', () => {
      if (!currentStaffUrlSuggestion || disposed || !state.visible || state.staff?.role !== 'super_admin' ||
          !isSystem() || state.activePanel !== 'start' || !state.data || state.loading || state.saving ||
          state.pendingMutation || state.awaitingReload || state.outcomeUncertain || dom.form.inert ||
          dom.useCurrentStaffUrl.matches(':disabled') || dom.systemStaffUrl.matches(':disabled')) return;
      const alreadyCurrent = dom.systemStaffUrl.value === currentStaffUrlSuggestion;
      dom.systemStaffUrl.value = currentStaffUrlSuggestion;
      dom.systemStaffUrl.dispatchEvent(new window.Event('input', { bubbles: true }));
      dom.systemStaffUrl.dispatchEvent(new window.Event('change', { bubbles: true }));
      renderCurrentStaffUrlSuggestion(alreadyCurrent ? 'unchanged' : 'applied');
    });
    listen(dom.scope, 'change', changeScope);
    listen(dom.manageParticipation, 'click', () => {
      if (!state.visible || state.staff?.role !== 'super_admin' || !isSystem() ||
          state.loading || state.pendingMutation || state.awaitingReload || dom.form.inert) return;
      if (activatePanel('polaris', true)) dom.organizationCatalog.focus();
    });
    listen(dom.switchSystem, 'click', async () => {
      if (state.staff?.role !== 'super_admin') return;
      dom.scope.value = 'system';
      await changeScope();
      if (!disposed && state.visible && dom.scope.isConnected) dom.scope.focus();
    });
    for (const [buttonId, value, label] of [
      ['copy-patron-public-url', dom.publicUrl, 'Patron URL'],
      ['copy-patron-iframe', dom.iframeMarkup, 'Iframe markup'],
      ['copy-patron-loader', dom.loaderMarkup, 'Auto-resizing loader markup']
    ]) {
      listen(root.querySelector(`#${buttonId}`), 'click', () => copyPublicValue(value.value, label));
    }
    listen(dom.logo, 'change', previewLogoFile);
    listen(dom.discardLogoDraft, 'click', () => {
      releaseLogoDraft();
      dom.logo.value = '';
      dom.discardLogoDraft.hidden = true;
      dom.brandingStatus.textContent = 'Unsaved image selection removed.';
      renderBrandingPreview();
      draftChanged();
    });
    listen(document.getElementById('branding-alt'), 'input', () => renderBrandingPreview(Boolean(logoDraftUrl)));
    for (const button of dom.nav) {
      button.tabIndex = button.dataset.settingsPanel === state.activePanel ? 0 : -1;
      listen(button, 'click', () => activatePanel(button.dataset.settingsPanel, true));
      listen(button, 'keydown', event => {
        if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const index = dom.nav.indexOf(button);
        const offset = event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1;
        const target = event.key === 'Home' ? dom.nav[0]
          : event.key === 'End' ? dom.nav.at(-1)
          : dom.nav[(index + offset + dom.nav.length) % dom.nav.length];
        for (const item of dom.nav) item.tabIndex = item === target ? 0 : -1;
        target.focus();
      });
    }
    listen(dom.refresh, 'click', async () => {
      if (state.pendingMutation) {
        notify('Wait for the settings change to finish before reloading.', 'warning');
        return;
      }
      const review = state.awaitingReloadMutation || state.unconfirmedMutation;
      if (review?.slot === 'administration-staff-mutation' && review.preserves?.length > 0) {
        await loadStaffAccess({ preserve: true });
        return;
      }
      if (isDirty() && !await confirmSettings('Reload settings?', 'Discard unsaved settings changes and reload?', 'Discard changes and reload', 'danger')) return;
      await load();
    });
    listen(dom.discard, 'click', async () => {
      if (state.pendingMutation) {
        notify('Wait for the settings change to finish before discarding changes.', 'warning');
        return;
      }
      if (!await confirmSettings('Discard settings changes?', 'Discard unsaved settings changes?', 'Discard changes', 'danger')) return;
      await load({ silent: true });
    });
    listen(dom.reset, 'click', resetSettings);
    listen(dom.testPolaris, 'click', testPolaris);
    listen(dom.syncOrganizations, 'click', syncOrganizations);
    listen(dom.staffRole, 'change', populateStaffCreateControls);
    listen(dom.staffCreate, 'click', createStaffUser);
    listen(dom.staffCreateToggle, 'click', () => setStaffCreateOpen(dom.staffCreateForm.hidden, true));
    listen(dom.staffCreateCancel, 'click', () => {
      resetStaffCreateDraft(); setStaffCreateOpen(false, true); drafts.touch(); updateDirtyState();
    });
    listen(dom.staffCreateForm, 'keydown', event => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      event.preventDefault(); event.stopPropagation(); setStaffCreateOpen(false, true);
    });
    const refreshStaff = async () => {
      if (state.pendingMutation) return;
      const review = state.awaitingReloadMutation || state.unconfirmedMutation;
      if (review?.slot === 'administration-staff-mutation' && review.preserves?.length > 0) {
        return loadStaffAccess({ preserve: true });
      }
      if (hasStaffDraft() && !await confirmSettings('Refresh staff roster?', 'Discard unsaved staff edits and refresh the roster?', 'Discard edits and refresh', 'danger')) return;
      return loadStaffAccess({ discard: true });
    };
    listen(dom.staffRefresh, 'click', refreshStaff);
    listen(dom.staffShowInactive, 'change', updateStaffVisibility);
    listen(dom.staffAuditHistory, 'click', () => {
      if (disposed || !state.visible || state.activePanel !== 'staff' || !state.staff) return;
      onStaffAuditHistory(scopedStaffOrganizationId(), { ownerKey: actorKey(state.staff) });
    });
    listen(dom.saveLogo, 'click', () => saveLogo(false));
    listen(dom.clearLogo, 'click', () => saveLogo(true));
    listen(dom.postmarkToken, 'input', event => {
      if (event.target.value) {
        dom.clearPostmarkToken.checked = false;
        renderPostmarkTokenStatus();
      }
    });
    listen(dom.clearPostmarkToken, 'change', event => {
      if (event.target.checked) dom.postmarkToken.value = '';
      renderPostmarkTokenStatus();
    });
    for (const [sourceId, targetId] of [
      ['email-from-address', 'email-template-from-address'],
      ['email-from-name', 'email-template-from-name']
    ]) {
      const source = document.getElementById(sourceId);
      const target = document.getElementById(targetId);
      const key = sourceId === 'email-from-address' ? 'fromAddress' : 'fromName';
      listen(source, 'input', () => {
        target.value = source.value;
        updateDirtyState();
      });
      listen(target, 'input', () => {
        source.value = target.value;
        updateDirtyState();
      });
    }
  }

  async function activate(panel = state.activePanel) {
    if (disposed || !state.staff || state.staff.role === 'staff') return;
    state.visible = true;
    activatePanel(panel);
    populateScopeOptions();
    const staffReview = state.awaitingReloadMutation || state.unconfirmedMutation;
    if (staffReview?.slot === 'administration-staff-mutation' && state.data && String(state.data.orgId) === String(state.scope)) {
      await loadStaffAccess(staffReview.preserves?.length > 0 ? { preserve: true } : { discard: true });
    } else if (state.awaitingReload || !state.data || String(state.data.orgId) !== String(state.scope)) await load();
    else configureScopedFields();
  }

  function suspend() {
    if (disposed) return false;
    if (state.pendingMutation || state.outcomeUncertain) return false;
    state.visible = false;
    emailTest.invalidate();
    placeholders.clear(); patronPlaceholders.clear(); suggestionLimitPlaceholders.refresh();
    placeholders.retireValidationNotice();
    cancelPolarisTest();
    releaseLogoDraft();
    dom.logo.value = '';
    dom.discardLogoDraft.hidden = true;
    renderBrandingPreview();
    updateDirtyState();
    copyGeneration += 1;
    reads.begin('administration-settings').abort();
    reads.begin('administration-staff-access').abort();
    reads.begin('administration-patron-codes').abort();
    return true;
  }

  function discardDraft() {
    if (disposed) return false;
    if (state.pendingMutation || state.outcomeUncertain) return false;
    if (state.data) populate(state.data);
    return true;
  }

  return {
    dispose() { if (disposed) return; cancelSettingsOperations(); emailTest.dispose(); releaseLogoDraft(); disposed = true; events.abort(); drafts.dispose(); domainEditors.dispose(); },
    bind,
    setStaff,
    signedOut,
    activate,
    activatePanel,
    suspend,
    prepareScope,
    discardDraft,
    load,
    isDirty,
    hasPendingMutation: () => Boolean(state.pendingMutation),
    hasUnconfirmedOutcome: () => state.outcomeUncertain,
    inspectDeparture: () => ({ owner: drafts, dirty: isDirty(), blocked: disposed || Boolean(state.pendingMutation || state.outcomeUncertain),
      stamp: JSON.stringify([drafts.revision(), controlSnapshot(root)]),
      message: state.outcomeUncertain
        ? 'Reload current settings to verify the uncertain change before navigating away.'
        : 'Wait for the settings change to finish before navigating away.',
      confirmMessage: 'Discard unsaved settings changes and navigate away?' }),
    currentScope: () => state.scope,
    isReady: () => Boolean(state.data && String(state.data.orgId) === String(state.scope) && !state.awaitingReload),
    currentPanel: () => state.activePanel
  };
}
