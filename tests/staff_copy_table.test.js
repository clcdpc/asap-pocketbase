const assert = require('node:assert/strict');
const path = require('node:path');
const { fixture } = require('./helpers/staff-controller.cjs');

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createCopyQueue } = await load('queues');
    const session = createSessionIdentity();
    const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
    session.accept(staff);
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    dom.window.gridjs = require(path.join(__dirname, '../src/Asap.Web/Frontend/vendor/gridjs/6.2.0/gridjs.umd.js'));
    let rendered;
    const Grid = dom.window.gridjs.Grid;
    dom.window.gridjs = { ...dom.window.gridjs, Grid: class extends Grid {
      constructor(options) { super(options); rendered = this; }
    } };
    async function until(predicate, message) {
      for (let i = 0; i < 250; i++) {
        if (rendered?.config.store.getState().status === 3 && predicate()) {
          await new Promise(resolve => window.setTimeout(resolve, rendered.config.processingThrottleMs + 40));
          if (predicate()) return;
        }
        await new Promise(resolve => setTimeout(resolve, 15));
      }
      assert.ok(predicate(), message);
    }
    let context = { activeView: 'additional-copies', scope: '2', additionalCopyStatus: 'open' };
    let data = Array.from({ length: 55 }, (_, index) => ({
      id: String(9007199254740993n + BigInt(index)), title: `Task ${String(index).padStart(2, '0')}`,
      author: `Writer ${54 - index}`, bibid: index + 9, identifier: `ISBN-${index}`, publication: `Publication ${index}`,
      libraryOrgId: 2, libraryOrgName: index % 2 ? 'Z library' : 'A library', formatLabel: 'Book',
      status: 'open', createdByUsername: `Creator ${index}`, notes: index % 2 ? '  ' : 'Private note contents',
      claimedByStaffUserId: index % 2 ? '20' : null, claimedByDisplayName: index % 2 ? 'Current staff' : null,
      created: `2026-10-${String(index % 20 + 1).padStart(2, '0')}T13:14:15Z`,
      timeoutContext: index === 1 ? null : { enabled: index !== 0, days: 14 },
      research: { leapBibUrlPattern: 'https://catalog.example.org/bib/{{bibid}}' }
    })).reverse();
    const opened = [], urls = [];
    const queue = createCopyQueue({ root: get('#additional-copy-view'), sessionIdentity: session, getContext: () => context,
      announce() {}, onOpen: (...args) => opened.push(args), onScopeIntent() {}, onStatusIntent() {},
      onScopeAccepted() {}, onLibraries() {}, onRefreshed() {}, onRendered() {},
      recovery: { current: () => null, begin: () => null, loaded: () => false },
      request: async url => { urls.push(url); return { items: data.map(item => ({ ...item })), scope: context.scope, availableLibraries: [] }; } });
    const rows = () => [...get('#additional-copy-grid').querySelectorAll('tbody tr')];
    const first = () => rows()[0];
    const button = () => first()?.querySelector('.additional-copy-open');
    const cell = kind => first()?.querySelector(`td[data-copy-column="${kind}"]`);
    const click = (node, options = {}) => node.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, cancelable: true, ...options }));
    const filter = async value => {
      const previous = button();
      get('#additional-copy-search').value = value;
      get('#additional-copy-search').dispatchEvent(new dom.window.Event('input'));
      await until(() => button() !== previous, 'Search replaces the accepted rows');
    };
    try {
      queue.setStaff(staff); await queue.refresh();
      await until(() => button(), 'Real Grid.js renders copy tasks');
      assert.equal(rows().length, 25);
      assert.deepEqual([...get('#additional-copy-grid').querySelectorAll('th')].map(n => n.querySelector('.gridjs-th-content').textContent),
        ['Title / Author', 'BIB / Format', 'Library', 'Staff', 'Timing', 'Action']);
      assert.equal(first().cells.length, 6);
      assert.equal(get('#additional-copy-grid').querySelectorAll('th button').length, 5);
      assert.equal(get('#additional-copy-grid').querySelectorAll('img, svg, picture').length, 0);
      assert.match(cell('title').textContent, /Task 54Writer 0Notes/);
      assert.match(cell('bib').textContent, /63Book/);
      assert.match(cell('staff').textContent, /UnclaimedCreated by: Creator 54/);
      assert.match(cell('timing').textContent, /Task timeout: 14 days/);
      assert.doesNotMatch(first().textContent, /Private note contents/);
      assert.equal(button().textContent, 'Open details');
      click(cell('title').querySelector('.additional-copy-title'));
      assert.equal(opened.at(-1)[0], '9007199254741047');
      assert.equal(opened.at(-1)[1], button(), 'Row activation uses the existing focus-return control');
      let count = opened.length;
      button().click(); assert.equal(opened.length, count + 1, 'Button and row delegation open exactly once');
      const bib = cell('bib').querySelector('a');
      assert.equal(bib.href, 'https://catalog.example.org/bib/63');
      assert.equal(bib.target, '_blank'); assert.equal(bib.rel, 'noopener noreferrer');
      bib.addEventListener('click', event => event.preventDefault());
      count = opened.length; click(bib); assert.equal(opened.length, count);
      for (const options of [{ ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { altKey: true }, { button: 1 }, { button: 2 }]) {
        click(cell('title'), options);
      }
      const prevented = event => event.preventDefault();
      cell('title').addEventListener('click', prevented); click(cell('title')); cell('title').removeEventListener('click', prevented);
      const range = document.createRange(); range.selectNodeContents(cell('title'));
      window.getSelection().addRange(range); click(cell('library')); // Selection in a different cell of the row.
      window.getSelection().removeAllRanges();
      for (const markup of ['a', 'button', 'input', 'select', 'textarea', 'summary', 'label', 'span']) {
        const interactive = document.createElement(markup);
        if (markup === 'span') interactive.setAttribute('role', 'button');
        const child = document.createElement('i'); interactive.append(child); cell('library').append(interactive);
        click(child); interactive.remove();
      }
      assert.equal(opened.length, count, 'Modified/prevented clicks, selection and interactive descendants cannot activate a row');
      for (const [index, field] of [[0, 'title'], [1, 'bibid'], [2, 'libraryOrgName'], [3, 'claimedByDisplayName'], [4, 'created']]) {
        count = opened.length;
        get(`#additional-copy-grid th:nth-child(${index + 1}) button`).click();
        await until(() => rendered.config.store.getState().sort.columns.some(c => c.index === index), 'Primary sort accepted');
        assert.equal(opened.length, count, 'Sort headers never activate a task');
        const values = rows().map(row => data.find(t => t.id === row.querySelector('button').dataset.copyId)[field] || 'Unclaimed');
        const expected = [...values].sort((a, b) => field === 'bibid' ? a - b : String(a).localeCompare(String(b)));
        assert.deepEqual(values, expected, `${field} sorts by its underlying primary field`);
        click(cell('library')); assert.equal(opened.at(-1)[0], button().dataset.copyId);
      }
      count = opened.length; get('#additional-copy-grid button[aria-label="Next"]').click();
      await until(() => get('#additional-copy-grid .gridjs-currentPage')?.textContent === '2', 'Second page');
      assert.equal(opened.length, count, 'Pagination never activates a task');
      click(cell('title')); assert.equal(opened.at(-1)[0], button().dataset.copyId);
      for (const query of ['Writer 51', 'ISBN-3', 'Publication 3', 'Task 03', 'A library', 'Current staff', '12']) {
        await filter(query); assert.ok(button(), `Underlying search supports ${query}`);
        click(cell('title')); assert.equal(opened.at(-1)[0], button().dataset.copyId);
      }
      await filter('Task 00'); assert.match(cell('timing').textContent, /Task timeout: Off/);
      await filter('Task 01'); assert.match(cell('timing').textContent, /Task timeout: Unavailable/);
      assert.equal(cell('title').querySelector('.additional-copy-notes'), null);
      assert.ok(cell('staff').querySelector('.mine'));
      await filter('');
      get('#additional-copy-claim-filter').value = 'mine'; get('#additional-copy-claim-filter').dispatchEvent(new dom.window.Event('change'));
      await until(() => get('#additional-copy-summary').textContent === '27 open tasks', 'Claim filter');
      assert.ok(rows().every(r => r.querySelector('.mine')));
      get('#additional-copy-claim-filter').value = 'all'; get('#additional-copy-claim-filter').dispatchEvent(new dom.window.Event('change'));
      await until(() => get('#additional-copy-summary').textContent === '55 open tasks', 'Claim reset');
      const oldRow = first(), oldButton = button(), port = queue.focusReturn(oldButton.dataset.copyId);
      data.reverse(); await queue.refresh();
      await until(() => button() !== oldButton, 'Refresh replaces rows');
      assert.equal(oldButton.isConnected, false);
      assert.ok(port.target()?.isConnected, 'Focus follows task identity after row replacement');
      count = opened.length; click(oldButton); click(oldRow); assert.equal(opened.length, count);
      // Even reinserting a retired row in the live container cannot revive its captured projection.
      get('#additional-copy-grid tbody').append(oldRow); click(oldButton); click(oldRow.querySelector('td'));
      assert.equal(click(oldRow.querySelector('a')), false, 'A reinserted stale BIB link cannot navigate');
      assert.equal(opened.length, count); oldRow.remove();
      const focusId = button().dataset.copyId;
      await filter('no matching task');
      assert.equal(queue.focusReturn(focusId).target(), undefined);
      assert.equal(queue.focusReturn(focusId).fallback, get('#refresh-additional-copies'));
      click(first()); assert.equal(opened.length, count, 'Empty state cannot open');
      await filter('');
      const scopeRow = first(), scopeButton = button();
      const previous = context; context = { ...context, scope: '3' };
      click(scopeRow.querySelector('td')); click(scopeButton); assert.equal(opened.length, count, 'Unaccepted scope cannot open');
      queue.contextChanged(context, previous);
      data = [{ ...data[0], id: '7', title: '<img src=x onerror=alert(1)>', author: null, bibid: null, libraryOrgId: 3,
        libraryOrgName: null, formatLabel: null, createdByUsername: null, notes: null, created: null }];
      await queue.refresh(); await until(() => button()?.dataset.copyId === '7', 'New scope');
      assert.match(cell('title').textContent, /<img src=x onerror=alert\(1\)>Author not recorded/);
      assert.equal(get('#additional-copy-grid').querySelectorAll('img').length, 0, 'Runtime data stays text');
      assert.match(cell('bib').textContent, /Not recorded/); assert.match(cell('library').textContent, /Library not recorded/);
      assert.match(cell('staff').textContent, /Created by: Not recorded/);
      assert.ok(urls.at(-1).includes('scope=3&status=open'));
      const openButton = button(), priorStatus = context;
      context = { ...context, additionalCopyStatus: 'closed' }; queue.contextChanged(context, priorStatus);
      click(openButton); assert.equal(opened.length, count);
      data = [{ ...data[0], status: 'closed' }]; await queue.refresh();
      await until(() => button() && button() !== openButton, 'Closed view');
      assert.match(cell('timing').textContent, /Task timeout: —/);
      click(cell('title')); assert.equal(opened.at(-1)[0], '7'); count = opened.length;
      context = { ...context, activeView: 'queue' }; click(button()); click(cell('title')); assert.equal(opened.length, count);
      context = { ...context, activeView: 'additional-copies' };
      session.accept({ ...staff, id: '21', authenticationEmail: 'b@example.org' });
      click(button()); click(cell('title')); assert.equal(opened.length, count, 'Retired session cannot open');
      queue.signedOut(); click(openButton); assert.equal(opened.length, count);
    } finally { queue.dispose(); }
  });
  console.log('Six-column real Grid.js copies: grouping, sorts, search, paging, identity, safe row activation and stale ownership passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
