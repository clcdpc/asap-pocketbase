import { createSuggestionController } from './suggestion-controller.js';
import { renderCustomFieldEditor } from './custom-fields.js';
import { unconfirmedResponseError, notificationOutcome, isCommittedRequestResponse } from './mutation-outcome.js';
import {
  authorizedJson,
  isAbortError,
  latestLoads,
  loadStaffSession,
  onSessionInvalid,
  onAccessUnavailable
} from './http.js';
import { createSettingsController } from './settings.js';
import { createTitleDetailController } from './title-detail.js';
import { createOperationsController } from './operations-controller.js';
import { createProfileController } from './profile-controller.js';
import { createSessionIdentity } from './session-identity.js';
import {
  forgetRecentRequest,
  readRecentRequests,
  recentStorageKey,
  rememberRecentRequest,
  validRequestId
} from './recent-requests.js';
import { applyPolarisResultToControls, createPolarisLookup, renderResearchLinks, selectedStaffBibId, positivePolarisId } from './research.js';
import { loadAnalytics, resetAnalytics } from './analytics.js';
import { sanitizedHtmlFragment } from '../../shared/html.js';
import { requestedRequestIdFromUrl, requestedSettingsPanelFromUrl, requestedStatusFromUrl } from './url-utils.js';
import { createDetailHost } from './detail-host.js';
import { createCopyDetailController } from './copy-detail.js';
import { createCopyCreationController } from './copy-creation.js';
import { createTitleQueue, createCopyQueue } from './queues.js';
import { createRouter } from './router.js';
import { createNavigationController } from './navigation.js';

import { STATUS_LABELS, element, icon, commandButton, text, dateTime, statusLabel, closeReasonLabel, timeoutLabel, addDetail, labeledInput, selectWithHistorical } from './ui.js';

function currentRequestParameter() {
  return requestedRequestIdFromUrl();
}

function currentStageParameter() {
  return requestedStatusFromUrl();
}

