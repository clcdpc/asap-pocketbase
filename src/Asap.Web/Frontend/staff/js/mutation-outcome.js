export function unconfirmedResponseError() {
  return Object.assign(new Error('The server response did not confirm the workflow result.'), { status: 0 });
}

export function notificationOutcome(status, reason, label = 'Notification') {
  if (!status || status === 'not_requested' || status === 'not_applicable') return { text: '', partial: false };
  const explanation = reason ? ` (${String(reason).replaceAll('_', ' ')})` : '';
  if (status === 'queued') return { text: ` ${label} queued; delivery is pending.`, partial: false };
  if (status === 'suppressed') return { text: ` ${label} suppressed${explanation}.`, partial: true };
  if (status === 'dispatch_failed') return { text: ` ${label} could not be queued${explanation}.`, partial: true };
  return { text: ` ${label} ${String(status).replaceAll('_', ' ')}${explanation}.`, partial: true };
}

export function isCommittedRequestResponse(result, requestId) {
  if (result?.committed !== true) return false;
  if (result.request === null) {
    return result.refreshUnavailable === true && typeof result.finalStatus === 'string' && Boolean(result.finalStatus);
  }
  const detail = result.request || result;
  return typeof detail.id === 'string' && detail.id === String(requestId) &&
    typeof detail.version === 'string' && Boolean(detail.version) &&
    typeof detail.status === 'string' && Boolean(detail.status);
}
