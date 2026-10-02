import { createBulkDeleteController } from './bulk-delete.js';
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
      bulkDelete.contextChanged(navigation.context());
      if (emailReadinessScopeKey(next) !== emailReadinessScopeKey(previous)) void refreshEmailReadiness();
    },
    getFeatures: () => [
      { key: 'bulk', inspectDeparture: bulkDelete.inspectDeparture,
        discardDeparture: () => bulkDelete.close({ navigation: true }) },
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
    onRendered: () => bulkDelete.contextChanged(navigation.context()),
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
    onRendered: () => bulkDelete.contextChanged(navigation.context()), onRefreshed() {},
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

  const bulkDelete = createBulkDeleteController({ root: document.querySelector('#bulk-delete-dialog'),
    titleTrigger: dom.bulkDelete, copyTrigger: dom.bulkDeleteCopies, sessionIdentity,
    getLibraries: () => titleQueue.libraries(), beforeOpen: () => navigation.allow(),
    beforeExecute: () => navigation.allow({ bulk: false }),
    captureReview: owner => navigation.captureClosedReview(owner), reviewClosed: intent => navigation.reviewClosed(intent),
    onReceipt: recordFeatureReceipt, clearReceipt: clearCommittedSessionFallback,
    onSessionLost: showSignedOut, onAccessUnavailable: showAccessUnavailable
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
    bulkDelete.signedOut();
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
    bulkDelete.contextChanged(navigation.context());
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
      bulkDelete.interrupt();
      showSignedOut(state.partialSessionFailureMessage ||
        'Your staff session ended or no longer has access. Sign in again.');
    });
    onAccessUnavailable(() => {
      bulkDelete.interrupt();
      showAccessUnavailable();
    });
    settingsController.bind();
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
          !profileController.hasPendingMutation() && !bulkDelete.hasPendingMutation()) return;
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
