const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { spawnSync } = require('child_process');
const { JSDOM } = require('jsdom');

const response = body => ({ ok: true, status: 200, json: async () => body });
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(predicate(), message);
}
async function afterFocusFrame(window) {
  await new Promise(resolve => window.requestAnimationFrame(() => window.setTimeout(resolve, 0)));
}
async function settleGridWork(window) {
  // Grid.js schedules follow-up renders after its data promise resolves.
  await afterFocusFrame(window);
  await afterFocusFrame(window);
  await new Promise(resolve => window.setTimeout(resolve, 0));
}

async function runJourney(scenario, pipelineOverlap = false) {
  const titleRequest = ['title-return', 'new-dialog'].includes(scenario);
  const replacesFocusedOpener = ['copy-replaced', 'replacement-user-focus'].includes(scenario);
  const apiPath = titleRequest ? '/title-requests' : '/additional-copies';
  const gridId = titleRequest ? 'request-grid' : 'additional-copy-grid';
  const openerLabel = titleRequest ? 'Open request' : 'Open additional-copy task';
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-dialog-focus-'));
  fs.cpSync(path.join(source, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
  let dom, app;
  try {
    dom = new JSDOM(fs.readFileSync(path.join(source, 'staff', 'index.html'), 'utf8'), {
      url: `https://localhost/staff/${titleRequest ? '' : '?stage=additional_copies'}`,
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    const gridModule = path.join(source, 'vendor/gridjs/6.2.0/gridjs.umd.js');
    delete require.cache[require.resolve(gridModule)];
    dom.window.gridjs = require(gridModule);
    let renderedGrid;
    const originalRender = dom.window.gridjs.Grid.prototype.render;
    dom.window.gridjs.Grid.prototype.render = function (container) {
      renderedGrid = this;
      return originalRender.call(this, container);
    };

    const staff = { id: '20', tenantId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      authenticationEmail: 'staff@example.invalid', role: scenario === 'library-navigation' ? 'super_admin' : 'staff',
      organizationId: scenario === 'library-navigation' ? '1' : '2', displayName: 'Library staff' };
    let task = {
      id: '91', version: 'first-version', title: 'Delayed grid focus journey',
      libraryOrgId: '2', libraryOrgName: 'Library two', status: titleRequest ? 'suggestion' : 'open',
      claimedByStaffUserId: null, claimedByDisplayName: null,
      capabilities: { canClaim: true }
    };
    const otherTask = { ...task, id: '92', title: 'A different task' };
    const filteredTask = { ...task, id: '93', title: 'Another staff member\'s task',
      claimedByStaffUserId: '21', claimedByDisplayName: 'Other staff' };
    const organizations = [{ id: '2', name: 'Library two' }, { id: '3', name: 'Library three' }];
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) return response({ authenticated: true, staff, antiforgeryToken: 'focus-af' });
      if (url.startsWith('/api/asap/config?')) return response({});
      if (url.includes(`${apiPath}?`)) {
        const scope = new URL(url, dom.window.location.href).searchParams.get('scope');
        return response({ items: scope === '3' ? [] : [task, otherTask, filteredTask], scope, organizations, availableLibraries: organizations });
      }
      if (url.endsWith(`${apiPath}/91`)) return response(task);
      if (url.endsWith(`${apiPath}/92`)) return response(otherTask);
      if (url.endsWith(`${apiPath}/91/claim`)) {
        task = { ...task, version: 'claimed-version', claimedByStaffUserId: staff.id,
          claimedByDisplayName: staff.displayName, capabilities: { canUnclaim: true } };
        return response({ ...task, committed: true, finalStatus: task.status });
      }
      if (url.endsWith(`${apiPath}/91/unclaim`)) {
        assert.strictEqual(options.headers['X-ASAP-Antiforgery'], 'focus-af');
        task = { ...task, version: 'unclaimed-version', claimedByStaffUserId: null,
          claimedByDisplayName: null, capabilities: { canClaim: true } };
        return response({ ...task, committed: true, finalStatus: task.status });
      }
      if (url.endsWith('/sign-out')) return response({ signedOut: true });
      throw new Error(`Unexpected request ${url}`);
    };
    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    app = workflow.createWorkflowApp();
    await app.start();
    const opener = (id = '91') => document.querySelector(`[aria-label="${openerLabel} ${id}"]`);
    const claimLabel = () => opener()?.closest('tr').querySelector('.claim-label');
    await until(() => opener() && opener('92') && opener('93'), 'The real Grid.js must render the initial tasks');
    const assertOpenNotSortable = () => {
      const header = document.querySelector(`#${gridId} th[data-column-id="open"]`);
      assert.equal(header.querySelector('.gridjs-sort'), null, 'The Open action column must remain unsortable');
      assert.equal(header.hasAttribute('tabindex'), false, 'The Open header must not become a sorting tab stop');
    };
    assertOpenNotSortable();
    if (scenario === 'new-dialog') {
      opener('92').click();
      const dialog = document.getElementById('request-dialog');
      await until(() => dialog.open, 'The second request must enter Recent');
      dialog.dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));
      await until(() => !new URL(dom.window.location.href).searchParams.has('request'),
        'The Recent seed dialog must finish its history close');
    }
    let releasePriorSort, priorSort;
    if (pipelineOverlap) {
      // Pause a real normal-array sort at its final processor, then accept a newer
      // filter. The old completion must not restore excluded rows or mutate the new grid.
      const started = deferred(), released = deferred();
      const pipeline = renderedGrid.config.pipeline;
      const process = pipeline.process;
      pipeline.process = function (...args) {
        this.process = process;
        priorSort = process.apply(this, args);
        return priorSort;
      };
      const limit = pipeline.steps.find(step => step.type === 8);
      const limitProcess = limit.process;
      limit.process = function (...args) {
        this.process = limitProcess;
        const rows = limitProcess.apply(this, args);
        started.resolve();
        return released.promise.then(() => rows);
      };
      releasePriorSort = released.resolve;
      document.querySelector(`#${gridId} th[data-column-id="title"]`).click();
      await started.promise;
    }
    const initialOpener = opener();
    const claimFilter = document.getElementById(titleRequest ? 'claim-filter' : 'additional-copy-claim-filter');
    claimFilter.value = replacesFocusedOpener ? 'mine_unclaimed' : 'unclaimed';
    claimFilter.dispatchEvent(new dom.window.Event('change'));
    // Wait for the actual filtered data before another update.
    await until(() => opener() && opener() !== initialOpener && opener('92') && !opener('93'),
      'The unclaimed tasks must remain after the other staff claim is visibly filtered out');
    assertOpenNotSortable();
    if (pipelineOverlap) {
      await until(() => document.querySelector(`#${gridId} .grid-open`) === opener('92'),
        'A refreshed grid must preserve the selected ascending sort');
      releasePriorSort();
      await priorSort;
      await settleGridWork(dom.window);
      assert.strictEqual(opener('93'), null, 'An older sort must not overwrite the newer accepted filter');
      document.querySelector(`#${gridId} th[data-column-id="title"]`).click();
      await until(() => document.querySelector(`#${gridId} .grid-open`) === opener(),
        'Title descending must sort the real filtered rows');
      const descendingOpener = opener();
      document.getElementById(titleRequest ? 'refresh-queue' : 'refresh-additional-copies').click();
      await until(() => opener() && opener() !== descendingOpener && !opener('93') &&
        document.querySelector(`#${gridId} .grid-open`) === opener(),
      'Refreshing must preserve the selected descending sort and current filter');
      document.querySelector(`#${gridId} th[data-column-id="title"]`).click();
      await until(() => document.querySelector(`#${gridId} .grid-open`) === opener('92'),
        'Title ascending must sort the real filtered rows');
    }
    opener().focus();
    opener().click();
    const dialog = document.getElementById('request-dialog');
    await until(() => dialog.open, 'The task dialog must open');
    const unclaimedOpener = opener();
    const unchangedTaskOpener = opener('92');
    [...dialog.querySelectorAll('button')].find(button => button.textContent === 'Claim').click();
    await until(() => (replacesFocusedOpener
      ? opener() && opener() !== unclaimedOpener && claimLabel()?.textContent === staff.displayName && claimLabel().classList.contains('mine')
      : !opener()) && opener('92') && opener('92') !== unchangedTaskOpener &&
      document.getElementById('app-status').textContent ===
        (titleRequest ? 'Request claimed. Final state: Suggestion.' : 'Task claimed. Final state: Open.'),
      'Claiming must update the filtered queue');
    await afterFocusFrame(dom.window);

    if (pipelineOverlap) {
      console.log(`Staff older normal-array sort, newer filter and claim passed: ${scenario}`);
      return;
    }

    // Use Grid.js's asynchronous data source so its genuine render completes after dialog close.
    let renderStarted = false;
    const renderReleased = deferred();
    const OriginalGrid = dom.window.gridjs.Grid;
    dom.window.gridjs.Grid = class extends OriginalGrid {
      constructor(configuration) {
        dom.window.gridjs.Grid = OriginalGrid;
        assert.ok(Array.isArray(configuration.data), 'The workflow must supply an array to the held update');
        assert.deepStrictEqual(configuration.data.map(row =>
          [row[titleRequest ? 11 : 10], row[titleRequest ? 9 : 6]]),
        [['91', 'Unclaimed'], ['92', 'Unclaimed']],
          'The held update must be the completed unclaim refresh, not an earlier filter or claim render');
        const rows = configuration.data;
        super({ ...configuration, data: async () => {
          renderStarted = true;
          await renderReleased.promise;
          return rows;
        } });
      }
      render(container) {
        assert.strictEqual(container.parentElement.id, gridId, 'Only the owning grid unclaim render may be held');
        return super.render(container);
      }
    };
    [...dialog.querySelectorAll('button')].find(button => button.textContent === 'Unclaim').click();
    await until(() => renderStarted, 'The unclaim must start the real asynchronous Grid.js data source');
    await until(() => document.getElementById('app-status').textContent ===
      (titleRequest ? 'Your claim was released. Final state: Suggestion.' : 'Task unclaimed. Final state: Open.'),
    'The unclaim and queue refresh must finish');
    const previousOpener = opener();
    assert.strictEqual(previousOpener, null,
      'A replacement grid must await its own data instead of presenting the prior revision rows');
    dialog.dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));
    assert.strictEqual(dialog.open, false);
    await until(() => !new URL(dom.window.location.href).searchParams.has('request'),
      'Explicit close must complete its history traversal before another navigation');
    await afterFocusFrame(dom.window);
    let newerFocus;
    if (scenario === 'view-navigation') {
      document.querySelector('[data-view="profile"]').click();
      newerFocus = document.getElementById('profile-title');
    } else if (scenario === 'new-dialog') {
      const recent = [...document.querySelectorAll('#recent-request-list button')]
        .find(button => button.textContent.includes('Request 92'));
      assert.ok(recent, 'The second request remains keyboard reachable from Recent while the grid loads');
      document.getElementById('recent-work').open = true;
      recent.focus();
      recent.click();
      await until(() => dialog.open && document.getElementById('request-dialog-title').textContent === otherTask.title, 'A newer task dialog must open');
      newerFocus = document.getElementById('close-request');
    } else if (scenario === 'user-focus' || scenario === 'replacement-user-focus') {
      newerFocus = document.getElementById('additional-copy-search');
      newerFocus.focus();
      newerFocus.value = 'Unsaved search';
    } else if (scenario === 'sign-out') {
      document.getElementById('sign-out').click();
      await until(() => document.getElementById('workspace').hidden, 'Sign-out must hide the old workspace');
      newerFocus = document.activeElement;
    } else if (scenario === 'library-navigation') {
      const scope = document.getElementById('additional-copy-library-scope');
      scope.value = '3';
      scope.dispatchEvent(new dom.window.Event('change'));
      await until(() => !document.getElementById('refresh-additional-copies').disabled &&
        document.getElementById('additional-copy-library-scope').value === '3' &&
        !document.getElementById('additional-copy-empty').hidden, 'The newer library scope must load');
      newerFocus = document.activeElement;
    }
    renderReleased.resolve();
    if (scenario === 'sign-out') {
      await settleGridWork(dom.window);
      assert.equal(document.querySelector(`#${gridId} tbody`), null,
        'A late protected grid render must stay detached after sign-out');
    } else {
      await until(() => document.querySelector(`#${gridId} tbody`) &&
        (scenario === 'library-navigation' || (opener() && opener() !== previousOpener)), 'The genuine asynchronous grid render must finish');
    }
    await afterFocusFrame(dom.window);
    if (newerFocus) {
      assert.strictEqual(document.activeElement, newerFocus, `${scenario}: the old grid must not steal newer focus`);
    } else {
      assert.strictEqual(document.activeElement, opener(), 'Closing after unclaim must return focus when the replacement opener renders');
    }
    if (scenario === 'new-dialog') assert.strictEqual(dialog.open, true);
    if (scenario === 'sign-out') assert.strictEqual(document.getElementById('workspace').hidden, true);
    if (scenario === 'library-navigation') assert.strictEqual(document.getElementById('additional-copy-library-scope').value, '3');
    console.log(`Staff delayed Grid.js dialog focus passed: ${scenario}`);
  } finally {
    // Drain the released render before retiring the app that owns its grids.
    if (dom) await settleGridWork(dom.window);
    app?.dispose();
    if (dom) await settleGridWork(dom.window);
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  if (process.argv[2]) {
    await runJourney(process.argv[2], process.argv[3] === 'pipeline-overlap');
    return;
  }
  // Grid.js retains scheduled render work. Give each DOM journey its own browser-like global lifetime.
  const journeys = ['copy-return', 'copy-replaced', 'title-return', 'view-navigation', 'new-dialog',
    'user-focus', 'replacement-user-focus', 'sign-out', 'library-navigation'].map(scenario => [scenario]);
  journeys.push(['copy-return', 'pipeline-overlap'], ['title-return', 'pipeline-overlap']);
  for (const journey of journeys) {
    const scenario = journey.join(' ');
    const result = spawnSync(process.execPath, [__filename, ...journey], { encoding: 'utf8', timeout: 20000 });
    process.stdout.write(result.stdout || '');
    process.stderr.write(result.stderr || '');
    assert.ifError(result.error);
    assert.strictEqual(result.status, 0, `${scenario} must pass in its isolated DOM`);
    assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, /\[Grid.js\] \[ERROR\]/, `${scenario} must complete without render errors`);
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
