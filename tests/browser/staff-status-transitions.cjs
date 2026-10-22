'use strict';
const assert = require('node:assert/strict');
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const listPath = '/api/asap/staff/title-requests';

exports.instrument = async page => {
  await page.addInitScript(() => { window.titleTrace = { events: [], nextGrid: 0 }; });
  await page.route('**/staff/js/queues.js*', async route => {
    const response = await route.fetch(), source = await response.text();
    const entry = 'function renderGrid({ preservePage = false } = {}) {';
    assert.ok(source.includes(entry), 'Instrument the actual title queue rendering entry point.');
    await route.fulfill({ response, body: source.replace(entry, `${entry}
      window.titleTrace.events.push({ type: 'renderGrid', status: getContext().status, time: performance.now() });`) });
  });
  await page.route('**/vendor/gridjs/6.2.0/gridjs.umd.js*', async route => {
    const response = await route.fetch();
    await route.fulfill({ response, body: `${await response.text()}
      const OriginalGrid = window.gridjs.Grid;
      window.gridjs.Grid = class extends OriginalGrid {
        constructor(options) {
          super(options); this.traceId = ++window.titleTrace.nextGrid;
          (window.titleTrace.instances ||= []).push(this);
          const hold = window.titleTrace.holdNextGrid;
          window.titleTrace.holdNextGrid = false;
          let held = false;
          window.titleTrace.events.push({ type: 'create', id: this.traceId, time: performance.now() });
          const process = this.config.pipeline.process.bind(this.config.pipeline);
          this.config.pipeline.process = (...args) => {
            window.titleTrace.events.push({ type: 'pipeline-start', id: this.traceId, time: performance.now() });
            return process(...args).then(value => {
              window.titleTrace.events.push({ type: 'pipeline-end', id: this.traceId, time: performance.now() });
              if (hold && !held) {
                held = true; window.titleTrace.gridHeld = this.traceId;
                return new Promise(resolve => { window.titleTrace.releaseGrid = () => resolve(value); });
              }
              return value;
            });
          };
        }
        render(frame) {
          window.titleTrace.events.push({ type: 'mount', id: this.traceId, connected: frame.isConnected, time: performance.now() });
          return super.render(frame);
        }
        destroy() {
          window.titleTrace.events.push({ type: 'destroy', id: this.traceId, time: performance.now() });
          return super.destroy();
        }
      };` });
  });
};

async function observe(page) {
  await page.evaluate(() => {
    const trace = window.titleTrace;
    trace.events = []; trace.frames = []; trace.commits = []; trace.recording = true;
    const generation = trace.generation = (trace.generation || 0) + 1;
    const tables = new WeakMap(); let nextTable = 0;
    const sample = () => {
      const grid = document.querySelector('#request-grid'), table = grid.querySelector('table');
      if (table && !tables.has(table)) tables.set(table, ++nextTable);
      return { table: table ? tables.get(table) : null,
        ready: !!grid.querySelector('.request-title-link, .gridjs-notfound'),
        loading: !!grid.querySelector('.gridjs-loading'),
        rows: [...grid.querySelectorAll('.request-title-link')].map(node => node.dataset.requestId),
        summary: document.querySelector('#queue-summary').textContent,
        inert: !!grid.firstElementChild?.inert };
    };
    trace.observer = new MutationObserver(records => {
      for (const record of records) {
        if (record.target.id === 'request-grid' && record.addedNodes.length) trace.commits.push({ time: performance.now(), ...sample() });
      }
    });
    trace.observer.observe(document.querySelector('#queue-view'), { childList: true, subtree: true });
    const frame = () => {
      if (!trace.recording || trace.generation !== generation) return;
      const value = sample();
      if (JSON.stringify(trace.frames.at(-1)?.value) !== JSON.stringify(value)) trace.frames.push({ time: performance.now(), value });
      requestAnimationFrame(frame);
    };
    frame();
  });
}

async function finish(page) {
  // Include Grid's 100ms deferred Sort/Pagination effects and any redundant
  // response/render after the target first appears; a final-content check misses it.
  await page.waitForTimeout(650);
  return page.evaluate(() => {
    const trace = window.titleTrace; trace.recording = false; trace.observer.disconnect();
    return { events: trace.events, frames: trace.frames, commits: trace.commits };
  });
}

async function settled(page, rows) {
  await page.waitForFunction(expected => {
    const grid = document.querySelector('#request-grid');
    const actual = [...grid.querySelectorAll('.request-title-link')].map(node => node.dataset.requestId);
    return !grid.firstElementChild?.inert && !grid.querySelector('.gridjs-loading') &&
      !!grid.querySelector('.request-title-link, .gridjs-notfound') && JSON.stringify(actual) === JSON.stringify(expected);
  }, rows);
}

