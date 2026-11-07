import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, dateTime } from './ui.js';
import { emailOperationStatusLabel } from './email-test.js';

const fields = ['search', 'scope', 'status', 'type', 'from', 'through', 'sort'];
const statuses = ['pending', 'sending', 'sent', 'failed', 'suppressed'];
const types = [{ value: 'business_event', label: 'Business event' },
  { value: 'staff_authorization_sensitive', label: 'Staff notification' },
  { value: 'operational_test', label: 'Operational test' }];
const canonicalId = value => typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
  (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
const validScope = value => ['all', 'system'].includes(value) ||
  /^[1-9]\d{0,9}$/.test(value || '') && Number(value) > 1 && Number(value) <= 2147483647;

// Browser-local calendar days intentionally differ from Analytics' configured
// business timezone. Construct each midnight independently for 23/25-hour days.
export function emailHistoryDateBounds(from, through) {
  function midnight(value, next = false) {
    if (!value) return null;
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) throw new Error('Choose valid Created dates.');
    const [year, month, day] = value.split('-').map(Number);
    const check = new Date(0); check.setUTCFullYear(year, month - 1, day);
    if (year < 1 || check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) {
      throw new Error('Choose valid Created dates.');
    }
    const local = new Date(0); local.setFullYear(year, month - 1, day + (next ? 1 : 0)); local.setHours(0, 0, 0, 0);
    if (local.getUTCFullYear() < 1 || local.getUTCFullYear() > 9999) throw new Error('Choose Created dates within the supported range.');
    return local.toISOString();
  }
  if (from && through && from > through) throw new Error('Created From must be on or before Through.');
  return { fromUtc: midnight(from), beforeUtc: midnight(through, true) };
}

