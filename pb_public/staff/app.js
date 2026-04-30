const pb = new PocketBase(window.location.origin);
const SETTINGS_RECORD_ID = 'settings0000001';

const loginContainer = document.getElementById('login-container');
const setupContainer = document.getElementById('setup-container');
const appContainer = document.getElementById('app-container');
const loginForm = document.getElementById('login-form');
const setupForm = document.getElementById('setup-form');
const logoutBtn = document.getElementById('logout-btn');
const profileBtn = document.getElementById('profile-btn');
const gridContainer = document.getElementById('grid-container');
const settingsContainer = document.getElementById('settings-container');
const settingsForm = document.getElementById('settings-form');
let grid;

let formatMap = {
  book: 'Book', ebook: 'eBook', audiobook_cd: 'Audiobook (Physical CD)',
  eaudiobook: 'eAudiobook', dvd: 'DVD', music_cd: 'Music CD'
};
let availableFormats = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
const ageMap = { adult: 'Adult', teen: 'Teen', children: 'Children' };
let currentRejectionTemplates = [];
const closeReasonMap = {
  rejected: 'Rejected by staff',
  hold_completed: 'Hold placed / completed',
  manual: 'Manually closed'
};
const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
const defaultAgeGroups = ['Adult', 'Young Adult / Teen', 'Children'];
const duplicateStatusLabelDefaults = {
  suggestion: 'Received',
  outstanding_purchase: 'Under review',
  pending_hold: 'Being prepared',
  hold_placed: 'Hold placed',
  closed: 'Completed',
  rejected: 'Not selected for purchase',
  hold_completed: 'Completed',
  hold_not_picked_up: 'Closed',
  manual: 'Closed',
  silent: 'Closed',
  'Silently Closed': 'Closed'
};
const duplicateStatusLabelFields = [
  ['suggestion', 'Received suggestion'],
  ['outstanding_purchase', 'Pending purchase'],
  ['pending_hold', 'Pending hold'],
  ['hold_placed', 'Hold placed'],
  ['closed', 'Closed'],
  ['rejected', 'Rejected outcome'],
  ['hold_completed', 'Fulfilled outcome'],
  ['hold_not_picked_up', 'Hold not picked up'],
  ['manual', 'Manual close'],
  ['silent', 'Silent close']
];
const patronFormatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
const patronFormatFields = [
  { key: 'title', label: 'Title (original)', storage: 'title' },
  { key: 'author', label: 'Author (original)', storage: 'author' },
  { key: 'identifier', label: 'Identifier', storage: 'identifier' },
  { key: 'agegroup', label: 'Age group', storage: 'agegroup' },
  { key: 'publication', label: 'Publication timing', storage: 'publication' }
];
const defaultPatronFormatRules = {
  book: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  },
  audiobook_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  },
  dvd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Director/Actors/Producer' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  },
  music_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Artist' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  },
  ebook: {
    messageBehavior: 'ebookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  },
  eaudiobook: {
    messageBehavior: 'eaudiobookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age group' },
      publication: { mode: 'required', label: 'Publication timing' }
    }
  }
};

const descriptions = {
  suggestion: 'Suggestions submitted by patrons and awaiting staff review. Review each suggestion and choose Purchase, Already own, Reject, or Silent close.',
  outstanding_purchase: 'Pending purchase contains approved suggestions that are waiting to appear in Polaris. If the auto-promoter is enabled, ASAP will check Polaris automatically. Staff can also add a BIB ID manually.',
  pending_hold: 'Pending hold contains suggestions with catalog matches that are waiting for hold placement. ASAP can place holds automatically during the hold check. Staff should confirm BIB IDs and resolve skipped items.',
  hold_placed: 'Hold placed contains active holds ASAP is tracking for checkout or pickup completion. Staff should review items that do not close automatically.',
  closed: 'Closed suggestions include rejected, silent-closed, fulfilled, or auto-closed suggestions. Use this tab to review outcomes or undo a closure when needed.',
  settings: 'Settings control staff access, patron experience, workflow automation, Polaris, SMTP, and email templates.'
};
const emptyStateMessages = {
  suggestion: 'No new suggestions need review.',
  outstanding_purchase: 'No approved suggestions are waiting for catalog matches.',
  pending_hold: 'No items are waiting for hold placement.',
  hold_placed: 'No active holds are being tracked.',
  closed: 'No closed suggestions found.'
};

const statusStages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const stageQueryMap = {
  submitted: 'suggestion',
  suggestion: 'suggestion',
  new: 'suggestion',
  purchased_waiting_for_bib: 'outstanding_purchase',
  outstanding_purchase: 'outstanding_purchase',
  pending_hold: 'pending_hold',
  hold_placed: 'hold_placed',
  closed: 'closed'
};

let currentStatus = requestedStatusFromUrl() || 'suggestion';
let currentSuggestions = [];
let allSuggestions = [];
let verifiedNewSuggestionBarcode = '';
let verifiedBibId = '';
let publicationOptions = defaultPublicationOptions.slice();
let workflowSettings = {
  autoPromote: false,
  outstandingTimeoutEnabled: false,
  outstandingTimeoutDays: 30,
  outstandingTimeoutSendEmail: false,
  outstandingTimeoutRejectionTemplateId: '',
  holdPickupTimeoutEnabled: false,
  holdPickupTimeoutDays: 14,
  pendingHoldTimeoutEnabled: false,
  pendingHoldTimeoutDays: 14
};
let currentLibraryContextOrgId = 'system';
let libraryTemplateOverrides = {}; // Map of orgId -> isOverride (boolean)
let libraryContextLoadSerial = 0;
let librarySelectorBound = false;
let bootstrapAdminMessage = '';
let setupRequired = false;
let canAssignSuperAdmin = false;
let currentEmailStatus = { enabled: true };
let organizationsStatus = 'not_loaded';
let organizationsStatusMessage = 'Polaris organizations have not been loaded yet. Organization selection will be available after the Polaris organization sync completes.';
const settingsSectionIds = ['start', 'polaris', 'staff', 'smtp', 'workflow', 'patron', 'templates'];
let currentSettingsSection = 'start';
let settingsDirty = false;
let settingsSaving = false;
let settingsLoading = false;
let activeActionMenu = null;
let rowActionIdCounter = 0;
const rowActionRegistry = new Map();

// --- DOM Field Helpers ---

function setFieldValue(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = value;
}

function setFieldChecked(id, checked) {
  const el = document.getElementById(id);
  if (el) el.checked = checked;
}

function getFieldValue(id, fallback = '') {
  const el = document.getElementById(id);
  return el ? el.value : fallback;
}

function getFieldChecked(id, fallback = false) {
  const el = document.getElementById(id);
  return el ? el.checked : fallback;
}

function requestedStatusFromUrl() {
  try {
    const params = new URLSearchParams(window.location.search || '');
    const raw = String(params.get('stage') || params.get('status') || '').trim();
    return stageQueryMap[raw] || '';
  } catch (err) {
    return '';
  }
}

function updateStageQuery(status) {
  if (!statusStages.includes(status)) return;
  try {
    const url = new URL(window.location.href);
    url.searchParams.set('stage', status === 'suggestion' ? 'submitted' : status);
    window.history.replaceState(null, '', url.pathname + url.search + url.hash);
  } catch (err) {}
}

function isValidSmtpHost(host) {
  const value = String(host || '').trim();
  if (!value) return false;
  if (value.toLowerCase() === 'localhost') return true;
  if (value.includes('://') || value.includes('/') || value.includes(':') || /\s/.test(value)) return false;
  const ipv4Pattern = /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/;
  if (ipv4Pattern.test(value)) return true;
  const labels = value.split('.');
  if (labels.length < 2) return false;
  return labels.every(label => /^[a-z0-9-]{1,63}$/i.test(label) && !label.startsWith('-') && !label.endsWith('-'));
}

function validateSmtpHostField(showMessage = false) {
  const host = getFieldValue('smtp-host').trim();
  const resultEl = document.getElementById('smtp-test-result');
  if (!host || isValidSmtpHost(host)) {
    if (showMessage && resultEl) {
      resultEl.textContent = '';
      resultEl.className = 'd-block mt-2';
    }
    return true;
  }
  if (showMessage && resultEl) {
    resultEl.textContent = 'Enter a valid SMTP host (DNS name or IP only, no protocol or port).';
    resultEl.className = 'mt-2 text-danger font-weight-bold small';
  }
  return false;
}

function setVisible(id, visible) {
  const el = document.getElementById(id);
  if (el) el.classList.toggle('hidden', !visible);
}

function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value || '';
}

function setDisabled(id, disabled) {
  const el = document.getElementById(id);
  if (el) el.disabled = !!disabled;
}

function setInlineStatus(id, message, type) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = message || '';
  el.className = type ? `text-${type} font-weight-bold` : '';
}

// --- In-page Toast / Dialog Helpers ---

function showToast(message, type = 'success') {
  const container = document.getElementById('toast-container');
  if (!container) return;
  const toast = document.createElement('div');
  toast.className = `asap-toast asap-toast-${type}`;
  toast.textContent = message;
  container.appendChild(toast);
  requestAnimationFrame(() => toast.classList.add('show'));
  setTimeout(() => {
    toast.classList.remove('show');
    setTimeout(() => toast.remove(), 300);
  }, 3500);
}

function showAlert(message) {
  return new Promise(resolve => {
    const dialog = document.getElementById('alert-dialog');
    if (!dialog) return resolve();
    const previousFocus = document.activeElement;
    document.getElementById('alert-dialog-message').textContent = message;
    const okBtn = document.getElementById('alert-dialog-ok');
    let settled = false;
    function cleanup() {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      okBtn.removeEventListener('click', onOk);
      dialog.removeEventListener('cancel', onCancel);
      if (previousFocus && typeof previousFocus.focus === 'function') {
        previousFocus.focus();
      }
      resolve();
    }
    function onOk() {
      cleanup();
    }
    function onCancel(event) {
      event.preventDefault();
      cleanup();
    }
    okBtn.addEventListener('click', onOk);
    dialog.addEventListener('cancel', onCancel);
    dialog.showModal();
    okBtn.focus();
  });
}

function showConfirm(titleOrMessage, maybeMessage) {
  return new Promise(resolve => {
    const dialog = document.getElementById('confirm-dialog');
    if (!dialog) return resolve(false);
    const previousFocus = document.activeElement;
    const message = maybeMessage || titleOrMessage;
    const title = maybeMessage ? titleOrMessage : 'Confirm action';
    const titleEl = document.getElementById('confirm-dialog-title');
    if (titleEl) titleEl.textContent = title;
    document.getElementById('confirm-dialog-message').textContent = message;
    const okBtn = document.getElementById('confirm-dialog-ok');
    const cancelBtn = document.getElementById('confirm-dialog-cancel');
    let settled = false;
    function cleanup(result) {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      okBtn.removeEventListener('click', onOk);
      cancelBtn.removeEventListener('click', onCancel);
      dialog.removeEventListener('cancel', onDialogCancel);
      if (previousFocus && typeof previousFocus.focus === 'function') {
        previousFocus.focus();
      }
      resolve(result);
    }
    function onOk() { cleanup(true); }
    function onCancel() { cleanup(false); }
    function onDialogCancel(event) {
      event.preventDefault();
      cleanup(false);
    }
    okBtn.addEventListener('click', onOk);
    cancelBtn.addEventListener('click', onCancel);
    dialog.addEventListener('cancel', onDialogCancel);
    dialog.showModal();
    cancelBtn.focus();
  });
}

function staffRole() {
  return pb.authStore.model ? String(pb.authStore.model.role || '').toLowerCase() : '';
}

function isSuperAdminStaff() {
  return staffRole() === 'super_admin';
}

function isAdminStaff() {
  return ['admin', 'super_admin'].includes(staffRole());
}

function getSettingsSectionFromHash() {
  const hash = window.location.hash || '';
  const prefix = '#settings-';
  if (!hash.startsWith(prefix)) {
    return '';
  }

  const section = decodeURIComponent(hash.slice(prefix.length));
  return settingsSectionIds.includes(section) ? section : '';
}

function activateStatusTab(status) {
  currentStatus = status;
  document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
    const isActive = link.getAttribute('data-status') === status;
    link.classList.toggle('active', isActive);
    if (link.hasAttribute('role')) {
      link.setAttribute('aria-selected', isActive ? 'true' : 'false');
    }
  });
}

function updateSaveBarState(state) {
  const title = document.getElementById('settings-save-title');
  const detail = document.getElementById('settings-save-detail');
  const msg = document.getElementById('settings-msg');
  const effectiveState = state || (settingsDirty ? 'dirty' : 'clean');
  const states = {
    clean: ['No changes', 'Everything in the current settings context is saved.', 'text-muted'],
    dirty: ['Unsaved changes', 'Save before leaving this library context to keep your edits.', 'text-warning'],
    saving: ['Saving...', 'Please wait while ASAP applies these settings.', 'text-info'],
    saved: ['Saved', 'Your settings were saved successfully.', 'text-success'],
    error: ['Error saving', 'Review the message below and try again.', 'text-danger']
  };
  const next = states[effectiveState] || states.clean;
  if (title) title.textContent = next[0];
  if (detail) {
    detail.textContent = next[1];
    detail.className = 'small ' + next[2];
  }
  setDisabled('settings-save-btn', settingsSaving || effectiveState === 'clean');
  if (msg && effectiveState === 'clean') {
    msg.textContent = '';
    msg.className = 'mt-2 font-weight-bold';
  }
}

function markSettingsDirty() {
  if (settingsLoading || settingsSaving) return;
  settingsDirty = true;
  updateSaveBarState('dirty');
}

function markSettingsClean(state = 'clean') {
  settingsDirty = false;
  updateSaveBarState(state);
}

function activateSettingsSection(section, options = {}) {
  const targetSection = settingsSectionIds.includes(section) ? section : 'start';
  currentSettingsSection = targetSection;

  document.querySelectorAll('[data-settings-section]').forEach(panel => {
    const isActive = panel.getAttribute('data-settings-section') === targetSection;
    panel.classList.toggle('active', isActive);
    panel.setAttribute('aria-hidden', isActive ? 'false' : 'true');
  });

  const overridableSections = ['workflow', 'patron', 'templates'];
  const wrapper = document.getElementById('library-context-wrapper');
  if (wrapper) {
    if (overridableSections.includes(targetSection)) {
      wrapper.classList.remove('hidden');
    } else {
      wrapper.classList.add('hidden');
    }
  }

  document.querySelectorAll('[data-settings-target]').forEach(button => {
    const isActive = button.getAttribute('data-settings-target') === targetSection;
    button.classList.toggle('active', isActive);
    button.setAttribute('aria-selected', isActive ? 'true' : 'false');
  });

  if (options.updateHash) {
    const nextHash = '#settings-' + encodeURIComponent(targetSection);
    if (window.location.hash !== nextHash) {
      window.history.replaceState(null, '', nextHash);
    }
  }

  if (options.focus) {
    const panel = document.getElementById('settings-' + targetSection);
    if (panel) {
      try {
        panel.focus({ preventScroll: true });
      } catch (err) {
        // Older browsers may not support preventScroll; skip focus instead of causing a jump.
      }
    }
  }
}

function initSettingsNavigation() {
  document.querySelectorAll('.settings-nav-link[data-settings-target]').forEach(button => {
    const section = button.getAttribute('data-settings-target');
    button.setAttribute('aria-controls', 'settings-' + section);
    button.setAttribute('aria-selected', section === currentSettingsSection ? 'true' : 'false');
    button.addEventListener('click', () => {
      activateSettingsSection(section, { updateHash: true, focus: true });
    });
  });

  window.addEventListener('hashchange', () => {
    const section = getSettingsSectionFromHash();
    if (!section) {
      return;
    }

    if (pb.authStore.isValid && currentStatus !== 'settings') {
      activateStatusTab('settings');
      loadTab('settings');
      return;
    }

    activateSettingsSection(section, { updateHash: false, focus: true });
  });

  activateSettingsSection(getSettingsSectionFromHash() || currentSettingsSection, { updateHash: false });
}

