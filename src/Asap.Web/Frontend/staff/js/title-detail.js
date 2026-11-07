import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';
import { confirmAction, controlSnapshot } from './confirmation.js';
import { createRequestEditor } from './request-editor.js';
import { researchRecordUrl, researchRecordValue } from './research.js';
import { createRequestWorkflows } from './request-workflows.js';
import { unconfirmedResponseError, notificationOutcome, isCommittedRequestResponse } from './mutation-outcome.js';
import { element, icon, commandButton, statusLabel, dateTime, text, closeReasonLabel, timeoutLabel, addDetail } from './ui.js';

export function createTitleDetailController({ host, sessionIdentity, polarisLookup, copyCreation, announce,
  beforeOpen, isNavigationCurrent, getNavigationGeneration, getScope, onAlign, onOpened, onUpdated = () => {}, beforeClose, onClosed,
  getFocusReturn, refreshQueue, queueSequence, rememberOpened, forgetUnavailable, onReceipt, clearReceipt,
  refreshCurrentStaff = async () => null,
  onNewSuggestion = null,
  onOpenEmailOperation = null,
  request: send = authorizedJson }) {
  const reads = createLatestLoad(), configurations = new Map();
  let lease = null, actor = null, current = null, returnFocus = null, activeAttempt = null;
  let drafts = createDraftScope(), renderRevision = null, editor = null, workflows = null, copyParent = null, disposed = false, targetGeneration = 0;
  let selectedTab = 'details', tabRequestId = null;
  let inlineOpeningEnter = false;
  // Duplicate recovery can sit above retained BIB linking; dismiss the latest
  // panel first and restore the toolbar only when both owners have finished.
  const inlineActions = new Map();

  function isPresentationCurrent(mounted, owner) {
    return !disposed && mounted?.isCurrent() && sessionIdentity.isCurrent(owner);
  }
  function isCurrentSelection(snapshot) { return isPresentationCurrent(lease, actor) && current?.id === snapshot.id; }
  function hasPendingMutation() { return Boolean(activeAttempt?.pending && sessionIdentity.isCurrent(activeAttempt.owner) || copyParent && copyCreation.hasPendingMutation(copyParent)); }
  function beginInlineAction(panel, cancel) {
    workflows?.invalidate();
    inlineActions.set(panel, cancel);
    inlineOpeningEnter = true;
    const bar = lease?.content.querySelector('.action-bar');
    if (bar) bar.hidden = true;
  }
  function endInlineAction(panel) {
    inlineActions.delete(panel);
    const bar = lease?.content.querySelector('.action-bar');
    if (bar) bar.hidden = inlineActions.size > 0;
  }
  function cancelInlineAction() {
    if (hasPendingMutation()) return false;
    const cancel = [...inlineActions.values()].at(-1);
    if (!cancel) return false;
    cancel();
    return true;
  }
  function allowRequestMutation(snapshot, declaration) {
    if (!isCurrentSelection(snapshot) || current.version !== snapshot.version || hasPendingMutation()) return false;
    if (editor?.needsBibLinkReload()) {
      editor.showBibLinkError('Reload the current request before trying another change.', true);
      return false;
    }
    const admission = drafts.admit(declaration);
    if (!admission.allowed) {
      announce(admission.kind === 'editor' ? 'Save or revert the current request edits before performing a workflow action.'
        : admission.reason === 'competing_draft' ? 'Finish or cancel the current request changes before performing another action.'
          : 'This draft no longer belongs to the current request.', 'warning');
    }
    return admission.allowed;
  }
  function detailPath(id, scope = getScope(), owner = actor) {
    const path = `/api/asap/staff/title-requests/${encodeURIComponent(id)}`;
    return owner?.role === 'super_admin' ? `${path}?scope=${encodeURIComponent(scope)}` : path;
  }
  async function configurationFor(library, signal, owner) {
    const key = String(library);
    let entry = configurations.get(key);
    if (!entry) { entry = {}; configurations.set(key, entry); }
    if (entry.value) return entry.value;
    const value = await send(`/api/asap/config?libraryOrgId=${encodeURIComponent(key)}`, { signal });
    if (!signal.aborted && sessionIdentity.isCurrent(owner)) {
      // A Settings refresh also invalidates an in-flight cache population.
      if (configurations.get(key) !== entry) return configurationFor(library, signal, owner);
      entry.value = value;
    }
    return value;
  }
  function invalidateConfiguration(scope) {
    if (disposed) return;
    const key = String(scope);
    if (key === 'system' || key === '1') configurations.clear();
    else configurations.delete(key);
  }
  function invalidate() { targetGeneration++; reads.begin('detail-target').abort(); reads.begin('detail').abort(); editor?.invalidate(); workflows?.invalidate(); if (copyParent) copyCreation.invalidate(copyParent); }
  function closeCopy(options) { return !copyParent || copyCreation.close(options, copyParent); }
  function showDetailsUnavailable(mounted, message) {
    if (!mounted?.isCurrent()) return false;
    const close = commandButton('Close', 'times', () => {
      if (mounted.isCurrent()) host.requestClose();
    });
    close.classList.add('request-detail-close');
    mounted.content.replaceChildren(
      element('div', { className: 'request-middle' }, element('p', { text: message })),
      element('footer', { className: 'request-save-footer' }, [
        element('div', { className: 'request-save-feedback', text: 'Details unavailable' }),
        element('div', { className: 'form-actions' }, [close])
      ])
    );
    return true;
  }
  function disposeChildren() {
    renderRevision = null;
    inlineActions.clear();
    inlineOpeningEnter = false;
    closeCopy({ navigation: true, force: true }); copyParent = null;
    editor?.dispose(); workflows?.dispose(); editor = null; workflows = null;
    drafts.dispose();
  }
  function disposeMounted() {
    invalidate(); disposeChildren(); lease = null; actor = null; current = null; returnFocus = null; activeAttempt = null;
  }
  async function prepare(id, options = {}) {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner) return null;
    const load = reads.begin('detail-target'), ticket = ++targetGeneration;
    const live = () => !disposed && !load.signal.aborted && ticket === targetGeneration && sessionIdentity.isCurrent(owner);
    try {
      const scope = options.scope ?? (owner.role === 'super_admin' && (options.align || options.fromRecent) ? 'all' : getScope());
      const request = await send(detailPath(id, scope, owner), { signal: load.signal });
      if (!live()) return null;
      const configuration = await configurationFor(request.libraryOrgId, load.signal, owner);
      const ready = () => live() && configurations.get(String(request.libraryOrgId))?.value === configuration;
      return ready() ? { request, configuration, scope, owner, isCurrent: ready } : null;
    } finally { reads.finish('detail-target', load.token); }
  }

  function present(target, opener, options, ticket) {
    if (disposed || !sessionIdentity.isCurrent(target.owner) || !isNavigationCurrent(ticket)) return false;
    const { request, configuration, owner } = target;
    if (!options.authoritativeRefresh) selectedTab = 'details';
    const mounted = host.acquire({ dispose: disposeMounted, onClose: close, hasInlineAction: () => inlineActions.size > 0,
      onEscape: () => { if (!hasPendingMutation() && workflows?.escape()) return; close(); } });
    lease = mounted; actor = owner;
    returnFocus = opener || document.activeElement; drafts = createDraftScope();
    mounted.content.addEventListener('keydown', event => {
      if (event.key !== 'Enter' || !inlineOpeningEnter || !isPresentationCurrent(mounted, owner)) return;
      // Focus moves during the opener's keydown. Its held-key repeats must not
      // approve the newly opened panel; a fresh press keeps normal form behavior.
      if (!event.repeat) { inlineOpeningEnter = false; return; }
      if (inlineActions.size) { event.preventDefault(); event.stopPropagation(); }
    }, { capture: true });
    mounted.content.addEventListener('keyup', event => {
      if (event.key === 'Enter' && isPresentationCurrent(mounted, owner)) inlineOpeningEnter = false;
    }, { capture: true });
    for (const name of ['input', 'change']) mounted.content.addEventListener(name, event => {
      if (isPresentationCurrent(mounted, owner) && mounted.content.contains(event.target)) drafts.touch(event.target);
    });
    onOpened(request, options); renderRequest(request, configuration, null, options.retainedEditor); mounted.show();
    announce(`Opened ${request.title}.`); rememberOpened(request.id);
    if (options.action) workflows.startAction(options.action);
    return true;
  }

  async function open(id, opener = null, options = {}) {
    if (disposed || !sessionIdentity.actor()) return false;
    const departure = await beforeOpen(options);
    if (!departure) return false;
    const owner = sessionIdentity.preferences(), priorTitle = current?.title || `Request ${id}`;
    announce('Loading request details...');
    try {
      let target = await prepare(id, options);
      if (!target?.isCurrent() || !departure.isCurrent()) return false;
      let alignment = await onAlign(target.request, options, owner, departure);
      if (!alignment || !target.isCurrent() || !departure.isCurrent()) return false;
      if (owner.role === 'super_admin' && target.scope !== alignment.scope) {
        target = await prepare(id, { ...options, scope: alignment.scope });
        if (!target?.isCurrent() || !departure.isCurrent()) return false;
        alignment = await onAlign(target.request, { ...options, reloaded: true }, owner, departure);
      }
      if (!alignment || !target.isCurrent() || !departure.isCurrent()) return false;
      const ticket = await alignment.commit();
      return ticket !== false && present(target, opener, options, ticket);
    } catch (error) {
      if (departure.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        if (options.authoritativeRefresh && lease?.isCurrent()) {
          disposeChildren(); current = Object.freeze({ id: String(id), version: null });
          lease.heading(priorTitle, `Request ${id}`);
          showDetailsUnavailable(lease, 'Current details could not refresh. Reload this request before performing another action.'); lease.show();
        }
        announce(error.status === 404 ? 'That request is no longer available.' : error.message, 'error');
        if (options.fromRecent && error.status === 404) forgetUnavailable(String(id));
      }
      return false;
    }
  }
  async function close(options = {}) {
    if (hasPendingMutation() && !options.force && !options.preserveMutation) {
      announce('The workflow action is in progress. Wait for its authoritative result before closing.', 'warning'); host.focusClose(); return false;
    }
    const mounted = lease;
    if (!options.force && !options.guarded && !options.preserveMutation && !await beforeClose(options)) return false;
    if (lease !== mounted) return false;
    if (!closeCopy({ navigation: true, force: options.force })) return false;
    const focusReturn = options.navigation ? null : getFocusReturn(current?.id, returnFocus);
    lease?.release({ ...options, focusReturn }); onClosed(options); return true;
  }
  function openCopy(snapshot, context, opener) {
    const mounted = lease, scope = drafts, owner = actor;
    copyParent = Object.freeze({ request: snapshot, actor: owner, isCurrent: context.isCurrent,
      isSelectionCurrent: () => isPresentationCurrent(lease, owner) && current?.id === snapshot.id,
      admit: context.admit,
      registerDraft: definition => scope.register(definition), releaseDraft: handle => scope.release(handle), touchDraft: () => scope.touch(),
      refresh: async target => {
        if (!isPresentationCurrent(mounted, owner)) return null;
        const generation = getNavigationGeneration(), queueRefreshed = await refreshQueue({ silent: true });
        if (!isPresentationCurrent(mounted, owner) || !isNavigationCurrent(generation)) return null;
        const detailLoaded = await open(snapshot.id, target, { authoritativeRefresh: true });
        const replacement = lease;
        return { queueRefreshed, detailLoaded,
          isCurrent: () => isNavigationCurrent(generation + (detailLoaded ? 1 : 0)) &&
            (detailLoaded || replacement === mounted) && isPresentationCurrent(replacement, owner) && current?.id === snapshot.id };
      }
    });
    return copyCreation.preview(copyParent, opener);
  }
  function beginAttempt(snapshot, operation, body) {
    const mounted = lease, owner = sessionIdentity.preferences(), generation = getNavigationGeneration();
    const attempt = { snapshot, owner, operation, body: Object.freeze({ ...body }), mounted, generation, pending: true, outcome: 'pending',
      isCurrent: () => isPresentationCurrent(mounted, owner) && isNavigationCurrent(generation) };
    activeAttempt = attempt; return attempt;
  }
  function disableControls(attempt) {
    const controls = [...attempt.mounted.content.querySelectorAll('input, select, textarea, button:not(.request-detail-close)')]
      .map(control => ({ control, disabled: control.disabled }));
    for (const item of controls) item.control.disabled = true;
    return () => { for (const item of controls) if (attempt.mounted.isCurrent() && item.control.isConnected) item.control.disabled = item.disabled; };
  }
  function recordReceipt(attempt, message, detailAvailable = false) {
    onReceipt(message, attempt.owner, attempt, { detailAvailable, queueSequence: queueSequence() });
  }
  function releaseAttempt(attempt) { if (activeAttempt === attempt) activeAttempt = null; }
  async function reloadAfterMutation(attempt, retained = null) {
    const refreshed = await refreshQueue({ skipDeepLink: true, silent: true });
    if (!attempt.isCurrent()) return null;
    if (retained) {
      const detailLoaded = await reloadBibLink(attempt.snapshot, retained, attempt.isCurrent);
      return { refreshed, detailLoaded, isCurrent: attempt.isCurrent };
    }
    const detailLoaded = await open(attempt.snapshot.id, null, { authoritativeRefresh: true });
    const replacement = lease;
    return { refreshed, detailLoaded,
      isCurrent: () => isPresentationCurrent(replacement, attempt.owner) && (detailLoaded || replacement === attempt.mounted) &&
        isNavigationCurrent(attempt.generation + (detailLoaded ? 1 : 0)) && current?.id === attempt.snapshot.id };
  }
  async function reloadBibLink(snapshot, retained, isCurrent = () => isCurrentSelection(snapshot)) {
    const ownerEditor = editor;
    try {
      const target = await prepare(snapshot.id, { scope: getScope() });
      if (!target?.isCurrent() || !isCurrent() || editor !== ownerEditor) return false;
      // Recovery reads leave the editor interactive. Retain changes and any
      // cancellation made while the authoritative response was in flight.
      const latestDraft = ownerEditor.captureDraft() ?? retained;
      onUpdated(target.request);
      renderRequest(target.request, target.configuration, null, { ...latestDraft, needsReload: false });
      if (!latestDraft.linking) host.focusClose();
      return true;
    } catch (error) {
      if (isCurrent() && !isAbortError(error) && error.status !== 401) {
        editor?.showBibLinkError('Current request details could not be loaded. Reload before retrying the action.', true);
      }
      return false;
    }
  }
  async function deleteTitleRequest(snapshot) {
    if (!allowRequestMutation(snapshot, { consumes: null })) return;
    const attempt = beginAttempt(snapshot, 'delete', { version: snapshot.version, actorVersion: sessionIdentity.preferences().version });
    const restore = disableControls(attempt);
    announce('Permanently deleting title request...');
    try {
      const result = await send(`/api/asap/staff/requests/${encodeURIComponent(snapshot.id)}`, { method: 'DELETE', body: attempt.body });
      if (result?.deleted !== true) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      const message = `Title request ${snapshot.id} permanently deleted. Its deletion audit remains.`;
      recordReceipt(attempt, `${message} Sign in again to review Closed work.`);
      if (!attempt.isCurrent()) return;
      close({ preserveMutation: true }); announce(message, 'success');
      const refreshed = await refreshQueue({ skipDeepLink: true, silent: true });
      if (refreshed === true) clearReceipt(attempt);
      if (refreshed === false && sessionIdentity.isCurrent(attempt.owner) && isNavigationCurrent(attempt.generation)) announce(`${message} The Closed view could not refresh.`, 'warning');
    } catch (error) {
      if (attempt.outcome === 'committed') return;
      const uncertain = error.outcomeUnknown === true || !error.status || isAbortError(error) || error.status === 408 || error.status >= 500;
      attempt.outcome = uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      if (uncertain) recordReceipt(attempt, 'Title-request deletion could not be confirmed. Sign in again and refresh Closed work before retrying.');
      if (!attempt.isCurrent() || error.status === 401) return;
      if (error.response?.code === 'actor_changed_since_preview') {
        try { await refreshCurrentStaff(attempt.owner); }
        catch { if (attempt.isCurrent()) announce('The current staff session could not be refreshed. Review it before retrying deletion.', 'error'); return; }
        if (!attempt.isCurrent()) return;
      }
      await refreshQueue({ skipDeepLink: true, silent: true });
      if (attempt.isCurrent()) announce(uncertain ? 'Title-request deletion could not be confirmed. Review Closed work before retrying.'
        : error.message || 'The title request was not deleted. Review its current state.', uncertain ? 'warning' : 'error');
    } finally { attempt.pending = false; releaseAttempt(attempt); restore(); }
  }
  async function mutateRequest(snapshot, path, body, successMessage, draft = null, retainEditor = false) {
    if (!allowRequestMutation(snapshot, { consumes: draft })) return;
    const retained = retainEditor ? editor.captureDraft() : null;
    const attempt = beginAttempt(snapshot, path, body), restore = disableControls(attempt);
    announce('Saving request...');
    try {
      const result = await send(path, { method: 'POST', body: attempt.body });
      if (!isCommittedRequestResponse(result, snapshot.id)) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      let detail = result.request || (result.id ? result : null);
      const status = detail?.status || result.finalStatus;
      const label = body.action === 'reject' ? 'Rejection email' : path.endsWith('/assign') ? 'Assignment notification'
        : body.action === 'purchase' ? 'Purchase reminder' : 'Notification';
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, label);
      const patronNotification = notificationOutcome(result.patronNotificationStatus, result.patronNotificationReason, 'Purchase approval email');
      let message = `${successMessage}${status ? ` Final state: ${statusLabel(status)}.` : ''}${notification.text}${patronNotification.text}`;
      recordReceipt(attempt, `${message} Sign in again to review the committed request.`, Boolean(detail));
      if (!attempt.isCurrent()) return;
      if (!detail) {
        const load = reads.begin('detail');
        try {
          detail = await send(detailPath(snapshot.id), { signal: load.signal });
          if (!load.isCurrent()) return;
        } catch (error) { if (!isAbortError(error) && error.status !== 401) message += ' Details and activity could not refresh.'; }
        finally { reads.finish('detail', load.token); }
      }
      if (!attempt.isCurrent()) return;
      if (detail?.status && detail.status !== status) message = `${successMessage} Final state: ${statusLabel(detail.status)}.${notification.text}${patronNotification.text}`;
      recordReceipt(attempt, `${message} Sign in again to review the committed request.`, Boolean(detail));
      if (detail) {
        onUpdated(detail);
        renderRequest(detail, configurations.get(String(detail.libraryOrgId))?.value || {}, editor?.acceptedVerification(detail, body), retained);
        if (retained) showBibLinkOutcome(message);
      }
      else if (retained) {
        const recovery = `${message} Current details could not refresh. Reload before making another change.`;
        editor.showBibLinkError(recovery, true);
        showBibLinkOutcome(recovery, true);
      } else {
        disposeChildren(); current = Object.freeze({ id: snapshot.id, version: null });
        attempt.mounted.heading(snapshot.title, `${snapshot.libraryOrgName} · Request ${snapshot.id}`);
        showDetailsUnavailable(attempt.mounted, 'The action committed. Reload this request to review current details.');
      }
      host.focusClose(); announce(message, notification.partial || patronNotification.partial ? 'warning' : 'success');
      releaseAttempt(attempt);
      const refreshed = await refreshQueue({ skipDeepLink: true, silent: true });
      if (attempt.isCurrent()) announce(refreshed === false ? `${message} The queue could not refresh.` : message,
        notification.partial || patronNotification.partial || refreshed === false ? 'warning' : 'success');
    } catch (error) {
      if (attempt.outcome === 'committed') {
        if (attempt.isCurrent()) announce(`${successMessage} Details and activity could not refresh.`, 'warning'); return;
      }
      const recordedProviderOutcome = error.response?.providerOutcomeRecorded === true;
      const definiteNoCommit = ['bib_validation_unavailable', 'notification_dependency_unavailable'].includes(error.response?.code);
      const uncertain = !definiteNoCommit && (error.outcomeUnknown === true || !error.status || isAbortError(error) || error.status === 408 || error.status >= 500 ||
        ['request_outcome_unconfirmed', 'hold_outcome_unconfirmed', 'hold_provider_error'].includes(error.response?.code));
      attempt.outcome = recordedProviderOutcome ? 'provider_recorded' : uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      if ((path.endsWith('/action') || path.endsWith('/place-hold')) && error.status === 409 && error.response?.code === 'duplicate_open_request') {
        if (attempt.isCurrent()) await showDuplicateRecovery(attempt, error.response.duplicate, retained); return;
      }
      const holdReview = path.endsWith('/place-hold') && error.status === 409;
      const pickupReview = path.endsWith('/pickup-preference') && Boolean(error.response?.operationId);
      const needsReceipt = uncertain || recordedProviderOutcome || holdReview || pickupReview;
      const message = pickupReview ? error.message || 'Pickup reconciliation is required. Review the live preference before retrying.'
        : recordedProviderOutcome ? 'Polaris returned a hold result, but request finalization was deferred after staff access changed. Review the hold operation with an authorized account.'
          : uncertain ? path.endsWith('/place-hold') ? 'The hold outcome could not be confirmed. Reload the operation before trying again.'
            : 'The request outcome could not be confirmed. Reload before trying again.'
            : error.message || 'The request changed. Review the refreshed version before trying again.';
      if (needsReceipt) recordReceipt(attempt, `${message} Sign in again to check the authoritative result before retrying.`);
      if (!attempt.isCurrent()) return;
      if (error.status === 409 || uncertain || recordedProviderOutcome) {
        const review = await reloadAfterMutation(attempt, retained);
        if (needsReceipt && review?.refreshed === true && review.detailLoaded === true) clearReceipt(attempt);
        if (review?.isCurrent()) {
          const feedback = retained && uncertain && review.detailLoaded
            ? `The response was not confirmed. Current saved state: ${statusLabel(current.status)}. Review the saved BIB before retrying.` : message;
          announce(feedback, 'error');
          if (retained) {
            showBibLinkOutcome(feedback, true);
            editor.showBibLinkError(review.detailLoaded ? feedback : 'Current request details could not be loaded. Reload before retrying the action.', !review.detailLoaded);
          }
        }
        return { outcome: uncertain && !review?.detailLoaded ? 'uncertain' : 'rejected', message, code: error.response?.code };
      } else if (error.status !== 401 && !isAbortError(error)) {
        const message = error.message || 'The request could not be updated.';
        announce(message, 'error');
        return { outcome: 'rejected', message, code: error.response?.code };
      }
    } finally { attempt.pending = false; releaseAttempt(attempt); restore(); }
  }
  function showBibLinkOutcome(message, error = false) {
    const panel = lease?.content.querySelector('.request-workflow-panel');
    if (!panel) return;
    panel.querySelector('.bib-link-outcome')?.remove();
    panel.append(element('p', { className: 'bib-link-outcome', role: error ? 'alert' : 'status' },
      [message, element('span', { className: 'bib-link-draft-status' })]));
    updateBibLinkDraftStatus(editor?.isDirty());
  }
  function updateBibLinkDraftStatus(dirty) {
    const status = lease?.content.querySelector('.bib-link-draft-status');
    const message = dirty ? ' Other request edits remain unsaved.' : '';
    if (status && status.textContent !== message) status.textContent = message;
  }
  async function mutateOperation(snapshot, operation, action, body, draft = null) {
    if (!allowRequestMutation(snapshot, { consumes: draft })) return;
    const attempt = beginAttempt(snapshot, action, body), restore = disableControls(attempt);
    announce(`${action === 'resolve' ? 'Resolving' : 'Reconciling'} hold operation...`);
    try {
      const result = await send(`/api/asap/staff/hold-operations/${operation.id}/${action}`, { method: 'POST', body: attempt.body });
      if (result?.committed !== true || !['updated', 'resolved'].includes(result.code) ||
          typeof result.operationId !== 'string' || result.operationId !== operation.id) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, 'Hold notification');
      const finalState = result.finalStatus ? ` Final state: ${statusLabel(result.finalStatus)}.` : ' Review the refreshed request for final state.';
      const message = action === 'resolve' ? `Hold operation ${result.operationId} resolved as ${body.outcome.replaceAll('_', ' ')}.${finalState}${notification.text}`
        : `Hold operation ${result.operationId} reconciliation recorded.${finalState}${notification.text}`;
      recordReceipt(attempt, `${message} Sign in again to review the committed recovery result.`);
      if (!attempt.isCurrent()) return;
      announce(message, notification.partial ? 'warning' : 'success');
      const review = await reloadAfterMutation(attempt);
      if (review?.refreshed === true && review.detailLoaded === true) clearReceipt(attempt);
      if (review?.isCurrent()) {
        const followup = `${review.refreshed === false ? ' The queue could not refresh.' : ''}${review.detailLoaded ? '' : ' Details could not refresh.'}`;
        announce(`${message}${followup}`, notification.partial || followup ? 'warning' : 'success');
      }
    } catch (error) {
      if (attempt.outcome === 'committed') { if (attempt.isCurrent()) announce('Hold recovery committed. Current details could not refresh.', 'warning'); return; }
      const provider = error.response?.providerOutcomeRecorded === true;
      const uncertain = error.response?.code !== 'hold_resolution_dependency_unavailable' && (error.outcomeUnknown === true || !error.status || isAbortError(error) || error.status === 408 || error.status >= 500 ||
        ['hold_outcome_unconfirmed', 'hold_provider_error'].includes(error.response?.code));
      attempt.outcome = provider ? 'provider_recorded' : uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      const message = provider ? 'Polaris returned a hold result, but request finalization was deferred after staff access changed. Review the hold operation with an authorized account.'
        : uncertain ? 'The hold recovery outcome could not be confirmed. Reload before trying again.'
          : error.message || 'The hold recovery changed. Review the refreshed request before trying again.';
      if (provider || uncertain || error.status === 409) recordReceipt(attempt, `${message} Sign in again to check the authoritative result before retrying.`);
      if (!attempt.isCurrent()) return;
      if (provider || uncertain || error.status === 409) {
        const review = await reloadAfterMutation(attempt);
        if (review?.refreshed === true && review.detailLoaded === true) clearReceipt(attempt);
        if (review?.isCurrent()) announce(message, 'error');
      } else if (error.status !== 401 && !isAbortError(error)) announce(error.message || 'Hold recovery could not be updated.', 'error');
    } finally { attempt.pending = false; releaseAttempt(attempt); restore(); }
  }
  async function showDuplicateRecovery(attempt, duplicate, retained = null) {
    const review = await reloadAfterMutation(attempt, retained);
    if (!review?.isCurrent()) return;
    if (!review.detailLoaded) {
      const message = 'The attempted change was not saved because another open request has this BIB. Current details could not refresh; reload this request before deciding whether to close it.';
      if (retained) {
        editor.showBibLinkError(message, true);
        showBibLinkOutcome(message, true);
      }
      announce(message, 'error'); return;
    }
    if (retained) editor.showBibLinkError('The BIB association and Already own action were not saved because another open request has this BIB. Your selected BIB and other edits remain unsaved.');
    const snapshot = current, mounted = lease, child = workflows;
    const live = () => review.isCurrent() && mounted.isCurrent() && current === snapshot;
    const label = duplicate && typeof duplicate.id === 'string' && /^\d+$/.test(duplicate.id)
      ? `Request ${duplicate.id}: ${duplicate.title || 'Untitled'} (${statusLabel(duplicate.status)}), BIB ${duplicate.bibid || 'unknown'}`
      : 'another open request for this patron and BIB';
    const panel = element('section', { className: 'action-choice duplicate-recovery', 'aria-labelledby': 'duplicate-recovery-title' });
    panel.append(element('h3', { id: 'duplicate-recovery-title', text: 'Duplicate BIB request' }),
      element('p', { text: `The attempted change was not saved. This patron already has ${label}.` }),
      element('p', { text: 'Close this current request as a duplicate, or leave it open to edit or select another BIB.' }));
    if (retained && editor.isDirty()) panel.append(element('p', { text: 'Your unsaved edits and selected BIB are retained. Save or revert those edits before closing this request as a duplicate.' }));
    const edit = element('button', { type: 'button', onclick: () => {
      if (!panel.isConnected || !live()) return;
      endInlineAction(panel);
      panel.remove(); mounted.content.querySelector('.edit-form input[inputmode="numeric"]')?.focus(); announce('The request remains open. Edit it or select another BIB.');
    } }, 'Continue editing');
    const closeDuplicate = element('button', { type: 'button', className: 'secondary-button', onclick: () => {
      if (panel.isConnected && live()) void child.runAction('closeDuplicate');
    } }, 'Close current request as duplicate');
    panel.append(element('div', { className: 'form-actions' }, snapshot.status === 'closed' ? [edit] : [closeDuplicate, edit]));
    const slot = mounted.content.querySelector('.request-workflow-panel');
    if (slot) {
      mounted.content.querySelector('.request-detail-tabs [role="tab"]')?.click();
      slot.append(panel);
    } else mounted.content.querySelector('.action-bar')?.after(panel);
    beginInlineAction(panel, () => edit.click());
    (snapshot.status === 'closed' ? edit : closeDuplicate).focus();
    announce('The attempted change was not saved. Review the duplicate request and choose whether to close this request.', 'error');
  }
  function renderRequest(request, configuration, verified = null, retained = null) {
    if (tabRequestId !== String(request.id)) { tabRequestId = String(request.id); selectedTab = 'details'; }
    disposeChildren();
    drafts = createDraftScope();
    current = Object.freeze({ ...request });
    const snapshot = current, mounted = lease, owner = actor, revision = Symbol('request context');
    renderRevision = revision;
    const scope = drafts;
    const live = () => isPresentationCurrent(mounted, owner) && renderRevision === revision && current === snapshot;
    const activityNavigation = {
      actorRole: owner?.role,
      actorOrganizationId: owner?.organizationId,
      onOpenEmailOperation: typeof onOpenEmailOperation === 'function' ? email => {
        if (!live()) return Promise.resolve(false);
        return onOpenEmailOperation(Object.freeze({ operationReference: email?.operationReference,
          operationsScope: email?.operationsScope, isCurrent: live }));
      } : null
    };
    const footerRoot = element('div', { className: 'request-footer-slot' });
    const panels = new Map(), tabs = new Map();
    let updatePatronResearch = () => {};
    function activateTab(name, focus = false) {
      if (!live() || !panels.has(name)) return;
      selectedTab = name;
      for (const [key, panel] of panels) {
        panel.hidden = key !== name;
        tabs.get(key).setAttribute('aria-selected', String(key === name));
        tabs.get(key).tabIndex = key === name ? 0 : -1;
      }
      if (focus) tabs.get(name).focus();
    }
    const context = Object.freeze({ request: snapshot, actor: owner, root: mounted.content, scope,
      isCurrent: live, isPending: hasPendingMutation,
      showDetails: () => activateTab('details'),
      hasInlineAction: () => inlineActions.size > 0,
      beginInlineAction: (panel, cancel) => { if (live()) beginInlineAction(panel, cancel); },
      endInlineAction: panel => { if (live()) endInlineAction(panel); },
      cancelInlineAction: () => live() && cancelInlineAction(),
      admit: declaration => live() && allowRequestMutation(snapshot, declaration),
      confirm: async (options, draft = null, discardOnly = false) => {
        // Revert owns only this editor; unrelated inline drafts must survive it.
        const allowed = () => live() && (discardOnly
          ? !hasPendingMutation() && draft !== null && editor?.draft() === draft
          : allowRequestMutation(snapshot, { consumes: draft }));
        if (!allowed()) return false;
        const stamp = scope.stamp(), generation = getNavigationGeneration(), selectedScope = getScope();
        const values = controlSnapshot(mounted.content);
        const isCurrent = () => live() && scope.stamp() === stamp && isNavigationCurrent(generation) &&
          getScope() === selectedScope && controlSnapshot(mounted.content) === values && allowed();
        return await confirmAction({ ...options, isCurrent }) && isCurrent();
      },
      mutate: (path, body, message, draft = null) => live() ? mutateRequest(snapshot, path, body, message, draft) : Promise.resolve(),
      completeBibLink: (bibId, draft) => live() ? mutateRequest(snapshot,
        `/api/asap/staff/title-requests/${snapshot.id}/action`,
        { version: snapshot.version, action: 'alreadyOwn', bibid: bibId, staffSelectedBibId: bibId },
        'BIB association and Already own action saved.', draft, true) : Promise.resolve(),
      reloadBibLink: retained => live() ? reloadBibLink(snapshot, retained, live) : Promise.resolve(false),
      mutateOperation: (operation, action, body, draft = null) => live() ? mutateOperation(snapshot, operation, action, body, draft) : Promise.resolve(),
      delete: () => live() ? deleteTitleRequest(snapshot) : Promise.resolve(),
      openCopy: opener => live() ? openCopy(snapshot, context, opener) : Promise.resolve(false)
    });
    editor = createRequestEditor({ context, configuration, polarisLookup, announce, footerRoot,
      closeDetail: () => host.requestClose(), onDirtyChange: dirty => { if (live()) updateBibLinkDraftStatus(dirty); },
      onResearchConfiguration: research => { if (live()) updatePatronResearch(research); },
      verified, retained, request: send });
    workflows = createRequestWorkflows({ context, editor, announce, request: send });
    const status = statusLabel(request.status);
    lease?.heading(request.title, `${request.libraryOrgName} · Request ${request.id}`, {
      label: request.status === 'closed' && request.closeReason ? `${status} · ${closeReasonLabel(request.closeReason)}` : status,
      closed: request.status === 'closed'
    });
    mounted.content.classList.add('title-detail-content');
    const body = document.createDocumentFragment();
    const meta = element('div', { className: 'detail-meta' }, [
      element('span', { text: `Submitted ${dateTime(request.created)}` }),
      element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
    ]);
    body.append(element('div', { className: 'request-heading-context' }, [meta, workflows.buildActions()]));
    const detailsPanel = element('section', { className: 'request-details-panel' });
    detailsPanel.append(element('div', { className: 'request-workflow-panel' }));
    const patronContext = element('div', { className: 'patron-context' });
    const patronName = [request.nameFirst, request.nameLast].filter(Boolean).join(' ');
    const patronValue = element('span', { className: 'patron-name-actions' }, [researchRecordValue(patronName, request.research, 'patron')]);
    if (typeof request.barcode === 'string' && request.barcode.trim() && onNewSuggestion) {
      patronValue.append(element('button', { type: 'button', className: 'grid-new-suggestion',
        'aria-label': `New suggestion for patron ${request.barcode} from request ${request.id}`,
        onclick: event => {
          if (live() && !hasPendingMutation()) onNewSuggestion(request.barcode, event.currentTarget, request.id);
        }
      }, [icon('plus'), element('span', { className: 'sr-only', text: 'New suggestion' })]));
    }

    if (request.capabilities && request.capabilities.blockingReason) {
      const reason = request.capabilities.blockingReason;
      detailsPanel.append(element('p', {
        className: ['pickup_reconciliation_required', 'hold_operation_incomplete'].includes(reason) ? 'blocked-callout' : 'identifier-lock-help field-help',
        text: reason === 'pickup_reconciliation_required'
          ? 'Pickup preference needs reconciliation. Review the live preference before continuing; an uncertain provider write will not be repeated.'
          : reason === 'hold_operation_incomplete'
          ? 'Workflow-changing edits are blocked while hold placement needs recovery.'
          : reason === 'hold_history_retained'
          ? "This request has a previous hold placement, so it can't be reopened or have its Identifier or BIB ID changed."
          : request.status === 'closed'
          ? "This request is closed, so its Identifier and BIB ID can't be changed. Reopen it first if that option is available."
          : request.status === 'hold_placed'
          ? 'This request has reached the hold stage. Its Identifier and BIB ID can no longer be changed.'
          : reason === 'identifier_locked_by_stage' && ['suggestion', 'outstanding_purchase', 'pending_hold'].includes(request.status)
          // The workflow policy only locks identifiers in a pre-placement
          // stage when placed-hold protection remains, including legacy holds.
          ? "A previous hold placement prevents changes to this request's Identifier and BIB ID."
          : "Changes to this request's Identifier and BIB ID are locked by its current workflow protections."
      }));
    }

    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'Patron', patronName).replaceChildren(patronValue);
    const barcodeValue = addDetail(details, 'Barcode', request.barcode);
    barcodeValue.replaceChildren(researchRecordValue(request.barcode, request.research, 'patron'));
    function updateRecordLink(container, value, research) {
      if (!live() || !container.isConnected) return;
      const currentLink = container.firstElementChild;
      if (!currentLink) return;
      const next = researchRecordValue(value, research, 'patron');
      if (currentLink.tagName === 'A' && next.tagName !== 'A') return;
      if (currentLink.tagName === next.tagName) {
        if (next.tagName === 'A') {
          if (currentLink.getAttribute('href') !== next.getAttribute('href')) {
            currentLink.setAttribute('href', next.getAttribute('href'));
          }
          currentLink.setAttribute('target', '_blank');
          currentLink.setAttribute('rel', 'noopener noreferrer');
        }
        if (currentLink.textContent !== next.textContent) currentLink.textContent = next.textContent;
        return;
      }
      currentLink.replaceWith(next);
    }
    updatePatronResearch = research => {
      if (!live() || !researchRecordUrl(research, 'patron')) return;
      updateRecordLink(patronValue, patronName, research);
      updateRecordLink(barcodeValue, request.barcode, research);
    };
    const email = text(request.email, '');
    const emailCell = addDetail(details, 'Email', email);
    if (email) {
      const encodedEmail = encodeURIComponent(email);
      const separator = encodedEmail.lastIndexOf('%40');
      const recipient = separator < 0 ? encodedEmail
        : `${encodedEmail.slice(0, separator)}@${encodedEmail.slice(separator + 3)}`;
      emailCell.replaceChildren(element('a', { href: `mailto:${recipient}`, text: email }));
    }
    const pickupCell = addDetail(details, 'Recorded ASAP pickup', request.preferredPickupBranchName || request.preferredPickupBranchId);
    const pickupHelp = element('details', { className: 'recorded-pickup-help' });
    const pickupHelpToggle = element('summary', { className: 'recorded-pickup-help-toggle', 'aria-label': 'Pickup details' }, [icon('info-circle'), 'Pickup details']);
    let pickupEscapeHeld = false;
    pickupHelp.append(pickupHelpToggle, element('div', { className: 'recorded-pickup-help-content' }, element('p', {
      text: "The recorded location is a historical snapshot. Each unfilled request uses the patron's live Polaris pickup preference when its hold is placed. Updating pickup changes the patron's account default; existing holds keep their recorded destination."
    })));
    pickupHelp.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      if (pickupEscapeHeld) { event.preventDefault(); event.stopPropagation(); return; }
      if (!pickupHelp.open) return;
      event.preventDefault(); event.stopPropagation(); pickupEscapeHeld = true; pickupHelp.open = false; pickupHelpToggle.focus();
    });
    pickupHelp.addEventListener('keyup', event => {
      if (event.key !== 'Escape' || !pickupEscapeHeld) return;
      event.preventDefault(); event.stopPropagation(); pickupEscapeHeld = false;
    });
    pickupCell.append(element('small', { className: 'recorded-pickup-summary', text: 'Historical; holds use live Polaris pickup preference.' }), pickupHelp);
    patronContext.prepend(details);
    body.append(patronContext);
    detailsPanel.append(element('p', { className: 'field-help request-metadata',
      text: [`Identifier check: ${text(request.isbnCheckStatus)}`, `Phase entered ${dateTime(request.phaseEnteredAt)}`,
        request.status === 'closed' ? `Close reason: ${closeReasonLabel(request.closeReason)}` : ''].filter(Boolean).join(' · ') }));

    if (request.workflowTags && request.workflowTags.length) {
      const tags = element('div', { className: 'tags', 'aria-label': 'Workflow tags' });
      for (const tag of request.workflowTags) tags.append(element('span', { className: 'tag', text: tag }));
      detailsPanel.append(tags);
    }
    const related = element('section', { className: 'related-requests', 'aria-label': 'Related title requests' });
    if (request.relatedRequests && Number.isInteger(request.relatedRequests.count)) {
      const visibleCounts = (request.relatedRequests.statusAndLibraryCounts || [])
        .filter(item => getScope() === 'all' || String(item.libraryOrgId) === getScope());
      const visibleCount = visibleCounts.reduce((total, item) => total + item.count, 0);
      related.append(element('p', { text: visibleCount === 0
        ? 'No related title requests are visible in your authorized scope.'
        : `${visibleCount} related title request${visibleCount === 1 ? '' : 's'} in your authorized scope.` }));
      if (visibleCount > 0) {
        const counts = element('ul');
        for (const item of visibleCounts) {
          counts.append(element('li', { text: `${item.libraryOrgName || `Library ${item.libraryOrgId}`} · ${statusLabel(item.status)}: ${item.count}` }));
        }
        related.append(counts);
      }
    } else related.append(element('p', { text: 'Related request information is unavailable in your authorized scope.' }));
    if (request.workflowContext && request.status !== 'closed') {
      detailsPanel.append(element('p', { className: 'workflow-context',
        text: request.status === 'suggestion'
          ? `Suggestion timeout: ${timeoutLabel(request.workflowContext.outstandingTimeoutEnabled, request.workflowContext.outstandingTimeoutDays)}`
          : request.status === 'outstanding_purchase'
            ? `Auto promotion when a BIB is available: ${request.workflowContext.autoPromote ? 'On' : 'Off'}`
            : request.status === 'pending_hold'
              ? `Pending hold timeout: ${timeoutLabel(request.workflowContext.pendingHoldTimeoutEnabled, request.workflowContext.pendingHoldTimeoutDays)}`
              : `Hold pickup timeout: ${timeoutLabel(request.workflowContext.holdPickupTimeoutEnabled, request.workflowContext.holdPickupTimeoutDays)}`
      }));
    }
    detailsPanel.append(editor.build());
    if (request.holdOperation) detailsPanel.append(workflows.buildHold());
    const tablist = element('div', { className: 'request-detail-tabs', role: 'tablist', 'aria-label': 'Request detail sections' });
    const middle = element('div', { className: 'request-middle' });
    for (const [name, label, panel] of [['details', 'Details', detailsPanel], ['activity', 'Activity', renderActivity(request.activity, activityNavigation)], ['related', 'Related requests', related]]) {
      panel.id = `request-${request.id}-${name}`;
      panel.setAttribute('role', 'tabpanel'); panel.tabIndex = 0;
      panel.setAttribute('aria-labelledby', `${panel.id}-tab`);
      const tab = element('button', { id: `${panel.id}-tab`, type: 'button', role: 'tab',
        'aria-controls': panel.id, onclick: () => activateTab(name) }, label);
      tab.addEventListener('keydown', event => {
        if (!live() || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const names = [...tabs.keys()], index = names.indexOf(name);
        activateTab(event.key === 'Home' ? names[0] : event.key === 'End' ? names.at(-1)
          : names[(index + (event.key === 'ArrowRight' ? 1 : -1) + names.length) % names.length], true);
      });
      tabs.set(name, tab); panels.set(name, panel); tablist.append(tab); middle.append(panel);
    }
    footerRoot.addEventListener('click', event => {
      // Expose invalid fields before native form validation/focus from another tab.
      if (event.target.closest('button[type="submit"]') && !mounted.content.querySelector('.edit-form')?.checkValidity()) activateTab('details');
    });
    // Context can wrap substantially at native browser zoom. Keep it in the
    // single scrolling pane so it cannot push the save footer out of view.
    middle.prepend(...body.childNodes, tablist);
    body.append(middle, footerRoot);
    lease.content.replaceChildren(body);
    activateTab(selectedTab);
    editor.restoreBibLink();
    void editor.loadResearch();
  }

  function renderActivity(activity, navigation = {}) {
    const section = element('section', { className: 'request-activity', 'aria-label': 'Request activity' });
    section.append(element('h3', { text: 'Activity' }));
    if (!Array.isArray(activity) || activity.length === 0) {
      section.append(element('p', { text: 'No recorded activity.' }));
      return section;
    }
    const list = element('ol');
    const eventId = value => typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
      (value.length < 19 || value.length === 19 && value <= '9223372036854775807') ? BigInt(value)
      : Number.isSafeInteger(value) && value > 0 ? BigInt(value) : null;
    const sourceOf = item => typeof item?.source === 'string' && item.source ? item.source : 'request_event';
    const displayId = item => typeof item?.id === 'string' ? item.id
      : Number.isSafeInteger(item?.id) ? String(item.id) : '';
    const ordinal = (left, right) => left < right ? -1 : left > right ? 1 : 0;
    const ordered = activity.map((value, index) => {
      const item = value && typeof value === 'object' ? value : {};
      const created = typeof item.created === 'string' ? item.created : '';
      const timestamp = Number.isFinite(Date.parse(created)) ? Date.parse(created) : -Infinity;
      // SQL timestamps retain seven fractional digits; Date.parse keeps only
      // milliseconds. Compare the remaining ticks before treating times as tied.
      const fraction = created.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1] || '';
      return { item, index, source: sourceOf(item), identity: `${sourceOf(item)}:${displayId(item)}`,
        timestamp, ticks: fraction.padEnd(7, '0').slice(3, 7), id: eventId(item.id), rawId: displayId(item) };
    });
    ordered.sort((a, b) => {
      if (a.timestamp !== b.timestamp) return a.timestamp > b.timestamp ? -1 : 1;
      if (a.timestamp !== -Infinity && a.ticks !== b.ticks) return a.ticks > b.ticks ? -1 : 1;
      const sourceOrder = ordinal(a.source, b.source);
      if (sourceOrder) return sourceOrder;
      if (a.id !== null && b.id !== null && a.id !== b.id) return a.id > b.id ? -1 : 1;
      if ((a.id === null) !== (b.id === null)) return a.id === null ? 1 : -1;
      if (a.id === null && b.id === null) {
        const rawIdOrder = ordinal(a.rawId, b.rawId);
        if (rawIdOrder) return rawIdOrder;
      }
      // Original indices make unknown or duplicate DTO identities a total,
      // stable order without mutating the frozen API array.
      return a.index - b.index;
    });
    const timestampLabels = { transition: 'Recorded', created: 'Created', last_attempt: 'Last attempt',
      sent: 'Sent', suppressed: 'Suppressed', recorded: 'Recorded' };
    const deliveryModeLabels = { file: 'File capture; no live email sent', postmark: 'Postmark',
      postmark_sandbox: 'Postmark sandbox', non_delivery: 'Non-delivery transport; no live email sent', unknown: 'Delivery mode unknown' };
    const isCanonicalReference = value => typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
      (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
    const isCanonicalLibrary = value => typeof value === 'string' && /^[1-9]\d{0,9}$/.test(value) &&
      (value.length < 10 || value <= '2147483647') && value !== '1';
    const canNavigate = email => typeof navigation.onOpenEmailOperation === 'function' &&
      isCanonicalReference(email?.operationReference) && isCanonicalLibrary(email?.operationsScope) &&
      (navigation.actorRole === 'super_admin' || navigation.actorRole === 'admin' &&
        String(navigation.actorOrganizationId) === email.operationsScope);
    for (const entry of ordered) {
      const { item, source, identity } = entry;
      if (source === 'email_event' || source === 'email_summary') {
        const email = item.email && typeof item.email === 'object' ? item.email : {};
        const itemType = text(email.messageType, 'Email type not recorded');
        const outcome = text(email.outcomeLabel, 'Outcome not recorded');
        const reference = typeof email.operationReference === 'string' ? email.operationReference : '';
        const timestampKind = typeof email.timestampKind === 'string' ? email.timestampKind : '';
        const timestampLabel = timestampLabels[timestampKind] || 'Recorded time (meaning unavailable)';
        const timestampValid = typeof item.created === 'string' && Number.isFinite(Date.parse(item.created));
        const timeValue = timestampValid ? dateTime(item.created) : 'Time not recorded';
        const deliveryMode = deliveryModeLabels[email.deliveryMode] || 'Delivery mode unknown';
        const row = element('li', { className: 'request-activity-email', 'data-source': source,
          'data-activity-id': identity, 'data-event-id': displayId(item) }, [
          element('strong', { text: itemType }),
          element('span', { className: 'request-email-outcome', text: outcome })
        ]);
        const fields = element('dl', { className: 'request-email-fields' });
        const addEmailField = (label, value) => fields.append(element('div', {}, [
          element('dt', { text: label }), element('dd', { text: value })
        ]));
        addEmailField('Intended recipient', text(email.intendedRecipient, 'Recipient not recorded'));
        addEmailField('Delivery mode', deliveryMode);
        if (email.reason !== null && email.reason !== undefined && String(email.reason).trim()) {
          addEmailField('Recorded reason', String(email.reason));
        } else if (['suppressed', 'failed_no_send', 'ambiguous', 'lease_expired_ambiguous',
          'recorded_failed', 'recorded_suppressed'].includes(email.outcome)) {
          addEmailField('Recorded reason', 'Reason not recorded');
        }
        row.append(fields);
        row.append(element('small', { className: 'request-email-recorded-time' }, [
          element('span', { text: `${timestampLabel}: ` }),
          timestampValid ? element('time', { datetime: item.created, text: timeValue }) : element('span', { text: timeValue })
        ]));
        if (email.isRecordedSummary === true) {
          row.append(element('p', { className: 'request-email-history-note',
            text: 'Recorded summary; earlier email activity may be incomplete.' }));
        }
        const referenceLine = element('p', { className: 'request-email-reference' });
        if (reference) {
          referenceLine.append(element('span', { text: 'Email operations reference: ' }), element('code', { text: reference }));
          if (canNavigate(email)) {
            referenceLine.append(element('button', { type: 'button', className: 'secondary-button request-email-reference-link',
              'aria-label': `Open email operation ${reference}`, onclick: () => {
                void navigation.onOpenEmailOperation(email);
              } }, 'View in Email operations'));
          }
        } else referenceLine.append(element('span', { text: 'Email operations reference not recorded.' }));
        row.append(referenceLine);
        list.append(row);
        continue;
      }
      const type = text(item.eventType, 'Event').replaceAll('_', ' ');
      const actor = item.actorName || (item.actorType === 'system' ? 'System' : 'Actor not recorded');
      list.append(element('li', { 'data-source': source, 'data-activity-id': identity, 'data-event-id': displayId(item) }, [
        element('strong', { text: type }),
        element('span', { text: item.message ? ` ${item.message}` : '' }),
        element('small', { text: `${actor} · ${dateTime(item.created)}` })
      ]));
    }
    section.append(list);
    return section;
  }

  return { open, prepare, present, close, invalidate, invalidateConfiguration,
    isDirty: () => Boolean(lease?.isCurrent() && drafts.isDirty()), hasPendingMutation,
    inspectDeparture: () => ({ owner: renderRevision || lease, dirty: Boolean(lease?.isCurrent() && drafts.isDirty()),
      stamp: JSON.stringify([drafts.revision(), lease ? controlSnapshot(lease.content) : null]), blocked: disposed || hasPendingMutation(),
      message: 'The workflow action is in progress. Wait for its authoritative result before navigating away.',
      confirmMessage: 'Discard unsaved request changes and navigate away?' }),
    setStaff() { configurations.clear(); },
    signedOut() { lease?.release({ navigation: true }); invalidate(); configurations.clear(); },
    dispose() { lease?.release({ navigation: true }); disposed = true; invalidate(); drafts.dispose(); configurations.clear(); }
  };
}
