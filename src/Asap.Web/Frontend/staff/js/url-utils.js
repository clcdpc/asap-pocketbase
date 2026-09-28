export const statusStages = Object.freeze([
  'suggestion',
  'outstanding_purchase',
  'pending_hold',
  'hold_placed',
  'additional_copies',
  'closed',
  'settings',
  'analytics'
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
  analytics: 'analytics'
});

function readUrl(href) {
  return new URL(href === undefined || href === null ? window.location.href : String(href));
}

function historyPath(url) {
  return `${url.pathname}${url.search}${url.hash}`;
}

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

export function replaceRequestUrl(href, id, additionalCopy = false) {
  const url = readUrl(href);
  const normalizedId = id === null || id === undefined ? '' : String(id).trim();
  if (normalizedId) url.searchParams.set('request', normalizedId);
  else url.searchParams.delete('request');
  if (additionalCopy) url.searchParams.set('stage', 'additional_copies');
  return historyPath(url);
}

export function replaceStageUrl(href, stage) {
  const url = readUrl(href);
  url.searchParams.delete('request');
  if (stage) url.searchParams.set('stage', String(stage));
  else url.searchParams.delete('stage');
  if (stage !== 'settings') {
    url.searchParams.delete('settingsScope');
    if (url.hash.startsWith('#settings-')) url.hash = '';
  }
  return historyPath(url);
}

export function requestUrl(href, id, stage) {
  const url = readUrl(href);
  url.searchParams.set('request', String(id));
  url.searchParams.set('stage', stage);
  url.hash = '';
  return historyPath(url);
}

export function pushRequestParameter(id, stage) {
  window.history.pushState(null, '', requestUrl(window.location.href, id, stage));
}

export function pushStageParameter(stage) {
  window.history.pushState(null, '', replaceStageUrl(window.location.href, stage));
}

export function pushSettingsPanelParameter(panel) {
  const url = readUrl();
  url.searchParams.delete('request');
  url.searchParams.set('stage', 'settings');
  url.hash = `settings-${panel}`;
  window.history.pushState(null, '', historyPath(url));
}

export function pushSettingsScopeParameter(scope) {
  const url = readUrl();
  url.searchParams.set('stage', 'settings');
  url.searchParams.set('settingsScope', String(scope));
  window.history.pushState(null, '', historyPath(url));
}

export function pushSettingsRouteParameter(scope, panel) {
  const url = readUrl();
  url.searchParams.delete('request');
  url.searchParams.set('stage', 'settings');
  url.searchParams.set('settingsScope', String(scope));
  url.hash = `settings-${panel}`;
  window.history.pushState(null, '', historyPath(url));
}

export function replaceRequestParameter(id, additionalCopy = false) {
  window.history.replaceState(null, '', replaceRequestUrl(window.location.href, id, additionalCopy));
}

export function replaceStageParameter(stage) {
  window.history.replaceState(null, '', replaceStageUrl(window.location.href, stage));
}
