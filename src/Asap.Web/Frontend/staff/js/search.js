import { authorizedJson, isAbortError, loadStaffSession } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element } from './ui.js';

export const searchGroups = Object.freeze({ 'title:suggestion': 'Title · Suggestions', 'title:outstanding_purchase': 'Title · Purchases',
  'title:pending_hold': 'Title · Pending holds', 'title:hold_placed': 'Title · Placed holds', 'title:closed': 'Title · Closed',
  'copy:open': 'Additional copy · Open', 'copy:closed': 'Additional copy · Closed' });
const placeholder = 'Search titles, authors, patrons, barcodes, or identifiers';
const normalize = value => value.replace(/\s+/gu, ' ').trim();

// Search is a read carried by POST for privacy. Give it the same post-read
// identity check as authorized GETs before accepting any private response.
async function readSearch(path, options) {
  const result = await authorizedJson(path, options);
  await loadStaffSession({ signal: options.signal });
  return result;
}

// Private terms and results live only in this session owner, never in URL/history/storage.
export function createSubmissionSearch({ dialog, view, launcher, detailLauncher, detailHost, sessionIdentity,
  getScope, canOpen = () => true, openResult, openAll, announce, request = readSearch, debounceMs = 180 }) {
  const events = new window.AbortController(), reads = createLatestLoad();
  let disposed = false, active = false, timer = null, opener = null, resumeDetail = null, revision = 0;
  let query = '', scope = null, group = '', page = 1, result = null, state = 'idle', errorText = '', libraries = [];
  let selected = -1, options = [], accepted = null, selection = null;
  const get = (root, name) => root.querySelector(`[data-search="${name}"]`);
  const controls = [dialog, view].map((root, index) => {
    const prefix = index ? 'search-results' : 'search-quick';
    const input = element('input', { id: `${prefix}-input`, type: 'search', placeholder, maxlength: 160, autocomplete: 'off', spellcheck: 'false' });
    const scopeSelect = element('select', { id: `${prefix}-scope` });
    const scopeField = element('label', { className: 'compact-field' }, [element('span', { text: 'Search library' }), scopeSelect]);
    const label = element('label', { for: input.id, text: 'Search submissions' });
    const scopeText = element('p', { className: 'search-scope', 'data-search': 'scope' });
    get(root, 'controls').append(element('div', { className: 'search-query' }, [label, input]), scopeField, scopeText);
    const summary = get(root, 'summary'), list = get(root, 'list'), retry = get(root, 'retry');
    if (!index) {
      input.setAttribute('role', 'combobox'); input.setAttribute('aria-autocomplete', 'list');
      input.setAttribute('aria-controls', list.id); input.setAttribute('aria-expanded', 'false');
      list.setAttribute('role', 'listbox'); list.setAttribute('aria-label', 'Matching submissions');
    }
    input.addEventListener('input', () => { query = input.value; group = ''; page = 1; replaceLoad(true); }, { signal: events.signal });
    scopeSelect.addEventListener('change', () => { scope = scopeSelect.value; group = ''; page = 1; replaceLoad(); }, { signal: events.signal });
    retry.addEventListener('click', () => replaceLoad(), { signal: events.signal });
    return { root, input, scopeSelect, scopeField, scopeText, summary, list, retry };
  });
  const filter = element('select', { id: 'search-result-filter' });
  get(view, 'filters').append(element('label', { className: 'compact-field' }, [element('span', { text: 'Result type and status' }), filter]),
    element('p', { text: 'Group counts include every match for this query and library. The result total below applies the selected filter.' }));
  filter.addEventListener('change', () => { group = filter.value; page = 1; replaceLoad(); }, { signal: events.signal });
  const previous = get(view, 'previous'), next = get(view, 'next'), all = get(dialog, 'all');
  previous.addEventListener('click', () => { if (result && page > 1) { page--; replaceLoad(); } }, { signal: events.signal });
  next.addEventListener('click', () => { if (result && page * result.pageSize < result.filteredTotal) { page++; replaceLoad(); } }, { signal: events.signal });
  all.addEventListener('click', async () => {
    if (!accepted || result !== accepted.result || !sessionIdentity.isCurrent(accepted.owner)) return;
    close({ refreshFull: false }); group = ''; page = 1;
    await openAll();
  }, { signal: events.signal });

  function retire() { revision++; window.clearTimeout(timer); timer = null; reads.begin('search').abort(); selection?.abort(); selection = null; }
  function clearResults() { result = null; accepted = null; selected = -1; options = []; }
  function validQuery() { return query.length <= 160 && normalize(query).split(' ').filter(Boolean).length <= 12; }
  function replaceLoad(debounce = false) {
    retire(); clearResults();
    state = normalize(query) ? validQuery() ? 'loading' : 'invalid' : 'idle';
    render();
    if (disposed || !sessionIdentity.actor() || !dialog.open && !active) return;
    if (debounce && state === 'loading') timer = window.setTimeout(() => { timer = null; void load(); }, debounceMs);
    else if (state !== 'invalid') void load();
  }
  async function load() {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner || !validQuery()) return;
    const stamp = revision, load = reads.begin('search');
    const input = Object.freeze({ query: normalize(query), scope, group: dialog.open ? '' : group, page: dialog.open ? 1 : page, pageSize: dialog.open ? 12 : 25 });
    const current = () => !disposed && (dialog.open || active) && stamp === revision && load.isCurrent() && sessionIdentity.isCurrent(owner);
    try {
      const value = await request('/api/asap/staff/search', { method: 'POST', body: input, signal: load.signal });
      if (!current()) return;
      result = value; scope = value.scope; if (!dialog.open) page = value.page; libraries = value.libraries;
      accepted = Object.freeze({ result, owner, scope, stamp });
      state = !input.query ? 'idle' : value.filteredTotal ? 'results' : 'empty';
      render();
    } catch (error) {
      if (current() && !isAbortError(error) && error.status !== 401 && error.response?.accessAllowed !== false) {
        clearResults(); state = 'failed'; errorText = error.message || 'Search could not be completed.'; render();
      }
    } finally { reads.finish('search', load.token); }
  }
  function setSelection(index, scroll = true) {
    selected = index;
    for (const [offset, node] of options.entries()) {
      node.setAttribute('aria-selected', String(offset === selected)); node.classList.toggle('search-active', offset === selected);
    }
    if (options[selected]) {
      controls[0].input.setAttribute('aria-activedescendant', options[selected].id);
      // A result can be taller than a short viewport; keep its identifying title visible.
      if (scroll) options[selected].firstElementChild?.scrollIntoView?.({ block: 'nearest' });
    } else controls[0].input.removeAttribute('aria-activedescendant');
  }
  async function select(item, snapshot, button) {
    if (!snapshot || accepted !== snapshot || snapshot.result !== result || !sessionIdentity.isCurrent(snapshot.owner)) return;
    const focus = dialog.open ? launcher : button;
    close({ refreshFull: false });
    const pending = selection = new window.AbortController(), stamp = revision;
    const intent = Object.freeze({ type: item.type, id: item.id, scope: snapshot.scope, owner: snapshot.owner, opener: focus,
      signal: pending.signal, isCurrent: () => !disposed && !pending.signal.aborted && revision === stamp && sessionIdentity.isCurrent(snapshot.owner) });
    const completed = await openResult(intent);
    if (selection === pending) selection = null;
    // A failed or cancelled departure keeps the source mounted and focused. Refresh
    // the still-visible results; a later reopening always re-fetches the preview.
    if (!completed && intent.isCurrent() && active) replaceLoad();
  }
  function row(item, snapshot, quick, index) {
    const node = element(quick ? 'div' : 'button', { className: 'search-result',
      ...(quick ? { id: `search-option-${index}`, role: 'option', 'aria-selected': 'false' } : { type: 'button' }) }, [
      element('strong', { text: item.title }), item.author ? element('span', { text: item.author }) : null,
      item.patron ? element('span', { text: `Patron: ${item.patron}${item.barcode ? ` · ${item.barcode}` : ''}` }) :
        item.barcode ? element('span', { text: `Barcode: ${item.barcode}` }) : null,
      element('span', { className: 'search-result-meta', text: `${item.libraryOrgName} · ${searchGroups[`${item.type}:${item.status}`]} · ${item.type === 'copy' ? 'Copy' : 'Request'} ${item.id}` }),
      item.identifier || item.bibId ? element('span', { className: 'search-result-meta', text: [item.identifier, item.bibId ? `BIB ${item.bibId}` : null].filter(Boolean).join(' · ') }) : null
    ]);
    node.addEventListener('click', () => select(item, snapshot, node));
    if (quick) node.addEventListener('mousemove', () => setSelection(index, false));
    return node;
  }
  function render() {
    const message = state === 'loading' ? 'Searching authorized submissions…' : state === 'failed' ? `Search unavailable. ${errorText}` :
      state === 'invalid' ? 'Use up to 160 characters and 12 words.' : state === 'idle' ? 'Enter a title, author, patron, barcode, or identifier to search submissions.' :
        state === 'empty' ? 'No matching submissions in this search scope.' : `${result.filteredTotal} matching ${result.filteredTotal === 1 ? 'submission' : 'submissions'}.`;
    options = [];
    for (const [index, control] of controls.entries()) {
      const quick = index === 0;
      if (control.input.value !== query) control.input.value = query;
      control.scopeField.hidden = sessionIdentity.actor()?.role !== 'super_admin';
      control.scopeSelect.replaceChildren(...(sessionIdentity.actor()?.role === 'super_admin' ? [element('option', { value: 'all', text: 'All authorized libraries' })] : []),
        ...libraries.map(item => element('option', { value: String(item.id), text: item.name })));
      control.scopeSelect.value = scope || '';
      control.scopeText.textContent = !libraries.length ? 'Resolving authorized search scope…' : `Searching: ${scope === 'all' ? 'All authorized libraries' : libraries.find(item => String(item.id) === scope)?.name || 'Library unavailable'}`;
      control.summary.textContent = message; control.retry.hidden = state !== 'failed';
      control.list.setAttribute('aria-busy', String(state === 'loading')); control.list.replaceChildren();
      if (state !== 'results') continue;
      if (quick) {
        let offset = 0;
        // The first server-ranked result determines group order. Grouping must
        // never push an exact identifier behind a lower-ranked workflow group.
        const keys = [...new Set([...result.items.map(item => `${item.type}:${item.status}`), ...result.groups.map(item => item.key)])];
        for (const key of keys) {
          const facet = result.groups.find(item => item.key === key);
          const items = result.items.filter(item => `${item.type}:${item.status}` === facet.key);
          if (!facet.count) continue;
          const heading = element('h3', { role: 'presentation', id: `search-group-${facet.key.replace(':', '-')}`, text: `${searchGroups[facet.key]} (${facet.count})` });
          const section = element('div', { role: 'group', 'aria-labelledby': heading.id }, [heading]);
          for (const item of items) { const node = row(item, accepted, true, offset++); options.push(node); section.append(node); }
          if (!items.length) section.append(element('p', { className: 'search-result-meta', text: 'More matches in View all results.' }));
          control.list.append(section);
        }
      } else control.list.append(...result.items.map((item, offset) => row(item, accepted, false, offset)));
    }
    controls[0].input.setAttribute('aria-expanded', String(dialog.open && options.length > 0));
    controls[0].input.removeAttribute('aria-activedescendant'); selected = -1;
    all.hidden = !result || state !== 'results' || result.total <= result.items.length;
    filter.replaceChildren(element('option', { value: '', text: `All types and statuses (${result?.total ?? '—'})` }),
      ...Object.entries(searchGroups).map(([key, label]) => element('option', { value: key, text: `${label} (${result?.groups.find(item => item.key === key)?.count ?? '—'})` })));
    filter.value = group;
    previous.disabled = !result || state !== 'results' || page <= 1;
    next.disabled = !result || state !== 'results' || page * result.pageSize >= result.filteredTotal;
    get(view, 'page').textContent = result && normalize(query) ? `Page ${page} of ${Math.max(1, Math.ceil(result.filteredTotal / result.pageSize))}` : '';
  }
  function close({ restoreFocus = true, refreshFull = true } = {}) {
    const wasOpen = dialog.open;
    retire();
    if (dialog.open) dialog.close();
    const resume = resumeDetail; resumeDetail = null; resume?.();
    const target = opener?.isConnected && !opener.closest('[hidden]') ? opener : launcher;
    if (restoreFocus && sessionIdentity.actor() && target?.isConnected && (!target.closest('dialog') || target.closest('dialog').open)) target.focus();
    opener = null;
    if (wasOpen && active && refreshFull) replaceLoad();
  }
  function open(source = launcher) {
    if (disposed || !sessionIdentity.actor() || dialog.open) return false;
    if (!canOpen()) { announce('Wait for the current action to finish before opening Search ASAP.', 'warning'); return false; }
    const competing = [...document.querySelectorAll('dialog[open]')];
    if (competing.length && !(competing.length === 1 && detailHost?.contains(source))) return false;
    resumeDetail = competing.length ? detailHost.suspendForSearch() : null;
    opener = source;
    const operationalScope = active ? scope : getScope();
    if (operationalScope && operationalScope !== 'system' && operationalScope !== '1' && operationalScope !== scope) {
      scope = operationalScope; group = ''; page = 1;
    }
    dialog.showModal(); controls[0].input.focus(); replaceLoad(); return true;
  }
  launcher.addEventListener('click', () => open(launcher), { signal: events.signal });
  detailLauncher?.addEventListener('click', () => open(detailLauncher), { signal: events.signal });
  get(dialog, 'close').addEventListener('click', () => close(), { signal: events.signal });
  dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }, { signal: events.signal });
  dialog.addEventListener('keydown', event => {
    // Native search inputs consume the first Escape to clear text. Search owns
    // dismissal so one press closes the panel and preserves the useful query.
    if (event.key === 'Escape' && !event.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); close(); }
  }, { signal: events.signal });
  controls[0].input.addEventListener('keydown', event => {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (!options.length) return;
      event.preventDefault(); setSelection(selected < 0 ? event.key === 'ArrowDown' ? 0 : options.length - 1
        : (selected + (event.key === 'ArrowDown' ? 1 : -1) + options.length) % options.length);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (selected >= 0) options[selected]?.click();
    }
  }, { signal: events.signal });
  document.addEventListener('keydown', event => {
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.altKey || event.shiftKey ||
        !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'k' || document.querySelector('dialog[open]')) return;
    if (open(launcher)) event.preventDefault();
  }, { signal: events.signal });
  render();
  return {
    open, close, refresh: () => replaceLoad(),
    dismissForNavigation() { if (dialog.open) close(); },
    activate() { active = true; replaceLoad(); },
    deactivate() { active = false; retire(); },
    invalidate() { close({ restoreFocus: false, refreshFull: false }); clearResults(); state = normalize(query) ? 'loading' : 'idle'; render(); },
    setStaff() { retire(); query = ''; scope = null; group = ''; page = 1; libraries = []; clearResults(); state = 'idle'; render(); },
    signedOut() { close({ restoreFocus: false, refreshFull: false }); active = false; query = ''; scope = null; group = ''; page = 1; libraries = []; clearResults(); state = 'idle'; render(); },
    dispose() { if (disposed) return; close({ restoreFocus: false, refreshFull: false }); disposed = true; events.abort(); clearResults(); }
  };
}
