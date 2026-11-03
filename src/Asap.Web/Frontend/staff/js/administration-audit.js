import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, dateTime } from './ui.js';

const categories = { staff: 'Staff access', settings: 'Settings', organizations: 'Library participation',
  workflow: 'Manual operations', holds: 'Hold recovery', other: 'Other actions' };
const targets = { StaffUser: 'Staff account', Configuration: 'Configuration', Branding: 'Branding', MaterialFormat: 'Material format',
  Organization: 'Organization', WeeklyStaffSummary: 'Weekly summary run', hold_placement_operation: 'Hold operation' };
const fields = ['search', 'scope', 'category', 'from', 'through', 'actor', 'sort'];

// Construct each local calendar midnight independently: DST days can contain
// 23 or 25 hours. Through includes its entire day and excludes the next one.
export function auditDateBounds(from, through) {
  function midnight(value, next = false) {
    if (!value) return null;
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new Error('Choose valid audit dates.');
    const [year, month, day] = value.split('-').map(Number);
    const check = new Date(0); check.setUTCFullYear(year, month - 1, day);
    if (year < 1 || check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) {
      throw new Error('Choose valid audit dates.');
    }
    const local = new Date(0); local.setFullYear(year, month - 1, day + (next ? 1 : 0)); local.setHours(0, 0, 0, 0);
    return local.toISOString();
  }
  if (from && through && from > through) throw new Error('From must be on or before Through.');
  return { fromUtc: midnight(from), beforeUtc: midnight(through, true) };
}

