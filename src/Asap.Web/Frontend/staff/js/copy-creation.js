import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { positivePolarisId } from './research.js';
import { validRequestId } from './recent-requests.js';
import { unconfirmedResponseError, notificationOutcome } from './mutation-outcome.js';
import { statusLabel } from './ui.js';

export function copyCreationStorageKey(staff) {
  if (!staff?.tenantId || !validRequestId(String(staff.id))) return null;
  return `asap.staff.unconfirmedCopyCreation.${String(staff.tenantId).toLowerCase()}.${staff.id}`;
}

export function decodeCopyCreation(raw, storageKey) {
  try {
    const saved = JSON.parse(raw);
    if (!Number.isSafeInteger(saved?.libraryOrgId) || saved.libraryOrgId <= 0 ||
        positivePolarisId(saved.bibid) === null || !validRequestId(saved.sourceId) ||
        typeof saved.version !== 'string' || !saved.version ||
        saved.emailPurchaseReminder !== undefined && typeof saved.emailPurchaseReminder !== 'boolean') return null;
    return { libraryOrgId: saved.libraryOrgId, bibid: positivePolarisId(saved.bibid), sourceId: saved.sourceId,
      version: saved.version, emailPurchaseReminder: saved.emailPurchaseReminder,
      storageKey, storageValue: raw, reviewReady: false, reviewed: false };
  } catch { return null; }
}

