import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, dateTime, statusLabel, closeReasonLabel, timeoutLabel } from './ui.js';

function detachGridContainer(container) {
  const replacement = container.cloneNode(false);
  container.replaceWith(replacement);
  return replacement;
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
  for (const organization of organizations || []) {
    select.append(element('option', { value: organization.id, text: organization.name }));
  }
  select.value = scope;
}

function affectsProjection(scope, loadedScope) {
  return scope === 'system' || String(scope) === '1' || loadedScope === 'all' || String(scope) === loadedScope;
}

function focusPort({ grid, label, fallback, sessionIdentity, getContext, copy }) {
  const owner = sessionIdentity.preferences();
  const context = getContext();
  return { root: grid, fallback,
    target: () => [...grid().querySelectorAll('.grid-open')].find(button => button.getAttribute('aria-label') === label),
    isCurrent: () => sessionIdentity.isCurrent(owner) && getContext().activeView === context.activeView &&
      getContext().scope === context.scope && (copy ? getContext().additionalCopyStatus === context.additionalCopyStatus : getContext().status === context.status)
  };
}

export function createTitleQueue({ root, sessionIdentity, getContext, announce,
  onOpen, onScopeIntent, onStatusIntent, onScopeAccepted, onLibraries, onRefreshed, onRendered,
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
  const state = { requests: [], grid: null, gridStatus: null, loadedContext: null, stale: true, sequence: 0 };
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let disposed = false, acceptingScope = false, targetGeneration = 0;

  function invalidate() { targetGeneration++; reads.begin('queue-target').abort(); reads.begin('queue').abort(); if (!disposed) dom.refresh.disabled = false; }

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
    if (sessionIdentity.actor()?.role === 'super_admin') { setLibraries(result.organizations); onLibraries(result.organizations); }
    populateTags(); renderGrid();
    onRefreshed({ sequence, items: state.requests, context: state.loadedContext });
  }

  async function refresh(options = {}) {
    const owner = sessionIdentity.preferences();
    if (disposed || !owner) return false;
    const load = reads.begin('queue');
    const sequence = ++state.sequence;
    dom.refresh.disabled = true;
    if (!options.silent) announce('Loading authorized requests...');
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
      if (!options.silent) announce(`${state.requests.length} authorized requests loaded.`);
      return true;
    } catch (error) {
      if (!disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) &&
          !options.silent && !isAbortError(error) && error.status !== 401) announce(error.message || 'Requests could not be loaded.', 'error');
      return false;
    } finally {
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
    const field = (label, value, width = '130px') => ({ label, value, width });
    const title = field('Title', request => request.title, '220px');
    const bib = field('BIB ID', request => request.bibid || '—');
    const format = field('Format', request => request.formatLabel || request.format || '—');
    const library = field('Library', request => request.libraryOrgName, '150px');
    const patron = field('Patron', request => [request.nameLast, request.nameFirst].filter(Boolean).join(', ') || request.barcode, '150px');
    const claim = field('Claim', request => request.claimedByDisplayName || 'Unclaimed', '145px');
    claim.kind = 'claim';
    const notes = field('Notes', request => request.notes?.trim() ? 'Present' : '—', '85px');
    const tags = field('Tags', request => request.workflowTags?.join(', ') || 'None', '145px');
    const open = field('Open', request => request.id, '85px');
    open.kind = 'open';
    const automation = label => field(label, workflowLabel, '145px');
    switch (getContext().status) {
      case 'suggestion':
        return [title, field('Identifier', request => request.identifier || '—'), format,
          field('Submitted', request => dateTime(request.created), '155px'), patron, library,
          field('Related', request => request.relatedRequests?.count ?? '—', '90px'),
          automation('Suggestion timeout'), tags, claim, notes, open];
      case 'outstanding_purchase':
        return [title, field('Identifier', request => request.identifier || '—'), bib, format,
          field('Submitted', request => dateTime(request.created), '155px'),
          field('Stage entered', request => dateTime(request.phaseEnteredAt), '155px'),
          automation('Auto promotion'), library, claim, notes, open];
      case 'pending_hold':
        return [title, bib, format, field('Stage entered', request => dateTime(request.phaseEnteredAt), '155px'),
          automation('Pending timeout'), patron, library, tags, claim, notes, open];
      case 'hold_placed':
        return [title, bib, format, field('Stage entered', request => dateTime(request.phaseEnteredAt), '155px'),
          automation('Pickup timeout'), patron, library, claim, notes, open];
      default:
        return [title, bib, format, field('Close reason', request => closeReasonLabel(request.closeReason), '150px'),
          field('Updated', request => dateTime(request.updated), '155px'), patron, library, claim, notes, open];
    }
  }

  function renderGrid() {
    if (disposed) return;
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
      : getContext().scope === 'all' ? 'Workflow rules are shown for each request’s library.'
      : stageRequests.length ? `${queueColumns().find(column => column.label.includes('timeout') || column.label === 'Auto promotion')?.label || 'Automation'}: ${workflowLabel(stageRequests[0])}.`
        : 'Workflow rules will appear with requests in this stage.';
    const definitions = queueColumns();
    const rows = requests.map(request => definitions.map(column => column.value(request)));
    if (state.grid && state.gridStatus !== getContext().status) {
      state.grid.destroy?.();
      dom.grid = detachGridContainer(dom.grid);
      state.grid = null;
    }
    if (!state.grid) {
      state.gridStatus = getContext().status;
      state.grid = new window.gridjs.Grid({
        columns: definitions.map((column, index) => {
          if (column.kind === 'claim') return {
            name: column.label, width: column.width,
            formatter: (cell, row) => {
              const request = state.requests.find(item => item.id === row.cells.at(-1).data);
              const mine = request && request.claimedByStaffUserId === sessionIdentity.preferences()?.id;
              return window.gridjs.h('span', { className: `claim-label${mine ? ' mine' : ''}` }, cell);
            }
          };
          if (column.kind === 'open') return {
            name: column.label, width: column.width, sort: false,
            formatter: id => window.gridjs.h('button', {
              type: 'button', className: 'grid-open', 'aria-label': `Open request ${id}`,
              onClick: event => {
                if (sessionIdentity.isCurrent(owner) && !disposed && getContext().activeView === 'queue' && event.currentTarget.isConnected && dom.grid.contains(event.currentTarget)) {
                  onOpen(String(id), event.currentTarget);
                }
              }
            }, [window.gridjs.h('i', { className: 'fa fa-chevron-right', 'aria-hidden': 'true' }), 'Open'])
          };
          return { name: column.label, width: column.width };
        }),
        data: rows,
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No requests match these filters.' }
      });
      state.grid.render(dom.grid);
    } else {
      state.grid.updateConfig({ data: rows }).forceRender();
    }
  }

  dom.refresh.addEventListener('click', () => { void refresh(); }, { signal: events.signal });
  dom.scope.addEventListener('change', () => {
    if (!onScopeIntent(dom.scope.value)) dom.scope.value = getContext().scope;
  }, { signal: events.signal });
  bindStatusTabs(dom.statusTabs, 'status', onStatusIntent, events.signal);
  dom.search.addEventListener('input', renderGrid, { signal: events.signal });
  for (const input of [dom.claim, dom.tag, dom.similar]) input.addEventListener('change', renderGrid, { signal: events.signal });
  return {
    refresh, prepare, invalidate, markStale, render: renderGrid, resetFilters: resetQueueFilters, setStaff, setLibraries,
    isReady: context => !state.stale && state.loadedContext?.scope === context.scope && state.loadedContext?.status === context.status,
    find: id => state.requests.find(item => item.id === id), sequence: () => state.sequence,
    libraryScope: scopeForLibraryOrAll,
    libraries: () => [...dom.scope.options].filter(option => option.value !== 'all')
      .map(option => Object.freeze({ id: option.value, name: option.textContent })),
    focusReturn: (id, fallback) => focusPort({ grid: () => dom.grid, label: `Open request ${id}`, fallback, sessionIdentity, getContext }),
    activate({ previous }) { if (disposed) return; if (previous !== 'queue') resetQueueFilters(); updateStatusTabs(); },
    deactivate: invalidate,
    refreshOnEntry() { if (state.stale || state.loadedContext?.scope !== getContext().scope) return refresh(); },
    contextChanged(next, previous) {
      if (disposed) return;
      dom.scope.value = next.scope;
      if (next.status !== previous.status) {
        invalidate(); resetQueueFilters(); updateStatusTabs();
        if (next.scope === previous.scope) renderGrid();
      }
      if (next.scope !== previous.scope) { resetQueueFilters(); retireProjection(); }
    },
    preferencesChanged() { if (disposed) return; dom.claim.value = sessionIdentity.preferences()?.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all'; renderGrid(); },
    clear: retireProjection,
    signedOut() { if (disposed) return; invalidate(); state.requests = []; state.loadedContext = null; state.grid?.destroy?.(); state.grid = null; dom.grid = detachGridContainer(dom.grid); },
    dispose() { if (disposed) return; disposed = true; events.abort(); invalidate(); state.grid?.destroy?.(); state.grid = null; dom.grid = detachGridContainer(dom.grid); state.requests = []; }
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
    if (!options.silent) announce('Loading authorized additional-copy tasks...');
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
      if (!options.silent) announce(reviewReady ? 'Open additional-copy tasks loaded. Review the matching tasks and acknowledge the review before trying again.'
        : `${state.additionalCopies.length} authorized additional-copy tasks loaded.`);
      return true;
    } catch (error) {
      if (!disposed && load.isCurrent() && sessionIdentity.isCurrent(owner) &&
          !options.silent && !isAbortError(error) && error.status !== 401) announce(error.message || 'Additional-copy tasks could not be loaded.', 'error');
      return false;
    } finally {
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
      state.additionalCopyGrid = new window.gridjs.Grid({
        columns: [
          { name: 'Title', width: '220px' },
          { name: 'Author', width: '155px' },
          { name: 'BIB ID', width: '140px' },
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
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No additional-copy tasks match these filters.' }
      });
      state.additionalCopyGrid.render(dom.additionalCopyGrid);
    } else {
      state.additionalCopyGrid.updateConfig({ data: rows }).forceRender();
    }
  }

  dom.additionalCopyRefresh.addEventListener('click', () => { void refresh(); }, { signal: events.signal });
  dom.additionalCopyScope.addEventListener('change', () => {
    if (!onScopeIntent(dom.additionalCopyScope.value)) dom.additionalCopyScope.value = getContext().scope;
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
