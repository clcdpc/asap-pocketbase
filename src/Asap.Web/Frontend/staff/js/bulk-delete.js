import { authorizedJson, isAbortError, loadStaffSession } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';
import { element, closeReasonLabel } from './ui.js';
import { actorKey } from './session-identity.js';

function itemLabel(item) {
  return `${item.type === 'title_request' ? 'Title request ' : 'Additional-copy task '}${item.id} (${item.libraryOrgName})`;
}

function validItem(item, type, scope) {
  return item?.type === type && typeof item.id === 'string' && /^[1-9]\d*$/.test(item.id) &&
    typeof item.version === 'string' && item.version.length > 0 && item.status === 'closed' &&
    Number.isSafeInteger(item.libraryOrgId) && (scope === 'all' || String(item.libraryOrgId) === scope);
}

function failureOutcome(error) {
  const code = error.response?.code;
  if (error.status === 404 || code === 'not_found') return 'not_found';
  if (code === 'stale_version') return 'stale';
  if (code === 'actor_changed_since_preview') return 'actor_changed';
  if (code === 'request_not_closed' || code === 'delete_requires_closed') return 'not_closed';
  if (error.status === 401 || error.status === 403 ||
      ['delete_forbidden', 'staff_scope_forbidden', 'staff_session_invalid'].includes(code)) return 'forbidden/out_of_scope';
  if (code === 'hold_history_retained') return 'blocked_hold_history';
  if (isAbortError(error) || error.status === 408 || error.status >= 500 || !error.status) return 'outcome_unconfirmed';
  return 'operational_failure';
}

