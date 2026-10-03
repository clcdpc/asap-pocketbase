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
    if (disposed || signOutAttempt?.pending || !admission.admit({ consumes: null }).allowed || !navigation.allow()) return;
    const attempt = { owner: identity.preferences(), pending: true, outcome: 'pending' }; signOutAttempt = attempt;
    try {
      await request('/api/asap/staff/sign-out', { method: 'POST' });
      attempt.outcome = 'committed';
      if (!disposed && shell.isPresentationOwner(attempt.owner)) lose('You are signed out.');
    } catch (error) {
      attempt.outcome = !error.status || isAbortError(error) ? 'uncertain' : 'rejected';
      if (!disposed && error.status !== 401) shell.signOutFailed(attempt.owner);
    } finally { attempt.pending = false; }
  }
  function updatePreferences(staff, owner) {
    if (disposed) return false;
    const preferences = identity.updatePreferences(staff, owner);
    if (!preferences) { lose('The staff account or access changed. Reload this page to continue.'); return false; }
    onPreferencesChanged(preferences); return true;
  }
  return { signOut, updatePreferences, lose, accessUnavailable,
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
