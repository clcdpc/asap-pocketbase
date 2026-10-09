import { authorizedJson, isAbortError, loadStaffSession } from './http.js';
import { unconfirmedResponseError } from './mutation-outcome.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';

export function createProfileController({ root, sessionIdentity, announce,
  onPreferences, onSessionLost, onAccessUnavailable, onReceipt, clearReceipt,
  request = authorizedJson, loadSession = loadStaffSession }) {
  const dom = {
    profile: root.querySelector('#profile-form'),
    profileRefresh: root.querySelector('#profile-refresh'),
    notificationEmail: root.querySelector('#notification-email'),
    weeklyEmail: root.querySelector('#weekly-email'),
    weeklyEnabled: root.querySelector('#weekly-enabled'),
    purchaseDefault: root.querySelector('#purchase-default'),
    additionalCopyDefault: root.querySelector('#additional-copy-default'),
    mineDefault: root.querySelector('#mine-default'),
  };
  const state = { profileBaseline: null, profileMutation: null };
  const events = new window.AbortController();
  const reads = createLatestLoad();
  const drafts = createDraftScope();
  const draft = drafts.register({ root: dom.profile, isDirty: hasProfileDraft });
  let disposed = false;
  const isPresentationCurrent = owner => !disposed && sessionIdentity.isCurrent(owner);

  async function readSession() {
    const load = reads.begin('session');
    try {
      const result = await loadSession({ signal: load.signal });
      load.signal.throwIfAborted();
      return result;
    } finally {
      reads.finish('session', load.token);
    }
  }

  function profileValues() {
    return {
      notificationEmail: dom.notificationEmail.value,
      weeklyActionSummaryEmail: dom.weeklyEmail.value,
      weeklyActionSummaryEnabled: dom.weeklyEnabled.checked,
      purchaseReminderDefault: dom.purchaseDefault.checked,
      additionalCopyReminderDefault: dom.additionalCopyDefault.checked,
      defaultMineUnclaimedFilter: dom.mineDefault.checked
    };
  }

  function hasProfileDraft() {
    if (!sessionIdentity.preferences() || !state.profileBaseline) return false;
    const current = profileValues();
    return Object.keys(current).some(key => current[key] !== state.profileBaseline[key]);
  }

  function populateProfile(staff) {
    if (disposed) return;
    dom.notificationEmail.value = staff?.notificationEmail || '';
    dom.weeklyEmail.value = staff?.weeklyActionSummaryEmail || '';
    dom.weeklyEnabled.checked = Boolean(staff?.weeklyActionSummaryEnabled);
    dom.purchaseDefault.checked = Boolean(staff?.purchaseReminderDefault);
    dom.additionalCopyDefault.checked = Boolean(staff?.additionalCopyReminderDefault);
    dom.mineDefault.checked = Boolean(staff?.defaultMineUnclaimedFilter);
    state.profileBaseline = staff ? profileValues() : null;
    updateProfileControls();
  }

  function updateProfileControls() {
    if (disposed) return;
    const mutation = state.profileMutation;
    for (const control of dom.profile.querySelectorAll('input, button')) {
      control.disabled = Boolean(mutation);
    }
    dom.profile.setAttribute('aria-busy', String(Boolean(mutation?.pending)));
    dom.profileRefresh.hidden = !mutation?.outcomeUnconfirmed;
    dom.profileRefresh.disabled = Boolean(mutation?.pending);
  }

  async function refreshUnconfirmedProfile() {
    if (disposed) return;
    const mutation = state.profileMutation;
    if (!mutation?.outcomeUnconfirmed || mutation.pending || !isPresentationCurrent(mutation.owner)) return;
    if (hasProfileDraft() && !window.confirm('Discard the Profile draft and reload current saved preferences to review the uncertain save?')) return;
    mutation.pending = true;
    updateProfileControls();
    try {
      const session = await readSession();
      if (state.profileMutation !== mutation || !isPresentationCurrent(mutation.owner)) return;
      if (!session.authenticated) { onSessionLost(); return; }
      if (session.accessAllowed === false) { onAccessUnavailable(); return; }
      if (!onPreferences(session.staff, mutation.owner)) return;
      state.profileMutation = null;
      populateProfile(session.staff);
      announce('Current Profile loaded. Review the saved preferences before making another change.', 'warning');
    } catch (error) {
      if (isPresentationCurrent(mutation.owner) && state.profileMutation === mutation && error.status !== 401) {
        announce('Profile could not refresh. Review current saved preferences before retrying.', 'error');
      }
    } finally {
      if (state.profileMutation === mutation) mutation.pending = false;
      updateProfileControls();
    }
  }

  async function saveProfile(event) {
    event.preventDefault();
    const owner = sessionIdentity.preferences();
    if (disposed || !owner || state.profileMutation || !drafts.admit({ consumes: draft }).allowed) return;
    const mutation = { owner, pending: true, outcomeUnconfirmed: false };
    state.profileMutation = mutation;
    updateProfileControls();
    announce('Saving profile...');
    try {
      const result = await request('/api/asap/staff/profile', {
        method: 'POST',
        body: {
          version: owner.version,
          weeklyActionSummaryEnabled: dom.weeklyEnabled.checked,
          weeklyActionSummaryEmail: dom.weeklyEmail.value.trim() || null,
          purchaseReminderDefault: dom.purchaseDefault.checked,
          additionalCopyReminderDefault: dom.additionalCopyDefault.checked,
          defaultMineUnclaimedFilter: dom.mineDefault.checked
        }
      });
      if (!result?.staff || String(result.staff.id) !== String(owner.id) ||
          result.staff.tenantId !== owner.tenantId || !result.staff.version) {
        throw unconfirmedResponseError();
      }
      const committedMessage = 'Profile saved. Sign in again to review the saved profile.';
      mutation.outcome = 'committed';
      mutation.message = committedMessage;
      onReceipt(committedMessage, owner, mutation);
      if (!isPresentationCurrent(owner)) return;
      let session;
      try {
        session = await readSession();
      } catch {
        if (isPresentationCurrent(owner)) onSessionLost(committedMessage);
        return;
      }
      if (!isPresentationCurrent(owner)) return;
      if (!session.authenticated || session.accessAllowed === false) {
        onSessionLost(committedMessage);
        return;
      }
      // The session read supplies the latest rowversion and preferences, including concurrent changes.
      if (!onPreferences(session.staff, owner)) return;
      clearReceipt(mutation);
      populateProfile(sessionIdentity.preferences());
      announce('Profile saved.', 'success');
    } catch (error) {
      if (mutation.outcome === 'committed') {
        if (isPresentationCurrent(owner)) announce('Profile saved. Current preferences could not be refreshed.', 'warning');
        return;
      }
      const unconfirmed = error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error);
      mutation.outcome = unconfirmed ? 'uncertain' : 'rejected';
      const conflictMessage = 'Profile could not be saved. Sign in again to review your current profile.';
      if (error.status === 409) onReceipt(conflictMessage, owner, mutation);
      if (!isPresentationCurrent(owner)) {
        if (unconfirmed) onReceipt('Profile save could not be confirmed. Sign in again to review current preferences.', owner, mutation);
        return;
      }
      if (error.status === 409) {
        let session;
        try {
          session = await readSession();
        } catch {
          if (isPresentationCurrent(owner)) onSessionLost(conflictMessage);
          return;
        }
        if (!isPresentationCurrent(owner)) return;
        if (session.accessAllowed === false) {
          onAccessUnavailable();
          return;
        }
        if (!session.authenticated) {
          onSessionLost(conflictMessage);
          return;
        }
        if (!onPreferences(session.staff, owner)) return;
        clearReceipt(mutation);
        populateProfile(session.staff);
      }
      if (unconfirmed) {
        mutation.outcomeUnconfirmed = true;
        announce('Profile save could not be confirmed. Reload Profile to review current saved preferences before retrying.', 'warning');
      } else if (error.status !== 401) {
        announce(error.message || 'Profile could not be saved.', 'error');
      }
    } finally {
      if (state.profileMutation === mutation) {
        mutation.pending = false;
        if (!mutation.outcomeUnconfirmed) state.profileMutation = null;
      }
      updateProfileControls();
    }
  }

  dom.profile.addEventListener('submit', saveProfile, { signal: events.signal });
  for (const name of ['input', 'change']) dom.profile.addEventListener(name, () => drafts.touch(), { signal: events.signal });
  dom.profileRefresh.addEventListener('click', refreshUnconfirmedProfile, { signal: events.signal });
  return {
    activate() { updateProfileControls(); },
    deactivate() {},
    setStaff: populateProfile,
    preferencesChanged(staff) { if (sessionIdentity.isCurrent(staff) && !state.profileMutation && !hasProfileDraft()) populateProfile(staff); },
    isDirty: hasProfileDraft,
    hasPendingMutation: () => Boolean(state.profileMutation),
    inspectDeparture() {
      return { owner: drafts, dirty: hasProfileDraft(), blocked: disposed || Boolean(state.profileMutation),
        stamp: JSON.stringify({ revision: drafts.revision(), values: profileValues() }),
        confirmMessage: 'Discard unsaved Profile changes and navigate away?',
        message: state.profileMutation?.outcomeUnconfirmed
          ? 'Reload Profile to review the uncertain save before navigating away.'
          : 'Wait for the Profile save to finish before navigating away.' };
    },
    discardDraft() { populateProfile(sessionIdentity.preferences()); },
    signedOut() { reads.begin('session').abort(); state.profileMutation = null; populateProfile(null); },
    save: saveProfile,
    review: refreshUnconfirmedProfile,
    dispose() { disposed = true; events.abort(); reads.begin('session').abort(); drafts.dispose(); state.profileBaseline = null; }
  };
}
