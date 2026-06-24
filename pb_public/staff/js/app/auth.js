import { pb, loginContainer, setupContainer, appContainer, bootstrapAdminMessage, setBootstrapAdminMessage, setupRequired, setSetupRequired, currentEmailStatus, setCurrentEmailStatus, currentStatus, setCurrentStatus, setCurrentClaimFilter, claimFilterSelect } from '../state.js';
import { loadTab, renderCurrentGrid, closeActionMenu } from '../grid.js';
import { closeOpenDialogs } from '../dialogs.js';
import { authorizedJson } from '../http.js';
import { requestJson } from '../../../shared/http.js';
import { loadSettings } from '../settings.js';
import { renderRecentSuggestionsSwitcher } from '../recent-suggestions.js';
import { setText, setVisible, setFieldChecked, setFieldValue } from './dom.js';
import { requestedStatusFromUrl, updateStageQuery } from './url-utils.js';
import { getSettingsSectionFromHash, activateStatusTab, updateSettingsSaveBarVisibility } from './nav.js';

export function staffRole() {
  return pb.authStore.model ? String(pb.authStore.model.role || '').toLowerCase() : '';
}

export function isSuperAdminStaff() {
  return staffRole() === 'super_admin';
}

export function isAdminStaff() {
  return ['admin', 'super_admin'].includes(staffRole());
}

export function showBootstrapAdminMessage() {
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

export function updateEmailStatusBanner(status) {
  setCurrentEmailStatus(status || currentEmailStatus || { enabled: true });
  const smtpMessage = document.getElementById('smtp-readiness-message');
  const configured = !!currentEmailStatus.enabled;
  const message = currentEmailStatus.message || 'Email notifications are not configured. Suggestions and staff workflows still work, but patron emails will not be sent.';

  setVisible('email-status-banner', !configured);
  if (smtpMessage) {
    smtpMessage.textContent = message;
    smtpMessage.className = configured ? 'alert alert-success small' : 'alert alert-warning small';
  }
}

export async function loadEmailStatus(orgId) {
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

export function checkAuth() {
  if (pb.authStore.isValid && pb.authStore.model && pb.authStore.model.collectionName === 'staff_users') {
    setupContainer.classList.add('hidden');
    loginContainer.classList.add('hidden');
    appContainer.classList.remove('hidden');
    const libraryName = pb.authStore.model.libraryOrgName || (isSuperAdminStaff() ? 'System' : '');
    const identityLabel = pb.authStore.model.identityKey || pb.authStore.model.username;
    document.getElementById('display-user').textContent = (pb.authStore.model.displayName || identityLabel) + (libraryName ? ` (${libraryName})` : '');
    const isAdmin = isAdminStaff();
    setVisible('nav-settings', isAdmin);
    showBootstrapAdminMessage();
    loadEmailStatus();
    applyProfileClaimFilterDefault();
    renderRecentSuggestionsSwitcher();

    const requestedSettingsSection = getSettingsSectionFromHash();
    const requestedStatus = requestedStatusFromUrl();
    if (requestedSettingsSection && (!requestedStatus || requestedStatus === 'settings')) {
      activateStatusTab('settings');
      updateStageQuery('settings');
    } else {
      updateSettingsSaveBarVisibility();
      if (requestedSettingsSection) {
        updateStageQuery(currentStatus);
      }
    }

    if (isAdmin && currentStatus !== 'settings') {
      loadSettings({ showErrors: false });
    }

    loadTab(currentStatus);
  } else {
    closeOpenDialogs();
    closeActionMenu?.();
    setupContainer.classList.toggle('hidden', !setupRequired);
    loginContainer.classList.toggle('hidden', setupRequired);
    appContainer.classList.add('hidden');
    setBootstrapAdminMessage('');
    showBootstrapAdminMessage();
  }
}

export async function loadSetupStatus() {
  try {
    const status = await requestJson('/api/asap/setup/status?t=' + Date.now());
    setSetupRequired(!!(status && status.setupRequired));
  } catch (err) {
    console.error('Failed to load setup status', err);
  }
}

let appliedProfileClaimFilterDefaultForStaffId = '';

function profileDefaultClaimFilter(model = pb.authStore.model) {
  return model && model.default_mine_unclaimed_filter ? 'mine_unclaimed' : 'all';
}

export function applyProfileClaimFilterDefault(options = {}) {
  const model = pb.authStore.model || {};
  const staffId = String(model.id || '').trim();
  if (!staffId) return;
  if (!options.force && appliedProfileClaimFilterDefaultForStaffId === staffId) return;
  setCurrentClaimFilter(profileDefaultClaimFilter(model));
  if (claimFilterSelect) {
    claimFilterSelect.value = profileDefaultClaimFilter(model);
  }
  appliedProfileClaimFilterDefaultForStaffId = staffId;
}

export function clearAppliedProfileClaimFilterDefault() {
  appliedProfileClaimFilterDefaultForStaffId = '';
}

export function openProfileDialog() {
  closeOpenDialogs();
  const dialog = document.getElementById('profile-dialog');
  if (!dialog) return;
  const msg = document.getElementById('profile-msg');
  if (msg) {
    msg.textContent = '';
    msg.className = 'mb-3 font-weight-bold';
  }
  setFieldChecked('profile-weekly-action-summary', !!(pb.authStore.model && pb.authStore.model.weekly_action_summary_enabled));
  setFieldChecked('profile-purchase-reminder-default', !!(pb.authStore.model && pb.authStore.model.purchase_reminder_default));
  setFieldChecked('profile-additional-copy-reminder-default', !!(pb.authStore.model && pb.authStore.model.additional_copy_reminder_default));
  setFieldChecked('profile-default-mine-unclaimed-filter', !!(pb.authStore.model && pb.authStore.model.default_mine_unclaimed_filter));

  setFieldValue('profile-weekly-action-summary-email', (pb.authStore.model && pb.authStore.model.weekly_action_summary_email) || '');
  dialog.showModal();
}