function showBootstrapAdminMessage() {
  const alert = document.getElementById('bootstrap-admin-alert');
  if (!alert) return;
  if (bootstrapAdminMessage) {
    setText('bootstrap-admin-alert', bootstrapAdminMessage);
    setVisible('bootstrap-admin-alert', true);
  } else {
    setText('bootstrap-admin-alert', '');
    setVisible('bootstrap-admin-alert', false);
  }
}

function updateEmailStatusBanner(status) {
  currentEmailStatus = status || currentEmailStatus || { enabled: true };
  const smtpMessage = document.getElementById('smtp-readiness-message');
  const configured = !!currentEmailStatus.enabled;
  const message = currentEmailStatus.message || 'Email notifications are not configured. Suggestions and staff workflows still work, but patron emails will not be sent.';

  setVisible('email-status-banner', !configured);
  if (smtpMessage) {
    smtpMessage.textContent = message;
    smtpMessage.className = configured ? 'alert alert-success small' : 'alert alert-warning small';
  }
}

function setOrganizationsStatus(status, message) {
  organizationsStatus = status || 'not_loaded';
  organizationsStatusMessage = message || '';
  const statusEl = document.getElementById('organizations-status-message');
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (container && organizationsStatus !== 'loaded') {
    container.removeAttribute('data-loaded');
  }

  if (statusEl) {
    const classMap = {
      not_loaded: 'alert alert-info small mb-3',
      loading: 'alert alert-info small mb-3',
      loaded: 'alert alert-success small mb-3',
      error: 'alert alert-warning small mb-3'
    };
    statusEl.className = classMap[organizationsStatus] || classMap.not_loaded;
    statusEl.textContent = organizationsStatusMessage || 'Polaris organization sync status is unknown.';
  }

  if (container && organizationsStatus === 'loading') {
    container.innerHTML = '<div class="p-3 text-muted">Organizations loading...</div>';
  } else if (container && organizationsStatus === 'error') {
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
  } else if (container && organizationsStatus === 'not_loaded') {
    container.innerHTML = '<div class="p-3 text-muted">Organizations not loaded yet.</div>';
  }
}

async function loadEmailStatus(orgId) {
  if (!pb.authStore.isValid || !pb.authStore.model || pb.authStore.model.collectionName !== 'staff_users') {
    return;
  }

  const defaultOrgId = isSuperAdminStaff() ? 'system' : (pb.authStore.model.libraryOrgId || '');
  const targetOrgId = orgId || defaultOrgId;
  try {
    const result = await authorizedJson(`/api/asap/staff/email-status?orgId=${encodeURIComponent(targetOrgId)}&_=${Date.now()}`, { cache: 'no-store' });
    updateEmailStatusBanner(result);
  } catch (err) {
    console.warn('Failed to load email status', err);
  }
}

function checkAuth() {
  if (pb.authStore.isValid && pb.authStore.model && pb.authStore.model.collectionName === 'staff_users') {
    setupContainer.classList.add('hidden');
    loginContainer.classList.add('hidden');
    appContainer.classList.remove('hidden');
    const libraryName = pb.authStore.model.libraryOrgName || (isSuperAdminStaff() ? 'System' : '');
    const identityLabel = pb.authStore.model.identityKey || pb.authStore.model.username;
    document.getElementById('display-user').textContent = (pb.authStore.model.displayName || identityLabel) + (libraryName ? ` (${libraryName})` : '');
    document.getElementById('nav-settings').classList.remove('hidden');
    showBootstrapAdminMessage();
    loadEmailStatus();

    const requestedSettingsSection = getSettingsSectionFromHash();
    if (requestedSettingsSection) {
      activateStatusTab('settings');
    }

    if (isSuperAdminStaff()) {
      loadSettings({ showErrors: false });
    }

    loadTab(currentStatus);
  } else {
    setupContainer.classList.toggle('hidden', !setupRequired);
    loginContainer.classList.toggle('hidden', setupRequired);
    appContainer.classList.add('hidden');
    bootstrapAdminMessage = '';
    showBootstrapAdminMessage();
  }
}

async function loadSetupStatus() {
  try {
    const res = await fetch('/api/asap/setup/status?t=' + Date.now());
    if (!res.ok) return;
    const status = await res.json();
    setupRequired = !!(status && status.setupRequired);
  } catch (err) {
    console.error('Failed to load setup status', err);
  }
}

function formDataObject(form) {
  return Object.fromEntries(new FormData(form).entries());
}

function setInlineResult(el, message, className) {
  if (!el) return;
  el.textContent = message;
  el.className = className;
}

async function postPolarisTest(url, resultEl, payload, options = {}) {
  const btn = options.button || null;
  if (btn) btn.disabled = true;
  setInlineResult(resultEl, options.pendingText || 'Testing Polaris...', options.pendingClass || 'text-muted');

  try {
    const headers = { 'Content-Type': 'application/json' };
    if (options.token) {
      headers.Authorization = options.token;
    }

    const res = await fetch(url, {
      method: 'POST',
      headers,
      body: JSON.stringify(payload || {})
    });
    const data = await res.json().catch(() => ({}));
    if (res.ok && data.success) {
      setInlineResult(resultEl, options.successText || 'Success! Polaris API is working.', options.successClass || 'text-success font-weight-bold');
      return true;
    }
    setInlineResult(resultEl, 'Error: ' + (data.message || 'Failed'), options.errorClass || 'text-danger font-weight-bold');
    return false;
  } catch (err) {
    setInlineResult(resultEl, options.networkErrorText || 'Error testing Polaris.', options.errorClass || 'text-danger font-weight-bold');
    return false;
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function authorizedJson(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (pb.authStore.token) {
    headers.Authorization = pb.authStore.token;
  }
  if (options.json !== false) {
    headers['Content-Type'] = 'application/json';
  }

  const res = await fetch(path, {
    method: options.method || 'GET',
    headers,
    body: options.body,
    cache: options.cache || 'default'
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(data.message || 'Request failed.');
    err.status = res.status;
    throw err;
  }
  return data;
}

function normalizeAllowedStaffUsers(value) {
  return Array.from(new Set(String(value || '')
    .split(',')
    .map(item => String(item || '').trim().toLowerCase())
    .filter(Boolean)))
    .join(', ');
}

function updateAutoRejectEmailControls() {
  const enabled = getFieldChecked('outstanding-timeout-enabled');
  const sendEmail = getFieldChecked('outstanding-timeout-send-email');
  const additionalTemplates = currentRejectionTemplates || [];
  const wrapper = document.getElementById('auto-reject-email-wrapper');
  const templateWrapper = document.getElementById('auto-reject-template-wrapper');
  const select = document.getElementById('outstanding-timeout-rejection-template-id');
  const warning = document.getElementById('auto-reject-template-warning');
  const help = document.getElementById('auto-reject-template-help');
  if (!wrapper || !select) return;

  wrapper.classList.toggle('hidden', !enabled);
  if (!enabled) {
    setFieldChecked('outstanding-timeout-send-email', false);
    setFieldValue('outstanding-timeout-rejection-template-id', '');
    return;
  }

  warning.classList.add('hidden');
  templateWrapper.classList.toggle('hidden', !sendEmail);
  select.innerHTML = '';
  select.disabled = false;

  if (!sendEmail) return;

  // The "Standard" template is the one in emails.rejected (no ID)
  const standardOption = { id: '', name: 'Standard Rejection Email' };
  const allTemplates = [standardOption, ...additionalTemplates];

  if (allTemplates.length === 1) {
    // Only standard exists
    select.innerHTML = `<option value="">${escapeAttr(standardOption.name)}</option>`;
    select.value = '';
    select.disabled = true;
    help.textContent = 'The system will use the default rejection template for this library.';
    return;
  }

  select.innerHTML = allTemplates.map(tpl =>
    `<option value="${escapeAttr(tpl.id)}">${escapeAttr(tpl.name || tpl.subject || 'Rejection template')}</option>`
  ).join('');
  
  // Restore previous selection if valid, otherwise default to Standard (empty)
  if (workflowSettings.outstandingTimeoutRejectionTemplateId) {
    const stillExists = allTemplates.some(t => t.id === workflowSettings.outstandingTimeoutRejectionTemplateId);
    if (stillExists) {
      select.value = workflowSettings.outstandingTimeoutRejectionTemplateId;
    } else {
      select.value = '';
    }
  } else {
    select.value = '';
  }
  
  help.textContent = 'Select the template to use for auto-rejected suggestions. "Standard Rejection Email" is the default.';
}

loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const errDiv = document.getElementById('login-error');
  errDiv.classList.add('hidden');
  try {
    const data = formDataObject(loginForm);
    const res = await fetch('/api/asap/staff/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    }).then(r => {
      if (!r.ok) {
        const err = new Error('invalid');
        err.response = r;
        throw err;
      }
      return r.json();
    });
    const meta = res.meta || res;
    bootstrapAdminMessage = meta.bootstrapAdmin
      ? (meta.bootstrapMessage || 'This is the first staff login, so your account has been made the admin user. Future staff logins will be created with non-admin staff roles.')
      : '';
    pb.authStore.save(res.token, res.record);
    checkAuth();
  } catch (err) {
    if (err.response && err.response.status === 409) {
      setupRequired = true;
      checkAuth();
      return;
    }
    errDiv.classList.remove('hidden');
  }
});

setupForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = document.getElementById('setup-btn');
  const errDiv = document.getElementById('setup-error');
  btn.disabled = true;
  errDiv.classList.add('hidden');
  errDiv.textContent = '';

  try {
    const data = formDataObject(setupForm);
    const res = await fetch('/api/asap/setup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(data)
    });
    const result = await res.json();
    if (!res.ok) {
      throw new Error(result.message || 'Setup failed.');
    }

	    setupRequired = false;
	    bootstrapAdminMessage = result.bootstrapMessage || 'Initial setup is complete. Your account is the admin user; future staff logins will be non-admin staff accounts.';
	    pb.authStore.save(result.token, result.record);
	    currentStatus = 'settings';
	    currentSettingsSection = 'start';
	    window.history.replaceState(null, '', '#settings-start');
	    setOrganizationsStatus('loading', 'Organizations loading from Polaris. Settings will unlock organization selection after this sync completes.');
	    checkAuth();
	    syncPolarisOrganizations().catch(() => {
	      // The visible organization status already explains the failure.
	    });
	  } catch (err) {
    errDiv.textContent = err.message || 'Setup failed.';
    errDiv.classList.remove('hidden');
  } finally {
    btn.disabled = false;
  }
});

const setupTestPolarisBtn = document.getElementById('setup-test-polaris-btn');
if (setupTestPolarisBtn) {
  setupTestPolarisBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    await postPolarisTest('/api/asap/setup/test-polaris', document.getElementById('setup-polaris-test-result'), formDataObject(setupForm), {
      button: setupTestPolarisBtn,
      pendingText: 'Testing Polaris...',
      successText: 'Success! Polaris API is working.'
    });
  });
}

logoutBtn.addEventListener('click', (e) => {
  e.preventDefault();
  pb.authStore.clear();
  document.getElementById('login-form').reset();
  document.getElementById('login-password').value = '';
  checkAuth();
});

function openProfileDialog() {
  const dialog = document.getElementById('profile-dialog');
  if (!dialog) return;
  const msg = document.getElementById('profile-msg');
  if (msg) {
    msg.textContent = '';
    msg.className = 'mb-3 font-weight-bold';
  }
  setFieldChecked('profile-weekly-action-summary', !!(pb.authStore.model && pb.authStore.model.weekly_action_summary_enabled));
  setFieldValue('profile-weekly-action-summary-email', (pb.authStore.model && pb.authStore.model.weekly_action_summary_email) || '');
  dialog.showModal();
}

if (profileBtn) {
  profileBtn.addEventListener('click', (e) => {
    e.preventDefault();
    openProfileDialog();
  });
}

const profileCancelBtn = document.getElementById('profile-cancel');
if (profileCancelBtn) {
  profileCancelBtn.addEventListener('click', () => {
    const dialog = document.getElementById('profile-dialog');
    if (dialog && dialog.open) dialog.close();
  });
}

const profileForm = document.getElementById('profile-form');
if (profileForm) {
  profileForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const msg = document.getElementById('profile-msg');
    const saveBtn = document.getElementById('profile-save');
    if (saveBtn) saveBtn.disabled = true;
    if (msg) {
      msg.textContent = 'Saving...';
      msg.className = 'mb-3 font-weight-bold text-info';
    }
    try {
      const enabled = getFieldChecked('profile-weekly-action-summary');
      const email = getFieldValue('profile-weekly-action-summary-email').trim();
      if (enabled && !email) {
        throw new Error('Enter a report email address before enabling the weekly summary.');
      }
      const updated = await authorizedJson('/api/asap/staff/profile', {
        method: 'POST',
        body: JSON.stringify({
          weekly_action_summary_enabled: enabled,
          weekly_action_summary_email: email
        })
      });
      pb.authStore.save(pb.authStore.token, Object.assign({}, pb.authStore.model || {}, updated));
      if (msg) {
        msg.textContent = 'Weekly report preference saved.';
        msg.className = 'mb-3 font-weight-bold text-success';
      }
      showToast('Weekly report preference saved.', 'success');
      setTimeout(() => {
        const dialog = document.getElementById('profile-dialog');
        if (dialog && dialog.open) dialog.close();
      }, 700);
    } catch (err) {
      if (msg) {
        msg.textContent = err.message || 'Could not save your weekly report preference. Please try again.';
        msg.className = 'mb-3 font-weight-bold text-danger';
      }
    } finally {
      if (saveBtn) saveBtn.disabled = false;
    }
  });
}

document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
  link.addEventListener('click', () => {
    const nextStatus = link.getAttribute('data-status');
    activateStatusTab(nextStatus);
    if (nextStatus !== 'settings') {
      updateStageQuery(nextStatus);
    }
    loadTab(currentStatus);
  });
});

async function loadTab(status) {
  const tabDesc = document.getElementById('tab-desc');
  let desc = descriptions[status] || '';

  // Add auto-rejection info for Suggestions
  if (status === 'suggestion') {
    desc += workflowSettings.outstandingTimeoutEnabled
      ? ` If auto-reject stalled suggestions is enabled, stalled suggestions will be rejected after ${workflowSettings.outstandingTimeoutDays} days.`
      : ' Auto-reject stalled suggestions is currently disabled in Settings.';
  }

  // Add auto-close info for Hold placed
  if (status === 'hold_placed') {
    if (workflowSettings.holdPickupTimeoutEnabled) {
      desc += ` Auto-close unpicked-up holds is enabled, so holds will close after checkout or after ${workflowSettings.holdPickupTimeoutDays} days if the item is never picked up.`;
    } else {
      desc += ' Holds will only move to Closed when the patron checks out the item. Enable auto-close unpicked-up holds in Settings to also close holds that are never picked up.';
    }
  }

  // Add auto-close info for Pending hold
  if (status === 'pending_hold') {
    workflowSettings.pendingHoldTimeoutDays = parseInt(workflowSettings.pendingHoldTimeoutDays || '14', 10) || 14;
    if (workflowSettings.pendingHoldTimeoutEnabled) {
      desc += ` Auto-close pending holds is enabled, so items will close after ${workflowSettings.pendingHoldTimeoutDays} days if they are not processed.`;
    }
  }
  tabDesc.textContent = desc;

  // Update job message
  const jobMsg = document.getElementById('job-msg');
  if (jobMsg) jobMsg.textContent = '';

  // Admin actions visibility
  const adminBar = document.getElementById('admin-actions-bar');
  const promoterBtn = document.getElementById('btn-run-promoter-check');
  const holdBtn = document.getElementById('btn-run-hold-check');

  adminBar.classList.add('hidden');
  promoterBtn.classList.add('hidden');
  holdBtn.classList.add('hidden');

  const isCurrentlyAdmin = isSuperAdminStaff();

  if (isCurrentlyAdmin) {
    if (status === 'outstanding_purchase' && workflowSettings.autoPromote) {
      adminBar.classList.remove('hidden');
      promoterBtn.classList.remove('hidden');
    } else if (status === 'pending_hold') {
      adminBar.classList.remove('hidden');
      holdBtn.classList.remove('hidden');
    }
  }

  if (status === 'settings') {
    gridContainer.classList.add('hidden');
    settingsContainer.classList.remove('hidden');
    activateSettingsSection(getSettingsSectionFromHash() || currentSettingsSection, { updateHash: false });
    if (!isAdminStaff()) {
      showSettingsAccessDenied();
      return;
    }
    loadSettings({ showErrors: true });
    return;
  }

  gridContainer.classList.remove('hidden');
  settingsContainer.classList.add('hidden');
  hideSettingsAccessDenied();
  resetGrid();

  try {
    const scopedResult = await authorizedJson('/api/asap/staff/title-requests');
    allSuggestions = Array.isArray(scopedResult.items) ? scopedResult.items : [];
    updateTabCounts(allSuggestions);

    currentSuggestions = allSuggestions.filter(row => normalizeStatus(row.status) === status);

    if (!currentSuggestions.length) {
      gridContainer.innerHTML = `<div class="alert alert-light border">${escapeAttr(emptyStateMessages[status] || 'No suggestions found.')}</div>`;
      return;
    }

    grid = new gridjs.Grid({
      columns: getGridColumns(status),
      data: currentSuggestions.map(row => getGridRow(row, status)),
      search: true,
      pagination: { limit: 25 },
      sort: true,
      width: '100%'
    }).render(gridContainer);
  } catch (err) {
    console.error('Failed to load data', err);
  }

  const announcer = document.getElementById('status-announcer');
  announcer.textContent = "Loaded " + status + " tab.";

  // Manage focus for screen readers when tab changes
  const firstHeader = document.getElementById('tab-desc');
  if (firstHeader) firstHeader.focus();
}

