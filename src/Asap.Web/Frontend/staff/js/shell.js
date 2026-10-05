import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { forgetRecentRequest, readRecentRequests, recentStorageKey, rememberRecentRequest, validRequestId } from './recent-requests.js';
import { element, statusLabel } from './ui.js';

// Shell owns the application frames and receipt presentation; feature bodies
// remain owned by their controllers. Receipts are bound to one accepted epoch.
export function createStaffShell({ root, sessionIdentity, getContext, settingsScope, operationsScope,
  findTitle, openRecentTitle, onViewIntent, onSignOutIntent, request = authorizedJson }) {
  const get = selector => root.querySelector(selector);
  const dom = { status: get('#app-status'), readiness: get('#email-readiness-warning'), signedOut: get('#signed-out'),
    message: get('#signed-out-message'), workspace: get('#workspace'), actions: get('#session-actions'),
    identity: get('#staff-identity'), recent: get('#recent-work'), recentList: get('#recent-request-list') };
  const views = { queue: ['#queue-view', '#queue-title'], 'additional-copies': ['#additional-copy-view', '#additional-copy-title'],
    settings: ['#settings-view', '#settings-title'], analytics: ['#analytics-view', '#analytics-title'],
    profile: ['#profile-view', '#profile-title'], operations: ['#operations-view', '#operations-title'] };
  const tabs = [...root.querySelectorAll('.view-tab')];
  const reads = createLatestLoad(), events = new window.AbortController(), receipts = new Map();
  let lastOwner = null, recentKey = null, receiptSequence = 0, disposed = false;

  function announce(message, kind = '') {
    if (disposed) return;
    dom.status.textContent = message || ''; dom.status.className = `status-message${kind ? ` ${kind}` : ''}`;
  }

  function storage() { try { return window.sessionStorage; } catch { return null; } }
  function latestReceipt() {
    const owner = sessionIdentity.preferences() || lastOwner;
    return [...receipts.values()].filter(receipt => sessionIdentity.sameSession(receipt.owner, owner))
      .sort((left, right) => right.sequence - left.sequence)[0];
  }
  function isPresentationOwner(owner) {
    return owner ? sessionIdentity.sameSession(owner, sessionIdentity.preferences() || lastOwner) : !lastOwner && !sessionIdentity.actor();
  }
  function recordReceipt(message, owner, attempt, evidence = {}) {
    if (!attempt || !owner || !sessionIdentity.sameSession(owner, sessionIdentity.preferences() || lastOwner)) return;
    receipts.set(attempt, { message, owner, ...evidence, sequence: ++receiptSequence });
    if (!disposed && dom.workspace.hidden) dom.message.textContent = latestReceipt()?.message || message;
  }
  function clearReceipt(attempt) {
    const receipt = receipts.get(attempt);
    if (receipt && sessionIdentity.isCurrent(receipt.owner)) receipts.delete(attempt);
  }
  function settingsRefreshed(owner, review) {
    if (disposed || !sessionIdentity.isCurrent(owner) || !review) return;
    for (const [attempt, receipt] of receipts) {
      const kind = attempt.slot === 'administration-staff-mutation' ? 'staff' : 'settings';
      if (receipt.feature === 'settings' && sessionIdentity.sameSession(receipt.owner, owner) &&
          kind === review.kind && attempt.context?.scope === review.scope) receipts.delete(attempt);
    }
    void refreshReadiness();
  }
  function queueRefreshed({ sequence }) {
    if (disposed) return;
    renderRecent();
    for (const [attempt, receipt] of receipts) {
      if (sessionIdentity.isCurrent(receipt.owner) && receipt.detailAvailable && sequence > receipt.queueSequence) receipts.delete(attempt);
    }
  }

  function presentView(name) {
    if (disposed) return;
    for (const [key, [selector]] of Object.entries(views)) get(selector).hidden = name !== key;
    for (const tab of tabs) {
      const active = tab.dataset.view === name;
      tab.classList.toggle('active', active);
      if (active) tab.setAttribute('aria-current', 'page'); else tab.removeAttribute('aria-current');
    }
    const heading = get(views[name][1]);
    if (heading.isConnected && sessionIdentity.actor()) heading.focus({ preventScroll: true });
  }

  function readinessScope(context = getContext()) {
    const scope = context.activeView === 'settings' ? settingsScope()
      : context.activeView === 'operations' ? operationsScope() : context.scope;
    return sessionIdentity.actor()?.role === 'super_admin' && /^\d+$/.test(String(scope)) ? String(scope) : 'default';
  }
  async function refreshReadiness() {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner) return;
    const load = reads.begin('readiness'), scope = readinessScope();
    const current = () => !disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) && readinessScope() === scope;
    try {
      const data = await request(`/api/asap/staff/email-readiness${scope === 'default' ? '' : `?organizationId=${encodeURIComponent(scope)}`}`, { signal: load.signal });
      if (!current()) return;
      const warning = data?.state === 'not_configured' ? 'Email delivery is not configured for this scope. Requests and staff workflows remain available.'
        : data?.state === 'non_delivery' ? 'Live email delivery is disabled in this environment. Requests and staff workflows remain available.'
          : data?.state === 'unavailable' ? 'Email delivery status is unavailable. Requests and staff workflows remain available.' : '';
      dom.readiness.textContent = warning; dom.readiness.hidden = !warning;
    } catch (error) {
      if (current() && !isAbortError(error) && error.status !== 401) {
        dom.readiness.textContent = 'Email delivery status is unavailable. Requests and staff workflows remain available.'; dom.readiness.hidden = false;
      }
    } finally { reads.finish('readiness', load.token); }
  }

  function renderRecent() {
    if (disposed) return;
    const store = storage(), items = store ? readRecentRequests(store, recentKey) : [], owner = sessionIdentity.preferences();
    dom.recentList.replaceChildren();
    if (!items.length) { dom.recentList.append(element('p', { text: 'No recently opened requests.' })); return; }
    for (const item of items) {
      const title = findTitle(item.id);
      const button = element('button', { type: 'button', text: title ? `${title.title} · Request ${item.id}` : `Request ${item.id}`,
        onclick: async () => {
          if (disposed || !button.isConnected || !sessionIdentity.isCurrent(owner)) return;
          dom.recent.open = false;
          await openRecentTitle(Object.freeze({ id: item.id, owner, opener: dom.recent }));
        } });
      dom.recentList.append(button);
    }
  }
  function rememberOpened(id) {
    const store = storage();
    if (disposed || !sessionIdentity.actor() || !store || !recentKey || !validRequestId(id)) return;
    rememberRecentRequest(store, recentKey, id); renderRecent();
  }
  function forgetUnavailable(id) {
    const store = storage(); if (!disposed && store && recentKey) forgetRecentRequest(store, recentKey, id);
    renderRecent();
  }

  function preferencesChanged(staff) {
    if (disposed || !sessionIdentity.isCurrent(staff)) return;
    dom.identity.textContent = staff.displayName || staff.userPrincipalName || 'Staff user';
    dom.identity.title = `${statusLabel(staff.role)} · ${staff.organizationName}`;
  }
  function showWorkspace(staff) {
    if (disposed) return;
    if (!sessionIdentity.sameSession(lastOwner, staff)) receipts.clear();
    lastOwner = staff; recentKey = recentStorageKey(staff); renderRecent();
    dom.signedOut.hidden = true; dom.workspace.hidden = false; dom.actions.hidden = false;
    preferencesChanged(staff);
    get('#operations-view-tab').hidden = !['admin', 'super_admin'].includes(staff.role);
    void refreshReadiness();
  }
  function showSignedOut(message, featureMessage = null, accessUnavailable = false) {
    if (disposed) return;
    reads.begin('readiness').abort(); dom.readiness.hidden = true;
    const store = storage(); if (store && recentKey) { try { store.removeItem(recentKey); } catch { /* Storage may be unavailable. */ } }
    recentKey = null; dom.recent.open = false; dom.recentList.replaceChildren();
    dom.message.textContent = latestReceipt()?.message || featureMessage || message || 'Sign in with your authorized library account.';
    dom.signedOut.hidden = false; dom.workspace.hidden = true; dom.actions.hidden = !accessUnavailable;
    if (accessUnavailable) { dom.identity.textContent = 'Access unavailable'; dom.identity.title = ''; }
    announce('');
  }

  for (const tab of tabs) tab.addEventListener('click', () => onViewIntent(tab.dataset.view), { signal: events.signal });
  get('#sign-out').addEventListener('click', () => onSignOutIntent(), { signal: events.signal });
  return { announce, presentView, showWorkspace, showSignedOut, preferencesChanged, recordReceipt, clearReceipt, settingsRefreshed, queueRefreshed,
    rememberOpened, forgetUnavailable, renderRecent, refreshReadiness,
    configurationCommitted(scope) {
      if (disposed || !sessionIdentity.actor()) return;
      const currentScope = readinessScope();
      const effectiveScope = currentScope === 'default' ? String(sessionIdentity.actor().organizationId) : currentScope;
      if (scope !== 'system' && String(scope) !== '1' && String(scope) !== effectiveScope) return;
      reads.begin('readiness').abort();
      dom.readiness.textContent = 'Email delivery status is unavailable. Requests and staff workflows remain available.';
      dom.readiness.hidden = false;
      void refreshReadiness();
    },
    isPresentationOwner,
    signOutFailed(owner, message = 'Sign out did not complete. Please try again.') {
      if (disposed || !isPresentationOwner(owner)) return;
      const retained = dom.workspace.hidden && dom.message.textContent !== message ? dom.message.textContent : '';
      dom.message.textContent = [message, retained].filter(Boolean).join(' ');
      announce(message, 'error');
    },
    contextChanged(next, previous) { if (readinessScope(next) !== readinessScope(previous)) void refreshReadiness(); },
    dispose() { if (disposed) return; disposed = true; events.abort(); reads.begin('readiness').abort(); }
  };
}
