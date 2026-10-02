import { readUrl, historyPath, applyQueueContext, replaceStageUrl, replaceRequestUrl, requestUrl,
  requestedStatusFromUrl, requestedRequestIdFromUrl, requestedSettingsPanelFromUrl,
  requestedSettingsScopeFromUrl, requestedOperationalScopeFromUrl, requestedCopyStatusFromUrl } from './url-utils.js';

export function parseStaffRoute(href) {
  const stage = requestedStatusFromUrl(href);
  return Object.freeze({ stage, requestId: requestedRequestIdFromUrl(href),
    scope: requestedOperationalScopeFromUrl(href), copyStatus: requestedCopyStatusFromUrl(href),
    settingsScope: requestedSettingsScopeFromUrl(href), panel: requestedSettingsPanelFromUrl(href) });
}

export function createRouter() {
  let accepted = null;
  let restoring = false;
  let closing = false;
  let closingOrigin = null;
  const events = new window.AbortController();
  function initializeStaffHistory() {
    if (!window.history.state?.asapStaff) {
      window.history.replaceState({ asapStaff: { session: window.crypto.randomUUID(), index: 0 } }, '', window.location.href);
    }
  }

  function staffHistorySnapshot() {
    return { href: window.location.href, state: window.history.state };
  }

  function restoreStaffHistory(snapshot) {
    const current = window.history.state?.asapStaff;
    const previous = snapshot.state?.asapStaff;
    if (current && previous && current.session === previous.session && current.index !== previous.index) {
      window.history.go(previous.index - current.index);
      return true;
    }
    window.history.replaceState(snapshot.state, '', snapshot.href);
    return false;
  }

  function writeHistory(path, replace = false, detailOrigin = null) {
    initializeStaffHistory();
    const previous = window.history.state.asapStaff;
    const currentPath = historyPath(readUrl());
    if (!replace && path === currentPath) return;
    const marker = { session: previous.session, index: previous.index + (replace ? 0 : 1), detailOrigin };
    window.history[replace ? 'replaceState' : 'pushState']({ asapStaff: marker }, '', path);
  }

  function closeDetailHistory(stage, context = {}) {
    const marker = window.history.state?.asapStaff;
    const path = replaceStageUrl(window.location.href, stage, context);
    if (marker?.detailOrigin && marker.detailOrigin.index === marker.index - 1 &&
        marker.detailOrigin.path === path) {
      window.history.back();
      return true;
    }
    writeHistory(path, true);
    return false;
  }

  function pushRequestParameter(id, stage, context = {}) {
    initializeStaffHistory();
    const marker = window.history.state.asapStaff;
    const origin = { index: marker.index, path: historyPath(readUrl()) };
    writeHistory(requestUrl(window.location.href, id, stage, context), false, origin);
  }

  function pushStageParameter(stage, context = {}) {
    writeHistory(replaceStageUrl(window.location.href, stage, context));
  }

  function pushSettingsPanelParameter(panel) {
    const url = readUrl();
    url.searchParams.delete('request');
    url.searchParams.set('stage', 'settings');
    url.hash = `settings-${panel}`;
    url.searchParams.delete('scope');
    url.searchParams.delete('copyStatus');
    writeHistory(historyPath(url));
  }

  function pushSettingsScopeParameter(scope) {
    const url = readUrl();
    url.searchParams.set('stage', 'settings');
    url.searchParams.set('settingsScope', String(scope));
    writeHistory(historyPath(url));
  }

  function pushSettingsRouteParameter(scope, panel) {
    const url = readUrl();
    url.searchParams.delete('request');
    url.searchParams.set('stage', 'settings');
    url.searchParams.set('settingsScope', String(scope));
    url.hash = `settings-${panel}`;
    url.searchParams.delete('scope');
    url.searchParams.delete('copyStatus');
    writeHistory(historyPath(url));
  }

  function replaceRequestParameter(id, additionalCopy = false, context = {}, stage = null) {
    const url = new URL(replaceRequestUrl(window.location.href, id, additionalCopy), window.location.href);
    if (stage) url.searchParams.set('stage', stage);
    applyQueueContext(url, requestedStatusFromUrl(url.href), context);
    writeHistory(historyPath(url), true, window.history.state?.asapStaff?.detailOrigin);
  }

  function replaceStageParameter(stage, context = {}) {
    writeHistory(replaceStageUrl(window.location.href, stage, context), true);
  }

  function remember() { accepted = Object.freeze(staffHistorySnapshot()); }
  return {
    requested: () => parseStaffRoute(window.location.href),
    snapshot: () => accepted,
    remember,
    busy: () => restoring || closing,
    reject() { if (accepted) restoring = restoreStaffHistory(accepted); },
    closeDetail(stage, context) {
      closingOrigin = window.history.state?.asapStaff?.detailOrigin || null;
      closing = closeDetailHistory(stage, context);
      if (!closing) remember();
      return closing;
    },
    pushRequest: pushRequestParameter, pushStage: pushStageParameter,
    replaceRequest: replaceRequestParameter, replaceStage: replaceStageParameter,
    pushSettingsPanel: pushSettingsPanelParameter, pushSettingsScope: pushSettingsScopeParameter,
    pushSettingsRoute: pushSettingsRouteParameter,
    start(onTraversal) {
      initializeStaffHistory(); remember();
      window.addEventListener('popstate', () => {
        if (restoring) {
          const current = window.history.state?.asapStaff;
          const target = accepted?.state?.asapStaff;
          if (current?.session !== target?.session || current?.index !== target?.index || window.location.href !== accepted?.href) {
            restoring = restoreStaffHistory(accepted);
            return;
          }
          restoring = false;
          return;
        }
        if (closing) {
          closing = false;
          if (window.history.state?.asapStaff?.index === closingOrigin?.index &&
              historyPath(readUrl()) === closingOrigin?.path) { remember(); return; }
        }
        void onTraversal();
      }, { signal: events.signal });
      window.addEventListener('hashchange', () => {
        if (requestedStatusFromUrl() === 'settings' && !restoring && accepted?.href !== window.location.href) void onTraversal();
      }, { signal: events.signal });
    },
    dispose() { events.abort(); }
  };
}