function stable(trace, expected, label) {
  assert.equal(trace.commits.length, 1, `${label}: exactly one connected frame replacement`);
  assert.equal(trace.commits[0].ready, true, `${label}: mount only a committed table`);
  assert.deepEqual(trace.commits[0].rows, expected, `${label}: replacement contains the selected status`);
  assert.ok(trace.frames.every(frame => frame.value.ready && !frame.value.loading), `${label}: no painted empty/loading startup body`);
  const tables = [...new Set(trace.frames.map(frame => frame.value.table))];
  assert.equal(tables.length, 2, `${label}: one visible table transition, including the original table`);
  const firstTarget = trace.frames.findIndex(frame => frame.value.table === tables[1]);
  assert.ok(firstTarget >= 0);
  assert.ok(trace.frames.slice(firstTarget).every(frame => frame.value.table === tables[1] &&
    JSON.stringify(frame.value.rows) === JSON.stringify(expected)), `${label}: no later empty/stale/replacement frame`);
}

exports.verify = async ({ page, context, baseURL, library, scan }) => {
  const response = await context.request.get(`${baseURL}${listPath}?scope=${library}`);
  assert.equal(response.status(), 200, await response.text());
  const accepted = await response.json();
  const expected = status => accepted.items.filter(item => item.status === status).slice(0, 25).map(item => item.id);
  assert.equal(expected('pending_hold').length, 0, 'Exercise a genuinely empty status.');
  assert.equal(expected('outstanding_purchase').length, 1, 'Exercise a genuinely single-row status.');
  assert.equal(expected('suggestion').length, 25, 'Exercise a genuinely multi-page status.');
  await page.locator('#claim-filter').selectOption('all'); await settled(page, expected('suggestion'));
  const requests = [], onRequest = request => {
    if (request.method() === 'GET' && new URL(request.url()).pathname === listPath) requests.push(request.url());
  };
  page.on('request', onRequest);
  const cdp = await context.newCDPSession(page), report = { transitions: [] };
  try {
    for (const rate of [1, 4]) {
      await cdp.send('Emulation.setCPUThrottlingRate', { rate });
      for (const status of [...stages.slice(1), stages[0]]) {
        const before = requests.length; await observe(page);
        await page.locator(`#status-tabs [data-status="${status}"]`).click();
        await settled(page, expected(status));
        const trace = await finish(page), label = `${status} at ${rate}x CPU`;
        assert.equal(requests.length, before, `${label}: no redundant Title requests GET`);
        for (const type of ['renderGrid', 'create', 'mount', 'destroy']) {
          assert.equal(trace.events.filter(event => event.type === type).length, 1, `${label}: one ${type}`);
        }
        stable(trace, expected(status), label);
        assert.equal(await page.locator(`#status-tabs [data-status="${status}"]`).getAttribute('aria-selected'), 'true');
        report.transitions.push({ status, rate, gets: requests.length - before, ...trace });
        if (status === 'pending_hold') await scan(`status-empty-${rate}x`);
      }
      await scan(`status-populated-${rate}x`);
    }

    // Real sorting and paging after repeated remounts at 4x CPU.
    const grid = page.locator('#request-grid');
    await grid.locator('th:nth-child(2) button').click();
    await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Quick-create source');
    await grid.getByRole('button', { name: 'Next', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Table browser record 24');
    assert.equal(await grid.locator('.gridjs-currentPage').textContent(), '2');
    await grid.getByRole('button', { name: 'Previous', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#request-grid .request-title-link')?.textContent === 'Quick-create source');

    const beforeRapid = requests.length; await observe(page);
    await page.evaluate(statuses => { for (const status of statuses) document.querySelector(`#status-tabs [data-status="${status}"]`).click(); }, [...stages.slice(1), stages[0]]);
    await settled(page, expected('suggestion'));
    report.rapid = await finish(page);
    assert.equal(requests.length, beforeRapid);
    assert.equal(report.rapid.events.filter(event => event.type === 'create').length, 5);
    stable(report.rapid, expected('suggestion'), 'Rapid five-tab selection');

    await grid.getByRole('button', { name: 'Next', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('#request-grid .gridjs-currentPage')?.textContent === '2');
    await page.evaluate(() => { window.titleTrace.holdNextGrid = true; });
    await page.locator('#status-tabs [data-status="closed"]').click();
    await page.waitForFunction(() => window.titleTrace.gridHeld);
    await page.locator('#refresh-queue').click();
    await settled(page, expected('closed'));
    assert.equal(await grid.locator('.gridjs-currentPage').textContent(), '1',
      'Refresh during a pending status reset cannot inherit the previous status page.');
    await page.evaluate(() => window.titleTrace.releaseGrid());
    await page.waitForFunction(() => window.titleTrace.events.some(event => event.type === 'destroy' && event.id === window.titleTrace.gridHeld));
    await settled(page, expected('closed'));
    await page.locator('#status-tabs [data-status="suggestion"]').click();
    await settled(page, expected('suggestion'));
    report.pendingStatusRefresh = true;

    // Enter a genuine old sort pipeline and keep it pending through the next
    // status commit. Its late completion must stay in its retired frame.
    await page.evaluate(() => {
      const instance = window.titleTrace.instances.findLast(grid => grid.config.container?.isConnected);
      const limit = instance.config.pipeline.steps.find(step => step.type === 8), process = limit.process;
      limit.process = function(...args) {
        this.process = process;
        const rows = process.apply(this, args); window.titleTrace.sortHeld = true;
        return new Promise(resolve => { window.titleTrace.releaseSort = () => resolve(rows); });
      };
      window.titleTrace.heldGrid = instance.traceId;
    });
    await grid.locator('th:nth-child(2) button').click();
    await page.waitForFunction(() => window.titleTrace.sortHeld);
    await page.locator('#status-tabs [data-status="closed"]').click();
    await settled(page, expected('closed'));
    assert.equal(await page.evaluate(() => window.titleTrace.events.some(event => event.type === 'destroy' && event.id === window.titleTrace.heldGrid)), false,
      'Destroy must wait for an entered sort to drain.');
    await page.evaluate(() => window.titleTrace.releaseSort());
    await page.waitForFunction(() => window.titleTrace.events.some(event => event.type === 'destroy' && event.id === window.titleTrace.heldGrid));
    await page.waitForTimeout(250);
    await settled(page, expected('closed'));
    await page.locator('#status-tabs [data-status="suggestion"]').click();
    await settled(page, expected('suggestion'));
    report.delayedSortRetired = true;

    // Hold a real HTTP response, then cancel its required reload by selecting a
    // different status. The replacement must fetch authoritative SQL data.
    let release, started, completed;
    const held = new Promise(done => { release = done; }), pending = new Promise(done => { started = done; });
    const completion = new Promise(done => { completed = done; });
    let intercepted = false;
    const delayed = '**/api/asap/staff/title-requests?*';
    await page.route(delayed, async route => {
      if (intercepted) return route.continue();
      intercepted = true;
      const result = await route.fetch(); started(); await held;
      try { await route.fulfill({ response: result, json: { ...(await result.json()), items: [{ ...accepted.items[0], title: 'Canceled stale response must never appear' }] } }); }
      catch (error) { if (!/closed|canceled|cancelled|invalid interception/i.test(error.message)) throw error; }
      finally { completed(); }
    });
    try {
      const beforeRefresh = requests.length;
      await page.locator('#refresh-queue').click(); await pending;
      await page.locator('#status-tabs [data-status="outstanding_purchase"]').click();
      await settled(page, expected('outstanding_purchase'));
      assert.equal(requests.length, beforeRefresh + 2, 'Canceled required refresh obtains one replacement authoritative read.');
      release(); await completion; await finish(page);
      assert.doesNotMatch(await grid.textContent(), /Canceled stale response/);
      await settled(page, expected('outstanding_purchase'));
    } finally { release(); await page.unroute(delayed); }

    for (const scope of ['all', library]) {
      const beforeScope = requests.length;
      await page.locator('#library-scope').selectOption(scope);
      await page.locator('#refresh-queue:not([disabled])').waitFor();
      assert.equal(requests.length, beforeScope + 1, 'Library-scope selection always fetches authoritative data.');
      assert.equal(new URL(requests.at(-1)).searchParams.get('scope'), scope);
    }
    await settled(page, expected('outstanding_purchase'));
    const selected = page.locator('#status-tabs [data-status="outstanding_purchase"]');
    await selected.focus(); await page.keyboard.press('End');
    await settled(page, expected('closed'));
    assert.equal(await page.locator('#status-tabs [data-status="closed"]').evaluate(node => node === document.activeElement), true);
    await page.keyboard.press('Home'); await settled(page, expected('suggestion'));
    const beforeHistory = requests.length;
    await page.goBack(); await settled(page, expected('closed'));
    await page.goForward(); await settled(page, expected('suggestion'));
    assert.equal(requests.length, beforeHistory, 'Same-scope status history uses the fresh accepted projection.');
    assert.equal(new URL(page.url()).searchParams.get('stage'), 'suggestion');
    await scan('status-keyboard-history');

    await page.locator('#request-search').fill('TABLE-BROWSER-00');
    await page.waitForFunction(() => document.querySelectorAll('#request-grid .request-title-link').length === 1);
    await page.locator('[data-view="profile"]').click();
    await page.locator('#profile-view').waitFor({ state: 'visible' });
    const beforeReentry = requests.length;
    await page.goBack(); await settled(page, expected('suggestion'));
    assert.equal(await page.locator('#request-search').inputValue(), '');
    assert.equal(requests.length, beforeReentry + 1, 'Returning from another view reloads the reset filters.');
    report.crossViewHistory = true;
    return report;
  } finally {
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
    await cdp.detach(); page.off('request', onRequest);
  }
};