export function createAdministrationAudit({ root, sessionIdentity, onRouteChange = () => {}, request = authorizedJson }) {
  const get = id => root.querySelector(`#audit-${id}`);
  const dom = Object.fromEntries([...fields, 'filters', 'clear', 'status', 'count', 'results', 'page', 'prev', 'next', 'timezone']
    .map(id => [id, get(id)]));
  const events = new window.AbortController(), reads = createLatestLoad();
  let active = false, disposed = false, page = 1, pageSize = 25, owner = null, searchTimer = null;
  const defaults = () => ({ search: '', scope: sessionIdentity.preferences()?.role === 'super_admin' ? 'all'
    : String(sessionIdentity.preferences()?.organizationId || ''), category: '', from: '', through: '', actor: '', sort: 'newest' });
  let filters = defaults();
  dom.timezone.textContent = `Dates and times shown in ${Intl.DateTimeFormat().resolvedOptions().timeZone}. Through includes the whole local day.`;

  function choices(control, items, selected) {
    control.replaceChildren(...items.map(item => element('option', { value: item.id, text: item.name })));
    if (selected && !items.some(item => String(item.id) === selected)) {
      control.append(element('option', { value: selected, text: `Selected ${selected}` }));
    }
    control.value = selected;
  }
  function scopeChoices(libraries = []) {
    const staff = sessionIdentity.preferences();
    choices(dom.scope, staff?.role === 'super_admin'
      ? [{ id: 'all', name: 'All libraries and system' }, { id: 'system', name: 'System only' }, ...libraries]
      : [{ id: String(staff?.organizationId || ''), name: libraries[0]?.name || 'My library' }], filters.scope);
    dom.scope.disabled = staff?.role !== 'super_admin';
  }
  function clearProjection(message = 'Audit history has not been loaded for this context.') {
    reads.begin('audit').abort();
    dom.results.replaceChildren(element('p', { className: 'operations-empty', text: message }));
    dom.results.removeAttribute('aria-busy');
    dom.count.textContent = ''; dom.page.textContent = '';
    dom.status.textContent = ''; dom.status.classList.remove('error');
    dom.prev.disabled = true; dom.next.disabled = true;
    choices(dom.actor, [{ id: '', name: 'Anyone' }], filters.actor);
  }
  function sync() {
    scopeChoices();
    for (const field of fields.filter(field => !['scope', 'actor'].includes(field))) dom[field].value = filters[field];
    choices(dom.actor, [{ id: '', name: 'Anyone' }], filters.actor);
  }
  function route() { return { ...filters, page }; }
  function changed() { onRouteChange({ replace: true }); }
  function setRoute(value = {}) {
    clearTimeout(searchTimer);
    filters = defaults(); page = 1;
    for (const field of fields) {
      if (typeof value[field] === 'string') filters[field] = value[field];
    }
    if (sessionIdentity.preferences()?.role !== 'super_admin') filters.scope = defaults().scope;
    if (!['all', 'system'].includes(filters.scope) && !/^[1-9]\d{0,9}$/.test(filters.scope)) filters.scope = defaults().scope;
    if (filters.category && !categories[filters.category]) filters.category = '';
    if (!['oldest', 'newest'].includes(filters.sort)) filters.sort = 'newest';
    if (/^[1-9]\d{0,6}$/.test(String(value.page || ''))) page = Number(value.page);
    clearProjection(); sync();
  }

  function render(data) {
    const table = element('table', { className: 'audit-log', 'aria-label': 'Administrative changes' });
    const head = element('thead'), headings = element('tr'), body = element('tbody');
    for (const [index, label] of ['When', 'Performed by', 'Library / scope', 'Change'].entries()) {
      headings.append(element('th', { scope: 'col', className: ['when', 'person', 'scope', 'change'][index], text: label }));
    }
    head.append(headings);
    for (const item of data.items) {
      const when = new Date(item.createdUtc);
      const time = element('td', {}, element('time', { datetime: item.createdUtc }, [
        element('div', { className: 'audit-date', text: when.toLocaleDateString() }),
        element('div', { className: 'operations-meta', text: when.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', timeZoneName: 'short' }) })]));
      const actor = element('td', { text: item.actorName || (item.actorStaffUserId
        ? `Staff ${item.actorStaffUserId} (name not recorded)` : 'Actor not recorded') });
      const change = element('td', {}, [element('div', { className: 'audit-event-title', text: item.summary }),
        element('div', { className: 'operations-meta', text: categories[item.category] || 'Other actions' }),
        element('div', { className: 'audit-event-target', text: [targets[item.targetType] || item.targetType, item.targetId, item.recordedTarget].filter(Boolean).join(' · ') })]);
      const detail = element('details'), summary = element('summary', { text: 'View details' });
      const recorded = element('dl', { className: 'audit-detail' });
      for (const [label, value] of [['Action', item.action], ['Audit reference', item.id],
        ['Recorded time', dateTime(item.createdUtc)], ...(item.details || []).map(entry => [entry.label, entry.value])]) {
        recorded.append(element('dt', { text: label }), element('dd', { text: value }));
      }
      detail.append(summary, recorded); change.append(detail);
      const row = element('tr'); row.append(time, actor, element('td', { text: item.scopeName }), change); body.append(row);
    }
    if (!data.items.length) body.append(element('tr', {}, element('td', { colspan: '4', text: 'No audit entries match these filters.' })));
    table.append(head, body); dom.results.replaceChildren(table);
    page = data.page; pageSize = data.pageSize;
    dom.count.textContent = `${data.count.toLocaleString()} matching ${data.count === 1 ? 'entry' : 'entries'}`;
    dom.page.textContent = data.count ? `Showing ${((page - 1) * pageSize + 1).toLocaleString()}–${Math.min(page * pageSize, data.count).toLocaleString()} of ${data.count.toLocaleString()}` : '0 entries';
    dom.prev.disabled = page <= 1; dom.next.disabled = page * pageSize >= data.count;
    scopeChoices(data.libraries || []);
    const actors = [...new Map((data.actors || []).map(item => [item.id, item])).values()];
    choices(dom.actor, [{ id: '', name: 'Anyone' }, ...actors], filters.actor);
    dom.status.textContent = data.actorsTruncated || data.librariesTruncated
      ? 'Some filter choices are omitted. Search history to find additional recorded people or records.' : '';
  }

  async function refresh({ focusButton = null } = {}) {
    if (disposed || !active || !sessionIdentity.isCurrent(owner)) return false;
    const pager = [dom.prev, dom.next].includes(focusButton) && document.activeElement === focusButton ? focusButton : null;
    clearTimeout(searchTimer); clearProjection('Loading administration audit…');
    const load = reads.begin('audit'), capturedOwner = owner, captured = JSON.stringify(route());
    // Native disabled buttons lose focus in Chromium. Keep a keyboard paging
    // gesture on the stable results region while loading; restore its button
    // only if still available and the user has not moved focus themselves.
    let focusMoved = false;
    const moved = () => { focusMoved = true; };
    if (pager) { dom.results.focus(); document.addEventListener('focusin', moved); }
    dom.status.textContent = 'Loading administration audit…'; dom.results.setAttribute('aria-busy', 'true');
    try {
      const query = new URLSearchParams({ scope: filters.scope, sort: filters.sort, page: String(page), pageSize: String(pageSize) });
      for (const [key, value] of Object.entries({ search: filters.search, category: filters.category, actorId: filters.actor, ...auditDateBounds(filters.from, filters.through) })) {
        if (value) query.set(key, value);
      }
      const response = await request(`/api/asap/staff/audit?${query}`, { signal: load.signal });
      if (!load.isCurrent() || !active || !sessionIdentity.isCurrent(capturedOwner) || captured !== JSON.stringify(route())) return false;
      const data = response?.data;
      if (!Array.isArray(data?.items) || !Number.isSafeInteger(data.count) || data.count < 0 ||
          !Number.isSafeInteger(data.page) || data.page < 1 || !Number.isSafeInteger(data.pageSize) || data.pageSize < 1) {
        throw new Error('Administration audit returned an unreadable response. Refresh operations to try again.');
      }
      render(data); return true;
    } catch (error) {
      if (load.isCurrent() && active && sessionIdentity.isCurrent(capturedOwner) && !isAbortError(error) && error.status !== 401) {
        dom.results.replaceChildren(element('p', { className: 'operations-empty', text: 'Administration audit is unavailable.' }));
        dom.status.textContent = error.message || 'Administration audit could not be loaded. Refresh operations to try again.';
        dom.status.classList.add('error');
      }
      return false;
    } finally {
      document.removeEventListener('focusin', moved);
      if (load.isCurrent()) dom.results.removeAttribute('aria-busy');
      if (pager && !focusMoved && load.isCurrent() && active && sessionIdentity.isCurrent(capturedOwner) &&
          document.activeElement === dom.results) {
        // Loading collapses a long page and clamps document scroll. Native
        // focus must reveal the restored pager; unavailable boundaries reveal
        // the still-focused results region instead of leaving focus offscreen.
        if (!pager.disabled) pager.focus();
        else dom.results.scrollIntoView({ block: 'start', inline: 'nearest' });
      }
      reads.finish('audit', load.token);
    }
  }
  function filterChanged(field) {
    filters[field] = dom[field].value; page = 1; clearTimeout(searchTimer); clearProjection(); changed();
    if (field === 'search') searchTimer = setTimeout(() => void refresh(), 250);
    else void refresh();
  }
  const listen = (node, event, handler) => node.addEventListener(event, handler, { signal: events.signal });
  for (const field of fields) listen(dom[field], field === 'search' ? 'input' : 'change', () => filterChanged(field));
  listen(dom.filters, 'submit', event => { event.preventDefault(); void refresh(); });
  listen(dom.clear, 'click', () => { setRoute(); changed(); void refresh(); });
  listen(dom.prev, 'click', () => { if (page > 1) { page--; changed(); void refresh({ focusButton: dom.prev }); } });
  listen(dom.next, 'click', () => { if (!dom.next.disabled) { page++; changed(); void refresh({ focusButton: dom.next }); } });
  listen(dom.results, 'keydown', event => {
    const detail = event.target.closest('details');
    if (event.key === 'Escape' && detail?.open) { detail.open = false; detail.querySelector('summary').focus(); event.preventDefault(); }
  });
  return {
    refresh, route, setRoute,
    setStaff(staff) { owner = staff; setRoute(); },
    activate() { active = true; },
    deactivate() { active = false; clearTimeout(searchTimer); clearProjection(); },
    invalidate() { clearTimeout(searchTimer); clearProjection(); },
    dispose() { disposed = true; active = false; clearTimeout(searchTimer); clearProjection(); events.abort(); }
  };
}
