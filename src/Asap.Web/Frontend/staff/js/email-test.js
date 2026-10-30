import { authorizedJson, isAbortError } from './http.js';
import { element } from './ui.js';
import { confirmAction } from './confirmation.js';

const reasons = {
  recipient_missing_or_invalid: 'Set your primary notification email in Staff Access before sending a test.',
  recipient_domain_not_allowed: 'Your notification email is blocked by this environment’s allowed-recipient policy.',
  sender_missing: 'Save a valid From address for this scope before sending a test.',
  mail_not_configured: 'Configure the System Postmark credential and approved sender before sending a test.',
  live_delivery_disabled: 'Live email sending is disabled in this environment. Configure live Postmark delivery before sending a test.',
  email_test_configuration_changed: 'Saved email settings or your account changed. Refresh the saved context before sending a new test.',
  test_configuration_changed: 'Saved email configuration changed before dispatch. This test was not sent.',
  test_delivery_mode_changed: 'Delivery mode changed before dispatch. This test was not sent.'
};
const canonicalId = value => typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
  (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
const uuid = value => typeof value === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value);
const unknownCodes = new Set(['provider_timeout', 'transport_failure', 'ambiguous_expired_lease',
  'provider_start_deadline_exceeded']);
const absenceRecheckDelay = 60000;

export function emailTestStatusMessage(row) {
  const reference = ` Reference ${row.id}.`;
  if (row.status === 'sent') {
    if (row.deliveryMode === 'file') return `Created a local test message. No email was sent to a mailbox.${reference}`;
    if (row.deliveryMode !== 'postmark') return `The test has a recorded result, but live delivery mode is unverified. Review Email operations.${reference}`;
    return `The email service accepted your test message. Check your inbox or junk folder.${reference}`;
  }
  if (row.status === 'suppressed') return `${reasons[row.suppressionReason] || 'The test was suppressed; no email was sent.'}${reference}`;
  if (row.status === 'failed') {
    if (unknownCodes.has(row.lastErrorCode) || !row.lastErrorCode) {
      return `Acceptance is unconfirmed. A message may still arrive. Review this reference before requesting another test.${reference}`;
    }
    return `${reasons[row.lastErrorCode] || 'The email service could not complete this test. Review Email operations.'}${reference}`;
  }
  if (row.status === 'sending') return `Submitting the test email to the email service…${reference}`;
  return `Test email queued. Waiting for the email service.${reference}`;
}

