import { pb, loginForm, setupForm, logoutBtn, profileBtn, grid, gridSearchInput, tagFilterSelect, claimFilterSelect, similarRequestFilterSelect, additionalCopyStatusFilterSelect, closedTypeFilterSelect, currentStatus, setCurrentStatus, setActiveTagFilter, setGridSearchKeyword, setCurrentClaimFilter, setCurrentSimilarRequestFilter, setCurrentAdditionalCopyStatus, setCurrentClosedTypeFilter, setBootstrapAdminMessage, setSetupRequired, setCurrentSettingsSection, setOrganizationsStatus } from '../state.js';
import { loadTab, renderCurrentGrid } from '../grid.js';
import { showToast } from '../dialogs.js';
import { authorizedJson } from '../http.js';
import { requestJson } from '../../../shared/http.js';
import { syncPolarisOrganizations } from '../settings-polaris.js';
import { initRecentSuggestionsDropdown } from '../recent-suggestions.js';
import { formDataObject, setFieldChecked, setFieldValue, getFieldChecked, getFieldValue } from './dom.js';
import { checkAuth, openProfileDialog, applyProfileClaimFilterDefault, clearAppliedProfileClaimFilterDefault } from './auth.js';
import { postPolarisTest } from './misc.js';
import { activateStatusTab } from './nav.js';
import { updateStageQuery as updateStageQueryFromUrl } from './url-utils.js';

loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const errDiv = document.getElementById('login-error');
  errDiv.classList.add('hidden');
  try {
    const body = formDataObject(loginForm);
    const res = await requestJson('/api/asap/staff/login', {
      method: 'POST',
      body
    });
    const meta = res.meta || res;
    setBootstrapAdminMessage(meta.bootstrapAdmin
      ? (meta.bootstrapMessage || 'This is the first staff login, so your account has been made the admin user. Future staff logins will be created with non-admin staff roles.')
      : '');
    pb.authStore.save(res.token, res.record);
    checkAuth();
  } catch (err) {
    if (err.status === 409) {
      setSetupRequired(true);
      checkAuth();
      return;
    }
    errDiv.textContent = err.message !== 'invalid' ? err.message : 'Invalid login';
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
    const body = formDataObject(setupForm);
    const result = await requestJson('/api/asap/setup', {
      method: 'POST',
      body
    });

    setSetupRequired(false);
    setBootstrapAdminMessage(result.bootstrapMessage || 'Initial setup is complete. Your account is the admin user; future staff logins will be non-admin staff accounts.');
    pb.authStore.save(result.token, result.record);
    setCurrentStatus('settings');
    setCurrentSettingsSection('start');
    window.history.replaceState(null, '', '#settings-start');
    setOrganizationsStatus('loading', 'Organizations loading from Polaris. Settings will unlock organization selection after this sync completes.');
    checkAuth();
    syncPolarisOrganizations().catch(() => {
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
  clearAppliedProfileClaimFilterDefault();
  setCurrentClaimFilter('all');
  document.getElementById('login-form').reset();
  document.getElementById('login-password').value = '';
  checkAuth();
});

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
      const summaryEnabled = getFieldChecked('profile-weekly-action-summary');
      const reminderDefault = getFieldChecked('profile-purchase-reminder-default');
      const additionalCopyReminderDefault = getFieldChecked('profile-additional-copy-reminder-default');
      const mineUnclaimedDefault = getFieldChecked('profile-default-mine-unclaimed-filter');
      const email = getFieldValue('profile-weekly-action-summary-email').trim();
      if (summaryEnabled && !email) {
        throw new Error('Enter a staff email address before enabling the weekly summary.');
      }
      const updated = await authorizedJson('/api/asap/staff/profile', {
        method: 'POST',
        body: {
          weekly_action_summary_enabled: summaryEnabled,
          purchase_reminder_default: reminderDefault,
          additional_copy_reminder_default: additionalCopyReminderDefault,
          default_mine_unclaimed_filter: mineUnclaimedDefault,
          weekly_action_summary_email: email
        }
      });

      pb.authStore.save(pb.authStore.token, Object.assign({}, pb.authStore.model || {}, updated));
      applyProfileClaimFilterDefault({ force: true });
      if (!['settings', 'analytics'].includes(currentStatus)) {
        renderCurrentGrid(currentStatus);
      }
      if (msg) {
        msg.textContent = 'Profile preferences saved.';
        msg.className = 'mb-3 font-weight-bold text-success';
      }
      showToast('Profile preferences saved.', 'success');
      setTimeout(() => {
        const dialog = document.getElementById('profile-dialog');
        if (dialog && dialog.open) dialog.close();
      }, 700);
    } catch (err) {
      if (msg) {
        msg.textContent = err.message || 'Could not save your profile preferences. Please try again.';
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
    updateStageQueryFromUrl(nextStatus);
    setActiveTagFilter('');
    setGridSearchKeyword('');
    if (gridSearchInput) gridSearchInput.value = '';
    loadTab(currentStatus);
  });
});

if (gridSearchInput) {
  gridSearchInput.addEventListener('input', event => {
    const keyword = event.target.value;
    setGridSearchKeyword(keyword);
    if (grid) {
      grid.updateConfig({
        search: {
          ...grid.config.search,
          keyword: keyword
        }
      }).forceRender();
    }
  });
}

if (tagFilterSelect) {
  tagFilterSelect.addEventListener('change', event => {
    setActiveTagFilter(event.target.value || '');
    renderCurrentGrid(currentStatus);
  });
}

if (similarRequestFilterSelect) {
  similarRequestFilterSelect.addEventListener('change', event => {
    setCurrentSimilarRequestFilter(event.target.value || 'all');
    renderCurrentGrid(currentStatus);
  });
}

if (claimFilterSelect) {
  claimFilterSelect.addEventListener('change', event => {
    setCurrentClaimFilter(event.target.value || 'all');
    renderCurrentGrid(currentStatus);
  });
}

if (additionalCopyStatusFilterSelect) {
  additionalCopyStatusFilterSelect.addEventListener('change', event => {
    setCurrentAdditionalCopyStatus(event.target.value || 'open');
    loadTab(currentStatus);
  });
}

if (closedTypeFilterSelect) {
  closedTypeFilterSelect.addEventListener('change', event => {
    setCurrentClosedTypeFilter(event.target.value || 'all');
    loadTab(currentStatus);
  });
}

initRecentSuggestionsDropdown();
