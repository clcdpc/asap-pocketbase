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

async function runJourney(scenario) {
  const titleRequest = scenario === 'title-return';
  const replacesFocusedOpener = ['copy-replaced', 'replacement-user-focus'].includes(scenario);
  const apiPath = titleRequest ? '/title-requests' : '/additional-copies';
  const gridId = titleRequest ? 'request-grid' : 'additional-copy-grid';
  const openerLabel = titleRequest ? 'Open request' : 'Open additional-copy task';
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-dialog-focus-'));
  fs.cpSync(path.join(source, 'staff-next'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
  let dom;
  try {
    dom = new JSDOM(fs.readFileSync(path.join(source, 'staff-next', 'index.html'), 'utf8'), {
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

    const staff = { id: '20', role: scenario === 'library-navigation' ? 'super_admin' : 'staff',
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
        return response(task);
      }
      if (url.endsWith(`${apiPath}/91/unclaim`)) {
        assert.strictEqual(options.headers['X-ASAP-Antiforgery'], 'focus-af');
        task = { ...task, version: 'unclaimed-version', claimedByStaffUserId: null,
          claimedByDisplayName: null, capabilities: { canClaim: true } };
        return response(task);
      }
      if (url.endsWith('/sign-out')) return response({ signedOut: true });
      throw new Error(`Unexpected request ${url}`);
    };
    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    const opener = (id = '91') => document.querySelector(`[aria-label="${openerLabel} ${id}"]`);
    const claimLabel = () => opener()?.closest('tr').querySelector('.claim-label');
    await until(() => opener() && opener('92') && opener('93'), 'The real Grid.js must render the initial tasks');
    const initialOpener = opener();
    const claimFilter = document.getElementById(titleRequest ? 'claim-filter' : 'additional-copy-claim-filter');
    claimFilter.value = replacesFocusedOpener ? 'mine_unclaimed' : 'unclaimed';
    claimFilter.dispatchEvent(new dom.window.Event('change'));
    // forceRender first remounts old rows; wait for the actual filtered data before another update.
    await until(() => opener() && opener() !== initialOpener && opener('92') && !opener('93'),
      'The unclaimed tasks must remain after the other staff claim is visibly filtered out');
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
      document.getElementById('app-status').textContent === (titleRequest ? 'Request updated.' : 'Task claimed.'), 'Claiming must update the filtered queue');
    await afterFocusFrame(dom.window);

    // Use Grid.js's asynchronous data source so its genuine render completes after dialog close.
    let renderStarted = false;
    const renderReleased = deferred();
    const updateConfig = dom.window.gridjs.Grid.prototype.updateConfig;
    dom.window.gridjs.Grid.prototype.updateConfig = function (configuration) {
      dom.window.gridjs.Grid.prototype.updateConfig = updateConfig;
      assert.strictEqual(this.config.container.id, gridId, 'Only the owning grid unclaim render may be held');
      assert.ok(Array.isArray(configuration.data), 'The workflow must supply an array to the held update');
      assert.deepStrictEqual(configuration.data.map(row => [row[6], row[4]]), [['91', 'Unclaimed'], ['92', 'Unclaimed']],
        'The held update must be the completed unclaim refresh, not an earlier filter or claim render');
      return updateConfig.call(this, { ...configuration, data: async () => {
        renderStarted = true;
        await renderReleased.promise;
        return configuration.data;
      } });
    };
    [...dialog.querySelectorAll('button')].find(button => button.textContent === 'Unclaim').click();
    await until(() => renderStarted, 'The unclaim must start the real asynchronous Grid.js data source');
    await until(() => document.getElementById('app-status').textContent === (titleRequest ? 'Request updated.' : 'Task unclaimed.'), 'The unclaim and queue refresh must finish');
    const previousOpener = opener();
    if (replacesFocusedOpener) assert.ok(previousOpener, 'The old row remains until Grid.js completes its data pipeline');
    else assert.strictEqual(previousOpener, null, 'The refreshed grid has not rendered the replacement opener');
    dialog.dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));
    assert.strictEqual(dialog.open, false);
    await afterFocusFrame(dom.window);
    let newerFocus;
    if (scenario === 'view-navigation') {
      document.querySelector('[data-view="profile"]').click();
      newerFocus = document.getElementById('profile-title');
    } else if (scenario === 'new-dialog') {
      opener('92').focus();
      opener('92').click();
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
      await until(() => document.getElementById('app-status').textContent === '0 authorized additional-copy tasks loaded.', 'The newer library scope must load');
      newerFocus = document.activeElement;
    }
    renderReleased.resolve();
    await until(() => document.querySelector(`#${gridId} tbody`) &&
      (scenario === 'library-navigation' || (opener() && opener() !== previousOpener)), 'The genuine asynchronous grid render must finish');
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
    if (dom) await settleGridWork(dom.window);
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  if (process.argv[2]) {
    await runJourney(process.argv[2]);
    return;
  }
  // Grid.js retains scheduled render work. Give each DOM journey its own browser-like global lifetime.
  for (const scenario of ['copy-return', 'copy-replaced', 'title-return', 'view-navigation', 'new-dialog',
    'user-focus', 'replacement-user-focus', 'sign-out', 'library-navigation']) {
    const result = spawnSync(process.execPath, [__filename, scenario], { encoding: 'utf8', timeout: 20000 });
    process.stdout.write(result.stdout || '');
    process.stderr.write(result.stderr || '');
    assert.ifError(result.error);
    assert.strictEqual(result.status, 0, `${scenario} must pass in its isolated DOM`);
    assert.doesNotMatch(`${result.stdout}\n${result.stderr}`, /\[Grid.js\] \[ERROR\]/, `${scenario} must complete without render errors`);
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