export function createCopyCreationController({ root, sessionIdentity, announce, onReceipt, clearReceipt,
  onRecoveryChanged, onParentChanged, request: send = authorizedJson, storage = () => window.sessionStorage }) {
  const form = root.querySelector('#additional-copy-create-form');
  const summary = root.querySelector('#additional-copy-create-summary');
  const reminder = root.querySelector('#additional-copy-reminder');
  const cancel = root.querySelector('#cancel-additional-copy');
  const submit = form.querySelector('button[type="submit"]');
  const events = new window.AbortController();
  const reads = createLatestLoad();
  let ui = null, retained = null, previewParent = null, disposed = false;

  function currentRecovery() {
    if (!retained || !sessionIdentity.isCurrent(retained.owner)) return null;
    const { libraryOrgId, bibid, sourceId, version, reviewReady, reviewed } = retained;
    return Object.freeze({ libraryOrgId, bibid, sourceId, version, reviewReady, reviewed });
  }
  function isCurrent(owner) {
    return !disposed && ui === owner && root.open && sessionIdentity.isCurrent(owner.actor) && owner.parent.isCurrent();
  }
  function loadRecovery() {
    if (disposed) return;
    retained = null;
    try {
      // The obsolete marker has no actor evidence and cannot be adopted.
      storage().removeItem('asap.staff.unconfirmedCopyCreation');
      const key = copyCreationStorageKey(sessionIdentity.preferences());
      if (!key) return;
      const value = decodeCopyCreation(storage().getItem(key), key);
      if (value) retained = { ...value, owner: sessionIdentity.preferences() };
    } catch { /* A submitted command verifies storage availability before dispatch. */ }
    if (retained) onRecoveryChanged({ owner: retained.owner, restored: true });
  }
  function writeAttempt(value, owner) {
    const storageKey = copyCreationStorageKey(owner);
    if (!storageKey) return null;
    const payload = { libraryOrgId: value.libraryOrgId, bibid: value.bibid, sourceId: value.sourceId,
      version: value.version, emailPurchaseReminder: value.emailPurchaseReminder, recordId: window.crypto.randomUUID() };
    const storageValue = JSON.stringify(payload);
    try {
      storage().setItem(storageKey, storageValue);
      return { ...payload, owner, storageKey, storageValue, reviewReady: false, reviewed: false };
    } catch { return null; }
  }
  function clearRecord(record) {
    try {
      // A retired completion may remove only the exact record it wrote.
      if (storage().getItem(record.storageKey) === record.storageValue) storage().removeItem(record.storageKey);
    } catch { /* Retaining evidence is safer than assuming unavailable storage was cleared. */ }
    if (retained === record) retained = null;
  }

  async function preview(parent, opener = null) {
    if (disposed || !parent.isCurrent() || !parent.admit({ consumes: null })) return false;
    const recovery = currentRecovery();
    if (recovery && (!recovery.reviewed || recovery.sourceId !== parent.request.id)) {
      announce('Additional-copy creation is unconfirmed. Refresh the open additional-copy task list for this library and review matching tasks before trying again.', 'warning'); return false;
    }
    const load = reads.begin('preview');
    previewParent = parent;
    announce('Loading additional-copy preview...');
    try {
      const result = await send(`/api/asap/staff/title-requests/${parent.request.id}/additional-copy`, { signal: load.signal });
      if (!load.isCurrent() || !parent.isCurrent() || !parent.admit({ consumes: null })) return false;
      if (!result?.version || result.version !== parent.request.version || positivePolarisId(result.bibid) !== positivePolarisId(parent.request.bibid)) {
        announce('The request changed. Reload its current details before creating an additional-copy task.', 'warning'); return false;
      }
      close({ navigation: true, force: true });
      const owner = { parent, actor: parent.actor, opener: opener || document.activeElement,
        version: result.version, baseline: recovery && retained.emailPurchaseReminder !== undefined
          ? retained.emailPurchaseReminder : Boolean(result.emailPurchaseReminderDefault), submitting: false, outcomeUnconfirmed: false };
      ui = owner;
      const holdState = parent.request.status === 'hold_placed' ? 'placed' : 'queued';
      summary.textContent = `${result.openCount} open additional-copy task${result.openCount === 1 ? '' : 's'} already exist for BIB ${result.bibid}. The patron hold remains ${holdState}.` +
        (recovery ? ' This retry uses the original request version; the server will reject it if the earlier creation changed the request.' : '');
      reminder.checked = owner.baseline; submit.disabled = false;
      owner.draft = parent.registerDraft({ root: form, isDirty: () => isCurrent(owner) && reminder.checked !== owner.baseline });
      root.showModal(); cancel.focus(); announce('Review the additional-copy task before creating it.');
      return true;
    } catch (error) {
      if (load.isCurrent() && parent.isCurrent() && !isAbortError(error) && error.status !== 401) announce(error.message || 'The additional-copy preview could not be loaded.', 'error');
      return false;
    } finally { reads.finish('preview', load.token); }
  }

  function close(options = {}, parent = null) {
    if (disposed || !ui && !previewParent) return true;
    if (parent !== null && ui?.parent !== parent && previewParent !== parent) return true;
    const owner = ui;
    if (owner?.submitting && !options.preserveMutation && !options.force) {
      announce('Task creation is in progress. Wait for the authoritative result before closing.', 'warning'); cancel.focus(); return false;
    }
    reads.begin('preview').abort();
    previewParent = null;
    owner?.parent.releaseDraft(owner.draft);
    ui = null;
    if (root.open) root.close(); summary.textContent = ''; reminder.checked = false; submit.disabled = true;
    if (!options.navigation && owner?.parent.isCurrent() && owner.opener?.isConnected) owner.opener.focus();
    return true;
  }

  async function create(event) {
    event.preventDefault();
    const owner = ui;
    const recovery = currentRecovery();
    if (!owner || !isCurrent(owner) || owner.submitting || owner.outcomeUnconfirmed ||
        recovery && (!recovery.reviewed || recovery.sourceId !== owner.parent.request.id) ||
        !owner.parent.admit({ consumes: owner.draft })) return;
    owner.submitting = true; submit.disabled = true;
    const attempt = { owner: owner.actor, outcome: 'pending', pending: true };
    announce('Creating additional-copy task...');
    const record = writeAttempt({ libraryOrgId: recovery?.libraryOrgId ?? owner.parent.request.libraryOrgId,
      bibid: recovery?.bibid ?? owner.parent.request.bibid, sourceId: owner.parent.request.id,
      version: recovery?.version ?? owner.version,
      emailPurchaseReminder: recovery && retained.emailPurchaseReminder !== undefined ? retained.emailPurchaseReminder : reminder.checked }, owner.actor);
    if (!record) {
      attempt.outcome = 'not_dispatched'; attempt.pending = false; owner.submitting = false; submit.disabled = false;
      announce('This browser could not save the pending task attempt. Enable site storage before creating the task.', 'error'); return;
    }
    attempt.record = record; retained = record;
    try {
      const result = await send(`/api/asap/staff/title-requests/${record.sourceId}/additional-copy`, {
        method: 'POST', body: Object.freeze({ version: record.version, emailPurchaseReminder: record.emailPurchaseReminder })
      });
      if (result?.committed !== true || typeof result.additionalCopyRequestId !== 'string' || !validRequestId(result.additionalCopyRequestId) ||
          typeof result.finalStatus !== 'string' || !result.finalStatus || !Object.hasOwn(result, 'additionalCopyRequest') ||
          result.additionalCopyRequest === null && result.refreshUnavailable !== true ||
          result.additionalCopyRequest !== null && (result.additionalCopyRequest.id !== result.additionalCopyRequestId ||
            result.additionalCopyRequest.status !== result.finalStatus || typeof result.additionalCopyRequest.version !== 'string' ||
            !result.additionalCopyRequest.version)) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      clearRecord(record);
      const taskId = result.additionalCopyRequest?.id || result.additionalCopyRequestId;
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, 'Purchase reminder');
      const message = `Additional-copy task ${taskId} created. Final state: ${statusLabel(result.finalStatus)}.${notification.text}`;
      onReceipt(`${message} Sign in again to review the committed task.`, owner.actor, attempt);
      onRecoveryChanged({ committed: true, owner: owner.actor });
      if (!isCurrent(owner)) return;
      owner.submitting = false;
      close({ preserveMutation: true });
      announce(message, notification.partial ? 'warning' : 'success');
      const resultRefresh = await onParentChanged(owner.parent, owner.opener);
      if (resultRefresh?.queueRefreshed === true && resultRefresh.detailLoaded === true) clearReceipt(attempt);
      if (resultRefresh?.isCurrent()) {
        const followup = `${resultRefresh?.queueRefreshed === false ? ' The request queue could not refresh.' : ''}${resultRefresh?.detailLoaded ? '' : ' Request details could not refresh.'}`;
        announce(`${message}${followup}`, notification.partial || followup ? 'warning' : 'success');
      }
    } catch (error) {
      if (attempt.outcome === 'committed') {
        if (owner.parent.isCurrent()) announce('Additional-copy task created. Request details could not refresh.', 'warning'); return;
      }
      const definiteNoCommit = [400, 401, 403, 404, 409].includes(error.status) || error.status === 503 && error.response?.code === 'notification_dependency_unavailable';
      const uncertain = !definiteNoCommit && (!error.status || error.status === 408 || error.status >= 500 || isAbortError(error));
      attempt.outcome = uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      if (definiteNoCommit) clearRecord(record);
      if (uncertain) {
        const message = 'Additional-copy creation could not be confirmed. Check the task list before trying again.';
        record.receiptAttempt = attempt;
        onReceipt(`${message} Sign in again to check the authoritative result before retrying.`, owner.actor, attempt);
        if (isCurrent(owner)) {
          owner.outcomeUnconfirmed = true; record.reviewReady = false; record.reviewed = false;
          onRecoveryChanged({ uncertain: true, owner: owner.actor }); announce(message, 'warning');
        }
      } else if (isCurrent(owner) && error.status === 409) {
        owner.submitting = false; close({ preserveMutation: true });
        const refreshed = await onParentChanged(owner.parent, owner.opener);
        if (refreshed?.isCurrent()) announce(error.message || 'The request changed. Review the refreshed version before trying again.', 'error');
      } else if (isCurrent(owner) && error.status !== 401) announce(error.message || 'The additional-copy task could not be created.', 'error');
    } finally {
      attempt.pending = false; owner.submitting = false;
      if (isCurrent(owner)) submit.disabled = owner.outcomeUnconfirmed;
    }
  }

  form.addEventListener('submit', create, { signal: events.signal });
  for (const name of ['input', 'change']) form.addEventListener(name, () => ui?.parent.touchDraft(), { signal: events.signal });
  cancel.addEventListener('click', () => close(), { signal: events.signal });
  root.addEventListener('cancel', event => { event.preventDefault(); close(); }, { signal: events.signal });

  return { preview, create, close,
    invalidate(parent = null) {
      if (parent === null || ui?.parent === parent || previewParent === parent) {
        reads.begin('preview').abort(); previewParent = null;
      }
    },
    hasPendingMutation: (parent = null) => Boolean(ui?.submitting && (parent === null || ui.parent === parent) && sessionIdentity.isCurrent(ui.actor)),
    setStaff: loadRecovery,
    signedOut() { if (!disposed) { close({ navigation: true, force: true }); retained = null; } },
    review: {
      current: currentRecovery,
      begin() { if (!currentRecovery()) return null; retained.reviewReady = false; return retained; },
      loaded(evidence, result) {
        if (!currentRecovery()) return false;
        retained.reviewReady = evidence === retained && result.status === 'open' &&
          (result.scope === 'all' || String(result.scope) === String(retained.libraryOrgId));
        return retained.reviewReady;
      },
      acknowledge(context) {
        if (!currentRecovery()?.reviewReady || context.additionalCopyStatus !== 'open' ||
            context.scope !== 'all' && String(context.scope) !== String(retained.libraryOrgId)) return false;
        retained.reviewed = true; clearReceipt(retained.receiptAttempt); return true;
      }
    },
    dispose() { if (disposed) return; close({ navigation: true, force: true }); disposed = true; events.abort(); reads.begin('preview').abort(); retained = null; }
  };
}
