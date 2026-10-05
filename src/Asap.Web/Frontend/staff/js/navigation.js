import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';

const titleStages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];

// Ports describe owner policies and lifecycle. Navigation never inspects forms.
export function createNavigationController({ router, sessionIdentity, detailHost, getFeatures, views,
  announce, present, closeTransient, onInvalidate, onContextChanged, request = authorizedJson }) {
  let context = Object.freeze({ scope: 'all', status: 'suggestion', additionalCopyStatus: 'open', activeView: 'queue' });
  let generation = 0;
  let disposed = false;
  let catalogStale = false;
  const reads = createLatestLoad();

  function gather(options = {}, prior = null) {
    if (disposed || router.busy()) return null;
    const inspections = getFeatures().filter(feature => options[feature.key] !== false)
      .map(feature => ({ feature, inspection: feature.inspectDeparture() }));
    for (const { feature, inspection } of inspections) {
      if (inspection.blocked) {
        (feature.reportBlocked || (message => announce(message, 'warning')))(inspection.message);
        return null;
      }
    }
    for (const { feature, inspection } of inspections) {
      if (inspection.dirty && (!prior?.has(feature.key) || prior.get(feature.key) !== inspection.stamp) &&
          !window.confirm(inspection.confirmMessage)) return null;
    }
    return new Map(inspections.filter(({ inspection }) => inspection.dirty)
      .map(({ feature, inspection }) => [feature.key, inspection.stamp]));
  }

  function discard(options = {}) {
    for (const feature of getFeatures()) {
      if (options[feature.key] !== false) feature.discardDeparture?.();
    }
  }

  function allow(options = {}) {
    if (!gather(options)) return false;
    discard(options);
    return true;
  }

  function invalidate() {
    generation += 1;
    reads.begin('route').abort();
    reads.begin('operational-catalog').abort();
    onInvalidate();
    return generation;
  }

  function align(changes) {
    const previous = context;
    context = Object.freeze({ ...context, ...changes });
    onContextChanged(context, previous);
  }

  function syncQueueRoute() {
    if (!['queue', 'additional-copies'].includes(context.activeView)) return;
    router.replaceAcceptedQueue(context.activeView === 'additional-copies' ? 'additional_copies' : context.status,
      { scope: context.scope, copyStatus: context.additionalCopyStatus });
  }

  function queueScopeAccepted(scope) {
    if (disposed || !sessionIdentity.actor() || !scope) return;
    catalogStale = false;
    if (context.scope === scope) return;
    align({ scope }); syncQueueRoute();
  }

  function organizationCatalogChanged(change) {
    if (disposed || !sessionIdentity.actor()) return;
    invalidate(); catalogStale = true;
    if (sessionIdentity.actor().role === 'super_admin' && change?.active === false && String(change.id) === context.scope) {
      align({ scope: 'all' }); syncQueueRoute();
    }
  }

  function resolveOperationalScope(owner) {
    if (disposed || !sessionIdentity.isCurrent(owner)) return null;
    if (!catalogStale || owner.role !== 'super_admin') return context.scope;
    return reviewOperationalCatalog(owner);
  }

  async function reviewOperationalCatalog(owner) {
    const load = reads.begin('operational-catalog');
    try {
      const result = await request('/api/asap/staff/organizations', { signal: load.signal });
      if (disposed || !load.isCurrent() || !sessionIdentity.isCurrent(owner)) return null;
      const libraries = (result?.data ?? result).filter(item => Number(item.id) > 1 && item.isActive);
      views.queue.setLibraries(libraries);
      return libraries.some(item => String(item.id) === context.scope) ? context.scope : 'all';
    } catch (error) {
      if (!disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        announce('Operational libraries could not be reviewed. Refresh to load current requests.', 'error');
      }
      return null;
    } finally { reads.finish('operational-catalog', load.token); }
  }

  function detailUpdated(request, copy = false) {
    if (disposed || !sessionIdentity.actor()) return;
    if (copy && ['open', 'closed'].includes(request.status)) align({ additionalCopyStatus: request.status });
    else if (!copy && titleStages.includes(request.status)) align({ status: request.status });
    syncQueueRoute();
  }

  function activate(name) {
    const previous = context.activeView;
    if (previous !== name) views[previous]?.deactivate?.();
    align({ activeView: name });
    present(name);
    views[name]?.activate?.({ context, previous, panel: name === 'settings' ? router.requested().panel || undefined : undefined });
  }

  function writeStage(name, replace = false) {
    const stage = name === 'additional-copies' ? 'additional_copies'
      : ['settings', 'profile', 'analytics', 'operations'].includes(name) ? name : context.status;
    if (name === 'settings' && !replace) {
      router.pushSettingsRoute(views.settings.currentScope(), views.settings.currentPanel());
    } else {
      router[replace ? 'replaceStage' : 'pushStage'](stage, { scope: context.scope, copyStatus: context.additionalCopyStatus });
    }
    router.remember();
  }

  function switchView(name, updateUrl = true) {
    if (!sessionIdentity.actor() || !views[name] ||
        ['settings', 'operations'].includes(name) && !['admin', 'super_admin'].includes(sessionIdentity.actor().role)) return false;
    const previous = context.activeView;
    if (updateUrl && previous === name) {
      const closed = views[name].closeOverlay?.();
      if (closed !== undefined && closed !== null) return closed;
    }
    if (updateUrl && previous !== name) {
      if (!allow()) return false;
      closeTransient();
      invalidate();
    }
    if (updateUrl && catalogStale && ['queue', 'additional-copies'].includes(name) && sessionIdentity.actor().role === 'super_admin') {
      const owner = sessionIdentity.preferences(), ticket = generation;
      void enterReviewedQueue(name, previous, owner, ticket);
      return true;
    }
    activate(name);
    if (updateUrl && previous !== name) writeStage(name);
    if (updateUrl) void views[name]?.refreshOnEntry?.({ context, previous });
    return true;
  }

  async function enterReviewedQueue(name, previous, owner, ticket) {
    const scope = await resolveOperationalScope(owner);
    if (!scope || disposed || generation !== ticket || !sessionIdentity.isCurrent(owner) || !allow()) return;
    queueScopeAccepted(scope);
    activate(name);
    if (previous !== name) writeStage(name);
    await views[name]?.refreshOnEntry?.({ context, previous });
  }

  function changeQueueContext(name, changes) {
    if (!sessionIdentity.actor() || !['queue', 'additional-copies'].includes(name)) return false;
    const same = Object.entries(changes).every(([key, value]) => context[key] === value);
    if (same) return true;
    if (!allow()) return false;
    closeTransient(); invalidate(); align(changes);
    writeStage(name);
    void views[name].refresh?.({ silent: name === 'queue' && !Object.hasOwn(changes, 'scope') });
    return true;
  }

  async function validate(route, signal, owner) {
    let stage = route.stage;
    let warning = '';
    let organizations = null;
    const admin = ['admin', 'super_admin'].includes(owner.role);
    if (['settings', 'operations'].includes(stage) && !admin) {
      warning = 'This view requires administrator access.';
      stage = context.status;
    }
    if (stage === 'settings') {
      const scope = route.settingsScope || (owner.role === 'super_admin' ? 'system' : String(owner.organizationId));
      if (!(scope === 'system' || /^[1-9]\d{0,9}$/.test(scope)) ||
          owner.role !== 'super_admin' && scope !== String(owner.organizationId)) {
        stage = context.status;
        warning = 'That Settings scope is not available to this staff account.';
      } else {
        try {
          if (route.settingsScope) await request(`/api/asap/staff/settings?orgId=${encodeURIComponent(scope)}`, { signal });
          return { name: 'settings', settingsScope: scope, panel: route.panel || undefined, warning };
        } catch (error) {
          if (isAbortError(error) || error.status === 401) throw error;
          stage = context.status;
          warning = 'That Settings scope is not available.';
        }
      }
    }
    if (['operations', 'analytics', 'profile'].includes(stage)) return { name: stage, warning };
    let scope = owner.role === 'super_admin' ? 'all' : String(owner.organizationId);
    if (owner.role === 'super_admin' && (catalogStale || /^[1-9]\d{0,9}$/.test(route.scope) && Number(route.scope) > 1)) {
      try {
        const result = await request('/api/asap/staff/organizations', { signal });
        organizations = (result?.data ?? result).filter(item => Number(item.id) > 1 && item.isActive);
        if (organizations.some(item => String(item.id) === route.scope)) scope = route.scope;
      } catch (error) {
        if (catalogStale || isAbortError(error) || error.status === 401) throw error;
      }
    }
    return { name: stage === 'additional_copies' ? 'additional-copies' : 'queue', scope,
      status: stage === 'additional_copies' ? context.status : titleStages.includes(stage) ? stage : 'suggestion', copyStatus: route.copyStatus,
      requestId: route.requestId, organizations, warning };
  }

  async function navigateFromUrl() {
    const owner = sessionIdentity.preferences();
    if (!owner) {
      if (getFeatures().some(feature => feature.inspectDeparture().blocked)) router.reject();
      return false;
    }
    const requested = router.requested();
    const nextSettingsScope = requested.settingsScope || (owner.role === 'super_admin' ? 'system' : String(owner.organizationId));
    const options = { settings: context.activeView === 'settings' &&
      (requested.stage !== 'settings' || nextSettingsScope !== views.settings.currentScope()) };
    const permission = gather(options);
    if (!permission) { router.reject(); return false; }
    const source = context;
    const ticket = invalidate();
    const load = reads.begin('route');
    try {
      const target = await validate(requested, load.signal, owner);
      if (!load.isCurrent() || generation !== ticket || !sessionIdentity.isCurrent(owner)) return false;
      // The visible source stays mounted during validation. Newly created drafts
      // or commands must obtain fresh permission before any source is discarded.
      if (!gather(options, permission) || context !== source) { router.reject(); return false; }
      discard(options);
      closeTransient();
      if (target.name === 'settings') views.settings.setScopeFromUrl(target.settingsScope);
      if (target.scope) align({ scope: target.scope, status: target.status,
        additionalCopyStatus: target.copyStatus });
      if (target.organizations) {
        catalogStale = false;
        views.queue.setLibraries(target.organizations);
        views['additional-copies'].setLibraries(target.organizations);
      }
      activate(target.name);
      if (target.requestId) {
        router.replaceRequest(target.requestId, target.name === 'additional-copies',
          { scope: context.scope, copyStatus: context.additionalCopyStatus },
          target.name === 'additional-copies' ? 'additional_copies' : context.status);
        router.remember();
      } else writeStage(target.name, true);
      if (target.warning) announce(target.warning, 'error');
      const loaded = await views[target.name]?.refresh?.({ skipDeepLink: true });
      if (generation === ticket && sessionIdentity.isCurrent(owner) && loaded === true && target.requestId) {
        await views[target.name].openDetail(target.requestId);
      }
      return true;
    } catch (error) {
      if (load.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        router.reject();
        announce(error.message || 'The requested route could not be loaded.', 'error');
      }
      return false;
    } finally {
      reads.finish('route', load.token);
    }
  }

  async function openCreatedTitle(intent) {
    if (disposed || !sessionIdentity.isCurrent(intent.owner)) return null;
    const ticket = invalidate();
    if (intent.owner.role === 'super_admin') align({ scope: String(intent.libraryOrgId) });
    let queueRefreshed = false;
    try { queueRefreshed = await views.queue.refresh({ skipDeepLink: true, silent: true }) === true; }
    catch { /* The command already committed; a failed presentation read remains unavailable. */ }
    if (generation !== ticket || !sessionIdentity.isCurrent(intent.owner)) return null;
    let detailLoaded = false;
    try { detailLoaded = await views.queue.openDetail(intent.id, intent.opener, { align: true, history: 'push' }) === true; }
    catch { /* Preserve the captured command receipt if detail presentation cannot complete. */ }
    if (![ticket, ticket + 1].includes(generation) || !sessionIdentity.isCurrent(intent.owner)) return null;
    const completion = generation;
    return { queueRefreshed, detailLoaded,
      isCurrent: () => generation === completion && sessionIdentity.isCurrent(intent.owner) };
  }

  async function openExistingTitle(intent) {
    if (disposed || !sessionIdentity.isCurrent(intent.owner)) return false;
    return views.queue.openDetail(intent.id, intent.opener, { align: true, history: 'push' });
  }

  function captureClosedReview(owner) {
    const captured = context, ticket = generation, href = router.snapshot()?.href;
    return Object.freeze({ view: captured.activeView,
      isCurrent: () => !disposed && sessionIdentity.isCurrent(owner) && generation === ticket &&
        Object.entries(captured).every(([key, value]) => context[key] === value) && window.location.href === href });
  }

  async function reviewClosed({ owner, scope, review }) {
    if (!review?.isCurrent() || !['queue', 'additional-copies'].includes(review.view)) return null;
    align({ scope, status: 'closed', additionalCopyStatus: 'closed' });
    writeStage(review.view, true);
    const completion = captureClosedReview(owner);
    let titles = false, copies = false;
    try { titles = await views.queue.refresh({ skipDeepLink: true, silent: true }) === true; }
    catch { /* Completed DELETEs remain authoritative when a review read fails. */ }
    if (!completion.isCurrent()) return null;
    try { copies = await views['additional-copies'].refresh({ skipDeepLink: true, silent: true }) === true; }
    catch { /* Retain the ledger receipt until both Closed lists are available. */ }
    if (!completion.isCurrent()) return null;
    return { refreshed: titles && copies, isCurrent: completion.isCurrent };
  }

  function beforeDetailOpen(options) {
    if (disposed || !sessionIdentity.actor() || router.busy() || !options.authoritativeRefresh && !allow({ suggestion: false })) return false;
    return invalidate();
  }
  async function alignTitle(request, options, owner, ticket) {
    if (options.reloaded || !options.align && !options.fromRecent) {
      if (request.status !== context.status) {
        align({ status: request.status });
        if (await views.queue.refresh({ silent: true, skipDeepLink: true }) !== true) return false;
      }
    } else if (options.align || options.fromRecent) {
      const needsRefresh = context.status !== request.status || views.queue.find(request.id)?.status !== request.status;
      const scope = views.queue.libraryScope(request.libraryOrgId);
      const scopeChanged = owner.role === 'super_admin' && context.scope !== 'all' && context.scope !== scope;
      views.queue.resetFilters();
      if (titleStages.includes(request.status)) align({ status: request.status });
      if (scopeChanged) align({ scope });
      if (scopeChanged || needsRefresh) {
        if (await views.queue.refresh({ silent: true, skipDeepLink: true }) !== true) return false;
      } else views.queue.render();
      if (generation !== ticket || !sessionIdentity.isCurrent(owner)) return false;
      switchView('queue', false);
    }
    return generation === ticket && sessionIdentity.isCurrent(owner);
  }
  async function alignCopy(request, options, owner, ticket) {
    const statusChanged = request.status !== context.additionalCopyStatus && ['open', 'closed'].includes(request.status);
    const scope = views.queue.libraryScope(request.libraryOrgId);
    const scopeChanged = options.fromDeepLink && owner.role === 'super_admin' && context.scope !== 'all' && context.scope !== scope;
    if (statusChanged || scopeChanged) {
      align({ ...(statusChanged ? { additionalCopyStatus: request.status } : {}), ...(scopeChanged ? { scope } : {}) });
      views['additional-copies'].resetFilters();
      if (await views['additional-copies'].refresh({ silent: true }) !== true || generation !== ticket || !sessionIdentity.isCurrent(owner)) return false;
    }
    return true;
  }
  function detailOpened(request, copy, options) {
    if (options.history === 'push' || options.fromRecent) router.pushRequest(request.id, copy ? 'additional_copies' : request.status,
      { scope: context.scope, copyStatus: context.additionalCopyStatus });
    else router.replaceRequest(request.id, copy, { scope: context.scope, copyStatus: context.additionalCopyStatus }, copy ? 'additional_copies' : context.status);
    router.remember();
  }
  function detailClosed(copy, options) {
    if (options.navigation) return;
    router.closeDetail(copy ? 'additional_copies' : context.status, { scope: context.scope, copyStatus: context.additionalCopyStatus });
    if (!router.busy()) router.remember();
  }
  function prepareSuggestion() {
    if (!detailHost.isOpen()) return true;
    if (!detailHost.requestClose({ navigation: true })) return false;
    writeStage('queue', true); return true;
  }
  async function openRecentTitle(intent) {
    if (disposed || !sessionIdentity.isCurrent(intent.owner)) return false;
    return views.queue.openDetail(intent.id, intent.opener, { fromRecent: true });
  }

  return { context: () => context, generation: () => generation, align, queueScopeAccepted, organizationCatalogChanged, resolveOperationalScope, detailUpdated, allow, invalidate, openCreatedTitle, openExistingTitle,
    captureClosedReview, reviewClosed, beforeDetailOpen, alignTitle, alignCopy, detailOpened, detailClosed, prepareSuggestion, openRecentTitle,
    settingsPanelChanged(panel) { invalidate(); router.pushSettingsPanel(panel); router.remember(); },
    settingsScopeChanged(scope) { invalidate(); router.pushSettingsScope(scope); router.remember(); },
    switchView, changeQueueContext, navigateFromUrl,
    start() { router.start(navigateFromUrl); },
    dispose() { disposed = true; reads.begin('route').abort(); reads.begin('operational-catalog').abort(); router.dispose(); }
  };
}
