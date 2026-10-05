import { authorizedJson, isAbortError } from './http.js';
import { createSessionIdentity } from './session-identity.js';
import { createSessionCoordinator } from './session.js';
import { createStaffShell } from './shell.js';
import { createRouter } from './router.js';
import { createNavigationController } from './navigation.js';
import { createDetailHost } from './detail-host.js';
import { createTitleQueue, createCopyQueue } from './queues.js';
import { createTitleDetailController } from './title-detail.js';
import { createCopyDetailController } from './copy-detail.js';
import { createCopyCreationController } from './copy-creation.js';
import { createSuggestionController } from './suggestion-controller.js';
import { createBulkDeleteController } from './bulk-delete.js';
import { createProfileController } from './profile-controller.js';
import { createOperationsController } from './operations-controller.js';
import { createSettingsController } from './settings.js';
import { createAnalyticsController } from './analytics.js';
import { createPolarisLookup } from './research.js';

export function createWorkflowApp() {
  const get = selector => document.querySelector(selector);
  const sessionIdentity = createSessionIdentity(), router = createRouter();
  const detailHost = createDetailHost({ root: get('#request-dialog') });
  const shell = createStaffShell({ root: document, sessionIdentity,
    getContext: () => navigation.context(), settingsScope: () => settings.currentScope(), operationsScope: () => operations.currentScope(),
    findTitle: id => titleQueue.find(id), openRecentTitle: intent => navigation.openRecentTitle(intent),
    onViewIntent: name => navigation.switchView(name), onSignOutIntent: () => session.signOut()
  });
  const announce = shell.announce, onReceipt = shell.recordReceipt, clearReceipt = shell.clearReceipt;
  const polarisLookup = createPolarisLookup({ authorizedJson, isAbortError, announce });
  const analytics = createAnalyticsController({ root: get('#analytics-container'), sessionIdentity });
  const navigation = createNavigationController({ router, sessionIdentity, detailHost, announce,
    present: shell.presentView,
    onInvalidate: () => {
      titleDetail.invalidate(); copyDetail.invalidate(); titleQueue.invalidate(); copyQueue.invalidate();
      suggestion.invalidate(); operations.invalidate();
    },
    closeTransient: () => {
      if (detailHost.isOpen()) detailHost.requestClose({ navigation: true, guarded: true });
      if (suggestion.isOpen()) suggestion.close({ guarded: true, navigation: true, focusButton: false });
    },
    onContextChanged: (next, previous) => {
      titleQueue.contextChanged(next, previous); copyQueue.contextChanged(next, previous);
      bulk.contextChanged(next); shell.contextChanged(next, previous);
    },
    getFeatures: () => [
      { key: 'session', inspectDeparture: session.inspectDeparture },
      { key: 'operations', inspectDeparture: operations.inspectDeparture },
      { key: 'analytics', inspectDeparture: analytics.inspectDeparture },
      { key: 'bulk', inspectDeparture: bulk.inspectDeparture, discardDeparture: () => bulk.close({ navigation: true }) },
      { key: 'profile', inspectDeparture: profile.inspectDeparture, discardDeparture: () => { if (profile.isDirty()) profile.discardDraft(); } },
      { key: 'settings', inspectDeparture: settings.inspectDeparture, discardDeparture: () => { if (settings.isDirty()) settings.discardDraft(); } },
      { key: 'request', inspectDeparture: titleDetail.inspectDeparture },
      { key: 'copy', inspectDeparture: copyDetail.inspectDeparture },
      { key: 'suggestion', inspectDeparture: suggestion.inspectDeparture, reportBlocked: suggestion.reportBlocked }
    ],
    views: {
      queue: {
        activate: options => titleQueue.activate(options), deactivate: () => titleQueue.deactivate(),
        refresh: options => titleQueue.refresh(options), render: () => titleQueue.render(),
        prepare: context => titleQueue.prepare(context),
        isReady: context => titleQueue.isReady(context),
        prepareDetail: (id, options) => titleDetail.prepare(id, options), presentDetail: (target, options, ticket) => titleDetail.present(target, null, options, ticket),
        refreshOnEntry: options => titleQueue.refreshOnEntry(options), setLibraries: libraries => populateScopes(libraries),
        find: id => titleQueue.find(id), libraryScope: id => titleQueue.libraryScope(id),
        resetFilters: () => titleQueue.resetFilters(),
        openDetail: (id, opener, options = { align: true }) => titleDetail.open(id, opener, options),
        closeOverlay: () => detailHost.isOpen() ? detailHost.requestClose() : suggestion.isOpen() ? suggestion.close() : null
      },
      'additional-copies': {
        activate: options => copyQueue.activate(options), deactivate: () => copyQueue.deactivate(),
        refresh: options => copyQueue.refresh(options), render: () => copyQueue.render(),
        prepare: context => copyQueue.prepare(context),
        isReady: context => copyQueue.isReady(context),
        prepareDetail: id => copyDetail.prepare(id), presentDetail: (target, options, ticket) => copyDetail.present(target, null, options, ticket),
        refreshOnEntry: options => copyQueue.refreshOnEntry(options), setLibraries: libraries => populateScopes(libraries),
        resetFilters: () => copyQueue.resetFilters(),
        openDetail: id => copyDetail.open(id, null, { fromDeepLink: true }),
        closeOverlay: () => detailHost.isOpen() ? detailHost.requestClose() : suggestion.isOpen() ? suggestion.close() : null
      },
      settings: { activate: options => { void settings.activate(options.panel); }, deactivate: () => settings.suspend(),
        prepare: scope => settings.prepareScope(scope),
        isReady: () => settings.isReady(),
        currentScope: () => settings.currentScope(), currentPanel: () => settings.currentPanel() },
      operations: { activate: () => operations.activate(), deactivate: () => operations.deactivate(),
        refresh: options => operations.refresh(options), refreshOnEntry: () => operations.refresh() },
      profile: { activate: () => profile.activate(), deactivate: () => profile.deactivate() },
      analytics: { activate: () => analytics.activate(), deactivate: () => analytics.deactivate(),
        refresh: () => analytics.refresh(), refreshOnEntry: () => analytics.refresh() }
    }
  });

  const titleQueue = createTitleQueue({ root: get('#queue-view'), sessionIdentity, announce,
    getContext: navigation.context, onOpen: (id, opener) => titleDetail.open(id, opener, { history: 'push' }),
    onScopeIntent: scope => navigation.changeQueueContext('queue', { scope }),
    onStatusIntent: status => navigation.changeQueueContext('queue', { status }),
    onScopeAccepted: navigation.queueScopeAccepted, onLibraries: populateScopes,
    resolveScope: navigation.resolveOperationalScope,
    onRendered: () => bulk.contextChanged(navigation.context()), onRefreshed: shell.queueRefreshed
  });
  const copyCreation = createCopyCreationController({ root: get('#additional-copy-create-dialog'), sessionIdentity, announce, onReceipt, clearReceipt,
    onRecoveryChanged: change => {
      if (!sessionIdentity.isCurrent(change.owner)) return;
      if (change.committed || change.restored) copyQueue.markStale();
      if (change.uncertain) { copyQueue.invalidate(); copyQueue.render(); }
    },
    onParentChanged: (parent, opener) => parent.refresh(opener)
  });
  const copyDetail = createCopyDetailController({ host: detailHost, sessionIdentity, announce, onReceipt, clearReceipt,
    beforeOpen: navigation.beforeDetailOpen, isNavigationCurrent: ticket => navigation.generation() === ticket,
    getNavigationGeneration: navigation.generation, onAlign: navigation.alignCopy,
    onOpened: (request, options) => navigation.detailOpened(request, true, options),
    onUpdated: request => navigation.detailUpdated(request, true),
    beforeClose: () => navigation.allow({ settings: false, suggestion: false }),
    onClosed: options => navigation.detailClosed(true, options),
    getFocusReturn: (id, opener) => copyQueue.focusReturn(id, opener), refreshQueue: options => copyQueue.refresh(options),
    refreshCurrentStaff: owner => session.refreshCurrentStaff(owner)
  });
  const copyQueue = createCopyQueue({ root: get('#additional-copy-view'), sessionIdentity, announce,
    getContext: navigation.context, onOpen: (id, opener) => copyDetail.open(id, opener, { history: 'push' }),
    onScopeIntent: scope => navigation.changeQueueContext('additional-copies', { scope }),
    onStatusIntent: additionalCopyStatus => navigation.changeQueueContext('additional-copies', { additionalCopyStatus }),
    onScopeAccepted: navigation.queueScopeAccepted, onLibraries: populateScopes,
    resolveScope: navigation.resolveOperationalScope,
    onRendered: () => bulk.contextChanged(navigation.context()), onRefreshed() {},
    recovery: { current: copyCreation.review.current, begin: copyCreation.review.begin, loaded: copyCreation.review.loaded,
      acknowledge: () => copyCreation.review.acknowledge(navigation.context()) }
  });
  const titleDetail = createTitleDetailController({ host: detailHost, sessionIdentity, polarisLookup, copyCreation, announce, onReceipt, clearReceipt,
    beforeOpen: navigation.beforeDetailOpen, isNavigationCurrent: ticket => navigation.generation() === ticket,
    getNavigationGeneration: navigation.generation, getScope: () => navigation.context().scope, onAlign: navigation.alignTitle,
    onOpened: (request, options) => navigation.detailOpened(request, false, options),
    onUpdated: navigation.detailUpdated,
    beforeClose: () => navigation.allow({ settings: false, suggestion: false }), onClosed: options => navigation.detailClosed(false, options),
    getFocusReturn: (id, opener) => titleQueue.focusReturn(id, opener), refreshQueue: options => titleQueue.refresh(options), queueSequence: titleQueue.sequence,
    rememberOpened: shell.rememberOpened, forgetUnavailable: shell.forgetUnavailable,
    refreshCurrentStaff: owner => session.refreshCurrentStaff(owner)
  });
  const suggestion = createSuggestionController({ root: get('#staff-suggestion-dialog'), trigger: get('#new-suggestion'), sessionIdentity, polarisLookup, announce,
    getLibraries: titleQueue.libraries, beforeOpen: navigation.prepareSuggestion, beforeClose: () => navigation.allow({ settings: false, request: false }),
    openCreatedTitle: navigation.openCreatedTitle, openExistingTitle: navigation.openExistingTitle, onReceipt, clearReceipt
  });
  const bulk = createBulkDeleteController({ root: get('#bulk-delete-dialog'), titleTrigger: get('#bulk-delete-closed'), copyTrigger: get('#bulk-delete-closed-copies'),
    sessionIdentity, getLibraries: titleQueue.libraries, beforeOpen: () => navigation.allow(), beforeExecute: () => navigation.allow({ bulk: false }),
    captureReview: navigation.captureClosedReview, reviewClosed: navigation.reviewClosed, onReceipt, clearReceipt,
    onSessionLost: () => session.lose(), onAccessUnavailable: () => session.accessUnavailable()
  });
  const operations = createOperationsController({ root: get('#operations-view'), sessionIdentity, announce,
    onScopeChange: shell.refreshReadiness, onReceipt, clearReceipt
  });
  const profile = createProfileController({ root: get('#profile-view'), sessionIdentity, announce, onReceipt, clearReceipt,
    onPreferences: (staff, owner) => session.updatePreferences(staff, owner),
    onSessionLost: message => session.lose(message), onAccessUnavailable: () => session.accessUnavailable()
  });
  const settings = createSettingsController({ root: get('#settings-view'), tab: get('#settings-view-tab'), announce,
    prepareDeparture: navigation.prepareDeparture,
    onPanelChange: navigation.settingsPanelChanged,
    onScopeChange: scope => { navigation.settingsScopeChanged(scope); void shell.refreshReadiness(); },
    refreshCurrentStaff: owner => session.refreshCurrentStaff(owner),
    onConfigurationCommitted: (owner, scope) => {
      if (!sessionIdentity.isCurrent(owner)) return;
      titleDetail.invalidateConfiguration(scope); titleQueue.markStale(scope); copyQueue.markStale(scope);
      shell.configurationCommitted(scope);
    },
    onStaffAccessCommitted: owner => {
      if (!sessionIdentity.isCurrent(owner)) return;
      titleQueue.markStale(); copyQueue.markStale();
    },
    onOrganizationCatalogCommitted: organizationCatalogCommitted,
    onCommitted: (message, owner, attempt) => shell.recordReceipt(`${message} Sign in again to review the current values.`, owner, attempt, { feature: 'settings' }),
    onUnconfirmed: (message, owner, attempt) => shell.recordReceipt(`${message} Sign in again and check saved values before retrying.`, owner, attempt, { feature: 'settings' }),
    onRefreshed: (owner, review) => {
      shell.settingsRefreshed(owner, review);
      if (sessionIdentity.isCurrent(owner) && review.kind === 'settings') titleDetail.invalidateConfiguration(review.scope);
      if (sessionIdentity.isCurrent(owner) && review.kind === 'staff' && review.changed) {
        titleQueue.markStale(); copyQueue.markStale();
      }
      if (sessionIdentity.isCurrent(owner) && review.kind === 'settings' && review.changed) {
        titleQueue.markStale(review.scope); copyQueue.markStale(review.scope); shell.configurationCommitted(review.scope);
        if (review.catalogChanged) organizationCatalogCommitted(owner);
      }
    }
  });
  const features = [bulk, suggestion, titleDetail, copyCreation, copyDetail, detailHost, polarisLookup,
    operations, titleQueue, copyQueue, profile, settings, analytics];
  const session = createSessionCoordinator({ identity: sessionIdentity, shell, navigation, getFeatures: () => features,
    onAccepted: staff => navigation.align({ scope: staff.role === 'super_admin' ? 'all' : String(staff.organizationId) }),
    onPreferencesChanged: staff => {
      titleQueue.preferencesChanged(); copyQueue.preferencesChanged(); profile.preferencesChanged(staff); settings.setStaff(staff);
    }
  });
  function populateScopes(libraries) { operations.setLibraries(libraries); titleQueue.setLibraries(libraries); copyQueue.setLibraries(libraries); }
  function organizationCatalogCommitted(owner, change) {
    if (!sessionIdentity.isCurrent(owner)) return;
    titleQueue.markStale(); copyQueue.markStale();
    populateScopes([]); operations.retireCatalog();
    navigation.organizationCatalogChanged(change);
  }
  let started = false, disposed = false;
  return {
    async start() { if (started || disposed) return; started = true; settings.bind(); navigation.start(); await session.start(); },
    dispose() { if (disposed) return; disposed = true; session.dispose(); shell.dispose(); navigation.dispose(); for (const feature of features) feature.dispose?.(); }
  };
}
