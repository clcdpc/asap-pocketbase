import { element, statusLabel } from './ui.js';
import { actorKey } from './session-identity.js';
import { validRequestId } from './recent-requests.js';
import { isCommittedRequestResponse, notificationOutcome, unconfirmedResponseError } from './mutation-outcome.js';

const actions = new Set(['catalogFound', 'additionalCopy']);
const operationId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function catalogQueueStorageKey(actor) {
  return actor?.tenantId && validRequestId(String(actor.id))
    ? `asap.staff.catalogQueue.${encodeURIComponent(actorKey(actor))}` : null;
}

export function decodeCatalogQueue(raw, actor) {
  try {
    const saved = JSON.parse(raw), body = saved?.body;
    if (saved?.actorKey !== actorKey(actor) || !validRequestId(saved?.requestId) ||
        !Number.isSafeInteger(saved.libraryOrgId) || saved.libraryOrgId <= 0 ||
        typeof saved.title !== 'string' || !body || !actions.has(body.action) ||
        body.requestType !== 'title_request' || typeof body.version !== 'string' || !body.version ||
        !operationId.test(body.operationId) || !Number.isInteger(body.bibid) || body.bibid <= 0 || body.bibid > 2147483647 ||
        body.staffSelectedBibId !== body.bibid ||
        body.action === 'additionalCopy' && typeof body.emailPurchaseReminder !== 'boolean' ||
        Object.keys(body).some(key => !['action', 'requestType', 'version', 'operationId', 'bibid', 'staffSelectedBibId', 'emailPurchaseReminder'].includes(key))) return null;
    return { ...saved, body: Object.freeze({ ...body }) };
  } catch { return null; }
}

