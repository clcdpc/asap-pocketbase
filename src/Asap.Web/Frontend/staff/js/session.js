import { authorizedJson, isAbortError, loadStaffSession, onAccessUnavailable, onSessionInvalid } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';

export function createSessionCoordinator({ identity, shell, navigation, getFeatures, onAccepted, onPreferencesChanged,
  request = authorizedJson, sessionRequest = loadStaffSession }) {
  const reads = createLatestLoad(), admission = createDraftScope(), events = new window.AbortController();
  let disposed = false, started = false, signOutAttempt = null, unsubscribeInvalid = null, unsubscribeAccess = null;

  function lose(message, accessUnavailable = false) {
    if (disposed) return;
    reads.begin('session').abort(); navigation.invalidate();
    let featureMessage = null;
    // Authoritative session loss retires all owners even if draft/context stamps
    // changed during Sign Out. It is revocation, not discard consent for a target.
    for (const feature of getFeatures()) {
      const result = feature.signedOut?.(); if (typeof result === 'string') featureMessage = result;
    }
    identity.clear(); shell.showSignedOut(message, featureMessage, accessUnavailable);
  }
  function accessUnavailable() {
    lose('Staff access is not currently available. Sign out or use a different authorized Microsoft account.', true);
  }
  async function startSession() {
    const load = reads.begin('session');
    try {
      const session = await sessionRequest({ signal: load.signal });
      if (disposed || !load.isCurrent()) return;
      if (!session.authenticated) { lose(); return; }
      if (session.accessAllowed === false) { accessUnavailable(); return; }
      const staff = identity.accept(session.staff);
      onAccepted(staff);
      for (const feature of getFeatures()) feature.setStaff?.(staff);
      shell.showWorkspace(staff); shell.announce('Staff session ready.');
      await navigation.navigateFromUrl();
    } catch (error) {
      if (!disposed && load.isCurrent() && !isAbortError(error)) lose('Staff access could not be loaded. Try signing in again.');
    } finally { reads.finish('session', load.token); }
  }
  async function signOut() {
    if (disposed || signOutAttempt?.pending || !admission.admit({ consumes: null }).allowed) return;
    const departure = navigation.prepareDeparture();
    if (!departure) return;
    const attempt = { owner: identity.preferences(), departure, pending: true, outcome: 'pending' }; signOutAttempt = attempt;
    try {
      await request('/api/asap/staff/sign-out', { method: 'POST', allowNoContent: true });
      attempt.outcome = 'committed';
      if (!disposed && shell.isPresentationOwner(attempt.owner)) lose('You are signed out.');
    } catch (error) {
      attempt.outcome = error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error) ? 'uncertain' : 'rejected';
      if (attempt.outcome === 'uncertain') await reviewSignOut(attempt);
      else if (!disposed && error.status !== 401) shell.signOutFailed(attempt.owner);
    } finally { attempt.pending = false; }
  }
  async function reviewSignOut(attempt) {
    const owns = () => !disposed && signOutAttempt === attempt &&
      (attempt.owner ? identity.isCurrent(attempt.owner) : shell.isPresentationOwner(null));
    if (!owns()) return;
    const load = reads.begin('session');
    const unconfirmed = 'The Sign out result could not be confirmed. Sign in again to establish your staff session.';
    const revoke = () => { lose(unconfirmed); shell.signOutFailed(attempt.owner, unconfirmed); };
    try {
      const session = await sessionRequest({ signal: load.signal });
      if (!load.isCurrent() || !owns()) return;
      if (session.authenticated === false) { attempt.outcome = 'committed'; lose('You are signed out.'); return; }
      if (session.authenticated === true && session.accessAllowed === false) { accessUnavailable(); return; }
      if (session.authenticated === true && session.accessAllowed === true && session.staff) {
        if (!updatePreferences(session.staff, attempt.owner)) return;
        shell.signOutFailed(attempt.owner, 'Sign out was not confirmed. Your staff session is still active. Please try again.');
        return;
      }
      revoke();
    } catch (error) {
      if (load.isCurrent() && owns()) revoke();
    } finally { reads.finish('session', load.token); }
  }
  function updatePreferences(staff, owner) {
    if (disposed || !identity.isCurrent(owner)) return false;
    const preferences = identity.updatePreferences(staff, owner);
    if (!preferences) { lose('The staff account or access changed. Reload this page to continue.'); return false; }
    shell.preferencesChanged?.(preferences);
    onPreferencesChanged(preferences); return true;
  }
  async function refreshCurrentStaff(owner) {
    if (disposed || !identity.isCurrent(owner)) return null;
    const load = reads.begin('session');
    try {
      const session = await sessionRequest({ signal: load.signal });
      if (disposed || !load.isCurrent() || !identity.isCurrent(owner)) return null;
      if (!session.authenticated) { lose('Your staff session ended. Sign in again.'); return null; }
      if (session.accessAllowed === false) { accessUnavailable(); return null; }
      return updatePreferences(session.staff, owner) ? identity.preferences() : null;
    } catch (error) {
      if (!disposed && load.isCurrent() && identity.isCurrent(owner)) {
        if (error.status === 401) lose('Your staff session ended or no longer has access. Sign in again.');
        else if (error.status === 403 && error.response?.accessAllowed === false) accessUnavailable();
      }
      throw error;
    } finally { reads.finish('session', load.token); }
  }
  return { signOut, updatePreferences, refreshCurrentStaff, lose, accessUnavailable,
    inspectDeparture: () => ({ blocked: Boolean(signOutAttempt?.pending &&
      (identity.isCurrent(signOutAttempt.owner) || !signOutAttempt.owner && shell.isPresentationOwner(null))),
      message: 'Wait for the Sign out result and session review before navigating away.' }),
    async start() {
      if (disposed || started) return; started = true;
      unsubscribeInvalid = onSessionInvalid(() => lose('Your staff session ended or no longer has access. Sign in again.'));
      unsubscribeAccess = onAccessUnavailable(accessUnavailable);
      window.addEventListener('beforeunload', event => {
        const inspections = getFeatures().map(feature => feature.inspectDeparture?.());
        if (!inspections.some(inspection => inspection?.dirty || inspection?.blocked) && !signOutAttempt?.pending) return;
        event.preventDefault(); event.returnValue = '';
      }, { signal: events.signal });
      await startSession();
    },
    dispose() { if (disposed) return; disposed = true; reads.begin('session').abort(); admission.dispose(); events.abort(); unsubscribeInvalid?.(); unsubscribeAccess?.(); }
  };
}
