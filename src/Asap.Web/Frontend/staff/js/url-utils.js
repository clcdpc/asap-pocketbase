export const statusStages = Object.freeze([
  'suggestion',
  'outstanding_purchase',
  'pending_hold',
  'hold_placed',
  'additional_copies',
  'closed',
  'settings',
  'analytics',
  'operations',
  'profile'
]);

export const stageQueryMap = Object.freeze({
  submitted: 'suggestion',
  suggestion: 'suggestion',
  new: 'suggestion',
  purchased_waiting_for_bib: 'outstanding_purchase',
  outstanding_purchase: 'outstanding_purchase',
  pending_hold: 'pending_hold',
  hold_placed: 'hold_placed',
  additional_copies: 'additional_copies',
  closed: 'closed',
  settings: 'settings',
  operations: 'operations',
  analytics: 'analytics',
  profile: 'profile'
});

export function readUrl(href) {
  return new URL(href === undefined || href === null ? window.location.href : String(href));
}

export function historyPath(url) {
  return `${url.pathname}${url.search}${url.hash}`;
}

// Entries carry an explicit origin and position. A rejected traversal returns to
// the original entry instead of pushing a duplicate route or overwriting its neighbor.
export function requestedStatusFromUrl(href) {
  const params = readUrl(href).searchParams;
  const raw = String(params.get('stage') || params.get('status') || '').trim();
  return stageQueryMap[raw] || '';
}

export function requestedRequestIdFromUrl(href) {
  const value = readUrl(href).searchParams.get('request');
  return String(value || '').trim();
}

export function requestedSettingsPanelFromUrl(href) {
  const hash = readUrl(href).hash;
  const panel = hash.startsWith('#settings-') ? hash.slice('#settings-'.length) : '';
  return ['start', 'polaris', 'smtp', 'staff', 'workflow', 'patron', 'templates'].includes(panel)
    ? panel : '';
}

export function requestedSettingsScopeFromUrl(href) {
  return readUrl(href).searchParams.get('settingsScope') || '';
}

export function requestedOperationalScopeFromUrl(href) {
  return readUrl(href).searchParams.get('scope') || '';
}

export function requestedCopyStatusFromUrl(href) {
  return readUrl(href).searchParams.get('copyStatus') === 'closed' ? 'closed' : 'open';
}

export function applyQueueContext(url, stage, context) {
  if (context.scope !== undefined) url.searchParams.set('scope', String(context.scope));
  if (stage === 'additional_copies' && context.copyStatus !== undefined) {
    url.searchParams.set('copyStatus', context.copyStatus === 'closed' ? 'closed' : 'open');
  } else if (stage !== 'additional_copies') {
    url.searchParams.delete('copyStatus');
  }
  if (!['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed', 'additional_copies'].includes(stage)) {
    url.searchParams.delete('scope');
  }
}

export function replaceRequestUrl(href, id, additionalCopy = false) {
  const url = readUrl(href);
  const normalizedId = id === null || id === undefined ? '' : String(id).trim();
  if (normalizedId) url.searchParams.set('request', normalizedId);
  else url.searchParams.delete('request');
  if (additionalCopy) url.searchParams.set('stage', 'additional_copies');
  return historyPath(url);
}

export function replaceStageUrl(href, stage, context = {}) {
  const url = readUrl(href);
  url.searchParams.delete('request');
  if (stage) url.searchParams.set('stage', String(stage));
  else url.searchParams.delete('stage');
  if (stage !== 'settings') {
    url.searchParams.delete('settingsScope');
    if (url.hash.startsWith('#settings-')) url.hash = '';
  }
  applyQueueContext(url, stage, context);
  return historyPath(url);
}

export function requestUrl(href, id, stage, context = {}) {
  const url = readUrl(href);
  url.searchParams.set('request', String(id));
  url.searchParams.set('stage', stage);
  url.searchParams.delete('settingsScope');
  applyQueueContext(url, stage, context);
  url.hash = '';
  return historyPath(url);
}