export function createWorkflowApp() {
  const sessionIdentity = createSessionIdentity();
  const router = createRouter();
  const dom = {
    status: document.querySelector('#app-status'),
    emailReadinessWarning: document.querySelector('#email-readiness-warning'),
    signedOut: document.querySelector('#signed-out'),
    signedOutMessage: document.querySelector('#signed-out-message'),
    workspace: document.querySelector('#workspace'),
    sessionActions: document.querySelector('#session-actions'),
    staffIdentity: document.querySelector('#staff-identity'),
    recentWork: document.querySelector('#recent-work'),
    recentList: document.querySelector('#recent-request-list'),
    signOut: document.querySelector('#sign-out'),
    queueView: document.querySelector('#queue-view'),
    additionalCopyView: document.querySelector('#additional-copy-view'),
    analyticsView: document.querySelector('#analytics-view'),
    analyticsContainer: document.querySelector('#analytics-container'),
    profileView: document.querySelector('#profile-view'),
    operationsView: document.querySelector('#operations-view'),
    operationsTab: document.querySelector('#operations-view-tab'),
    settingsView: document.querySelector('#settings-view'),
    settingsTab: document.querySelector('#settings-view-tab'),
    viewTabs: [...document.querySelectorAll('.view-tab')],
    bulkDelete: document.querySelector('#bulk-delete-closed'),
    bulkDeleteCopies: document.querySelector('#bulk-delete-closed-copies'),
    bulkDeleteDialog: document.querySelector('#bulk-delete-dialog'),
    bulkDeleteClose: document.querySelector('#bulk-delete-close'),
    bulkDeleteScope: document.querySelector('#bulk-delete-scope'),
    bulkDeletePreview: document.querySelector('#bulk-delete-preview'),
    bulkDeleteSummary: document.querySelector('#bulk-delete-summary'),
    bulkDeleteItems: document.querySelector('#bulk-delete-items'),
    bulkDeleteConfirmation: document.querySelector('#bulk-delete-confirmation'),
    bulkDeleteExecute: document.querySelector('#bulk-delete-execute'),
    bulkDeleteResults: document.querySelector('#bulk-delete-results'),
    newSuggestion: document.querySelector('#new-suggestion'),
    dialog: document.querySelector('#request-dialog'),
    dialogBody: document.querySelector('#request-dialog-body'),
    staffSuggestionDialog: document.querySelector('#staff-suggestion-dialog'),
    staffSuggestionForm: document.querySelector('#staff-suggestion-form'),
    staffSuggestionStatus: document.querySelector('#staff-suggestion-status'),
    staffSuggestionBody: document.querySelector('#staff-suggestion-body'),
    staffSuggestionActions: document.querySelector('#staff-suggestion-actions'),
    closeStaffSuggestion: document.querySelector('#close-staff-suggestion')
  };

  const state = {
    staff: null,
    partialSessionFailureMessage: null,
    settingsCommitPendingRefresh: null,
    partialSessionFailureOwner: null,
    partialSessionFailureDetailAvailable: false,
    partialSessionFailureAfterQueueSequence: null,
    bulkDeleteState: null,
    recentKey: null
  };

  const navigation = createNavigationController({ router, sessionIdentity, announce,
    present: presentView, onInvalidate: invalidateFeatureReads,
    closeTransient: () => {
      if (detailHost.isOpen()) detailHost.requestClose({ navigation: true, guarded: true });
      if (suggestionController.isOpen()) suggestionController.close({ guarded: true, navigation: true, focusButton: false });
    },
    onContextChanged: (next, previous) => {
      titleQueue.contextChanged(next, previous);
      copyQueue.contextChanged(next, previous);
      updateBulkDeleteButtons();
      if (emailReadinessScopeKey(next) !== emailReadinessScopeKey(previous)) void refreshEmailReadiness();
    },
    getFeatures: () => [
      { key: 'bulk', inspectDeparture: () => ({ blocked: Boolean(state.bulkDeleteState?.submitting),
          message: 'Deletion is in progress. Wait for the complete ledger before navigating away.' }),
        discardDeparture: () => closeBulkDelete({ navigation: true }) },
      { key: 'profile', inspectDeparture: profileController.inspectDeparture,
        discardDeparture: () => { if (profileController.isDirty()) profileController.discardDraft(); } },
      { key: 'settings', inspectDeparture: settingsController.inspectDeparture,
        discardDeparture: () => { if (settingsController.isDirty()) settingsController.discardDraft(); } },
      { key: 'request', inspectDeparture: () => titleDetail.inspectDeparture() },
      { key: 'copy', inspectDeparture: () => copyDetail.inspectDeparture() },
      { key: 'suggestion', inspectDeparture: () => suggestionController.inspectDeparture(),
        reportBlocked: message => suggestionController.reportBlocked(message) }
    ],
    views: {
      queue: {
        activate: options => titleQueue.activate(options), deactivate: () => titleQueue.deactivate(),
        refresh: options => titleQueue.refresh(options), render: () => titleQueue.render(),
        refreshOnEntry: options => titleQueue.refreshOnEntry(options),
        setLibraries: libraries => populateScopes(libraries),
        openDetail: (id, opener, options = { align: true }) => titleDetail.open(id, opener, options),
        closeOverlay: () => detailHost.isOpen() ? detailHost.requestClose() : suggestionController.isOpen() ? suggestionController.close() : null
      },
      'additional-copies': {
        activate: options => copyQueue.activate(options), deactivate: () => copyQueue.deactivate(),
        refresh: options => copyQueue.refresh(options), render: () => copyQueue.render(),
        refreshOnEntry: options => copyQueue.refreshOnEntry(options),
        setLibraries: libraries => populateScopes(libraries),
        openDetail: id => copyDetail.open(id, null, { fromDeepLink: true }),
        closeOverlay: () => detailHost.isOpen() ? detailHost.requestClose() : suggestionController.isOpen() ? suggestionController.close() : null
      },
      settings: {
        activate: () => { void settingsController.activate(requestedSettingsPanelFromUrl() || undefined); },
        deactivate: () => settingsController.suspend(),
        currentScope: () => settingsController.currentScope(), currentPanel: () => settingsController.currentPanel(),
        setScopeFromUrl: scope => settingsController.setScopeFromUrl(scope)
      },
      operations: { activate: () => operationsController.activate(), deactivate: () => operationsController.deactivate(),
        refresh: options => operationsController.refresh(options), refreshOnEntry: () => operationsController.refresh() },
      profile: { activate: () => profileController.activate(), deactivate: () => profileController.deactivate() },
      analytics: { deactivate: resetAnalytics, refresh: () => loadAnalytics(dom.analyticsContainer),
        refreshOnEntry: () => loadAnalytics(dom.analyticsContainer) }
    }
  });

  const detailHost = createDetailHost({ root: document.querySelector('#request-dialog') });
  const titleQueue = createTitleQueue({ root: dom.queueView, sessionIdentity,
    getContext: () => navigation.context(), announce,
    onOpen: (id, opener) => titleDetail.open(id, opener, { history: 'push' }),
    onScopeIntent: scope => navigation.changeQueueContext('queue', { scope }),
    onStatusIntent: status => navigation.changeQueueContext('queue', { status }),
    onScopeAccepted: scope => navigation.align({ scope }), onLibraries: populateScopes,
    onRendered: updateBulkDeleteButtons,
    onRefreshed: ({ sequence }) => {
      renderRecentRequests();
      if (state.partialSessionFailureDetailAvailable && state.partialSessionFailureAfterQueueSequence !== null &&
          sequence > state.partialSessionFailureAfterQueueSequence) {
        state.partialSessionFailureMessage = null; state.partialSessionFailureOwner = null;
        state.partialSessionFailureDetailAvailable = false; state.partialSessionFailureAfterQueueSequence = null;
      }
    }
  });
  const copyCreation = createCopyCreationController({ root: document.querySelector('#additional-copy-create-dialog'),
    sessionIdentity, announce, onReceipt: recordFeatureReceipt, clearReceipt: clearCommittedSessionFallback,
    onRecoveryChanged: change => {
      if (!sessionIdentity.isCurrent(change.owner)) return;
      if (change.committed) copyQueue.markStale();
      if (change.uncertain) { copyQueue.invalidate(); copyQueue.render(); }
    },
    onParentChanged: (parent, opener) => parent.refresh(opener)
  });
  const copyDetail = createCopyDetailController({ host: detailHost, sessionIdentity, announce,
    beforeOpen: options => {
      if (!sessionIdentity.actor() || router.busy() || !options.authoritativeRefresh && !navigation.allow({ suggestion: false })) return false;
      return navigation.invalidate();
    },
    isNavigationCurrent: ticket => navigation.generation() === ticket,
    getNavigationGeneration: () => navigation.generation(),
    onAlign: async (request, options, owner, ticket) => {
      const statusChanged = options.fromDeepLink && request.status !== navigation.context().additionalCopyStatus && ['open', 'closed'].includes(request.status);
      const alignedScope = titleQueue.libraryScope(request.libraryOrgId);
      const scopeChanged = options.fromDeepLink && owner.role === 'super_admin' && navigation.context().scope !== 'all' && navigation.context().scope !== alignedScope;
      if (statusChanged || scopeChanged) {
        navigation.align({ ...(statusChanged ? { additionalCopyStatus: request.status } : {}), ...(scopeChanged ? { scope: alignedScope } : {}) });
        copyQueue.resetFilters();
        const refreshed = await copyQueue.refresh({ silent: true });
        if (refreshed !== true || navigation.generation() !== ticket || !sessionIdentity.isCurrent(owner)) return false;
      }
      return true;
    },
    onOpened: (request, options) => {
      if (options.history === 'push') pushRequestParameter(request.id, 'additional_copies'); else replaceRequestParameter(request.id, true);
    },
    beforeClose: () => navigation.allow({ settings: false, suggestion: false }),
    onClosed: options => { if (!options.navigation) { router.closeDetail('additional_copies', queueRouteContext()); if (!router.busy()) rememberRoute(); } },
    getFocusReturn: (id, opener) => copyQueue.focusReturn(id, opener),
    refreshQueue: options => copyQueue.refresh(options), onReceipt: recordFeatureReceipt, clearReceipt: clearCommittedSessionFallback
  });

  function recordFeatureReceipt(message, owner, attempt, evidence = {}) {
    if (state.staff && !sessionIdentity.isCurrent(owner)) return;
    state.partialSessionFailureMessage = message; state.partialSessionFailureOwner = attempt;
    state.partialSessionFailureDetailAvailable = evidence.detailAvailable === true;
    state.partialSessionFailureAfterQueueSequence = evidence.detailAvailable === true ? evidence.queueSequence : null;
    if (dom.workspace.hidden) dom.signedOutMessage.textContent = message;
  }

  const copyQueue = createCopyQueue({ root: dom.additionalCopyView, sessionIdentity,
    getContext: () => navigation.context(), announce,
    onOpen: (id, opener) => copyDetail.open(id, opener, { history: 'push' }),
    onScopeIntent: scope => navigation.changeQueueContext('additional-copies', { scope }),
    onStatusIntent: additionalCopyStatus => navigation.changeQueueContext('additional-copies', { additionalCopyStatus }),
    onScopeAccepted: scope => navigation.align({ scope }), onLibraries: populateScopes,
    onRendered: updateBulkDeleteButtons, onRefreshed() {},
    recovery: {
      current: () => copyCreation.review.current(), begin: () => copyCreation.review.begin(),
      loaded: (evidence, result) => copyCreation.review.loaded(evidence, result),
      acknowledge: () => copyCreation.review.acknowledge(navigation.context())
    }
  });

  function presentView(name) {
    dom.queueView.hidden = name !== 'queue';
    dom.additionalCopyView.hidden = name !== 'additional-copies';
    dom.analyticsView.hidden = name !== 'analytics';
    dom.profileView.hidden = name !== 'profile';
    dom.operationsView.hidden = name !== 'operations';
    dom.settingsView.hidden = name !== 'settings';
    for (const tab of dom.viewTabs) {
      const active = tab.dataset.view === name;
      tab.classList.toggle('active', active);
      if (active) tab.setAttribute('aria-current', 'page');
      else tab.removeAttribute('aria-current');
    }
    const heading = name === 'queue'
      ? '#queue-title'
      : name === 'additional-copies' ? '#additional-copy-title'
      : name === 'analytics' ? '#analytics-title'
      : name === 'profile' ? '#profile-title' : name === 'operations' ? '#operations-title' : '#settings-title';
    document.querySelector(heading).focus({ preventScroll: true });
  }

  const queueRouteContext = () => ({ scope: navigation.context().scope, copyStatus: navigation.context().additionalCopyStatus });
  function rememberRoute() { router.remember(); }
  function pushRequestParameter(id, stage) { router.pushRequest(id, stage, queueRouteContext()); rememberRoute(); }
  function pushStageParameter(stage) { router.pushStage(stage, queueRouteContext()); rememberRoute(); }
  function replaceStageParameter(stage) { router.replaceStage(stage, queueRouteContext()); rememberRoute(); }
  function replaceRequestParameter(id, copy = false) { router.replaceRequest(id, copy, queueRouteContext(), copy ? 'additional_copies' : navigation.context().status); rememberRoute(); }
  function pushSettingsPanelParameter(panel) { router.pushSettingsPanel(panel); rememberRoute(); }
  function pushSettingsScopeParameter(scope) { router.pushSettingsScope(scope); rememberRoute(); }
  function pushSettingsRouteParameter(scope, panel) { router.pushSettingsRoute(scope, panel); rememberRoute(); }

  function announce(message, kind = '') {
    dom.status.textContent = message || '';
    dom.status.className = `status-message${kind ? ` ${kind}` : ''}`;
  }

  const polarisLookup = createPolarisLookup({ authorizedJson, isAbortError, announce });

  const titleDetail = createTitleDetailController({ host: detailHost, sessionIdentity, polarisLookup, copyCreation, announce,
    beforeOpen: options => {
      if (!sessionIdentity.actor() || router.busy() || !options.authoritativeRefresh && !navigation.allow({ suggestion: false })) return false;
      return navigation.invalidate();
    },
    isNavigationCurrent: ticket => navigation.generation() === ticket,
    getNavigationGeneration: () => navigation.generation(), getScope: () => navigation.context().scope,
    onAlign: async (request, options, owner, ticket) => {
      if (options.reloaded) {
        if (request.status !== navigation.context().status) {
          navigation.align({ status: request.status }); titleQueue.clear();
          if (await titleQueue.refresh({ silent: true, skipDeepLink: true }) !== true) return false;
        }
      } else if (options.align || options.fromRecent) {
        const needsRefresh = navigation.context().status !== request.status || titleQueue.find(request.id)?.status !== request.status;
        const scope = titleQueue.libraryScope(request.libraryOrgId);
        const scopeChanged = owner.role === 'super_admin' && navigation.context().scope !== 'all' && navigation.context().scope !== scope;
        titleQueue.resetFilters();
        if (STATUS_LABELS[request.status] && request.status !== 'open') navigation.align({ status: request.status });
        if (scopeChanged) { navigation.align({ scope }); copyQueue.clear(); }
        if (scopeChanged || needsRefresh) {
          titleQueue.clear();
          if (await titleQueue.refresh({ silent: true, skipDeepLink: true }) !== true) return false;
        } else titleQueue.render();
        navigation.switchView('queue', false);
      }
      return navigation.generation() === ticket && sessionIdentity.isCurrent(owner);
    },
    onOpened: (request, options) => {
      if (options.history === 'push' || options.fromRecent) pushRequestParameter(request.id, request.status);
      else replaceRequestParameter(request.id, false);
    },
    beforeClose: () => navigation.allow({ settings: false, suggestion: false }),
    onClosed: options => {
      if (!options.navigation) {
        router.closeDetail(navigation.context().status, queueRouteContext());
        if (!router.busy()) rememberRoute();
      }
    },
    getFocusReturn: (id, opener) => titleQueue.focusReturn(id, opener),
    refreshQueue: options => titleQueue.refresh(options), queueSequence: () => titleQueue.sequence(),
    rememberOpened: rememberOpenedRequest,
    forgetUnavailable: id => {
      const storage = recentStorage(); if (storage) forgetRecentRequest(storage, state.recentKey, id); renderRecentRequests();
    },
    onReceipt: recordFeatureReceipt, clearReceipt: clearCommittedSessionFallback
  });


  const suggestionController = createSuggestionController({ root: document.querySelector('#staff-suggestion-dialog'),
    trigger: dom.newSuggestion, sessionIdentity, polarisLookup, announce,
    getLibraries: () => titleQueue.libraries(),
    beforeOpen: () => {
      if (detailHost.isOpen()) {
        if (!detailHost.requestClose({ navigation: true })) return false;
        replaceStageParameter(navigation.context().status);
      }
      return true;
    },
    beforeClose: () => navigation.allow({ settings: false, request: false }),
    openCreatedTitle: intent => navigation.openCreatedTitle(intent), openExistingTitle: intent => navigation.openExistingTitle(intent),
    onReceipt: recordFeatureReceipt, clearReceipt: clearCommittedSessionFallback
  });

  const operationsController = createOperationsController({ root: dom.operationsView, sessionIdentity, announce,
    onScopeChange: () => { void refreshEmailReadiness(); },
    onReceipt: (message, owner, attempt) => {
      if (state.staff && !sessionIdentity.isCurrent(owner)) return;
      state.partialSessionFailureMessage = message;
      state.partialSessionFailureOwner = attempt;
      if (dom.workspace.hidden) dom.signedOutMessage.textContent = message;
    },
    clearReceipt: clearCommittedSessionFallback
  });

  const profileController = createProfileController({ root: dom.profileView, sessionIdentity, announce,
    onPreferences: (staff, owner) => {
      if (!updateStaffPreferences(staff, owner)) return false;
      titleQueue.preferencesChanged(); copyQueue.preferencesChanged();
      return true;
    },
    onSessionLost: showSignedOut, onAccessUnavailable: showAccessUnavailable,
    onReceipt: (message, owner, attempt) => {
      if (state.staff && !sessionIdentity.isCurrent(owner)) return;
      state.partialSessionFailureMessage = message;
      state.partialSessionFailureOwner = attempt;
      if (dom.workspace.hidden) dom.signedOutMessage.textContent = message;
    },
    clearReceipt: clearCommittedSessionFallback
  });

  const settingsController = createSettingsController({
    root: dom.settingsView,
    tab: dom.settingsTab,
    announce,
    getStaff: () => state.staff,
    onPanelChange: panel => {
      navigation.invalidate();
      pushSettingsPanelParameter(panel);
    },
    onScopeChange: scope => {
      navigation.invalidate();
      pushSettingsScopeParameter(scope);
      void refreshEmailReadiness();
    },
    onCommitted: (message = 'Settings saved.') => { state.settingsCommitPendingRefresh = message; },
    onRefreshed: () => {
      state.settingsCommitPendingRefresh = null;
      void refreshEmailReadiness();
    }
  });

  function emailReadinessScopeKey(context = navigation.context()) {
    const selectedScope = context.activeView === 'settings'
      ? settingsController.currentScope()
      : context.activeView === 'operations' ? operationsController.currentScope() : context.scope;
    return state.staff?.role === 'super_admin' && /^\d+$/.test(String(selectedScope))
      ? String(selectedScope) : 'default';
  }

  async function refreshEmailReadiness() {
    if (!state.staff) return;
    const owner = state.staff;
    const load = latestLoads.begin('email-readiness');
    const scopeKey = emailReadinessScopeKey();
    const query = scopeKey === 'default' ? '' : `?organizationId=${encodeURIComponent(scopeKey)}`;
    try {
      const result = await authorizedJson(`/api/asap/staff/email-readiness${query}`, { signal: load.signal });
      if (!load.isCurrent() || !sessionIdentity.isCurrent(owner)) return;
      const stateCode = result?.state;
      const warning = stateCode === 'not_configured'
        ? 'Email delivery is not configured for this scope. Requests and staff workflows remain available.'
        : stateCode === 'non_delivery'
          ? 'Live email delivery is disabled in this environment. Requests and staff workflows remain available.'
          : stateCode === 'unavailable'
            ? 'Email delivery status is unavailable. Requests and staff workflows remain available.'
            : '';
      dom.emailReadinessWarning.textContent = warning;
      dom.emailReadinessWarning.hidden = !warning;
    } catch (error) {
      if (load.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        dom.emailReadinessWarning.textContent = 'Email delivery status is unavailable. Requests and staff workflows remain available.';
        dom.emailReadinessWarning.hidden = false;
      }
    } finally {
      latestLoads.finish('email-readiness', load.token);
    }
  }

  function recentStorage() {
    try {
      return window.sessionStorage;
    } catch {
      return null;
    }
  }

  function renderRecentRequests() {
    const storage = recentStorage();
    const items = storage ? readRecentRequests(storage, state.recentKey) : [];
    dom.recentList.replaceChildren();
    if (!items.length) {
      dom.recentList.append(element('p', { text: 'No recently opened requests.' }));
      return;
    }
    for (const item of items) {
      const current = titleQueue.find(item.id);
      const label = current ? `${current.title} · Request ${item.id}` : `Request ${item.id}`;
      dom.recentList.append(element('button', {
        type: 'button',
        text: label,
        onclick: async () => {
          dom.recentWork.open = false;
          await titleDetail.open(item.id, dom.recentWork, { fromRecent: true });
        }
      }));
    }
  }

  function rememberOpenedRequest(id) {
    const storage = recentStorage();
    if (!storage || !state.recentKey || !validRequestId(id)) return;
    rememberRecentRequest(storage, state.recentKey, id);
    renderRecentRequests();
  }

  function invalidateFeatureReads() {
    titleDetail.invalidate();
    copyDetail.invalidate();
    titleQueue.invalidate();
    copyQueue.invalidate();
    latestLoads.begin('settings-route').abort();
    latestLoads.begin('operational-scope').abort();
    suggestionController.invalidate();
    operationsController.deactivate();
  }

  function updateBulkDeleteButtons() {
    const authorized = ['admin', 'super_admin'].includes(state.staff?.role);
    dom.bulkDelete.hidden = !authorized || navigation.context().status !== 'closed';
    dom.bulkDeleteCopies.hidden = !authorized || navigation.context().additionalCopyStatus !== 'closed';
  }

  function closeBulkDelete(options = {}) {
    const batch = state.bulkDeleteState;
    if (!batch) return;
    if (batch.submitting && !options.force) {
      dom.bulkDeleteSummary.textContent = 'Deletion is in progress. Wait for the result before closing.';
      return;
    }
    batch.previewAbort?.abort();
    state.bulkDeleteState = null;
    if (dom.bulkDeleteDialog.open) dom.bulkDeleteDialog.close();
    dom.bulkDeleteItems.replaceChildren();
    dom.bulkDeleteResults.replaceChildren();
    dom.bulkDeleteSummary.textContent = '';
    dom.bulkDeleteConfirmation.value = '';
    dom.bulkDeleteExecute.disabled = true;
    if (!options.navigation && batch.returnFocus?.isConnected && state.staff) batch.returnFocus.focus();
  }

  function resetBulkDeletePreview() {
    const batch = state.bulkDeleteState;
    if (!batch || batch.submitting) return;
    batch.previewAbort?.abort();
    batch.previewAbort = null;
    batch.snapshot = null;
    dom.bulkDeleteSummary.textContent = '';
    dom.bulkDeleteItems.replaceChildren();
    dom.bulkDeleteResults.replaceChildren();
    dom.bulkDeleteConfirmation.value = '';
    dom.bulkDeleteExecute.disabled = true;
    dom.bulkDeletePreview.disabled = false;
    dom.bulkDeleteScope.disabled = false;
    dom.bulkDeleteConfirmation.disabled = false;
  }

  function openBulkDelete(returnFocus) {
    if (!['admin', 'super_admin'].includes(state.staff?.role)) return;
    const batch = { returnFocus, previewAbort: null, snapshot: null, submitting: false, ledger: [] };
    state.bulkDeleteState = batch;
    dom.bulkDeleteScope.replaceChildren();
    if (state.staff.role === 'super_admin') {
      dom.bulkDeleteScope.append(element('option', { value: '', text: 'Choose a library scope' }));
      dom.bulkDeleteScope.append(element('option', { value: 'all', text: 'All libraries' }));
      for (const library of titleQueue.libraries()) {
        dom.bulkDeleteScope.append(element('option', { value: library.id, text: library.name }));
      }
    } else {
      dom.bulkDeleteScope.append(element('option', {
        value: String(state.staff.organizationId), text: state.staff.organizationName || 'My library'
      }));
    }
    dom.bulkDeleteScope.value = state.staff.role === 'super_admin' ? '' : String(state.staff.organizationId);
    resetBulkDeletePreview();
    dom.bulkDeleteDialog.showModal();
    (state.staff.role === 'super_admin' ? dom.bulkDeleteScope : dom.bulkDeletePreview).focus();
  }

  function bulkItemLabel(item) {
    return (item.type === 'title_request' ? 'Title request ' : 'Additional-copy task ') +
      item.id + ' (' + item.libraryOrgName + ')';
  }

  function validateBulkItem(item, type, scope) {
    return item?.type === type && typeof item.id === 'string' && /^[1-9]\d*$/.test(item.id) &&
      typeof item.version === 'string' && item.version.length > 0 && item.status === 'closed' &&
      Number.isSafeInteger(item.libraryOrgId) &&
      (scope === 'all' || String(item.libraryOrgId) === scope);
  }

  async function previewBulkDelete() {
    const batch = state.bulkDeleteState;
    const scope = dom.bulkDeleteScope.value;
    resetBulkDeletePreview();
    if (!batch || !scope) {
      dom.bulkDeleteSummary.textContent = 'Choose a library scope before previewing.';
      dom.bulkDeleteScope.focus();
      return;
    }
    const controller = new AbortController();
    batch.previewAbort = controller;
    dom.bulkDeletePreview.disabled = true;
    dom.bulkDeleteSummary.textContent = 'Loading closed title requests and additional-copy tasks...';
    try {
      const session = await loadStaffSession({ signal: controller.signal });
      if (state.bulkDeleteState !== batch || controller.signal.aborted) return;
      if (!session.authenticated) {
        showSignedOut();
        return;
      }
      if (!session.accessAllowed) {
        showAccessUnavailable();
        return;
      }
      const previewActor = session.staff;
      if (!previewActor || previewActor.id !== state.staff?.id ||
          !['admin', 'super_admin'].includes(previewActor.role) ||
          (scope === 'all' && previewActor.role !== 'super_admin') ||
          (previewActor.role === 'admin' && scope !== String(previewActor.organizationId)) ||
          typeof previewActor.version !== 'string' || !previewActor.version) {
        throw new Error('Staff role or library scope changed. Reload the workspace before previewing deletion.');
      }
      const query = encodeURIComponent(scope);
      const [titles, copies] = await Promise.all([
        authorizedJson('/api/asap/staff/title-requests?scope=' + query, { signal: controller.signal }),
        authorizedJson('/api/asap/staff/additional-copies?scope=' + query + '&status=closed',
          { signal: controller.signal })
      ]);
      if (state.bulkDeleteState !== batch || controller.signal.aborted) return;
      if (titles.scope !== scope || copies.scope !== scope || copies.status !== 'closed' ||
          !Array.isArray(titles.items) || !Array.isArray(copies.items)) {
        throw new Error('The requested deletion scope could not be verified.');
      }
      const titleItems = titles.items.filter(item => item.status === 'closed');
      const copyItems = copies.items;
      if (titleItems.some(item => !validateBulkItem(item, 'title_request', scope)) ||
          copyItems.some(item => !validateBulkItem(item, 'additional_copy', scope))) {
        throw new Error('The closed-work preview contained an invalid identity or scope.');
      }
      batch.snapshot = {
        scope,
        actorVersion: previewActor.version,
        items: [...titleItems, ...copyItems].map(item => ({
          type: item.type, id: item.id, version: item.version,
          title: item.title, libraryOrgName: item.libraryOrgName,
          libraryOrgId: item.libraryOrgId, closeReason: item.closeReason
        }))
      };
      const scopeName = dom.bulkDeleteScope.selectedOptions[0]?.textContent || scope;
      dom.bulkDeleteSummary.textContent = 'Preview for ' + scopeName + ': ' +
        titleItems.length + ' closed title requests and ' + copyItems.length +
        ' closed additional-copy tasks. Search, claim, tag, and grid filters do not affect this population.';
      dom.bulkDeleteItems.replaceChildren(...batch.snapshot.items.map(item =>
        element('li', { text: bulkItemLabel(item) + ' — ' + item.title +
          (item.type === 'title_request' ? ' — ' + closeReasonLabel(item.closeReason) : '') })));
      if (batch.snapshot.items.length === 0) {
        dom.bulkDeleteSummary.textContent += ' There are no eligible records to submit.';
      }
      dom.bulkDeleteConfirmation.focus();
    } catch (error) {
      if (state.bulkDeleteState === batch && !controller.signal.aborted && error.status !== 401) {
        dom.bulkDeleteSummary.textContent = error.message || 'The closed-work preview could not be loaded.';
      }
    } finally {
      if (state.bulkDeleteState === batch) {
        batch.previewAbort = null;
        dom.bulkDeletePreview.disabled = false;
      }
    }
  }

  function bulkOutcome(error) {
    const code = error.response?.code;
    if (error.status === 404 || code === 'not_found') return 'not_found';
    if (code === 'stale_version') return 'stale';
    if (code === 'actor_changed_since_preview') return 'actor_changed';
    if (code === 'request_not_closed' || code === 'delete_requires_closed') return 'not_closed';
    if (error.status === 401 || error.status === 403 ||
        ['delete_forbidden', 'staff_scope_forbidden', 'staff_session_invalid'].includes(code)) {
      return 'forbidden/out_of_scope';
    }
    if (code === 'hold_history_retained') return 'blocked_hold_history';
    if (isAbortError(error) || error.status === 408 || error.status >= 500 || !error.status) {
      return 'outcome_unconfirmed';
    }
    return 'operational_failure';
  }

  function renderBulkLedger(batch, ledger = batch.ledger) {
    const deleted = ledger.filter(item => item.outcome === 'deleted').length;
    const attempted = ledger.filter(item => item.outcome !== 'not_attempted').length;
    const summary = 'Confirmed deleted: ' + deleted + ' of ' + batch.snapshot.items.length +
      '. Attempted: ' + attempted + '. Every record is rechecked by the server.';
    dom.bulkDeleteResults.replaceChildren(
      element('p', { text: summary }),
      element('ul', {}, ledger.map(item =>
        element('li', { text: bulkItemLabel(item) + ': ' + item.outcome.replaceAll('_', ' ') })))
    );
    state.partialSessionFailureMessage = summary + ' ' + ledger.map(item =>
      bulkItemLabel(item) + ': ' + item.outcome.replaceAll('_', ' ')).join('; ') +
      '. Sign in again and refresh Closed work before retrying.';
    state.partialSessionFailureOwner = batch;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
    if (!state.staff) dom.signedOutMessage.textContent = state.partialSessionFailureMessage;
  }

  function retainInterruptedBulkLedger() {
    const batch = state.bulkDeleteState;
    if (!batch?.submitting || !batch.currentItem) return;
    const remaining = batch.snapshot.items.slice(batch.snapshot.items.indexOf(batch.currentItem) + 1);
    // Losing access through another request does not determine the outstanding DELETE's outcome.
    renderBulkLedger(batch, [...batch.ledger, { ...batch.currentItem, outcome: 'outcome_unconfirmed' },
      ...remaining.map(item => ({ ...item, outcome: 'not_attempted' }))]);
  }

  async function executeBulkDelete() {
    const batch = state.bulkDeleteState;
    if (!batch?.snapshot || batch.submitting || dom.bulkDeleteConfirmation.value !== 'DELETE' ||
        dom.bulkDeleteScope.value !== batch.snapshot.scope || batch.snapshot.items.length === 0) return;
    const owner = state.staff;
    const navigationGeneration = navigation.generation();
    const view = navigation.context().activeView;
    let expectedScope = navigation.context().scope;
    let expectedStatus = navigation.context().status;
    let expectedCopyStatus = navigation.context().additionalCopyStatus;
    let expectedRoute = router.snapshot()?.href;
    const isCurrentContext = () => state.bulkDeleteState === batch && sessionIdentity.isCurrent(owner) &&
      navigation.generation() === navigationGeneration && navigation.context().activeView === view &&
      navigation.context().scope === expectedScope && navigation.context().status === expectedStatus &&
      navigation.context().additionalCopyStatus === expectedCopyStatus && window.location.href === expectedRoute;
    batch.submitting = true;
    dom.bulkDeleteExecute.disabled = true;
    dom.bulkDeletePreview.disabled = true;
    dom.bulkDeleteScope.disabled = true;
    dom.bulkDeleteConfirmation.disabled = true;
    let stop = false;
    for (const item of batch.snapshot.items) {
      if (stop || state.bulkDeleteState !== batch || !sessionIdentity.isCurrent(owner)) {
        batch.ledger.push({ ...item, outcome: 'not_attempted' });
        continue;
      }
      const route = item.type === 'title_request'
        ? '/api/asap/staff/requests/' : '/api/asap/staff/additional-copies/';
      batch.currentItem = item;
      try {
        const result = await authorizedJson(route + encodeURIComponent(item.id),
          { method: 'DELETE', body: {
            version: item.version, actorVersion: batch.snapshot.actorVersion
          } });
        batch.ledger.push({ ...item, outcome: result?.deleted === true ? 'deleted' : 'outcome_unconfirmed' });
        if (result?.deleted !== true) stop = true;
      } catch (error) {
        const outcome = bulkOutcome(error);
        batch.ledger.push({ ...item, outcome });
        stop = outcome === 'outcome_unconfirmed' || outcome === 'actor_changed' ||
          error.status === 401 || error.status === 403;
      }
      batch.currentItem = null;
      if (state.bulkDeleteState === batch) renderBulkLedger(batch);
    }
    batch.submitting = false;
    if (state.bulkDeleteState !== batch) return;
    renderBulkLedger(batch);
    if (!state.staff) {
      state.bulkDeleteState = null;
      return;
    }
    dom.bulkDeleteSummary.textContent = 'Deletion finished. Review the ledger and refresh Closed work before retrying.';
    if (!isCurrentContext()) return;
    navigation.align({ scope: batch.snapshot.scope });
    navigation.align({ status: 'closed' });
    navigation.align({ additionalCopyStatus: 'closed' });
    expectedScope = navigation.context().scope;
    expectedStatus = navigation.context().status;
    expectedCopyStatus = navigation.context().additionalCopyStatus;
    replaceStageParameter(view === 'additional-copies' ? 'additional_copies' : 'closed');
    expectedRoute = router.snapshot().href;
    const queueRefreshed = await titleQueue.refresh({ skipDeepLink: true, silent: true });
    if (!isCurrentContext()) return;
    const copiesRefreshed = await copyQueue.refresh({ skipDeepLink: true, silent: true });
    if (!isCurrentContext()) return;
    if (queueRefreshed === true && copiesRefreshed === true) clearCommittedSessionFallback(batch);
    if (state.bulkDeleteState === batch) {
      dom.bulkDeleteSummary.textContent = queueRefreshed === true && copiesRefreshed === true
        ? 'Deletion finished. Both Closed views were refreshed from the server.'
        : 'Deletion finished, but a Closed view could not refresh. Review the ledger and refresh before retrying.';
      dom.bulkDeleteResults.focus();
    }
  }

  function showSignedOut(message) {
    const settingsMutationUnconfirmed = (settingsController.hasPendingMutation() ||
      settingsController.hasUnconfirmedOutcome()) && !state.settingsCommitPendingRefresh;
    navigation.invalidate();
    const storage = recentStorage();
    if (storage && state.recentKey) {
      try { storage.removeItem(state.recentKey); } catch { /* Storage may be unavailable. */ }
    }
    state.recentKey = null;
    dom.recentWork.open = false;
    dom.recentList.replaceChildren();
    if (state.bulkDeleteState) {
      state.bulkDeleteState.previewAbort?.abort();
      if (!state.bulkDeleteState.submitting) state.bulkDeleteState = null;
    }
    if (dom.bulkDeleteDialog.open) dom.bulkDeleteDialog.close();
    polarisLookup.close();
    latestLoads.begin('email-readiness').abort();
    dom.emailReadinessWarning.hidden = true;
    detailHost.cancelFocusReturn();
    suggestionController.signedOut();
    titleDetail.signedOut();
    detailHost.reset();
    copyCreation.signedOut(); copyDetail.signedOut();
    sessionIdentity.clear();
    state.staff = null;
    operationsController.signedOut();
    titleQueue.signedOut(); copyQueue.signedOut();
    profileController.signedOut();
    titleQueue.invalidate();
    copyQueue.invalidate();
    operationsController.deactivate();
    titleDetail.invalidate();
    copyDetail.invalidate();
    copyCreation.invalidate();
    resetAnalytics();
    settingsController.signedOut();
    dom.signedOutMessage.textContent = state.settingsCommitPendingRefresh
      ? `${state.settingsCommitPendingRefresh} Sign in again to review the current values.`
      : settingsMutationUnconfirmed
        ? 'Settings change outcome is uncertain. Sign in again and check saved values before retrying.'
        : state.partialSessionFailureMessage ||
      message || 'Sign in with your authorized library account.';
    dom.signedOut.hidden = false;
    dom.workspace.hidden = true;
    dom.sessionActions.hidden = true;
    announce('');
  }

  function clearCommittedSessionFallback(owner) {
    if (state.partialSessionFailureOwner !== owner || !state.staff) return;
    state.partialSessionFailureMessage = null;
    state.partialSessionFailureOwner = null;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
  }

  function retainUnconfirmedOutcome(message, owner) {
    state.partialSessionFailureMessage = `${message} Sign in again to check the authoritative result before retrying.`;
    state.partialSessionFailureOwner = owner;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
  }

  function updateStaffPreferences(staff, owner) {
    const preferences = sessionIdentity.updatePreferences(staff, owner);
    if (!preferences) {
      showSignedOut('The staff account or access changed. Reload this page to continue.');
      return false;
    }
    state.staff = preferences;
    return true;
  }

  function showWorkspace(staff) {
    state.partialSessionFailureMessage = null;
    state.partialSessionFailureOwner = null;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
    state.staff = sessionIdentity.accept(staff);
    titleDetail.setStaff(staff);
    copyCreation.setStaff(staff);
    if (copyCreation.review.current()) copyQueue.markStale();
    operationsController.setStaff(staff);
    state.recentKey = recentStorageKey(staff);
    renderRecentRequests();
    navigation.align({ scope: staff.role === 'super_admin' ? 'all' : String(staff.organizationId) });
    titleQueue.setStaff(staff); copyQueue.setStaff(staff);
    dom.signedOut.hidden = true;
    dom.workspace.hidden = false;
    dom.sessionActions.hidden = false;
    dom.staffIdentity.textContent = staff.displayName || staff.userPrincipalName || 'Staff user';
    dom.staffIdentity.title = `${statusLabel(staff.role)} · ${staff.organizationName}`;
    dom.operationsTab.hidden = staff.role !== 'admin' && staff.role !== 'super_admin';
    updateBulkDeleteButtons();
    settingsController.setStaff(staff);
    profileController.setStaff(staff);
    void refreshEmailReadiness();
  }

  function showAccessUnavailable() {
    showSignedOut('Staff access is not currently available. Sign out or use a different authorized Microsoft account.');
    dom.sessionActions.hidden = false;
    dom.staffIdentity.textContent = 'Access unavailable';
    dom.staffIdentity.title = '';
  }

  async function startSession() {
    const load = latestLoads.begin('session');
    try {
      const session = await loadStaffSession({ signal: load.signal });
      if (!load.isCurrent()) return;
      if (!session.authenticated) {
        showSignedOut();
        return;
      }
      if (session.accessAllowed === false) {
        showAccessUnavailable();
        return;
      }
      showWorkspace(session.staff);
      announce('Staff session ready.');
      await navigation.navigateFromUrl();
    } catch (error) {
      if (!isAbortError(error)) showSignedOut('Staff access could not be loaded. Try signing in again.');
    } finally {
      latestLoads.finish('session', load.token);
    }
  }

  function populateScopes(organizations) {
    operationsController.setLibraries(organizations);
    titleQueue.setLibraries(organizations); copyQueue.setLibraries(organizations);
  }

  function bindEvents() {
    onSessionInvalid(error => {
      retainInterruptedBulkLedger();
      showSignedOut(state.partialSessionFailureMessage ||
        'Your staff session ended or no longer has access. Sign in again.');
    });
    onAccessUnavailable(() => {
      retainInterruptedBulkLedger();
      showAccessUnavailable();
    });
    settingsController.bind();
    dom.bulkDelete.addEventListener('click', event => openBulkDelete(event.currentTarget));
    dom.bulkDeleteCopies.addEventListener('click', event => openBulkDelete(event.currentTarget));
    dom.bulkDeleteClose.addEventListener('click', () => closeBulkDelete());
    dom.bulkDeleteDialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeBulkDelete();
    });
    dom.bulkDeleteScope.addEventListener('change', resetBulkDeletePreview);
    dom.bulkDeletePreview.addEventListener('click', previewBulkDelete);
    dom.bulkDeleteConfirmation.addEventListener('input', () => {
      const batch = state.bulkDeleteState;
      dom.bulkDeleteExecute.disabled = !batch?.snapshot?.items.length ||
        batch.submitting || dom.bulkDeleteScope.value !== batch.snapshot.scope ||
        dom.bulkDeleteConfirmation.value !== 'DELETE';
    });
    dom.bulkDeleteExecute.addEventListener('click', executeBulkDelete);
    dom.signOut.addEventListener('click', async () => {
      if (!navigation.allow()) return;
      try {
        await authorizedJson('/api/asap/staff/sign-out', { method: 'POST' });
        showSignedOut('You are signed out.');
      } catch (error) {
        if (error.status !== 401) {
          dom.signedOutMessage.textContent = 'Sign out did not complete. Please try again.';
          announce('Sign out did not complete. Please try again.', 'error');
        }
      }
    });
    for (const tab of dom.viewTabs) tab.addEventListener('click', () => {
      navigation.switchView(tab.dataset.view);
    });
    window.addEventListener('beforeunload', event => {
      if (!titleDetail.isDirty() && !titleDetail.hasPendingMutation() && !copyDetail.isDirty() && !copyDetail.inspectDeparture().blocked && !copyCreation.hasPendingMutation() && !suggestionController.isDirty() && !suggestionController.inspectDeparture().blocked && !profileController.isDirty() &&
          !profileController.hasPendingMutation() && !state.bulkDeleteState?.submitting) return;
      event.preventDefault();
      event.returnValue = '';
    });

  }

  return {
    async start() {
      navigation.start();
      bindEvents();
      await startSession();
    }
  };
}