// Both Settings and Operations use this same outbox intent and observer. A
// persisted operation is never POSTed again after an uncertain acknowledgement.
export function createEmailTestController({ root, context, request = authorizedJson, onOperations = () => {},
  pollInterval = 2000, observationLimit = 60000 }) {
  const dom = Object.fromEntries(['context', 'send', 'refresh', 'retire', 'history', 'result', 'operations']
    .map(name => [name, root.querySelector(`[data-email-test="${name}"]`)]));
  const events = new window.AbortController();
  let generation = 0, key = null, saved = null, record = null, loading = false, submitting = false;
  let contextRead = null, statusRead = null, timer = null, deadline = 0, disposed = false;
  let contextSequence = 0, statusSequence = 0, observing = false;
  let absence = null, retiring = false;
  let resultText = '';
  const currentKey = value => value?.ownerKey && Number.isInteger(value.organizationId) && value.organizationId > 0
    ? `${value.ownerKey}|${value.organizationId}|${value.version ?? ''}` : null;
  const storageKey = value => `asap.staff.email-test.${encodeURIComponent(value.ownerKey)}.${value.organizationId}`;
  const current = owner => !disposed && owner.generation === generation && key === currentKey(context()) && context().active;
  function stopReads() {
    contextRead?.abort(); contextRead = null; contextSequence += 1;
    loading = false;
    stopObservation();
  }
  function stopObservation() {
    statusRead?.abort(); statusRead = null; statusSequence += 1; observing = false;
    if (timer !== null) window.clearTimeout(timer);
    timer = null;
  }
  function store(value) {
    try {
      window.sessionStorage.setItem(value.storageKey, JSON.stringify(value));
      return true;
    } catch { return false; }
  }
  function restore(value) {
    try {
      const parsed = JSON.parse(window.sessionStorage.getItem(storageKey(value)) || 'null');
      return parsed && uuid(parsed.operationId) && (!parsed.id || canonicalId(parsed.id))
        ? { ...parsed, storageKey: storageKey(value) } : null;
    } catch { return null; }
  }
  function ownsStored(value) {
    try { return JSON.parse(window.sessionStorage.getItem(value.storageKey) || 'null')?.operationId === value.operationId; }
    catch { return false; }
  }
  function updateStored(value) { return ownsStored(value) && store(value); }
  function restoredMessage(value) {
    if (value?.retired) return `Retired unrecorded client request ${value.operationId}. A message may still arrive; keep the retirement reference for operator review.`;
    if (value?.rejected) return reasons[value.rejectionCode] || (value.rejectionCode === 'email_test_cooldown'
      ? 'A recent test was already requested. Wait before requesting another test.' : 'The previous test request was rejected; no new message was queued.');
    return value ? 'Reviewing the existing test. No new message is being requested.' : '';
  }
  function reconcileStored(value) {
    const stored = restore(value);
    if (record && !stored) {
      absence = null;
      message(`The stored test reference is unavailable. The current request remains unresolved. Reference ${record.id || record.operationId}.`);
      render(); return true;
    }
    if ((!record && !stored) || record && stored &&
        ['operationId', 'id', 'pending', 'retired', 'rejected'].every(field => record[field] === stored[field])) return false;
    // The other panel can learn a late acknowledgement while this panel holds
    // a retired snapshot. Re-read shared authority before retirement or a send.
    stopObservation(); absence = null; record = stored;
    message(restoredMessage(record)); renderHistory(value); render();
    return true;
  }
  function retirementReferences(value) {
    const rows = JSON.parse(window.sessionStorage.getItem(`${storageKey(value)}.retirement-references`) || '[]');
    if (!Array.isArray(rows) || rows.some(row => !uuid(row?.operationId) || typeof row.reviewedAt !== 'string')) {
      throw new Error('Retirement references are unavailable.');
    }
    return rows;
  }
  function renderHistory(value) {
    dom.history.hidden = true;
    dom.history.querySelector('ul').replaceChildren();
    if (!value.active || key !== currentKey(value)) return;
    try {
      const references = retirementReferences(value);
      dom.history.querySelector('ul').replaceChildren(...references.map(row =>
        element('li', { text: `Request ${row.operationId}; retirement reviewed ${row.reviewedAt}.` })));
      dom.history.hidden = references.length === 0;
    } catch { /* Failure to retain references blocks retirement below. */ }
  }
  function canRetire() {
    return record?.pending && !record.id && !record.rejected && !record.retired && absence?.record === record &&
      absence.checks >= 2 && absence.lastAt - absence.firstAt >= absenceRecheckDelay;
  }
  function message(value) { resultText = value; dom.result.textContent = value; }
  function blockedMessage(value) {
    return value.dirty ? 'Save or discard your settings changes before sending a test email.'
      : value.blocked ? 'Wait for the current settings or operation result before sending a test email.'
        : !value.ready ? 'Load the current saved settings before sending a test email.' : '';
  }
  function render() {
    const value = context(), barrier = blockedMessage(value);
    dom.send.disabled = disposed || !value.active || Boolean(barrier) || loading || submitting || retiring ||
      saved?.state !== 'ready' || Boolean(record?.pending);
    dom.refresh.hidden = !record || Boolean(record.rejected);
    dom.refresh.disabled = !value.active || loading || submitting || retiring;
    dom.retire.hidden = !canRetire();
    dom.retire.disabled = disposed || !value.active || Boolean(barrier) || loading || submitting || observing || retiring;
    dom.operations.hidden = !record || Boolean(record.rejected);
    dom.operations.disabled = !value.active;
    if (barrier && !submitting) dom.result.textContent = barrier;
    else dom.result.textContent = resultText;
  }
  function renderSaved() {
    dom.context.replaceChildren();
    if (!saved) return;
    const fields = [
      ['Settings scope', `${saved.scopeName} (${saved.organizationId})`],
      ['From', saved.fromAddress ? `${saved.fromName ? `${saved.fromName} ` : ''}<${saved.fromAddress}>` : 'Not configured'],
      ['Send to', saved.recipient || 'No primary notification email'],
      ['Delivery mode', saved.deliveryMode === 'postmark' ? 'Live email via Postmark'
        : saved.deliveryMode === 'file' ? 'Local capture; no mailbox delivery'
          : 'Non-delivery or unavailable transport']
    ];
    const list = element('dl');
    for (const [label, value] of fields) list.append(element('dt', { text: label }), element('dd', { text: value }));
    dom.context.append(list);
  }
  async function loadContext(owner) {
    loading = true; render();
    contextRead?.abort();
    const read = new window.AbortController(), sequence = ++contextSequence;
    contextRead = read;
    try {
      const response = await request(`/api/asap/staff/email-operations/test-context?organizationId=${owner.value.organizationId}`,
        { signal: read.signal });
      if (!current(owner) || sequence !== contextSequence) return;
      const value = response?.data;
      if (response?.code !== 'ok' || value?.organizationId !== owner.value.organizationId ||
          typeof value.configurationVersion !== 'string' || !/^[0-9a-f]{64}$/i.test(value.configurationVersion)) {
        throw new Error('Saved test-email context is unavailable. Refresh before sending a test.');
      }
      saved = value; renderSaved();
      if (!record) message(reasons[value.blockingReason] || 'This test uses the saved settings shown above.');
    } catch (error) {
      if (current(owner) && sequence === contextSequence && !isAbortError(error)) message('Saved test-email context is unavailable. Refresh Settings or Operations before sending a test.');
    } finally {
      if (current(owner) && sequence === contextSequence) { loading = false; contextRead = null; render(); }
    }
  }
  async function observe(owner, manual = false, retireOnMissing = false) {
    if (!current(owner) || !record || submitting || retiring && !retireOnMissing) return;
    if (manual) deadline = Date.now() + observationLimit;
    stopObservation();
    const read = new window.AbortController(), sequence = ++statusSequence;
    statusRead = read; observing = true;
    const observed = record;
    render();
    const path = observed.id
      ? `/api/asap/staff/email-operations/${encodeURIComponent(observed.id)}?organizationId=${owner.value.organizationId}`
      : `/api/asap/staff/email-operations/test?organizationId=${owner.value.organizationId}&operationId=${encodeURIComponent(observed.operationId)}`;
    try {
      const response = await request(path, { signal: read.signal });
      if (!current(owner) || sequence !== statusSequence || record !== observed || reconcileStored(owner.value)) return;
      const row = response?.data;
      if (response?.code !== 'ok' || !canonicalId(row?.id) || row.organizationId !== owner.value.organizationId ||
          !['pending', 'sending', 'sent', 'suppressed', 'failed'].includes(row.status)) {
        throw new Error('The test-email status could not be confirmed.');
      }
      absence = null;
      observed.id = row.id;
      observed.retired = false;
      observed.pending = row.status === 'pending' || row.status === 'sending' ||
        row.status === 'failed' && (unknownCodes.has(row.lastErrorCode) || !row.lastErrorCode);
      updateStored(observed);
      message(emailTestStatusMessage(row));
      if (row.status === 'pending' || row.status === 'sending') {
        if (Date.now() < deadline) timer = window.setTimeout(() => void observe(owner), pollInterval);
        else message(`Still waiting for a result. Check Email operations. Reference ${row.id}.`);
      }
    } catch (error) {
      if (current(owner) && sequence === statusSequence && record === observed && !isAbortError(error)) {
        if (reconcileStored(owner.value)) return;
        // Only an authorized, exact by-intent not_found is absence evidence.
        // Repeated absence cannot prove that an older request will never commit.
        if (!observed.id && observed.pending && !observed.rejected &&
            error.status === 404 && error.response?.code === 'not_found') {
          const now = Date.now();
          if (absence?.record !== observed) absence = { record: observed, firstAt: now, checks: 0 };
          absence.checks += 1; absence.lastAt = now;
          if (retireOnMissing && canRetire()) {
            try {
              const references = retirementReferences(owner.value);
              if (!references.some(row => row.operationId === observed.operationId)) {
                references.push({ operationId: observed.operationId, reviewedAt: new Date(now).toISOString() });
              }
              window.sessionStorage.setItem(`${storageKey(owner.value)}.retirement-references`, JSON.stringify(references));
              const retired = { ...observed, pending: false, retired: true };
              if (!updateStored(retired)) throw new Error('The original reference could not be retained.');
              record = retired; absence = null; renderHistory(owner.value);
              message(`Retired the unrecorded client request ${observed.operationId}. A delayed request may still commit and a message may still arrive. A separate Send test email action creates a new request.`);
            } catch {
              absence = null;
              message(`Could not retain the retirement reference. The request remains unresolved. Request ${observed.operationId}.`);
            }
          } else {
            message(`No server record was found for request ${observed.operationId}. ${canRetire()
              ? 'You may explicitly retire this unrecorded client request after one final status check.'
              : 'Refresh its status again after at least one minute before considering retirement.'} A delayed request may still commit and a message may still arrive.`);
          }
        } else {
          absence = null;
          message(`The test outcome is unconfirmed. Refresh this test’s status or check Email operations. No new test was requested. Reference ${observed.id || observed.operationId}.`);
        }
      }
    } finally {
      if (current(owner) && sequence === statusSequence) { observing = false; statusRead = null; render(); }
    }
  }
  async function retire() {
    const value = context();
    if (!disposed && value.active && key === currentKey(value) && reconcileStored(value)) return;
    if (disposed || !value.active || blockedMessage(value) || loading || submitting || observing || retiring || !canRetire()) return;
    const owner = { generation, value: { ...value } };
    const observed = record, evidence = absence, checks = absence?.checks;
    const isCurrent = () => current(owner) && record === observed && absence === evidence && absence?.checks === checks &&
      !blockedMessage(context()) && !loading && !submitting && !observing && !retiring && canRetire() && ownsStored(observed);
    if (!await confirmAction({ title: 'Retire unrecorded test request?',
      message: `Retire unrecorded request ${observed.operationId}? Repeated checks found no server record, but a delayed request may still commit and a message may still arrive. Retirement does not cancel it. The reference will be retained for operator review. No new email is requested by this action.`,
      confirmLabel: 'Retire request', tone: 'danger', isCurrent }) || !isCurrent() || reconcileStored(value)) return;
    retiring = true; render();
    try { await observe(owner, true, true); }
    finally {
      if (current(owner)) {
        retiring = false; render();
        if (record?.retired) (dom.send.disabled ? dom.refresh : dom.send).focus();
      }
    }
  }
  async function send() {
    const value = context();
    if (!disposed && value.active && key === currentKey(value) && reconcileStored(value)) return;
    if (disposed || !value.active || blockedMessage(value) || loading || submitting || retiring || saved?.state !== 'ready' || record?.pending) return;
    const owner = { generation, value: { ...value } };
    const attempt = { storageKey: storageKey(value), operationId: window.crypto.randomUUID(),
      id: null, pending: true, configurationVersion: saved.configurationVersion, intendedDeliveryMode: 'postmark' };
    if (!store(attempt)) {
      message('Enable session storage before sending a test so an uncertain result can be reviewed safely.'); render(); return;
    }
    record = attempt; absence = null; submitting = true; stopReads();
    message('Requesting test email…'); render();
    try {
      const response = await request(`/api/asap/staff/email-operations/test?organizationId=${value.organizationId}&operationId=${attempt.operationId}`,
        { method: 'POST', body: { configurationVersion: attempt.configurationVersion, intendedDeliveryMode: attempt.intendedDeliveryMode } });
      const data = response?.data;
      if (!['queued', 'suppressed'].includes(response?.code) || !canonicalId(data?.id) || typeof data.version !== 'string' || !data.version) {
        throw new Error('The test request acknowledgement is unconfirmed.');
      }
      attempt.id = data.id; attempt.pending = response.code !== 'suppressed'; updateStored(attempt);
      if (!current(owner) || record !== attempt) return;
      message(response.code === 'suppressed'
        ? `${reasons[data.code] || 'The test was suppressed; no email was sent.'} Reference ${data.id}.`
        : `Test email queued. Waiting for the email service. Reference ${data.id}.`);
      deadline = Date.now() + observationLimit;
    } catch (error) {
      const uncertain = error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error);
      if (!uncertain) {
        attempt.pending = false; attempt.rejected = true; attempt.rejectionCode = error.response?.code;
        attempt.retryAfterSeconds = error.response?.data?.retryAfterSeconds; updateStored(attempt);
      }
      if (!current(owner) || record !== attempt) return;
      const code = error.response?.code;
      if (code === 'email_test_configuration_changed') { saved = null; dom.context.replaceChildren(); }
      message(uncertain ? 'The test request outcome is unconfirmed. Refresh its status before requesting another test.'
        : code === 'email_test_cooldown' ? `Wait ${error.response?.data?.retryAfterSeconds || 60} seconds before requesting another test.`
          : reasons[code] || 'The test was not queued. Refresh the saved configuration before trying again.');
    } finally {
      if (current(owner) && record === attempt) {
        submitting = false; render();
        if (attempt.id && attempt.pending) void observe(owner);
      }
    }
  }
  function invalidate() {
    generation += 1; stopReads(); loading = false; submitting = false; retiring = false; absence = null;
    key = null; saved = null; record = null; dom.context.replaceChildren(); message(''); render();
    renderHistory(context());
  }
  function sync() {
    if (disposed) return;
    const value = context(), nextKey = currentKey(value);
    if (!value.active || !nextKey) { if (key !== null) invalidate(); render(); return; }
    if (key !== nextKey) {
      invalidate(); key = nextKey; record = restore(value);
      if (record) message(restoredMessage(record));
    }
    reconcileStored(value);
    renderHistory(value); render();
    if (blockedMessage(value)) { stopReads(); return; }
    const owner = { generation, value: { ...value } };
    if (!saved && !loading) void loadContext(owner);
    if (record && !record.rejected && !record.retired && !submitting && !observing && timer === null) { deadline = Date.now() + observationLimit; void observe(owner); }
  }
  dom.send.addEventListener('click', () => void send(), { signal: events.signal });
  dom.retire.addEventListener('click', () => void retire(), { signal: events.signal });
  dom.refresh.addEventListener('click', () => {
    const owner = { generation, value: { ...context() } };
    if (record) void observe(owner, true); else void loadContext(owner);
  }, { signal: events.signal });
  dom.operations.addEventListener('click', () => onOperations(context().organizationId, record?.id), { signal: events.signal });
  render();
  function adopt(organizationId, operationId, row) {
    const value = context();
    if (!value.ownerKey || !uuid(operationId) || !canonicalId(row?.id) || row.organizationId !== organizationId) return false;
    const target = { ...value, organizationId }, prior = restore(target);
    if (prior?.pending && prior.operationId !== operationId) return false;
    const recovered = { storageKey: storageKey(target), operationId, id: row.id,
      pending: row.status === 'pending' || row.status === 'sending' ||
        row.status === 'failed' && (unknownCodes.has(row.lastErrorCode) || !row.lastErrorCode) };
    if (!store(recovered)) return false;
    if (value.organizationId === organizationId) {
      record = recovered; message(emailTestStatusMessage(row)); render();
    }
    return true;
  }
  return { sync, invalidate, send, adopt, refresh: () => observe({ generation, value: { ...context() } }, true),
    dispose() { if (disposed) return; invalidate(); disposed = true; events.abort(); } };
}
