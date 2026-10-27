import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { byDisplayLabel } from './display-order.js';
import { researchRecordUrl } from './research.js';
import { element, dateTime, statusLabel, closeReasonLabel, timeoutLabel } from './ui.js';

function detachGridContainer(container) {
  const replacement = container.cloneNode(false);
  container.replaceWith(replacement);
  return replacement;
}

function createQueueGrid(options, container) {
  // Grid.js 6 forceRender reuses a mutable store/pipeline: old sort completions
  // can replace newer filtered rows. Give each revision its own instance while
  // keeping the outer queue/focus owner stable and preserving its selected sort.
  let disposed = false;
  let displayed = null;
  let current = create(options);

  function create(configuration, sort = null, stage = false) {
    // Grid.js normalizes columns in place; retain pristine options for later revisions.
    // A newly mounted sort button initializes direction from its column.
    const columns = configuration.columns.map((column, index) => {
      const selected = sort?.columns.find(item => item.index === index);
      return selected ? { ...column, sort: { ...column.sort, direction: selected.direction } } : { ...column };
    });
    const grid = new window.gridjs.Grid({ ...configuration, columns });
    if (sort) {
      grid.config.store.dispatch(state => ({ ...state,
        sort: { ...sort, columns: sort.columns.map(column => ({ ...column })) } }));
    }
    const pipeline = grid.config.pipeline;
    const process = pipeline.process.bind(pipeline);
    let tail = Promise.resolve();
    let retired = false, pending = 0, destroyed = false;
    let observer = null, unsubscribe = null;
    const cleanup = () => {
      if (retired && !pending && !destroyed) {
        destroyed = true;
        grid.destroy();
      }
    };
    pipeline.process = (...args) => {
      // A throttled render may enter after retirement. Already-entered work
      // finishes against its detached frame before destroy unregisters Sort.
      if (retired) return Promise.resolve(null);
      pending++;
      const result = tail.then(() => {
        if (retired) return null;
        // Mounting Sort can invalidate an earlier run's cache marker. Recompute
        // these local array rows in order, including every current processor.
        pipeline.clearCache();
        return process(...args);
      });
      const complete = () => { pending--; cleanup(); };
      tail = result.then(complete, complete);
      return result;
    };
    const frame = document.createElement('div');
    const revision = { grid, frame, retire() {
      retired = true;
      observer?.disconnect(); unsubscribe?.();
      frame.remove(); cleanup();
    } };
    if (stage) {
      // A status selection filters accepted rows. Grid.js first mounts an empty
      // body, then commits data asynchronously. Keep the previous frame inert
      // until the replacement has committed its own rows, including an empty result.
      const present = () => {
        const state = grid.config.store.getState(), body = frame.querySelector('tbody');
        // Vendored Grid.js 6.2.0 status 3 is Rendered; its store may lead the DOM.
        if (disposed || retired || pending || state.status !== 3 ||
            !body || body.rows.length !== Math.max(1, state.data?.rows.length ?? 0)) return;
        observer.disconnect(); unsubscribe();
        displayed?.retire();
        container.replaceChildren(frame);
        displayed = revision;
      };
      observer = new window.MutationObserver(present);
      observer.observe(frame, { childList: true, subtree: true });
      unsubscribe = grid.config.store.subscribe(present);
    } else {
      container.replaceChildren(frame);
      displayed = revision;
    }
    grid.render(frame);
    return revision;
  }

  return {
    // A pending status revision has already reset pagination; the retained
    // visible frame still belongs to the previous status until the atomic swap.
    page: () => current === displayed ? Number(container.querySelector('.gridjs-currentPage')?.textContent || 1) - 1 : 0,
    replaceRows(rows, page = 0, stage = false) {
      if (disposed) return;
      const sort = stage ? null : current.grid.config.store.getState().sort;
      if (current !== displayed) current.retire();
      if (stage) {
        displayed.frame.inert = true;
        displayed.frame.setAttribute('aria-hidden', 'true');
      }
      else displayed?.retire();
      current = create({ ...options, data: rows, pagination: { ...options.pagination, page } }, sort, stage);
    },
    destroy() {
      if (disposed) return;
      disposed = true;
      current.retire();
      if (displayed !== current) displayed?.retire();
    }
  };
}

function bindStatusTabs(tabs, key, intent, signal) {
  for (const tab of tabs) {
    tab.addEventListener('click', () => intent(tab.dataset[key]), { signal });
    tab.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = tabs.indexOf(tab);
      const offset = event.key === 'ArrowRight' ? 1 : -1;
      const target = event.key === 'Home' ? tabs[0] : event.key === 'End' ? tabs.at(-1)
        : tabs[(index + offset + tabs.length) % tabs.length];
      target.focus(); target.click();
    }, { signal });
  }
}

function populateLibraries(select, organizations, scope) {
  select.replaceChildren(element('option', { value: 'all', text: 'All libraries' }));
  for (const organization of byDisplayLabel(organizations)) {
    select.append(element('option', { value: organization.id, text: organization.name }));
  }
  select.value = scope;
}

function affectsProjection(scope, loadedScope) {
  return scope === 'system' || String(scope) === '1' || loadedScope === 'all' || String(scope) === loadedScope;
}

function focusPort({ grid, label, fallback, sessionIdentity, getContext, copy, selector = '.grid-open' }) {
  const owner = sessionIdentity.preferences();
  const context = getContext();
  return { root: grid, fallback,
    target: () => [...grid().querySelectorAll(selector)].find(button => {
      const name = button.getAttribute('aria-label') || '';
      return name === label || name.startsWith(`${label}: `);
    }),
    isCurrent: () => sessionIdentity.isCurrent(owner) && getContext().activeView === context.activeView &&
      getContext().scope === context.scope && (copy ? getContext().additionalCopyStatus === context.additionalCopyStatus : getContext().status === context.status)
  };
}

