import { staffSession, loginContainer, appContainer, currentEmailStatus, setCurrentEmailStatus, currentStatus, setCurrentStatus, setCurrentClaimFilter, claimFilterSelect } from '../state.js';
import { loadTab, renderCurrentGrid, closeActionMenu } from '../grid.js';
import { closeOpenDialogs } from '../dialogs.js';
import { authorizedJson } from '../http.js';
import { requestJson } from '../../../shared/http.js';
import { loadSettings } from '../settings.js';
import { renderRecentSuggestionsSwitcher } from '../recent-suggestions.js';
import { setText, setVisible, setFieldChecked, setFieldValue } from './dom.js';
import { requestedStatusFromUrl, updateStageQuery } from './url-utils.js';
import { getSettingsSectionFromHash, activateStatusTab, updateSettingsSaveBarVisibility } from './nav.js';
import { createLatestLoad } from '../../../shared/latest-load.js';
import { isAbortError } from '../../../shared/http.js';

const emailStatusLoads = createLatestLoad();

export function staffRole() {
  return staffSession.staff ? String(staffSession.staff.role || '').toLowerCase() : '';
}

export function isSuperAdminStaff() {
  return staffRole() === 'super_admin';
}

export function isAdminStaff() {
  return ['admin', 'super_admin'].includes(staffRole());
}

export function updateEmailStatusBanner(status) {
  setCurrentEmailStatus(status || currentEmailStatus || { enabled: false });
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
  if (!staffSession.authenticated || !staffSession.accessAllowed || !staffSession.staff) {
    return;
  }
  const guard = emailStatusLoads.begin('email-status');
  const staff = staffSession.staff;
  updateEmailStatusBanner({ enabled: false, message: 'Checking Postmark configuration…' });
  try {
    const status = await authorizedJson(`/api/asap/staff/email-status${orgId ? `?orgId=${encodeURIComponent(orgId)}` : ''}`, { signal: guard.signal });
    if (guard.isCurrent() && staff === staffSession.staff && staffSession.authenticated) {
      updateEmailStatusBanner(status);
    }
  } catch (error) {
    if (!isAbortError(error) && guard.isCurrent()) {
      updateEmailStatusBanner({ enabled: false, message: 'Postmark configuration status could not be loaded.' });
    }
  } finally {
    emailStatusLoads.finish('email-status', guard.token);
  }
}

export function checkAuth() {
  if (staffSession.authenticated && staffSession.accessAllowed && staffSession.staff) {
    loginContainer.classList.add('hidden');
    appContainer.classList.remove('hidden');
    setText('login-status', '');
    setVisible('login-status', false);
    setVisible('login-sign-out-btn', false);
    const libraryName = staffSession.staff.libraryOrgName || (isSuperAdminStaff() ? 'System' : '');
    const identityLabel = staffSession.staff.identityKey || staffSession.staff.username;
    document.getElementById('display-user').textContent = (staffSession.staff.displayName || identityLabel) + (libraryName ? ` (${libraryName})` : '');
    const isAdmin = isAdminStaff();
    setVisible('nav-settings', isAdmin);
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
      loadSettings({ showErrors: false, preserveDraft: true });
    }

    loadTab(currentStatus);
  } else {
    closeOpenDialogs();
    closeActionMenu?.();
    loginContainer.classList.remove('hidden');
    appContainer.classList.add('hidden');
    const accessUnavailable = staffSession.authenticated && !staffSession.accessAllowed;
    const sessionEnded = staffSession.code === 'staff_session_invalid';
    const revalidationRequired = staffSession.code === 'staff_session_revalidation_required';
    const status = revalidationRequired
      ? 'The Staff Access change was saved, but this browser session could not be safely refreshed. Sign in again to revalidate your session before continuing.'
      : accessUnavailable
      ? 'Your Microsoft account is signed in, but staff access is not currently available. Your account or library access may have changed. Contact an administrator or sign out and try another account.'
      : sessionEnded
        ? 'Your staff session ended because the account is no longer valid for this application. Sign in again to continue.'
        : '';
    setText('login-status', status);
    setVisible('login-status', !!status);
    setVisible('login-sign-out-btn', accessUnavailable || revalidationRequired);
  }
}

let appliedProfileClaimFilterDefaultForStaffId = '';

function profileDefaultClaimFilter(model = staffSession.staff) {
  return model && model.default_mine_unclaimed_filter ? 'mine_unclaimed' : 'all';
}

export function applyProfileClaimFilterDefault(options = {}) {
  const model = staffSession.staff || {};
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
  setFieldChecked('profile-weekly-action-summary', !!staffSession.staff?.weekly_action_summary_enabled);
  setFieldChecked('profile-purchase-reminder-default', !!staffSession.staff?.purchase_reminder_default);
  setFieldChecked('profile-additional-copy-reminder-default', !!staffSession.staff?.additional_copy_reminder_default);
  setFieldChecked('profile-default-mine-unclaimed-filter', !!staffSession.staff?.default_mine_unclaimed_filter);

  setFieldValue('profile-weekly-action-summary-email', staffSession.staff?.weekly_action_summary_email || '');
  dialog.showModal();
}