// The combined server action owns BIB verification, the autohold override, the
// copy task, audit and outbox. Persist exactly one command before dispatch so a
// lost response (including a reload) can recover its receipt without a new task.
export function createCatalogQueue({ dialog, panel, searchForm, results, sessionIdentity,
  authorizedJson, isAbortError, announce, onReceipt = () => {}, clearReceipt = () => {},
  storage = () => window.sessionStorage, isCurrent, onCommitted, onReturnToSearch }) {
  let decision = null, pending = null, retained = null;

  function recovery(current) {
    const actor = current?.actor;
    if (!actor || !sessionIdentity?.isCurrent(actor)) return null;
    const key = catalogQueueStorageKey(actor);
    try {
      const raw = storage().getItem(key);
      const saved = raw && decodeCatalogQueue(raw, actor);
      retained = saved ? { ...saved, owner: actor, key, raw } : null;
    } catch { retained = null; }
    return retained;
  }

  function controls(locked) {
    for (const control of searchForm.elements) control.disabled = locked;
    searchForm.hidden = locked;
    results.hidden = locked;
    panel.hidden = !locked;
  }

  function dismiss() {
    if (pending) { announce('The catalog action is in progress. Wait for its result before closing.', 'warning'); return false; }
    const prior = decision;
    decision = null;
    controls(false);
    panel.replaceChildren();
    if (prior && isCurrent(prior.current) && dialog.open) {
      const target = [prior.opener, ...searchForm.querySelectorAll('input, select, button'), dialog.querySelector('#close-polaris')]
        .find(control => control?.isConnected && !control.disabled && dialog.contains(control) &&
          !control.closest('[hidden], [inert]'));
      target?.focus();
    }
    return true;
  }

  function retain(record) {
    const key = catalogQueueStorageKey(record.owner);
    if (!key) return null;
    const raw = JSON.stringify({ actorKey: actorKey(record.owner), requestId: record.requestId,
      libraryOrgId: record.libraryOrgId, title: record.title, body: record.body });
    try { storage().setItem(key, raw); }
    catch { return null; }
    retained = { ...record, key, raw };
    return retained;
  }

  function clear(record) {
    try { if (storage().getItem(record.key) === record.raw) storage().removeItem(record.key); }
    catch { /* Keep the receipt recoverable if browser storage is unavailable. */ }
    if (retained?.raw === record.raw) retained = null;
  }

  function showRecovery(current, record) {
    const opener = decision?.current === current ? decision.opener : document.activeElement;
    // The focused confirmation/retry control is about to be removed. Keep the
    // result or search control that opened this decision instead.
    decision = { current, opener: panel.contains(opener) ? null : opener };
    controls(true);
    const sameRequest = record.requestId === String(current.requestId) && record.libraryOrgId === current.libraryOrgId;
    panel.replaceChildren(
      element('h3', { text: 'Catalog action unconfirmed' }),
      element('p', { text: `Request ${record.requestId}: ${record.title}, BIB ${record.body.bibid}. The earlier action may have committed. Recover its result before starting another catalog action.` })
    );
    const buttons = element('div', { className: 'form-actions' });
    if (sameRequest) {
      const retry = element('button', { type: 'button', className: 'primary-button', text: 'Recover identical action' });
      retry.addEventListener('click', () => {
        if (decision?.current === current && isCurrent(current) && !pending) void dispatch(current, record);
      });
      buttons.append(retry);
    } else panel.append(element('p', { text: `Open catalog research for request ${record.requestId} to recover the identical action.` }));
    const cancel = element('button', { type: 'button', className: 'secondary-button', text: 'Close recovery' });
    cancel.addEventListener('click', dismiss);
    buttons.append(cancel); panel.append(buttons); cancel.focus();
    announce('A previous catalog action is unconfirmed. Its original command has been retained for safe recovery.', 'warning');
  }

  function confirm(current, row, detail, action, opener) {
    if (decision || pending || !isCurrent(current) || current.directActions !== true) return;
    const previous = recovery(current);
    if (previous) { showRecovery(current, previous); return; }
    const preview = detail?.queueActions, choice = preview?.[action === 'additionalCopy' ? 'additionalCopy' : 'useBib'];
    if (!choice?.allowed || preview.requestId !== String(current.requestId) || preview.libraryOrgId !== current.libraryOrgId ||
        preview.bibId !== row.bibId || typeof preview.version !== 'string' || !preview.version ||
        typeof preview.currentAutohold !== 'boolean') return;
    const copy = action === 'additionalCopy';
    decision = { current, opener };
    controls(true);
    panel.replaceChildren(
      element('h3', { text: copy ? 'Buy another copy + Queue Now' : 'Use BIB & Queue Now' }),
      element('p', { text: `${detail.title || row.title || 'Catalog title'} — BIB ${row.bibId}` }),
      element('p', { text: copy
        ? 'Create an additional-copy purchase task and queue the patron request against this BIB. Queueing does not mean a Polaris hold has been placed.'
        : preview.currentAutohold
          ? 'Queue this BIB for the hold workflow. Queueing does not mean a Polaris hold has been placed.'
          : 'Automatic hold placement is off. This action will link this BIB and close the request without placing a hold.' })
    );
    const reminder = element('input', { type: 'checkbox', checked: preview.emailPurchaseReminderDefault });
    if (copy) {
      panel.append(element('p', { text: Number.isInteger(preview.openAdditionalCopyCount)
        ? `${preview.openAdditionalCopyCount} open additional-copy task${preview.openAdditionalCopyCount === 1 ? '' : 's'} already exist for this library and BIB.`
        : 'The number of existing additional-copy tasks is unavailable.' }));
      if (!preview.currentAutohold) panel.append(element('p', { className: 'polaris-override-warning',
        text: 'This action will turn on automatic hold placement for this request.' }));
      panel.append(element('label', { className: 'check-field' }, [reminder, element('span', { text: 'Email me a purchase reminder' })]));
    }
    const cancel = element('button', { type: 'button', className: 'secondary-button', text: 'Cancel' });
    cancel.addEventListener('click', dismiss);
    const approve = element('button', { type: 'button', className: 'primary-button', text: copy ? 'Confirm additional copy and queue' : 'Confirm use BIB' });
    approve.addEventListener('click', () => {
      if (decision?.current !== current || !isCurrent(current) || pending) return;
      const body = Object.freeze({ action, requestType: 'title_request', version: preview.version,
        operationId: window.crypto.randomUUID(), bibid: row.bibId, staffSelectedBibId: row.bibId,
        ...(copy ? { emailPurchaseReminder: reminder.checked } : {}) });
      const record = retain({ requestId: String(current.requestId), libraryOrgId: current.libraryOrgId,
        title: detail.title || row.title || 'Catalog title', body, owner: current.actor });
      if (!record) { announce('This browser could not save the pending catalog action. Enable site storage before trying again.', 'error'); return; }
      void dispatch(current, record);
    });
    panel.append(element('div', { className: 'form-actions' }, [cancel, approve]));
    cancel.focus(); announce('Review the selected BIB and confirm the catalog action.');
  }

  async function dispatch(current, record) {
    if (pending || !isCurrent(current) || !sessionIdentity.isCurrent(record.owner)) return;
    const attempt = { owner: record.owner, pending: true, outcome: 'pending' };
    pending = attempt;
    for (const control of panel.querySelectorAll('button, input')) control.disabled = true;
    const progress = element('p', { role: 'status', text: 'Saving catalog action…' });
    panel.append(progress);
    announce('Saving catalog action…');
    try {
      const result = await authorizedJson(`/api/asap/staff/title-requests/${record.requestId}/action`, { method: 'POST', body: record.body });
      if (!isCommittedRequestResponse(result, record.requestId) || result.operationId !== record.body.operationId ||
          record.body.action === 'additionalCopy' && !validRequestId(result.additionalCopyRequestId)) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      clear(record);
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, 'Purchase reminder');
      const finalState = result.status || result.request?.status || result.finalStatus;
      const message = `${record.body.action === 'additionalCopy' ? `Additional-copy task ${result.additionalCopyRequestId} created.` : 'Catalog BIB linked.'} Final state: ${statusLabel(finalState)}.${notification.text}`;
      onReceipt(`${message} Sign in again to review the committed request.`, record.owner, attempt);
      if (!isCurrent(current)) return;
      pending = null; dismiss();
      const refreshed = await onCommitted(current, result);
      if (refreshed === true) clearReceipt(attempt);
      if (sessionIdentity.isCurrent(record.owner)) announce(`${message}${refreshed === false ? ' The queue could not refresh.' : ''}`,
        notification.partial || refreshed === false ? 'warning' : 'success');
    } catch (error) {
      if (attempt.outcome === 'committed') {
        if (sessionIdentity.isCurrent(record.owner)) announce('The catalog action committed. The queue could not refresh.', 'warning');
        return;
      }
      const rejected = [400, 401, 403, 404, 409].includes(error.status) ||
        ['bib_validation_unavailable', 'holdings_unavailable', 'notification_dependency_unavailable'].includes(error.response?.code);
      const uncertain = !rejected && (error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error));
      attempt.outcome = uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      if (!uncertain) clear(record);
      if (uncertain) onReceipt('The catalog action could not be confirmed. Recover the identical action before retrying.', record.owner, attempt);
      if (!isCurrent(current)) return;
      pending = null;
      if (uncertain) { showRecovery(current, record); return; }
      panel.replaceChildren(element('h3', { text: 'Catalog action not completed' }),
        element('p', { role: 'alert', text: error.message || 'The request or its eligibility changed. Search again to review its current state.' }));
      const review = element('button', { type: 'button', className: 'secondary-button', text: 'Return to search' });
      review.addEventListener('click', () => {
        if (decision?.current !== current || !isCurrent(current) || !dismiss()) return;
        onReturnToSearch(current);
      });
      panel.append(review); review.focus();
      announce(error.message || 'The catalog action was rejected. Search again before trying another action.', 'error');
    } finally {
      attempt.pending = false;
      if (pending === attempt) pending = null;
    }
  }

  return {
    confirm,
    isLocked: () => Boolean(decision || pending),
    hasPending: () => Boolean(pending),
    inspectDeparture: () => ({ owner: pending, blocked: Boolean(pending), dirty: false,
      message: 'The catalog action is in progress. Wait for its result before leaving.' }),
    open(current) { const saved = recovery(current); if (saved) showRecovery(current, saved); },
    dismiss,
    reset(force = false) {
      if (!force && !dismiss()) return false;
      if (force) pending = null;
      decision = null; controls(false); panel.replaceChildren();
      return true;
    }
  };
}