export function createTitleQueue({ root, sessionIdentity, getContext, announce,
  onOpen, onScopeIntent, onStatusIntent, onScopeAccepted, onLibraries, onRefreshed, onRendered,
  onNewSuggestion = null, polarisLookup = null,
  resolveScope = () => getContext().scope,
  request = authorizedJson }) {
  const dom = {
    statusTabs: [...root.querySelectorAll('#status-tabs [data-status]')],
    scopeField: root.querySelector('#scope-field'),
    scope: root.querySelector('#library-scope'),
    search: root.querySelector('#request-search'),
    claim: root.querySelector('#claim-filter'),
    tag: root.querySelector('#tag-filter'),
    similar: root.querySelector('#similar-filter'),
    similarField: root.querySelector('#similar-filter-field'),
    queueAutomation: root.querySelector('#queue-automation'),
    refresh: root.querySelector('#refresh-queue'),
    summary: root.querySelector('#queue-summary'),
    grid: root.querySelector('#request-grid'),
    empty: root.querySelector('#queue-empty'),
  };
  const state = { requests: [], grid: null, gridStatus: null, loadedContext: null, stale: true, refreshRequired: false, sequence: 0 };
  // The API accepts a library scope and returns every title status in that scope.
  const isReady = context => !state.stale && !state.refreshRequired && state.loadedContext?.scope === context.scope;
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let disposed = false, acceptingScope = false, targetGeneration = 0, lookupContext = null;
  const gridAccessibility = new window.MutationObserver(() => {
    const wrapper = dom.grid.querySelector('.gridjs-wrapper');
    if (!wrapper || disposed) return;
    wrapper.tabIndex = 0;
    wrapper.setAttribute('role', 'region');
    wrapper.setAttribute('aria-label', 'Title requests');
    wrapper.setAttribute('aria-describedby', 'request-table-help');
  });
  gridAccessibility.observe(root, { childList: true, subtree: true });

  function closeLookup() { if (lookupContext) polarisLookup?.close(lookupContext); lookupContext = null; }
  function invalidate() { closeLookup(); targetGeneration++; reads.begin('queue-target').abort(); reads.begin('queue').abort(); if (!disposed) dom.refresh.disabled = false; }

  async function prepare(context) {
    const owner = sessionIdentity.preferences(), load = reads.begin('queue-target'), ticket = ++targetGeneration;
    const live = () => !disposed && !load.signal.aborted && ticket === targetGeneration && sessionIdentity.isCurrent(owner);
    try {
      const scope = owner.role === 'super_admin' ? context.scope : String(owner.organizationId);
      const result = await request(`/api/asap/staff/title-requests?scope=${encodeURIComponent(scope)}`, { signal: load.signal });
      if (!live()) return null;
      return { scope: result.scope, isCurrent: live, accept: () => acceptResult(result, ++state.sequence) };
    } finally { reads.finish('queue-target', load.token); }
  }

  function acceptResult(result, sequence) {
    if (disposed) return;
    state.requests = Object.freeze(Array.isArray(result.items) ? result.items.map(item => Object.freeze({ ...item })) : []);
    state.loadedContext = Object.freeze({ scope: result.scope, status: getContext().status });
    state.stale = false;
    state.refreshRequired = false;
    if (sessionIdentity.actor()?.role === 'super_admin') { setLibraries(result.organizations); onLibraries(result.organizations); }
    populateTags(); renderGrid({ preservePage: true });
    onRefreshed({ sequence, items: state.requests, context: state.loadedContext });
  }

  async function refresh(options = {}) {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner) return false;
    const load = reads.begin('queue');
    const sequence = ++state.sequence;
    // A canceled authoritative reload still requires a read on navigation, while
    // accepted rows remain usable to open fresh details during that reload.
    state.refreshRequired = true;
    dom.refresh.disabled = true;
    const clearLoading = !options.silent ? announce('Loading authorized requests...') : null;
    try {
      const resolution = resolveScope(owner);
      const acceptedScope = typeof resolution === 'string' ? resolution : await resolution;
      if (!acceptedScope || disposed || !load.isCurrent() || !sessionIdentity.isCurrent(owner)) return false;
      acceptingScope = true;
      try { onScopeAccepted(acceptedScope); } finally { acceptingScope = false; }
      const context = getContext();
      const scope = owner.role === 'super_admin' ? context.scope : String(owner.organizationId);
      const result = await request(`/api/asap/staff/title-requests?scope=${encodeURIComponent(scope)}`, { signal: load.signal });
      if (disposed || !load.isCurrent() || !sessionIdentity.isCurrent(owner) || context.scope !== getContext().scope || context.status !== getContext().status) return false;
      acceptingScope = true;
      try { onScopeAccepted(result.scope); } finally { acceptingScope = false; }
      acceptResult(result, sequence);
      return true;
    } catch (error) {
      if (!disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) &&
          !options.silent && !isAbortError(error) && error.status !== 401) announce(error.message || 'Requests could not be loaded.', 'error');
      return false;
    } finally {
      if (typeof clearLoading === 'function') clearLoading();
      if (!disposed && load.isCurrent()) dom.refresh.disabled = false;
      reads.finish('queue', load.token);
    }
  }

  function setLibraries(libraries) { if (!disposed) populateLibraries(dom.scope, libraries, getContext().scope); }
  function setStaff(staff) {
    if (disposed) return;
    dom.scopeField.hidden = staff?.role !== 'super_admin';
    dom.scope.replaceChildren(element('option', { value: getContext().scope, text: staff?.role === 'super_admin' ? 'All libraries' : 'My library' }));
    resetQueueFilters(); updateStatusTabs();
  }

  function resetQueueFilters() {
    if (disposed) return;
    dom.search.value = '';
    dom.tag.value = 'all';
    dom.similar.value = 'all';
    dom.claim.value = sessionIdentity.preferences()?.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
  }

  function retireProjection() {
    if (disposed) return;
    state.requests = [];
    state.loadedContext = null;
    state.stale = true;
    if (!acceptingScope) invalidate();
    state.grid?.destroy?.();
    // An older Grid.js render can finish after navigation; keep its container detached.
    dom.grid = detachGridContainer(dom.grid);
    state.grid = null;
    populateTags();
    renderGrid();
  }

  function markStale(scope = 'system') {
    if (!disposed && affectsProjection(scope, state.loadedContext?.scope ?? getContext().scope)) retireProjection();
  }

  function updateStatusTabs() {
    for (const tab of dom.statusTabs) {
      const selected = tab.dataset.status === getContext().status;
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
    }
    dom.similarField.hidden = getContext().status !== 'suggestion';
  }

  function populateTags() {
    const selected = dom.tag.value;
    const tags = [...new Set(state.requests.flatMap(request => request.workflowTags || []))]
      .sort((first, second) => first.localeCompare(second));
    dom.tag.replaceChildren(element('option', { value: 'all', text: 'All tags' }));
    for (const tag of tags) dom.tag.append(element('option', { value: tag, text: tag }));
    dom.tag.value = tags.includes(selected) ? selected : 'all';
  }

  function filteredRequests() {
    const query = dom.search.value.trim().toLocaleLowerCase();
    const claim = dom.claim.value;
    const tag = dom.tag.value;
    const similarity = dom.similar.value;
    return state.requests.filter(request => {
      if (request.status !== getContext().status) return false;
      if (getContext().status === 'suggestion' && similarity !== 'all') {
        const count = request.relatedRequests?.count;
        if (!Number.isInteger(count) || (similarity === 'similar' ? count < 1 : count !== 0)) return false;
      }
      const mine = request.claimedByStaffUserId === sessionIdentity.preferences()?.id;
      const unclaimed = !request.claimedByStaffUserId;
      if (claim === 'mine' && !mine) return false;
      if (claim === 'unclaimed' && !unclaimed) return false;
      if (claim === 'mine_unclaimed' && !mine && !unclaimed) return false;
      if (tag !== 'all' && !(request.workflowTags || []).includes(tag)) return false;
      if (!query) return true;
      return [request.title, request.author, request.nameFirst, request.nameLast,
        request.barcode, request.identifier, request.bibid, request.libraryOrgName]
        .filter(Boolean)
        .some(value => String(value).toLocaleLowerCase().includes(query));
    });
  }

  function scopeForLibraryOrAll(libraryOrgId) {
    const libraryScope = String(libraryOrgId);
    return [...dom.scope.options].some(option => option.value === libraryScope) ? libraryScope : 'all';
  }

  function workflowLabel(request) {
    const workflow = request.workflowContext;
    if (!workflow) return 'Unavailable';
    if (getContext().status === 'suggestion') return timeoutLabel(workflow.outstandingTimeoutEnabled, workflow.outstandingTimeoutDays);
    if (getContext().status === 'outstanding_purchase') return workflow.autoPromote ? 'On' : 'Off';
    if (getContext().status === 'pending_hold') return timeoutLabel(workflow.pendingHoldTimeoutEnabled, workflow.pendingHoldTimeoutDays);
    if (getContext().status === 'hold_placed') return timeoutLabel(workflow.holdPickupTimeoutEnabled, workflow.holdPickupTimeoutDays);
    return '—';
  }

  function queueColumns() {
    return [
      { label: 'Barcode', kind: 'patron', width: '14%', value: request => request.barcode || 'Not recorded' },
      // The combined bibliographic column sorts by Title; Author has its own search shortcut.
      { label: 'Bibliographic details', kind: 'bibliographic', width: '23%', value: request => request.title },
      { label: 'Format', width: '8%', value: request => request.formatLabel || request.format || 'Not recorded' },
      { label: 'Submitted', kind: 'submitted', width: '10%', value: request => request.created || '' },
      { label: 'Library', width: '10%', value: request => request.libraryOrgName },
      { label: 'Claim', kind: 'claim', width: '9%', value: request => request.claimedByDisplayName || 'Unclaimed' },
      { label: 'Status', kind: 'status', width: '9%', value: request => statusLabel(request.status) },
      { label: 'Actions', kind: 'actions', width: '17%', value: request => request.id }
    ];
  }

  function liveRow(event, owner, request) {
    return sessionIdentity.isCurrent(owner) && !disposed && !state.stale && state.requests.includes(request) &&
      request.status === getContext().status && getContext().activeView === 'queue' &&
      event.currentTarget.isConnected && dom.grid.contains(event.currentTarget);
  }

  function searchCatalog(request, mode, opener) {
    if (!polarisLookup) return;
    const owner = sessionIdentity.preferences(), projection = state.requests, context = getContext();
    lookupContext = {
      requestId: request.id, libraryOrgId: request.libraryOrgId, mode,
      query: mode === 'author' ? request.author || '' : request.title,
      readOnly: true, canApply: false, returnFocus: opener,
      isCurrent: () => !disposed && !state.stale && state.requests === projection && sessionIdentity.isCurrent(owner) &&
        getContext().activeView === 'queue' && getContext().scope === context.scope && getContext().status === context.status && opener.isConnected
    };
    polarisLookup.open(lookupContext);
  }

  function formatCell(column, cell, row, owner) {
    const h = window.gridjs.h, request = state.requests.find(item => item.id === row.cells.at(-1).data);
    if (!request) return cell;
    const live = event => liveRow(event, owner, request);
    const glyph = name => h('i', { className: `fa fa-${name}`, 'aria-hidden': 'true' });
    if (column.kind === 'patron') {
      const name = [request.nameFirst, request.nameLast].filter(Boolean).join(' ') || 'Name not recorded';
      const url = researchRecordUrl(request.research, 'patron');
      return h('div', { className: 'request-patron-cell' }, [
        h('div', { className: 'request-patron-text' }, [
          url ? h('a', { href: url, target: '_blank', rel: 'noopener noreferrer', title: String(cell) }, String(cell)) : h('span', {}, cell),
          h('small', { className: 'request-cell-text', title: name }, name)
        ]),
        request.barcode?.trim() && onNewSuggestion ? h('button', {
          type: 'button', className: 'grid-new-suggestion',
          'aria-label': `New suggestion for patron ${request.barcode.trim()} from request ${request.id}`,
          title: `New suggestion for ${name}`,
          onClick: event => { if (live(event)) onNewSuggestion(request.barcode.trim(), event.currentTarget, request.id); }
        }, glyph('plus')) : null
      ]);
    }
    if (column.kind === 'bibliographic') {
      const url = new URL(window.location.href);
      url.searchParams.set('request', request.id);
      url.searchParams.set('stage', getContext().status);
      url.searchParams.set('scope', getContext().scope);
      const search = (mode, value) => h('button', { type: 'button', className: 'request-search-shortcut', disabled: !value?.trim(),
        'aria-label': `Search Polaris for ${mode}: ${value || 'Not recorded'}`, title: `Search Polaris for ${mode}: ${value || 'Not recorded'}`,
        onClick: event => { if (live(event)) searchCatalog(request, mode, event.currentTarget); }
      }, glyph('search'));
      const bibUrl = researchRecordUrl(request.research, 'bib', request.bibid);
      const flags = [...new Set([...(request.workflowTags || []),
        request.isbnCheckStatus === 'not_found' && !(request.workflowTags || []).includes('Polaris BIB not found') ? 'Identifier not found in Polaris' : null,
        request.relatedRequests?.count > 0 ? `${request.relatedRequests.count} related request(s)` : null].filter(Boolean))];
      return h('div', { className: 'request-bibliographic-cell' }, [
        h('div', { className: 'request-bibliographic-line' }, [
          h('a', { href: url.pathname + url.search, className: 'grid-open request-title-link request-cell-text', title: request.title,
            'data-request-id': request.id, 'aria-label': `Open request ${request.id}: ${request.title}`,
            onClick: event => {
              if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button > 0) return;
              event.preventDefault(); if (live(event)) onOpen(request.id, event.currentTarget);
            }
          }, request.title), search('title', request.title)
        ]),
        h('div', { className: 'request-bibliographic-line request-author-line' }, [
          h('span', { className: 'request-cell-text', title: request.author || 'Author not recorded' }, request.author || 'Author not recorded'), search('author', request.author)
        ]),
        bibUrl ? h('a', { className: 'request-bib-link', href: bibUrl, target: '_blank', rel: 'noopener noreferrer' }, `BIB ${request.bibid}`) : null,
        flags.length ? h('div', { className: 'tags request-row-flags' }, flags.map(flag => h('span', { className: 'tag' }, flag))) : null
      ]);
    }
    if (column.kind === 'submitted') {
      const date = new Date(cell);
      return cell && !Number.isNaN(date.getTime()) ? h('time', { dateTime: cell, title: dateTime(cell) }, [
        h('span', {}, date.toLocaleDateString()), h('small', {}, date.toLocaleTimeString())
      ]) : dateTime(cell);
    }
    if (column.kind === 'claim') return h('span', { className: `claim-label${request.claimedByStaffUserId === owner.id ? ' mine' : ''}`, title: cell }, cell);
    if (column.kind === 'status') return h('div', {}, [h('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}` }, cell),
      request.status === 'closed' ? h('small', {}, closeReasonLabel(request.closeReason)) : null]);
    if (column.kind !== 'actions') return cell;
    const command = (label, action, name, className = 'secondary-button') => h('button', {
      type: 'button', className, 'aria-label': `${label} request ${request.id}`,
      onClick: event => {
        if (!live(event)) return;
        const popup = event.currentTarget.closest('[popover]');
        const opener = popup ? popup.parentElement.querySelector('.request-row-more') : event.currentTarget;
        popup?.hidePopover();
        onOpen(request.id, opener, { action });
      }
    }, [glyph(name), label]);
    const allowed = new Set(request.capabilities?.allowedActions || []);
    const options = [command('Open details', 'edit', 'pencil')];
    if (allowed.has('alreadyOwn')) options.push(command('Already own', 'alreadyOwn', 'book'));
    if (request.status !== 'closed') {
      if (!request.claimedByStaffUserId) options.push(command('Claim', 'claim', 'user-plus'));
      options.push(command('Assign', 'assign', 'users'));
    }
    if (allowed.has('silentClose')) options.push(command('Close silently', 'silentClose', 'archive'));
    const popupId = `request-row-${request.id}-actions`;
    const more = h('button', { type: 'button', className: 'request-row-more secondary-button', 'aria-label': `More actions for request ${request.id}`,
      'aria-controls': popupId, 'aria-expanded': 'false', title: 'More actions',
      onClick: event => {
        if (!live(event)) return;
        const popup = event.currentTarget.parentElement.querySelector('[popover]');
        if (popup.matches(':popover-open')) { popup.hidePopover(); return; }
        popup.showPopover();
        const rect = event.currentTarget.getBoundingClientRect();
        popup.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - popup.offsetWidth - 8))}px`;
        popup.style.top = `${Math.max(8, Math.min(rect.bottom + 4, window.innerHeight - popup.offsetHeight - 8))}px`;
        popup.querySelector('button')?.focus();
      }
    }, glyph('ellipsis-h'));
    return h('div', { className: 'request-row-actions' }, [
      allowed.has('purchase') ? command('Purchase', 'purchase', 'shopping-cart', 'primary-button') : null,
      allowed.has('reject') ? command('Reject', 'reject', 'ban', 'danger-button') : null,
      more,
      h('div', { id: popupId, popover: 'auto', className: 'request-table-menu', role: 'group', 'aria-label': `Actions for request ${request.id}`,
        onToggle: event => event.currentTarget.parentElement.querySelector('.request-row-more')?.setAttribute('aria-expanded', String(event.newState === 'open')),
        onKeyDown: event => {
          if (event.key !== 'Escape') return;
          event.preventDefault(); event.stopPropagation(); event.currentTarget.hidePopover();
          event.currentTarget.parentElement.querySelector('.request-row-more')?.focus();
        }
      }, options)
    ]);
  }

  function renderGrid({ preservePage = false } = {}) {
    if (disposed) return;
    closeLookup();
    const owner = sessionIdentity.preferences();
    onRendered();
    const requests = filteredRequests();
    dom.summary.textContent = state.stale ? 'Requests unavailable. Refresh to load current requests.'
      : `${requests.length} ${statusLabel(getContext().status).toLocaleLowerCase()} request${requests.length === 1 ? '' : 's'}`;
    dom.empty.hidden = state.stale || requests.length !== 0;
    if (state.stale) {
      dom.queueAutomation.textContent = getContext().status === 'closed' ? '' : 'Workflow rules will appear with requests in this stage.';
      return;
    }
    const stageRequests = state.requests.filter(request => request.status === getContext().status);
    dom.queueAutomation.textContent = getContext().status === 'closed' ? ''
      : getContext().scope === 'all' ? 'Workflow rules follow each request’s library; open Details to review.'
      : stageRequests.length ? `${({ suggestion: 'Suggestion timeout', outstanding_purchase: 'Auto promotion', pending_hold: 'Pending timeout', hold_placed: 'Pickup timeout' })[getContext().status]}: ${workflowLabel(stageRequests[0])}.`
        : 'Workflow rules will appear with requests in this stage.';
    const definitions = queueColumns();
    const rows = requests.map(request => definitions.map(column => column.value(request)));
    const currentPage = state.grid?.page() ?? 0;
    const page = preservePage ? Math.max(0, Math.min(currentPage, Math.ceil(rows.length / 25) - 1)) : 0;
    if (!state.grid) {
      state.gridStatus = getContext().status;
      state.grid = createQueueGrid({
        columns: definitions.map(column => ({
          name: column.label, width: column.width, sort: column.kind !== 'actions',
          formatter: (cell, row) => formatCell(column, cell, row, owner)
        })),
        data: rows,
        // Columns own their widths. Grid.js 6 sizing headers can replace the live Sort processor during a render.
        autoWidth: false,
        sort: true,
        pagination: { limit: 25, summary: true, resetPageOnUpdate: false },
        language: { noRecordsFound: 'No requests match these filters.' }
      }, dom.grid);
    } else {
      // Refresh retains the current page, clamped to the accepted rows. Filters reset it.
      state.grid.replaceRows(rows, page, state.gridStatus !== getContext().status);
      state.gridStatus = getContext().status;
    }
  }

  root.addEventListener('click', event => {
    if (event.defaultPrevented || event.button > 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey ||
        event.target.closest('a, button, input, select, textarea, summary, [popover], [role="button"], [contenteditable]')) return;
    const row = event.target.closest('tr');
    if (!row || !dom.grid.contains(row)) return;
    const link = row.querySelector('.request-title-link');
    const request = state.requests.find(item => item.id === link?.dataset.requestId);
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed && row.contains(selection.anchorNode)) return;
    if (request && liveRow({ currentTarget: link }, sessionIdentity.preferences(), request)) onOpen(request.id, link);
  }, { signal: events.signal });
  dom.refresh.addEventListener('click', () => { void refresh(); }, { signal: events.signal });
  dom.scope.addEventListener('change', async () => {
    const scope = dom.scope.value;
    dom.scope.value = getContext().scope;
    await onScopeIntent(scope);
    dom.scope.value = getContext().scope;
  }, { signal: events.signal });
  bindStatusTabs(dom.statusTabs, 'status', onStatusIntent, events.signal);
  dom.search.addEventListener('input', renderGrid, { signal: events.signal });
  for (const input of [dom.claim, dom.tag, dom.similar]) input.addEventListener('change', renderGrid, { signal: events.signal });
  return {
    refresh, prepare, invalidate, markStale, render: renderGrid, resetFilters: resetQueueFilters, setStaff, setLibraries,
    isReady,
    find: id => state.requests.find(item => item.id === id), sequence: () => state.sequence,
    libraryScope: scopeForLibraryOrAll,
    quickCreateFocusReturn: id => {
      const request = state.requests.find(item => item.id === id);
      return focusPort({ grid: () => dom.grid,
        label: `New suggestion for patron ${request?.barcode?.trim() || ''} from request ${id}`,
        selector: '.grid-new-suggestion', fallback: dom.refresh, sessionIdentity, getContext, copy: false });
    },
    libraries: () => [...dom.scope.options].filter(option => option.value !== 'all')
      .map(option => Object.freeze({ id: option.value, name: option.textContent })),
    focusReturn: (id, fallback) => focusPort({ grid: () => dom.grid, label: `Open request ${id}`, fallback, sessionIdentity, getContext }),
    activate({ previous }) { if (disposed) return; if (previous !== 'queue') resetQueueFilters(); updateStatusTabs(); },
    deactivate: invalidate,
    refreshOnEntry() { if (!isReady(getContext())) return refresh(); },
    contextChanged(next, previous) {
      if (disposed) return;
      dom.scope.value = next.scope;
      if (next.status !== previous.status) {
        invalidate(); resetQueueFilters(); updateStatusTabs();
        if (next.scope === previous.scope) {
          if (isReady(next)) renderGrid();
          else retireProjection();
        }
      }
      if (next.scope !== previous.scope) { resetQueueFilters(); retireProjection(); }
    },
    preferencesChanged() { if (disposed) return; dom.claim.value = sessionIdentity.preferences()?.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all'; renderGrid(); },
    clear: retireProjection,
    signedOut() { if (disposed) return; invalidate(); state.requests = []; state.loadedContext = null; state.grid?.destroy?.(); state.grid = null; dom.grid = detachGridContainer(dom.grid); },
    dispose() { if (disposed) return; disposed = true; gridAccessibility.disconnect(); events.abort(); invalidate(); state.grid?.destroy?.(); state.grid = null; dom.grid = detachGridContainer(dom.grid); state.requests = []; }
  };
}

export function createCopyQueue({ root, sessionIdentity, getContext, announce,
  onOpen, onScopeIntent, onStatusIntent, onScopeAccepted, onLibraries, onRefreshed, onRendered, recovery,
  resolveScope = () => getContext().scope,
  request = authorizedJson }) {
  const dom = {
    additionalCopyStatusTabs: [...root.querySelectorAll('#additional-copy-status-tabs [data-copy-status]')],
    additionalCopyScopeField: root.querySelector('#additional-copy-scope-field'),
    additionalCopyScope: root.querySelector('#additional-copy-library-scope'),
    additionalCopySearch: root.querySelector('#additional-copy-search'),
    additionalCopyClaim: root.querySelector('#additional-copy-claim-filter'),
    additionalCopyRefresh: root.querySelector('#refresh-additional-copies'),
    additionalCopySummary: root.querySelector('#additional-copy-summary'),
    additionalCopyGrid: root.querySelector('#additional-copy-grid'),
    additionalCopyEmpty: root.querySelector('#additional-copy-empty'),
    additionalCopyCreateReview: root.querySelector('#additional-copy-create-review'),
    additionalCopyCreateReviewSummary: root.querySelector('#additional-copy-create-review-summary'),
    additionalCopyCreateReviewDone: root.querySelector('#additional-copy-create-review-done'),
  };
  const state = { additionalCopies: [], additionalCopyGrid: null, loadedContext: null, stale: true };
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let disposed = false, acceptingScope = false, targetGeneration = 0;

  function invalidate() { targetGeneration++; reads.begin('queue-target').abort(); reads.begin('queue').abort(); if (!disposed) dom.additionalCopyRefresh.disabled = false; }

  async function prepare(context) {
    const owner = sessionIdentity.preferences(), load = reads.begin('queue-target'), ticket = ++targetGeneration;
    const live = () => !disposed && !load.signal.aborted && ticket === targetGeneration && sessionIdentity.isCurrent(owner);
    try {
      const scope = owner.role === 'super_admin' ? context.scope : String(owner.organizationId);
      const result = await request(`/api/asap/staff/additional-copies?scope=${encodeURIComponent(scope)}&status=${encodeURIComponent(context.additionalCopyStatus)}`, { signal: load.signal });
      if (!live()) return null;
      return { scope: result.scope, isCurrent: live, accept: () => acceptResult(result) };
    } finally { reads.finish('queue-target', load.token); }
  }

  function acceptResult(result, evidence = null) {
    if (disposed) return false;
    state.additionalCopies = Object.freeze(Array.isArray(result.items) ? result.items.map(item => Object.freeze({ ...item })) : []);
    state.loadedContext = Object.freeze({ scope: result.scope, status: getContext().additionalCopyStatus });
    state.stale = false;
    if (sessionIdentity.actor()?.role === 'super_admin') { setLibraries(result.availableLibraries); onLibraries(result.availableLibraries); }
    const reviewReady = evidence && recovery.loaded(evidence, { scope: result.scope, status: result.status });
    if (reviewReady) { dom.additionalCopySearch.value = ''; dom.additionalCopyClaim.value = 'all'; }
    renderAdditionalCopyGrid(); onRefreshed({ items: state.additionalCopies, context: state.loadedContext });
    return reviewReady;
  }

  async function refresh(options = {}) {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner) return false;
    const load = reads.begin('queue');
    const wasReviewReady = recovery.current()?.reviewReady;
    const evidence = recovery.begin();
    dom.additionalCopyRefresh.disabled = true;
    if (wasReviewReady) renderAdditionalCopyGrid();
    const clearLoading = !options.silent ? announce('Loading authorized additional-copy tasks...') : null;
    try {
      const resolution = resolveScope(owner);
      const acceptedScope = typeof resolution === 'string' ? resolution : await resolution;
      if (!acceptedScope || disposed || !load.isCurrent() || !sessionIdentity.isCurrent(owner)) return false;
      acceptingScope = true;
      try { onScopeAccepted(acceptedScope); } finally { acceptingScope = false; }
      const context = getContext();
      const scope = owner.role === 'super_admin' ? context.scope : String(owner.organizationId);
      const result = await request(`/api/asap/staff/additional-copies?scope=${encodeURIComponent(scope)}&status=${encodeURIComponent(context.additionalCopyStatus)}`, { signal: load.signal });
      if (disposed || !load.isCurrent() || !sessionIdentity.isCurrent(owner) || context.scope !== getContext().scope || context.additionalCopyStatus !== getContext().additionalCopyStatus) return false;
      acceptingScope = true;
      try { onScopeAccepted(result.scope); } finally { acceptingScope = false; }
      const reviewReady = acceptResult(result, evidence);
      if (!options.silent && reviewReady) announce('Open additional-copy tasks loaded. Review the matching tasks and acknowledge the review before trying again.');
      return true;
    } catch (error) {
      if (!disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) &&
          !options.silent && !isAbortError(error) && error.status !== 401) announce(error.message || 'Additional-copy tasks could not be loaded.', 'error');
      return false;
    } finally {
      if (typeof clearLoading === 'function') clearLoading();
      if (!disposed && load.isCurrent()) dom.additionalCopyRefresh.disabled = false;
      reads.finish('queue', load.token);
    }
  }

  function setLibraries(libraries) { if (!disposed) populateLibraries(dom.additionalCopyScope, libraries, getContext().scope); }
  function setStaff(staff) {
    if (disposed) return;
    dom.additionalCopyScopeField.hidden = staff?.role !== 'super_admin';
    dom.additionalCopyScope.replaceChildren(element('option', { value: getContext().scope, text: staff?.role === 'super_admin' ? 'All libraries' : 'My library' }));
    resetAdditionalCopyFilters(); updateAdditionalCopyStatusTabs(); dom.additionalCopyCreateReview.hidden = true;
  }

  function resetAdditionalCopyFilters() {
    if (disposed) return;
    dom.additionalCopySearch.value = '';
    dom.additionalCopyClaim.value = sessionIdentity.preferences()?.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
  }

  function retireProjection() {
    if (disposed) return;
    state.additionalCopies = [];
    state.loadedContext = null;
    state.stale = true;
    if (!acceptingScope) invalidate();
    state.additionalCopyGrid?.destroy?.();
    // An older Grid.js render can finish after the scope changes; keep its container detached.
    dom.additionalCopyGrid = detachGridContainer(dom.additionalCopyGrid);
    state.additionalCopyGrid = null;
    renderAdditionalCopyGrid();
  }

  function markStale(scope = 'system') {
    if (!disposed && affectsProjection(scope, state.loadedContext?.scope ?? getContext().scope)) retireProjection();
  }

  function updateAdditionalCopyStatusTabs() {
    for (const tab of dom.additionalCopyStatusTabs) {
      const selected = tab.dataset.copyStatus === getContext().additionalCopyStatus;
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
    }
  }

  function filteredAdditionalCopies() {
    const query = dom.additionalCopySearch.value.trim().toLocaleLowerCase();
    const claim = dom.additionalCopyClaim.value;
    return state.additionalCopies.filter(request => {
      const mine = request.claimedByStaffUserId === sessionIdentity.preferences()?.id;
      const unclaimed = !request.claimedByStaffUserId;
      if (claim === 'mine' && !mine) return false;
      if (claim === 'unclaimed' && !unclaimed) return false;
      if (claim === 'mine_unclaimed' && !mine && !unclaimed) return false;
      if (!query) return true;
      return [request.title, request.author, request.bibid, request.identifier,
        request.publication, request.libraryOrgName, request.claimedByDisplayName]
        .filter(Boolean)
        .some(value => String(value).toLocaleLowerCase().includes(query));
    });
  }

  function renderAdditionalCopyGrid() {
    if (disposed) return;
    const owner = sessionIdentity.preferences();
    onRendered();
    const uncertainCreation = recovery.current();
    const reviewReady = !state.stale && Boolean(uncertainCreation?.reviewReady) && !uncertainCreation.reviewed &&
      getContext().additionalCopyStatus === 'open' &&
      (getContext().scope === 'all' || String(getContext().scope) === String(uncertainCreation.libraryOrgId));
    dom.additionalCopyCreateReview.hidden = !reviewReady;
    if (reviewReady) {
      const matching = state.additionalCopies.filter(item =>
        String(item.libraryOrgId) === String(uncertainCreation.libraryOrgId) &&
        item.bibid === uncertainCreation.bibid);
      const ids = matching.map(item => item.id).join(', ');
      dom.additionalCopyCreateReviewSummary.textContent =
        matching.length === 0
          ? `Creation for BIB ${uncertainCreation.bibid} could not be confirmed. No matching open task is visible yet. Review the list before retrying; a retry will use the original request version so a completed earlier creation cannot be duplicated.`
          : `Creation for BIB ${uncertainCreation.bibid} could not be confirmed. Review the ${matching.length} matching open task${matching.length === 1 ? '' : 's'} for this library before creating another: ${ids}.`;
    }
    const requests = filteredAdditionalCopies();
    dom.additionalCopySummary.textContent = state.stale ? 'Additional-copy tasks unavailable. Refresh to load current tasks.'
      : `${requests.length} ${getContext().additionalCopyStatus} task${requests.length === 1 ? '' : 's'}`;
    dom.additionalCopyEmpty.hidden = state.stale || requests.length !== 0;
    if (state.stale) return;
    const rows = requests.map(request => [
      request.title,
      request.author || '—',
      request.bibid,
      request.libraryOrgName,
      request.formatLabel || request.format || 'Not recorded',
      request.createdByUsername || 'Not recorded',
      request.claimedByDisplayName || 'Unclaimed',
      request.status === 'open'
        ? request.timeoutContext ? timeoutLabel(request.timeoutContext.enabled, request.timeoutContext.days) : 'Unavailable'
        : '—',
      request.notes?.trim() ? 'Present' : '—',
      dateTime(request.created),
      request.id
    ]);
    if (!state.additionalCopyGrid) {
      state.additionalCopyGrid = createQueueGrid({
        columns: [
          { name: 'Title', width: '220px' },
          { name: 'Author', width: '155px' },
          { name: 'BIB ID', width: '140px', formatter: (cell, row) => {
            const request = state.additionalCopies.find(item => item.id === row.cells[10].data);
            const url = researchRecordUrl(request?.research, 'bib', request?.bibid);
            return url ? window.gridjs.h('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, String(cell)) : cell;
          } },
          { name: 'Library', width: '145px' },
          { name: 'Format', width: '125px' },
          { name: 'Created by', width: '145px' },
          {
            name: 'Claim',
            width: '140px',
            formatter: (cell, row) => {
              const request = state.additionalCopies.find(item => item.id === row.cells[10].data);
              const mine = request && request.claimedByStaffUserId === sessionIdentity.preferences()?.id;
              return window.gridjs.h('span', { className: `claim-label${mine ? ' mine' : ''}` }, cell);
            }
          },
          { name: 'Task timeout', width: '120px' },
          { name: 'Notes', width: '85px' },
          { name: 'Created', width: '155px' },
          {
            name: 'Open',
            width: '74px',
            sort: false,
            formatter: id => window.gridjs.h('button', {
              type: 'button',
              className: 'grid-open additional-copy-open',
              'aria-label': `Open additional-copy task ${id}`,
              onClick: event => {
                if (sessionIdentity.isCurrent(owner) && !disposed && getContext().activeView === 'additional-copies' && event.currentTarget.isConnected && dom.additionalCopyGrid.contains(event.currentTarget)) {
                  onOpen(String(id), event.currentTarget);
                }
              }
            }, [window.gridjs.h('i', { className: 'fa fa-chevron-right', 'aria-hidden': 'true' }), 'Open'])
          }
        ],
        data: rows,
        // Keep sizing headers from replacing the live Sort processor during a render; every column has a width.
        autoWidth: false,
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No additional-copy tasks match these filters.' }
      }, dom.additionalCopyGrid);
    } else {
      state.additionalCopyGrid.replaceRows(rows);
    }
  }

  dom.additionalCopyRefresh.addEventListener('click', () => { void refresh(); }, { signal: events.signal });
  dom.additionalCopyScope.addEventListener('change', async () => {
    const scope = dom.additionalCopyScope.value;
    dom.additionalCopyScope.value = getContext().scope;
    await onScopeIntent(scope);
    dom.additionalCopyScope.value = getContext().scope;
  }, { signal: events.signal });
  bindStatusTabs(dom.additionalCopyStatusTabs, 'copyStatus', onStatusIntent, events.signal);
  dom.additionalCopySearch.addEventListener('input', renderAdditionalCopyGrid, { signal: events.signal });
  dom.additionalCopyClaim.addEventListener('change', renderAdditionalCopyGrid, { signal: events.signal });
  dom.additionalCopyCreateReviewDone.addEventListener('click', () => {
    if (!recovery.acknowledge()) return;
    renderAdditionalCopyGrid(); dom.additionalCopyRefresh.focus();
    announce('Additional-copy task list reviewed. You can open the request again if another task is needed.', 'success');
  }, { signal: events.signal });
  return {
    refresh, prepare, invalidate, render: renderAdditionalCopyGrid, resetFilters: resetAdditionalCopyFilters, setStaff, setLibraries,
    isReady: context => !state.stale && state.loadedContext?.scope === context.scope && state.loadedContext?.status === context.additionalCopyStatus,
    find: id => state.additionalCopies.find(item => item.id === id),
    markStale,
    focusReturn: (id, fallback) => focusPort({ grid: () => dom.additionalCopyGrid, label: `Open additional-copy task ${id}`, fallback, sessionIdentity, getContext, copy: true }),
    activate({ previous }) { if (disposed) return; if (previous !== 'additional-copies') resetAdditionalCopyFilters(); updateAdditionalCopyStatusTabs(); },
    deactivate: invalidate,
    refreshOnEntry() { if (state.stale || state.loadedContext?.scope !== getContext().scope || state.loadedContext?.status !== getContext().additionalCopyStatus) return refresh(); },
    contextChanged(next, previous) {
      if (disposed) return;
      dom.additionalCopyScope.value = next.scope;
      if (next.additionalCopyStatus !== previous.additionalCopyStatus) { resetAdditionalCopyFilters(); updateAdditionalCopyStatusTabs(); retireProjection(); }
      if (next.scope !== previous.scope) { resetAdditionalCopyFilters(); retireProjection(); }
    },
    preferencesChanged() { if (disposed) return; dom.additionalCopyClaim.value = sessionIdentity.preferences()?.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all'; if (state.loadedContext) renderAdditionalCopyGrid(); },
    clear: retireProjection,
    signedOut() { if (disposed) return; invalidate(); state.additionalCopies = []; state.loadedContext = null; state.additionalCopyGrid?.destroy?.(); state.additionalCopyGrid = null; dom.additionalCopyGrid = detachGridContainer(dom.additionalCopyGrid); dom.additionalCopyCreateReview.hidden = true; },
    dispose() { if (disposed) return; disposed = true; events.abort(); invalidate(); state.additionalCopyGrid?.destroy?.(); state.additionalCopyGrid = null; dom.additionalCopyGrid = detachGridContainer(dom.additionalCopyGrid); state.additionalCopies = []; }
  };
}