export function createEmailHistory({ root, sessionIdentity, request = authorizedJson, onRouteChange = () => {},
  selectSnapshot = row => row, onRetry = () => {}, isVisible = () => true }) {
  const get = id => root.querySelector(`#email-${id}`);
  const dom = Object.fromEntries(['search', 'status', 'type', 'from', 'through', 'sort', 'filters', 'clear',
    'history-status', 'count', 'page', 'prev', 'next', 'timezone', 'reference-context', 'reference-value', 'reference-clear']
    .map(id => [id, get(id)]));
  dom.scope = get('view-scope'); dom.results = get('operations-table');
  const reads = createLatestLoad(), events = new window.AbortController();
  let owner = null, active = false, disposed = false, searchTimer = null, page = 1, blocked = false, projection = null, failure = null;
  const pageSize = 25;
  const defaults = () => ({ search: '', scope: sessionIdentity.preferences()?.role === 'super_admin' ? 'all'
    : String(sessionIdentity.preferences()?.organizationId || ''), status: '', type: '', from: '', through: '', sort: 'newest', reference: '' });
  let filters = defaults();
  dom.timezone.textContent = `Created dates and times use ${Intl.DateTimeFormat().resolvedOptions().timeZone}. Through includes the whole local day.`;
  function choices(control, options, selected) {
    control.replaceChildren(...options.map(item => element('option', { value: item.value, text: item.label })));
    if (selected && !options.some(item => String(item.value) === selected)) {
      control.append(element('option', { value: selected, text: `Selected library ${selected}` }));
    }
    control.value = selected;
  }
  function scopeChoices(libraries = []) {
    const own = String(sessionIdentity.preferences()?.organizationId || '');
    choices(dom.scope, sessionIdentity.preferences()?.role === 'super_admin'
      ? [{ value: 'all', label: 'All libraries and system' }, { value: 'system', label: 'System only' },
        ...libraries.map(item => ({ value: String(item.id), label: item.name }))]
      : [{ value: own, label: libraries.find(item => String(item.id) === own)?.name || 'My library' }], filters.scope);
    dom.scope.disabled = sessionIdentity.preferences()?.role !== 'super_admin';
  }
  function clearProjection(message = 'Email history has not been loaded for this context.') {
    reads.begin('history').abort(); projection = null; failure = null;
    dom.results.replaceChildren(element('p', { className: 'operations-empty', text: message }));
    dom.results.removeAttribute('aria-busy'); dom.count.textContent = ''; dom.page.textContent = '';
    dom['history-status'].textContent = ''; dom['history-status'].classList.remove('error');
    dom.prev.disabled = true; dom.next.disabled = true;
    scopeChoices(); choices(dom.type, [{ value: '', label: 'All types' }, ...types], filters.type);
  }
  function sync() {
    for (const field of fields.filter(field => field !== 'scope')) dom[field].value = filters[field];
    scopeChoices();
    dom['reference-context'].hidden = !filters.reference;
    dom['reference-value'].textContent = filters.reference ? `Showing exact email operation ${filters.reference}.` : '';
  }
  function route() { return { ...filters, page }; }
  function changed() { onRouteChange({ replace: true }); }
  function setRoute(value = {}) {
    window.clearTimeout(searchTimer); filters = defaults(); page = 1;
    for (const field of [...fields, 'reference']) if (typeof value[field] === 'string') filters[field] = value[field];
    if (filters.scope === '1') filters.scope = 'system';
    if (sessionIdentity.preferences()?.role !== 'super_admin' || !validScope(filters.scope)) filters.scope = defaults().scope;
    if (!statuses.includes(filters.status)) filters.status = '';
    if (!types.some(type => type.value === filters.type)) filters.type = '';
    if (!['newest', 'oldest'].includes(filters.sort)) filters.sort = 'newest';
    if (!canonicalId(filters.reference)) filters.reference = '';
    if (/^[1-9]\d{0,7}$/.test(String(value.page || ''))) page = Number(value.page);
    clearProjection(); sync();
  }
  function render() {
    if (!projection || !active || !sessionIdentity.isCurrent(projection.owner) || projection.query !== JSON.stringify(route())) return;
    const rendered = projection;
    const data = projection.data;
    const table = element('table', { 'aria-label': 'Email operations history' });
    const head = element('thead'), headings = element('tr'), body = element('tbody');
    for (const label of ['Reference', 'Created', 'Status', 'Type / detail', 'Action']) headings.append(element('th', { scope: 'col', text: label }));
    head.append(headings);
    for (const listed of data.items) {
      const row = selectSnapshot(listed), tr = element('tr');
      const action = row.status === 'failed' && row.canRetry === true
        ? element('button', { className: 'secondary-button', type: 'button', text: 'Retry', disabled: blocked, 'data-email-retry-id': row.id })
        : element('span', { text: 'No action' });
      if (action.tagName === 'BUTTON') action.addEventListener('click', () => {
        if (projection !== rendered || blocked || !active || !sessionIdentity.isCurrent(rendered.owner) || rendered.query !== JSON.stringify(route())) return;
        // An exact poll can advance after rendering. Recheck the whole snapshot
        // before dispatch so an old list button never revives retry permission.
        if (selectSnapshot(listed) !== listed) { render(); return; }
        void retryWithFocus(listed, action);
      });
      tr.append(element('td', { text: row.id }),
        element('td', {}, element('time', { text: dateTime(row.createdUtc), datetime: row.createdUtc })),
        element('td', { text: emailOperationStatusLabel(row) }),
        element('td', {}, [element('div', { text: row.typeLabel || types.find(type => type.value === row.deliveryClass)?.label || 'Unknown message type' }),
          row.detailLabel ? element('div', { className: 'operations-meta', text: row.detailLabel }) : null]), element('td', {}, action));
      body.append(tr);
    }
    if (!data.items.length) body.append(element('tr', {}, element('td', { colspan: '5', text: 'No email operations match these filters.' })));
    table.append(head, body); dom.results.replaceChildren(table);
  }
  async function retryWithFocus(listed, button) {
    const capturedOwner = owner, captured = JSON.stringify(route());
    const restore = document.activeElement === button;
    let focusMoved = false;
    const moved = () => { focusMoved = true; };
    // The results region survives both command locking and the authoritative
    // follow-up load. A removed Retry button must not drop keyboard focus.
    if (restore) { dom.results.focus(); document.addEventListener('focusin', moved, { signal: events.signal }); }
    try {
      await onRetry(listed);
    } finally {
      document.removeEventListener('focusin', moved);
      if (restore && !focusMoved && !disposed && active && isVisible() && sessionIdentity.isCurrent(capturedOwner) &&
          captured === JSON.stringify(route()) && document.activeElement === dom.results) {
        const retry = [...dom.results.querySelectorAll('[data-email-retry-id]')]
          .find(control => control.dataset.emailRetryId === listed.id && !control.disabled);
        if (retry) retry.focus();
        else dom.results.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      }
    }
  }
  async function refresh({ focusButton = null, waitFor = null } = {}) {
    if (disposed || !active || !sessionIdentity.isCurrent(owner)) return false;
    window.clearTimeout(searchTimer);
    const focusedPager = focusButton || document.activeElement;
    const pager = [dom.prev, dom.next].includes(focusedPager) && document.activeElement === focusedPager ? focusedPager : null;
    let focusMoved = false;
    const moved = () => { focusMoved = true; };
    if (pager) { dom.results.focus(); document.addEventListener('focusin', moved); }
    clearProjection('Loading email history…');
    const load = reads.begin('history'), capturedOwner = owner, captured = JSON.stringify(route());
    const current = () => load.isCurrent() && !disposed && active && sessionIdentity.isCurrent(capturedOwner) && captured === JSON.stringify(route());
    dom['history-status'].textContent = 'Loading email history…'; dom.results.setAttribute('aria-busy', 'true');
    try {
      const query = new URLSearchParams({ scope: filters.scope, sort: filters.sort, page: String(page), pageSize: String(pageSize) });
      if (filters.search.length > 200) throw new Error('Search must contain at most 200 characters.');
      for (const [key, value] of Object.entries({ search: filters.search.trim(), status: filters.status, type: filters.type,
        reference: filters.reference, ...emailHistoryDateBounds(filters.from, filters.through) })) if (value) query.set(key, value);
      const [listed] = await Promise.allSettled([request(`/api/asap/staff/email-operations?${query}`, { signal: load.signal }), waitFor]);
      if (!current()) return false;
      if (listed.status === 'rejected') throw listed.reason;
      const data = listed.value;
      if (!Array.isArray(data?.items) || !Number.isSafeInteger(data.count) || data.count < data.items.length ||
          !Number.isSafeInteger(data.page) || data.page < 1 || data.page > page || data.pageSize !== pageSize ||
          data.items.length > pageSize || data.scope !== filters.scope || data.items.some(item => !canonicalId(item.id))) {
        throw new Error('Email history returned an unreadable response. Refresh operations to try again.');
      }
      page = data.page;
      projection = { data, owner: capturedOwner, query: JSON.stringify(route()) };
      render();
      dom.count.textContent = `${data.count.toLocaleString()} matching ${data.count === 1 ? 'operation' : 'operations'}`;
      dom.page.textContent = data.count ? `Showing ${((page - 1) * pageSize + 1).toLocaleString()}–${Math.min(page * pageSize, data.count).toLocaleString()} of ${data.count.toLocaleString()} · Page ${page.toLocaleString()}` : '0 operations · Page 1';
      dom.prev.disabled = page <= 1; dom.next.disabled = page * pageSize >= data.count;
      scopeChoices(data.libraries || []);
      choices(dom.type, [{ value: '', label: 'All types' }, ...types], filters.type);
      dom['history-status'].textContent = data.librariesTruncated ? 'Some library choices are omitted. An existing library link can still select its authorized scope.' : '';
      if (captured !== JSON.stringify(route())) changed();
      return true;
    } catch (error) {
      if (current() && !isAbortError(error) && error.status !== 401) {
        failure = error;
        dom.results.replaceChildren(element('p', { className: 'operations-empty', text: 'Email history is unavailable.' }));
        dom['history-status'].textContent = error.message || 'Email history could not be loaded. Refresh operations to try again.';
        dom['history-status'].classList.add('error');
      }
      return false;
    } finally {
      document.removeEventListener('focusin', moved);
      // Accepted page clamping changes the route, while the same load still owns focus.
      if (load.isCurrent()) dom.results.removeAttribute('aria-busy');
      if (pager && !focusMoved && load.isCurrent() && active && isVisible() && sessionIdentity.isCurrent(capturedOwner) && document.activeElement === dom.results) {
        if (!pager.disabled) pager.focus();
        else dom.results.scrollIntoView?.({ block: 'start', inline: 'nearest' });
      }
      reads.finish('history', load.token);
    }
  }
  const listen = (node, event, handler) => node.addEventListener(event, handler, { signal: events.signal });
  for (const field of fields) listen(dom[field], field === 'search' ? 'input' : 'change', () => {
    filters[field] = dom[field].value; page = 1; window.clearTimeout(searchTimer); clearProjection(); changed();
    if (field === 'search') searchTimer = window.setTimeout(() => void refresh(), 250);
    else void refresh();
  });
  listen(dom.filters, 'submit', event => { event.preventDefault(); void refresh(); });
  listen(dom.clear, 'click', () => { setRoute(); changed(); void refresh(); });
  listen(dom['reference-clear'], 'click', () => { filters.reference = ''; page = 1; clearProjection(); sync(); changed(); void refresh(); dom.search.focus(); });
  listen(dom.prev, 'click', () => { if (!dom.prev.disabled) { page--; changed(); void refresh({ focusButton: dom.prev }); } });
  listen(dom.next, 'click', () => { if (!dom.next.disabled) { page++; changed(); void refresh({ focusButton: dom.next }); } });
  return { route, setRoute, refresh, render, failure: () => failure,
    setBlocked(value) { if (blocked !== value) { blocked = value; render(); } },
    setStaff(staff) { owner = staff; setRoute(); },
    activate() { active = true; },
    deactivate() { active = false; window.clearTimeout(searchTimer); clearProjection(); },
    invalidate: clearProjection,
    dispose() { disposed = true; active = false; window.clearTimeout(searchTimer); clearProjection(); events.abort(); }
  };
}
