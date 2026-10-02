import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { createDraftScope } from './draft-scope.js';
import { unconfirmedResponseError, notificationOutcome, isCommittedRequestResponse } from './mutation-outcome.js';
import { element, icon, commandButton, statusLabel, dateTime, timeoutLabel, addDetail, labeledInput } from './ui.js';

export function createCopyDetailController({ host, sessionIdentity, announce, beforeOpen, isNavigationCurrent,
  onAlign, onOpened, beforeClose, onClosed, getFocusReturn, refreshQueue, onReceipt, clearReceipt,
  getNavigationGeneration = () => 0, request: send = authorizedJson }) {
  const reads = createLatestLoad();
  let lease = null, current = null, actor = null, returnFocus = null, activeAttempt = null;
  let drafts = createDraftScope(), forms = new WeakMap(), disposed = false;

  function isPresentationCurrent(mounted, owner) { return !disposed && mounted?.isCurrent() && sessionIdentity.isCurrent(owner); }
  function isCurrentDialogSelection(request) {
    return isPresentationCurrent(lease, actor) && host.isOpen() && current?.id === request.id;
  }
  function isCurrentDialogRequest(request) { return isCurrentDialogSelection(request) && current.version === request.version; }
  function invalidate() { reads.begin('detail').abort(); reads.begin('assignment-candidates').abort(); }
  function resetDrafts() {
    reads.begin('assignment-candidates').abort(); drafts.dispose(); drafts = createDraftScope(); forms = new WeakMap();
  }
  function disposeMounted() {
    invalidate(); drafts.dispose(); current = null; actor = null; returnFocus = null; activeAttempt = null; lease = null;
  }

  async function open(id, opener = null, options = {}) {
    if (disposed || !sessionIdentity.actor()) return false;
    const ticket = beforeOpen(options);
    if (ticket === false || ticket === null) return false;
    const owner = sessionIdentity.preferences();
    const prior = current;
    const mounted = host.acquire({ dispose: disposeMounted, onClose: close });
    lease = mounted; actor = owner; current = null; returnFocus = opener || document.activeElement;
    drafts = createDraftScope();
    const load = reads.begin('detail');
    announce('Loading additional-copy details...');
    try {
      const result = await send(`/api/asap/staff/additional-copies/${encodeURIComponent(id)}`, { signal: load.signal });
      if (!load.isCurrent() || !isPresentationCurrent(mounted, owner) || !isNavigationCurrent(ticket)) return false;
      if (!await onAlign(result, options, owner, ticket)) return false;
      if (!load.isCurrent() || !isPresentationCurrent(mounted, owner) || !isNavigationCurrent(ticket)) return false;
      onOpened(result, options);
      renderAdditionalCopy(result); mounted.show();
      mounted.content.addEventListener('input', () => drafts.touch());
      mounted.content.addEventListener('change', () => drafts.touch());
      announce(`Opened additional-copy task ${result.id}.`);
      return true;
    } catch (error) {
      if (load.isCurrent() && isPresentationCurrent(mounted, owner) && isNavigationCurrent(ticket) && !isAbortError(error) && error.status !== 401) {
        if (options.authoritativeRefresh) {
          current = Object.freeze({ id: String(id), version: null });
          mounted.heading(prior?.title || `Additional copy ${id}`, `Additional copy ${id}`);
          mounted.content.replaceChildren(element('p', { text: 'Current details could not refresh. Reload this task before performing another action.' })); mounted.show();
        }
        announce(error.status === 404 ? 'That additional-copy task is no longer available.' : error.message, 'error');
      }
      return false;
    } finally { reads.finish('detail', load.token); }
  }

  function allowRequestMutation(request, declaration) {
    if (!isCurrentDialogRequest(request) || activeAttempt) return false;
    const admission = drafts.admit(declaration);
    if (!admission.allowed) announce('Finish or cancel the current request changes before performing another action.', 'warning');
    return admission.allowed;
  }
  function confirmCurrent(request, type, message) {
    return allowRequestMutation(request, { consumes: null }) && window.confirm(message) && isCurrentDialogRequest(request);
  }
  function trackDialogFormDraft(form) {
    const baseline = [...form.querySelectorAll('input, select, textarea')].map(control => ({ control, value: control.value }));
    const draft = drafts.register({ root: form, isDirty: () => baseline.some(item => item.control.value !== item.value) });
    forms.set(form, draft); return draft;
  }
  function cancelDialogFormDraft(form, target) {
    if (!form.isConnected || activeAttempt || !isPresentationCurrent(lease, actor)) return;
    drafts.release(forms.get(form)); form.remove(); if (target?.isConnected) target.focus();
    announce('Unsaved request changes discarded.');
  }
  function disableControls(mounted) {
    const controls = [...mounted.content.querySelectorAll('input, select, textarea, button')].map(control => ({ control, disabled: control.disabled }));
    for (const item of controls) item.control.disabled = true;
    return () => { for (const item of controls) if (item.control.isConnected && mounted.isCurrent()) item.control.disabled = item.disabled; };
  }

  async function mutateAdditionalCopy(snapshot, operation, successMessage, extra = {}, draft = null) {
    if (!allowRequestMutation(snapshot, { consumes: draft })) return;
    const mounted = lease, owner = actor;
    const generation = getNavigationGeneration();
    const attempt = { owner, snapshot, operation, outcome: 'pending', pending: true };
    activeAttempt = attempt;
    const restoreControls = disableControls(mounted);
    const body = Object.freeze({ version: snapshot.version, ...(operation === 'delete' ? { actorVersion: owner.version } : {}), ...extra });
    announce('Saving additional-copy task...');
    try {
      // The command owns its authoritative outcome independently of disposable reads.
      const result = await send(`/api/asap/staff/additional-copies/${snapshot.id}${operation === 'delete' ? '' : `/${operation}`}`, {
        method: operation === 'delete' ? 'DELETE' : 'POST', body
      });
      if (operation === 'delete' ? result?.deleted !== true : !isCommittedRequestResponse(result, snapshot.id)) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, operation === 'assign' ? 'Assignment notification' : 'Notification');
      const resultStatus = result.status || result.finalStatus || result.request?.status;
      let message = `${successMessage}${resultStatus ? ` Final state: ${statusLabel(resultStatus)}.` : ''}${notification.text}`;
      onReceipt(`${message} Sign in again to review ${operation === 'delete' ? 'the updated task list' : 'the committed task'}.`, owner, attempt);
      if (!isPresentationCurrent(mounted, owner) || !isNavigationCurrent(generation)) return;
      if (operation === 'delete') {
        close({ preserveMutation: true }); announce(message, notification.partial ? 'warning' : 'success');
        const refreshed = await refreshQueue({ silent: true });
        if (refreshed === true) clearReceipt(attempt);
        if (refreshed === false && !disposed && isNavigationCurrent(generation) && sessionIdentity.isCurrent(owner)) {
          announce(`${message} The task list could not refresh.`, 'warning');
        }
        return;
      }
      let detail = result.request || (result.id ? result : null);
      if (!detail) {
        const load = reads.begin('detail');
        try {
          detail = await send(`/api/asap/staff/additional-copies/${encodeURIComponent(snapshot.id)}`, { signal: load.signal });
          if (!load.isCurrent()) return;
        }
        catch (error) { if (!isAbortError(error) && error.status !== 401) message += ' Details could not refresh.'; }
        finally { reads.finish('detail', load.token); }
      }
      if (!isPresentationCurrent(mounted, owner) || !isNavigationCurrent(generation)) return;
      if (detail?.status && detail.status !== resultStatus) message = `${successMessage} Final state: ${statusLabel(detail.status)}.${notification.text}`;
      if (result.claimClearedReason) message += ` The retained claim was cleared (${String(result.claimClearedReason).replaceAll('_', ' ')}).`;
      onReceipt(`${message} Sign in again to review the committed task.`, owner, attempt);
      if (detail) renderAdditionalCopy(detail);
      else { current = Object.freeze({ id: snapshot.id, version: null }); mounted.content.replaceChildren(element('p', { text: 'The action committed. Reload this task to review current details.' })); }
      host.focusClose(); announce(message, notification.partial ? 'warning' : 'success');
      if (activeAttempt === attempt) activeAttempt = null;
      const refreshed = await refreshQueue({ silent: true });
      if (refreshed === true && detail) clearReceipt(attempt);
      if (isPresentationCurrent(mounted, owner) && isNavigationCurrent(generation)) announce(refreshed === false ? `${message} The task list could not refresh.` : message, notification.partial || refreshed === false ? 'warning' : 'success');
    } catch (error) {
      if (attempt.outcome === 'committed') {
        if (isPresentationCurrent(mounted, owner)) announce(`${successMessage} Current task details could not refresh.`, 'warning');
        return;
      }
      const definiteNoCommit = error.status === 503 && error.response?.code === 'notification_dependency_unavailable';
      const uncertain = !definiteNoCommit && (!error.status || error.status === 408 || error.status >= 500 || isAbortError(error));
      attempt.outcome = uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      const message = uncertain ? 'The additional-copy action outcome could not be confirmed. Reload before trying again.'
        : error.message || 'The task changed. Review the refreshed version before trying again.';
      if (uncertain) onReceipt(`${message} Sign in again to check the authoritative result before retrying.`, owner, attempt);
      if (!isPresentationCurrent(mounted, owner)) return;
      if (error.status === 409 || uncertain) {
        const refreshed = await refreshQueue({ silent: true });
        if (!isPresentationCurrent(mounted, owner)) return;
        const loaded = await open(snapshot.id, null, { authoritativeRefresh: true });
        if (uncertain && refreshed === true && loaded === true) clearReceipt(attempt);
        if (isCurrentDialogSelection(snapshot)) announce(message, 'error');
      } else if (error.status !== 401) announce(error.message || 'The additional-copy task could not be updated.', 'error');
    } finally {
      attempt.pending = false;
      if (activeAttempt === attempt) activeAttempt = null;
      restoreControls();
    }
  }

  function close(options = {}) {
    if (activeAttempt?.pending && !options.force && !options.preserveMutation) {
      announce('The workflow action is in progress. Wait for its authoritative result before closing.', 'warning'); host.focusClose(); return false;
    }
    if (!options.force && !options.guarded && !options.preserveMutation && !beforeClose(options)) return false;
    const focusReturn = options.navigation ? null : getFocusReturn(current?.id, returnFocus);
    lease?.release({ ...options, focusReturn }); onClosed(options); return true;
  }

  function renderAdditionalCopy(request) {
    resetDrafts();
    current = Object.freeze({ ...request });
    lease?.heading(request.title, `${request.libraryOrgName} · Additional copy ${request.id}`);
    const body = document.createDocumentFragment();
    body.append(
      element('div', { className: 'detail-meta' }, [
        element('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}`, text: statusLabel(request.status) }),
        element('span', { text: `Updated ${dateTime(request.updated)}` }),
        element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
      ]),
      buildAdditionalCopyActionBar(request)
    );
    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'BIB ID', request.bibid);
    addDetail(details, 'Format', request.formatLabel || request.format);
    addDetail(details, 'Author', request.author);
    addDetail(details, 'Identifier', request.identifier);
    addDetail(details, 'Publication', request.publication);
    addDetail(details, 'Created by', request.createdByUsername);
    addDetail(details, 'Created', dateTime(request.created));
    if (request.status === 'open' && request.timeoutContext) {
      addDetail(details, 'Task timeout', timeoutLabel(request.timeoutContext.enabled, request.timeoutContext.days));
    }
    addDetail(details, 'Closed by', request.closedByUsername);
    addDetail(details, 'Closed', dateTime(request.closedAt));
    if (request.status === 'closed') addDetail(details, 'Close reason', 'No reason recorded');
    body.append(details);
    if (request.sourceTitleRequest) {
      body.append(element('a', {
        className: 'source-link',
        href: `/staff/?request=${encodeURIComponent(request.sourceTitleRequest)}`
      }, [icon('external-link'), 'Open source title request']));
    } else {
      body.append(element('p', { text: 'The source title request is no longer available.' }));
    }
    if (request.notes) {
      body.append(
        element('h3', { text: 'Notes' }),
        element('pre', { className: 'notes-history', text: request.notes })
      );
    }
    lease.content.replaceChildren(body);
  }

  function buildAdditionalCopyActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Additional-copy actions' });
    if (request.capabilities?.canUnclaim && request.claimedByStaffUserId === sessionIdentity.preferences()?.id) {
      bar.append(commandButton('Unclaim', 'user-times', () => mutateAdditionalCopy(request, 'unclaim', 'Task unclaimed.')));
    } else if (request.capabilities?.canClearClaim && request.claimedByStaffUserId &&
               ['admin', 'super_admin'].includes(sessionIdentity.preferences()?.role)) {
      bar.append(commandButton('Clear claim', 'user-times', () => {
        if (confirmCurrent(request, 'additional_copy',
          `Clear ${request.claimedByDisplayName || 'another staff member'}'s claim? The task will remain open and unclaimed.`)) {
          mutateAdditionalCopy(request, 'clear-claim', 'Additional-copy claim cleared.');
        }
      }));
    } else if (request.capabilities?.canClaim) {
      bar.append(commandButton('Claim', 'user-plus', () => mutateAdditionalCopy(request, 'claim', 'Task claimed.'), 'primary-button'));
    }
    if (request.capabilities?.canAssign) {
      bar.append(commandButton('Assign', 'users', event => showAdditionalCopyAssignment(request, event.currentTarget)));
    }
    if (request.capabilities?.canClose) {
      bar.append(commandButton('Close task', 'check', () => {
        if (confirmCurrent(request, 'additional_copy',
          'Close this additional-copy task? It will leave the open work queue; the source patron hold will not change.')) {
          mutateAdditionalCopy(request, 'close', 'Additional-copy task closed.');
        }
      }, 'primary-button'));
    }
    if (request.capabilities?.canReopen) {
      bar.append(commandButton('Reopen task', 'undo', () => {
        if (confirmCurrent(request, 'additional_copy',
          'Reopen this additional-copy task? It will return to open work. An eligible retained claim stays; an invalid claim is cleared.')) {
          mutateAdditionalCopy(request, 'reopen', 'Additional-copy task reopened.');
        }
      }, 'primary-button'));
    }
    if (request.capabilities?.canDelete) {
      bar.append(commandButton('Permanently delete task', 'trash', () => {
        if (confirmCurrent(request, 'additional_copy',
          `Permanently delete closed additional-copy task ${request.id}? This cannot be undone. Its deletion audit will remain.`)) {
          mutateAdditionalCopy(request, 'delete', 'Additional-copy task deleted.');
        }
      }, 'danger-button'));
    }
    return bar;
  }

  async function showAdditionalCopyAssignment(request, returnFocus) {
    if (!isCurrentDialogRequest(request, 'additional_copy') || activeAttempt) return;
    const load = reads.begin('assignment-candidates');
    announce('Loading eligible staff...');
    try {
      const result = await send(`/api/asap/staff/assignment-candidates?libraryOrgId=${request.libraryOrgId}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'additional_copy') || activeAttempt) return;
      const select = element('select', { 'aria-label': 'Assign additional-copy task' });
      const candidates = result.candidates || [];
      for (const candidate of candidates) {
        select.append(element('option', {
          value: candidate.id,
          text: candidate.displayName
        }));
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Assign task', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: candidates.length === 0 }, [icon('user-plus'), 'Assign'])
      ]);
      form.append(commandButton('Cancel', 'times', () => cancelDialogFormDraft(form, returnFocus)));
      const draft = trackDialogFormDraft(form);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'additional_copy')) return;
        await mutateAdditionalCopy(request, 'assign', 'Additional-copy task assigned.', {
          assigneeId: select.value
        }, draft);
      });
      lease.content.prepend(form);
      select.focus();
      announce(candidates.length ? 'Choose an assignee.' : 'No eligible staff are available.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'additional_copy') &&
          error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'Assignable staff could not be loaded.', 'error');
      }
    } finally {
      reads.finish('assignment-candidates', load.token);
    }
  }

  return { open, close, mutate: mutateAdditionalCopy, invalidate,
    isDirty: () => Boolean(lease?.isCurrent() && drafts.isDirty()),
    inspectDeparture: () => ({ dirty: Boolean(lease?.isCurrent() && drafts.isDirty()), stamp: drafts.stamp(),
      blocked: Boolean(activeAttempt && sessionIdentity.isCurrent(activeAttempt.owner)),
      message: 'The workflow action is in progress. Wait for its authoritative result before navigating away.',
      confirmMessage: 'Discard unsaved request changes and navigate away?' }),
    signedOut() { lease?.release({ navigation: true }); invalidate(); },
    dispose() { lease?.release({ navigation: true }); disposed = true; invalidate(); drafts.dispose(); }
  };
}
