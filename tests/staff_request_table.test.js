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
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
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
    const queue = createTitleQueue({ root: get('#queue-view'), sessionIdentity: session, getContext: () => context,
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
    queue.setStaff(staff); await queue.refresh();
    const rows = () => [...get('#request-grid').querySelectorAll('tbody tr')];
    const first = () => rows()[0];
    await until(() => first()?.querySelector('.request-title-link'), 'Real Grid.js renders request title links.');
    assert.deepEqual([...get('#request-grid').querySelectorAll('th')].map(node => node.querySelector('.gridjs-th-content').textContent),
      ['Barcode', 'Bibliographic details', 'Format', 'Submitted', 'Library', 'Claim', 'Status', 'Actions']);
    assert.equal(first().cells.length, 8); assert.equal(rows().length, 25);
    assert.match(get('#queue-summary').textContent, /55 suggestion requests/);
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
    const beforeRefresh = first().querySelector('.request-title-link');
    data = data.slice(1); // A genuinely new 54-row response, not Grid's retained old store.
    await queue.refresh();
    await until(() => first()?.querySelector('.request-title-link') && first().querySelector('.request-title-link') !== beforeRefresh &&
      get('#request-grid .gridjs-summary')?.textContent.includes('of 54'),
      'Refresh must render its new accepted rows before retention is assessed.');
    await until(() => first()?.querySelector('.request-title-link')?.textContent === 'Title 25', 'Refresh preserves sort and the current page.');
    const full = data;
    data = data.slice(-10);
    await queue.refresh();
    await until(() => rows().length === 10 && get('#request-grid .gridjs-currentPage')?.textContent === '1', 'A shrinking accepted result clamps the saved page.');
    data = full;
    await queue.refresh();
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'The full accepted result renders before filtering.');
    get('#request-search').value = 'Author 03'; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 1 && first().textContent.includes('Title 03'), 'Author search resets paging and finds its request.');
    assert.match(get('#queue-summary').textContent, /^1 suggestion request$/);
    get('#request-search').value = 'IDENTIFIER-4'; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 11 && get('#request-grid .gridjs-summary')?.textContent.includes('of 11'), 'Identifier search remains supported.');
    get('#request-search').value = ''; get('#request-search').dispatchEvent(new dom.window.Event('input'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'Clearing search renders its accepted rows.');
    get('#claim-filter').value = 'mine'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 27'), 'Mine filtering retains exact staff identity.');
    get('#claim-filter').value = 'all'; get('#claim-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 25 && get('#request-grid .gridjs-summary')?.textContent.includes('of 54'), 'Clearing claim renders before another filter action.');
    get('#tag-filter').value = 'Visible warning'; get('#tag-filter').dispatchEvent(new dom.window.Event('change'));
    await until(() => rows().length === 1 && first().textContent.includes('Visible warning'), 'Warnings remain visible and filterable without a Tags column.');
    const staleButton = first().querySelector('.request-search-shortcut'); staleButton.click();
    const staleSearch = searches.at(-1);
    context = { ...context, scope: '3' }; queue.contextChanged(context, { ...context, scope: '2' });
    assert.equal(staleSearch.isCurrent(), false); assert.ok(closed.includes(staleSearch));
    const before = searches.length; staleButton.click(); assert.equal(searches.length, before, 'Detached rows cannot launch another catalog lookup.');
    data = [{ ...data[0], id: '7', title: 'New authorized projection', libraryOrgId: 3 }];
    await queue.refresh(); await until(() => get('#request-grid').textContent.includes('New authorized projection'), 'New scope renders after retirement.');
    const wrapper = get('#request-grid .gridjs-wrapper');
    assert.equal(wrapper.tabIndex, 0); assert.equal(wrapper.getAttribute('role'), 'region');
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
