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

const payload = (scope, range, label, dates = {}) => ({
  scope: {
    mode: scope === 'all' ? 'all' : 'library',
    libraryOrgId: scope === 'all' ? null : Number(scope),
    label,
    superAdmin: true
  },
  dateRange: {
    key: range,
    start: '2026-09-01T04:00:00Z',
    end: '2026-09-16T03:59:59Z',
    startDate: dates.startDate || '2026-09-01',
    endDate: dates.endDate || '2026-09-15',
    timeZoneId: 'America/New_York',
    ...dates
  },
  availableLibraries: [{ orgId: 2, name: 'Library Two' }, { orgId: 3, name: 'Library Three' }],
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

    const { createAnalyticsController } = await import(pathToFileURL(path.join(temporary, 'staff/js/analytics.js')).href);
    const container = document.getElementById('analytics-container');
    const owner = {};
    const analytics = createAnalyticsController({ root: container, sessionIdentity: { preferences: () => owner, isCurrent: captured => captured === owner } });
    analytics.activate();
    const initial = analytics.refresh();
    assert.strictEqual(pending.length, 1);
    assert.equal(new URL(requests[0].url, dom.window.location.href).searchParams.get('range'), 'lastMonth',
      'the controller should request the initial Last month report');
    pending[0].resolve(response(200, payload('all', 'lastMonth', 'All libraries')));
    await initial;
    assert.ok(container.textContent.includes('All libraries'));

    const scope = container.querySelector('#analytics-scope');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await flush();
    assert.strictEqual(pending.length, 2);
    const scopeRace = analytics.refresh();
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
    const rangeRace = analytics.refresh();
    await flush();
    assert.strictEqual(pending.length, 5);
    pending[3].resolve(response(200, payload('2', 'lastMonth', 'Stale last-month range response')));
    pending[4].resolve(response(200, payload('2', 'last90', 'Library Two')));
    await rangeRace;
    await flush();
    await flush();
    assert.equal(container.querySelector('#analytics-date-range').value, 'last90');

    const recoveryStart = requests.length;
    const recoveryPendingStart = pending.length;
    const recovery = analytics.refresh();
    await flush();
    assert.strictEqual(pending.length, recoveryPendingStart + 1);
    pending[recoveryPendingStart].resolve(response(400, {
      code: 'invalid_scope',
      message: 'The analytics scope is invalid.'
    }));
    await flush();
    assert.strictEqual(pending.length, recoveryPendingStart + 2);
    assert.equal(new URL(requests[recoveryStart + 1].url, dom.window.location.href).searchParams.get('scope'), 'all');
    pending[recoveryPendingStart + 1].resolve(response(200, payload('all', 'last90', 'All libraries after recovery')));
    await recovery;
    await flush();
    await flush();
    assert.deepStrictEqual(
      requests.slice(recoveryStart).map(item => new URL(item.url, dom.window.location.href).searchParams.get('scope')),
      ['2', 'all']
    );
    assert.equal(container.querySelector('#analytics-scope').value, 'all');
    assert.equal(container.querySelector('#analytics-date-range').value, 'last90');
    assert.ok(container.querySelector('#analytics-scope').options.length >= 3);
    assert.match(container.textContent, /All libraries after recovery/);

    const restore = analytics.refresh();
    await flush();
    const restoreIndex = pending.length - 1;
    pending[restoreIndex].resolve(response(200, payload('2', 'last90', 'Library Two restored')));
    await restore;
    await flush();
    assert.equal(container.querySelector('#analytics-scope').value, '2');

    const noLoopStart = requests.length;
    const noLoopPendingStart = pending.length;
    const retryFailure = analytics.refresh();
    await flush();
    assert.strictEqual(pending.length, noLoopPendingStart + 1);
    pending[noLoopPendingStart].resolve(response(400, {
      code: 'invalid_scope',
      message: 'The analytics scope is invalid.'
    }));
    await flush();
    assert.strictEqual(pending.length, noLoopPendingStart + 2);
    pending[noLoopPendingStart + 1].resolve(response(400, {
      code: 'invalid_scope',
      message: 'The analytics scope is still invalid.'
    }));
    await retryFailure;
    await flush();
    await flush();
    assert.strictEqual(requests.length - noLoopStart, 2, 'invalid_scope recovery must run once');
    assert.match(container.textContent, /The analytics scope is still invalid/);

    const arbitrary400Start = requests.length;
    const arbitrary400PendingStart = pending.length;
    const arbitrary400 = analytics.refresh();
    await flush();
    pending[arbitrary400PendingStart].resolve(response(400, {
      code: 'analytics_unavailable',
      message: 'Analytics is unavailable.'
    }));
    await arbitrary400;
    await flush();
    assert.strictEqual(requests.length - arbitrary400Start, 1, 'other 400 responses must not recover');
    assert.match(container.textContent, /Analytics is unavailable/);

    const restoreForRace = analytics.refresh();
    await flush();
    pending[pending.length - 1].resolve(response(200, payload('2', 'last90', 'Library Two restored for race')));
    await restoreForRace;
    await flush();
    assert.equal(container.querySelector('#analytics-scope').value, '2');

    const staleRecoveryStart = requests.length;
    const staleRecoveryPendingStart = pending.length;
    const staleRecovery = analytics.refresh();
    await flush();
    pending[staleRecoveryPendingStart].resolve(response(400, {
      code: 'invalid_scope',
      message: 'The analytics scope is invalid.'
    }));
    await flush();
    const recoveryIndex = pending.length - 1;
    const newerLoad = analytics.refresh();
    await flush();
    const newerPendingIndex = pending.length - 1;
    assert.deepStrictEqual(
      requests.slice(staleRecoveryStart).map(item => new URL(item.url, dom.window.location.href).searchParams.get('scope')),
      ['2', 'all', 'all']
    );
    pending[recoveryIndex].resolve(response(200, payload('all', 'last90', 'Stale recovery response')));
    pending[newerPendingIndex].resolve(response(200, payload('all', 'last90', 'Newer all response')));
    await Promise.all([staleRecovery, newerLoad]);
    await flush();
    await flush();
    assert.match(container.textContent, /Newer all response/);
    assert.ok(!container.textContent.includes('Stale recovery response'));

    const staleIndex = requests.length;
    const stalePendingIndex = pending.length;
    const stale = analytics.refresh();
    await flush();
    assert.strictEqual(pending.length, stalePendingIndex + 1);
    analytics.deactivate();
    pending[stalePendingIndex].resolve(response(200, payload('all', 'lastMonth', 'Stale auth response')));
    await stale;
    await flush();
    assert.match(container.textContent, /Updating analytics/);
    assert.ok(!container.textContent.includes('Stale auth response'));
    assert.ok(requests.some(item => new URL(item.url, dom.window.location.href).searchParams.get('scope') === '2'));
    assert.ok(requests.some(item => new URL(item.url, dom.window.location.href).searchParams.get('range') === 'last90'));
    analytics.signedOut(); analytics.activate();
    const afterSignOut = analytics.refresh();
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('scope'), null);
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('range'), 'lastMonth');
    pending[pending.length - 1].resolve(response(200, payload('all', 'lastMonth', 'New session defaults')));
    await afterSignOut; analytics.dispose();

    const { createSessionIdentity } = await import(pathToFileURL(path.join(temporary, 'staff/js/session-identity.js')).href);
    const identity = createSessionIdentity();
    const staff = { id: '20', tenantId: 'tenant', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
    const preferences = identity.accept(staff);
    const replacement = createAnalyticsController({ root: container, sessionIdentity: identity });
    replacement.setStaff(preferences); replacement.activate();
    const initialActorLoad = replacement.refresh();
    pending[pending.length - 1].resolve(response(200, payload('all', 'lastMonth', 'Actor A defaults')));
    await initialActorLoad;
    container.querySelector('#analytics-scope').value = '2';
    container.querySelector('#analytics-scope').dispatchEvent(new dom.window.Event('change'));
    const staleScopeRequest = pending.length - 1;
    container.querySelector('#analytics-date-range').value = 'last90';
    container.querySelector('#analytics-date-range').dispatchEvent(new dom.window.Event('change'));
    const selectedActorRequest = pending.length - 1;
    pending[staleScopeRequest].resolve(response(200, payload('2', 'lastMonth', 'Stale actor scope')));
    pending[selectedActorRequest].resolve(response(200, payload('2', 'last90', 'Actor A selections')));
    await flush();
    assert.equal(container.querySelector('#analytics-scope').value, '2');
    assert.equal(container.querySelector('#analytics-date-range').value, 'last90');
    replacement.setStaff(identity.updatePreferences({ ...staff, version: 'v2' }, preferences));
    const sameActor = replacement.refresh();
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('range'), 'last90');
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('scope'), '2');
    replacement.setStaff(identity.accept({ ...staff, id: '21', authenticationEmail: 'b@example.org' }));
    const newActor = replacement.refresh();
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('range'), 'lastMonth');
    assert.equal(new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams.get('scope'), 'all');
    pending[pending.length - 1].resolve(response(200, payload('all', 'lastMonth', 'Actor B defaults')));
    await newActor;
    pending[pending.length - 2].resolve(response(200, payload('2', 'last90', 'Stale actor A')));
    await sameActor;
    assert.match(container.textContent, /Actor B defaults/);
    assert.doesNotMatch(container.textContent, /Stale actor A/);
    replacement.dispose();

    const customIdentity = createSessionIdentity();
    const customOwner = customIdentity.accept({ ...staff, id: '22', authenticationEmail: 'c@example.org' });
    const customController = createAnalyticsController({ root: container, sessionIdentity: customIdentity });
    customController.setStaff(customOwner); customController.activate();
    const customInitial = customController.refresh();
    const initialRequestIndex = pending.length - 1;
    pending[initialRequestIndex].resolve(response(200, payload('all', 'lastMonth', 'Custom test baseline')));
    await customInitial;
    const customRange = container.querySelector('#analytics-date-range');
    customRange.value = 'custom';
    customRange.dispatchEvent(new dom.window.Event('change'));
    assert.equal(pending.length, initialRequestIndex + 1, 'selecting Custom must not fetch');
    assert.equal(container.querySelector('.analytics-custom-range').hidden, false,
      'Custom reveals the stable editor node used by the browser controls');
    assert.equal(container.querySelector('#analytics-start-date').getAttribute('aria-label'), 'Start date');
    assert.equal(container.querySelector('#analytics-end-date').getAttribute('aria-label'), 'End date');
    container.querySelector('#analytics-apply-range').click();
    assert.equal(pending.length, initialRequestIndex + 1, 'incomplete custom dates must not fetch');
    assert.equal(document.activeElement, container.querySelector('#analytics-start-date'));
    assert.equal(container.querySelector('#analytics-start-date').getAttribute('aria-invalid'), 'true');
    container.querySelector('#analytics-start-date').value = '2026-03-08';
    container.querySelector('#analytics-start-date').dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    container.querySelector('#analytics-end-date').value = '2026-03-08';
    container.querySelector('#analytics-end-date').dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    container.querySelector('#analytics-apply-range').click();
    assert.equal(pending.length, initialRequestIndex + 2, 'a valid same-day range should fetch');
    const customQuery = new URL(requests[requests.length - 1].url, dom.window.location.href).searchParams;
    assert.equal(customQuery.get('range'), 'custom');
    assert.equal(customQuery.get('startDate'), '2026-03-08');
    assert.equal(customQuery.get('endDate'), '2026-03-08');
    pending[pending.length - 1].resolve(response(200, payload('all', 'custom', 'Custom day', {
      startDate: '2026-03-08', endDate: '2026-03-08'
    })));
    await flush();
    assert.match(container.querySelector('.analytics-range-summary').textContent, /Mar 8, 2026 through Mar 8, 2026/);
    assert.match(container.querySelector('.analytics-range-summary').textContent, /America\/New_York/);

    const startInput = container.querySelector('#analytics-start-date');
    const endInput = container.querySelector('#analytics-end-date');
    const applyButton = container.querySelector('#analytics-apply-range');
    const rangeSelect = container.querySelector('#analytics-date-range');
    const scopeSelect = container.querySelector('#analytics-scope');
    const summary = () => container.querySelector('.analytics-range-summary').textContent;
    const queryAt = index => new URL(requests[index].url, dom.window.location.href).searchParams;

    startInput.value = '2026-11-01';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-02';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(pending.length, initialRequestIndex + 2, 'editing dates must not fetch');
    assert.match(summary(), /Mar 8, 2026 through Mar 8, 2026/, 'draft dates must not relabel the displayed report');

    scopeSelect.value = '2';
    scopeSelect.focus();
    scopeSelect.dispatchEvent(new dom.window.Event('change'));
    const pendingScopeIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('range'), 'custom');
    assert.equal(queryAt(requests.length - 1).get('startDate'), '2026-03-08');
    assert.equal(queryAt(requests.length - 1).get('endDate'), '2026-03-08');
    assert.equal(queryAt(requests.length - 1).get('scope'), '2');
    assert.equal(startInput.value, '2026-11-01');
    assert.equal(endInput.value, '2026-11-02');
    pending[pendingScopeIndex].resolve(response(200, payload('2', 'custom', 'Library Two applied day', {
      startDate: '2026-03-08', endDate: '2026-03-08'
    })));
    await flush();
    assert.match(summary(), /Library Two applied day/);
    assert.equal(document.activeElement, scopeSelect, 'scope recovery/render must not replace control focus');

    rangeSelect.value = 'last90';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));
    const presetIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('range'), 'last90');
    assert.equal(queryAt(requests.length - 1).has('startDate'), false, 'presets must omit stale dates');
    assert.equal(queryAt(requests.length - 1).has('endDate'), false, 'presets must omit stale dates');
    pending[presetIndex].resolve(response(200, payload('2', 'last90', 'Library Two preset')));
    await flush();
    rangeSelect.value = 'custom';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));
    assert.equal(pending.length, presetIndex + 1, 'returning to Custom must not fetch');
    assert.equal(startInput.value, '2026-11-01');
    assert.equal(endInput.value, '2026-11-02');

    applyButton.focus();
    applyButton.click();
    const firstCustomIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('startDate'), '2026-11-01');
    assert.equal(queryAt(requests.length - 1).get('endDate'), '2026-11-02');
    startInput.value = '2026-11-03';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-04';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(pending.length, firstCustomIndex + 1, 'typing while loading must not dispatch another report');
    pending[firstCustomIndex].resolve(response(200, payload('2', 'custom', 'Applied November range', {
      startDate: '2026-11-01', endDate: '2026-11-02'
    })));
    await flush();
    assert.match(summary(), /Nov 1, 2026 through Nov 2, 2026/);
    assert.equal(startInput.value, '2026-11-03');
    assert.equal(endInput.value, '2026-11-04');

    applyButton.focus();
    applyButton.click();
    const invalidResponseIndex = pending.length - 1;
    pending[invalidResponseIndex].resolve(response(400, {
      code: 'invalid_date_range', message: 'Choose a valid custom date range.',
      errors: { endDate: ['End date must be a representable business calendar date.'] }
    }));
    await flush();
    assert.equal(pending.length, invalidResponseIndex + 1, 'date validation errors must not enter scope recovery');
    assert.equal(endInput.getAttribute('aria-invalid'), 'true');
    assert.equal(endInput.getAttribute('aria-describedby'), 'analytics-end-date-error');
    assert.equal(document.activeElement, endInput, 'matching server errors focus the field only while Apply retains focus');
    assert.match(container.querySelector('#analytics-end-date-error').textContent, /representable/);
    assert.match(summary(), /Nov 1, 2026 through Nov 2, 2026/);

    startInput.value = '2026-11-10';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-05';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    applyButton.click();
    assert.equal(pending.length, invalidResponseIndex + 1, 'reversed dates must fail before dispatch');
    assert.equal(document.activeElement, endInput);
    assert.match(container.querySelector('#analytics-end-date-error').textContent, /on or after/);

    startInput.value = '2026-11-05';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-06';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    applyButton.click();
    const retryableIndex = pending.length - 1;
    pending[retryableIndex].resolve(response(503, { code: 'analytics_unavailable', message: 'Analytics is unavailable.' }));
    await flush();
    assert.match(summary(), /Nov 1, 2026 through Nov 2, 2026/);
    assert.equal(container.querySelector('.analytics-retry').hidden, false);
    container.querySelector('.analytics-retry').click();
    const retryIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('startDate'), '2026-11-05');
    assert.equal(queryAt(requests.length - 1).get('endDate'), '2026-11-06');
    pending[retryIndex].resolve(response(200, payload('2', 'custom', 'Retried November range', {
      startDate: '2026-11-05', endDate: '2026-11-06'
    })));
    await flush();
    assert.match(summary(), /Nov 5, 2026 through Nov 6, 2026/);

    startInput.value = '2026-11-07';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-08';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    scopeSelect.value = '3';
    scopeSelect.focus();
    scopeSelect.dispatchEvent(new dom.window.Event('change'));
    const invalidScopeIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('startDate'), '2026-11-05');
    assert.equal(queryAt(requests.length - 1).get('endDate'), '2026-11-06');
    pending[invalidScopeIndex].resolve(response(400, {
      code: 'invalid_scope', message: 'The analytics scope is invalid.'
    }));
    await flush();
    const customRecoveryIndex = pending.length - 1;
    assert.equal(queryAt(requests.length - 1).get('scope'), 'all');
    assert.equal(queryAt(requests.length - 1).get('range'), 'custom');
    assert.equal(queryAt(requests.length - 1).get('startDate'), '2026-11-05');
    assert.equal(queryAt(requests.length - 1).get('endDate'), '2026-11-06');
    assert.equal(startInput.value, '2026-11-07');
    assert.equal(endInput.value, '2026-11-08');
    const untrustedReport = payload('all', 'custom', 'Recovered <img id="analytics-xss" src="missing">', {
      startDate: '2026-11-05', endDate: '2026-11-06'
    });
    untrustedReport.closedReasons = [{ reason: '<img id="analytics-reason-xss">', count: 1 }];
    pending[customRecoveryIndex].resolve(response(200, untrustedReport));
    await flush();
    assert.equal(scopeSelect.value, 'all');
    assert.equal(rangeSelect.value, 'custom');
    assert.equal(document.activeElement, scopeSelect);
    assert.match(summary(), /Nov 5, 2026 through Nov 6, 2026/);
    assert.ok(summary().includes('<img id="analytics-xss" src="missing">'));
    assert.equal(container.querySelector('#analytics-xss'), null, 'Runtime scope labels render as text');
    assert.equal(container.querySelector('#analytics-reason-xss'), null, 'Runtime metric labels render as text');

    startInput.value = '2026-11-07';
    startInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    endInput.value = '2026-11-08';
    endInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    applyButton.click();
    const staleCustomSuccess = pending.length - 1;
    rangeSelect.value = 'last30';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));
    const latestPresetSuccess = pending.length - 1;
    pending[staleCustomSuccess].resolve(response(200, payload('all', 'custom', 'Stale custom result', {
      startDate: '2026-11-07', endDate: '2026-11-08'
    })));
    pending[latestPresetSuccess].resolve(response(200, payload('all', 'last30', 'Latest preset result')));
    await flush();
    assert.match(summary(), /Latest preset result/);
    assert.doesNotMatch(container.textContent, /Stale custom result/);
    assert.equal(rangeSelect.value, 'last30');
    assert.equal(startInput.value, '2026-11-07');
    assert.equal(endInput.value, '2026-11-08');

    rangeSelect.value = 'custom';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));
    applyButton.click();
    const staleDateError = pending.length - 1;
    rangeSelect.value = 'thisMonth';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));
    const latestMonthSuccess = pending.length - 1;
    pending[staleDateError].resolve(response(400, {
      code: 'invalid_date_range', message: 'Choose a valid custom date range.',
      errors: { startDate: ['Old submitted date must not mark the newer draft invalid.'] }
    }));
    pending[latestMonthSuccess].resolve(response(200, payload('all', 'thisMonth', 'Latest month result')));
    await flush();
    assert.match(summary(), /Latest month result/);
    assert.equal(startInput.hasAttribute('aria-invalid'), false, 'A stale field error cannot mark a newer selection invalid');
    assert.equal(endInput.hasAttribute('aria-invalid'), false);
    rangeSelect.value = 'custom';
    rangeSelect.dispatchEvent(new dom.window.Event('change'));

    const sameActorDraft = customIdentity.updatePreferences({ ...staff, version: 'v2' }, customOwner);
    customController.setStaff(sameActorDraft);
    assert.equal(startInput.value, '2026-11-07', 'same-actor preference refresh should preserve drafts');
    const nextActor = customIdentity.accept({ ...staff, id: '23', authenticationEmail: 'd@example.org' });
    customController.setStaff(nextActor);
    assert.equal(container.querySelector('#analytics-date-range').value, 'lastMonth');
    assert.equal(container.querySelector('#analytics-start-date').value, '');
    assert.equal(container.querySelector('#analytics-end-date').value, '');
    customController.dispose();
    console.log('Staff Analytics stale scope/range/auth reset regression checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
