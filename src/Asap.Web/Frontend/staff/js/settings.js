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
  ['fromName', 'email-from-name'],
  ['postmarkToken', 'email-postmark-token']
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
  'administration-settings-participation'
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
  return [...form.elements].map(control => [
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
  getStaff
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
    saveLogo: root.querySelector('#save-branding-logo'),
    clearLogo: root.querySelector('#clear-branding-logo'),
    logo: root.querySelector('#branding-logo'),
    brandingStatus: root.querySelector('#branding-status'),
    saveTitle: root.querySelector('#settings-save-title'),
    saveDetail: root.querySelector('#settings-save-detail')
  };

  const state = {
    staff: null,
    data: null,
    organizations: [],
    scope: 'system',
    activePanel: 'start',
    baselineSnapshot: [],
    baselineEditors: new Map(),
    baselineTemplates: new Map(),
    baselineOverrides: new Map(),
    pendingDeletedFormats: [],
    bound: false
  };

  const domainEditors = createSettingsDomainEditors({
    root,
    onChange: updateDirtyState
  });

  function isSystem() {
    return state.scope === 'system';
  }

  function staffContextKey(staff) {
    if (!staff) return '';
    return [
      property(staff, 'id'),
      staff.tenantId ?? staff.entraTenantId ?? staff.EntraTenantId,
      staff.objectId ?? staff.entraObjectId ?? staff.EntraObjectId,
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

  function cancelSettingsOperations() {
    for (const slot of SETTINGS_OPERATION_SLOTS) latestLoads.begin(slot).abort();
  }

  function organizationId() {
    return isSystem() ? 1 : Number(state.scope);
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
    dom.save.disabled = !dirty;
    dom.discard.hidden = !dirty;
    dom.saveTitle.textContent = dirty ? 'Unsaved changes' : 'No changes';
    dom.saveDetail.textContent = dirty
      ? 'Changes are local until you save this settings context.'
      : 'Everything in this settings context is saved.';
    dom.reset.hidden = isSystem();
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
    if (section === 'email' && key === 'postmarkToken') {
      return Boolean(property(rawSection(section), 'hasPostmarkToken'));
    }
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
      dom.organizations.append(node('p', { className: 'settings-empty', text: 'No organizations are available.' }));
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

  function populate(data) {
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
    setCollections(data);
    writeControl(document.getElementById('branding-alt'), property(
      isSystem() ? configuredSystem.branding : effective,
      'logoAltText'
    ) || property(isSystem() ? configuredSystem.branding : libraryOverride.branding, 'altText'));
    setTemplates(data);
    setRejectionTemplateOptions(data, workflow);

    state.baselineOverrides.clear();
    for (const [section, fields] of [['workflow', WORKFLOW_FIELDS], ['patron', PATRON_FIELDS], ['email', EMAIL_FIELDS], ['branding', [['altText', 'branding-alt']]]]) {
      for (const [key] of fields) state.baselineOverrides.set(`${section}.${key}`, hasRawOverride(section, key));
    }
    configureScopedFields();
    populateParticipation();
    renderOrganizations();
    dom.scope.value = state.scope;
    state.baselineSnapshot = snapshotForm(dom.form);
    updateDirtyState();
  }

  function currentScopeForRequest() {
    return encodeURIComponent(state.scope || 'system');
  }

  async function load(options = {}) {
    if (!state.staff || state.staff.role === 'staff') return;
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
      if (!loadState.isCurrent() || !isSettingsContextCurrent(context)) return;
      const data = settingsResponse?.data && settingsResponse?.version === undefined
        ? settingsResponse.data
        : settingsResponse;
      const organizations = organizationsResponse?.data ?? organizationsResponse;
      state.organizations = Array.isArray(organizations) ? organizations : [];
      populateScopeOptions();
      const patronCodeChoices = patronCodesResponse?.data ?? patronCodesResponse;
      data.patronCodeChoices = Array.isArray(patronCodeChoices) ? patronCodeChoices : [];
      populate(data || {});
      if (!options.silent && loadState.isCurrent() && isSettingsContextCurrent(context)) notify('Settings loaded.');
    } catch (error) {
      if (loadState.isCurrent() && isSettingsContextCurrent(context) && !isAbortError(error) && error.status !== 401) {
        notify(error.message || 'Settings could not be loaded.', 'error');
      }
    } finally {
      if (loadState.isCurrent() && isSettingsContextCurrent(context)) dom.refresh.disabled = false;
      latestLoads.finish('administration-settings', loadState.token);
    }
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
      const subject = clean(document.getElementById(subjectId).value);
      const body = clean(document.getElementById(bodyId).value);
      const systemRow = findTemplate(systemRows, key);
      if (isSystem()) {
        rows.push({ templateKey: key, subject, body });
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
      const baseline = state.baselineTemplates.get(key) || {};
      const row = {
        templateKey: key,
        sourceTemplateId: String(systemRow?.id || libraryRow?.sourceTemplateId || '')
      };
      if (subject !== (baseline.subject || null)) row.subject = subject;
      if (body !== (baseline.body || null)) row.body = body;
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
        : [];
    }
    if (domainData.domainsChanged.providers) payload.providers = domainValues.providers;
    if (domainData.domainsChanged.formats) payload.formats = domainValues.formats;
    if (domainData.domainsChanged.rules) payload.formatRules = domainValues.rules;
    if (domainData.domainsChanged.fields && !isSystem()) payload.customFields = domainValues.fields;
    if (domainData.domainsChanged.claims && !isSystem()) payload.autoClaimRules = domainValues.claims;
    if (document.getElementById('email-clear-postmark-token').checked) {
      payload.email.clearPostmarkToken = true;
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
    if (!state.data || !isDirty()) return;
    const mutation = beginSettingsOperation('administration-settings-save');
    dom.save.disabled = true;
    notify('Saving settings...');
    try {
      const payload = collectPayload();
      const deletedFormats = state.pendingDeletedFormats.slice();
      const response = await authorizedJson('/api/asap/staff/settings', {
        method: 'POST',
        body: payload,
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
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
      await load({ silent: true });
      if (isSettingsOperationCurrent(mutation)) notify(response?.data?.version ? 'Settings saved.' : 'Settings saved.', 'success');
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return;
      if (error.status === 409) {
        const stale = error.response?.code === 'stale_version';
        if (stale) {
          await load({ silent: true });
          if (isSettingsOperationCurrent(mutation)) {
            notify('These settings changed elsewhere. Review the refreshed values before saving again.', 'error');
          }
        } else {
          notify(error.message || 'The settings could not be saved.', 'error');
        }
      } else if (error.status !== 401 && !isAbortError(error)) {
        notify(error.message || 'The settings could not be saved.', 'error');
      }
    } finally {
      latestLoads.finish('administration-settings-save', mutation.token);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function resetSettings() {
    if (isSystem() || !state.data) return;
    if (isDirty() && !window.confirm('Discard unsaved changes before resetting inherited overrides?')) return;
    if (!window.confirm('Reset this library\'s inherited overrides to the current system values?')) return;
    const mutation = beginSettingsOperation('administration-settings-reset');
    try {
      await authorizedJson(`/api/asap/staff/settings/reset?organizationId=${encodeURIComponent(organizationId())}`, {
        method: 'POST',
        body: { version: state.data.version },
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      await load({ silent: true });
      if (isSettingsOperationCurrent(mutation)) notify('Inherited overrides reset.', 'success');
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return;
      if (error.status === 409) {
        await load({ silent: true });
        if (isSettingsOperationCurrent(mutation)) {
          notify(error.message || 'The settings changed elsewhere. Review the refreshed values.', 'error');
        }
      } else if (error.status !== 401) notify(error.message || 'The inherited overrides could not be reset.', 'error');
    } finally {
      latestLoads.finish('administration-settings-reset', mutation.token);
      if (isSettingsContextCurrent(mutation.context)) updateDirtyState();
    }
  }

  async function saveLogo(clear = false) {
    if (!state.data) return;
    const file = dom.logo.files?.[0];
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
    try {
      const scope = encodeURIComponent(String(state.scope));
      await authorizedJson(`/api/asap/staff/settings/logo?orgId=${scope}`, {
        method: 'POST',
        body,
        signal: mutation.signal
      });
      if (!isSettingsOperationCurrent(mutation)) return;
      await load({ silent: true });
      if (!isSettingsOperationCurrent(mutation)) return;
      dom.brandingStatus.textContent = clear ? 'Logo image cleared.' : 'Logo image saved.';
      notify(clear ? 'Logo image cleared.' : 'Logo image saved.', 'success');
    } catch (error) {
      if (!isSettingsOperationCurrent(mutation)) return;
      if (error.status === 409) {
        await load({ silent: true });
        if (!isSettingsOperationCurrent(mutation)) return;
      }
      if (error.status !== 401 && !isAbortError(error)) {
        dom.brandingStatus.textContent = error.message || 'Logo could not be saved.';
        notify(error.message || 'Logo could not be saved.', 'error');
      }
    } finally {
      latestLoads.finish('administration-settings-logo', mutation.token);
      dom.saveLogo.disabled = false;
      dom.clearLogo.disabled = false;
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
    if (isDirty() && !window.confirm('Discard unsaved settings changes and switch scope?')) {
      dom.scope.value = state.scope;
      return;
    }
    cancelSettingsOperations();
    state.scope = next;
    await load();
  }

  function activatePanel(name) {
    state.activePanel = name;
    for (const button of dom.nav) {
      const active = button.dataset.settingsPanel === name;
      button.setAttribute('aria-selected', String(active));
      button.classList.toggle('active', active);
    }
    for (const panel of dom.panels) panel.hidden = panel.dataset.settingsPanelContent !== name;
    const panel = dom.panels.find(item => item.dataset.settingsPanelContent === name);
    panel?.focus({ preventScroll: true });
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
    }
    dom.scope.value = [...dom.scope.options].some(option => option.value === selected)
      ? selected
      : 'system';
    state.scope = dom.scope.value;
    dom.scopeField.hidden = state.staff?.role !== 'super_admin';
  }

  function setStaff(staff) {
    if (staffContextKey(state.staff) !== staffContextKey(staff)) cancelSettingsOperations();
    state.staff = staff;
    tab.hidden = !staff || staff.role === 'staff';
    if (staff?.role === 'super_admin') {
      if (!state.data) state.scope = 'system';
    } else if (staff) {
      state.scope = String(staff.organizationId);
    }
    populateScopeOptions();
  }

  function signedOut() {
    cancelSettingsOperations();
    state.staff = null;
    state.data = null;
    state.organizations = [];
    state.baselineSnapshot = [];
    updateDirtyState();
  }

  function bind() {
    if (state.bound) return;
    state.bound = true;
    dom.form.addEventListener('submit', saveSettings);
    dom.form.addEventListener('input', updateDirtyState);
    dom.form.addEventListener('change', updateDirtyState);
    dom.scope.addEventListener('change', changeScope);
    for (const button of dom.nav) {
      button.addEventListener('click', () => activatePanel(button.dataset.settingsPanel));
      button.addEventListener('keydown', event => {
        if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const index = dom.nav.indexOf(button);
        const offset = event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1;
        dom.nav[(index + offset + dom.nav.length) % dom.nav.length].focus();
      });
    }
    dom.refresh.addEventListener('click', async () => {
      if (isDirty() && !window.confirm('Discard unsaved settings changes and reload?')) return;
      await load();
    });
    dom.discard.addEventListener('click', async () => {
      if (!window.confirm('Discard unsaved settings changes?')) return;
      await load({ silent: true });
    });
    dom.reset.addEventListener('click', resetSettings);
    dom.testPolaris.addEventListener('click', testPolaris);
    dom.syncOrganizations.addEventListener('click', syncOrganizations);
    dom.saveLogo.addEventListener('click', () => saveLogo(false));
    dom.clearLogo.addEventListener('click', () => saveLogo(true));
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
      if (!isDirty()) return;
      event.preventDefault();
      event.returnValue = '';
    });
  }

  async function activate() {
    if (!state.staff || state.staff.role === 'staff') return;
    populateScopeOptions();
    if (!state.data || String(state.data.orgId) !== String(state.scope)) await load();
    else configureScopedFields();
  }

  return {
    bind,
    setStaff,
    signedOut,
    activate,
    load,
    isDirty
  };
}
