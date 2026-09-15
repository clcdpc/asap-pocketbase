const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({
  ok: status >= 200 && status < 300,
  status,
  statusText: status === 401 ? 'Unauthorized' : 'OK',
  json: async () => body
});

const payload = (scope, range, label) => ({
  scope: {
    mode: scope === 'all' ? 'all' : 'library',
    libraryOrgId: scope === 'all' ? '' : scope,
    label,
    superAdmin: true
  },
  dateRange: { key: range, start: '2026-09-01T04:00:00Z', end: '2026-09-16T03:59:59Z' },
  availableLibraries: [{ orgId: '2', name: 'Library Two' }, { orgId: '3', name: 'Library Three' }],
  summary: { newSuggestions: 1, openRequests: 2, closedRequests: 3, heldRequests: 4, averageDaysToHold: 2.5 },
  stageCounts: { suggestion: 1, outstanding_purchase: 0, pending_hold: 0, hold_placed: 1, closed: 1, additional_copies: 0 },
  closedReasons: [],
  aging: { thresholdDays: 30, openOlderThanThreshold: 0, averageAgeByStage: [] },
  exceptions: { holdFailures: 0, identifierFailures: 0 }
});

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-analytics-race-'));
  fs.cpSync(path.join(source, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  let dom;
  try {
    dom = new JSDOM('<main><div id="analytics-container"></div></main>', {
      url: 'https://localhost/staff/',
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    global.FormData = dom.window.FormData;

    const requests = [];
    const pending = [];
    global.fetch = (url, options = {}) => {
      const request = { url: String(url), options };
      requests.push(request);
      if (request.url.endsWith('/session')) {
        return Promise.resolve(response(200, { authenticated: true, antiforgeryToken: 'analytics-af' }));
      }
      if (request.url.includes('/api/asap/staff/analytics?')) {
        return new Promise(resolve => pending.push({ request, resolve }));
      }
      throw new Error(`Unexpected request ${request.url}`);
    };

    const analytics = await import(pathToFileURL(path.join(temporary, 'staff/js/analytics.js')).href);
    const container = document.getElementById('analytics-container');
    const initial = analytics.loadAnalytics(container);
    assert.strictEqual(pending.length, 1);
    pending[0].resolve(response(200, payload('all', 'lastMonth', 'All libraries')));
    await initial;
    assert.ok(container.textContent.includes('All libraries'));

    const scope = container.querySelector('#analytics-scope');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await flush();
    assert.strictEqual(pending.length, 2);
    const scopeRace = analytics.loadAnalytics(container);
    await flush();
    assert.strictEqual(pending.length, 3);
    pending[1].resolve(response(200, payload('all', 'lastMonth', 'Stale all-libraries scope response')));
    pending[2].resolve(response(200, payload('2', 'lastMonth', 'Library Two')));
    await scopeRace;
    await flush();
    await flush();
    assert.match(container.textContent, /Library Two/);
    assert.equal(container.querySelector('#analytics-scope').value, '2');
    assert.ok(!container.querySelector('.analytics-range-summary').textContent.includes('All libraries'));

    const range = container.querySelector('#analytics-date-range');
    range.value = 'last90';
    range.dispatchEvent(new dom.window.Event('change'));
    await flush();
    assert.strictEqual(pending.length, 4);
    const rangeRace = analytics.loadAnalytics(container);
    await flush();
    assert.strictEqual(pending.length, 5);
    pending[3].resolve(response(200, payload('2', 'lastMonth', 'Stale last-month range response')));
    pending[4].resolve(response(200, payload('2', 'last90', 'Library Two')));
    await rangeRace;
    await flush();
    await flush();
    assert.equal(container.querySelector('#analytics-date-range').value, 'last90');

    const stale = analytics.loadAnalytics(container);
    await flush();
    assert.strictEqual(pending.length, 6);
    analytics.resetAnalytics();
    pending[5].resolve(response(200, payload('all', 'lastMonth', 'Stale auth response')));
    await stale;
    await flush();
    assert.match(container.textContent, /Loading analytics/);
    assert.ok(!container.textContent.includes('Stale auth response'));
    assert.ok(requests.some(item => new URL(item.url, dom.window.location.href).searchParams.get('scope') === '2'));
    assert.ok(requests.some(item => new URL(item.url, dom.window.location.href).searchParams.get('range') === 'last90'));
    console.log('Staff Analytics stale scope/range/auth reset regression checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
