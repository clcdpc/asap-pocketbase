import { authorizedJson, isAbortError, latestLoads } from './http.js';
import { createSettingsDomainEditors } from './settings-domains.js';

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
  ['loginPrompt', 'patron-login-prompt'],
  ['loginNote', 'patron-login-note'],
  ['suggestionFormNote', 'patron-suggestion-form-note'],
  ['noEmailMessage', 'patron-no-email-message'],
  ['successTitle', 'patron-success-title'],
  ['successMessage', 'patron-success-message'],
  ['alreadySubmittedMessage', 'patron-already-submitted-message'],
  ['ebookMessage', 'patron-ebook-message'],
  ['eaudiobookMessage', 'patron-eaudiobook-message'],
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
  ['systemPolarisUserId', 'polaris-system-user-id'],
  ['organizationIdForRequests', 'polaris-requesting-org-id'],
  ['pickupOrganizationId', 'polaris-pickup-org-id']
];

const SETTINGS_OPERATION_SLOTS = [
  'administration-settings',
  'administration-settings-save',
  'administration-settings-reset',
  'administration-settings-logo',
  'administration-settings-polaris-test',
  'administration-settings-organization-sync',
  'administration-settings-participation',
  'administration-staff-access',
  'administration-staff-mutation'
];

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
    else if (name.startsWith('on') && typeof attribute === 'function') {
      value.addEventListener(name.slice(2), attribute);
    } else {
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
  return [...form.elements].filter(control => !control.closest?.('[data-settings-ignore="true"]')).map(control => [
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
  getStaff,
  onPanelChange,
  onScopeChange,
  onCommitted = () => {},
  onRefreshed = () => {}
}) {
  const dom = {
    contextSummary: root.querySelector('#settings-context-summary'),
    message: root.querySelector('#settings-message'),
    scopeField: root.querySelector('#settings-scope-field'),
    scope: root.querySelector('#settings-scope'),
    form: root.querySelector('#settings-form'),
    version: root.querySelector('#settings-version'),
    nav: [...root.querySelectorAll('#settings-nav [data-settings-panel]')],
    panels: [...root.querySelectorAll('[data-settings-panel-content]')],
    refresh: root.querySelector('#settings-refresh'),
    save: root.querySelector('#settings-save'),
    discard: root.querySelector('#settings-discard'),
    reset: root.querySelector('#settings-reset'),
    testPolaris: root.querySelector('#btn-test-polaris'),
    syncOrganizations: root.querySelector('#btn-sync-organizations'),
    polarisResult: root.querySelector('#polaris-test-result'),
    syncResult: root.querySelector('#organizations-sync-result'),
    organizationStatus: root.querySelector('#organizations-status-message'),
    organizations: root.querySelector('#settings-organizations-list'),
    enabledLibraries: root.querySelector('#enabled-libraries-checkbox-container'),
    staffStatus: root.querySelector('#staff-access-status'),
    staffRefresh: root.querySelector('#staff-refresh'),
    staffCreate: root.querySelector('#staff-add-submit'),
    staffEmail: root.querySelector('#staff-add-email'),
    staffRole: root.querySelector('#staff-add-role'),
    staffOrganization: root.querySelector('#staff-add-organization'),
    staffUsers: root.querySelector('#settings-staff-users-list'),
    staffAuditRefresh: root.querySelector('#staff-audit-refresh'),
    staffAudit: root.querySelector('#settings-staff-audit-list'),
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
    staffUsers: [],
    staffAudit: [],
    staffCanAssignSuperAdmin: false,
    staffAccessLoaded: false,
    lastStaffCleanup: null,
    scope: 'system',
    activePanel: 'start',
    baselineSnapshot: [],
    baselineEditors: new Map(),
    baselineTemplates: new Map(),
    baselineOverrides: new Map(),
    pendingDeletedFormats: [],
    templateSelection: null,
    savedRejectionTemplateId: '',
    awaitingReload: false,
    outcomeUncertain: false,
    saving: false,
    pendingMutation: null,
    visible: false,
    bound: false
  };
  let logoDraftUrl = null;
  let logoPreviewGeneration = 0;
  let copyGeneration = 0;

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
    onChange: updateDirtyState,
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

  function isTemplateTextEditor(control) {
    return Boolean(control?.matches) && !control.disabled &&
      (control.matches('#settings-template-editor input[type="text"], #settings-template-editor textarea') ||
       control.matches('#email-templates-editor .template-subject, #email-templates-editor .template-body'));
  }

  function rememberTemplateSelection(control) {
    if (!isTemplateTextEditor(control)) return;
    state.templateSelection = {
      control,
      start: control.selectionStart ?? control.value.length,
      end: control.selectionEnd ?? control.value.length
    };
  }

  function insertPlaceholder(key) {
    const selection = state.templateSelection;
    const status = document.getElementById('template-placeholder-status');
    if (!selection?.control?.isConnected || !isTemplateTextEditor(selection.control)) {
      status.textContent = 'Select a template Subject or Body field first.';
      return;
    }
    const token = `{{${key}}}`;
    selection.control.setRangeText(token, selection.start, selection.end, 'end');
    selection.control.dispatchEvent(new window.Event('input', { bubbles: true }));
    selection.control.focus();
    rememberTemplateSelection(selection.control);
    updateDirtyState();
    status.textContent = `${token} inserted.`;
  }

  function renderPlaceholderHelp(data) {
    const list = document.getElementById('template-placeholder-list');
    list.replaceChildren();
    for (const key of Array.isArray(data.templatePlaceholders) ? data.templatePlaceholders : []) {
      if (typeof key !== 'string' || !/^\w+$/.test(key)) continue;
      const button = node('button', { type: 'button', className: 'secondary-button', text: `{{${key}}}` });
      button.addEventListener('click', () => insertPlaceholder(key));
      list.append(button);
    }
    state.templateSelection = null;
    document.getElementById('template-placeholder-status').textContent = '';
  }

  function isSystem() {
    return state.scope === 'system';
  }

  function staffContextKey(staff) {
    if (!staff) return '';
    return [
      property(staff, 'id'),
      property(staff, 'userPrincipalName'),
      property(staff, 'role'),
      property(staff, 'organizationId')
    ].map(value => value === null || value === undefined ? '' : String(value)).join('|');
  }

  function captureSettingsContext() {
    return { scope: String(state.scope), staff: staffContextKey(state.staff) };
  }

  function isSettingsContextCurrent(context) {
    return context && context.scope === String(state.scope) && context.staff === staffContextKey(state.staff);
  }

  function beginSettingsOperation(slot) {
    const operation = latestLoads.begin(slot);
    const context = captureSettingsContext();
    return { ...operation, context };
  }

  function isSettingsOperationCurrent(operation) {
    return operation?.isCurrent() && isSettingsContextCurrent(operation.context);
  }

  function isUnconfirmedMutationFailure(error, operation) {
    return !operation.signal.aborted && (error?.status === 0 || isAbortError(error));
  }

  function markUnconfirmedMutation(message) {
    state.outcomeUncertain = true;
    state.awaitingReload = true;
    notify(message, 'error');
  }

  function holdSettingsMutation(operation) {
    latestLoads.begin('administration-settings').abort();
    state.pendingMutation = operation;
    dom.form.inert = true;
    dom.form.setAttribute('aria-busy', 'true');
    dom.refresh.disabled = false;
  }

  function finishSettingsMutation(operation) {
    if (state.pendingMutation !== operation) return;
    state.pendingMutation = null;
    dom.form.inert = false;
    dom.form.removeAttribute('aria-busy');
    dom.refresh.disabled = false;
  }

  function cancelSettingsOperations() {
    for (const slot of SETTINGS_OPERATION_SLOTS) latestLoads.begin(slot).abort();
  }

  function organizationId() {
    return isSystem() ? 1 : Number(state.scope);
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
      ? state.organizations.filter(item => String(item.id) === String(staffScope))
      : state.organizations.filter(item => Number(item.id) > 1);
    return organizations.map(item => ({
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

  function setStaffStatus(message, kind = '') {
    if (!dom.staffStatus) return;
    dom.staffStatus.textContent = message || '';
    dom.staffStatus.className = `settings-result${kind ? ` ${kind}` : ''}`;
  }

  function notify(message, kind = '') {
    dom.message.textContent = message || '';
    dom.message.className = `settings-message${kind ? ` ${kind}` : ''}`;
    announce(message || '', kind);
  }

  function isDirty() {
    return state.data !== null && !sameSnapshot(state.baselineSnapshot, snapshotForm(dom.form));
  }

  function updateDirtyState() {
    const dirty = isDirty();
    dom.save.disabled = !dirty || state.awaitingReload || state.saving;
    dom.saveLogo.disabled = state.awaitingReload || Boolean(state.pendingMutation);
    dom.clearLogo.disabled = state.awaitingReload || Boolean(state.pendingMutation);
    dom.discard.hidden = !dirty;
    dom.saveTitle.textContent = state.outcomeUncertain ? 'Outcome uncertain; reload needed'
      : state.awaitingReload ? 'Saved; reload needed' : dirty ? 'Unsaved changes' : 'No changes';
    dom.saveDetail.textContent = state.outcomeUncertain
      ? 'The request may have committed. Reload current settings before retrying.'
      : state.awaitingReload
      ? 'The save committed. Reload current settings before editing again.'
      : dirty
      ? 'Changes are local until you save this settings context.'
      : 'Everything in this settings context is saved.';
    dom.reset.hidden = isSystem();
    dom.reset.disabled = state.awaitingReload || Boolean(state.pendingMutation);
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
    return meaningful(property(rawSection(section), key));
  }

  function fieldInput(field) {
    return field.querySelector('input:not(.settings-override-toggle), textarea, select');
  }

  function syncOverrideToggles(section, key, checked) {
    for (const field of root.querySelectorAll(
      `[data-setting-section="${section}"][data-setting-key="${key}"]`
    )) {
      const toggle = field.querySelector('.settings-override-toggle');
      const input = fieldInput(field);
      if (toggle) toggle.checked = checked;
      if (input) input.disabled = !checked;
    }
  }

  function createOverrideToggle(field, section, key) {
    let input = field.querySelector('.settings-override-toggle');
    if (!input) {
      input = node('input', {
        type: 'checkbox',
        className: 'settings-override-toggle',
        'aria-label': `Override ${field.querySelector('span')?.textContent || key}`
      });
      const wrapper = node('span', { className: 'settings-override-control' }, [
        input,
        node('span', { text: 'Override' })
      ]);
      input.addEventListener('change', () => {
        syncOverrideToggles(section, key, input.checked);
        updateDirtyState();
      });
      field.append(wrapper);
    }
    input.checked = hasRawOverride(section, key);
    const control = fieldInput(field);
    if (control) control.disabled = !input.checked;
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
        field.querySelector('.settings-override-control')?.remove();
        continue;
      }
      if (!['workflow', 'patron', 'email', 'branding'].includes(section)) continue;
      if (system) {
        if (input) input.disabled = false;
        field.querySelector('.settings-override-control')?.remove();
      } else {
        createOverrideToggle(field, section, key);
      }
    }
    for (const fieldset of root.querySelectorAll('fieldset[data-system-only="true"]')) {
      fieldset.disabled = !system;
    }
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
    document.getElementById('polaris-api-key').value = '';
    document.getElementById('polaris-admin-pass').value = '';
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
        subject: property(effective, 'subject') || null,
        body: property(effective, 'body') || null
      });
      const fieldset = root.querySelector(`[data-template-key="${key}"]`);
      fieldset.querySelector('.settings-template-override-control')?.remove();
      if (!isSystem()) {
        const systemRow = findTemplate(systemRows, key);
        const libraryRow = libraryRows.find(row =>
          String(row.sourceTemplateId || '') === String(systemRow?.id || '') && !row.isCustom);
        const input = node('input', {
          type: 'checkbox',
          className: 'settings-template-override',
          'aria-label': `Override ${fieldset.querySelector('legend')?.textContent || key}`,
          checked: Boolean(libraryRow)
        });
        const wrapper = node('span', { className: 'settings-template-override-control' }, [
          input,
          node('span', { text: 'Override' })
        ]);
        fieldset.querySelector('legend').append(wrapper);
        input.addEventListener('change', () => {
          document.getElementById(subjectId).disabled = !input.checked;
          document.getElementById(bodyId).disabled = !input.checked;
          updateDirtyState();
        });
        document.getElementById(subjectId).disabled = !input.checked;
        document.getElementById(bodyId).disabled = !input.checked;
      }
    }

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

  function populateParticipation() {
    dom.enabledLibraries.replaceChildren();
    const libraries = state.organizations.filter(item => Number(item.id) > 1);
    if (libraries.length === 0) {
      dom.enabledLibraries.append(node('p', { className: 'settings-empty', text: 'No synced library organizations.' }));
      return;
    }
    for (const organization of libraries) {
      const checkbox = node('input', {
        type: 'checkbox',
        value: organization.id,
        checked: organization.active,
        'aria-label': `Enable ${organization.name}`
      });
      dom.enabledLibraries.append(node('label', { className: 'settings-list-row' }, [
        checkbox,
        node('span', { text: organization.name })
      ]));
    }
  }

  function renderOrganizations() {
    dom.organizations.replaceChildren();
    if (state.organizations.length === 0) {
      dom.organizations.append(node('li', { className: 'settings-empty', text: 'No organizations are available.' }));
      return;
    }
    for (const organization of state.organizations) {
      const active = Boolean(organization.active);
      const item = node('li', { className: 'settings-list-row' }, [
        node('span', { className: 'settings-organization-name', text: `${organization.name}${organization.abbreviation ? ` (${organization.abbreviation})` : ''}` }),
        node('span', { className: `status-badge${active ? '' : ' blocked'}`, text: active ? 'Active' : 'Inactive' })
      ]);
      if (Number(organization.id) > 1 && state.staff?.role === 'super_admin') {
        const action = node('button', {
          type: 'button',
          className: 'secondary-button',
          title: active ? `Deactivate ${organization.name}` : `Activate ${organization.name}`,
          'aria-label': active ? `Deactivate ${organization.name}` : `Activate ${organization.name}`
        }, [node('i', { className: `fa fa-${active ? 'ban' : 'check'}`, 'aria-hidden': 'true' })]);
        action.addEventListener('click', () => setOrganizationActive(organization, !active));
        item.append(action);
      }
      dom.organizations.append(item);
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

  function renderStaffUsers() {
    if (!dom.staffUsers) return;
    dom.staffUsers.replaceChildren();
    populateStaffCreateControls();
    if (state.staffUsers.length === 0) {
      const message = state.staffAccessLoaded
        ? `No staff users found for ${scopedStaffLabel()}.`
        : 'Open or refresh the roster to load staff users.';
      dom.staffUsers.append(node('p', { className: 'settings-empty', text: message }));
      return;
    }

    for (const user of state.staffUsers) {
      const id = stringValue(property(user, 'id'));
      const version = stringValue(property(user, 'version'));
      const active = property(user, 'active') !== false;
      const row = node('article', { className: `settings-staff-row${active ? '' : ' inactive'}` });
      const upn = node('input', {
        type: 'email',
        value: property(user, 'userPrincipalName') || '',
        'aria-label': `Authentication email for ${staffUserDisplay(user)}`
      });
      const displayName = node('input', {
        type: 'text',
        value: property(user, 'displayName') || '',
        'aria-label': `Display name for ${staffUserDisplay(user)}`
      });
      const notificationEmail = node('input', {
        type: 'email',
        value: property(user, 'notificationEmail') || '',
        'aria-label': `Notification email for ${staffUserDisplay(user)}`
      });
      const role = node('select', { 'aria-label': `Role for ${staffUserDisplay(user)}` });
      replaceSelectOptions(role, userRoleChoices(user), property(user, 'role') || 'staff');
      const organization = node('select', { 'aria-label': `Library for ${staffUserDisplay(user)}` });
      replaceSelectOptions(organization, organizationChoices(role.value), property(user, 'organizationId'));
      organization.disabled = role.value === 'super_admin' || state.staff?.role !== 'super_admin';
      role.addEventListener('change', () => {
        replaceSelectOptions(organization, organizationChoices(role.value), organization.value);
        organization.disabled = role.value === 'super_admin' || state.staff?.role !== 'super_admin';
      });

      const saveMetadata = node('button', {
        type: 'button',
        className: 'secondary-button',
        text: 'Save profile'
      });
      saveMetadata.addEventListener('click', () => updateStaffMetadata(id, version, {
        email: clean(upn.value),
        displayName: clean(displayName.value),
        notificationEmail: clean(notificationEmail.value)
      }));

      const saveRole = node('button', {
        type: 'button',
        className: 'secondary-button',
        text: 'Update access'
      });
      saveRole.addEventListener('click', () => changeStaffRole(id, version, {
        role: role.value,
        organizationId: role.value === 'super_admin' ? 1 : Number(organization.value)
      }));

      const lifecycle = node('button', {
        type: 'button',
        className: active ? 'danger-button' : 'secondary-button',
        text: active ? 'Deactivate' : 'Reactivate'
      });
      lifecycle.addEventListener('click', () => {
        if (active) deactivateStaffUser(id, version, staffUserDisplay(user));
        else reactivateStaffUser(user, role.value, organization.value);
      });

      const actions = [saveMetadata, saveRole, lifecycle];

      row.replaceChildren(
        node('div', { className: 'settings-staff-heading' }, [
          node('strong', { text: staffUserDisplay(user) }),
          node('span', { className: `status-badge${active ? '' : ' blocked'}`, text: active ? 'Active' : 'Inactive' })
        ]),
        node('p', { className: 'settings-staff-meta' }, [
          node('span', { text: `ID ${id}` }),
          node('span', { text: roleLabel(property(user, 'role')) }),
          node('span', { text: staffUserOrganizationName(user) }),
          node('span', { text: `Version ${version}` })
        ]),
        node('div', { className: 'settings-staff-controls' }, [
          node('label', { className: 'settings-field' }, [node('span', { text: 'Authentication email' }), upn]),
          node('label', { className: 'settings-field' }, [node('span', { text: 'Display name' }), displayName]),
          node('label', { className: 'settings-field' }, [node('span', { text: 'Notification email' }), notificationEmail]),
          node('label', { className: 'settings-field' }, [node('span', { text: 'Role' }), role]),
          node('label', { className: 'settings-field' }, [node('span', { text: 'Library' }), organization])
        ]),
        node('div', { className: 'settings-staff-actions' }, actions)
      );
      if (state.lastStaffCleanup?.staffId === id) {
        row.append(node('p', {
          className: 'settings-cleanup-result',
          text: cleanupSummary(state.lastStaffCleanup.cleanup)
        }));
      }
      dom.staffUsers.append(row);
    }
  }

  function auditDetails(details) {
    const text = clean(details);
    if (!text) return '';
    try {
      const parsed = JSON.parse(text);
      if (!parsed || typeof parsed !== 'object') return text;
      return Object.entries(parsed).map(([key, value]) => `${key}: ${value}`).join('; ');
    } catch {
      return text;
    }
  }

  function renderStaffAudit() {
    if (!dom.staffAudit) return;
    dom.staffAudit.replaceChildren();
    if (state.staffAudit.length === 0) {
      dom.staffAudit.append(node('li', { className: 'settings-empty', text: 'No audit entries found for this scope.' }));
      return;
    }
    for (const entry of state.staffAudit) {
      const details = auditDetails(property(entry, 'detailsJson'));
      const created = clean(property(entry, 'createdUtc')) || '';
      const targetType = clean(property(entry, 'targetType'));
      const targetId = clean(property(entry, 'targetId'));
      dom.staffAudit.append(node('li', { className: 'settings-audit-item' }, [
        node('strong', { text: property(entry, 'action') || 'administration_action' }),
        node('span', {
          className: 'settings-audit-meta',
          text: `${created}${property(entry, 'actorName') ? ` by ${property(entry, 'actorName')}` : ''}` +
            `${targetType ? `; ${targetType}${targetId ? ` ${targetId}` : ''}` : ''}`
        }),
        details ? node('span', { text: details }) : null
      ]));
    }
  }

  function populate(data) {
    releaseLogoDraft();
    dom.logo.value = '';
    dom.discardLogoDraft.hidden = true;
    state.data = data;
    state.scope = String(data.orgId || state.scope);
    const stored = object(data.stored);
    const configuredSystem = object(stored.configuredSystem);
    const libraryOverride = object(stored.libraryOverride);
    const systemSettings = object(stored.systemSettings);
    const effective = object(data.effective);
    const effectivePatron = object(data.ui_text || effective);
    const systemWorkflow = mergeConfigured(configuredSystem.workflow, null, stored.workflow);
    const workflow = isSystem()
      ? systemWorkflow
      : mergeConfigured(systemWorkflow, libraryOverride.workflow, data.workflow);
    state.savedRejectionTemplateId = stringValue(property(workflow, 'outstandingTimeoutRejectionTemplateId'));
    const systemPatron = mergeConfigured(configuredSystem.patron, null, effectivePatron);
    const patron = isSystem()
      ? systemPatron
      : mergeConfigured(systemPatron, libraryOverride.patron, effectivePatron);
    const email = isSystem()
      ? mergeConfigured(configuredSystem.email, null, data.emails)
      : mergeConfigured(configuredSystem.email, libraryOverride.email, data.emails);

    dom.contextSummary.textContent = isSystem()
      ? 'System level configuration'
      : `Library configuration for ${data.organization?.name || `organization ${organizationId()}`}`;
    dom.version.value = data.version || '';
    setSystemFields(systemSettings);
    setPolarisFields(configuredSystem.polaris || stored.polaris);
    for (const [key, id, kind] of WORKFLOW_FIELDS) writeControl(document.getElementById(id), property(workflow, key));
    setScopedFields(patron, PATRON_FIELDS);
    setSender(email);
    document.getElementById('email-token-status').textContent =
      property(configuredSystem.email, 'hasPostmarkToken')
        ? 'A system Postmark token is stored. Leave the token field blank to keep it.'
        : 'No system Postmark token is stored.';
    setCollections(data);
    renderPublicLinks(data);
    writeControl(document.getElementById('branding-alt'), property(effective, 'logoAltText'));
    setTemplates(data);
    renderPlaceholderHelp(data);
    setRejectionTemplateOptions(data, workflow);

    state.baselineOverrides.clear();
    for (const [section, fields] of [['workflow', WORKFLOW_FIELDS], ['patron', PATRON_FIELDS], ['email', EMAIL_FIELDS], ['branding', [['altText', 'branding-alt']]]]) {
      for (const [key] of fields) state.baselineOverrides.set(`${section}.${key}`, hasRawOverride(section, key));
    }
    configureScopedFields();
    dom.systemContext.hidden = isSystem();
    dom.switchSystem.hidden = state.staff?.role !== 'super_admin';
    dom.clearLogo.textContent = isSystem() ? 'Clear system image' : 'Clear image (use inherited image)';
    renderBrandingPreview();
    populateParticipation();
    renderOrganizations();
    renderStaffUsers();
    renderStaffAudit();
    dom.scope.value = state.scope;
    state.baselineSnapshot = snapshotForm(dom.form);
    updateDirtyState();
  }

  function currentScopeForRequest() {
    return encodeURIComponent(state.scope || 'system');
  }

  async function load(options = {}) {
    if (!state.staff || state.staff.role === 'staff') return;
    if (state.pendingMutation && state.pendingMutation !== options.owner) return false;
    const context = captureSettingsContext();
    const loadState = latestLoads.begin('administration-settings');
    dom.refresh.disabled = true;
    if (!options.silent) notify('Loading settings...');
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
      const data = settingsResponse?.data && settingsResponse?.version === undefined
        ? settingsResponse.data
        : settingsResponse;
      const organizations = organizationsResponse?.data ?? organizationsResponse;
      state.organizations = Array.isArray(organizations) ? organizations : [];
      populateScopeOptions();
      const patronCodeChoices = patronCodesResponse?.data ?? patronCodesResponse;
      data.patronCodeChoices = Array.isArray(patronCodeChoices) ? patronCodeChoices : [];
      const wasHidden = dom.form.hidden;
      state.awaitingReload = false;
      state.outcomeUncertain = false;
      populate(data || {});
      onRefreshed();
      dom.form.hidden = false;
      if (wasHidden) dom.panels.find(item => item.dataset.settingsPanelContent === state.activePanel)?.focus({ preventScroll: true });
      if (state.activePanel === 'staff') void loadStaffAccess({ silent: true });
      if (!options.silent && loadState.isCurrent() && isSettingsContextCurrent(context)) notify('Settings loaded.');
      return true;
    } catch (error) {
      if (loadState.isCurrent() && isSettingsContextCurrent(context) && state.visible && !isAbortError(error) && error.status !== 401) {
        notify(state.outcomeUncertain
          ? 'Settings change outcome is still uncertain. Current values could not be reloaded; try again before editing.'
          : error.message || 'Settings could not be loaded.', 'error');
      }
      return false;
    } finally {
      if (loadState.isCurrent() && isSettingsContextCurrent(context)) dom.refresh.disabled = false;
      latestLoads.finish('administration-settings', loadState.token);
    }
  }

  function staffUsersUrl() {
    const scope = scopedStaffOrganizationId();
    return scope ? `/api/asap/staff/users?orgId=${encodeURIComponent(scope)}` : '/api/asap/staff/users';
  }

  function staffAuditUrl() {
    const scope = scopedStaffOrganizationId();
    const query = new URLSearchParams({ limit: '50' });
    if (scope) query.set('organizationId', scope);
    return `/api/asap/staff/audit?${query.toString()}`;
  }

  async function loadStaffAccess(options = {}) {
    if (!state.staff || state.staff.role === 'staff') return;
    const loadState = beginSettingsOperation('administration-staff-access');
    if (!options.silent) setStaffStatus(`Loading staff access for ${scopedStaffLabel()}...`);
    if (dom.staffRefresh) dom.staffRefresh.disabled = true;
    if (dom.staffAuditRefresh) dom.staffAuditRefresh.disabled = true;
    try {
      const [usersResponse, auditResponse] = await Promise.all([
        authorizedJson(staffUsersUrl(), { signal: loadState.signal }),
        authorizedJson(staffAuditUrl(), { signal: loadState.signal }).catch(error => {
          if (isAbortError(error) || error.status === 401) throw error;
          return { data: [] };
        })
      ]);
      if (!isSettingsOperationCurrent(loadState)) return;
      const users = usersResponse?.data ?? usersResponse ?? {};
      state.staffUsers = Array.isArray(users.users) ? users.users : [];
      state.staffCanAssignSuperAdmin = Boolean(users.canAssignSuperAdmin);
      const audit = auditResponse?.data ?? auditResponse;
      state.staffAudit = Array.isArray(audit) ? audit : [];
      state.staffAccessLoaded = true;
      renderStaffUsers();
      renderStaffAudit();
      if (!options.silent) setStaffStatus(`Staff access loaded for ${scopedStaffLabel()}.`, 'success');
    } catch (error) {
      if (isSettingsOperationCurrent(loadState) && !isAbortError(error) && error.status !== 401) {
        setStaffStatus(error.message || 'Staff access could not be loaded.', 'error');
      }
    } finally {
      if (isSettingsOperationCurrent(loadState)) {
        if (dom.staffRefresh) dom.staffRefresh.disabled = false;
        if (dom.staffAuditRefresh) dom.staffAuditRefresh.disabled = false;
      }
      latestLoads.finish('administration-staff-access', loadState.token);
    }
  }

  async function mutateStaffUser(path, options, successMessage, staffId = null) {
    const mutation = beginSettingsOperation('administration-staff-mutation');
    setStaffStatus(successMessage.replace(/\.$/, '') + '...');
    try {
      const response = await authorizedJson(path, { ...options, signal: mutation.signal });
      if (!isSettingsOperationCurrent(mutation)) return null;
      const cleanup = response?.cleanup ?? response?.data?.cleanup ?? {};
      state.lastStaffCleanup = { staffId: stringValue(property(response?.user ?? response?.data?.user, 'id') || staffId), cleanup };
      await loadStaffAccess({ silent: true });
      if (isSettingsOperationCurrent(mutation)) setStaffStatus(`${successMessage} ${cleanupSummary(cleanup)}`, 'success');
      return response;
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return null;
      if (error.status === 409) {
        await loadStaffAccess({ silent: true });
        if (!isSettingsOperationCurrent(mutation)) return null;
        setStaffStatus(error.message || 'Staff access changed elsewhere. Review the refreshed roster.', 'error');
      } else if (error.status !== 401 && !isAbortError(error)) {
        setStaffStatus(error.message || 'The staff access change could not be saved.', 'error');
      }
      return null;
    } finally {
      latestLoads.finish('administration-staff-mutation', mutation.token);
    }
  }

  async function createStaffUser() {
    const role = dom.staffRole.value || 'staff';
    const body = {
      email: clean(dom.staffEmail.value),
      role,
      organizationId: role === 'super_admin' ? 1 : Number(dom.staffOrganization.value)
    };
    const response = await mutateStaffUser('/api/asap/staff/users', {
      method: 'POST',
      body
    }, 'Staff user saved.');
    if (response) {
      dom.staffEmail.value = '';
    }
  }

  async function updateStaffMetadata(id, version, values) {
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: {
        version: stringValue(version),
        email: values.email,
        displayName: values.displayName,
        notificationEmail: values.notificationEmail
      }
    }, 'Staff profile saved.', id);
  }

  async function changeStaffRole(id, version, values) {
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(id)}/role`, {
      method: 'POST',
      body: {
        version: stringValue(version),
        role: values.role,
        organizationId: values.organizationId
      }
    }, 'Staff access updated.', id);
  }

  async function deactivateStaffUser(id, version, displayName) {
    if (!window.confirm(`Deactivate ${displayName}?`)) return;
    await mutateStaffUser(`/api/asap/staff/users/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      body: { version: stringValue(version) }
    }, 'Staff user deactivated.', id);
  }

  async function reactivateStaffUser(user, role, organizationId) {
    await mutateStaffUser('/api/asap/staff/users', {
      method: 'POST',
      body: {
        email: clean(property(user, 'userPrincipalName')),
        role,
        organizationId: role === 'super_admin' ? 1 : Number(organizationId)
      }
    }, 'Staff user reactivated.', property(user, 'id'));
  }

  function collectScoped(section, fields) {
    const result = {};
    for (const [key, id, kind] of fields) {
      const input = document.getElementById(id);
      if (!input) continue;
      const field = input.closest('[data-setting-section]');
      if (!isSystem()) {
        const toggle = field?.querySelector('.settings-override-toggle');
        if (!toggle?.checked) {
          if (state.baselineOverrides.get(`${section}.${key}`)) result[key] = null;
          continue;
        }
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
    result.enabledLibraryOrgIds = [...dom.enabledLibraries.querySelectorAll('input[type="checkbox"]')]
      .filter(input => input.checked).map(input => Number(input.value));
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
    const raw = templateRows(state.data);
    const systemRows = raw.filter(row => Number(row.organizationId) === 1);
    const libraryRows = raw.filter(row => Number(row.organizationId) === organizationId());
    for (const [key, prefix, subjectId, bodyId] of TEMPLATE_FIELDS) {
      const subject = document.getElementById(subjectId).value;
      const body = document.getElementById(bodyId).value;
      const systemRow = findTemplate(systemRows, key);
      const baseline = state.baselineTemplates.get(key) || {};
      if (isSystem()) {
        if (subject !== (baseline.subject ?? '') || body !== (baseline.body ?? '')) {
          rows.push({ templateKey: key, subject, body });
        }
        continue;
      }
      const fieldset = root.querySelector(`[data-template-key="${key}"]`);
      const overrideToggle = fieldset.querySelector('.settings-template-override');
      const libraryRow = libraryRows.find(row =>
        String(row.sourceTemplateId || '') === String(systemRow?.id || '') && !row.isCustom);
      if (!overrideToggle?.checked) {
        if (libraryRow) rows.push({
          templateKey: key,
          sourceTemplateId: String(systemRow?.id || libraryRow.sourceTemplateId || ''),
          reset: true
        });
        continue;
      }
      const row = {
        templateKey: key,
        sourceTemplateId: String(systemRow?.id || libraryRow?.sourceTemplateId || '')
      };
      if (subject !== (baseline.subject ?? '')) row.subject = subject;
      if (body !== (baseline.body ?? '')) row.body = body;
      if (Object.keys(row).length > 2) rows.push(row);
    }
    if (domainData?.domainsChanged?.templates) {
      for (const item of domainData.templates) {
        if (item.isCustom) {
          rows.push({
            templateKey: item.templateKey,
            displayName: item.displayName,
            subject: item.subject,
            body: item.body,
            enabled: item.enabled,
            isCustom: true
          });
          continue;
        }
        const row = {
          templateKey: item.templateKey,
          sourceTemplateId: item.sourceTemplateId || undefined
        };
        if (item.reset) {
          row.reset = true;
        } else {
          if (item.subjectChanged) row.subject = item.subject;
          if (item.bodyChanged) row.body = item.body;
          if (item.displayNameChanged) row.displayName = item.displayName;
          if (item.enabledChanged) row.enabled = item.enabled;
        }
        if (row.reset || item.overridden && !item.hadOverride || Object.keys(row).length > 2) rows.push(row);
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
    const brandingField = brandingInput.closest('[data-setting-section]');
    const branding = {};
    if (isSystem()) {
      if (brandingInput.value !== state.baselineSnapshot.find(item => item[0] === 'branding-alt')?.[1]) {
        branding.altText = clean(brandingInput.value);
      }
    } else {
      const toggle = brandingField?.querySelector('.settings-override-toggle');
      if (toggle?.checked) branding.altText = clean(brandingInput.value);
      else if (state.baselineOverrides.get('branding.altText')) branding.altText = null;
    }

    payload.workflow = workflow;
    payload.patron = patron;
    payload.email = email;
    const domainValues = domainData.values;
    if (domainData.domainsChanged.publication) {
      payload.patron.publicationOptions = isSystem() || !domainValues.publicationUseSystem
        ? domainValues.publication
        : [];
    }
    if (domainData.domainsChanged.creators) {
      payload.workflow.commonAuthorsList = isSystem() || !domainValues.creatorsUseSystem
        ? domainValues.creators
        : [];
    }
    if (domainData.domainsChanged.codes) {
      payload.workflow.allowedPatronCodeIds = isSystem() || !domainValues.codesUseSystem
        ? domainValues.codes
        : null;
    }
    if (domainData.domainsChanged.providers) payload.providers = domainValues.providers;
    if (domainData.domainsChanged.formats) payload.formats = domainValues.formats;
    if (domainData.domainsChanged.rules) payload.formatRules = domainValues.rules;
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
    if (dom.logo.files?.length) {
      notify('The selected logo image is still a draft. Use Save logo or remove the image selection before saving settings.', 'error');
      dom.saveLogo.focus();
      return;
    }
    state.saving = true;
    const mutation = beginSettingsOperation('administration-settings-save');
    holdSettingsMutation(mutation);
    dom.save.disabled = true;
    notify('Saving settings...');
    let committed = false;
    try {
      const payload = collectPayload();
      const deletedFormats = state.pendingDeletedFormats.slice();
      const response = await authorizedJson('/api/asap/staff/settings', {
        method: 'POST',
        body: payload,
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      committed = true;
      state.data.version = response?.data?.version || state.data.version;
      state.baselineSnapshot = snapshotForm(dom.form);
      domainEditors.setBaseline();
      state.awaitingReload = true;
      onCommitted();
      notify('Settings saved. Refreshing current values...', 'success');
      for (const format of deletedFormats) {
        if (!isSettingsOperationCurrent(mutation)) return;
        const id = encodeURIComponent(String(format.id));
        const version = encodeURIComponent(String(format.version || ''));
        await authorizedJson(`/api/asap/staff/settings/formats/${id}?version=${version}`, {
          method: 'DELETE',
          signal: mutation.signal
        });
      }
      if (!isSettingsOperationCurrent(mutation)) return;
      const refreshed = await load({ silent: true, owner: mutation });
      if (isSettingsOperationCurrent(mutation)) {
        notify(refreshed ? 'Settings saved.' :
          'Settings saved, but current values could not be refreshed. Reload before editing again.',
        refreshed ? 'success' : 'error');
      }
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return;
      if (committed) {
        notify('Settings saved, but a follow-up action failed. Reload before editing again.', 'error');
        return;
      }
      if (isUnconfirmedMutationFailure(error, mutation)) {
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
      state.saving = false;
      finishSettingsMutation(mutation);
      latestLoads.finish('administration-settings-save', mutation.token);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function resetSettings() {
    if (isSystem() || !state.data || state.awaitingReload || state.pendingMutation) return;
    if (isDirty() && !window.confirm('Discard unsaved changes before resetting inherited overrides?')) return;
    if (!window.confirm('Reset this library\'s inherited overrides to the current system values?')) return;
    const mutation = beginSettingsOperation('administration-settings-reset');
    holdSettingsMutation(mutation);
    let committed = false;
    try {
      const response = await authorizedJson(`/api/asap/staff/settings/reset?organizationId=${encodeURIComponent(organizationId())}`, {
        method: 'POST',
        body: { version: state.data.version },
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      committed = true;
      state.data.version = response?.data?.version || state.data.version;
      state.awaitingReload = true;
      onCommitted();
      notify('Inherited overrides reset. Refreshing current values...', 'success');
      const refreshed = await load({ silent: true, owner: mutation });
      if (isSettingsOperationCurrent(mutation)) notify(refreshed ? 'Inherited overrides reset.' :
        'Inherited overrides reset, but current values could not be refreshed. Reload before editing.',
      refreshed ? 'success' : 'error');
    } catch (error) {
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
      latestLoads.finish('administration-settings-reset', mutation.token);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function saveLogo(clear = false) {
    if (!state.data || state.awaitingReload || state.pendingMutation) return;
    const withoutLogo = rows => rows.filter(([id]) => id !== 'branding-logo');
    const file = dom.logo.files?.[0];
    const hasOtherDraft = !sameSnapshot(withoutLogo(state.baselineSnapshot), withoutLogo(snapshotForm(dom.form)));
    if ((hasOtherDraft || clear && file) &&
        !window.confirm('Discard unsaved settings changes or image selection before updating the saved logo?')) return;
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
    const mutation = beginSettingsOperation('administration-settings-logo');
    holdSettingsMutation(mutation);
    let committed = false;
    try {
      const scope = encodeURIComponent(String(state.scope));
      const response = await authorizedJson(`/api/asap/staff/settings/logo?orgId=${scope}`, {
        method: 'POST',
        body,
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      committed = true;
      state.data.version = response?.data?.version || state.data.version;
      state.awaitingReload = true;
      releaseLogoDraft();
      dom.logo.value = '';
      dom.brandingPreview.hidden = true;
      dom.brandingPreviewSource.textContent = 'Image change saved. Reloading the effective preview.';
      dom.brandingStatus.textContent = clear ? 'Image cleared; refreshing the effective logo.' : 'Logo saved; refreshing the effective logo.';
      notify(dom.brandingStatus.textContent, 'success');
      onCommitted();
      const refreshed = await load({ silent: true, owner: mutation });
      if (!isSettingsOperationCurrent(mutation)) return;
      dom.brandingStatus.textContent = refreshed
        ? clear ? 'Image cleared; effective logo refreshed.' : 'Logo saved; effective logo refreshed.'
        : 'Image change saved, but effective branding could not be refreshed. Reload before editing.';
      notify(dom.brandingStatus.textContent, refreshed ? 'success' : 'error');
    } catch (error) {
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
      latestLoads.finish('administration-settings-logo', mutation.token);
      if (isSettingsContextCurrent(mutation.context)) {
        dom.saveLogo.disabled = false;
        dom.clearLogo.disabled = false;
        updateDirtyState();
      }
    }
  }

  async function testPolaris() {
    const initialContext = captureSettingsContext();
    if (isDirty()) {
      if (!window.confirm('Test the saved Polaris configuration and discard current unsaved edits?')) return;
      await load({ silent: true });
      if (!isSettingsContextCurrent(initialContext)) return;
    }
    const mutation = beginSettingsOperation('administration-settings-polaris-test');
    dom.testPolaris.disabled = true;
    try {
      const response = await authorizedJson('/api/asap/staff/polaris/test', {
        method: 'POST',
        body: {},
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      const data = response.data || {};
      dom.polarisResult.textContent = data.connected
        ? `Connected; ${data.organizationCount || 0} organizations available.`
        : `Polaris is unavailable${data.errorCode ? ` (${data.errorCode})` : ''}.`;
      notify(data.connected ? 'Polaris connection succeeded.' : 'Polaris connection is unavailable.', data.connected ? 'success' : 'error');
    } catch (error) {
      if (isSettingsOperationCurrent(mutation) && error.status !== 401 && !isAbortError(error)) {
        notify(error.message || 'Polaris connection test failed.', 'error');
      }
    } finally {
      latestLoads.finish('administration-settings-polaris-test', mutation.token);
      dom.testPolaris.disabled = false;
    }
  }

  async function syncOrganizations() {
    const mutation = beginSettingsOperation('administration-settings-organization-sync');
    dom.syncOrganizations.disabled = true;
    try {
      const response = await authorizedJson('/api/asap/staff/organizations/sync', {
        method: 'POST',
        body: {},
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      dom.syncResult.textContent = `Synchronized ${response.data?.received || 0} organizations.`;
      await load({ silent: true });
      if (isSettingsOperationCurrent(mutation)) notify('Polaris organizations synchronized.', 'success');
    } catch (error) {
      if (isSettingsOperationCurrent(mutation) && error.status !== 401 && !isAbortError(error)) {
        notify(error.message || 'Organizations could not be synchronized.', 'error');
      }
    } finally {
      latestLoads.finish('administration-settings-organization-sync', mutation.token);
      dom.syncOrganizations.disabled = false;
    }
  }

  async function setOrganizationActive(organization, active) {
    if (!window.confirm(`${active ? 'Activate' : 'Deactivate'} ${organization.name}?`)) return;
    const mutation = beginSettingsOperation('administration-settings-participation');
    try {
      await authorizedJson(`/api/asap/staff/organizations/${encodeURIComponent(organization.id)}/${active ? 'activate' : 'deactivate'}`, {
        method: 'POST',
        body: { version: organization.version },
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      await load({ silent: true });
      if (isSettingsOperationCurrent(mutation)) notify(`${organization.name} ${active ? 'activated' : 'deactivated'}.`, 'success');
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return;
      if (error.status === 409) {
        await load({ silent: true });
        if (!isSettingsOperationCurrent(mutation)) return;
      }
      if (error.status !== 401 && !isAbortError(error)) notify(error.message || 'Organization participation could not be changed.', 'error');
    } finally {
      latestLoads.finish('administration-settings-participation', mutation.token);
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
    if (isDirty() && !window.confirm('Discard unsaved settings changes and switch scope?')) {
      dom.scope.value = state.scope;
      return;
    }
    cancelSettingsOperations();
    releaseLogoDraft();
    copyGeneration += 1;
    state.scope = next;
    state.data = null;
    dom.form.hidden = true;
    onScopeChange?.(next);
    state.staffUsers = [];
    state.staffAudit = [];
    state.staffAccessLoaded = false;
    state.lastStaffCleanup = null;
    renderStaffUsers();
    renderStaffAudit();
    await load();
  }

  function activatePanel(name, updateUrl = false) {
    if (!dom.nav.some(button => button.dataset.settingsPanel === name) ||
        !state.staff || state.staff.role === 'staff') return false;
    state.activePanel = name;
    for (const button of dom.nav) {
      const active = button.dataset.settingsPanel === name;
      button.setAttribute('aria-selected', String(active));
      button.classList.toggle('active', active);
      button.tabIndex = active ? 0 : -1;
    }
    for (const panel of dom.panels) panel.hidden = panel.dataset.settingsPanelContent !== name;
    const panel = dom.panels.find(item => item.dataset.settingsPanelContent === name);
    panel?.focus({ preventScroll: true });
    if (name === 'staff') void loadStaffAccess();
    if (updateUrl) onPanelChange?.(name);
    return true;
  }

  function populateScopeOptions() {
    const staffScope = state.staff?.role === 'super_admin'
      ? null
      : clean(state.staff?.organizationId);
    const selected = staffScope || state.scope;
    dom.scope.replaceChildren(node('option', { value: 'system', text: 'System level' }));
    if (staffScope && Number(staffScope) > 1) {
      const organization = state.organizations.find(item => String(item.id) === staffScope);
      dom.scope.append(node('option', {
        value: staffScope,
        text: organization?.name || `Library ${staffScope}`
      }));
    } else {
      for (const organization of state.organizations.filter(item => Number(item.id) > 1)) {
        dom.scope.append(node('option', { value: String(organization.id), text: organization.name }));
      }
      if (state.staff?.role === 'super_admin' && /^\d+$/.test(String(selected)) &&
          ![...dom.scope.options].some(option => option.value === String(selected))) {
        dom.scope.append(node('option', { value: String(selected), text: `Library ${selected}` }));
      }
    }
    dom.scope.value = [...dom.scope.options].some(option => option.value === selected)
      ? selected
      : 'system';
    state.scope = dom.scope.value;
    dom.scopeField.hidden = state.staff?.role !== 'super_admin';
  }

  function setStaff(staff) {
    if (staffContextKey(state.staff) !== staffContextKey(staff)) {
      cancelSettingsOperations();
      releaseLogoDraft();
      copyGeneration += 1;
      state.data = null;
      dom.form.hidden = true;
    }
    state.staff = staff;
    tab.hidden = !staff || staff.role === 'staff';
    if (staff?.role === 'super_admin') {
      if (!state.data) state.scope = 'system';
    } else if (staff) {
      state.scope = String(staff.organizationId);
    }
    state.staffUsers = [];
    state.staffAudit = [];
    state.staffAccessLoaded = false;
    state.lastStaffCleanup = null;
    populateScopeOptions();
    populateStaffCreateControls();
    renderStaffUsers();
    renderStaffAudit();
  }

  function signedOut() {
    cancelSettingsOperations();
    releaseLogoDraft();
    copyGeneration += 1;
    state.visible = false;
    root.hidden = true;
    dom.form.hidden = true;
    state.staff = null;
    state.data = null;
    state.organizations = [];
    state.staffUsers = [];
    state.staffAudit = [];
    state.staffAccessLoaded = false;
    state.lastStaffCleanup = null;
    state.baselineSnapshot = [];
    renderStaffUsers();
    renderStaffAudit();
    updateDirtyState();
  }

  function bind() {
    if (state.bound) return;
    state.bound = true;
    dom.form.addEventListener('submit', saveSettings);
    dom.form.addEventListener('input', updateDirtyState);
    dom.form.addEventListener('change', updateDirtyState);
    const templatesPanel = document.getElementById('settings-templates');
    for (const eventName of ['focusin', 'select', 'keyup', 'mouseup']) {
      templatesPanel.addEventListener(eventName, event => rememberTemplateSelection(event.target));
    }
    dom.scope.addEventListener('change', changeScope);
    dom.switchSystem.addEventListener('click', async () => {
      if (state.staff?.role !== 'super_admin') return;
      dom.scope.value = 'system';
      await changeScope();
      dom.scope.focus();
    });
    for (const [buttonId, value, label] of [
      ['copy-patron-public-url', dom.publicUrl, 'Patron URL'],
      ['copy-patron-iframe', dom.iframeMarkup, 'Iframe markup'],
      ['copy-patron-loader', dom.loaderMarkup, 'Auto-resizing loader markup']
    ]) {
      root.querySelector(`#${buttonId}`).addEventListener('click', () => copyPublicValue(value.value, label));
    }
    dom.logo.addEventListener('change', previewLogoFile);
    dom.discardLogoDraft.addEventListener('click', () => {
      releaseLogoDraft();
      dom.logo.value = '';
      dom.discardLogoDraft.hidden = true;
      dom.brandingStatus.textContent = 'Unsaved image selection removed.';
      renderBrandingPreview();
      updateDirtyState();
    });
    document.getElementById('branding-alt').addEventListener('input', () => renderBrandingPreview(Boolean(logoDraftUrl)));
    for (const button of dom.nav) {
      button.tabIndex = button.dataset.settingsPanel === state.activePanel ? 0 : -1;
      button.addEventListener('click', () => activatePanel(button.dataset.settingsPanel, true));
      button.addEventListener('keydown', event => {
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
    dom.refresh.addEventListener('click', async () => {
      if (state.pendingMutation) {
        notify('Wait for the settings change to finish before reloading.', 'warning');
        return;
      }
      if (isDirty() && !window.confirm('Discard unsaved settings changes and reload?')) return;
      await load();
    });
    dom.discard.addEventListener('click', async () => {
      if (state.pendingMutation) {
        notify('Wait for the settings change to finish before discarding changes.', 'warning');
        return;
      }
      if (!window.confirm('Discard unsaved settings changes?')) return;
      await load({ silent: true });
    });
    dom.reset.addEventListener('click', resetSettings);
    dom.testPolaris.addEventListener('click', testPolaris);
    dom.syncOrganizations.addEventListener('click', syncOrganizations);
    dom.staffRole.addEventListener('change', populateStaffCreateControls);
    dom.staffCreate.addEventListener('click', createStaffUser);
    dom.staffRefresh.addEventListener('click', () => loadStaffAccess());
    dom.staffAuditRefresh.addEventListener('click', () => loadStaffAccess());
    dom.saveLogo.addEventListener('click', () => saveLogo(false));
    dom.clearLogo.addEventListener('click', () => saveLogo(true));
    document.getElementById('email-postmark-token').addEventListener('input', event => {
      if (event.target.value) document.getElementById('email-clear-postmark-token').checked = false;
    });
    document.getElementById('email-clear-postmark-token').addEventListener('change', event => {
      if (event.target.checked) document.getElementById('email-postmark-token').value = '';
    });
    for (const [sourceId, targetId] of [
      ['email-from-address', 'email-template-from-address'],
      ['email-from-name', 'email-template-from-name']
    ]) {
      const source = document.getElementById(sourceId);
      const target = document.getElementById(targetId);
      const key = sourceId === 'email-from-address' ? 'fromAddress' : 'fromName';
      source.addEventListener('input', () => {
        target.value = source.value;
        updateDirtyState();
      });
      target.addEventListener('input', () => {
        source.value = target.value;
        updateDirtyState();
      });
      source.addEventListener('change', () => {
        const toggle = source.closest('[data-setting-section]')?.querySelector('.settings-override-toggle');
        if (toggle) syncOverrideToggles('email', key, toggle.checked);
      });
      target.addEventListener('change', () => {
        const toggle = target.closest('[data-setting-section]')?.querySelector('.settings-override-toggle');
        if (toggle) syncOverrideToggles('email', key, toggle.checked);
      });
    }
    window.addEventListener('beforeunload', event => {
      if (!isDirty() && !state.pendingMutation && !state.outcomeUncertain) return;
      event.preventDefault();
      event.returnValue = '';
    });
  }

  async function activate(panel = state.activePanel) {
    if (!state.staff || state.staff.role === 'staff') return;
    state.visible = true;
    activatePanel(panel);
    populateScopeOptions();
    if (!state.data || String(state.data.orgId) !== String(state.scope)) await load();
    else configureScopedFields();
  }

  function suspend() {
    if (state.pendingMutation || state.outcomeUncertain) return false;
    state.visible = false;
    releaseLogoDraft();
    dom.logo.value = '';
    dom.discardLogoDraft.hidden = true;
    renderBrandingPreview();
    updateDirtyState();
    copyGeneration += 1;
    latestLoads.begin('administration-settings').abort();
    latestLoads.begin('administration-staff-access').abort();
    return true;
  }

  function setScopeFromUrl(scope) {
    if (state.pendingMutation || state.outcomeUncertain) return false;
    if (state.scope !== scope) {
      state.data = null;
      dom.form.hidden = true;
    }
    state.scope = scope;
    return true;
  }

  return {
    bind,
    setStaff,
    signedOut,
    activate,
    activatePanel,
    suspend,
    setScopeFromUrl,
    load,
    isDirty,
    hasPendingMutation: () => Boolean(state.pendingMutation),
    hasUnconfirmedOutcome: () => state.outcomeUncertain,
    currentScope: () => state.scope,
    currentPanel: () => state.activePanel
  };
}