function resetGrid() {
  if (grid && typeof grid.destroy === 'function') {
    grid.destroy();
  }
  grid = null;
  gridContainer.innerHTML = '';
}

function updateTabCounts(records) {
  const counts = Object.fromEntries(statusStages.map(status => [status, 0]));
  records.forEach(row => {
    const status = normalizeStatus(row.status);
    if (Object.prototype.hasOwnProperty.call(counts, status)) {
      counts[status] += 1;
    }
  });

  document.querySelectorAll('#status-tabs .nav-link[data-status]').forEach(link => {
    const status = link.getAttribute('data-status');
    const count = counts[status];
    const badge = link.querySelector('.tab-count');
    if (badge && count !== undefined) {
      badge.textContent = count;
      badge.setAttribute('aria-label', count + ' records');
    }
  });
}

function formatStandardDate(d) {
  if (!d) return '';
  const date = (d instanceof Date) ? d : new Date(d);
  return date.toLocaleDateString('en-US');
}

function formatDateTime(value) {
  if (!value) return '';
  const date = new Date(value);
  return formatStandardDate(date) + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function normalizeStatus(value) {
  return String(value || '').trim();
}

function formatPublication(value) {
  return String(value || '').trim();
}

function formatNote(note) {
  const text = String(note || '').trim();
  if (!text) return '';
  if (text.length <= 50) return text;
  const visibleText = text.substring(0, 50) + '...';
  return gridjs.html(`<button type="button" class="truncate-note" data-full-note="${escapeAttr(text)}" data-notes-action="true" data-no-row-edit="true" title="Click to view full note" aria-label="Truncated note, click to view full text">${escapeAttr(visibleText)}</button>`);
}

function rowMarker(row) {
  return `<span class="asap-row-marker" data-suggestion-id="${escapeAttr(row.id)}" hidden></span>`;
}

function getActionsColumnWidth(status) {
  if (status === 'pending_hold' || status === 'hold_placed') {
    return '120px';
  }
  if (status === 'closed') {
    return '130px';
  }
  return '160px';
}

function getGridColumns(status) {
  const actionsColumn = { name: 'Actions', width: getActionsColumnWidth(status), sort: false };

  if (status === 'suggestion') {
    return [
      'Barcode',
      'Title (original)',
      'Author (original)',
      'Format',
      'Timing',
      'Submitted',
      { name: 'Notes', width: '200px' },
      'Edited by',
      actionsColumn,
    ];
  }

  if (status === 'closed') {
    return [
      'Barcode',
      'Title (original)',
      'Author (original)',
      'Format',
      'Submitted',
      'Closed reason',
      { name: 'Notes', width: '200px' },
      'Edited by',
      actionsColumn,
    ];
  }

  return [
    'Barcode',
    'Title (original)',
    'Author (original)',
    'ISBN',
    'BIBID',
    'Age group',
    'Format',
    'Timing',
    'Submitted',
    'Last checked',
    { name: 'Notes', width: '200px' },
    'Edited by',
    actionsColumn,
  ];
}


function getDuplicateBadgesHtml(row) {
  if (!allSuggestions || !allSuggestions.length) return '';

  const duplicates = allSuggestions.filter(r => {
    if (r.id === row.id) return false;

    // Check identifier match if both have one
    if (r.identifier && row.identifier && r.identifier.trim().toLowerCase() === row.identifier.trim().toLowerCase()) {
      return true;
    }

    // Check BibID match
    if (r.bibid && row.bibid && r.bibid.trim().toLowerCase() === row.bibid.trim().toLowerCase()) {
      return true;
    }

    // Check title match
    if (r.title && row.title && r.title.trim().toLowerCase() === row.title.trim().toLowerCase()) {
      return true;
    }

    return false;
  });

  if (!duplicates.length) return '';

  // Group by status
  const statusCounts = {};
  duplicates.forEach(d => {
    const s = normalizeStatus(d.status);
    statusCounts[s] = (statusCounts[s] || 0) + 1;
  });

  const statusNames = {
    'suggestion': 'Suggestion',
    'outstanding_purchase': 'Pending purchase',
    'pending_hold': 'Pending hold',
    'hold_placed': 'Hold placed',
    'closed': 'Closed'
  };

  const statusColors = {
    'suggestion': 'badge-secondary',
    'outstanding_purchase': 'badge-info',
    'pending_hold': 'badge-warning',
    'hold_placed': 'badge-success',
    'closed': 'badge-dark'
  };

  let badges = '';
  for (const [status, count] of Object.entries(statusCounts)) {
    const displayName = statusNames[status] || status;
    const colorClass = statusColors[status] || 'badge-secondary';
    const text = count > 1 ? `Dup (${displayName} x${count})` : `Dup (${displayName})`;
    badges += ` <span class="badge ${colorClass} asap-duplicate-badge" title="${count} duplicate(s) in ${displayName} stage">${text}</span>`;
  }

  return badges;
}

function getWorkflowTagBadgesHtml(row) {
  const tags = Array.isArray(row.workflowTags) ? row.workflowTags : [];
  const visibleTags = tags.filter(tag => {
    const cleanTag = String(tag || '').trim();
    return cleanTag && !/^\d+$/.test(cleanTag);
  });
  if (!visibleTags.length) return '';
  return visibleTags.map(tag => {
    const label = escapeAttr(tag);
    return ` <span class="badge badge-primary asap-polaris-tag" title="Polaris check tag">${label}</span>`;
  }).join('');
}

function getIsbnCheckBadgesHtml(row) {
  const status = typeof row?.isbnCheckStatus === 'string' ? row.isbnCheckStatus : '';
  const isbnStatusLabels = {
    pending: 'New / ISBN check in progress',
    found: 'ISBN found',
    not_found: 'ISBN not found',
    error_max_retries: 'ISBN check retry limit reached'
  };
  const label = isbnStatusLabels[status];
  if (!label) return '';
  const tooltip = status === 'pending'
    ? 'Background ISBN processing is still running. This suggestion is already submitted.'
    : 'ISBN background processing result.';
  return ` <span class="badge badge-info asap-isbn-check-badge" title="${escapeAttr(tooltip)}">${escapeAttr(label)}</span>`;
}

function getTitleBadgesHtml(row) {
  return getDuplicateBadgesHtml(row) + getWorkflowTagBadgesHtml(row) + getIsbnCheckBadgesHtml(row);
}

function getGridRow(row, status) {
  if (status === 'suggestion') {
    return [
      row.barcode,
      gridjs.html(rowMarker(row) + escapeAttr(row.title) + getTitleBadgesHtml(row)),
      row.author,
      formatMap[row.format] || row.format,
      formatPublication(row.publication),
      formatStandardDate(row.created),
      formatNote(row.notes),
      row.editedBy,
      gridjs.html(renderRowActions(row)),
    ];
  }

  if (status === 'closed') {
    return [
      row.barcode,
      gridjs.html(rowMarker(row) + escapeAttr(row.title) + getTitleBadgesHtml(row)),
      row.author,
      formatMap[row.format] || row.format,
      formatStandardDate(row.created),
      formatCloseReason(row),
      formatNote(row.notes),
      row.editedBy,
      gridjs.html(renderRowActions(row)),
    ];
  }

  return [
    row.barcode,
    gridjs.html(rowMarker(row) + escapeAttr(row.title) + getTitleBadgesHtml(row)),
    row.author,
    row.identifier,
    row.bibid,
    ageMap[row.agegroup] || row.agegroup,
    formatMap[row.format] || row.format,
    formatPublication(row.publication),
    formatStandardDate(row.created),
    formatDateTime(row.lastPromoterCheck),
    formatNote(row.notes),
    row.editedBy,
    gridjs.html(renderRowActions(row)),
  ];
}

function formatCloseReason(row) {
  if (normalizeStatus(row.status) !== 'closed') {
    return '';
  }
  return closeReasonMap[row.closeReason] || 'Closed';
}

function getRowActions(row) {
  const status = normalizeStatus(row.status);

  if (status === 'suggestion') {
    return {
      primary: { label: 'Purchase', className: 'btn-primary', onClick: () => openEdit(row.id, 'outstanding_purchase', 'Approve for purchase', 'purchase') },
      secondary: [
        { label: 'Already own', onClick: () => openEdit(row.id, 'pending_hold', 'Already own', 'alreadyOwn') },
        { label: 'Reject', className: 'danger', onClick: () => openEdit(row.id, 'closed', 'Reject', 'reject') },
        { label: 'Silent close', className: 'danger', onClick: () => openEdit(row.id, 'closed', 'Silent close', 'silentClose') },
        { label: 'Edit', onClick: () => openEdit(row.id, 'suggestion', 'Edit suggestion', '') },
      ]
    };
  }

  if (status === 'outstanding_purchase') {
    return {
      primary: { label: 'Ready for hold', className: 'btn-success', onClick: () => openEdit(row.id, 'pending_hold', 'Move to Pending hold', '') },
      secondary: [
        { label: 'Silent close', className: 'danger', onClick: () => openEdit(row.id, 'closed', 'Silent close', 'silentClose') },
        { label: 'Undo', onClick: () => undoRow(row.id) },
        { label: 'Edit', onClick: () => openEdit(row.id, 'outstanding_purchase', 'Edit', '') },
      ]
    };
  }

  if (status === 'pending_hold' || status === 'hold_placed' || status === 'closed') {
    const secondary = [];
    if (status !== 'closed') secondary.push({ label: 'Silent close', className: 'danger', onClick: () => openEdit(row.id, 'closed', 'Silent close', 'silentClose') });
    secondary.push({ label: 'Edit', onClick: () => openEdit(row.id, row.status, 'Edit', '') });
    return {
      primary: { label: 'Undo', className: 'btn-outline-secondary', onClick: () => undoRow(row.id) },
      secondary
    };
  }

  return {
    primary: { label: 'Edit', className: 'btn-secondary', onClick: () => openEdit(row.id, row.status, 'Edit', '') },
    secondary: []
  };
}

async function runRowAction(action) {
  closeActionMenu();
  try {
    await action.onClick();
  } catch (error) {
    await showAlert(error.message || String(error) || 'Action failed');
  }
}

function registerRowAction(action) {
  rowActionIdCounter += 1;
  const actionId = `row-action-${rowActionIdCounter}`;
  rowActionRegistry.set(actionId, action);
  return actionId;
}

function getRegisteredRowAction(actionId) {
  return rowActionRegistry.get(actionId);
}

function renderRowActions(row) {
  const actions = getRowActions(row);
  const primaryActionId = registerRowAction(actions.primary);
  let markup = `<div class="row-action-group" data-no-row-edit="true">`;
  markup += `<button type="button" class="btn btn-sm row-action-primary ${escapeAttr(actions.primary.className || 'btn-primary')}" data-row-action-id="${primaryActionId}" data-no-row-edit="true">${escapeAttr(actions.primary.label)}</button>`;
  if (actions.secondary?.length) {
    const menuActionIds = actions.secondary.map(action => registerRowAction(action)).join(',');
    markup += `<button type="button" class="btn btn-sm btn-outline-secondary row-action-menu-trigger" aria-haspopup="menu" aria-expanded="false" data-row-menu-action-ids="${menuActionIds}" data-no-row-edit="true">⋯</button>`;
  }
  markup += `</div>`;
  return markup;
}

function openActionMenu(triggerButton, actionIds) {
  closeActionMenu();
  const layer = document.getElementById('action-menu-layer');
  if (!layer) return;
  triggerButton.setAttribute('aria-expanded', 'true');
  const menu = document.createElement('div');
  menu.className = 'row-action-menu';
  menu.setAttribute('role', 'menu');
  actionIds.forEach((actionId) => {
    const action = getRegisteredRowAction(actionId);
    if (!action) return;
    const item = document.createElement('button');
    item.type = 'button';
    item.className = `row-action-menu-item ${action.className || ''}`.trim();
    item.setAttribute('role', 'menuitem');
    item.setAttribute('data-row-action-id', actionId);
    item.setAttribute('data-no-row-edit', 'true');
    item.textContent = action.label;
    menu.appendChild(item);
  });
  layer.appendChild(menu);
  positionActionMenu(triggerButton, menu);
  activeActionMenu = { triggerButton, menu };
}

function positionActionMenu(triggerButton, menu) {
  const triggerRect = triggerButton.getBoundingClientRect();
  const menuRect = menu.getBoundingClientRect();
  const spacing = 6;
  const viewportPadding = 8;
  let top = triggerRect.bottom + spacing;
  let left = triggerRect.right - menuRect.width;
  if (top + menuRect.height > window.innerHeight - viewportPadding) {
    top = triggerRect.top - menuRect.height - spacing;
  }
  left = Math.max(viewportPadding, Math.min(left, window.innerWidth - menuRect.width - viewportPadding));
  menu.style.top = `${top}px`;
  menu.style.left = `${left}px`;
}

function closeActionMenu() {
  if (!activeActionMenu) return;
  activeActionMenu.triggerButton?.setAttribute('aria-expanded', 'false');
  activeActionMenu.menu?.remove();
  activeActionMenu = null;
}

function escapeAttr(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function shouldIgnoreRowEditClick(target, event) {
  if (event.defaultPrevented) return true;
  if (event.button !== 0) return true;
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return true;

  return !!target.closest([
    'button',
    'a',
    'input',
    'select',
    'textarea',
    'label',
    'summary',
    '[role="button"]',
    '[role="menu"]',
    '[role="menuitem"]',
    '[data-row-action-id]',
    '[data-row-menu-action-ids]',
    '[data-no-row-edit]',
    '[data-notes-action]',
    '.row-action-group',
    '.row-action-menu',
    '.gridjs-search',
    '.gridjs-pagination'
  ].join(','));
}

function openSuggestionEditFromRow(recordId) {
  const row = currentSuggestions.find(item => item.id === recordId) || allSuggestions.find(item => item.id === recordId);
  if (!row) {
    showToast('Could not find that suggestion. Refresh and try again.', 'error');
    return;
  }

  const status = normalizeStatus(row.status);
  openEdit(row.id, status || currentStatus, status === 'suggestion' ? 'Edit suggestion' : 'Edit', '');
}

gridContainer.addEventListener('click', (e) => {
  const target = e.target;
  if (!(target instanceof Element)) return;

  const actionButton = e.target.closest('[data-row-action-id]');
  if (actionButton) {
    e.preventDefault();
    e.stopPropagation();
    const action = getRegisteredRowAction(actionButton.getAttribute('data-row-action-id'));
    if (action) runRowAction(action);
    return;
  }

  const menuTrigger = e.target.closest('[data-row-menu-action-ids]');
  if (menuTrigger) {
    e.preventDefault();
    e.stopPropagation();
    const actionIds = (menuTrigger.getAttribute('data-row-menu-action-ids') || '').split(',').filter(Boolean);
    openActionMenu(menuTrigger, actionIds);
    return;
  }

  const truncateBtn = e.target.closest('.truncate-note');
  if (truncateBtn && gridContainer.contains(truncateBtn)) {
    e.preventDefault();
    e.stopPropagation();
    const fullNote = truncateBtn.getAttribute('data-full-note');
    document.getElementById('noteDialogContent').textContent = fullNote;
    document.getElementById('noteDialog').showModal();
    document.getElementById('noteDialogCloseBtn').focus();
    return;
  }

  if (shouldIgnoreRowEditClick(target, e)) return;

  const tableRow = target.closest('tr');
  if (!tableRow || !gridContainer.contains(tableRow)) return;

  const marker = tableRow.querySelector('[data-suggestion-id]');
  const recordId = marker ? marker.getAttribute('data-suggestion-id') : '';
  if (!recordId) return;

  openSuggestionEditFromRow(recordId);
});

document.addEventListener('click', (event) => {
  const menuActionButton = event.target.closest('#action-menu-layer [data-row-action-id]');
  if (menuActionButton) {
    event.preventDefault();
    event.stopPropagation();
    const action = getRegisteredRowAction(menuActionButton.getAttribute('data-row-action-id'));
    if (action) runRowAction(action);
    return;
  }
  if (!activeActionMenu) return;
  const clickedMenu = activeActionMenu.menu.contains(event.target);
  const clickedTrigger = activeActionMenu.triggerButton.contains(event.target);
  if (!clickedMenu && !clickedTrigger) closeActionMenu();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') closeActionMenu();
});
window.addEventListener('resize', closeActionMenu);
window.addEventListener('scroll', closeActionMenu, true);

function openEdit(id, nextStatus, dialogTitle, actionStr) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;

  document.getElementById('editModalLabel').textContent = dialogTitle;
  document.getElementById('edit-id').value = row.id;
  document.getElementById('edit-next-status').value = nextStatus;
  document.getElementById('edit-action').value = actionStr;
  setBibIdRequirement(nextStatus);

  document.getElementById('edit-title').value = row.title || '';
  document.getElementById('edit-author').value = row.author || '';
  document.getElementById('edit-identifier').value = row.identifier || '';
  document.getElementById('edit-bibid').value = row.bibid || '';

  const editFormat = document.getElementById('edit-format');
  const fmt = row.format || 'book';
  if (fmt && !availableFormats.includes(fmt)) {
    if (!Array.from(editFormat.options).some(o => o.value === fmt)) {
      const opt = document.createElement('option');
      opt.value = fmt;
      opt.textContent = formatMap[fmt] || fmt;
      editFormat.appendChild(opt);
    }
  }
  editFormat.value = fmt;
  document.getElementById('edit-age').value = row.agegroup || 'adult';
  setSelectValue(document.getElementById('edit-publication'), row.publication || publicationOptions[0]);
  document.getElementById('edit-exact-publication-date').value = dateOnly(row.exactPublicationDate);
  renderEditPatronContext(row);

  const username = (pb.authStore.model && pb.authStore.model.username) ? pb.authStore.model.username : 'staff';
  const today = formatStandardDate(new Date());
  let appendNotes = '';
  if (actionStr === 'alreadyOwn') appendNotes = `${today} ALREADY OWN by ${username}.`;
  if (actionStr === 'reject') appendNotes = `${today} REJECTED by ${username}.`;
  if (actionStr === 'silentClose') appendNotes = `${today} SILENTLY CLOSED by ${username}.`;

  const existingNotes = (row.notes || '').trim();
  document.getElementById('edit-notes').value = appendNotes + (appendNotes && existingNotes ? '\n' : '') + existingNotes;

  document.getElementById('bib-info-display').classList.add('hidden');
  document.getElementById('bib-info-text').textContent = '';
  verifiedBibId = row.bibid || '';

  const rejectionContainer = document.getElementById('edit-rejection-template-container');
  if (actionStr === 'reject' && currentRejectionTemplates.length > 0) {
    rejectionContainer.classList.remove('hidden');
    const select = document.getElementById('edit-rejection-template');
    select.innerHTML = '<option value="">Default rejection template</option>';
    const sortedTemplates = [...currentRejectionTemplates].sort((a, b) => {
      const nameA = (a.name || a.subject || '').toLowerCase();
      const nameB = (b.name || b.subject || '').toLowerCase();
      return nameA.localeCompare(nameB);
    });

    sortedTemplates.forEach(t => {
      const opt = document.createElement('option');
      opt.value = t.id;
      opt.textContent = t.name || t.subject;
      select.appendChild(opt);
    });
  } else {
    rejectionContainer.classList.add('hidden');
    document.getElementById('edit-rejection-template').value = '';
  }

  document.getElementById('editModal').showModal();
  document.getElementById('close-modal-btn').focus();
}

function renderEditPatronContext(row) {
  const editBody = document.querySelector('#editModal .asap-dialog-edit-body');
  if (!editBody) return;

  let block = document.getElementById('edit-patron-context');
  if (!block) {
    block = document.createElement('div');
    block.id = 'edit-patron-context';
    block.className = 'alert alert-light border py-2 px-3 mb-2 small';
    const anchor = document.getElementById('edit-rejection-template-container');
    if (anchor && anchor.parentNode === editBody) {
      editBody.insertBefore(block, anchor.nextSibling);
    } else {
      editBody.insertBefore(block, editBody.firstChild);
    }
  }

  const patronName = row.patronName || `${row.nameFirst || ''} ${row.nameLast || ''}`.trim() || '—';
  const patronEmail = row.patronEmail || row.email || '—';
  const libraryOrgName = row.libraryOrgName || row.libraryOrgId || '—';
  const preferredPickupBranchName = row.preferredPickupBranchName || '—';
  const barcode = row.barcode || '—';

  block.innerHTML = `
    <div><strong>Patron:</strong> ${escapeAttr(patronName)}</div>
    <div><strong>Email:</strong> ${escapeAttr(patronEmail)}</div>
    <div><strong>Barcode:</strong> ${escapeAttr(barcode)}</div>
    <div><strong>Library:</strong> ${escapeAttr(libraryOrgName)}</div>
    <div><strong>Preferred pickup branch:</strong> ${escapeAttr(preferredPickupBranchName)}</div>
  `;
}

document.getElementById('edit-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const id = document.getElementById('edit-id').value;
  const nextStatus = document.getElementById('edit-next-status').value;
  const bibid = document.getElementById('edit-bibid').value.trim();
  if (nextStatus === 'pending_hold') {
    if (!bibid) {
      await showAlert('BIB ID is required before moving this suggestion to Pending hold.');
      document.getElementById('edit-bibid').focus();
      return;
    }
    if (bibid !== verifiedBibId) {
      await showAlert('Please use the "Lookup BIB" button to verify this BIB ID before moving to Pending hold.');
      document.getElementById('btn-bib-lookup').focus();
      return;
    }
  }
  const actionValue = document.getElementById('edit-action').value || undefined;
  const payload = {
    action: actionValue,
    status: nextStatus,
    title: document.getElementById('edit-title').value,
    author: document.getElementById('edit-author').value,
    identifier: document.getElementById('edit-identifier').value,
    bibid: bibid,
    format: document.getElementById('edit-format').value,
    agegroup: document.getElementById('edit-age').value,
    publication: document.getElementById('edit-publication').value,
    exactPublicationDate: document.getElementById('edit-exact-publication-date').value,
    notes: document.getElementById('edit-notes').value,
    editedBy: pb.authStore.model.username
  };

  if (actionValue === 'reject') {
    payload.rejectionTemplateId = document.getElementById('edit-rejection-template').value;
  }

  try {
    const res = await fetch(`/api/asap/staff/title-requests/${id}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.message || 'Error updating suggestion');
    }
    const updatedRecord = await res.json().catch(() => ({}));
    document.getElementById('editModal').close();

    if (updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
      const statusNames = {
        'outstanding_purchase': 'Pending purchase',
        'pending_hold': 'Pending hold',
        'hold_placed': 'Hold placed',
        'closed': 'Closed'
      };
      await showAlert(`Note: This suggestion moved directly to "${statusNames[updatedRecord.status] || updatedRecord.status}" because it was detected as already being on hold or having a BIB ID.`);
    }

    loadTab(currentStatus);
  } catch (err) {
    await showAlert(err.message || 'Error updating suggestion');
  }
});

function setBibIdRequirement(nextStatus) {
  const bibInput = document.getElementById('edit-bibid');
  const bibRequiredMarker = document.getElementById('edit-bibid-required');
  const bibHint = document.getElementById('edit-bibid-hint');

  const isRequired = nextStatus === 'pending_hold';
  bibInput.required = isRequired;
  bibInput.setAttribute('aria-required', String(isRequired));
  if (bibRequiredMarker) {
    bibRequiredMarker.classList.toggle('hidden', !isRequired);
  }
  if (bibHint) {
    bibHint.classList.toggle('text-danger', isRequired);
    bibHint.classList.toggle('font-weight-bold', isRequired);
    bibHint.textContent = isRequired
      ? 'Required for Pending hold so the system can place the patron hold.'
      : 'Needed before a hold can be placed for the patron.';
  }
}

async function undoRow(id) {
  if (!await showConfirm('Undo action', 'Undo action and return this suggestion to Suggestions?')) return;
  const row = currentSuggestions.find(r => r.id === id);
  if (!row) return;
  try {
    const res = await fetch(`/api/asap/staff/title-requests/${id}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify({
        ...row,
        status: 'suggestion',
        editedBy: pb.authStore.model.username
      })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.message || 'Error undoing suggestion');
    }
    loadTab(currentStatus);
  } catch (err) {
    await showAlert(err.message || 'Error undoing suggestion');
  }
}

