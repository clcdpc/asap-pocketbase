import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';

const titleStages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];

// Ports describe owner policies and lifecycle. Navigation never inspects forms.
export function createNavigationController({ router, sessionIdentity, getFeatures, views,
  announce, present, closeTransient, onInvalidate, onContextChanged, request = authorizedJson }) {
  let context = Object.freeze({ scope: 'all', status: 'suggestion', additionalCopyStatus: 'open', activeView: 'queue' });
  let generation = 0;
  let disposed = false;
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
    onInvalidate();
    return generation;
  }

  function align(changes) {
    const previous = context;
    context = Object.freeze({ ...context, ...changes });
    onContextChanged(context, previous);
  }

  function activate(name) {
    const previous = context.activeView;
    if (previous !== name) views[previous]?.deactivate?.();
    align({ activeView: name });
    present(name);
    views[name]?.activate?.({ context, previous });
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
    activate(name);
    if (updateUrl && previous !== name) writeStage(name);
    if (updateUrl) void views[name]?.refreshOnEntry?.({ context, previous });
    return true;
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
    if (owner.role === 'super_admin' && /^[1-9]\d{0,9}$/.test(route.scope) && Number(route.scope) > 1) {
      try {
        const result = await request('/api/asap/staff/organizations', { signal });
        organizations = (result?.data ?? result).filter(item => Number(item.id) > 1 && item.active !== false);
        if (organizations.some(item => String(item.id) === route.scope)) scope = route.scope;
      } catch (error) {
        if (isAbortError(error) || error.status === 401) throw error;
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
    const ticket = invalidate();
    const load = reads.begin('route');
    try {
      const target = await validate(requested, load.signal, owner);
      if (!load.isCurrent() || generation !== ticket || !sessionIdentity.isCurrent(owner)) return false;
      // The visible source stays mounted during validation. Newly created drafts
      // or commands must obtain fresh permission before any source is discarded.
      if (!gather(options, permission)) { router.reject(); return false; }
      discard(options);
      closeTransient();
      if (target.name === 'settings') views.settings.setScopeFromUrl(target.settingsScope);
      if (target.scope) align({ scope: target.scope, status: target.status,
        additionalCopyStatus: target.copyStatus });
      if (target.organizations) {
        views.queue.setLibraries(target.organizations);
        views['additional-copies'].setLibraries(target.organizations);
      }
      activate(target.name);
      writeStage(target.name, true);
      if (target.requestId) {
        router.replaceRequest(target.requestId, target.name === 'additional-copies',
          { scope: context.scope, copyStatus: context.additionalCopyStatus },
          target.name === 'additional-copies' ? 'additional_copies' : context.status);
        router.remember();
      }
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

  return { context: () => context, generation: () => generation, align, allow, invalidate,
    switchView, changeQueueContext, navigateFromUrl,
    start() { router.start(navigateFromUrl); },
    dispose() { disposed = true; reads.begin('route').abort(); router.dispose(); }
  };
}
