import { staffSession, setStaffSession, normalizeSessionStaff, loginForm, setupForm, logoutBtn, profileBtn, gridSearchInput, tagFilterSelect, claimFilterSelect, similarRequestFilterSelect, additionalCopyStatusFilterSelect, closedTypeFilterSelect, currentStatus, setCurrentStatus, setActiveTagFilter, setGridSearchKeyword, setCurrentClaimFilter, setCurrentSimilarRequestFilter, setCurrentAdditionalCopyStatus, setCurrentClosedTypeFilter } from '../state.js';
import { loadTab, renderCurrentGrid } from '../grid.js';
import { showToast } from '../dialogs.js';
import { authorizedJson } from '../http.js';
import { initRecentSuggestionsDropdown } from '../recent-suggestions.js';
import { getFieldChecked, getFieldValue } from './dom.js';
import { checkAuth, openProfileDialog, applyProfileClaimFilterDefault, clearAppliedProfileClaimFilterDefault } from './auth.js';
import { postPolarisTest } from './misc.js';
import { activateStatusTab } from './nav.js';
import { updateStageQuery as updateStageQueryFromUrl } from './url-utils.js';

loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const returnUrl = window.location.pathname + window.location.search + window.location.hash;
  window.location.assign(`/api/asap/staff/sign-in?returnUrl=${encodeURIComponent(returnUrl)}`);
});

setupForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const errDiv = document.getElementById('setup-error');
  errDiv.textContent = 'Initial setup is managed by the ASP.NET Core deployment configuration.';
  errDiv.classList.remove('hidden');
});

const setupTestPolarisBtn = document.getElementById('setup-test-polaris-btn');
if (setupTestPolarisBtn) {
  setupTestPolarisBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    const result = document.getElementById('setup-polaris-test-result');
    result.textContent = 'Initial setup is managed by the ASP.NET Core deployment configuration.';
  });
}

logoutBtn.addEventListener('click', async (e) => {
  e.preventDefault();
  try {
    await authorizedJson('/api/asap/staff/sign-out', { method: 'POST' });
  } finally {
    setStaffSession({ authenticated: false, antiforgeryToken: staffSession.antiforgeryToken });
    clearAppliedProfileClaimFilterDefault();
    setCurrentClaimFilter('all');
    checkAuth();
  }
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
      const updated = await authorizedJson('/api/asap/staff/profile', {
        method: 'POST',
        body: {
          weeklyActionSummaryEnabled: summaryEnabled,
          purchaseReminderDefault: reminderDefault,
          additionalCopyReminderDefault,
          defaultMineUnclaimedFilter: mineUnclaimedDefault,
          weeklyActionSummaryEmail: email
        }
      });

      staffSession.staff = normalizeSessionStaff(updated.staff);
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
    renderCurrentGrid(currentStatus);
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