document.getElementById('close-modal-x').addEventListener('click', () => {
  document.getElementById('editModal').close();
});
document.getElementById('close-modal-btn').addEventListener('click', () => {
  document.getElementById('editModal').close();
});

document.getElementById('btn-new-suggestion').addEventListener('click', () => {
  document.getElementById('new-suggestion-form').reset();
  document.getElementById('new-error').classList.add('hidden');
  resetStaffPatronLookup();
  document.getElementById('newSuggestionModal').showModal();
  document.getElementById('close-new-modal-btn').focus();
  document.getElementById('new-barcode').focus();
});

document.getElementById('close-new-modal-x').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});
document.getElementById('close-new-modal-btn').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});

document.getElementById('new-barcode').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    document.getElementById('btn-lookup-patron').click();
  }
});

document.getElementById('new-barcode').addEventListener('input', () => {
  const barcode = document.getElementById('new-barcode').value.trim();
  if (verifiedNewSuggestionBarcode && barcode !== verifiedNewSuggestionBarcode) {
    resetStaffPatronLookup();
    showLookupResult('Barcode changed. Look up the patron again before entering suggestion details.', 'warning');
  }
});

document.getElementById('btn-lookup-patron').addEventListener('click', async () => {
  const barcode = document.getElementById('new-barcode').value.trim();
  const btn = document.getElementById('btn-lookup-patron');
  document.getElementById('new-error').classList.add('hidden');
  resetStaffPatronLookup();

  if (!barcode) {
    showLookupResult('Enter a patron barcode before lookup.', 'danger');
    document.getElementById('new-barcode').focus();
    return;
  }

  btn.disabled = true;
  btn.textContent = 'Looking up...';
  try {
    const res = await fetch('/api/asap/staff/patron-lookup', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify({ barcode })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      throw new Error(data.message || 'Invalid patron barcode');
    }

    verifiedNewSuggestionBarcode = String(data.barcode || barcode).trim();
    setNewSuggestionDetailsEnabled(true);
    const emailStr = data.email ? ` | Email: ${data.email}` : ' | No email on file';
    const libraryStr = data.libraryOrgName ? ` | Library: ${data.libraryOrgName}` : '';
    showLookupResult('Patron verified: ' + patronLookupName(data) + ' (' + verifiedNewSuggestionBarcode + ')' + emailStr + libraryStr, 'success');
    document.getElementById('new-title').focus();
  } catch (err) {
    showLookupResult(err.message || 'Invalid patron barcode', 'danger');
    document.getElementById('new-barcode').focus();
  } finally {
    btn.disabled = false;
    btn.textContent = 'Lookup Patron';
  }
});

document.getElementById('new-suggestion-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const barcode = document.getElementById('new-barcode').value.trim();
  if (!verifiedNewSuggestionBarcode || barcode !== verifiedNewSuggestionBarcode) {
    const errDiv = document.getElementById('new-error');
    errDiv.textContent = 'Look up and verify the patron barcode before submitting a suggestion.';
    errDiv.classList.remove('hidden');
    document.getElementById('new-barcode').focus();
    return;
  }

  const payload = {
    barcode: barcode,
    title: document.getElementById('new-title').value,
    author: document.getElementById('new-author').value,
    identifier: document.getElementById('new-identifier').value,
    format: document.getElementById('new-format').value,
    agegroup: document.getElementById('new-age').value,
    publication: document.getElementById('new-publication').value,
    notes: document.getElementById('new-notes').value
  };

  const errDiv = document.getElementById('new-error');
  errDiv.classList.add('hidden');
  const btn = document.getElementById('btn-submit-new');
  btn.disabled = true;
  btn.textContent = 'Submitting...';

  try {
    const res = await fetch('/api/asap/staff/suggestions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });

    if (!res.ok) {
      const data = await res.json();
      throw new Error(data.message || 'Failed to create suggestion');
    }

    document.getElementById('newSuggestionModal').close();
    loadTab('suggestion');
  } catch (err) {
    errDiv.textContent = err.message;
    errDiv.classList.remove('hidden');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Submit';
  }
});

function resetStaffPatronLookup() {
  verifiedNewSuggestionBarcode = '';
  clearNewSuggestionDetails();
  setNewSuggestionDetailsEnabled(false);
  document.getElementById('new-lookup-result').className = 'mt-2 hidden';
  document.getElementById('new-lookup-result').textContent = '';
}

function clearNewSuggestionDetails() {
  document.getElementById('new-title').value = '';
  document.getElementById('new-author').value = '';
  document.getElementById('new-identifier').value = '';
  document.getElementById('new-format').selectedIndex = 0;
  document.getElementById('new-age').selectedIndex = 0;
  document.getElementById('new-publication').selectedIndex = 0;
  document.getElementById('new-notes').value = '';
}

function setNewSuggestionDetailsEnabled(enabled) {
  document.getElementById('new-suggestion-details').classList.toggle('hidden', !enabled);
  document.querySelectorAll('.new-detail-field').forEach(field => {
    field.disabled = !enabled;
  });
  document.getElementById('btn-submit-new').disabled = !enabled;
}

function showLookupResult(message, type) {
  const result = document.getElementById('new-lookup-result');
  result.className = 'mt-2 alert alert-' + type + ' py-2';
  result.textContent = message;
}

function patronLookupName(data) {
  const name = [data.nameFirst, data.nameLast].filter(Boolean).join(' ').trim();
  return name || 'barcode found';
}

function normalizePublicationOptions(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw.map(option => String(option || '').trim()).filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultPublicationOptions.slice();
}

function normalizeAgeGroups(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw.map(option => String(option || '').trim()).filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultAgeGroups.slice();
}

function setPublicationOptions(options) {
  const normalized = normalizePublicationOptions(options);
  document.querySelectorAll('.publication-options-select').forEach(select => {
    const val = select.value || normalized[0];
    select.innerHTML = '';
    normalized.forEach(opt => {
      const el = document.createElement('option');
      el.value = opt;
      el.textContent = opt;
      select.appendChild(el);
    });
    select.value = normalized.includes(val) ? val : normalized[0];
  });
}

function setAgeGroups(options) {
  const normalized = normalizeAgeGroups(options);
  ['edit-age', 'new-age'].forEach(id => {
    const select = document.getElementById(id);
    if (!select) return;
    const val = select.value || normalized[0];
    select.innerHTML = '';
    normalized.forEach(opt => {
      const el = document.createElement('option');
      el.value = opt;
      el.textContent = opt;
      select.appendChild(el);
    });
    // Try to preserve value, fallback to the first option
    select.value = normalized.includes(val) ? val : normalized[0];
  });
}

