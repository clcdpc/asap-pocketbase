const pb = new PocketBase(window.location.origin);
const SETTINGS_RECORD_ID = 'settings0000001';

const loginContainer = document.getElementById('login-container');
const setupContainer = document.getElementById('setup-container');
const appContainer = document.getElementById('app-container');
const loginForm = document.getElementById('login-form');
const setupForm = document.getElementById('setup-form');
const logoutBtn = document.getElementById('logout-btn');
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
const closeReasonMap = {
  rejected: 'Rejected by staff',
  hold_completed: 'Hold placed / completed',
  manual: 'Manually closed'
};
const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
const patronFormatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
const patronFormatFields = [
  { key: 'title', label: 'Title', storage: 'title' },
  { key: 'author', label: 'Author / Creator', storage: 'author' },
  { key: 'identifier', label: 'Identifier', storage: 'identifier' },
  { key: 'agegroup', label: 'Age Group', storage: 'agegroup' },
  { key: 'publication', label: 'Publication Timing', storage: 'publication' }
];
const defaultPatronFormatRules = {
  book: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  audiobook_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  dvd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Director/Actors/Producer' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  music_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Artist' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  ebook: {
    messageBehavior: 'ebookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  eaudiobook: {
    messageBehavior: 'eaudiobookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  }
};

const descriptions = {
  suggestion: 'Unprocessed suggestions submitted by library patrons.',
  outstanding_purchase: 'Items approved for purchase that are not yet in the library catalog. The Auto-Promoter (if enabled in Settings) will search Polaris nightly for these items.',
  pending_hold: 'Items in the catalog waiting for an automated hold to be placed.',
  hold_placed: 'Materials that now have active holds placed for the patron.',
  closed: 'Completed, rejected, or manually closed suggestions.',
  settings: 'Manage application configuration and integrations.'
};

const statusStages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];

let currentStatus = 'suggestion';
let currentSuggestions = [];
let allSuggestions = [];
let verifiedNewSuggestionBarcode = '';
let verifiedBibId = '';
let publicationOptions = defaultPublicationOptions.slice();
let workflowSettings = {
  autoPromote: false,
  outstandingTimeoutEnabled: false,
  outstandingTimeoutDays: 30,
  holdPickupTimeoutEnabled: false,
  holdPickupTimeoutDays: 14
};
let currentLibraryContextOrgId = 'system';
let libraryTemplateOverrides = {}; // Map of orgId -> isOverride (boolean)
let libraryContextLoadSerial = 0;
let librarySelectorBound = false;
let bootstrapAdminMessage = '';
let setupRequired = false;
let canAssignSuperAdmin = false;
const settingsSectionIds = ['start', 'polaris', 'staff', 'smtp', 'workflow', 'patron', 'templates'];
let currentSettingsSection = 'start';

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
    if (!dialog) { alert(message); resolve(); return; }
    document.getElementById('alert-dialog-message').textContent = message;
    const okBtn = document.getElementById('alert-dialog-ok');
    function onOk() {
      dialog.close();
      okBtn.removeEventListener('click', onOk);
      resolve();
    }
    okBtn.addEventListener('click', onOk);
    dialog.showModal();
    okBtn.focus();
  });
}

