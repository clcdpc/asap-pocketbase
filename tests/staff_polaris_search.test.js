const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-polaris-search-'));
  const root = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff');
  const markup = fs.readFileSync(path.join(root, 'index.html'), 'utf8').match(/<dialog id="polaris-dialog"[\s\S]*?<\/dialog>/)[0];
  const dom = new JSDOM(markup, { url: 'https://localhost/staff/' });
  try {
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    for (const name of ['research', 'catalog-queue', 'ui', 'session-identity', 'recent-requests', 'mutation-outcome']) {
      fs.copyFileSync(path.join(root, 'js', `${name}.js`), path.join(temporary, `${name}.js`));
    }
    global.window = dom.window; global.document = dom.window.document; global.Node = dom.window.Node;
    const { createPolarisLookup, safeMaterialIconUrl } = await import(pathToFileURL(path.join(temporary, 'research.js')).href);
    const { createSessionIdentity } = await import(pathToFileURL(path.join(temporary, 'session-identity.js')).href);
    const { catalogQueueStorageKey, decodeCatalogQueue } = await import(pathToFileURL(path.join(temporary, 'catalog-queue.js')).href);
    const tick = () => new Promise(resolve => setTimeout(resolve, 0));
    const until = async (predicate, message) => {
      for (let i = 0; i < 100; i++) { if (predicate()) return; await tick(); }
      assert.fail(message);
    };
    const sessionIdentity = createSessionIdentity();
    const actor = sessionIdentity.accept({ id: '7', tenantId: 'tenant', authenticationEmail: 'staff@example.org', role: 'super_admin', organizationId: 1 });
    const dialog = document.querySelector('#polaris-dialog');
    dialog.showModal = () => { dialog.open = true; }; dialog.close = () => { dialog.open = false; };
    const query = document.querySelector('#polaris-query'), mode = document.querySelector('#polaris-mode');
    const resultPane = document.querySelector('#polaris-results'), panel = document.querySelector('#polaris-confirmation');
    const rows = Array.from({ length: 12 }, (_, index) => ({ bibId: 9001 + index, title: `Catalog <unsafe> ${index}`,
      author: 'Catalog author', publication: '2026', format: index === 1 ? '33181604' : 'Book',
      formatIconUrl: index === 0 ? '/material.svg' : 'javascript:alert(1)', identifier: `ID-${index}` }));
    let lookups = [], mutations = [], pendingReads = [], activeReads = 0, maxReads = 0, delay = false, version = 'authoritative-v2';
    let behavior = 'success', mutationResolve, callbacks = 0, readCurrent = true;
    const messages = [], receipts = [];
    const detail = id => ({ ...rows.find(row => row.bibId === id), requestId: '9007199254740993', libraryOrgId: 2,
      version, holdingsSummary: { myLibraryCount: id === 9002 ? 2 : 0, otherLibraryCount: 4,
        consortiumCount: id === 9002 ? 6 : 4, isHoldable: true },
      queueActions: { requestId: '9007199254740993', libraryOrgId: 2, version, bibId: id, currentAutohold: false,
        emailPurchaseReminderDefault: true, openAdditionalCopyCount: 2,
        useBib: { allowed: true, targetStatus: 'closed' }, additionalCopy: { allowed: true, targetStatus: 'pending_hold' } } });
    const response = body => ({ id: '9007199254740993', version: 'committed-v3', status: body.action === 'catalogFound' ? 'closed' : 'pending_hold',
      committed: true, operationId: body.operationId, additionalCopyRequestId: body.action === 'additionalCopy' ? '21' : null,
      notificationStatus: 'queued' });
    let searchRows = rows;
    const lookup = createPolarisLookup({ sessionIdentity, announce: (...args) => messages.push(args),
      onReceipt: (...args) => receipts.push(args), clearReceipt() {}, isAbortError: error => error.name === 'AbortError',
      authorizedJson: async (url, options) => {
        if (url.endsWith('/action')) {
          mutations.push(options.body);
          if (behavior === 'pending') return new Promise(resolve => { mutationResolve = () => resolve(response(options.body)); });
          if (behavior === 'uncertain') throw Object.assign(new Error('Lost response'), { status: 503, response: { code: 'request_outcome_unconfirmed' } });
          if (behavior === 'reject') throw Object.assign(new Error('The request changed.'), { status: 409, response: { code: 'stale_version' } });
          return response(options.body);
        }
        lookups.push({ ...options.body, signal: options.signal });
        if (options.body.mode !== 'bib') return { results: searchRows, totalMatches: 2951 };
        activeReads++; maxReads = Math.max(maxReads, activeReads);
        if (delay) await new Promise(resolve => pendingReads.push(resolve));
        activeReads--;
        return detail(options.body.bibId);
      }
    });
    const current = (overrides = {}) => ({ requestId: '9007199254740993', libraryOrgId: 2, actor,
      title: 'Staff title', author: 'Staff author', query: 'Catalog', mode: 'title', readOnly: true,
      directActions: true, isCurrent: () => readCurrent && sessionIdentity.isCurrent(actor),
      onCommitted: async () => { callbacks++; return true; }, ...overrides });
    const search = async () => {
      document.querySelector('#polaris-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await tick();
    };
    const click = text => [...panel.querySelectorAll('button')].find(button => button.textContent === text).click();
    const cancel = () => dialog.dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));

    assert.equal(safeMaterialIconUrl('https://localhost/valid.svg'), 'https://localhost/valid.svg');
    assert.equal(safeMaterialIconUrl('/icons/material-01.svg'), '/icons/material-01.svg');
    for (const url of ['javascript:alert(1)', 'data:image/png;base64,x', '//evil.test/x', '///evil.test/x', '/\\evil.test/x',
      '/icons/\nmaterial.svg', 'https://user:secret@evil.test/x', 'https://x.test/\\y', 'https://x.test/\nx']) {
      assert.equal(safeMaterialIconUrl(url), '');
    }
    delay = true; lookup.open(current()); await search();
    assert.equal(pendingReads.length, 3, 'Only three ownership lookups can run concurrently.');
    assert.equal(resultPane.children.length, 10, 'Defend the ten-result cap at rendering as well as at the endpoint.');
    assert.equal(document.querySelector('#polaris-status').textContent, '10 catalog results shown of 2,951.');
    assert.match(resultPane.textContent, /Checking ownership/);
    assert.equal(resultPane.querySelector('button').disabled, true);
    assert.equal(resultPane.querySelectorAll('img').length, 1); assert.equal(resultPane.querySelector('img').alt, '');
    assert.equal(resultPane.querySelector('img').src, 'https://localhost/material.svg', 'Root-relative material icons stay on the application origin.');
    resultPane.querySelector('img').dispatchEvent(new dom.window.Event('error'));
    assert.equal(resultPane.querySelectorAll('img').length, 0, 'Broken decorative icons fall back to the readable format label.');
    assert.doesNotMatch(resultPane.textContent, /33181604/);
    assert.equal(resultPane.querySelector('unsafe'), null);
    query.value = 'Replacement'; query.dispatchEvent(new dom.window.Event('input'));
    const staleSignals = lookups.filter(item => item.mode === 'bib').map(item => item.signal);
    assert.ok(staleSignals.every(signal => signal.aborted));
    pendingReads.splice(0).forEach(resolve => resolve()); await tick();
    assert.equal(resultPane.children.length, 0, 'Late ownership cannot repaint a changed query.');
    assert.equal(maxReads, 3);
    delay = false;
    searchRows = [rows[0], rows[0], rows[1]];
    lookups = []; await search();
    await until(() => resultPane.textContent.includes('Owned by you (2)'), 'Ownership completes.');
    assert.equal(lookups.filter(item => item.mode === 'bib').length, 2, 'Duplicate BIBs share a lookup within one context.');
    assert.ok(lookups.filter(item => item.mode === 'bib').every(item => item.libraryOrgId === 2 && item.includePatronHolds === false));
    assert.match(resultPane.children[2].textContent, /Owned by consortium \(6\)/, 'Use authoritative total, not the other-library count.');
    const searchesBeforeShortcut = lookups.length;
    document.querySelector('#polaris-add-author').click();
    assert.equal(mode.value, 'title_author'); assert.equal(document.querySelector('#polaris-title').value, 'Replacement');
    assert.equal(document.querySelector('#polaris-author').value, 'Staff author');
    assert.equal(document.activeElement.id, 'polaris-author'); assert.equal(lookups.length, searchesBeforeShortcut);
    mode.value = 'identifier'; mode.dispatchEvent(new dom.window.Event('change'));
    assert.equal(document.querySelector('#polaris-add-author').hidden, true);
    lookup.close();

    // Both buttons use the exact preview version rather than the stale grid version.
    lookup.open(current()); await search(); await until(() => !resultPane.querySelector('button').disabled, 'Actions become eligible.');
    resultPane.querySelector('.polaris-copy-button').click();
    assert.match(panel.textContent, /This action will turn on automatic hold placement for this request\./);
    assert.match(panel.textContent, /2 open additional-copy tasks/);
    assert.equal(panel.querySelector('input').checked, true);
    assert.equal(mutations.length, 0, 'Opening a confirmation never mutates.');
    cancel(); assert.equal(panel.hidden, true); assert.equal(document.activeElement.className, 'polaris-copy-button');
    resultPane.querySelector('.polaris-copy-button').click(); panel.querySelector('input').checked = false;
    behavior = 'pending'; click('Confirm additional copy and queue'); click('Confirm additional copy and queue');
    await tick(); assert.equal(mutations.length, 1); assert.equal(lookup.inspectDeparture().blocked, true);
    document.querySelector('#close-polaris').click(); cancel();
    assert.equal(dialog.open, true, 'Close and Escape cannot discard a pending action.');
    assert.equal(mutations[0].version, 'authoritative-v2'); assert.equal(mutations[0].requestType, 'title_request');
    assert.equal(mutations[0].bibid, mutations[0].staffSelectedBibId); assert.equal(mutations[0].emailPurchaseReminder, false);
    assert.equal(Object.hasOwn(mutations[0], 'autohold'), false, 'The server owns the forced override.');
    mutationResolve(); await until(() => !dialog.open, 'Confirmed action closes.');
    assert.equal(callbacks, 1); assert.equal(window.sessionStorage.getItem(catalogQueueStorageKey(actor)), null);
    assert.match(messages.at(-1)[0], /Final state: Pending hold/);

    behavior = 'reject'; lookup.open(current()); await search();
    await until(() => !resultPane.querySelector('button').disabled, 'Action before rejection is eligible.');
    assert.match(document.querySelector('#polaris-status').textContent, /catalog results shown of 2,951/);
    resultPane.querySelector('.polaris-queue-button').click(); click('Confirm use BIB');
    await until(() => panel.textContent.includes('Catalog action not completed'), 'Rejection is visible.');
    click('Return to search');
    assert.equal(resultPane.children.length, 0);
    assert.equal(document.querySelector('#polaris-status').textContent,
      'Search Polaris again to review current results and available actions.');
    assert.equal(document.querySelector('#polaris-ownership-status').textContent, '');
    assert.equal(document.activeElement, document.querySelector('#polaris-form button[type="submit"]'));
    lookup.close();

    behavior = 'uncertain'; lookup.open(current()); await search();
    await until(() => !resultPane.querySelector('button').disabled, 'Second action eligible.');
    const recoveryOpener = resultPane.querySelector('.polaris-copy-button');
    recoveryOpener.click(); click('Confirm additional copy and queue');
    await until(() => panel.textContent.includes('Catalog action unconfirmed'), 'Uncertainty is visible.');
    const original = mutations.at(-1), saved = window.sessionStorage.getItem(catalogQueueStorageKey(actor));
    assert.deepEqual(decodeCatalogQueue(saved, actor).body, original);
    assert.equal(decodeCatalogQueue(saved, { ...actor, role: 'staff' }), null);
    assert.equal(decodeCatalogQueue(JSON.stringify({ ...JSON.parse(saved), body: { ...original, autohold: false } }), actor), null);
    const escape = new dom.window.KeyboardEvent('keydown', { key: 'Escape', cancelable: true, bubbles: true });
    dialog.dispatchEvent(escape);
    dialog.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Escape', repeat: true, cancelable: true, bubbles: true }));
    dialog.dispatchEvent(new dom.window.KeyboardEvent('keyup', { key: 'Escape', cancelable: true, bubbles: true }));
    assert.equal(escape.defaultPrevented, true, 'An inline recovery owns Escape before the native dialog close action.');
    assert.equal(dialog.open, true);
    assert.equal(document.activeElement, recoveryOpener, 'Escape returns to the original result after an uncertain submission.');
    recoveryOpener.click(); click('Close recovery');
    assert.equal(document.activeElement, recoveryOpener, 'Close recovery returns to its connected result opener.');
    recoveryOpener.click(); recoveryOpener.remove(); click('Close recovery');
    assert.equal(document.activeElement, mode, 'A removed result opener falls back to a visible search control.');
    lookup.close(); lookup.dispose();

    // Re-create the controller as a browser reload would, preserving exact command identity.
    const restored = createPolarisLookup({ sessionIdentity, announce: (...args) => messages.push(args), isAbortError: () => false,
      authorizedJson: async (url, options) => { assert.ok(url.endsWith('/action')); mutations.push(options.body); return response(options.body); } });
    restored.open(current()); assert.match(panel.textContent, /Catalog action unconfirmed/);
    click('Recover identical action'); await until(() => !dialog.open, 'Recovery commits.');
    assert.deepEqual(mutations.at(-1), original); assert.equal(callbacks, 2);
    assert.equal(window.sessionStorage.getItem(catalogQueueStorageKey(actor)), null);
    restored.dispose();
    assert.ok(receipts.length >= 2);
    let selected = null;
    const createLookup = createPolarisLookup({ announce() {}, isAbortError: () => false,
      authorizedJson: async (url, options) => {
        assert.equal(url, '/api/asap/staff/bib-lookup');
        assert.equal(options.body.requestId, null);
        return { ...detail(9001), requestId: null };
      } });
    createLookup.open({ requestId: null, libraryOrgId: 2, mode: 'bib', query: '9001', canApply: true,
      isCurrent: () => true, apply: row => { selected = row; } });
    await search();
    assert.equal(resultPane.querySelectorAll('.polaris-queue-button, .polaris-copy-button').length, 0,
      'A suggestion draft without a saved request can select a BIB but cannot mutate a workflow.');
    resultPane.querySelector('button').click(); await tick();
    assert.equal(selected.bibId, 9001); assert.equal(dialog.open, false);
    createLookup.dispose();
    console.log('Polaris search: bounded/cancel-safe ownership, modes, icons, direct actions, confirmation, locking and durable identical retry passed');
  } finally {
    dom.window.close(); delete global.window; delete global.document; delete global.Node;
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
