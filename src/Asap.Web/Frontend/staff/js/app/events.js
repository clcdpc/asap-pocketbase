import { staffSession, staffAccessGeneration, staffSessionEpoch, setStaffSession, loginForm, logoutBtn, loginSignOutBtn, profileBtn, gridSearchInput, tagFilterSelect, claimFilterSelect, similarRequestFilterSelect, additionalCopyStatusFilterSelect, closedTypeFilterSelect, currentStatus, setCurrentStatus, setActiveTagFilter, setGridSearchKeyword, setCurrentClaimFilter, setCurrentSimilarRequestFilter, setCurrentAdditionalCopyStatus, setCurrentClosedTypeFilter } from '../state.js';
import { loadTab, renderCurrentGrid } from '../grid.js';
import { showToast } from '../dialogs.js';
import { authorizedJson, isAbortError, loadStaffSession } from '../http.js';
import { initRecentSuggestionsDropdown } from '../recent-suggestions.js';
import { getFieldChecked, getFieldValue } from './dom.js';
import { checkAuth, openProfileDialog, currentProfileDialogGeneration, applyProfileClaimFilterDefault, clearAppliedProfileClaimFilterDefault } from './auth.js';
import { postPolarisTest } from './misc.js';
import { activateStatusTab } from './nav.js';
import { updateStageQuery as updateStageQueryFromUrl } from './url-utils.js';

loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const returnUrl = window.location.pathname + window.location.search + window.location.hash;
  window.location.assign(`/api/asap/staff/sign-in?returnUrl=${encodeURIComponent(returnUrl)}`);
});

async function signOut(e) {
  e.preventDefault();
  setStaffSession({ authenticated: false, antiforgeryToken: staffSession.antiforgeryToken });
  const signOutEpoch = staffSessionEpoch;
  clearAppliedProfileClaimFilterDefault();
  setCurrentClaimFilter('all');
  checkAuth();
  try {
    let token = staffSession.antiforgeryToken;
    if (!token) {
      const session = await loadStaffSession({ apply: false });
      if (staffSessionEpoch !== signOutEpoch) return;
      token = session.antiforgeryToken;
    }
    if (!token) throw new Error('The sign-out request token could not be loaded.');
    await authorizedJson('/api/asap/staff/sign-out', {
      method: 'POST',
      headers: { 'X-ASAP-Antiforgery': token }
    });
  } catch (error) {
    if (isAbortError(error) || staffSessionEpoch !== signOutEpoch) return;
    throw error;
  }
}

logoutBtn.addEventListener('click', signOut);
loginSignOutBtn?.addEventListener('click', signOut);

window.addEventListener('asap:session-invalid', checkAuth);
window.addEventListener('asap:access-forbidden', (event) => {
  if (event.detail?.accessAllowed === false) {
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
let profileSaveGeneration = 0;
if (profileForm) {
  profileForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const saveGeneration = ++profileSaveGeneration;
    const accessGeneration = staffAccessGeneration;
    const staffId = staffSession.staff?.id;
    const dialogGeneration = currentProfileDialogGeneration();
    const ownsProfile = () => saveGeneration === profileSaveGeneration &&
      accessGeneration === staffAccessGeneration && staffId === staffSession.staff?.id &&
      dialogGeneration === currentProfileDialogGeneration() &&
      staffSession.authenticated && staffSession.accessAllowed;
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
      const version = staffSession.staff?.version;
      const updated = await authorizedJson('/api/asap/staff/legacy/profile', {
        method: 'POST',
        body: {
          version,
          weeklyActionSummaryEnabled: summaryEnabled,
          purchaseReminderDefault: reminderDefault,
          additionalCopyReminderDefault,
          defaultMineUnclaimedFilter: mineUnclaimedDefault,
          weeklyActionSummaryEmail: email
        }
      });

      if (!ownsProfile()) return;

      setStaffSession({ ...staffSession, staff: updated.staff });
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
        if (!ownsProfile()) return;
        const dialog = document.getElementById('profile-dialog');
        if (dialog && dialog.open) dialog.close();
      }, 700);
    } catch (err) {
      if (msg && ownsProfile()) {
        msg.textContent = err.message || 'Could not save your profile preferences. Please try again.';
        msg.className = 'mb-3 font-weight-bold text-danger';
      }
    } finally {
      if (saveBtn && ownsProfile()) saveBtn.disabled = false;
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