function showConfirm(message) {
  return new Promise(resolve => {
    const dialog = document.getElementById('confirm-dialog');
    if (!dialog) { resolve(confirm(message)); return; }
    document.getElementById('confirm-dialog-message').textContent = message;
    const okBtn = document.getElementById('confirm-dialog-ok');
    const cancelBtn = document.getElementById('confirm-dialog-cancel');
    function cleanup(result) {
      dialog.close();
      okBtn.removeEventListener('click', onOk);
      cancelBtn.removeEventListener('click', onCancel);
      resolve(result);
    }
    function onOk() { cleanup(true); }
    function onCancel() { cleanup(false); }
    okBtn.addEventListener('click', onOk);
    cancelBtn.addEventListener('click', onCancel);
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
    alert.textContent = bootstrapAdminMessage;
    alert.classList.remove('hidden');
  } else {
    alert.textContent = '';
    alert.classList.add('hidden');
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
    checkAuth();
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

document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
  link.addEventListener('click', () => {
    activateStatusTab(link.getAttribute('data-status'));
    loadTab(currentStatus);
  });
});

async function loadTab(status) {
  const tabDesc = document.getElementById('tab-desc');
  let desc = descriptions[status] || '';
  
  // Add auto-rejection info for Suggestions
  if (status === 'suggestion') {
    desc += ` The Auto-Rejector (${workflowSettings.outstandingTimeoutEnabled ? 'enabled' : 'currently disabled'} in Settings) will automatically reject suggestions older than ${workflowSettings.outstandingTimeoutDays} days.`;
  }

  // Add auto-close info for Hold Placed
  if (status === 'hold_placed') {
    if (workflowSettings.holdPickupTimeoutEnabled) {
      desc += ` Holds will move to Closed automatically when the patron checks out the item, or after ${workflowSettings.holdPickupTimeoutDays} days if the item is never picked up (auto-close enabled in Settings).`;
    } else {
      desc += ' Holds will only move to Closed when the patron checks out the item. Enable auto-close in Settings to also close holds that are never picked up.';
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
      gridContainer.innerHTML = '<div class="alert alert-light border">No records in this stage.</div>';
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
  return gridjs.html(`<button type="button" class="truncate-note" data-full-note="${escapeAttr(text)}" title="Click to view full note" aria-label="Truncated note, click to view full text">${escapeAttr(visibleText)}</button>`);
}

function getGridColumns(status) {
  if (status === 'suggestion') {
    return [
      'Barcode',
      'Title',
      'Author',
      'Format',
      'Timing',
      'Submitted',
      { name: 'Notes', width: '200px' },
      'Edited By',
      { name: 'Actions', width: '280px', sort: false },
    ];
  }

  if (status === 'closed') {
    return [
      'Barcode',
      'Title',
      'Author',
      'Format',
      'Submitted',
      'Closed Reason',
      { name: 'Notes', width: '200px' },
      'Edited By',
      { name: 'Actions', width: '130px', sort: false },
    ];
  }

  return [
    'Barcode',
    'Title',
    'Author',
    'ISBN',
    'BIBID',
    'Age Group',
    'Format',
    'Timing',
    'Submitted',
    'Last Checked',
    { name: 'Notes', width: '200px' },
    'Edited By',
    { name: 'Actions', width: '210px', sort: false },
  ];
}

function getGridRow(row, status) {
  if (status === 'suggestion') {
    return [
      row.barcode,
      row.title,
      row.author,
      formatMap[row.format] || row.format,
      formatPublication(row.publication),
      formatStandardDate(row.created),
      formatNote(row.notes),
      row.editedBy,
      gridjs.html(getActionButtons(row)),
    ];
  }

  if (status === 'closed') {
    return [
      row.barcode,
      row.title,
      row.author,
      formatMap[row.format] || row.format,
      formatStandardDate(row.created),
      formatCloseReason(row),
      formatNote(row.notes),
      row.editedBy,
      gridjs.html(getActionButtons(row)),
    ];
  }

  return [
    row.barcode,
    row.title,
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
    gridjs.html(getActionButtons(row)),
  ];
}

function formatCloseReason(row) {
  if (normalizeStatus(row.status) !== 'closed') {
    return '';
  }
  return closeReasonMap[row.closeReason] || 'Closed';
}

function getActionButtons(row) {
  const id = escapeAttr(row.id);
  const status = normalizeStatus(row.status);

  if (status === 'suggestion') {
    return `<button type="button" class="btn btn-sm btn-primary" data-row-action="edit" data-row-id="${id}" data-next-status="outstanding_purchase" data-dialog-title="Approve for Purchase" data-action-value="purchase">Purchase</button>
            <button type="button" class="btn btn-sm btn-warning" data-row-action="edit" data-row-id="${id}" data-next-status="pending_hold" data-dialog-title="Already Own" data-action-value="alreadyOwn">Already Own</button>
            <button type="button" class="btn btn-sm btn-danger" data-row-action="edit" data-row-id="${id}" data-next-status="closed" data-dialog-title="Reject" data-action-value="reject">Reject</button>
            <button type="button" class="btn btn-sm btn-outline-danger" data-row-action="edit" data-row-id="${id}" data-next-status="closed" data-dialog-title="Silent Close" data-action-value="silentClose">Silent Close</button>
            <button type="button" class="btn btn-sm btn-secondary" data-row-action="edit" data-row-id="${id}" data-next-status="suggestion" data-dialog-title="Edit Suggestion" data-action-value="">Edit</button>`;
  }

  if (status === 'outstanding_purchase') {
    return `<button type="button" class="btn btn-sm btn-success" data-row-action="edit" data-row-id="${id}" data-next-status="pending_hold" data-dialog-title="Move to Pending Hold" data-action-value="">Ready for Hold</button>
            <button type="button" class="btn btn-sm btn-outline-danger" data-row-action="edit" data-row-id="${id}" data-next-status="closed" data-dialog-title="Silent Close" data-action-value="silentClose">Silent Close</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-row-action="undo" data-row-id="${id}">Undo</button>
            <button type="button" class="btn btn-sm btn-secondary" data-row-action="edit" data-row-id="${id}" data-next-status="outstanding_purchase" data-dialog-title="Edit" data-action-value="">Edit</button>`;
  }

  if (status === 'pending_hold' || status === 'hold_placed' || status === 'closed') {
    let buttons = `<button type="button" class="btn btn-sm btn-outline-secondary" data-row-action="undo" data-row-id="${id}">Undo</button>`;
    if (status !== 'closed') {
      buttons += `<button type="button" class="btn btn-sm btn-outline-danger" data-row-action="edit" data-row-id="${id}" data-next-status="closed" data-dialog-title="Silent Close" data-action-value="silentClose">Silent Close</button>`;
    }
    buttons += `<button type="button" class="btn btn-sm btn-secondary" data-row-action="edit" data-row-id="${id}" data-next-status="${escapeAttr(row.status)}" data-dialog-title="Edit" data-action-value="">Edit</button>`;
    return buttons;
  }

  return `<button type="button" class="btn btn-sm btn-secondary" data-row-action="edit" data-row-id="${id}" data-next-status="${escapeAttr(row.status)}" data-dialog-title="Edit" data-action-value="">Edit</button>`;
}

function escapeAttr(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

gridContainer.addEventListener('click', (e) => {
  const truncateBtn = e.target.closest('.truncate-note');
  if (truncateBtn && gridContainer.contains(truncateBtn)) {
    const fullNote = truncateBtn.getAttribute('data-full-note');
    document.getElementById('noteDialogContent').textContent = fullNote;
    document.getElementById('noteDialog').showModal();
    document.getElementById('noteDialogCloseBtn').focus();
    return;
  }

  const button = e.target.closest('[data-row-action]');
  if (!button || !gridContainer.contains(button)) return;

  const id = button.getAttribute('data-row-id');
  if (button.getAttribute('data-row-action') === 'undo') {
    undoRow(id);
    return;
  }

  openEdit(
    id,
    button.getAttribute('data-next-status') || '',
    button.getAttribute('data-dialog-title') || 'Edit',
    button.getAttribute('data-action-value') || ''
  );
});

function openEdit(id, nextStatus, dialogTitle, actionStr) {
  const row = currentSuggestions.find(r => r.id === id);
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
  document.getElementById('edit-format').value = row.format || 'book';
  document.getElementById('edit-age').value = row.agegroup || 'adult';
  setSelectValue(document.getElementById('edit-publication'), row.publication || publicationOptions[0]);
  document.getElementById('edit-exact-publication-date').value = dateOnly(row.exactPublicationDate);

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

  document.getElementById('editModal').showModal();
  document.getElementById('close-modal-btn').focus();
}

document.getElementById('edit-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const id = document.getElementById('edit-id').value;
  const nextStatus = document.getElementById('edit-next-status').value;
  const bibid = document.getElementById('edit-bibid').value.trim();
  if (nextStatus === 'pending_hold') {
    if (!bibid) {
      await showAlert('BIB ID is required before moving a suggestion to Pending Hold.');
      document.getElementById('edit-bibid').focus();
      return;
    }
    if (bibid !== verifiedBibId) {
      await showAlert('Please use the "Lookup BIB" button to verify this BIB ID before moving to Pending Hold.');
      document.getElementById('btn-bib-lookup').focus();
      return;
    }
  }
  const payload = {
    action: document.getElementById('edit-action').value || undefined,
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
        'outstanding_purchase': 'Pending Purchase',
        'pending_hold': 'Pending Hold',
        'hold_placed': 'Hold Placed',
        'closed': 'Closed'
      };
      await showAlert(`Note: This request was moved straight to "${statusNames[updatedRecord.status] || updatedRecord.status}" because it was detected as already being on hold or having a BIB ID.`);
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
      ? 'Required for Pending Hold so the system can place the patron hold.'
      : 'Needed before a hold can be placed for the patron.';
  }
}

async function undoRow(id) {
  if (!await showConfirm('Undo action and return this request to Suggestions?')) return;
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
          <td style="min-width: 130px;">
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
  const btn = e.target.closest('button');

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

  await postPolarisTest('/api/asap/staff/test-polaris', resSpan, { polaris: collectSettingsPolaris() }, {
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
if (syncOrganizationsBtn) {
  syncOrganizationsBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    const resultEl = document.getElementById('organizations-sync-result');
    syncOrganizationsBtn.disabled = true;
    setInlineResult(resultEl, 'Syncing organizations...', 'ml-2 text-muted');
    try {
      const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
      setInlineResult(resultEl, `Synced ${result.synced || 0} organization records.`, 'ml-2 text-success font-weight-bold');
    } catch (err) {
      setInlineResult(resultEl, 'Error: ' + (err.message || 'Organization sync failed.'), 'ml-2 text-danger font-weight-bold');
    } finally {
      syncOrganizationsBtn.disabled = false;
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
      msg.textContent = `Hold check complete. Promoted: ${data.promoted}, Holds: ${data.holdsPlaced}, Closed: ${data.checkoutClosures}, Timed out: ${data.timedOut}`;
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
  msg.textContent = 'Running auto promoter...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const res = await fetch('/api/asap/jobs/promoter-check', {
      method: 'POST',
      headers: { 'Authorization': pb.authStore.token }
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = `Auto promoter complete. Promoted: ${data.promoted} items to Pending Hold.`;
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
  const btn = e.target.closest('button');
  
  const testEmail = testInput ? testInput.value.trim() : '';

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
  const errorEl = document.getElementById('settings-error');
  if (errorEl) errorEl.classList.remove('hidden');
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.add('hidden');
}

function hideSettingsAccessDenied() {
  const errorEl = document.getElementById('settings-error');
  if (errorEl) errorEl.classList.add('hidden');
}

async function loadSettings(options = {}) {
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;

  // Filter sidebar for non-super admins
  document.querySelectorAll('[data-settings-nav]').forEach(el => {
    const section = el.getAttribute('data-settings-nav');
    if (!isSuper && section !== 'templates') {
      el.classList.add('hidden');
    } else {
      el.classList.remove('hidden');
    }
  });

  // If not super admin, force them to the templates section
  if (!isSuper && currentSettingsSection !== 'templates') {
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

  await loadLibrarySettings(currentLibraryContextOrgId);

  if (!isSuper) {
    // Hide error, show form for library admins even if they can't load app_settings
    const errorEl = document.getElementById('settings-error');
    if (errorEl) errorEl.classList.add('hidden');
    const formEl = document.getElementById('settings-form');
    if (formEl) formEl.classList.remove('hidden');
    return;
  }

  try {
    const record = await pb.collection('app_settings').getOne(SETTINGS_RECORD_ID);

    const smtp = record.smtp || {};
    const polaris = record.polaris || {};
    const uiText = record.ui_text || {};
    const emails = record.emails || {};

    workflowSettings.outstandingTimeoutEnabled = !!record.outstandingTimeoutEnabled;
    workflowSettings.outstandingTimeoutDays = parseInt(record.outstandingTimeoutDays || '30', 10) || 30;
    workflowSettings.autoPromote = polaris.autoPromote !== false;

    // Workflow form population is handled by loadLibrarySettings

    // SMTP
    setFieldValue('smtp-host', smtp.host || '');
    setFieldValue('smtp-port', smtp.port || 587);
    setFieldValue('smtp-username', smtp.username || '');
    setFieldValue('smtp-password', smtp.password || '');
    setFieldValue('smtp-from', smtp.from || '');
    setFieldValue('smtp-from-name', smtp.fromName || '');
    setFieldChecked('smtp-tls', smtp.tls !== false);

    // Polaris
    setFieldValue('polaris-host', polaris.host || '');
    setFieldValue('polaris-api-key', polaris.apiKey || '');
    setFieldValue('polaris-access-id', polaris.accessId || '');
    setFieldValue('polaris-domain', polaris.staffDomain || '');
    setFieldValue('polaris-admin-user', polaris.adminUser || '');
    setFieldValue('polaris-admin-pass', polaris.adminPassword || '');
    setFieldValue('polaris-override-pass', polaris.overridePassword || '');
    setFieldChecked('polaris-auto-promote', polaris.autoPromote !== false);
    setFieldValue('allowed-staff-users', normalizeAllowedStaffUsers(record.allowedStaffUsers || ''));

    // UI Text and Formats are handled by populatePatronUiForms called via loadLibrarySettings
    // but we can set them here if needed. Since loadLibrarySettings is called right before this, 
    // it will be populated. Wait, loadLibrarySettings is called AT THE TOP of loadSettings asynchronously!
    // So if it completes before loadSettings finishes, loadSettings might overwrite it?
    // Actually, loadLibrarySettings is awaited at the top. So we should just remove the uiText Population from loadSettings and let loadLibrarySettings handle it.
    await loadStaffUsers();



    // Success: hide error, show form
    const errorEl = document.getElementById('settings-error');
    if (errorEl) errorEl.classList.add('hidden');
    const formEl = document.getElementById('settings-form');
    if (formEl) formEl.classList.remove('hidden');

  } catch (err) {
    console.error('Failed to load settings', err);
    if (showErrors) {
      showSettingsAccessDenied();
    }
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
  const emailSubmit = emails.suggestion_submitted || {};
  if (document.getElementById('email-submit-subject')) document.getElementById('email-submit-subject').value = emailSubmit.subject || emailTemplateDefaults.suggestion_submitted.subject;
  if (document.getElementById('email-submit-body')) document.getElementById('email-submit-body').value = emailSubmit.body || emailTemplateDefaults.suggestion_submitted.body;

  const emailOwned = emails.already_owned || {};
  if (document.getElementById('email-owned-subject')) document.getElementById('email-owned-subject').value = emailOwned.subject || emailTemplateDefaults.already_owned.subject;
  if (document.getElementById('email-owned-body')) document.getElementById('email-owned-body').value = emailOwned.body || emailTemplateDefaults.already_owned.body;

  const emailRejected = emails.rejected || {};
  if (document.getElementById('email-rejected-subject')) document.getElementById('email-rejected-subject').value = emailRejected.subject || emailTemplateDefaults.rejected.subject;
  if (document.getElementById('email-rejected-body')) document.getElementById('email-rejected-body').value = emailRejected.body || emailTemplateDefaults.rejected.body;

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
      sort: 'displayName'
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
        currentLibraryContextOrgId = e.target.value || 'system';
        const display = e.target.options[e.target.selectedIndex].text;
        document.getElementById('library-context-display').textContent = display;
        await loadLibrarySettings(currentLibraryContextOrgId);
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
    } else {
      statusAlert.classList.remove('hidden');
      if (isOverride) {
        statusAlert.className = 'alert alert-info mb-3 d-flex justify-content-between align-items-center';
        overrideMsg.innerHTML = '<i class="fa fa-check-circle mr-1"></i> This library has <strong>Custom Overrides</strong> saved.';
        resetBtn.classList.remove('hidden');
      } else {
        statusAlert.className = 'alert alert-warning mb-3 d-flex justify-content-between align-items-center';
        overrideMsg.innerHTML = '<i class="fa fa-info-circle mr-1"></i> This library is currently using <strong>System Defaults</strong> for these settings. Saving changes will create a library-specific override.';
        resetBtn.classList.add('hidden');
      }
    }

    populateEmailTemplateForms(settings.emails || {});
    populatePatronUiForms(settings.ui_text || {});
    populateWorkflowForms(settings.workflow || {});

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
  document.getElementById('hold-pickup-timeout-enabled').checked = !!wf.holdPickupTimeoutEnabled;
  setFieldValue('hold-pickup-timeout-days', wf.holdPickupTimeoutDays !== undefined ? wf.holdPickupTimeoutDays : '14');
  toggleTimeoutGroup();
  toggleHoldPickupTimeoutGroup();
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
  setFieldValue('ui-already-submitted-msg', uiText.alreadySubmittedMessage || 'This suggestion has already been submitted. We only accept one suggestion per title. Check the catalog to see if the material was acquired and place a hold.<div>Thank you for using this library\'s suggestion service.</div>');
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
  renderPatronFormatRulesEditor(uiText.formatRules);
  setPublicationOptions(uiText.publicationOptions);
}

document.getElementById('btn-reset-library-settings').addEventListener('click', async () => {
  if (currentLibraryContextOrgId === 'system') return;
  const confirmed = await showConfirm('Reset Library Settings', 'Are you sure you want to delete this library\'s overrides and revert to system defaults?');
  if (confirmed) {
    await authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify({ orgId: currentLibraryContextOrgId, action: 'reset' })
    });
    showToast('Library settings reset to system defaults', 'success');
    await loadLibrarySettings(currentLibraryContextOrgId);
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

  if (!users.length) {
    bodyEl.innerHTML = '<tr><td colspan="6" class="text-muted">No staff users found.</td></tr>';
    return;
  }

  bodyEl.innerHTML = users.map(user => {
    const id = escapeAttr(user.id || '');
    const username = escapeAttr(user.username || '');
    const domain = escapeAttr(user.domain || '');
    const library = escapeAttr(user.libraryOrgName || user.libraryOrgId || (user.scope === 'system' ? 'System' : 'Unmapped'));
    const displayName = escapeAttr(user.displayName || '');
    const role = ['staff', 'admin', 'super_admin'].includes(String(user.role || '').toLowerCase()) ? String(user.role || '').toLowerCase() : 'staff';
    const superAdminOption = canAssignSuperAdmin
      ? `<option value="super_admin"${role === 'super_admin' ? ' selected' : ''}>Super Admin</option>`
      : '';
    return `
      <tr data-staff-id="${id}">
        <td><strong>${username}</strong></td>
        <td>${domain || '<span class="text-muted">Default</span>'}</td>
        <td>${library}</td>
        <td>${displayName || '<span class="text-muted">No display name</span>'}</td>
        <td>
          <select class="form-control form-control-sm staff-role-select">
            <option value="staff"${role === 'staff' ? ' selected' : ''}>Staff</option>
            <option value="admin"${role === 'admin' ? ' selected' : ''}>Admin</option>
            ${superAdminOption}
          </select>
        </td>
        <td>
          <button type="button" class="btn btn-sm btn-primary staff-role-save">Save Role</button>
        </td>
      </tr>
    `;
  }).join('');
}

const staffUsersTableBody = document.getElementById('staff-users-table-body');
if (staffUsersTableBody) {
  staffUsersTableBody.addEventListener('click', async (e) => {
    const btn = e.target.closest('.staff-role-save');
    if (!btn) return;

    const row = btn.closest('tr[data-staff-id]');
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

function buildSettingsPayload() {
  const smtp = {
    host: getFieldValue('smtp-host'),
    port: parseInt(getFieldValue('smtp-port', '587'), 10) || 587,
    username: getFieldValue('smtp-username'),
    password: getFieldValue('smtp-password'),
    from: getFieldValue('smtp-from'),
    fromName: getFieldValue('smtp-from-name'),
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
    ebookMessage: getFieldValue('ui-ebook-msg'),
    eaudiobookMessage: getFieldValue('ui-eaudiobook-msg'),
    formatLabels: collectFormatLabels(),
    availableFormats: collectAvailableFormats(),
    publicationOptions: normalizePublicationOptions(getFieldValue('ui-publication-options')),
    formatRules: collectPatronFormatRules()
  };

  const emails = {
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
    hold_placed: {
      subject: getFieldValue('email-hold-subject'),
      body: getFieldValue('email-hold-body')
    }
  };

  const payload = {
    smtp, polaris, ui_text: uiText, emails,
    allowedStaffUsers: normalizeAllowedStaffUsers(getFieldValue('allowed-staff-users')),
    suggestionLimit: parseInt(getFieldValue('suggestion-limit', '5'), 10) || 5,
    suggestionLimitMessage: getFieldValue('suggestion-limit-msg'),
    outstandingTimeoutEnabled: getFieldChecked('outstanding-timeout-enabled'),
    outstandingTimeoutDays: parseInt(getFieldValue('outstanding-timeout-days', '30'), 10) || 30,
    holdPickupTimeoutEnabled: getFieldChecked('hold-pickup-timeout-enabled'),
    holdPickupTimeoutDays: parseInt(getFieldValue('hold-pickup-timeout-days', '14'), 10) || 14
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

  buttons.forEach(button => {
    button.disabled = true;
  });
  msg.textContent = options.pendingText || 'Saving...';
  msg.className = 'mt-2 font-weight-bold text-info';

  try {
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();
    
    // Save templates via the new library-scoped API
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      emails: payload.emails,
      ui_text: payload.ui_text,
      workflow: {
        suggestionLimit: payload.suggestionLimit,
        suggestionLimitMessage: payload.suggestionLimitMessage,
        outstandingTimeoutEnabled: payload.outstandingTimeoutEnabled,
        outstandingTimeoutDays: payload.outstandingTimeoutDays,
        holdPickupTimeoutEnabled: payload.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: payload.holdPickupTimeoutDays
      }
    };

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify(libraryPayload)
    });

    let globalPromise = Promise.resolve();
    if (isSuper) {
      if (currentLibraryContextOrgId !== 'system') {
        delete payload.emails;
        delete payload.ui_text;
        delete payload.suggestionLimit;
        delete payload.suggestionLimitMessage;
        delete payload.outstandingTimeoutEnabled;
        delete payload.outstandingTimeoutDays;
        delete payload.holdPickupTimeoutEnabled;
        delete payload.holdPickupTimeoutDays;
      }
      globalPromise = pb.collection('app_settings').update(SETTINGS_RECORD_ID, payload);
    }

    await Promise.all([globalPromise, libraryPromise]);
    msg.textContent = options.successText || 'Settings saved successfully!';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    await loadSettings({ showErrors: false }); // Sync internal state (also triggers loadLibrarySettings)
    await loadStaffConfig(); // Refresh logo and titles immediately after saving
    loadStaffUsers();
    return true;
  } catch (err) {
    console.error(err);
    msg.textContent = 'Failed to save settings.';
    msg.className = 'mb-3 font-weight-bold text-danger';
    return false;
  } finally {
    buttons.forEach(button => {
      button.disabled = false;
    });
  }
}

settingsForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  await saveSettings();
});

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

document.getElementById('outstanding-timeout-enabled').addEventListener('change', toggleTimeoutGroup);

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

document.getElementById('hold-pickup-timeout-enabled').addEventListener('change', toggleHoldPickupTimeoutGroup);

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
      <table class="table table-sm table-borderless mb-0">
        <thead>
          <tr>
            <th style="width: 28px;"></th>
            <th style="width: 40px;">Show</th>
            <th>Format Key</th>
            <th>Display Label</th>
            <th style="width: 40px;"></th>
          </tr>
        </thead>
        <tbody id="format-settings-body">
          ${allKeys.map(key => `
            <tr class="format-setting-row" data-key="${escapeAttr(key)}" draggable="true" style="cursor: grab;">
              <td class="align-middle text-muted" style="font-size: 1.1rem; cursor: grab; user-select: none;">&#8597;</td>
              <td class="text-center align-middle">
                <input type="checkbox" class="format-enabled-check" ${availableFormats.includes(key) ? 'checked' : ''}>
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
      draggingRow.style.opacity = '0.4';
      e.dataTransfer.effectAllowed = 'move';
    }
  });

  tbody.addEventListener('dragend', () => {
    if (draggingRow) {
      draggingRow.style.opacity = '';
      draggingRow = null;
    }
    // Remove all drag-over highlights
    tbody.querySelectorAll('tr').forEach(r => r.style.borderTop = '');
  });

  tbody.addEventListener('dragover', (e) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const target = e.target.closest('tr');
    if (target && target !== draggingRow) {
      tbody.querySelectorAll('tr').forEach(r => r.style.borderTop = '');
      target.style.borderTop = '2px solid #007bff';
    }
  });

  tbody.addEventListener('drop', (e) => {
    e.preventDefault();
    const target = e.target.closest('tr');
    tbody.querySelectorAll('tr').forEach(r => r.style.borderTop = '');
    if (target && draggingRow && target !== draggingRow) {
      tbody.insertBefore(draggingRow, target);
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
    
    // Populate with ALL known formats (even disabled ones, to support legacy records)
    select.innerHTML = Object.keys(formatMap).map(k => `
      <option value="${escapeAttr(k)}">${escapeAttr(formatMap[k] || k)}</option>
    `).join('');
    
    select.value = val;
  });
}

const btnAddFormat = document.getElementById('btn-add-format');
if (btnAddFormat) {
  btnAddFormat.addEventListener('click', async () => {
    const name = prompt('Enter a short, unique name for the new format (e.g. "videogame"):');
    if (!name) return;
    const key = name.toLowerCase().replace(/[^a-z0-9_]/g, '_');
    if (formatMap[key]) {
      await showAlert('This format key already exists.');
      return;
    }
    const label = name.charAt(0).toUpperCase() + name.slice(1);
    formatMap[key] = label;
    availableFormats.push(key);
    renderFormatSettings();
    renderPatronFormatRulesEditor(collectPatronFormatRules());
  });
}

const formatSettingsContainer = document.getElementById('format-settings-container');
if (formatSettingsContainer) {
  formatSettingsContainer.addEventListener('click', async (e) => {
    if (e.target.classList.contains('btn-remove-format')) {
      const row = e.target.closest('tr');
      const key = row.getAttribute('data-key');
      if (await showConfirm(`Remove format "${key}"? This will only remove it from the settings list. Existing requests with this format will remain in the database.`)) {
        delete formatMap[key];
        availableFormats = availableFormats.filter(k => k !== key);
        renderFormatSettings();
        renderPatronFormatRulesEditor(collectPatronFormatRules());
      }
    }
  });
}
