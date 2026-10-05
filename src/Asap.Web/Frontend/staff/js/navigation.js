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
  let departureRevision = 0;
  const reads = createLatestLoad();

  function inspect(options) {
    return getFeatures().filter(feature => options[feature.key] !== false)
      .map(feature => ({ feature, inspection: feature.inspectDeparture() }));
  }

  function gather(inspections, prior = null) {
    if (disposed || router.busy()) return null;
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

  // Consent is evidence about these owners and drafts, never an early discard.
  // The opaque handle is single-use; even commit re-inspects pending commands.
  function prepareDeparture(options = {}) {
    options = { ...options };
    const source = context, ticket = generation, actor = sessionIdentity.actor(), revision = ++departureRevision;
    const inspections = inspect(options);
    let permission = gather(inspections), committed = false;
    if (!permission) return null;
    const isCurrent = () => !disposed && !committed && revision === departureRevision && generation === ticket && context === source && sessionIdentity.actor() === actor;
    function revalidate() {
      if (!isCurrent()) return false;
      const current = inspect(options);
      if (current.length !== inspections.length || current.some(({ feature, inspection }, index) => {
        const previous = inspections[index];
        return feature.key !== previous.feature.key || feature.inspectDeparture !== previous.feature.inspectDeparture ||
          inspection.owner !== previous.inspection.owner;
      })) return false;
      const consent = gather(current, permission);
      if (!consent || !isCurrent()) return false;
      permission = consent;
      return true;
    }
    return Object.freeze({ isCurrent, revalidate,
      commit() {
        if (!revalidate()) return false;
        committed = true;
        for (const { feature } of inspections) feature.discardDeparture?.();
        return true;
      }
    });
  }

  function allow(options = {}) { return prepareDeparture(options)?.commit() === true; }

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
    const departure = updateUrl && previous !== name ? prepareDeparture() : null;
    if (updateUrl && previous !== name && !departure) return false;
    if (departure && name === 'settings' && !views.settings.isReady()) {
      void enterSettings(previous, departure); return true;
    }
    if (updateUrl && catalogStale && ['queue', 'additional-copies'].includes(name) && sessionIdentity.actor().role === 'super_admin') {
      void enterReviewedQueue(name, previous, sessionIdentity.preferences(), departure || prepareDeparture());
      return true;
    }
    if (departure) {
      if (!departure.commit()) return false;
      closeTransient(); invalidate();
    }
    activate(name);
    if (updateUrl && previous !== name) writeStage(name);
    if (updateUrl) void views[name]?.refreshOnEntry?.({ context, previous });
    return true;
  }

  async function enterReviewedQueue(name, previous, owner, departure) {
    if (!departure) return;
    const scope = await resolveOperationalScope(owner);
    if (!scope || !sessionIdentity.isCurrent(owner) || !departure.commit()) return;
    closeTransient(); invalidate();
    queueScopeAccepted(scope);
    activate(name);
    if (previous !== name) writeStage(name);
    await views[name]?.refreshOnEntry?.({ context, previous });
  }

  async function enterSettings(previous, departure) {
    const target = await views.settings.prepare(views.settings.currentScope());
    if (!target?.isCurrent() || !departure.commit()) return;
    closeTransient(); invalidate(); target.accept(); activate('settings');
    if (previous !== 'settings') writeStage('settings');
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
        if (context.activeView === 'settings' && scope === views.settings.currentScope()) {
          return { name: 'settings', settingsScope: scope, panel: route.panel || undefined, warning };
        }
        const replacement = await views.settings.prepare(scope);
        return replacement ? { name: 'settings', settingsScope: scope, panel: route.panel || undefined, replacement, warning } : null;
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
    const departure = prepareDeparture(options);
    if (!departure) { router.reject(); return false; }
    const load = reads.begin('route');
    try {
      const target = await validate(requested, load.signal, owner);
      if (!load.isCurrent() || !sessionIdentity.isCurrent(owner)) return false;
      if (!target) { router.reject(); return false; }
      if (target.replacement && !target.replacement.isCurrent()) { router.reject(); return false; }
      let detail = null, queue = null;
      if (target.requestId) {
        detail = await views[target.name].prepareDetail(target.requestId, { scope: target.scope });
        if (!load.isCurrent() || !sessionIdentity.isCurrent(owner)) return false;
        if (!detail?.isCurrent() || !departure.isCurrent()) { router.reject(); return false; }
        if (target.name === 'queue') target.status = detail.request.status;
        else target.copyStatus = detail.request.status;
        queue = await views[target.name].prepare({ ...context, scope: target.scope, status: target.status, additionalCopyStatus: target.copyStatus });
        if (!load.isCurrent() || !sessionIdentity.isCurrent(owner)) return false;
        if (!queue?.isCurrent() || !detail.isCurrent()) { router.reject(); return false; }
        target.scope = queue.scope;
      }
      // The visible source stays mounted during validation. Newly created drafts
      // or commands must obtain fresh permission before any source is discarded.
      if (!departure.commit()) { router.reject(); return false; }
      closeTransient();
      const ticket = invalidate();
      if (target.replacement) target.replacement.accept();
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
      if (queue) queue.accept();
      if (detail) views[target.name].presentDetail(detail, {}, ticket);
      else await views[target.name]?.refresh?.({ skipDeepLink: true });
      return true;
    } catch (error) {
      if (load.isCurrent() && sessionIdentity.isCurrent(owner) && !isAbortError(error) && error.status !== 401) {
        router.reject();
        announce(error.status === 404 && requested.requestId ? requested.stage === 'additional_copies'
          ? 'That additional-copy task is no longer available.' : 'That request is no longer available.'
          : error.message || 'The requested route could not be loaded.', 'error');
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
    if (disposed || !sessionIdentity.actor() || router.busy()) return null;
    return prepareDeparture(options.authoritativeRefresh ? { request: false, copy: false } : {});
  }
  async function alignTitle(request, options, owner, departure) {
    if (!departure?.isCurrent() || !sessionIdentity.isCurrent(owner)) return null;
    const scope = owner.role === 'super_admin' && (options.align || options.fromRecent) && context.scope !== 'all'
      ? views.queue.libraryScope(request.libraryOrgId) : context.scope;
    return prepareDetailAlignment('queue', { ...context, scope,
      status: titleStages.includes(request.status) ? request.status : context.status }, owner, departure, Boolean(options.align || options.fromRecent));
  }
  async function alignCopy(request, options, owner, departure) {
    if (!departure?.isCurrent() || !sessionIdentity.isCurrent(owner)) return null;
    const scope = options.fromDeepLink && owner.role === 'super_admin' && context.scope !== 'all'
      ? views.queue.libraryScope(request.libraryOrgId) : context.scope;
    return prepareDetailAlignment('additional-copies', { ...context, scope,
      additionalCopyStatus: ['open', 'closed'].includes(request.status) ? request.status : context.additionalCopyStatus }, owner, departure);
  }
  async function prepareDetailAlignment(name, target, owner, departure, resetFilters = false) {
    const queue = views[name].isReady(target)
      ? { scope: target.scope, isCurrent: departure.isCurrent, accept() {} }
      : await views[name].prepare(target);
    if (!queue?.isCurrent() || !departure.isCurrent() || !sessionIdentity.isCurrent(owner)) return null;
    return { scope: queue.scope,
      commit() {
        if (!queue.isCurrent() || !departure.commit()) return false;
        closeTransient(); const ticket = invalidate();
        align({ scope: queue.scope, status: target.status, additionalCopyStatus: target.additionalCopyStatus });
        catalogStale = false;
        activate(name);
        if (resetFilters) views[name].resetFilters();
        queue.accept();
        return ticket;
      }
    };
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

  return { context: () => context, generation: () => generation, align, queueScopeAccepted, organizationCatalogChanged, resolveOperationalScope, detailUpdated, prepareDeparture, allow, invalidate, openCreatedTitle, openExistingTitle,
    captureClosedReview, reviewClosed, beforeDetailOpen, alignTitle, alignCopy, detailOpened, detailClosed, prepareSuggestion, openRecentTitle,
    settingsPanelChanged(panel) { invalidate(); router.pushSettingsPanel(panel); router.remember(); },
    settingsScopeChanged(scope) { invalidate(); router.pushSettingsScope(scope); router.remember(); },
    switchView, changeQueueContext, navigateFromUrl,
    start() { router.start(navigateFromUrl); },
    dispose() { disposed = true; reads.begin('route').abort(); reads.begin('operational-catalog').abort(); router.dispose(); }
  };
}
