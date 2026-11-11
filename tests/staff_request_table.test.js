const assert = require('node:assert/strict');
const path = require('node:path');
const { fixture } = require('./helpers/staff-controller.cjs');
const pause = () => new Promise(resolve => setTimeout(resolve, 15));
let renderedGrid;
let phase = 'initial';
const isRendered = () => renderedGrid?.config.store.getState().status === 3; // Vendored Grid.js 6.2.0 Rendered state.
async function until(predicate, message) {
  phase = message;
  for (let index = 0; index < 250; index++) {
    if (isRendered() && predicate()) {
      // Grid.js attaches sorting/paging effects after its DOM commit. Let the
      // actual browser-like frame finish before starting another pipeline.
      await new Promise(resolve => window.requestAnimationFrame(() => window.requestAnimationFrame(() => window.setTimeout(resolve, 0))));
      // Grid also debounces pipeline effects (100 ms in the pinned vendor).
      // Drain those effects between distinct fixture actions, just as the
      // real-browser journey waits for its accepted render before continuing.
      await new Promise(resolve => window.setTimeout(resolve, renderedGrid.config.processingThrottleMs + 16));
      if (isRendered() && predicate()) return;
    }
    await pause();
  }
  assert.ok(isRendered() && predicate(), message);
}

(async () => {
  await fixture(async ({ load, dom, get }) => {
    const { requestRowActions, eligibleRowActions } = await load('request-row-actions');
    const actor = { id: '20', role: 'staff', organizationId: 2 };
    const record = { libraryOrgId: 2, claimedByStaffUserId: '20' };
    for (const [status, capabilities, expected] of [
      ['suggestion', { allowedActions: ['purchase', 'reject', 'alreadyOwn', 'silentClose'] }, ['reject', 'alreadyOwn', 'assign', 'silentClose', 'purchase']],
      ['suggestion', {}, ['assign']],
      ['outstanding_purchase', { allowedActions: ['catalogFound'] }, ['catalogFound', 'assign']],
      ['outstanding_purchase', {}, ['assign']],
      ['pending_hold', { canPlaceHold: true, canChangeWorkflowState: true }, ['place-hold', 'assign']],
      ['pending_hold', { canPlaceHold: false, canChangeWorkflowState: true }, ['assign']],
      ['pending_hold', { canPlaceHold: true, canChangeWorkflowState: false }, ['assign']],
      ['hold_placed', { allowedActions: ['close'] }, ['close', 'assign']],
      ['hold_placed', {}, ['assign']],
      ['closed', { allowedActions: ['reopen'] }, ['reopen']],
      ['closed', { allowedActions: [], blockingReason: 'hold_history_retained' }, []]
    ]) {
      const request = { ...record, status, capabilities };
      assert.deepEqual(eligibleRowActions(request, actor).map(a => a.key), expected, status);
      assert.deepEqual(eligibleRowActions(request, { ...actor, organizationId: 3 }), [], 'Other library cannot offer commands');
      assert.deepEqual(eligibleRowActions(request, { ...actor, role: 'retired' }), [], 'Unknown role cannot offer commands');
    }
    for (const autohold of [true, false]) {
      const missingBib = eligibleRowActions({ ...record, status: 'outstanding_purchase', autohold, bibid: null,
        capabilities: { allowedActions: ['catalogFound'] } }, actor)[0];
      assert.equal(missingBib.label, autohold ? 'Ready for hold' : 'Close without hold');
      assert.equal(Boolean(missingBib.disabled), autohold, 'Ready for hold is disabled until the saved request has a BIB.');
      const savedBib = eligibleRowActions({ ...record, status: 'outstanding_purchase', autohold: true, bibid: 9001,
        capabilities: { allowedActions: ['catalogFound'] } }, actor)[0];
      assert.equal(Boolean(savedBib.disabled), false, 'A saved BIB keeps the action capability available.');
    }
    const observers = new Set();
    dom.window.ResizeObserver = class {
      constructor(callback) { this.callback = callback; observers.add(this); }
      observe() {}
      disconnect() { observers.delete(this); }
    };
    let width = 300, current = true;
    const host = document.createElement('div'); get('#queue-view').append(host);
    Object.defineProperty(host, 'clientWidth', { get: () => width });
    host.getBoundingClientRect = () => ({ width });
    const rect = dom.window.HTMLElement.prototype.getBoundingClientRect;
    dom.window.HTMLElement.prototype.getBoundingClientRect = function () {
      return { width: this.classList.contains('request-row-more') ? 36 : 80 };
    };
    const settle = () => new Promise(resolve => dom.window.requestAnimationFrame(() => dom.window.requestAnimationFrame(resolve)));
    const resized = async value => { width = value; for (const observer of observers) observer.callback(); await settle(); };
    const dispatched = [];
    const options = { id: '27', live: () => current, onOpen: (...args) => dispatched.push(args), fallback: get('#refresh-queue') };
    const mount = actions => {
      const ref = requestRowActions({ ...options, actions }); ref(host); return () => ref(null);
    };
    let unmount = mount([]); await settle();
    assert.equal(host.children.length, 0, 'Zero commands have no controls or popover'); unmount();
    unmount = mount([{ key: 'claim', label: 'Claim', glyph: 'user-plus' }]);
    await resized(20);
    assert.equal(host.children.length, 1, 'Even an extremely narrow sole action stays direct');
    host.firstElementChild.click(); assert.deepEqual(dispatched.at(-1)[2], { action: 'claim' }); unmount();
    const actions = [
      { key: 'reject', label: 'Reject', glyph: 'ban', tone: 'danger-button' },
      { key: 'alreadyOwn', label: 'Already own', glyph: 'book' },
      { key: 'assign', label: 'Assign', glyph: 'users' },
      { key: 'purchase', label: 'Purchase', glyph: 'shopping-cart', tone: 'primary-button' }
    ];
    unmount = mount(actions); await resized(400);
    const inline = () => [...host.querySelectorAll(':scope > [data-request-action]')].map(n => n.dataset.requestAction);
    assert.deepEqual(inline(), ['assign', 'reject', 'alreadyOwn']);
    assert.deepEqual([...host.querySelectorAll('[popover] button')].map(n => n.dataset.requestAction), ['purchase'],
      'Purchase stays in the single More menu entry even when the other actions fit directly.');
    let more = host.querySelector('.request-row-more');
    assert.ok(more, 'The More trigger remains available for the Purchase entry.');
    const purchase = host.querySelector('[popover] [data-request-action="purchase"]');
    assert.equal(purchase.textContent, 'Purchase');
    assert.equal(purchase.querySelector('i')?.classList.contains('fa-shopping-cart'), true);
    assert.equal(host.querySelectorAll('[data-request-action]').length, 4, 'Each command exists exactly once');
    const prior = dispatched.length;
    purchase.click();
    assert.equal(dispatched.length, prior + 1); assert.deepEqual(dispatched.at(-1)[2], { action: 'purchase' });
    assert.equal(dispatched.at(-1)[1], more, 'More-menu Purchase opens the scoped request workflow from its connected trigger.');
    await resized(160);
    assert.deepEqual(inline(), ['assign']);
    assert.deepEqual([...host.querySelectorAll('[popover] button')].map(n => n.dataset.requestAction), ['purchase', 'reject', 'alreadyOwn']);
    more = host.querySelector('.request-row-more');
    const reject = host.querySelector('[popover] [data-request-action="reject"]');
    reject.click();
    assert.equal(dispatched.length, prior + 2);
    assert.deepEqual(dispatched.at(-1)[2], { action: 'reject' });
    assert.equal(dispatched.at(-1)[1], more, 'Other overflow actions retain the same More trigger.');
    host.querySelector('[data-request-action="alreadyOwn"]').focus();
    await resized(400);
    assert.equal(document.activeElement.dataset.requestAction, 'alreadyOwn', 'Promoted command retains focus');
    assert.ok(host.querySelector('[popover] [data-request-action="purchase"]'), 'Responsive reflow keeps Purchase in More.');
    await resized(120); assert.deepEqual(inline(), ['assign']);
    current = false;
    host.querySelector('[data-request-action]').click(); assert.equal(dispatched.length, prior + 2, 'Revoked context cannot dispatch');
    const retired = host.firstElementChild; unmount(); retired.click();
    assert.equal(dispatched.length, prior + 2, 'Retired controls have no handlers');
    assert.equal(observers.size, 0, 'Unmount retires every observer');
    dom.window.HTMLElement.prototype.getBoundingClientRect = rect;
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const { createTitleQueue } = await load('queues');
    const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
    const session = createSessionIdentity(); session.accept(staff);
    // Preact detects the browser frame API on the global object. Expose the
    // existing visual JSDOM API before loading Grid, as the browser does.
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    dom.window.gridjs = require(path.join(__dirname, '../src/Asap.Web/Frontend/vendor/gridjs/6.2.0/gridjs.umd.js'));
    const Grid = dom.window.gridjs.Grid;
    dom.window.gridjs = { ...dom.window.gridjs, Grid: class extends Grid {
      constructor(configuration) { super(configuration); renderedGrid = this; }
    } };
    const opened = [], created = [], searches = [], closed = [];
    let context = { activeView: 'queue', scope: '2', status: 'suggestion' };
    let data = Array.from({ length: 55 }, (_, index) => ({
      id: String(9007199254740993n + BigInt(index)), title: `Title ${String(index).padStart(2, '0')}`, author: `Author ${String(index).padStart(2, '0')}`,
      barcode: `2000000000${String(index).padStart(4, '0')}`, nameFirst: 'Alex', nameLast: `Reader ${index}`,
      libraryOrgId: 2, libraryOrgName: 'Library two', format: 'book', formatLabel: 'Book', status: 'suggestion',
      created: `2026-10-${String(index % 20 + 1).padStart(2, '0')}T13:14:15Z`,
      claimedByStaffUserId: index % 2 ? '20' : null, claimedByDisplayName: index % 2 ? 'Current staff' : null,
      workflowTags: index === 0 ? ['Visible warning'] : [], identifier: `IDENTIFIER-${index}`,
      capabilities: { allowedActions: ['purchase', 'reject', 'alreadyOwn', 'silentClose'] },
      research: { patronId: index + 1, leapPatronUrlPattern: 'https://catalog.example.org/patron/{{patron-id}}' }
    })).reverse();
    const queue = createTitleQueue({ root: get('#queue-view'), sessionIdentity: session, claimsFilter: createClaimsFilter({ sessionIdentity: session }), getContext: () => context,
      announce() {}, onOpen: (...args) => opened.push(args), onNewSuggestion: (...args) => created.push(args),
      polarisLookup: { open: value => searches.push(value), close: value => closed.push(value) },
      onScopeIntent() {}, onStatusIntent() {}, onScopeAccepted() {}, onLibraries() {}, onRefreshed() {}, onRendered() {},
      request: async () => ({ items: data.map(item => ({ ...item })), scope: context.scope, organizations: [] }) });
    const renderErrors = [], logError = console.error;
    let completed = false;
    console.error = (...args) => {
      if (args.some(value => String(value).includes('[Grid.js] [ERROR]'))) renderErrors.push(`${phase}: ${args.map(String).join(' ')}`);
      logError(...args);
    };
    try {
    queue.setStaff(session.preferences()); await queue.refresh();
    const rows = () => [...get('#request-grid').querySelectorAll('tbody tr')];
    const first = () => rows()[0];
    await until(() => first()?.querySelector('.request-title-link'), 'Real Grid.js renders request title links.');
    assert.deepEqual([...get('#request-grid').querySelectorAll('th')].map(node => node.querySelector('.gridjs-th-content').textContent),
      ['Barcode', 'Bibliographic details', 'Format', 'Submitted', 'Library', 'Claim', 'Status', 'Actions']);
    const headings = [...get('#request-grid').querySelectorAll('th')];
    const assertSort = (header, direction, next) => {
      const name = header.querySelector('.gridjs-th-content').textContent;
      assert.equal(header.getAttribute('aria-sort'), direction, name);
      assert.equal(header.querySelector('button').getAttribute('aria-label'), `Sort ${name} ${next}`, name);
    };
    for (const header of headings.slice(0, 7)) {
      assertSort(header, 'none', 'ascending');
      header.focus();
      for (const [direction, next, shiftKey] of [
        ['ascending', 'descending', false], ['descending', 'ascending', false], ['none', 'ascending', true]
      ]) {
        header.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Enter', which: 13, keyCode: 13, shiftKey, bubbles: true }));
        await until(() => header.getAttribute('aria-sort') === direction, `Keyboard sort exposes ${direction}`);
        assertSort(header, direction, next);
        assert.equal(document.activeElement, header, 'Sorting retains its header focus');
      }
    }
    assert.equal(headings[7].hasAttribute('aria-sort'), false, 'Actions is unsortable');
    assert.equal(headings[7].querySelector('button'), null);
    assert.equal(first().cells.length, 8); assert.equal(rows().length, 25);
    assert.match(get('#queue-summary').textContent, /55 suggestion requests/);
    const assertCounts = () => assert.deepEqual([...get('#status-tabs').querySelectorAll('[data-status]')].map(tab => tab.getAttribute('aria-label')),
      [`Suggestions (${data.length})`, 'Purchases (0)', 'Pending holds (0)', 'Placed holds (0)', 'Closed (0)']);
    assertCounts();
    assert.match(first().cells[0].textContent, /Alex Reader 54/);
    const link = first().querySelector('.request-title-link');
    assert.equal(link.tagName, 'A'); assert.match(link.href, /request=9007199254741047/);
    assert.match(link.getAttribute('aria-label'), /Title 54/);
    first().cells[2].dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true }));
    assert.equal(opened.length, 1); assert.equal(opened[0][0], link.dataset.requestId);
    link.click(); assert.equal(opened.length, 2);
    first().querySelector('[aria-label^="Search Polaris for title:"]').click();
    first().querySelector('[aria-label^="Search Polaris for author:"]').click();
    assert.deepEqual(searches.map(item => [item.mode, item.query, item.canApply, item.readOnly]),
      [['title', 'Title 54', false, true], ['author', 'Author 54', false, true]]);
    first().querySelector('.grid-new-suggestion').click(); assert.equal(created.length, 1); assert.equal(opened.length, 2);
    first().querySelector('[aria-label^="Purchase request"]').click();
    assert.equal(opened.length, 3); assert.deepEqual(opened[2][2], { action: 'purchase' });
    get('#request-grid th:nth-child(2) button').click();
    await until(() => first()?.querySelector('.request-title-link')?.textContent === 'Title 00', 'Bibliographic details sort by Title.');
    const next = get('#request-grid button[aria-label="Next"]'); next.click();
    await until(() => first()?.querySelector('.request-title-link')?.textContent === 'Title 25', 'Pagination reaches the second sorted page.');
    assertCounts();
    const beforeRefresh = first().querySelector('.request-title-link');
    data = data.slice(1); // A genuinely new 54-row response, not Grid's retained old store.
    await queue.refresh();
    await until(() => first()?.querySelector('.request-title-link') && first().querySelector('.request-title-link') !== beforeRefresh &&
      get('#request-grid .gridjs-summary')?.textContent.includes('of 54'),
      'Refresh must render its new accepted rows before retention is assessed.');
    await until(() => first()?.querySelector('.request-title-link')?.textContent === 'Title 25', 'Refresh preserves sort and the current page.');
    assertSort(get('#request-grid th:nth-child(2)'), 'ascending', 'descending');
    const full = data;
    data = data.slice(-10);
    await queue.refresh();
    await until(() => rows().length === 10 && get('#request-grid .gridjs-currentPage')?.textContent === '1', 'A shrinking accepted result clamps the saved page.');
    assertCounts();
    data = full;
    await queue.refresh();
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'The full accepted result renders before filtering.');
    get('#request-search').value = 'Author 03'; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 1 && first().textContent.includes('Title 03'), 'Author search resets paging and finds its request.');
    assert.match(get('#queue-summary').textContent, /^1 suggestion request$/);
    assertCounts();
    get('#request-search').value = 'IDENTIFIER-4'; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 11 && get('#request-grid .gridjs-summary')?.textContent.includes('of 11'), 'Identifier search remains supported.');
    get('#request-search').value = ''; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'Clearing search renders its accepted rows.');
    get('#claim-filter').value = 'mine'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 27'), 'Mine filtering retains exact staff identity.');
    assertCounts();
    get('#claim-filter').value = 'all'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'Clearing claim renders before another filter action.');
    get('#similar-filter').value = 'unique'; get('#similar-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => get('#request-grid .gridjs-notfound'), 'Similarity can hide all rows without changing scope totals.');
    assertCounts();
    get('#similar-filter').value = 'all'; get('#similar-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 25, 'Clearing similarity restores the accepted records.');
    get('#tag-filter').value = 'Visible warning'; get('#tag-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 1 && first().textContent.includes('Visible warning'), 'Warnings remain visible and filterable without a Tags column.');
    assertCounts();
    const staleButton = first().querySelector('.request-search-shortcut'); staleButton.click();
    const staleSearch = searches.at(-1);
    context = { ...context, scope: '3' }; queue.contextChanged(context, { ...context, scope: '2' });
    assert.equal(staleSearch.isCurrent(), false); assert.ok(closed.includes(staleSearch));
    const before = searches.length; staleButton.click(); assert.equal(searches.length, before, 'Detached rows cannot launch another catalog lookup.');
    data = [{ ...data[0], id: '7', title: 'New authorized projection', libraryOrgId: 3 }];
    await queue.refresh(); await until(() => get('#request-grid').textContent.includes('New authorized projection'), 'New scope renders after retirement.');
    const wrapper = get('#request-grid .gridjs-wrapper');
    assert.equal(wrapper.tabIndex, 0); assert.equal(wrapper.getAttribute('role'), 'region');
    const stableCommand = first().querySelector('[data-request-action]'); stableCommand.focus();
    get('#request-grid th:nth-child(2) button').click();
    await until(() => first()?.querySelector('.request-title-link')?.textContent === 'New authorized projection', 'An unchanged single row settles its Grid.js sort effects');
    assert.equal(stableCommand.isConnected, false, 'Grid.js retires the prior cell when its sort pipeline replaces rows');
    assert.equal(document.activeElement, get('#refresh-queue'), 'Retired cell focus returns to its connected queue control');
    const retiredOpens = opened.length; stableCommand.click();
    assert.equal(opened.length, retiredOpens, 'Retired sort controls cannot dispatch');
    const previousActorButton = first().querySelector('[data-request-action]'), previousOpens = opened.length;
    const libraryStaff = { ...staff, role: 'staff', organizationId: 3 };
    session.accept(libraryStaff); previousActorButton.click();
    assert.equal(opened.length, previousOpens, 'A role/library identity replacement revokes existing controls immediately');
    queue.clear(); queue.setStaff(session.preferences()); await queue.refresh();
    await until(() => first()?.querySelector('[data-request-action]'), 'Authorized library staff receive their refreshed server capabilities');
    for (const [status, claim, capabilities, expected] of [
      ['closed', null, { allowedActions: ['reopen'] }, ['reopen']],
      ['closed', null, { allowedActions: [], blockingReason: 'hold_history_retained' }, []],
      ['outstanding_purchase', '20', { allowedActions: ['catalogFound'] }, ['catalogFound', 'assign']],
      ['pending_hold', '20', { canPlaceHold: true, canChangeWorkflowState: true }, ['place-hold', 'assign']],
      ['pending_hold', '20', { canPlaceHold: false }, ['assign']],
      ['hold_placed', '99', { allowedActions: ['close'] }, ['close', 'assign']],
      ['suggestion', null, { allowedActions: ['reject'] }, ['reject', 'claim', 'assign']]
    ]) {
      const previous = context; context = { ...context, status }; queue.contextChanged(context, previous);
      data = [{ ...data[0], status, claimedByStaffUserId: claim, capabilities }];
      await queue.refresh();
      await until(() => first()?.querySelectorAll('[data-request-action]').length === expected.length, 'Accepted status/capability projection replaces its commands');
      assert.deepEqual([...first().querySelectorAll('[data-request-action]')].map(n => n.dataset.requestAction), expected);
      assert.equal(first().querySelector('[data-request-action="edit"]'), null, 'Details remain a row/title path');
      if (!expected.length) assert.equal(first().querySelector('.request-row-more, [popover]'), null);
    }
    const revoked = first().querySelector('[data-request-action]'), opensBeforeSignout = opened.length;
    session.clear(); queue.signedOut(); revoked.click();
    assert.equal(opened.length, opensBeforeSignout, 'Signed-out rows cannot dispatch');
    phase = 'dispose'; queue.dispose();
    assert.equal(staleSearch.isCurrent(), false);
    completed = true;
    } finally {
      queue.dispose(); await pause(); console.error = logError;
      if (completed) assert.deepEqual(renderErrors, [], 'Grid must complete without render errors.');
    }
  });
  console.log('Eight-column real Grid.js table, independent actions/searches, sorting, paging, filters and stale ownership passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