function normalizePatronFormatRules(rules) {
  const normalized = structuredClone(defaultPatronFormatRules);
  const incoming = rules && typeof rules === 'object' ? rules : {};

  patronFormatKeys.forEach(format => {
    const incomingFormat = incoming[format] || {};
    const behavior = String(incomingFormat.messageBehavior || '').trim();
    if (['none', 'ebookMessage', 'eaudiobookMessage'].includes(behavior)) {
      normalized[format].messageBehavior = behavior;
    }

    const incomingFields = incomingFormat.fields || {};
    patronFormatFields.forEach(fieldInfo => {
      const field = fieldInfo.key;
      const incomingField = incomingFields[field] || {};
      const defaultField = normalized[format].fields[field];
      let mode = String(incomingField.mode || defaultField.mode || 'optional').trim();
      if (!['required', 'optional', 'hidden'].includes(mode)) mode = defaultField.mode || 'optional';
      if (field === 'title') mode = 'required';
      normalized[format].fields[field] = {
        mode,
        label: String(incomingField.label || defaultField.label || fieldInfo.label).trim() || defaultField.label || fieldInfo.label
      };
    });
  });

  return normalized;
}

function renderPatronFormatRulesEditor(rules) {
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return;

  const normalized = normalizePatronFormatRules(rules);
  editor.innerHTML = patronFormatKeys.map(format => {
    const rule = normalized[format];
    const rows = patronFormatFields.map(fieldInfo => {
      const field = rule.fields[fieldInfo.key];
      const titleLocked = fieldInfo.key === 'title';
      return `
        <tr>
          <td>
            <strong>${escapeAttr(fieldInfo.label)}</strong>
            <div class="small text-muted">Saves to <code>${escapeAttr(fieldInfo.storage)}</code></div>
          </td>
          <td class="format-rule-mode-cell">
            <select class="form-control form-control-sm format-rule-mode" data-format="${escapeAttr(format)}" data-field="${escapeAttr(fieldInfo.key)}"${titleLocked ? ' disabled' : ''}>
              <option value="required"${field.mode === 'required' ? ' selected' : ''}>Required</option>
              <option value="optional"${field.mode === 'optional' ? ' selected' : ''}>Optional</option>
              <option value="hidden"${field.mode === 'hidden' ? ' selected' : ''}>Hidden</option>
            </select>
            ${titleLocked ? '<div class="small text-muted">Title is always required.</div>' : ''}
          </td>
          <td>
            <input type="text" class="form-control form-control-sm format-rule-label" data-format="${escapeAttr(format)}" data-field="${escapeAttr(fieldInfo.key)}" value="${escapeAttr(field.label)}">
          </td>
        </tr>
      `;
    }).join('');

    return `
      <div class="card mb-3">
        <div class="card-header d-flex flex-wrap justify-content-between align-items-center">
          <strong>${escapeAttr(formatMap[format] || format)}</strong>
          <div class="form-inline mt-2 mt-md-0">
            <label class="small text-muted mr-2" for="format-rule-message-${escapeAttr(format)}">Message behavior</label>
            <select id="format-rule-message-${escapeAttr(format)}" class="form-control form-control-sm format-rule-message" data-format="${escapeAttr(format)}">
              <option value="none"${rule.messageBehavior === 'none' ? ' selected' : ''}>Show fields and allow submission</option>
              <option value="ebookMessage"${rule.messageBehavior === 'ebookMessage' ? ' selected' : ''}>Show eBook message only</option>
              <option value="eaudiobookMessage"${rule.messageBehavior === 'eaudiobookMessage' ? ' selected' : ''}>Show eAudiobook message only</option>
            </select>
          </div>
        </div>
        <div class="table-responsive">
          <table class="table table-sm mb-0">
            <thead>
              <tr>
                <th>Canonical Field</th>
                <th>Mode</th>
                <th>Patron Label</th>
              </tr>
            </thead>
            <tbody>${rows}</tbody>
          </table>
        </div>
      </div>
    `;
  }).join('');
}

function collectPatronFormatRules() {
  const rules = normalizePatronFormatRules(defaultPatronFormatRules);
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return rules;

  editor.querySelectorAll('.format-rule-message').forEach(select => {
    const format = select.getAttribute('data-format');
    if (rules[format]) rules[format].messageBehavior = select.value;
  });

  editor.querySelectorAll('.format-rule-mode').forEach(select => {
    const format = select.getAttribute('data-format');
    const field = select.getAttribute('data-field');
    if (rules[format] && rules[format].fields[field]) {
      rules[format].fields[field].mode = field === 'title' ? 'required' : select.value;
    }
  });

  editor.querySelectorAll('.format-rule-label').forEach(input => {
    const format = input.getAttribute('data-format');
    const field = input.getAttribute('data-field');
    if (rules[format] && rules[format].fields[field]) {
      rules[format].fields[field].label = input.value.trim() || rules[format].fields[field].label;
    }
  });

  return normalizePatronFormatRules(rules);
}

function setPublicationOptions(options) {
  publicationOptions = normalizePublicationOptions(options);
  document.querySelectorAll('.publication-options-select').forEach(select => {
    populatePublicationSelect(select, select.value);
  });
}

function populatePublicationSelect(select, selectedValue) {
  if (!select) return;
  const selected = selectedValue || select.value || publicationOptions[0];
  select.innerHTML = '';
  publicationOptions.forEach(option => {
    const item = document.createElement('option');
    item.value = option;
    item.textContent = option;
    select.appendChild(item);
  });
  setSelectValue(select, selected);
}

function setSelectValue(select, value) {
  if (!select) return;
  value = String(value || '').trim();
  if (value && !publicationOptions.includes(value)) {
    const item = document.createElement('option');
    item.value = value;
    item.textContent = value;
    select.appendChild(item);
  }
  select.value = value || publicationOptions[0] || '';
}

document.getElementById('btn-bib-lookup').addEventListener('click', async () => {
  const bibId = document.getElementById('edit-bibid').value.trim();
  const btn = document.getElementById('btn-bib-lookup');
  const display = document.getElementById('bib-info-display');
  const text = document.getElementById('bib-info-text');

  if (!bibId) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Please enter a BIB ID first.';
    return;
  }

  btn.disabled = true;
  btn.textContent = '...';
  display.classList.add('hidden');

  try {
    const row = currentSuggestions.find(r => r.id === document.getElementById('edit-id').value);
    const barcode = row ? row.barcode : '';

    const res = await fetch('/api/asap/staff/bib-lookup', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify({ bibId, barcode })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.message || 'Lookup failed');

    display.classList.remove('hidden', 'alert-danger', 'alert-warning');
    display.classList.add('alert-info');

    let infoText = (data.title || 'No title') + (data.author ? ' by ' + data.author : '');

    // Update title and author fields if they don't match the bib data
    const titleInput = document.getElementById('edit-title');
    const authorInput = document.getElementById('edit-author');

    if (data.title) {
      const oldTitle = titleInput.value.trim();
      const pTitle = data.title.trim();
      if (pTitle && oldTitle !== pTitle && oldTitle.indexOf(pTitle + " (") !== 0) {
        titleInput.value = pTitle + " (" + oldTitle + ")";
      }
    }

    if (data.author) {
      const oldAuthor = authorInput.value.trim();
      const pAuthor = data.author.trim();
      if (pAuthor && oldAuthor !== pAuthor && oldAuthor.indexOf(pAuthor + " (") !== 0) {
        authorInput.value = pAuthor + " (" + oldAuthor + ")";
      }
    }

    // Check for duplicate hold in Polaris
    if (data.patronHoldCheck && data.patronHoldCheck.statusValue === 29) {
      display.classList.remove('alert-info');
      display.classList.add('alert-warning');
      infoText = "DUPLICATE: Patron already has a hold on this item in Polaris. " + infoText;
    }

    text.textContent = infoText;
    verifiedBibId = bibId;
  } catch (err) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Error: ' + err.message;
    verifiedBibId = '';
  } finally {
    btn.disabled = false;
    btn.textContent = 'Lookup BIB';
  }
});

function dateOnly(value) {
  value = String(value || '').trim();
  return value ? value.split(' ')[0].split('T')[0] : '';
}

function collectSettingsPolaris() {
  return {
    host: getFieldValue('polaris-host'),
    apiKey: getFieldValue('polaris-api-key'),
    accessId: getFieldValue('polaris-access-id'),
    staffDomain: getFieldValue('polaris-domain'),
    adminUser: getFieldValue('polaris-admin-user'),
    adminPassword: getFieldValue('polaris-admin-pass'),
    overridePassword: getFieldValue('polaris-override-pass'),
    autoPromote: getFieldChecked('polaris-auto-promote'),
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: "1",
    userId: "1"
  };
}

document.getElementById('btn-test-polaris').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('polaris-test-result');
  const btn = e.currentTarget;
  const polarisPayload = collectSettingsPolaris();

  if (!polarisPayload.host || !polarisPayload.accessId || !polarisPayload.apiKey) {
    setInlineResult(resSpan, 'Enter the Polaris host, PAPI access ID, and PAPI API key before testing.', 'ml-2 text-danger font-weight-bold');
    return;
  }

  const saved = await saveSettings({
    button: btn,
    pendingText: 'Saving before Polaris test...',
    successText: 'Settings saved. Testing Polaris...',
    clearDelay: 0
  });
  if (!saved) {
    setInlineResult(resSpan, 'Error: settings could not be saved.', 'ml-2 text-danger font-weight-bold');
    return;
  }

  await postPolarisTest('/api/asap/staff/test-polaris', resSpan, { polaris: polarisPayload }, {
    button: btn,
    token: pb.authStore.token,
    pendingText: 'Saving and testing...',
    pendingClass: 'ml-2 text-muted',
    successClass: 'ml-2 text-success font-weight-bold',
    errorClass: 'ml-2 text-danger font-weight-bold',
    successText: 'Success! Polaris API is working.'
  });
});

const syncOrganizationsBtn = document.getElementById('btn-sync-organizations');
async function syncPolarisOrganizations(options = {}) {
  const resultEl = document.getElementById('organizations-sync-result');
  const btn = options.button || syncOrganizationsBtn;
  if (btn) btn.disabled = true;
  setOrganizationsStatus('loading', 'Organizations loading from Polaris. Organization selection will be available after this sync completes.');
  setInlineResult(resultEl, 'Syncing organizations...', 'ml-2 text-muted');

  try {
    const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
    const count = result.synced || 0;
    setOrganizationsStatus('loaded', `Polaris organizations loaded successfully. ${count} organization record${count === 1 ? '' : 's'} synced. Leave all libraries unchecked to enable all organizations.`);
    setInlineResult(resultEl, `Synced ${count} organization records.`, 'ml-2 text-success font-weight-bold');
    const container = document.getElementById('enabled-libraries-checkbox-container');
    if (container) {
      container.removeAttribute('data-loaded');
    }
    if (isSuperAdminStaff()) {
      await populateLibrarySelector();
    }
    await renderLibraryParticipationCheckboxes();
    return result;
  } catch (err) {
    setOrganizationsStatus('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    setInlineResult(resultEl, 'Warning: ' + (err.message || 'Organization sync failed.'), 'ml-2 text-warning font-weight-bold');
    throw err;
  } finally {
    if (btn) btn.disabled = false;
  }
}

if (syncOrganizationsBtn) {
  syncOrganizationsBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    try {
      await syncPolarisOrganizations({ button: syncOrganizationsBtn });
    } catch (err) {
      // syncPolarisOrganizations already updates the visible warning state.
    }
  });
}

document.getElementById('btn-run-hold-check').addEventListener('click', async () => {
  const btn = document.getElementById('btn-run-hold-check');
  const msg = document.getElementById('job-msg');

  btn.disabled = true;
  msg.textContent = 'Running hold check...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const res = await fetch('/api/asap/jobs/hold-check', {
      method: 'POST',
      headers: { 'Authorization': pb.authStore.token }
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = `Hold check complete. Moved to Pending hold: ${data.promoted}, holds placed: ${data.holdsPlaced}, closed after checkout: ${data.checkoutClosures}, auto-closed: ${data.timedOut}`;
      msg.className = 'mb-3 font-weight-bold text-success';
      loadTab(currentStatus);
    } else {
      throw new Error(data.message || 'Failed to run hold check');
    }
  } catch (err) {
    msg.textContent = 'Error: ' + err.message;
    msg.className = 'mb-3 font-weight-bold text-danger';
  } finally {
    btn.disabled = false;
  }
});

document.getElementById('btn-run-promoter-check').addEventListener('click', async () => {
  const btn = document.getElementById('btn-run-promoter-check');
  const msg = document.getElementById('job-msg');

  btn.disabled = true;
  msg.textContent = 'Running auto-promoter...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const res = await fetch('/api/asap/jobs/promoter-check', {
      method: 'POST',
      headers: { 'Authorization': pb.authStore.token }
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = `Auto-promoter complete. Moved ${data.promoted} item${data.promoted === 1 ? '' : 's'} to Pending hold.`;
      msg.className = 'mb-3 font-weight-bold text-success';
      loadTab(currentStatus);
    } else {
      throw new Error(data.message || 'Failed to run promoter check');
    }
  } catch (err) {
    msg.textContent = 'Error: ' + err.message;
    msg.className = 'mb-3 font-weight-bold text-danger';
  } finally {
    btn.disabled = false;
  }
});

document.getElementById('btn-test-smtp').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('smtp-test-result');
  const testInput = document.getElementById('smtp-test-email');
  const btn = e.currentTarget;

  const testEmail = testInput ? testInput.value.trim() : '';
  const smtpHost = getFieldValue('smtp-host').trim();
  const sender = getFieldValue('smtp-from').trim() || getFieldValue('email-from-address').trim();

  if (!smtpHost || !sender || !testEmail) {
    resSpan.textContent = 'Enter SMTP host, sender address, and test recipient before testing SMTP.';
    resSpan.className = 'mt-2 text-danger font-weight-bold small';
    return;
  }
  if (!validateSmtpHostField(true)) {
    return;
  }

  resSpan.textContent = "Saving and testing...";
  resSpan.className = "mt-2 text-muted small";

  const saved = await saveSettings({
    button: btn,
    pendingText: 'Saving settings...',
    successText: 'Settings saved. Testing SMTP...',
    clearDelay: 0
  });
  if (!saved) {
    resSpan.textContent = "Error: settings could not be saved.";
    resSpan.className = "mt-2 text-danger font-weight-bold small";
    return;
  }

  try {
    // Give PocketBase hooks a brief moment to apply freshly saved SMTP settings
    // before issuing the test request.
    await new Promise(resolve => setTimeout(resolve, 300));

    const res = await fetch('/api/asap/staff/test-smtp', {
      method: 'POST',
      headers: {
        'Authorization': pb.authStore.token,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ email: testEmail })
    });
    const data = await res.json();
    if (res.ok && data.success) {
      resSpan.textContent = "Success! " + data.message;
      resSpan.className = "mt-2 text-success font-weight-bold small";
    } else {
      resSpan.textContent = "Error: " + (data.message || "Failed");
      resSpan.className = "mt-2 text-danger font-weight-bold small";
    }
  } catch (err) {
    resSpan.textContent = "Error testing SMTP.";
    resSpan.className = "mt-2 text-danger font-weight-bold small";
  }
});

function showSettingsAccessDenied() {
  settingsContainer.classList.remove('hidden');
  setVisible('settings-error', true);
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.add('hidden');
}

function hideSettingsAccessDenied() {
  setVisible('settings-error', false);
}

