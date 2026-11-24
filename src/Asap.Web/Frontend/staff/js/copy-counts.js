import { authorizedJson } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';

export function createCopyCounts({ root, sessionIdentity, getScope, request = authorizedJson }) {
  const labels = [...root.querySelectorAll('[data-copy-count]')];
  const description = root.querySelector('#copy-count-state');
  const reads = createLatestLoad();
  let requestedScope = null, pending = false, disposed = false;

  function render(counts, message = '') {
    for (const label of labels) {
      const value = label.dataset.copyCount === 'open' ? counts?.openCount : counts?.closedCount;
      label.textContent = value === undefined ? '' : ` (${value})`;
    }
    description.textContent = message;
  }

  function clear() {
    reads.begin('counts').abort();
    requestedScope = null;
    pending = false;
    render(null);
  }

  async function refresh(owner = sessionIdentity.preferences()) {
    if (disposed || !sessionIdentity.isCurrent(owner)) return;
    const scope = getScope(), load = reads.begin('counts');
    const current = () => load.isCurrent() && sessionIdentity.isCurrent(owner) && getScope() === scope;
    requestedScope = scope;
    pending = true;
    render(null, 'Task counts loading.');
    try {
      const result = await request(`/api/asap/staff/additional-copies/counts?scope=${encodeURIComponent(scope)}`, { signal: load.signal });
      if (!current()) return;
      if (result?.scope !== scope || (owner.role !== 'super_admin' && scope !== String(owner.organizationId)) ||
          ![result.openCount, result.closedCount].every(value => Number.isSafeInteger(value) && value >= 0)) {
        throw new Error('Invalid additional-copy counts.');
      }
      render(result);
    } catch {
      if (current()) render(null, 'Task counts unavailable. Refresh to try again.');
    } finally {
      if (load.isCurrent()) pending = false;
      reads.finish('counts', load.token);
    }
  }

  function ensureScope() {
    if (requestedScope !== getScope()) void refresh();
  }

  return { refresh, ensureScope,
    contextChanged(next, previous) {
      if (!pending && next.activeView === 'additional-copies' && previous.activeView !== next.activeView) {
        void refresh();
      } else ensureScope();
    },
    signedOut: clear,
    dispose() { disposed = true; clear(); }
  };
}
