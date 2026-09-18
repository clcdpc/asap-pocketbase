import { authorizedJson, isAbortError } from './http.js';

const TERMINAL_STATUSES = new Set(['sent', 'failed', 'suppressed']);

export function createEmailTestRequestId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, character => {
    const random = Math.floor(Math.random() * 16);
    const value = character === 'x' ? random : (random & 0x3) | 0x8;
    return value.toString(16);
  });
}

function scopeQuery(scope) {
  if (scope === null || scope === undefined || scope === '' || scope === 'all') return '';
  const value = String(scope) === 'system' ? '1' : String(scope);
  return `?organizationId=${encodeURIComponent(value)}`;
}

function statusUrl(id, scope) {
  return `/api/asap/staff/email-operations/${encodeURIComponent(String(id))}${scopeQuery(scope)}`;
}

export function loadEmailTestContext(scope, options = {}) {
  return authorizedJson(
    `/api/asap/staff/email-operations/test-context${scopeQuery(scope)}`,
    options
  );
}

export function queueEmailTest(scope, requestId, options = {}) {
  return authorizedJson(
    `/api/asap/staff/email-operations/test${scopeQuery(scope)}`,
    {
      ...options,
      method: 'POST',
      body: { requestId }
    }
  );
}

export function loadEmailTestStatus(id, scope, options = {}) {
  return authorizedJson(statusUrl(id, scope), options);
}

export function isEmailTestTerminal(status) {
  return TERMINAL_STATUSES.has(String(status || '').toLowerCase());
}

function wait(delay, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('The operation was aborted.', 'AbortError'));
      return;
    }
    const timer = setTimeout(resolve, delay);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      reject(new DOMException('The operation was aborted.', 'AbortError'));
    }, { once: true });
  });
}

export async function waitForEmailTest({
  id,
  scope,
  signal,
  onUpdate,
  intervalMs = 2000,
  timeoutMs = 60000
}) {
  const started = Date.now();
  let latest = null;
  while (Date.now() - started <= timeoutMs) {
    const response = await loadEmailTestStatus(id, scope, { signal });
    latest = response?.data ?? response;
    onUpdate?.(latest);
    if (isEmailTestTerminal(latest?.status)) return { item: latest, timedOut: false };
    const remaining = timeoutMs - (Date.now() - started);
    if (remaining <= 0) break;
    await wait(Math.min(intervalMs, remaining), signal);
  }
  return { item: latest, timedOut: true };
}

export function emailTestStatusMessage(item, timedOut = false) {
  if (timedOut) return 'The test email is still processing. Check Email operations for the latest status.';
  const status = String(item?.status || '').toLowerCase();
  if (status === 'sent') {
    if (item?.deliveryMode === 'capture') {
      return 'The test message was captured locally; no mailbox delivery occurred.';
    }
    return item?.providerMessageId
      ? `The provider accepted the test email. Reference ${item.providerMessageId}.`
      : 'The provider accepted the test email.';
  }
  if (status === 'suppressed') return `The test email was suppressed: ${item?.suppressionReason || 'not configured'}.`;
  if (status === 'failed') return `The test email failed: ${item?.lastErrorCode || 'transport failure'}.`;
  if (status === 'sending') return 'The test email is being sent…';
  return 'The test email is queued for delivery…';
}

export function emailTestContextMessage(context) {
  if (!context) return 'Test email readiness is unavailable.';
  if (context.canSend) return 'Ready to send a real test email using the saved settings for this scope.';
  if (context.blockingReason === 'non_delivery_mode') {
    return 'Live email sending is disabled in this environment. Switch the configured transport to live before sending.';
  }
  if (context.blockingReason === 'mail_credentials_missing' || context.blockingReason === 'mail_credentials_invalid') {
    return 'Live email credentials are not ready. Save a valid Postmark server token for this scope or its system fallback.';
  }
  if (context.blockingReason === 'sender_missing') return 'Save a From email address before sending a test email.';
  if (context.blockingReason === 'sender_invalid') return 'Save a valid From email address before sending a test email.';
  if (context.blockingReason === 'recipient_missing_or_invalid') return 'Your primary notification email is missing or invalid.';
  if (context.blockingReason === 'recipient_domain_not_allowed') return 'Your primary notification email is outside the configured recipient allowlist.';
  if (context.blockingReason === 'organization_inactive') return 'This organization is inactive.';
  return `Test email is not ready: ${context.blockingReason || 'configuration unavailable'}.`;
}

export function isEmailTestAbort(error) {
  return isAbortError(error) || error?.name === 'AbortError';
}