async function loadSettings(options = {}) {
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;
  settingsLoading = true;

  try {
    // Filter sidebar for non-super admins
    document.querySelectorAll('[data-settings-target]').forEach(el => {
      const section = el.getAttribute('data-settings-target');
      // Allow library admins to see settings they can override
      const allowedForAdmins = ['templates', 'workflow', 'patron'];
      if (!isSuper && !allowedForAdmins.includes(section)) {
        el.classList.add('hidden');
      } else {
        el.classList.remove('hidden');
      }
    });

    // If not super admin, force them to an allowed section if they are on a hidden one
    const allowedForAdmins = ['templates', 'workflow', 'patron'];
    if (!isSuper && !allowedForAdmins.includes(currentSettingsSection)) {
      activateSettingsSection('templates', { updateHash: true });
    }

    // Load Library Selector for Super Admins
    if (isSuper) {
      await populateLibrarySelector();
      document.getElementById('super-admin-library-selector').classList.remove('hidden');
    } else {
      document.getElementById('super-admin-library-selector').classList.add('hidden');
      currentLibraryContextOrgId = pb.authStore.model.libraryOrgId || 'system';
      const libraryName = pb.authStore.model.libraryOrgName || 'My Library';
      document.getElementById('library-context-display').textContent = currentLibraryContextOrgId === 'system'
        ? libraryName
        : `${libraryName} (ID ${currentLibraryContextOrgId})`;
    }

    const loadedLibrarySettings = await loadLibrarySettings(currentLibraryContextOrgId);

    if (!isSuper) {
      // Hide error, show form for library admins even if they can't load system-scoped settings.
      hideSettingsAccessDenied();
      const formEl = document.getElementById('settings-form');
      if (formEl) formEl.classList.remove('hidden');
      return;
    }

    const smtp = (loadedLibrarySettings && loadedLibrarySettings.smtp) || {};
    const polaris = (loadedLibrarySettings && loadedLibrarySettings.polaris) || {};
    const emails = (loadedLibrarySettings && loadedLibrarySettings.emails) || {};

    const hasPolarisCredentials = !!(polaris.host && polaris.apiKey && polaris.accessId && polaris.staffDomain && polaris.adminUser && polaris.adminPassword);
    if (hasPolarisCredentials && (organizationsStatus === 'not_loaded' || organizationsStatus === 'error')) {
      syncPolarisOrganizations().catch(() => {
        // syncPolarisOrganizations updates the visible warning state.
      });
    }

    workflowSettings.outstandingTimeoutEnabled = !!((loadedLibrarySettings && loadedLibrarySettings.workflow || {}).outstandingTimeoutEnabled);
    workflowSettings.outstandingTimeoutDays = parseInt(((loadedLibrarySettings && loadedLibrarySettings.workflow || {}).outstandingTimeoutDays) || '30', 10) || 30;
    workflowSettings.autoPromote = polaris.autoPromote !== false;

    // Workflow form population is handled by loadLibrarySettings

    // SMTP
    setFieldValue('smtp-host', smtp.host || '');
    setFieldValue('smtp-port', smtp.port || 587);
    setFieldValue('smtp-username', smtp.username || '');
    setFieldValue('smtp-password', smtp.password || '');
    setFieldChecked('smtp-tls', smtp.tls !== false);

    // Also populate the duplicate SMTP fields with the emails value
    setFieldValue('smtp-from', emails.fromAddress || '');
    setFieldValue('smtp-from-name', emails.fromName || '');

    // Polaris
    setFieldValue('polaris-host', polaris.host || '');
    setFieldValue('polaris-api-key', polaris.apiKey || '');
    setFieldValue('polaris-access-id', polaris.accessId || '');
    setFieldValue('polaris-domain', polaris.staffDomain || '');
    setFieldValue('polaris-admin-user', polaris.adminUser || '');
    setFieldValue('polaris-admin-pass', polaris.adminPassword || '');
    setFieldValue('polaris-override-pass', polaris.overridePassword || '');
    setFieldChecked('polaris-auto-promote', polaris.autoPromote !== false);

    // UI Text and Formats are handled by populatePatronUiForms called via loadLibrarySettings
    // but we can set them here if needed. Since loadLibrarySettings is called right before this, 
    // it will be populated. Wait, loadLibrarySettings is called AT THE TOP of loadSettings asynchronously!
    // So if it completes before loadSettings finishes, loadSettings might overwrite it?
    // Actually, loadLibrarySettings is awaited at the top. So we should just remove the uiText Population from loadSettings and let loadLibrarySettings handle it.
    await populateStaffLibraryOptions();
    await loadStaffUsers();



    // Success: hide error, show form
    hideSettingsAccessDenied();
    const formEl = document.getElementById('settings-form');
    if (formEl) formEl.classList.remove('hidden');

  } catch (err) {
    console.error('Failed to load settings', err);
    if (showErrors) {
      showSettingsAccessDenied();
    }
  } finally {
    settingsLoading = false;
    markSettingsClean('clean');
  }
}

const emailTemplateDefaults = {
  suggestion_submitted: {
    subject: 'Suggestion received: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.'
  },
  already_owned: {
    subject: '{{title}} is already available',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library\'s suggestion service.'
  },
  rejected: {
    subject: 'Update on your suggestion: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community.'
  },
  hold_placed: {
    subject: 'Hold placed for {{title}}',
    body: 'Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.'
  }
};

const templateFieldIds = [
  'email-submit-subject',
  'email-submit-body',
  'email-owned-subject',
  'email-owned-body',
  'email-rejected-subject',
  'email-rejected-body',
  'email-hold-subject',
  'email-hold-body'
];

function setTemplateInputsDisabled(disabled) {
  templateFieldIds.forEach(id => {
    const field = document.getElementById(id);
    if (field) field.disabled = disabled;
  });
}

function clearTemplateInputs() {
  templateFieldIds.forEach(id => {
    const field = document.getElementById(id);
    if (field) field.value = '';
  });
}

function setTemplateStatus(message, className, hidden = false) {
  const statusAlert = document.getElementById('library-override-status');
  if (!statusAlert) return;
  statusAlert.className = className || 'alert alert-warning mb-4';
  statusAlert.textContent = message || '';
  statusAlert.classList.toggle('hidden', hidden);
}

function populateEmailTemplateForms(emails) {
  emails = emails || {};

  if (document.getElementById('email-from-address')) document.getElementById('email-from-address').value = emails.fromAddress || '';
  if (document.getElementById('email-from-name')) document.getElementById('email-from-name').value = emails.fromName || '';

  // Sync to SMTP tab if we are modifying the system context
  if (currentLibraryContextOrgId === 'system') {
    if (document.getElementById('smtp-from')) document.getElementById('smtp-from').value = emails.fromAddress || '';
    if (document.getElementById('smtp-from-name')) document.getElementById('smtp-from-name').value = emails.fromName || '';
  }

  const emailSubmit = emails.suggestion_submitted || {};
  if (document.getElementById('email-submit-subject')) document.getElementById('email-submit-subject').value = emailSubmit.subject || emailTemplateDefaults.suggestion_submitted.subject;
  if (document.getElementById('email-submit-body')) document.getElementById('email-submit-body').value = emailSubmit.body || emailTemplateDefaults.suggestion_submitted.body;

  const emailOwned = emails.already_owned || {};
  if (document.getElementById('email-owned-subject')) document.getElementById('email-owned-subject').value = emailOwned.subject || emailTemplateDefaults.already_owned.subject;
  if (document.getElementById('email-owned-body')) document.getElementById('email-owned-body').value = emailOwned.body || emailTemplateDefaults.already_owned.body;

  const emailRejected = emails.rejected || {};
  if (document.getElementById('email-rejected-subject')) document.getElementById('email-rejected-subject').value = emailRejected.subject || emailTemplateDefaults.rejected.subject;
  if (document.getElementById('email-rejected-body')) document.getElementById('email-rejected-body').value = emailRejected.body || emailTemplateDefaults.rejected.body;

  currentRejectionTemplates = Array.isArray(emails.rejection_templates) ? JSON.parse(JSON.stringify(emails.rejection_templates)) : [];
  renderRejectionTemplates();

  const emailHold = emails.hold_placed || {};
  if (document.getElementById('email-hold-subject')) document.getElementById('email-hold-subject').value = emailHold.subject || emailTemplateDefaults.hold_placed.subject;
  if (document.getElementById('email-hold-body')) document.getElementById('email-hold-body').value = emailHold.body || emailTemplateDefaults.hold_placed.body;
}

async function populateLibrarySelector() {
  const select = document.getElementById('select-library-context');
  if (!select) return;

  try {
    const selectedOrgId = currentLibraryContextOrgId || select.value || 'system';
    select.disabled = true;
    select.innerHTML = '<option value="system">System Defaults</option>';

    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-library-selector'
    });

    orgs.forEach(org => {
      const opt = document.createElement('option');
      opt.value = org.organizationId;
      opt.textContent = `${org.displayName || org.name} (ID ${org.organizationId})`;
      select.appendChild(opt);
    });

    select.value = Array.from(select.options).some(option => option.value === selectedOrgId) ? selectedOrgId : 'system';
    currentLibraryContextOrgId = select.value;
    const selectedOption = select.options[select.selectedIndex];
    if (selectedOption) {
      document.getElementById('library-context-display').textContent = selectedOption.text;
    }

    if (!librarySelectorBound) {
      select.addEventListener('change', async (e) => {
        const nextOrgId = e.target.value || 'system';
        const previousOrgId = currentLibraryContextOrgId || 'system';
        if (settingsDirty) {
          const proceed = await showConfirm('Unsaved changes', 'You have unsaved changes. Switch libraries without saving?');
          if (!proceed) {
            e.target.value = previousOrgId;
            return;
          }
        }
        currentLibraryContextOrgId = nextOrgId;
        const display = e.target.options[e.target.selectedIndex].text;
        document.getElementById('library-context-display').textContent = display;
        await loadLibrarySettings(currentLibraryContextOrgId);
        markSettingsClean('clean');
      });
      librarySelectorBound = true;
    }
  } catch (err) {
    console.error('Failed to populate library selector', err);
  } finally {
    select.disabled = false;
  }
}

async function loadLibrarySettings(orgId) {
  const requestedOrgId = orgId || 'system';
  const requestId = ++libraryContextLoadSerial;
  currentLibraryContextOrgId = requestedOrgId;

  try {
    let settings = {};
    let isOverride = false;

    const result = await authorizedJson(`/api/asap/staff/settings/library?orgId=${encodeURIComponent(requestedOrgId)}&_=${Date.now()}`, { cache: 'no-store' });
    if (requestId !== libraryContextLoadSerial || requestedOrgId !== currentLibraryContextOrgId) {
      return; // A newer request is in flight
    }

    settings = result;
    isOverride = !!result.isOverride;

    const resetBtn = document.getElementById('btn-reset-library-settings');
    const statusAlert = document.getElementById('library-override-status');
    const overrideMsg = document.getElementById('library-override-msg');

    if (requestedOrgId === 'system') {
      resetBtn.classList.add('hidden');
      statusAlert.classList.add('hidden');
      if (document.getElementById('system-enabled-libraries-group')) {
        document.getElementById('system-enabled-libraries-group').classList.remove('hidden');
        renderLibraryParticipationCheckboxes();
      }
    } else {
      statusAlert.classList.remove('hidden');
      if (document.getElementById('system-enabled-libraries-group')) {
        document.getElementById('system-enabled-libraries-group').classList.add('hidden');
      }
      if (isOverride) {
        statusAlert.className = 'alert alert-info mb-3 d-flex justify-content-between align-items-center';
        overrideMsg.innerHTML = '<i class="fa fa-check-circle mr-1"></i> Editing: <strong>' + escapeAttr(document.getElementById('library-context-display').textContent || 'selected library') + '</strong>. This library has custom settings.';
        resetBtn.classList.remove('hidden');
      } else {
        statusAlert.className = 'alert alert-warning mb-3 d-flex justify-content-between align-items-center';
        overrideMsg.innerHTML = '<i class="fa fa-info-circle mr-1"></i> Editing: <strong>' + escapeAttr(document.getElementById('library-context-display').textContent || 'selected library') + '</strong>. This library is using system defaults. Saving will create a library-specific override.';
        resetBtn.classList.add('hidden');
      }
    }

    populateEmailTemplateForms(settings.emails || {});
    populatePatronUiForms(settings.ui_text || {});
    populateWorkflowForms(settings.workflow || {});
    updateEmailStatusBanner(settings.emailStatus);
    if (settings.organizationSync) {
      const state = settings.organizationSync.status || 'not_loaded';
      const message = settings.organizationSync.error || settings.organizationSync.message || organizationsStatusMessage;
      setOrganizationsStatus(state, message);
    }
    return settings;

  } catch (err) {
    console.error('Error loading library settings:', err);
    showToast('Failed to load library settings', 'error');
  }
}

function populateWorkflowForms(wf) {
  setFieldValue('suggestion-limit', wf.suggestionLimit !== undefined ? wf.suggestionLimit : '5');
  setFieldValue('suggestion-limit-msg', wf.suggestionLimitMessage || 'Weekly suggestion limit reached');
  document.getElementById('outstanding-timeout-enabled').checked = !!wf.outstandingTimeoutEnabled;
  setFieldValue('outstanding-timeout-days', wf.outstandingTimeoutDays !== undefined ? wf.outstandingTimeoutDays : '30');
  
  setFieldChecked('outstanding-timeout-send-email', !!wf.outstandingTimeoutSendEmail);
  workflowSettings.outstandingTimeoutSendEmail = !!wf.outstandingTimeoutSendEmail;
  workflowSettings.outstandingTimeoutRejectionTemplateId = wf.outstandingTimeoutRejectionTemplateId || '';
  
  document.getElementById('hold-pickup-timeout-enabled').checked = !!wf.holdPickupTimeoutEnabled;
  setFieldValue('hold-pickup-timeout-days', wf.holdPickupTimeoutDays !== undefined ? wf.holdPickupTimeoutDays : '14');
  document.getElementById('pending-hold-timeout-enabled').checked = !!wf.pendingHoldTimeoutEnabled;
  setFieldValue('pending-hold-timeout-days', wf.pendingHoldTimeoutDays !== undefined ? wf.pendingHoldTimeoutDays : '14');

  // Cache for checkbox renderer
  window.lastWorkflowEnabledList = (wf.enabledLibraryOrgIds || '').split(',').map(s => s.trim()).filter(s => s.length > 0);

  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (container) {
    const checkboxes = container.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => {
      cb.checked = window.lastWorkflowEnabledList.indexOf(cb.value) >= 0;
    });
  }

  toggleTimeoutGroup();
  updateAutoRejectEmailControls();
  toggleHoldPickupTimeoutGroup();
  togglePendingHoldTimeoutGroup();

  setFieldValue('wf-common-authors-list', wf.commonAuthorsList || '');
  setFieldValue('wf-common-authors-message', wf.commonAuthorsMessage || '');
  document.getElementById('wf-common-authors-enabled').checked = !!wf.commonAuthorsEnabled;
  toggleCommonAuthorsGroup();
}

