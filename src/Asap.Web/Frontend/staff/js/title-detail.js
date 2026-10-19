import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';
import { createRequestEditor } from './request-editor.js';
import { createRequestWorkflows } from './request-workflows.js';
import { unconfirmedResponseError, notificationOutcome, isCommittedRequestResponse } from './mutation-outcome.js';
import { element, icon, statusLabel, dateTime, text, closeReasonLabel, timeoutLabel, addDetail } from './ui.js';

export function createTitleDetailController({ host, sessionIdentity, polarisLookup, copyCreation, announce,
  beforeOpen, isNavigationCurrent, getNavigationGeneration, getScope, onAlign, onOpened, onUpdated = () => {}, beforeClose, onClosed,
  getFocusReturn, refreshQueue, queueSequence, rememberOpened, forgetUnavailable, onReceipt, clearReceipt,
  refreshCurrentStaff = async () => null,
  request: send = authorizedJson }) {
  const reads = createLatestLoad(), configurations = new Map();
  let lease = null, actor = null, current = null, returnFocus = null, activeAttempt = null;
  let drafts = createDraftScope(), renderRevision = null, editor = null, workflows = null, copyParent = null, disposed = false, targetGeneration = 0;

  function isPresentationCurrent(mounted, owner) {
    return !disposed && mounted?.isCurrent() && sessionIdentity.isCurrent(owner);
  }
  function isCurrentSelection(snapshot) { return isPresentationCurrent(lease, actor) && current?.id === snapshot.id; }
  function hasPendingMutation() { return Boolean(activeAttempt?.pending && sessionIdentity.isCurrent(activeAttempt.owner) || copyParent && copyCreation.hasPendingMutation(copyParent)); }
  function allowRequestMutation(snapshot, declaration) {
    if (!isCurrentSelection(snapshot) || current.version !== snapshot.version || hasPendingMutation()) return false;
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
  function disposeChildren() {
    renderRevision = null;
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
    const mounted = host.acquire({ dispose: disposeMounted, onClose: close,
      onEscape: () => { if (!hasPendingMutation() && workflows?.escape()) return; close(); } });
    lease = mounted; actor = owner;
    returnFocus = opener || document.activeElement; drafts = createDraftScope();
    for (const name of ['input', 'change']) mounted.content.addEventListener(name, event => {
      if (isPresentationCurrent(mounted, owner) && mounted.content.contains(event.target)) drafts.touch();
    });
    onOpened(request, options); renderRequest(request, configuration); mounted.show();
    announce(`Opened ${request.title}.`); rememberOpened(request.id); return true;
  }

  async function open(id, opener = null, options = {}) {
    if (disposed || !sessionIdentity.actor()) return false;
    const departure = beforeOpen(options);
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
      const ticket = alignment.commit();
      return ticket !== false && present(target, opener, options, ticket);
    } catch (error) {
      if (departure.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        if (options.authoritativeRefresh && lease?.isCurrent()) {
          disposeChildren(); current = Object.freeze({ id: String(id), version: null });
          lease.heading(priorTitle, `Request ${id}`);
          lease.content.replaceChildren(element('p', { text: 'Current details could not refresh. Reload this request before performing another action.' })); lease.show();
        }
        announce(error.status === 404 ? 'That request is no longer available.' : error.message, 'error');
        if (options.fromRecent && error.status === 404) forgetUnavailable(String(id));
      }
      return false;
    }
  }
  function close(options = {}) {
    if (hasPendingMutation() && !options.force && !options.preserveMutation) {
      announce('The workflow action is in progress. Wait for its authoritative result before closing.', 'warning'); host.focusClose(); return false;
    }
    if (!options.force && !options.guarded && !options.preserveMutation && !beforeClose(options)) return false;
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
    const controls = [...attempt.mounted.content.querySelectorAll('input, select, textarea, button')].map(control => ({ control, disabled: control.disabled }));
    for (const item of controls) item.control.disabled = true;
    return () => { for (const item of controls) if (attempt.mounted.isCurrent() && item.control.isConnected) item.control.disabled = item.disabled; };
  }
  function recordReceipt(attempt, message, detailAvailable = false) {
    onReceipt(message, attempt.owner, attempt, { detailAvailable, queueSequence: queueSequence() });
  }
  function releaseAttempt(attempt) { if (activeAttempt === attempt) activeAttempt = null; }
  async function reloadAfterMutation(attempt) {
    const refreshed = await refreshQueue({ skipDeepLink: true, silent: true });
    if (!attempt.isCurrent()) return null;
    const detailLoaded = await open(attempt.snapshot.id, null, { authoritativeRefresh: true });
    const replacement = lease;
    return { refreshed, detailLoaded,
      isCurrent: () => isPresentationCurrent(replacement, attempt.owner) && (detailLoaded || replacement === attempt.mounted) &&
        isNavigationCurrent(attempt.generation + (detailLoaded ? 1 : 0)) && current?.id === attempt.snapshot.id };
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
  async function mutateRequest(snapshot, path, body, successMessage, draft = null) {
    if (!allowRequestMutation(snapshot, { consumes: draft })) return;
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
        renderRequest(detail, configurations.get(String(detail.libraryOrgId))?.value || {}, editor?.acceptedVerification(detail, body));
      }
      else { disposeChildren(); current = Object.freeze({ id: snapshot.id, version: null }); attempt.mounted.content.replaceChildren(element('p', { text: 'The action committed. Reload this request to review current details.' })); }
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
        if (attempt.isCurrent()) await showDuplicateRecovery(attempt, error.response.duplicate); return;
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
        const review = await reloadAfterMutation(attempt);
        if (needsReceipt && review?.refreshed === true && review.detailLoaded === true) clearReceipt(attempt);
        if (review?.isCurrent()) announce(message, 'error');
      } else if (error.status !== 401 && !isAbortError(error)) announce(error.message || 'The request could not be updated.', 'error');
    } finally { attempt.pending = false; releaseAttempt(attempt); restore(); }
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
  async function showDuplicateRecovery(attempt, duplicate) {
    const review = await reloadAfterMutation(attempt);
    if (!review?.isCurrent()) return;
    if (!review.detailLoaded) {
      announce('The attempted change was not saved because another open request has this BIB. Current details could not refresh; reload this request before deciding whether to close it.', 'error'); return;
    }
    const snapshot = current, mounted = lease, child = workflows;
    const live = () => review.isCurrent() && mounted.isCurrent() && current === snapshot;
    const label = duplicate && typeof duplicate.id === 'string' && /^\d+$/.test(duplicate.id)
      ? `Request ${duplicate.id}: ${duplicate.title || 'Untitled'} (${statusLabel(duplicate.status)}), BIB ${duplicate.bibid || 'unknown'}`
      : 'another open request for this patron and BIB';
    const panel = element('section', { className: 'action-choice duplicate-recovery', 'aria-labelledby': 'duplicate-recovery-title' });
    panel.append(element('h3', { id: 'duplicate-recovery-title', text: 'Duplicate BIB request' }),
      element('p', { text: `The attempted change was not saved. This patron already has ${label}.` }),
      element('p', { text: 'Close this current request as a duplicate, or leave it open to edit or select another BIB.' }));
    const edit = element('button', { type: 'button', onclick: () => {
      if (!panel.isConnected || !live()) return;
      panel.remove(); mounted.content.querySelector('.edit-form input[inputmode="numeric"]')?.focus(); announce('The request remains open. Edit it or select another BIB.');
    } }, 'Continue editing');
    const closeDuplicate = element('button', { type: 'button', className: 'secondary-button', onclick: () => {
      if (panel.isConnected && live()) void child.runAction('closeDuplicate');
    } }, 'Close current request as duplicate');
    panel.append(element('div', { className: 'form-actions' }, snapshot.status === 'closed' ? [edit] : [closeDuplicate, edit]));
    mounted.content.querySelector('.action-bar')?.after(panel); (snapshot.status === 'closed' ? edit : closeDuplicate).focus();
    announce('The attempted change was not saved. Review the duplicate request and choose whether to close this request.', 'error');
  }
  function renderRequest(request, configuration, verified = null) {
    disposeChildren();
    drafts = createDraftScope();
    current = Object.freeze({ ...request });
    const snapshot = current, mounted = lease, owner = actor, revision = Symbol('request context');
    renderRevision = revision;
    const scope = drafts;
    const live = () => isPresentationCurrent(mounted, owner) && renderRevision === revision && current === snapshot;
    const context = Object.freeze({ request: snapshot, actor: owner, root: mounted.content, scope,
      isCurrent: live, isPending: hasPendingMutation,
      admit: declaration => live() && allowRequestMutation(snapshot, declaration),
      confirm: (message, draft = null) => live() && allowRequestMutation(snapshot, { consumes: draft }) && window.confirm(message) && live(),
      mutate: (path, body, message, draft = null) => live() ? mutateRequest(snapshot, path, body, message, draft) : Promise.resolve(),
      mutateOperation: (operation, action, body, draft = null) => live() ? mutateOperation(snapshot, operation, action, body, draft) : Promise.resolve(),
      delete: () => live() ? deleteTitleRequest(snapshot) : Promise.resolve(),
      openCopy: opener => live() ? openCopy(snapshot, context, opener) : Promise.resolve(false)
    });
    editor = createRequestEditor({ context, configuration, polarisLookup, announce, verified, request: send });
    workflows = createRequestWorkflows({ context, editor, announce, request: send });
    lease?.heading(request.title, `${request.libraryOrgName} · Request ${request.id}`);
    const body = document.createDocumentFragment();
    const meta = element('div', { className: 'detail-meta' }, [
      element('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}`, text: statusLabel(request.status) }),
      element('span', { text: `Phase entered ${dateTime(request.phaseEnteredAt)}` }),
      element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
    ]);
    body.append(meta, workflows.buildActions());

    if (request.capabilities && request.capabilities.blockingReason) {
      body.append(element('p', {
        className: 'blocked-callout',
        text: request.capabilities.blockingReason === 'pickup_reconciliation_required'
          ? 'Pickup preference needs reconciliation. Review the live preference before continuing; an uncertain provider write will not be repeated.'
          : request.capabilities.blockingReason === 'hold_operation_incomplete'
          ? 'Workflow-changing edits are blocked while hold placement needs recovery.'
          : request.capabilities.blockingReason === 'hold_history_retained'
          ? 'Reopen is unavailable because placed-hold history has no confirmed external reversal.'
          : 'Identifier and BIB changes are locked by this request’s placement history.'
      }));
    }

    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'Patron', [request.nameFirst, request.nameLast].filter(Boolean).join(' '));
    addDetail(details, 'Barcode', request.barcode);
    addDetail(details, 'Email', request.email);
    addDetail(details, 'Format', request.formatLabel || request.format);
    addDetail(details, 'Identifier', request.identifier);
    addDetail(details, 'BIB ID', request.bibid);
    addDetail(details, 'Publication', request.publication);
    addDetail(details, 'Pickup', request.preferredPickupBranchName || request.preferredPickupBranchId);
    addDetail(details, 'Identifier check', request.isbnCheckStatus);
    if (request.status === 'closed') addDetail(details, 'Close reason', closeReasonLabel(request.closeReason));
    body.append(details);

    if (request.workflowTags && request.workflowTags.length) {
      const tags = element('div', { className: 'tags', 'aria-label': 'Workflow tags' });
      for (const tag of request.workflowTags) tags.append(element('span', { className: 'tag', text: tag }));
      body.append(tags);
    }
    if (request.relatedRequests && Number.isInteger(request.relatedRequests.count)) {
      const visibleCounts = (request.relatedRequests.statusAndLibraryCounts || [])
        .filter(item => getScope() === 'all' || String(item.libraryOrgId) === getScope());
      const visibleCount = visibleCounts.reduce((total, item) => total + item.count, 0);
      const related = element('section', { className: 'related-requests', 'aria-label': 'Related title requests' });
      related.append(element('h3', { text: 'Related title requests' }));
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
      body.append(related);
    }
    if (request.workflowContext && request.status !== 'closed') {
      body.append(element('p', { className: 'workflow-context',
        text: request.status === 'suggestion'
          ? `Suggestion timeout: ${timeoutLabel(request.workflowContext.outstandingTimeoutEnabled, request.workflowContext.outstandingTimeoutDays)}`
          : request.status === 'outstanding_purchase'
            ? `Auto promotion when a BIB is available: ${request.workflowContext.autoPromote ? 'On' : 'Off'}`
            : request.status === 'pending_hold'
              ? `Pending hold timeout: ${timeoutLabel(request.workflowContext.pendingHoldTimeoutEnabled, request.workflowContext.pendingHoldTimeoutDays)}`
              : `Hold pickup timeout: ${timeoutLabel(request.workflowContext.holdPickupTimeoutEnabled, request.workflowContext.holdPickupTimeoutDays)}`
      }));
    }
    body.append(editor.build());
    body.append(renderActivity(request.activity));
    body.append(element('section', { className: 'research-section', hidden: 'hidden' }));
    if (request.holdOperation) body.append(workflows.buildHold());
    lease.content.replaceChildren(body);
    void editor.loadResearch();
  }

  function renderActivity(activity) {
    const section = element('section', { className: 'request-activity', 'aria-label': 'Request activity' });
    section.append(element('h3', { text: 'Activity' }));
    if (!Array.isArray(activity) || activity.length === 0) {
      section.append(element('p', { text: 'No recorded activity.' }));
      return section;
    }
    const list = element('ol');
    for (const item of activity) {
      const type = text(item.eventType, 'Event').replaceAll('_', ' ');
      const actor = item.actorName || (item.actorType === 'system' ? 'System' : 'Actor not recorded');
      list.append(element('li', { 'data-event-id': String(item.id) }, [
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
    inspectDeparture: () => ({ owner: renderRevision || lease, dirty: Boolean(lease?.isCurrent() && drafts.isDirty()), stamp: drafts.stamp(), blocked: disposed || hasPendingMutation(),
      message: 'The workflow action is in progress. Wait for its authoritative result before navigating away.',
      confirmMessage: 'Discard unsaved request changes and navigate away?' }),
    setStaff() { configurations.clear(); },
    signedOut() { lease?.release({ navigation: true }); invalidate(); configurations.clear(); },
    dispose() { lease?.release({ navigation: true }); disposed = true; invalidate(); drafts.dispose(); configurations.clear(); }
  };
}