// The preview is disposable. A submitted runner keeps its captured ledger until
// its outstanding DELETE settles, even when the preview and session disappear.
export function createBulkDeleteController({ root, titleTrigger, copyTrigger, sessionIdentity,
  getLibraries, beforeOpen, beforeExecute, captureReview, reviewClosed, onReceipt, clearReceipt,
  onSessionLost, onAccessUnavailable, request = authorizedJson, sessionRequest = loadStaffSession }) {
  const dom = Object.fromEntries(['close', 'scope', 'preview', 'summary', 'items', 'confirmation', 'execute', 'results']
    .map(name => [name, root.querySelector(`#bulk-delete-${name}`)]));
  const reads = createLatestLoad(), admission = createDraftScope(), events = new window.AbortController();
  let ui = null, runner = null, disposed = false;
  const owns = batch => !disposed && ui === batch && root.open && sessionIdentity.isCurrent(batch.owner);

  function contextChanged(context) {
    if (disposed) return;
    const authorized = ['admin', 'super_admin'].includes(sessionIdentity.actor()?.role);
    titleTrigger.hidden = !authorized || context.status !== 'closed';
    copyTrigger.hidden = !authorized || context.additionalCopyStatus !== 'closed';
  }

  function close(options = {}) {
    if (disposed || !ui) return true;
    if (ui.submitting && !options.force) {
      dom.summary.textContent = 'Deletion is in progress. Wait for the result before closing.';
      return false;
    }
    const previous = ui; ui = null;
    reads.begin('preview').abort();
    if (root.open) root.close();
    dom.items.replaceChildren(); dom.results.replaceChildren(); dom.summary.textContent = '';
    dom.confirmation.value = ''; dom.execute.disabled = true;
    if (!options.navigation && sessionIdentity.isCurrent(previous.owner) && previous.returnFocus?.isConnected) previous.returnFocus.focus();
    return true;
  }

  function resetPreview() {
    if (!ui || ui.submitting || disposed) return;
    reads.begin('preview').abort(); ui.snapshot = null; ui.finished = false;
    dom.summary.textContent = ''; dom.items.replaceChildren(); dom.results.replaceChildren();
    dom.confirmation.value = ''; dom.execute.disabled = true;
    dom.preview.disabled = false; dom.scope.disabled = false; dom.confirmation.disabled = false;
  }

  function open(returnFocus) {
    const owner = sessionIdentity.preferences();
    if (disposed || runner?.submitting || !['admin', 'super_admin'].includes(owner?.role) || !beforeOpen()) return false;
    close({ navigation: true });
    ui = { owner, returnFocus, snapshot: null, submitting: false, ledger: [] };
    dom.scope.replaceChildren();
    if (owner.role === 'super_admin') {
      dom.scope.append(element('option', { value: '', text: 'Choose a library scope' }),
        element('option', { value: 'all', text: 'All libraries' }));
      for (const library of getLibraries()) dom.scope.append(element('option', { value: library.id, text: library.name }));
    } else dom.scope.append(element('option', { value: String(owner.organizationId), text: owner.organizationName || 'My library' }));
    dom.scope.value = owner.role === 'super_admin' ? '' : String(owner.organizationId);
    resetPreview(); root.showModal();
    (owner.role === 'super_admin' ? dom.scope : dom.preview).focus(); return true;
  }

  async function preview() {
    if (!ui || ui.submitting || disposed) return;
    const batch = ui, scope = dom.scope.value;
    resetPreview();
    if (!scope) { dom.summary.textContent = 'Choose a library scope before previewing.'; dom.scope.focus(); return; }
    const load = reads.begin('preview');
    const current = () => load.isCurrent() && owns(batch) && dom.scope.value === scope;
    dom.preview.disabled = true;
    dom.summary.textContent = 'Loading closed title requests and additional-copy tasks...';
    try {
      const session = await sessionRequest({ signal: load.signal });
      if (!current()) return;
      if (!session.authenticated) { onSessionLost(); return; }
      if (!session.accessAllowed) { onAccessUnavailable(); return; }
      const actor = session.staff, owner = batch.owner;
      if (!actor || actorKey(actor) !== actorKey(owner) || !['admin', 'super_admin'].includes(actor.role) ||
          scope === 'all' && actor.role !== 'super_admin' || actor.role === 'admin' && scope !== String(actor.organizationId) ||
          typeof actor.version !== 'string' || !actor.version) throw new Error('Staff role or library scope changed. Reload the workspace before previewing deletion.');
      const query = encodeURIComponent(scope);
      const [titles, copies] = await Promise.all([
        request(`/api/asap/staff/title-requests?scope=${query}`, { signal: load.signal }),
        request(`/api/asap/staff/additional-copies?scope=${query}&status=closed`, { signal: load.signal })
      ]);
      if (!current()) return;
      if (titles.scope !== scope || copies.scope !== scope || copies.status !== 'closed' ||
          !Array.isArray(titles.items) || !Array.isArray(copies.items)) throw new Error('The requested deletion scope could not be verified.');
      const titleItems = titles.items.filter(item => item.status === 'closed');
      if (titleItems.some(item => !validItem(item, 'title_request', scope)) ||
          copies.items.some(item => !validItem(item, 'additional_copy', scope))) throw new Error('The closed-work preview contained an invalid identity or scope.');
      batch.snapshot = Object.freeze({ scope, actorVersion: actor.version,
        items: Object.freeze([...titleItems, ...copies.items].map(item => Object.freeze({
          type: item.type, id: item.id, version: item.version, title: item.title,
          libraryOrgName: item.libraryOrgName, libraryOrgId: item.libraryOrgId, closeReason: item.closeReason
        }))) });
      const scopeName = dom.scope.selectedOptions[0]?.textContent || scope;
      dom.summary.textContent = `Preview for ${scopeName}: ${titleItems.length} closed title requests and ${copies.items.length} closed additional-copy tasks. Search, claim, tag, and grid filters do not affect this population.`;
      dom.items.replaceChildren(...batch.snapshot.items.map(item => element('li', { text: `${itemLabel(item)} — ${item.title}${item.type === 'title_request' ? ` — ${closeReasonLabel(item.closeReason)}` : ''}` })));
      if (!batch.snapshot.items.length) dom.summary.textContent += ' There are no eligible records to submit.';
      dom.confirmation.focus();
    } catch (error) {
      if (current() && !isAbortError(error) && error.status !== 401) dom.summary.textContent = error.message || 'The closed-work preview could not be loaded.';
    } finally {
      if (current()) dom.preview.disabled = false;
      reads.finish('preview', load.token);
    }
  }

  function recordLedger(batch, ledger = batch.ledger) {
    const deleted = ledger.filter(item => item.outcome === 'deleted').length;
    const attempted = ledger.filter(item => item.outcome !== 'not_attempted').length;
    const summary = `Confirmed deleted: ${deleted} of ${batch.snapshot.items.length}. Attempted: ${attempted}. Every record is rechecked by the server.`;
    onReceipt(`${summary} ${ledger.map(item => `${itemLabel(item)}: ${item.outcome.replaceAll('_', ' ')}`).join('; ')}. Sign in again and refresh Closed work before retrying.`, batch.owner, batch);
    if (owns(batch)) dom.results.replaceChildren(element('p', { text: summary }),
      element('ul', {}, ledger.map(item => element('li', { text: `${itemLabel(item)}: ${item.outcome.replaceAll('_', ' ')}` }))));
  }

  function interrupt() {
    const batch = runner;
    if (!batch?.submitting) return;
    batch.interrupted = true;
    if (!batch.currentItem) return;
    const remaining = batch.snapshot.items.slice(batch.snapshot.items.indexOf(batch.currentItem) + 1);
    recordLedger(batch, [...batch.ledger, { ...batch.currentItem, outcome: 'outcome_unconfirmed' },
      ...remaining.map(item => ({ ...item, outcome: 'not_attempted' }))]);
  }

  async function execute() {
    const batch = ui;
    if (!batch || !owns(batch) || !batch.snapshot?.items.length || batch.finished || batch.submitting || runner?.submitting ||
        dom.confirmation.value !== 'DELETE' || dom.scope.value !== batch.snapshot.scope ||
        !admission.admit({ consumes: null }).allowed || !beforeExecute()) return;
    const review = captureReview(batch.owner);
    batch.submitting = true; batch.interrupted = false; batch.ledger = []; runner = batch;
    dom.execute.disabled = true; dom.preview.disabled = true; dom.scope.disabled = true; dom.confirmation.disabled = true;
    let stop = false;
    for (const item of batch.snapshot.items) {
      if (stop || batch.interrupted || disposed || !sessionIdentity.isCurrent(batch.owner)) {
        batch.ledger.push({ ...item, outcome: 'not_attempted' }); continue;
      }
      const path = item.type === 'title_request' ? '/api/asap/staff/requests/' : '/api/asap/staff/additional-copies/';
      batch.currentItem = item;
      try {
        const result = await request(path + encodeURIComponent(item.id), { method: 'DELETE',
          body: { version: item.version, actorVersion: batch.snapshot.actorVersion } });
        batch.ledger.push({ ...item, outcome: result?.deleted === true ? 'deleted' : 'outcome_unconfirmed' });
        if (result?.deleted !== true) stop = true;
      } catch (error) {
        const outcome = failureOutcome(error); batch.ledger.push({ ...item, outcome });
        stop = outcome === 'outcome_unconfirmed' || outcome === 'actor_changed' || error.status === 401 || error.status === 403;
      }
      batch.currentItem = null; recordLedger(batch);
    }
    batch.submitting = false; batch.finished = true; if (runner === batch) runner = null;
    recordLedger(batch);
    if (!owns(batch)) return;
    dom.summary.textContent = 'Deletion finished. Review the ledger and refresh Closed work before retrying.';
    let result;
    try { result = await reviewClosed(Object.freeze({ owner: batch.owner, scope: batch.snapshot.scope, review })); }
    catch {
      if (owns(batch)) dom.summary.textContent = 'Deletion finished, but a Closed view could not refresh. Review the ledger and refresh before retrying.';
      return;
    }
    if (!owns(batch) || !result?.isCurrent()) return;
    if (result.refreshed) clearReceipt(batch);
    dom.summary.textContent = result.refreshed ? 'Deletion finished. Both Closed views were refreshed from the server.'
      : 'Deletion finished, but a Closed view could not refresh. Review the ledger and refresh before retrying.';
    dom.results.focus();
  }

  titleTrigger.addEventListener('click', event => open(event.currentTarget), { signal: events.signal });
  copyTrigger.addEventListener('click', event => open(event.currentTarget), { signal: events.signal });
  dom.close.addEventListener('click', () => close(), { signal: events.signal });
  root.addEventListener('cancel', event => { event.preventDefault(); close(); }, { signal: events.signal });
  root.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); close(); } }, { signal: events.signal });
  dom.scope.addEventListener('change', resetPreview, { signal: events.signal });
  dom.preview.addEventListener('click', preview, { signal: events.signal });
  dom.confirmation.addEventListener('input', () => {
    dom.execute.disabled = !ui?.snapshot?.items.length || ui.submitting || dom.scope.value !== ui.snapshot.scope || dom.confirmation.value !== 'DELETE';
  }, { signal: events.signal });
  dom.execute.addEventListener('click', execute, { signal: events.signal });
  return { open, close, contextChanged, interrupt,
    inspectDeparture: () => ({ owner: ui, blocked: disposed || Boolean(runner?.submitting), message: 'Deletion is in progress. Wait for the complete ledger before navigating away.' }),
    hasPendingMutation: () => Boolean(runner?.submitting),
    signedOut() { if (!disposed) { interrupt(); close({ force: true, navigation: true }); } },
    dispose() { if (disposed) return; interrupt(); close({ force: true, navigation: true }); disposed = true; reads.begin('preview').abort(); admission.dispose(); events.abort(); }
  };
}