function populatePatronUiForms(uiText) {
  setFieldValue('ui-logo-alt', uiText.logoAlt || '');
  setFieldValue('ui-patron-page-title', uiText.pageTitle || '');
  setFieldValue('ui-barcode-label', uiText.barcodeLabel || '');
  setFieldValue('ui-pin-label', uiText.pinLabel || '');
  setFieldValue('ui-login-prompt', uiText.loginPrompt || 'Please enter your information below to start the suggestion process.');
  setFieldValue('ui-login-note', uiText.loginNote || 'Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.');
  setFieldValue('ui-suggestion-note', uiText.suggestionFormNote || 'If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don\'t see the email.');
  setFieldValue('ui-no-email-msg', uiText.noEmailMessage || 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.');
  setFieldValue('ui-success-title', uiText.successTitle || 'Suggestion Submitted');
  setFieldValue('ui-success-msg', uiText.successMessage || 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>');
  setFieldValue('ui-already-submitted-msg', uiText.alreadySubmittedMessage || 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>');
  renderDuplicateStatusLabelSettings(uiText.duplicateStatusLabels || {});
  setFieldValue('ui-ebook-msg', uiText.ebookMessage || '<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>');
  setFieldValue('ui-eaudiobook-msg', uiText.eaudiobookMessage || '<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>');

  // Format Labels & Available Formats
  const labels = uiText.formatLabels || {};
  const available = uiText.availableFormats || ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];

  // Merge into formatMap for the rest of the app
  Object.keys(labels).forEach(k => formatMap[k] = labels[k]);
  availableFormats = available;

  renderFormatSettings();
  updateModalFormatDropdowns();

  const pubOptionsField = document.getElementById('ui-publication-options');
  if (pubOptionsField) {
    pubOptionsField.value = normalizePublicationOptions(uiText.publicationOptions).join('\n');
  }
  const ageGroupsField = document.getElementById('ui-age-groups');
  if (ageGroupsField) {
    ageGroupsField.value = normalizeAgeGroups(uiText.ageGroups).join('\n');
  }
  renderPatronFormatRulesEditor(uiText.formatRules);
  setPublicationOptions(uiText.publicationOptions);
  setAgeGroups(uiText.ageGroups);
}

function normalizeDuplicateStatusLabels(labels = {}) {
  return { ...duplicateStatusLabelDefaults, ...(labels && typeof labels === 'object' ? labels : {}) };
}

function renderDuplicateStatusLabelSettings(labels = {}) {
  const container = document.getElementById('duplicate-status-labels-container');
  if (!container) return;
  const normalized = normalizeDuplicateStatusLabels(labels);
  container.innerHTML = duplicateStatusLabelFields.map(([key, label]) => `
    <div class="form-group col-md-6">
      <label for="duplicate-status-${escapeAttr(key)}" class="small font-weight-bold">${escapeAttr(label)}</label>
      <input type="text" id="duplicate-status-${escapeAttr(key)}" class="form-control form-control-sm duplicate-status-label-input" data-key="${escapeAttr(key)}" value="${escapeAttr(normalized[key] || '')}">
    </div>
  `).join('');
}

function collectDuplicateStatusLabels() {
  const labels = {};
  duplicateStatusLabelFields.forEach(([key]) => {
    const el = document.getElementById(`duplicate-status-${key}`);
    const value = el ? el.value.trim() : '';
    labels[key] = value || duplicateStatusLabelDefaults[key] || '';
  });
  labels['Silently Closed'] = labels.silent || duplicateStatusLabelDefaults.silent;
  return labels;
}

document.getElementById('btn-reset-library-settings').addEventListener('click', async () => {
  if (currentLibraryContextOrgId === 'system') return;
  const confirmed = await showConfirm('Reset library settings', 'Are you sure you want to delete this library\'s overrides and revert to system defaults?');
  if (confirmed) {
    await authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify({ orgId: currentLibraryContextOrgId, action: 'reset' })
    });
    showToast('Library settings reset to system defaults', 'success');
    await loadLibrarySettings(currentLibraryContextOrgId);
    markSettingsClean('clean');
  }
});

async function loadStaffUsers() {
  const msgEl = document.getElementById('staff-users-msg');
  const bodyEl = document.getElementById('staff-users-table-body');
  const refreshBtn = document.getElementById('btn-refresh-staff-users');
  if (!msgEl || !bodyEl) {
    return;
  }

  if (refreshBtn) refreshBtn.disabled = true;
  msgEl.textContent = 'Loading staff users...';
  msgEl.className = 'mb-2 text-muted';
  bodyEl.innerHTML = '<tr><td colspan="6" class="text-muted">Loading staff users...</td></tr>';

  try {
    const result = await authorizedJson('/api/asap/staff/users');
    const users = Array.isArray(result.users) ? result.users : [];
    canAssignSuperAdmin = !!result.canAssignSuperAdmin;
    renderStaffUsers(users);
    msgEl.textContent = users.length ? `Loaded ${users.length} staff user${users.length === 1 ? '' : 's'}.` : 'No staff users found.';
    msgEl.className = 'mb-2 text-muted';
  } catch (err) {
    console.error('Failed to load staff users', err);
    msgEl.textContent = err.message || 'Failed to load staff users.';
    msgEl.className = 'mb-2 text-danger font-weight-bold';
    bodyEl.innerHTML = '<tr><td colspan="6" class="text-muted">Unable to load staff users.</td></tr>';
  } finally {
    if (refreshBtn) refreshBtn.disabled = false;
  }
}

function renderStaffUsers(users) {
  const bodyEl = document.getElementById('staff-users-table-body');
  if (!bodyEl) {
    return;
  }

  bodyEl.innerHTML = '';

  if (!users.length) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 6;
    td.className = 'text-muted';
    td.textContent = 'No staff users found.';
    tr.appendChild(td);
    bodyEl.appendChild(tr);
    return;
  }


  for (const user of users) {
    const tr = document.createElement('tr');
    tr.setAttribute('data-staff-id', user.id || '');

    const tdUsername = document.createElement('td');
    const strongUsername = document.createElement('strong');
    strongUsername.textContent = user.username || '';
    tdUsername.appendChild(strongUsername);
    tr.appendChild(tdUsername);

    const tdDomain = document.createElement('td');
    if (user.domain) {
      tdDomain.textContent = user.domain;
    } else {
      const span = document.createElement('span');
      span.className = 'text-muted';
      span.textContent = 'Default';
      tdDomain.appendChild(span);
    }
    tr.appendChild(tdDomain);

    const tdLibrary = document.createElement('td');
    tdLibrary.textContent = user.libraryOrgName || user.libraryOrgId || (user.scope === 'system' ? 'System' : 'Unmapped');
    tr.appendChild(tdLibrary);

    const tdDisplayName = document.createElement('td');
    if (user.displayName) {
      tdDisplayName.textContent = user.displayName;
    } else {
      const span = document.createElement('span');
      span.className = 'text-muted';
      span.textContent = 'No display name';
      tdDisplayName.appendChild(span);
    }
    tr.appendChild(tdDisplayName);

    const tdRole = document.createElement('td');
    const select = document.createElement('select');
    select.className = 'form-control form-control-sm staff-role-select';

    const role = ['staff', 'admin', 'super_admin'].includes(String(user.role || '').toLowerCase()) ? String(user.role || '').toLowerCase() : 'staff';

    select.appendChild(new Option('Staff', 'staff', false, role === 'staff'));
    select.appendChild(new Option('Admin', 'admin', false, role === 'admin'));

    if (canAssignSuperAdmin) {
      select.appendChild(new Option('Super Admin', 'super_admin', false, role === 'super_admin'));
    }

    tdRole.appendChild(select);
    tr.appendChild(tdRole);

    const tdSave = document.createElement('td');
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-primary staff-role-save mr-1';
    btn.textContent = 'Save Role';
    tdSave.appendChild(btn);
    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'btn btn-sm btn-outline-danger staff-user-delete';
    del.textContent = 'Remove';
    tdSave.appendChild(del);
    tr.appendChild(tdSave);

    bodyEl.appendChild(tr);
  }
}

const staffUsersTableBody = document.getElementById('staff-users-table-body');
if (staffUsersTableBody) {
  staffUsersTableBody.addEventListener('click', async (e) => {
    const row = e.target.closest('tr[data-staff-id]');
    if (!row) return;
    const delBtn = e.target.closest('.staff-user-delete');
    if (delBtn) {
      const id = row.getAttribute('data-staff-id');
      const ok = await showConfirm('Remove staff member', 'Are you sure you want to remove this staff user from access?');
      if (!ok) return;
      delBtn.disabled = true;
      try {
        await authorizedJson(`/api/asap/staff/users/${encodeURIComponent(id)}`, { method: 'DELETE' });
        await populateStaffLibraryOptions();
    await loadStaffUsers();
      } catch (err) {
        const msgEl = document.getElementById('staff-users-msg');
        if (msgEl) { msgEl.textContent = err.message || 'Failed to remove staff user.'; msgEl.className = 'mb-2 text-danger font-weight-bold'; }
      } finally { delBtn.disabled = false; }
      return;
    }

    const btn = e.target.closest('.staff-role-save');
    if (!btn) return;


    if (!row) return;

    const id = row.getAttribute('data-staff-id');
    const select = row.querySelector('.staff-role-select');
    const nextRole = select ? select.value : 'staff';
    const msgEl = document.getElementById('staff-users-msg');

    btn.disabled = true;
    if (msgEl) {
      msgEl.textContent = 'Saving role...';
      msgEl.className = 'mb-2 text-muted';
    }

    try {
      await authorizedJson(`/api/asap/staff/users/${encodeURIComponent(id)}/role`, {
        method: 'POST',
        body: JSON.stringify({ role: nextRole })
      });
      if (msgEl) {
        msgEl.textContent = 'Staff role updated successfully.';
        msgEl.className = 'mb-2 text-success font-weight-bold';
      }
      await populateStaffLibraryOptions();
    await loadStaffUsers();
    } catch (err) {
      console.error('Failed to update staff role', err);
      if (msgEl) {
        msgEl.textContent = err.message || 'Failed to update staff role.';
        msgEl.className = 'mb-2 text-danger font-weight-bold';
      }
    } finally {
      btn.disabled = false;
    }
  });
}

const refreshStaffUsersBtn = document.getElementById('btn-refresh-staff-users');
if (refreshStaffUsersBtn) {
  refreshStaffUsersBtn.addEventListener('click', (e) => {
    e.preventDefault();
    loadStaffUsers();
  });
}

async function populateStaffLibraryOptions() {
  const select = document.getElementById('staff-add-library');
  if (!select) return;
  select.innerHTML = '<option value="">Select library</option>';
  const me = pb.authStore.model || {};
  if (isSuperAdminStaff()) {
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-staff-options'
    });
    orgs.forEach(org => select.appendChild(new Option(`${org.displayName || org.name} (ID ${org.organizationId})`, org.organizationId)));
  } else if (me.libraryOrgId) {
    select.appendChild(new Option(`${me.libraryOrgName || me.libraryOrgId} (ID ${me.libraryOrgId})`, me.libraryOrgId));
    select.value = me.libraryOrgId;
  }
}

const addStaffBtn = document.getElementById('btn-add-staff-user');
if (addStaffBtn) {
  addStaffBtn.addEventListener('click', async () => {
    const identity = getFieldValue('staff-add-identity').trim();
    const libraryOrgId = getFieldValue('staff-add-library').trim();
    const role = getFieldValue('staff-add-role').trim() || 'staff';
    const msgEl = document.getElementById('staff-users-msg');
    if (!identity) return showAlert('Enter a staff username or identity.');
    if (role !== 'super_admin' && !libraryOrgId) return showAlert('Select a library for this staff member.');
    try {
      const libSelect = document.getElementById('staff-add-library');
      const opt = libSelect && libSelect.selectedIndex >= 0 ? libSelect.options[libSelect.selectedIndex] : null;
      await authorizedJson('/api/asap/staff/users', { method: 'POST', body: JSON.stringify({ username: identity, libraryOrgId, libraryOrgName: opt ? opt.text : '', role }) });
      setFieldValue('staff-add-identity', '');
      if (msgEl) { msgEl.textContent = 'Staff member added.'; msgEl.className = 'mb-2 text-success font-weight-bold'; }
      await populateStaffLibraryOptions();
    await loadStaffUsers();
    } catch (err) {
      if (msgEl) { msgEl.textContent = err.message || 'Failed to add staff member.'; msgEl.className = 'mb-2 text-danger font-weight-bold'; }
    }
  });
}

function buildSettingsPayload() {
  function positiveInt(id, fallback, label) {
    const raw = getFieldValue(id, String(fallback)).trim();
    if (!raw) return fallback;
    const value = parseInt(raw, 10);
    if (!Number.isFinite(value) || value < 1) {
      throw new Error(`${label} must be a number greater than 0.`);
    }
    return value;
  }

  const smtp = {
    host: getFieldValue('smtp-host').trim(),
    port: positiveInt('smtp-port', 587, 'SMTP port'),
    username: getFieldValue('smtp-username').trim(),
    password: getFieldValue('smtp-password'),
    tls: getFieldChecked('smtp-tls', true)
  };

  const polaris = collectSettingsPolaris();

  const uiText = {
    logoAlt: getFieldValue('ui-logo-alt'),
    pageTitle: getFieldValue('ui-patron-page-title'),
    barcodeLabel: getFieldValue('ui-barcode-label'),
    pinLabel: getFieldValue('ui-pin-label'),
    loginPrompt: getFieldValue('ui-login-prompt'),
    loginNote: getFieldValue('ui-login-note'),
    suggestionFormNote: getFieldValue('ui-suggestion-note'),
    noEmailMessage: getFieldValue('ui-no-email-msg'),
    successTitle: getFieldValue('ui-success-title'),
    successMessage: getFieldValue('ui-success-msg'),
    alreadySubmittedMessage: getFieldValue('ui-already-submitted-msg'),
    duplicateStatusLabels: collectDuplicateStatusLabels(),
    ebookMessage: getFieldValue('ui-ebook-msg'),
    eaudiobookMessage: getFieldValue('ui-eaudiobook-msg'),
    formatLabels: collectFormatLabels(),
    availableFormats: collectAvailableFormats(),
    publicationOptions: normalizePublicationOptions(getFieldValue('ui-publication-options')),
    ageGroups: normalizeAgeGroups(getFieldValue('ui-age-groups')),
    formatRules: collectPatronFormatRules()
  };

  const emails = {
    fromAddress: getFieldValue('email-from-address'),
    fromName: getFieldValue('email-from-name'),
    suggestion_submitted: {
      subject: getFieldValue('email-submit-subject'),
      body: getFieldValue('email-submit-body')
    },
    already_owned: {
      subject: getFieldValue('email-owned-subject'),
      body: getFieldValue('email-owned-body')
    },
    rejected: {
      subject: getFieldValue('email-rejected-subject'),
      body: getFieldValue('email-rejected-body')
    },
    rejection_templates: currentRejectionTemplates,
    hold_placed: {
      subject: getFieldValue('email-hold-subject'),
      body: getFieldValue('email-hold-body')
    }
  };

  const sendAutoRejectEmail = getFieldChecked('outstanding-timeout-send-email');
  const selectedTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');
  if (getFieldChecked('outstanding-timeout-enabled') && sendAutoRejectEmail) {
    if (!currentRejectionTemplates.length) {
      throw new Error('Create at least one rejection template before enabling auto-reject email.');
    }
    if (currentRejectionTemplates.length > 1 && !selectedTemplateId) {
      throw new Error('Select a rejection template for auto-reject emails.');
    }
  }

  const payload = {
    smtp, polaris, ui_text: uiText, emails,
    suggestionLimit: positiveInt('suggestion-limit', 5, 'Suggestion limit'),
    suggestionLimitMessage: getFieldValue('suggestion-limit-msg'),
    outstandingTimeoutEnabled: getFieldChecked('outstanding-timeout-enabled'),
    outstandingTimeoutDays: positiveInt('outstanding-timeout-days', 30, 'Auto-reject stalled suggestions days'),
    outstandingTimeoutSendEmail: sendAutoRejectEmail,
    outstandingTimeoutRejectionTemplateId: currentRejectionTemplates.length === 1 ? currentRejectionTemplates[0].id : selectedTemplateId,
    holdPickupTimeoutEnabled: getFieldChecked('hold-pickup-timeout-enabled'),
    holdPickupTimeoutDays: positiveInt('hold-pickup-timeout-days', 14, 'Auto-close unpicked-up holds days'),
    pendingHoldTimeoutEnabled: getFieldChecked('pending-hold-timeout-enabled'),
    pendingHoldTimeoutDays: positiveInt('pending-hold-timeout-days', 14, 'Auto-close pending holds days'),
    enabledLibraryOrgIds: collectEnabledLibraryIds(),
    commonAuthorsEnabled: getFieldChecked('wf-common-authors-enabled'),
    commonAuthorsList: sortAuthorsByLastName(getFieldValue('wf-common-authors-list')),
    commonAuthorsMessage: getFieldValue('wf-common-authors-message')
  };

  const fileInput = document.getElementById('ui-logo-file');
  if (fileInput && fileInput.files.length > 0) {
    payload.logo = fileInput.files[0];
  }

  return payload;
}

async function saveSettings(options = {}) {
  const submitBtn = settingsForm.querySelector('button[type="submit"]');
  const triggerBtn = options.button || null;
  const buttons = Array.from(new Set([submitBtn, triggerBtn].filter(Boolean)));
  const msg = document.getElementById('settings-msg');
  let saveHadError = false;
  let saveSucceeded = false;

  settingsSaving = true;
  updateSaveBarState('saving');
  buttons.forEach(button => {
    button.disabled = true;
  });
  msg.textContent = options.pendingText || 'Saving...';
  msg.className = 'mt-2 font-weight-bold text-info';

  try {
    if (!validateSmtpHostField(true)) {
      throw new Error('SMTP host is invalid.');
    }
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();

    // Save templates via the new library-scoped API
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      smtp: payload.smtp,
      polaris: payload.polaris,
      emails: payload.emails,
      ui_text: payload.ui_text,
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
        enabledLibraryOrgIds: payload.enabledLibraryOrgIds,
        commonAuthorsEnabled: payload.commonAuthorsEnabled,
        commonAuthorsList: payload.commonAuthorsList,
        commonAuthorsMessage: payload.commonAuthorsMessage
      }
    };

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify(libraryPayload)
    });

    await libraryPromise;
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    await loadSettings({ showErrors: false }); // Sync internal state (also triggers loadLibrarySettings)
    await loadStaffConfig(); // Refresh logo and titles immediately after saving
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
    settingsSaving = false;
    buttons.forEach(button => {
      button.disabled = false;
    });
    updateSaveBarState(saveHadError ? 'error' : (saveSucceeded ? 'saved' : (settingsDirty ? 'dirty' : 'clean')));
  }
}

settingsForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  await saveSettings();
});
settingsForm.addEventListener('input', markSettingsDirty);
settingsForm.addEventListener('change', markSettingsDirty);

async function loadStaffConfig() {
  try {
    const res = await fetch('/api/asap/config');
    const config = await res.json();
    if (config) {
      if (config.logoUrl) {
        document.getElementById('app-icon').href = config.logoUrl;
        document.getElementById('setup-logo').src = config.logoUrl;
        document.getElementById('login-logo').src = config.logoUrl;
        document.getElementById('nav-logo').src = config.logoUrl;
      }
      if (config.logoAlt) {
        document.getElementById('setup-logo').alt = config.logoAlt;
        document.getElementById('login-logo').alt = config.logoAlt;
        document.getElementById('nav-logo').alt = config.logoAlt;
      }
      setPublicationOptions(config.publicationOptions);
    }
  } catch (err) {
    console.error('Failed to load global config');
  }
}

async function initStaffApp() {
  initSettingsNavigation();
  await loadStaffConfig();
  await loadSetupStatus();
  checkAuth();
}

initStaffApp();

document.getElementById('outstanding-timeout-enabled').addEventListener('change', () => {
  toggleTimeoutGroup();
  updateAutoRejectEmailControls();
});
document.getElementById('outstanding-timeout-send-email').addEventListener('change', updateAutoRejectEmailControls);

function toggleTimeoutGroup() {
  const group = document.getElementById('timeout-config-group');
  const enabled = document.getElementById('outstanding-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

function toggleHoldPickupTimeoutGroup() {
  const group = document.getElementById('hold-pickup-timeout-group');
  const enabled = document.getElementById('hold-pickup-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

function togglePendingHoldTimeoutGroup() {
  const group = document.getElementById('pending-hold-timeout-group');
  const enabled = document.getElementById('pending-hold-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

function toggleCommonAuthorsGroup() {
  const group = document.getElementById('common-authors-config-group');
  const enabled = document.getElementById('wf-common-authors-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

function sortAuthorsByLastName(authorsListStr) {
  if (!authorsListStr) return '';
  const authors = authorsListStr.split('\n').map(s => s.trim()).filter(s => s.length > 0);
  authors.sort((a, b) => {
    // Basic last name detection (everything after the first comma, or the last word)
    const getLastName = (name) => {
      if (name.includes(',')) return name.split(',')[0].trim();
      const parts = name.split(' ');
      return parts[parts.length - 1].trim();
    };
    const lastA = getLastName(a).toLowerCase();
    const lastB = getLastName(b).toLowerCase();
    return lastA.localeCompare(lastB);
  });
  return authors.join('\n');
}

document.getElementById('hold-pickup-timeout-enabled').addEventListener('change', toggleHoldPickupTimeoutGroup);
document.getElementById('pending-hold-timeout-enabled').addEventListener('change', togglePendingHoldTimeoutGroup);
document.getElementById('wf-common-authors-enabled').addEventListener('change', toggleCommonAuthorsGroup);

document.getElementById('edit-bibid').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    document.getElementById('btn-bib-lookup').click();
  }
});

document.getElementById('edit-bibid').addEventListener('input', () => {
  const bibId = document.getElementById('edit-bibid').value.trim();
  if (verifiedBibId && bibId !== verifiedBibId) {
    verifiedBibId = '';
    document.getElementById('bib-info-display').classList.add('hidden');
    document.getElementById('bib-info-text').textContent = '';
  }
});

function renderFormatSettings() {
  const container = document.getElementById('format-settings-container');
  if (!container) return;

  // Show enabled formats first (in their saved order), then disabled ones
  const enabledKeys = availableFormats.filter(k => formatMap[k] !== undefined);
  const disabledKeys = Object.keys(formatMap).filter(k => !availableFormats.includes(k));
  const allKeys = [...enabledKeys, ...disabledKeys];

  container.innerHTML = `
    <div class="table-responsive">
      <table class="table table-sm mb-0">
        <thead>
          <tr>
            <th class="format-drag-col"></th>
            <th class="format-show-col">Show</th>
            <th class="format-key-col">Format key</th>
            <th>Display label</th>
            <th class="format-remove-col"></th>
          </tr>
        </thead>
        <tbody id="format-settings-body">
          ${allKeys.map(key => `
            <tr class="format-setting-row" data-key="${escapeAttr(key)}" draggable="true">
              <td class="align-middle text-muted format-drag-handle">&#8597;</td>
              <td class="align-middle">
                <div class="custom-control custom-checkbox">
                  <input type="checkbox" class="custom-control-input format-enabled-check" id="fmt-chk-${key}" ${availableFormats.includes(key) ? 'checked' : ''}>
                  <label class="custom-control-label" for="fmt-chk-${key}"></label>
                </div>
              </td>
              <td class="align-middle"><code>${escapeAttr(key)}</code></td>
              <td>
                <input type="text" class="form-control form-control-sm format-label-input" value="${escapeAttr(formatMap[key] || key)}">
              </td>
              <td class="text-right align-middle">
                ${['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'].includes(key) ? '' : '<button type="button" class="btn btn-sm btn-link text-danger p-0 btn-remove-format">&times;</button>'}
              </td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    </div>
  `;

  initFormatDragSort();
}

function initFormatDragSort() {
  const tbody = document.getElementById('format-settings-body');
  if (!tbody) return;

  let draggingRow = null;

  tbody.addEventListener('dragstart', (e) => {
    draggingRow = e.target.closest('tr');
    if (draggingRow) {
      draggingRow.classList.add('format-row-dragging');
      e.dataTransfer.effectAllowed = 'move';
    }
  });

  tbody.addEventListener('dragend', () => {
    if (draggingRow) {
      draggingRow.classList.remove('format-row-dragging');
      draggingRow = null;
    }
    // Remove all drag-over highlights
    tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
  });

  tbody.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const target = e.target.closest('tr');
    if (target && target !== draggingRow) {
      tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
      target.classList.add('format-row-drop-target');
    }
  });

  tbody.addEventListener('drop', (e) => {
    e.preventDefault();
    const target = e.target.closest('tr');
    tbody.querySelectorAll('tr').forEach(r => r.classList.remove('format-row-drop-target'));
    if (target && draggingRow && target !== draggingRow) {
      tbody.insertBefore(draggingRow, target);
      markSettingsDirty();
    }
  });
}

function collectFormatLabels() {
  const labels = {};
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const key = row.getAttribute('data-key');
    const label = row.querySelector('.format-label-input').value.trim();
    if (key && label) labels[key] = label;
  });
  return labels;
}

function collectAvailableFormats() {
  const available = [];
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const key = row.getAttribute('data-key');
    const enabled = row.querySelector('.format-enabled-check').checked;
    if (key && enabled) available.push(key);
  });
  return available;
}

function updateModalFormatDropdowns() {
  ['edit-format', 'new-format'].forEach(id => {
    const select = document.getElementById(id);
    if (!select) return;

    // Keep current value
    const val = select.value;

    // Only include availableFormats
    select.innerHTML = availableFormats.map(k => `
      <option value="${escapeAttr(k)}">${escapeAttr(formatMap[k] || k)}</option>
    `).join('');

    // Try to restore value, or fallback to first
    select.value = availableFormats.includes(val) ? val : (availableFormats[0] || '');
  });
}

const btnAddFormat = document.getElementById('btn-add-format');
if (btnAddFormat) {
  btnAddFormat.addEventListener('click', () => {
    const keyInput = document.getElementById('new-format-key');
    const labelInput = document.getElementById('new-format-label');
    const rawKey = keyInput ? keyInput.value.trim() : '';
    const rawLabel = labelInput ? labelInput.value.trim() : '';
    const key = rawKey.toLowerCase().replace(/[^a-z0-9_]/g, '_').replace(/^_+|_+$/g, '');
    if (!key) {
      setInlineStatus('new-format-error', 'Enter a short format key, such as videogame.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    if (!/^[a-z0-9_]+$/.test(key)) {
      setInlineStatus('new-format-error', 'Use only letters, numbers, and underscores for the format key.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    if (formatMap[key]) {
      setInlineStatus('new-format-error', 'This format key already exists.', 'danger');
      if (keyInput) keyInput.focus();
      return;
    }
    const label = rawLabel || key.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    formatMap[key] = label;
    availableFormats.push(key);
    if (keyInput) keyInput.value = '';
    if (labelInput) labelInput.value = '';
    setInlineStatus('new-format-error', `Added ${label}. Save settings to keep this format.`, 'success');
    renderFormatSettings();
    renderPatronFormatRulesEditor(collectPatronFormatRules());
    markSettingsDirty();
  });
}

const formatSettingsContainer = document.getElementById('format-settings-container');
if (formatSettingsContainer) {
  formatSettingsContainer.addEventListener('click', async (e) => {
    if (e.target.classList.contains('btn-remove-format')) {
      const row = e.target.closest('tr');
      const key = row.getAttribute('data-key');
      if (await showConfirm('Remove format', `Remove format "${key}"? This will only remove it from the settings list. Existing suggestions with this format will remain in the database.`)) {
        delete formatMap[key];
        availableFormats = availableFormats.filter(k => k !== key);
        renderFormatSettings();
        renderPatronFormatRulesEditor(collectPatronFormatRules());
      }
    }
  });
}

// Keep duplicate sender fields in sync between Email Settings and SMTP Settings
function syncInputPair(idA, idB) {
  const elA = document.getElementById(idA);
  const elB = document.getElementById(idB);
  if (elA && elB) {
    elA.addEventListener('input', (e) => elB.value = e.target.value);
    elB.addEventListener('input', (e) => elA.value = e.target.value);
  }
}
syncInputPair('email-from-address', 'smtp-from');
syncInputPair('email-from-name', 'smtp-from-name');
const smtpHostInput = document.getElementById('smtp-host');
if (smtpHostInput) {
  smtpHostInput.addEventListener('blur', () => validateSmtpHostField(true));
  smtpHostInput.addEventListener('input', () => {
    if (isValidSmtpHost(smtpHostInput.value) || !smtpHostInput.value.trim()) {
      const resultEl = document.getElementById('smtp-test-result');
      if (resultEl && resultEl.className.includes('text-danger') && resultEl.textContent.includes('SMTP host')) {
        resultEl.textContent = '';
        resultEl.className = 'd-block mt-2';
      }
    }
  });
}

function renderRejectionTemplates() {
  const container = document.getElementById('rejection-templates-container');
  if (!container) return;

  if (currentRejectionTemplates.length === 0) {
    container.innerHTML = '<div class="text-muted small">No additional rejection templates configured.</div>';
    return;
  }

  container.innerHTML = '';
  currentRejectionTemplates.forEach((template, index) => {
    const wrapper = document.createElement('div');
    wrapper.className = 'border rounded p-3 mb-3 bg-light position-relative';

    wrapper.innerHTML = `
      <button type="button" class="btn btn-sm btn-outline-danger position-absolute rejection-template-remove" onclick="removeRejectionTemplate(${index})" title="Remove template">&times;</button>
      <div class="form-group">
        <label>Template name</label>
        <input type="text" class="form-control form-control-sm" value="${escapeAttr(template.name || '')}" onchange="updateRejectionTemplate(${index}, 'name', this.value)">
      </div>
      <div class="form-group">
        <label>Subject</label>
        <input type="text" class="form-control form-control-sm" value="${escapeAttr(template.subject || '')}" onchange="updateRejectionTemplate(${index}, 'subject', this.value)">
      </div>
      <div class="form-group mb-0">
        <label>Body</label>
        <textarea class="form-control form-control-sm" rows="3" onchange="updateRejectionTemplate(${index}, 'body', this.value)">${escapeAttr(template.body || '')}</textarea>
      </div>
    `;
    container.appendChild(wrapper);
  });
}

function updateRejectionTemplate(index, field, value) {
  if (currentRejectionTemplates[index]) {
    currentRejectionTemplates[index][field] = value;
    markSettingsDirty();
  }
}

function removeRejectionTemplate(index) {
  currentRejectionTemplates.splice(index, 1);
  renderRejectionTemplates();
  markSettingsDirty();
}

const btnAddRejectionTemplate = document.getElementById('btn-add-rejection-template');
if (btnAddRejectionTemplate) {
  btnAddRejectionTemplate.addEventListener('click', () => {
    currentRejectionTemplates.unshift({
      id: pb.authStore.model ? pb.authStore.model.id + '_' + Date.now() : 'tpl_' + Date.now(),
      name: 'New Rejection Reason',
      subject: emailTemplateDefaults.rejected.subject,
      body: emailTemplateDefaults.rejected.body
    });
    renderRejectionTemplates();
    markSettingsDirty();
  });
}

async function renderLibraryParticipationCheckboxes() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container || container.getAttribute('data-loaded') === 'true') return;

  if (organizationsStatus === 'loading') {
    container.innerHTML = '<div class="p-3 text-muted">Organizations loading...</div>';
    return;
  }

  if (organizationsStatus === 'error') {
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
    return;
  }

  try {
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-participation'
    });

    if (!orgs.length) {
      if (organizationsStatus === 'not_loaded') {
        container.innerHTML = '<div class="p-3 text-muted">Organizations have not been synced yet. Use Settings > Polaris > Sync Polaris Organizations Now.</div>';
      } else {
        container.innerHTML = '<div class="p-3 text-muted">Organization sync completed, but no library organizations were returned.</div>';
      }
      return;
    }

    setOrganizationsStatus('loaded', `Polaris organizations loaded. ${orgs.length} library organization${orgs.length === 1 ? '' : 's'} available. Leave all libraries unchecked to enable all organizations.`);
    container.innerHTML = `
      <table class="table table-sm table-hover mb-0">
        <thead class="bg-white library-table-head">
          <tr>
            <th class="library-enable-col">Enable</th>
            <th>Library name</th>
            <th class="library-id-col">ID</th>
          </tr>
        </thead>
        <tbody>
          ${orgs.map(org => `
            <tr>
              <td class="align-middle">
                <div class="custom-control custom-checkbox">
                  <input type="checkbox" class="custom-control-input lib-participation-cb" id="lib-p-${org.organizationId}" value="${org.organizationId}">
                  <label class="custom-control-label" for="lib-p-${org.organizationId}"></label>
                </div>
              </td>
              <td class="align-middle font-weight-bold">${escapeAttr(org.displayName || org.name)}</td>
              <td class="align-middle text-muted small">${org.organizationId}</td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    `;

    container.setAttribute('data-loaded', 'true');

    // Restore checked state if we have it
    if (window.lastWorkflowEnabledList) {
      const checkboxes = container.querySelectorAll('.lib-participation-cb');
      checkboxes.forEach(cb => {
        cb.checked = window.lastWorkflowEnabledList.indexOf(cb.value) >= 0;
      });
    }
  } catch (err) {
    console.error('Failed to load libraries for participation list', err);
    setOrganizationsStatus('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
  }
}

function collectEnabledLibraryIds() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container) return '';
  const checked = Array.from(container.querySelectorAll('.lib-participation-cb:checked')).map(cb => cb.value);
  return checked.join(',');
}
