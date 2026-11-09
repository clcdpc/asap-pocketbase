'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { Blocked, result, exitCode, safeError } = require('./helpers/reporting.cjs');
const { validateEvidence, requireFixture, verifyEnvironment } = require('./helpers/environment.cjs');
const { parse, run, runScenarios } = require('./run.cjs');
const { screenshot, registerScreenshotPage } = require('./helpers/accessibility.cjs');
const appSha = 'a'.repeat(40), toolSha = 'b'.repeat(40);
const repository = path.resolve(__dirname, '../..');
test('Requests cleanup suite preserves its issue map and guarded mutation owner', () => {
  const suite = require('./suites/i479.cjs');
  assert.equal(suite.id, 'i479');
  assert.deepEqual(suite.scenarios.filter(s => s.writes).map(s => s.id), ['i479-mutation-retention']);
  assert.ok(suite.scenarios.every(s => s.requirements.some(r => /#45[78]|#476/.test(r))));
  assert.ok(suite.human.some(row => row[1].includes('#398')));
  assert.ok(suite.uncovered.some(row => row[0] === 'i479-native-identity-and-invalid-links'));
});
test('confirmation failure is observed while its trigger is still pending', async () => {
  const { decideConfirmation } = require('../browser/confirmation.cjs');
  const page = { locator: () => ({ waitFor: async () => { throw new Error('Synthetic confirmation unavailable'); } }) };
  const action = new Promise(resolve => setTimeout(resolve, 25));
  await assert.rejects(decideConfirmation(page, () => action, () => {}), /Synthetic confirmation unavailable/);
  await action;
});
test('non-assertion failures retain a code location without exporting error values', async t => {
  const root = await temporary(t), directory = path.join(root, 'timeout-source'); await fs.mkdir(directory);
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  await runScenarios({ suite: { scenarios: [{ id: 'timeout', requirements: ['Sanitized diagnostics'], run: async () => {
    const error = new Error('Cookie=private-error-canary'); error.name = 'TimeoutError'; throw error;
  } }] }, options: {}, environment: environment(root), browser: fakeBrowser(), report, directory, verify: async () => environment(root) });
  assert.match(report.scenarios[0].source, /^tests\/acceptance\/runner.test.cjs:\d+:\d+$/);
  assert.doesNotMatch(await fs.readFile(path.join(directory, 'timeout-failure-trace.json'), 'utf8'), /private-error-canary|Cookie=/);
});
const clone = value => JSON.parse(JSON.stringify(value));
function state() {
  return { Sha: appSha, Auth: 'Local', ToolingVersion: '0.4.0', ToolingSha: toolSha };
}
function runtime(s = state()) {
  return { identity: { applicationCommit: s.Sha, toolingCommit: s.ToolingSha, toolingVersion: s.ToolingVersion },
    aspnetEnvironment: 'Testing', authenticationMode: s.Auth, evidence: { ...evidence(),
      titles: [...evidence().titles, { key: 'acceptance-pr410-close-reopen' }],
      copies: [{ key: 'acceptance-pr410-copy' }] } };
}
function evidence() {
  return { contract: 1, database: 'AsapReview', login: 'asap_review_runtime', sqlMajorVersion: 16, sqlEdition: 'Developer',
    environment: 'Review', deterministicProviders: true, deliveryMode: 'capture',
    titles: [{ id: '27', key: 'acceptance-pr410-draft', status: 'suggestion',
      events: 0, pristineNotes: true, identityBound: true, bibid: null, verified: false }],
    copies: [], outbox: [], rejectionTemplates: ['7'] };
}
async function temporary(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-runner-unit-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  return root;
}
function environment(root) {
  const s = state(root);
  return { origin: 'https://localhost:8080', authenticationMode: 'Local', identity: runtime(s).identity, evidence: evidence() };
}
const args = (...extra) => ['--suite', 'pr410', '--expected-commit', appSha, ...extra];

test('acceptance supports only local authentication and rejects retired options', () => {
  assert.equal(parse(args('--read-only')).auth, 'local');
  for (const flag of ['--auth', '--auth-timeout-seconds']) {
    assert.throws(() => parse(args(flag, 'entra')), /Unknown acceptance option/);
  }
});

test('local reports record synthetic authentication without leaking browser state', async t => {
  const root = await temporary(t);
  const browser = { ...fakeBrowser(), cookie: 'cookie-canary', credential: 'credential-canary' };
  const outcome = await run(args('--read-only'), dependencies(root, {
    authenticate: async () => browser, runScenarios: async () => {}
  }));
  assert.equal(outcome.report.authentication.mode, 'local');
  assert.equal(outcome.report.authentication.status, 'PASS');
  const json = await fs.readFile(path.join(outcome.directory, 'results.json'), 'utf8');
  const markdown = await fs.readFile(path.join(outcome.directory, 'report.md'), 'utf8');
  assert.doesNotMatch(json + markdown, /cookie-canary|credential-canary/);
  assert.ok(markdown.includes('Synthetic reviewer; OIDC/MFA/Entra configuration were not tested'));
});

test('authentication mode mismatch blocks before opening a browser and cannot fall back', async t => {
  const root = await temporary(t);
  const outcome = await run(args('--read-only'), dependencies(root, {
    verifyEnvironment: async () => ({ ...environment(root), authenticationMode: 'Entra' }),
    authenticate: async () => { throw new Error('Browser must not open for a mismatched runtime'); }
  }));
  assert.equal(outcome.code, 2);
  assert.match(outcome.report.environment.detail, /authentication mode differs/);
  assert.equal(outcome.report.authentication.mode, 'local');
  assert.equal(outcome.report.authentication.status, 'NOT RUN');
});

test('staff checks complete before the runtime verification at scenario and mutation boundaries', async t => {
  const root = await temporary(t), calls = [];
  const browser = { ...fakeBrowser(), verifyReviewer: async () => { calls.push('staff'); } };
  const outcome = await run(args('--read-only'), dependencies(root, {
    verifyEnvironment: async () => { calls.push('proof'); return environment(root); },
    authenticate: async () => browser,
    runScenarios: async ({ verify }) => {
      calls.length = 0;
      await verify();
      assert.deepEqual(calls, ['staff', 'proof']);
    }
  }));
  assert.equal(outcome.report.authentication.status, 'PASS');
});
function dependencies(root, extra = {}) {
  return { root, toolingIdentity: () => ({ commit: toolSha, dirty: false }),
    verifyEnvironment: async () => environment(root), ...extra };
}
test('unknown and prototype suite names fail clearly and still write results', async t => {
  const root = await temporary(t);
  for (const suite of ['unknown', 'constructor']) {
    const outcome = await run(['--suite', suite, '--expected-commit', appSha], dependencies(root));
    assert.equal(outcome.code, 2);
    assert.match(outcome.report.environment.detail, /Unknown suite/);
    assert.ok(await fs.stat(path.join(outcome.directory, 'results.json')));
  }
});
test('default and legacy write modes run complete suites after environment and authentication checks', async t => {
  const root = await temporary(t);
  for (const flags of [[], ['--allow-review-writes']]) {
    const calls = [];
    const outcome = await run(args(...flags), dependencies(root, {
      verifyEnvironment: async () => { calls.push('environment'); return environment(root); },
      authenticate: async () => { calls.push('authentication'); return fakeBrowser(); },
      runScenarios: async ({ suite, options, report }) => {
        calls.push('scenarios');
        assert.equal(options.allowWrites, true);
        assert.equal(options.readOnly, false);
        report.scenarios = suite.scenarios.map(s => result(s, 'PASS'));
      }
    }));
    assert.equal(outcome.code, 0);
    assert.equal(outcome.report.mode, 'mutating');
    assert.deepEqual(calls, ['environment', 'authentication', 'scenarios']);
  }
  assert.equal(parse(args('--read-only')).allowWrites, false);
});
test('runtime validation checks exact revisions, Testing, local auth, SQL, providers and fixtures', async () => {
  const actual = await verifyEnvironment(repository, appSha, { runtimeEvidence: async () => runtime(), health: async () => {} });
  assert.equal(actual.identity.applicationCommit, appSha);
  assert.equal(actual.identity.toolingCommit, toolSha);
  for (const mutate of [
    r => { r.identity.applicationCommit = toolSha; }, r => { r.identity.toolingCommit = 'HEAD'; },
    r => { r.identity.toolingVersion = 'old'; }, r => { r.aspnetEnvironment = 'Production'; },
    r => { r.authenticationMode = 'Entra'; }, r => { r.evidence.database = 'production'; },
    r => { r.evidence.sqlMajorVersion = 17; }, r => { r.evidence.deliveryMode = 'postmark'; },
    r => { r.evidence.deterministicProviders = false; }, r => { r.evidence.titles = []; },
    r => { r.evidence.copies = []; }
  ]) {
    const current = runtime(); mutate(current);
    await assert.rejects(verifyEnvironment(repository, appSha,
      { runtimeEvidence: async () => current, health: async () => {} }), Blocked);
  }
  await assert.rejects(verifyEnvironment(repository, 'HEAD'), /full lowercase/);
  await assert.rejects(verifyEnvironment(repository, appSha, { runtimeEvidence: async () => runtime(),
    health: async () => { throw new Blocked('Health unavailable.'); } }), /Health unavailable/);
  assert.throws(() => validateEvidence(null), Blocked);
});

test('missing, ambiguous, consumed and invalid fixture identities block rather than reset', () => {
  assert.equal(requireFixture(evidence(), 'draft').id, '27');
  for (const alter of [
    e => { e.titles = []; }, e => { e.titles.push(clone(e.titles[0])); },
    e => { e.titles[0].events = 1; }, e => { e.titles[0].status = 'closed'; },
    e => { e.titles[0].pristineNotes = false; }, e => { e.titles[0].id = 'bad'; },
    e => { e.titles[0].identityBound = false; }
  ]) { const e = evidence(); alter(e); assert.throws(() => requireFixture(e, 'draft'), Blocked); }
});
test('independent application/tooling SHAs and read-only NOT RUN are recorded', async t => {
  const root = await temporary(t);
  const deps = dependencies(root, { authenticate: async () => fakeBrowser(),
    runScenarios: async ({ suite, report }) => {
      report.scenarios = suite.scenarios.map(s => result(s, s.writes ? 'NOT RUN' : 'PASS'));
    } });
  const a = await run(args('--read-only'), deps);
  const b = await run(args('--read-only'), deps);
  assert.equal(a.code, 2); assert.notEqual(a.directory, b.directory);
  assert.equal(a.report.applicationCommit, appSha);
  assert.equal(a.report.toolingCommit, toolSha);
  assert.equal(a.report.authentication.status, 'PASS');
  assert.equal(a.report.automatedFunctionalAcceptance, 'INCOMPLETE');
  assert.ok(a.report.scenarios.filter(s => s.category === 'functional').some(s => s.status === 'NOT RUN'));
  const failed = await run(args('--read-only'), dependencies(root, {
    verifyEnvironment: async () => { throw new Blocked('Application SHA mismatch.'); }
  }));
  assert.equal(failed.report.applicationCommit, null, 'Requested SHA is never substituted for a verified runtime SHA');
  assert.equal(failed.report.expectedApplicationCommit, appSha);
});
test('authentication cancellation and failures never pass, and exception secrets are not output', async t => {
  const root = await temporary(t);
  for (const error of [new Blocked('Authentication cancelled.'), new Error('Cookie=secret-canary; Password=secret-canary')]) {
    const outcome = await run(args('--read-only'), dependencies(root, { authenticate: async () => { throw error; } }));
    assert.equal(outcome.code, 2); assert.equal(outcome.report.authentication.status, 'BLOCKED');
    const text = await fs.readFile(path.join(outcome.directory, 'results.json'), 'utf8');
    assert.doesNotMatch(text, /secret-canary/);
    assert.equal(outcome.report.automatedFunctionalAcceptance, 'BLOCKED');
  }
});
test('reports distinguish PASS, FAIL, BLOCKED, NOT RUN, human review; failures are nonzero', () => {
  const scenarios = ['PASS', 'FAIL', 'BLOCKED', 'NOT RUN', 'HUMAN REVIEW REQUIRED'].map(status =>
    result({ id: status, requirements: ['runner'] }, status));
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios };
  assert.equal(exitCode(report), 2, 'Blocked/incomplete execution takes priority while FAIL rows remain present');
  assert.equal(scenarios[3].executed, false);
  assert.equal(scenarios[2].executed, false);
  report.scenarios = scenarios.filter(s => s.status !== 'FAIL');
  assert.equal(exitCode(report), 2);
  assert.doesNotMatch(safeError(new Error('access_token=secret-canary')), /secret-canary/);
});
function fakeBrowser(pageOverrides = {}) {
  const context = { route: async (_pattern, handler) => { context.handler = handler; },
    unroute: async () => {}, newPage: async () => ({
      on: () => {}, close: async () => {}, isClosed: () => false, url: () => 'about:blank', ...pageOverrides
    }) };
  return { context, page: { close: async () => {} }, close: async () => {}, role: 'super_admin', library: 2 };
}
test('actual scenario executor produces failure trace/results without network secrets', async t => {
  const root = await temporary(t), browser = fakeBrowser();
  const directory = path.join(root, 'failure'); await fs.mkdir(directory);
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  const suite = { scenarios: [
    { id: 'passes', requirements: ['observable success'], run: async () => {} },
    { id: 'fails', requirements: ['observable assertion'], run: async () => assert.equal(1, 2, 'Action count must be one') },
    { id: 'blocked', requirements: ['fixture'], run: async () => { throw new Blocked('Fixture unavailable.'); } }
  ] };
  await runScenarios({ suite, options: { readOnly: false, allowWrites: true }, environment: environment(root),
    browser, report, directory, verify: async () => environment(root) });
  assert.deepEqual(report.scenarios.map(s => s.status), ['PASS', 'FAIL', 'BLOCKED']);
  assert.equal(exitCode(report), 2);
  assert.ok(await fs.stat(path.join(directory, 'fails-failure-trace.json')));
  const trace = JSON.parse(await fs.readFile(path.join(directory, 'fails-failure-trace.json'), 'utf8'));
  assert.equal(trace.events.at(-1).assertion.expected, 2);
  assert.equal(trace.events.at(-1).assertion.actual, 1);
  assert.match(trace.events.at(-1).assertion.label, /Action count/);
  assert.ok(await fs.stat(path.join(directory, 'results.json')));
});
async function dispatchProbe(t, { readOnly = false, writes = false, path: pathname = '/api/asap/staff/settings', method = 'POST', revoke = false } = {}) {
  const root = await temporary(t), directory = path.join(root, 'dispatch'); await fs.mkdir(directory);
  const browser = fakeBrowser(), report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  let dispatched = 0, aborted = 0, checks = 0, metadata;
  await runScenarios({ suite: { scenarios: [{ id: 'dispatch', writes, requirements: ['Disposable mutations'], run: async scenario => {
    metadata = scenario.mutations;
    assert.equal(scenario.allowFixture, undefined);
    assert.equal(scenario.allowSettingsReset, undefined);
    await browser.context.handler({ request: () => ({ url: () => new URL(pathname, scenario.origin).href, method: () => method,
      postDataJSON: () => ({ secret: 'settings-document-canary' }) }),
      abort: async () => { aborted++; }, continue: async () => { dispatched++; } });
  } }] }, options: { readOnly }, environment: environment(root), browser, report, directory,
  verify: async () => { if (checks++ && revoke) throw new Blocked('Runtime unavailable.'); return environment(root); } });
  return { dispatched, aborted, checks, metadata, report, directory };
}

test('normal disposable runs permit Settings, Staff, other records and every mutation method without grants', async t => {
  for (const [method, pathname] of [['POST', '/api/asap/staff/settings'], ['PUT', '/api/asap/staff/users/999'],
    ['PATCH', '/api/asap/staff/users/27'], ['DELETE', '/api/asap/staff/users/27'],
    ['POST', '/api/asap/staff/settings/reset?organizationId=47102'], ['POST', '/api/asap/staff/additional-copies/999/close']]) {
    const outcome = await dispatchProbe(t, { method, path: pathname });
    assert.equal(outcome.dispatched, 1); assert.equal(outcome.aborted, 0);
    assert.equal(outcome.checks, 2); assert.equal(outcome.report.scenarios[0].status, 'PASS');
    assert.equal(outcome.metadata.length, 1);
    assert.doesNotMatch(JSON.stringify(outcome.metadata), /settings-document-canary|secret/);
  }
});

test('external requests remain blocked and runtime loss blocks all local writes', async t => {
  for (const input of [{ path: 'https://example.org/api/asap/staff/settings' }, { method: 'PUT', revoke: true }]) {
    const outcome = await dispatchProbe(t, input);
    assert.equal(outcome.dispatched, 0); assert.equal(outcome.aborted, 1);
    assert.equal(outcome.report.scenarios[0].status, input.revoke ? 'BLOCKED' : 'FAIL');
  }
});

test('read-only mode rejects accidental writes of every method and skips required write scenarios before execution', async t => {
  for (const method of ['POST', 'PUT', 'PATCH', 'DELETE']) {
    const outcome = await dispatchProbe(t, { readOnly: true, method });
    assert.equal(outcome.dispatched, 0); assert.equal(outcome.aborted, 1);
    assert.equal(outcome.checks, 1); assert.equal(outcome.report.scenarios[0].status, 'FAIL');
  }
  const skipped = await dispatchProbe(t, { readOnly: true, writes: true });
  assert.equal(skipped.dispatched, 0); assert.equal(skipped.aborted, 0); assert.equal(skipped.checks, 0);
  assert.equal(skipped.report.scenarios[0].status, 'NOT RUN'); assert.equal(exitCode(skipped.report), 2);
});

test('read-only Search POST is narrowly exempt without admitting writes or remote requests', async t => {
  const search = await dispatchProbe(t, { readOnly: true, path: '/api/asap/staff/search' });
  assert.equal(search.dispatched, 1); assert.equal(search.aborted, 0);
  assert.deepEqual(search.metadata, []); assert.equal(search.report.scenarios[0].status, 'PASS');
  for (const input of [{ path: '/api/asap/staff/search/action' }, { path: '/api/asap/staff/search/' },
    { path: '/api/asap/staff/search', method: 'DELETE' },
    { path: 'https://example.org/api/asap/staff/search' }]) {
    const rejected = await dispatchProbe(t, { readOnly: true, ...input });
    assert.equal(rejected.dispatched, 0); assert.equal(rejected.aborted, 1);
  }
});

test('supported npm entry points remain separate and acceptance artifacts are Git ignored', async () => {
  // Execution, selection and required-gate behavior is exercised by the existing
  // CI workflow contracts and scripts/ci/test_selection.py, without freezing source.
  const { scripts } = JSON.parse(await fs.readFile(path.join(repository, 'package.json'), 'utf8'));
  assert.equal(scripts.test, 'node tests/run_all.js');
  assert.equal(scripts['test:browser'], 'node tests/browser/run.cjs');
  assert.match(execFileSync('git', ['check-ignore', '.artifacts/acceptance/run/results.json'], { cwd: repository, encoding: 'utf8' }), /acceptance/);
});
test('CLI rejects incompatible modes, remote targets, unsafe inputs and incomplete SHAs', () => {
  for (const input of [args('--read-only', '--allow-review-writes'), args('--url', 'https://example.org'),
    args('--auth-timeout-seconds', 'NaN'), ['--suite', 'pr410', '--expected-commit', 'HEAD']]) {
    assert.throws(() => parse(input), Blocked);
  }
});
test('read-only executor never dispatches a server write', async t => {
  const root = await temporary(t), directory = path.join(root, 'read-only');
  await fs.mkdir(directory);
  const browser = fakeBrowser(), report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  let dispatched = false, aborted = false;
  await runScenarios({ suite: { scenarios: [{ id: 'read-only', writes: false, requirements: ['mutation safety'], run: async t => {
    await browser.context.handler({ request: () => ({ url: () => t.origin + '/api/asap/staff/title-requests/27/action', method: () => 'POST' }),
      abort: async () => { aborted = true; }, continue: async () => { dispatched = true; } });
  } }] }, options: { readOnly: true, allowWrites: false }, environment: environment(root), browser, report, directory,
    verify: async () => environment(root) });
  assert.equal(aborted, true); assert.equal(dispatched, false); assert.equal(report.scenarios[0].status, 'FAIL');
});
test('cancelled runs mark every unexecuted scenario BLOCKED without opening new tabs', async t => {
  const root = await temporary(t), directory = path.join(root, 'cancelled');
  await fs.mkdir(directory);
  const browser = fakeBrowser(), report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  browser.context.newPage = async () => { throw new Error('A cancelled run must not open a browser tab'); };
  const cancellation = new AbortController(); cancellation.abort();
  await runScenarios({ suite: { scenarios: [{ id: 'cancelled', writes: true, requirements: ['cancellation'],
    run: async () => { throw new Error('must not run'); } }] },
    options: { signal: cancellation.signal }, environment: environment(root), browser, report, directory,
    verify: async () => environment(root) });
  assert.equal(report.scenarios[0].status, 'BLOCKED');
  assert.equal(report.scenarios[0].executed, false);
  assert.equal(exitCode(report), 2);
});
test('loss of runtime evidence at the mutation boundary blocks dispatch and reports BLOCKED', async t => {
  const root = await temporary(t), directory = path.join(root, 'revoked'); await fs.mkdir(directory);
  const browser = fakeBrowser(), report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
  let checks = 0, dispatched = false;
  await runScenarios({ suite: { scenarios: [{ id: 'revoked', writes: true, requirements: ['revocation'], run: async t => {
    await browser.context.handler({ request: () => ({ url: () => t.origin + '/api/asap/staff/title-requests/27/action', method: () => 'POST' }),
      abort: async () => {}, continue: async () => { dispatched = true; } });
  } }] }, options: { allowWrites: true }, environment: environment(root), browser, report, directory,
  verify: async () => { if (checks++) throw new Blocked('Review runtime unavailable.'); return environment(root); } });
  assert.equal(dispatched, false);
  assert.equal(report.scenarios[0].status, 'BLOCKED');
});
test('partial results never report automated completion while required scenarios are absent', async t => {
  const root = await temporary(t), directory = path.join(root, 'partial'); await fs.mkdir(directory);
  const { writeReport } = require('./helpers/reporting.cjs');
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, requiredScenarioIds: ['one', 'two'],
    scenarios: [result({ id: 'one', requirements: ['first scenario'] }, 'PASS')] };
  await writeReport(directory, report);
  assert.equal(report.automatedFunctionalAcceptance, 'INCOMPLETE');
  assert.equal(exitCode(report), 2);
});

test('complete functional results use 0/1/2 with blockers and required skips taking precedence', () => {
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' },
    requiredScenarioIds: ['one', 'two'], scenarios: [] };
  for (const [statuses, code] of [
    [['PASS', 'PASS'], 0], [['FAIL', 'PASS'], 1], [['PASS', 'FAIL'], 1],
    [['FAIL', 'BLOCKED'], 2], [['PASS', 'NOT RUN'], 2], [['FAIL', 'NOT RUN'], 2]
  ]) {
    report.scenarios = report.requiredScenarioIds.map((id, index) => result({ id, requirements: ['required check'] }, statuses[index]));
    report.scenarios.push(result({ id: 'human', category: 'human', requirements: ['human judgment'] }, 'HUMAN REVIEW REQUIRED'));
    report.scenarios.push(result({ id: 'gap', category: 'coverage-gap', requirements: ['separate coverage'] }, 'NOT RUN'));
    assert.equal(exitCode(report), code, statuses.join('/'));
  }
  report.scenarios = ['one', 'one', 'two'].map(id => result({ id, requirements: [] }, 'PASS'));
  assert.equal(exitCode(report), 2, 'Duplicate required results cannot pass');
});

test('registered suites match discovered modules and retain their mutation boundaries', async () => {
  const registry = require('./suites/index.cjs');
  const names = (await fs.readdir(path.join(__dirname, 'suites')))
    .filter(name => name.endsWith('.cjs') && name !== 'index.cjs').map(name => name.slice(0, -4)).sort();
  assert.deepEqual(require('./suites/registry.json'), names, 'Each suite is registered exactly once in deterministic order');
  assert.deepEqual(Object.keys(registry), names);
  assert.equal(registry.smoke.id, 'smoke');
  assert.equal(registry.smoke.mutations, true);
  assert.equal(registry.s1.mutations, false);
  assert.equal(registry.i467.id, 'i467');
  assert.equal(registry.i467.mutations, false);
  assert.ok(registry.i467.scenarios.every(scenario => scenario.writes === false && scenario.requirements.length > 0));
  assert.ok(registry.s1.scenarios.every(scenario => scenario.writes === false));
  assert.equal(registry.i390.id, 'i390');
  assert.equal(registry.i390.mutations, false);
  assert.ok(registry.i390.scenarios.every(scenario => scenario.writes === false && scenario.requirements.length > 0));
  assert.deepEqual(new Set(registry.i390.scenarios.map(scenario => scenario.id)),
    new Set(['i390-draft-system', 'i390-layout-system', 'i390-draft-2', 'i390-layout-2']));
  assert.deepEqual(registry.smoke.scenarios.map(s => [s.id, s.writes]), [
    ['smoke-authenticated-review', false], ['smoke-synthetic-close', true]
  ]);
  for (const suite of Object.values(registry)) {
    assert.ok(suite.scenarios.length > 0);
    assert.equal(new Set(suite.scenarios.map(s => s.id)).size, suite.scenarios.length, 'Unique scenario identities');
    assert.ok(suite.scenarios.every(s => typeof s.id === 'string' && s.id && typeof s.run === 'function'
      && typeof s.writes === 'boolean' && s.requirements.length > 0));
    assert.equal(suite.mutations, suite.scenarios.some(s => s.writes), 'Suite declares its actual write boundary');
    assert.ok(suite.requires.includes('wslc-review-0.4.0'));
    assert.ok(suite.human.length > 0);
    assert.ok(Array.isArray(suite.uncovered));
    assert.ok(suite.id === 'q1' || suite.uncovered.length > 0, 'Existing suites retain their uncovered-requirement records');
  }
  assert.equal(registry.q1.mutations, false);
  assert.equal(registry.i403.mutations, true);
  assert.equal(registry.i403.id, 'i403');
  assert.deepEqual(registry.i403.scenarios.map(s => [s.id, s.writes]), [
    ['i403-closed-and-empty', false], ['i403-two-and-many-resize', false], ['i403-primary-workflows', false], ['i403-keyboard-and-guarded-dispatch', true],
    ['i403-grid-replacement', false], ['i403-mobile-native-and-forced-colors', false]
  ]);
  assert.ok(registry.i403.scenarios.every(scenario => scenario.requirements.length > 0));
  assert.deepEqual(registry.q1.uncovered, []);
  assert.ok(registry.q1.scenarios.every(scenario => scenario.writes === false && scenario.requirements.length > 0));
  // These IDs are report/requirement identities, and writes controls read-only
  // execution. Assert the supported contract independently of scenario source.
  assert.deepEqual(registry.pr410.scenarios.map(s => [s.id, s.writes]), [
    ['staff-shell', false], ['confirmation-desktop-mobile-zoom', false],
    ['purchase-reminder-on', true], ['purchase-reminder-off', true],
    ['purchase-verified-autohold', true], ['purchase-verified-no-hold', true],
    ['purchase-double-submit', true], ['reject-template', true],
    ['dirty-editor-navigation', false], ['stale-draft-consent', false],
    ['revert-preserves-unrelated-draft', false], ['profile-navigation-and-history', false],
    ['competing-navigation-consent', false], ['settings-discard', false],
    ['settings-reset-approved', true], ['profile-uncertain-recovery', false], ['beforeunload', false],
    ['close-reopen-and-claim', true], ['stale-purchase-failure', true],
    ['unverified-bib-blocked', false], ['additional-copy-close-reopen', true]
  ]);
});

test('D1 registers read-only coverage independently of its explicitly authorized close/reopen scenario', () => {
  const suite = require('./suites/d1.cjs');
  assert.equal(suite.id, 'd1');
  assert.equal(suite.mutations, true);
  assert.deepEqual(suite.scenarios.map(s => [s.id, s.writes]), [
    ['d1-toolbar-layout', false], ['d1-action-focus', false], ['d1-research-patron', false],
    ['d1-closed-locks', false], ['d1-activity', false], ['d1-close-reopen-refresh', true]
  ]);
});

test('i471 read-only selection reports every required write scenario NOT RUN without executing setup', async t => {
  const root = await temporary(t), directory = path.join(root, 'i471-read-only'); await fs.mkdir(directory);
  const suite = require('./suites/i471.cjs'), executed = [];
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' },
    requiredScenarioIds: suite.scenarios.map(row => row.id), scenarios: [] };
  await runScenarios({ suite: { ...suite, scenarios: suite.scenarios.map(row => ({ ...row,
    run: async () => { assert.equal(row.writes, false); executed.push(row.id); } })) }, options: { readOnly: true },
    environment: environment(root), browser: fakeBrowser(), report, directory, verify: async () => environment(root) });
  assert.deepEqual(executed, ['i471-empty-format-prerequisite']);
  assert.deepEqual(report.scenarios.filter(row => row.status === 'NOT RUN').map(row => row.id),
    suite.scenarios.filter(row => row.writes).map(row => row.id));
  assert.equal(exitCode(report), 2);
  assert.equal(JSON.parse(await fs.readFile(path.join(directory, 'results.json'), 'utf8')).automatedFunctionalAcceptance, 'INCOMPLETE');
});

test('D1 admits only pristine synthetic closed/protected/history fixture states', () => {
  for (const [key, status, events, protectedHold, closeReason] of [
    ['d1-closed', 'closed', 0, false, 'rejected'], ['d1-protected', 'closed', 0, true, 'hold_completed'],
    ['d1-history', 'suggestion', 3, false, null]
  ]) {
    const row = { id: '9007199254740993', key: 'acceptance-pr410-' + key, status, events,
      holdHistoryRetained: protectedHold, closeReason, pristineNotes: true, identityBound: true };
    assert.equal(requireFixture({ titles: [row] }, key), row);
    for (const change of [{ identityBound: false }, { pristineNotes: false }, { events: events + 1 },
      { holdHistoryRetained: !protectedHold }, { status: 'pending_hold' }, { closeReason: 'changed' }]) {
      assert.throws(() => requireFixture({ titles: [{ ...row, ...change }] }, key), /consumed or has changed/);
    }
    assert.throws(() => requireFixture({ titles: [row, row] }, key), /ambiguous/);
    assert.throws(() => requireFixture({ titles: [{ ...row, id: 'not-an-id' }] }, key), /authoritative request identity/);
  }
});

const smokeArgs = (...extra) => ['--suite', 'smoke', '--expected-commit', appSha, ...extra];
test('default smoke verifies isolation before running both registered scenarios without a write flag', async t => {
  const root = await temporary(t);
  let verified = false;
  const outcome = await run(smokeArgs(), dependencies(root, {
    verifyEnvironment: async () => { verified = true; return environment(root); },
    authenticate: async () => fakeBrowser(),
    runScenarios: async ({ suite, options, report }) => {
      assert.equal(verified, true); assert.equal(options.allowWrites, true);
      report.scenarios = suite.scenarios.map(s => result(s, 'PASS'));
    }
  }));
  assert.equal(outcome.code, 0);
  assert.deepEqual(outcome.report.scenarios.filter(s => s.category === 'functional').map(s => s.status), ['PASS', 'PASS']);
});

test('browser cleanup failure blocks completion without erasing successful functional checks', async t => {
  const root = await temporary(t);
  const outcome = await run(smokeArgs(), dependencies(root, {
    authenticate: async () => ({ ...fakeBrowser(), close: async () => { throw new Error('Profile removal failed'); } }),
    runScenarios: async ({ suite, report }) => { report.scenarios = suite.scenarios.map(s => result(s, 'PASS')); }
  }));
  assert.equal(outcome.code, 2);
  assert.deepEqual(outcome.report.scenarios.filter(s => s.category === 'functional').map(s => s.status), ['PASS', 'PASS', 'BLOCKED']);
});

test('read-only smoke reports a browser assertion as FAIL and never starts the mutating scenario', async t => {
  const root = await temporary(t);
  let navigations = 0, verifications = 0;
  const browser = fakeBrowser({ goto: async () => {
    navigations++;
    assert.fail('Staff workspace must be visible');
  } });
  const outcome = await run(smokeArgs('--read-only'), dependencies(root, {
    authenticate: async () => browser,
    verifyEnvironment: async () => { verifications++; return environment(root); }
  }));
  const rows = outcome.report.scenarios.filter(s => s.category === 'functional');
  assert.deepEqual(rows.map(s => [s.id, s.status]), [
    ['smoke-authenticated-review', 'FAIL'], ['smoke-synthetic-close', 'NOT RUN']
  ]);
  assert.deepEqual(rows.map(s => s.requirements), require('./suites/smoke.cjs').scenarios.map(s => s.requirements));
  assert.equal(navigations, 1);
  assert.equal(verifications, 2, 'Only initial environment and read-only scenario verification');
  assert.equal(rows[1].executed, false);
  assert.equal(outcome.code, 2, 'A skipped required mutation keeps acceptance incomplete even with another functional FAIL');
  assert.equal(outcome.report.automatedFunctionalAcceptance, 'FAIL');
  assert.ok(await fs.stat(path.join(outcome.directory, 'smoke-authenticated-review-failure-trace.json')));
});

test('a missing closure fixture blocks that smoke scenario before opening or authorizing it', async t => {
  const root = await temporary(t);
  const browser = fakeBrowser({ goto: async () => { throw new Blocked('Read-only browser checkpoint unavailable.'); } });
  const outcome = await run(smokeArgs('--allow-review-writes'), dependencies(root, { authenticate: async () => browser }));
  const close = outcome.report.scenarios.find(s => s.id === 'smoke-synthetic-close');
  assert.equal(close.status, 'BLOCKED');
  assert.equal(close.executed, false);
  assert.match(close.detail, /Fixture close-reopen is missing or ambiguous/);
  assert.equal(outcome.code, 2);
  assert.equal(outcome.report.automatedFunctionalAcceptance, 'BLOCKED');
  const trace = JSON.parse(await fs.readFile(path.join(outcome.directory, close.diagnostics), 'utf8'));
  assert.deepEqual(trace.mutations, []);
});

test('shared reports index screenshot locations while preserving automated PASS and incomplete human acceptance', async t => {
  const root = await temporary(t);
  const { createReport, writeReport } = require('./helpers/reporting.cjs');
  const suite = require('./suites/smoke.cjs');
  const { directory, report } = await createReport(root, suite.id, appSha, toolSha, false);
  report.environment.status = 'PASS'; report.authentication.status = 'PASS';
  report.requiredScenarioIds = suite.scenarios.map(s => s.id);
  report.scenarios = suite.scenarios.map(s => result(s, 'PASS'));
  const filenames = suite.scenarios.map(s => s.id + '.png');
  for (const filename of filenames) await fs.writeFile(path.join(directory, filename), 'synthetic report-index test');
  await writeReport(directory, report);
  const saved = JSON.parse(await fs.readFile(path.join(directory, 'results.json'), 'utf8'));
  assert.deepEqual(saved.screenshots, filenames);
  assert.equal(saved.automatedFunctionalAcceptance, 'PASS');
  assert.equal(saved.overallAcceptance, 'INCOMPLETE');
  const markdown = await fs.readFile(path.join(directory, 'report.md'), 'utf8');
  for (const filename of filenames) assert.ok(markdown.includes('[' + filename + '](' + filename + ')'));
});

function capturePage() {
  const page = { address: 'https://localhost:8080/staff/?scope=2', visible: true, captures: [], size: { width: 1280, height: 900 },
    url: () => page.address, viewportSize: () => page.size,
    locator: () => ({ isVisible: async () => page.visible }),
    screenshot: async options => {
      page.captures.push(options);
      const buffer = Buffer.alloc(24);
      Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]).copy(buffer);
      buffer.writeUInt32BE(page.size.width, 16); buffer.writeUInt32BE(page.size.height, 20);
      return buffer;
    } };
  return page;
}

test('synthetic screenshots default to the actual unmasked viewport and retain explicit page and corrected crop options', async t => {
  const directory = await temporary(t), page = capturePage();
  let checks = 0;
  registerScreenshotPage(page, async () => { checks++; return environment(); });
  const viewport = await screenshot(page, directory, 'purchase-reminder-on-review');
  assert.deepEqual(page.captures[0], { type: 'png', fullPage: false });
  assert.deepEqual(await fs.readFile(path.join(directory, 'purchase-reminder-on-review.png')), viewport);
  await screenshot(page, directory, 'explicit-page', { fullPage: true });
  assert.deepEqual(page.captures[1], { type: 'png', fullPage: true });
  page.size = { width: 1040, height: 320 };
  const clip = { x: 120, y: 80, width: 1040, height: 320 };
  await screenshot(page, directory, 'confirmation-desktop-mobile-zoom-zoom-dialog', { clip });
  assert.deepEqual(page.captures[2], { type: 'png', fullPage: false, clip });
  assert.equal(checks, 3, 'Every capture revalidates the runtime and reviewer');
  await assert.rejects(screenshot(page, directory, 'unsupported', { mask: [] }), /Only viewport/);
  assert.equal(page.captures.length, 3, 'No masking override is accepted');
});

test('screenshots fail closed for unverified runtimes, external origins, authentication pages and capture races', async t => {
  const directory = await temporary(t), page = capturePage();
  await assert.rejects(screenshot(page, directory, 'unbound'), Blocked);
  for (const mutate of [
    r => { r.aspnetEnvironment = 'Production'; }, r => { r.evidence.database = 'production'; },
    r => { r.authenticationMode = 'Entra'; }, r => { r.evidence.deterministicProviders = false; },
    r => { r.evidence.deliveryMode = 'postmark'; }
  ]) {
    const current = runtime(); mutate(current);
    registerScreenshotPage(page, () => verifyEnvironment(repository, appSha,
      { runtimeEvidence: async () => current, health: async () => {} }));
    await assert.rejects(screenshot(page, directory, 'invalid-runtime'), Blocked);
  }
  registerScreenshotPage(page, async () => { throw new Blocked('Synthetic reviewer changed.'); });
  await assert.rejects(screenshot(page, directory, 'invalid-reviewer'), /reviewer changed/);
  registerScreenshotPage(page, async () => ({ ...environment(), origin: 'https://example.org' }));
  await assert.rejects(screenshot(page, directory, 'wrong-environment-origin'), Blocked);
  registerScreenshotPage(page, async () => environment());
  for (const address of ['https://example.org/staff/', 'http://localhost:8080/staff/', 'https://localhost:8080/_review/auth/session']) {
    page.address = address;
    await assert.rejects(screenshot(page, directory, 'wrong-page'), Blocked);
  }
  page.address = 'https://localhost:8080/staff/'; page.visible = false;
  await assert.rejects(screenshot(page, directory, 'signed-out'), Blocked);
  assert.equal(page.captures.length, 0);
  page.visible = true;
  const capture = page.screenshot;
  page.screenshot = async options => { const pixels = await capture(options); page.address = 'https://example.org/'; return pixels; };
  await assert.rejects(screenshot(page, directory, 'navigation-race'), Blocked);
  assert.deepEqual(await fs.readdir(directory), [], 'No unverified pixels were exported');
});

test('native viewport capture retains runtime guards, actual zoom and dimensions without document clipping', async t => {
  const directory = await temporary(t), page = capturePage(), calls = [];
  const pixels = await page.screenshot({});
  let zoom = 2, checks = 0, detached = 0;
  page.screenshot = async () => { throw new Error('Native capture must not use a document clip'); };
  page.context = () => ({ newCDPSession: async () => ({ send: async (method, options) => {
    calls.push([method, options]);
    return method === 'Page.getLayoutMetrics' ? { visualViewport: { zoom } } : { data: pixels.toString('base64') };
  }, detach: async () => { detached++; } }) });
  await assert.rejects(screenshot(page, directory, 'unverified', { nativeViewport: true }), /no verified synthetic/);
  registerScreenshotPage(page, async () => { checks++; return environment(); });
  await screenshot(page, directory, 'native', { nativeViewport: true });
  assert.equal(checks, 1);
  assert.deepEqual(calls, [['Page.getLayoutMetrics', undefined], ['Page.captureScreenshot', {
    format: 'png', captureBeyondViewport: false
  }]]);
  assert.equal(detached, 1);
  zoom = 1;
  await assert.rejects(screenshot(page, directory, 'wrong-zoom', { nativeViewport: true }), /actual 200 percent/);
  assert.equal(detached, 2);
  zoom = 2; pixels.writeUInt32BE(640, 16);
  await assert.rejects(screenshot(page, directory, 'wrong-size', { nativeViewport: true }), /Screenshot dimensions/);
  await assert.rejects(screenshot(page, directory, 'invalid-crop', { nativeViewport: true, fullPage: true }), /cannot be combined/);
  assert.deepEqual(await fs.readdir(directory), ['native.png']);
});

test('wrong screenshot dimensions fail the scenario and retain failure reporting without exporting misleading evidence', async t => {
  const directory = await temporary(t), page = capturePage();
  const capture = page.screenshot;
  page.screenshot = async options => { const pixels = await capture(options); pixels.writeUInt32BE(640, 16); return pixels; };
  const browser = fakeBrowser(page);
  let scenarioPage;
  const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' },
    requiredScenarioIds: ['clipped-evidence'], scenarios: [] };
  await runScenarios({ suite: { scenarios: [{ id: 'clipped-evidence', requirements: ['Complete viewport evidence'],
    run: async ({ page }) => { scenarioPage = page; await screenshot(page, directory, 'clipped-evidence'); } }] },
    options: { readOnly: true }, environment: environment(), browser, report, directory, verify: async () => environment() });
  assert.equal(report.scenarios[0].status, 'FAIL');
  assert.equal(exitCode(report), 1);
  assert.match(report.scenarios[0].assertion.label, /Screenshot dimensions/);
  assert.ok(await fs.stat(path.join(directory, 'clipped-evidence-failure-trace.json')));
  assert.deepEqual(JSON.parse(await fs.readFile(path.join(directory, 'results.json'), 'utf8')).screenshots, []);
  await assert.rejects(screenshot(scenarioPage, directory, 'revoked'), /no verified synthetic/);
});

test('capture uses the runner verifier and blocks export when the pinned reviewer changes', async t => {
  const root = await temporary(t), calls = [], browser = fakeBrowser();
  let revoked = false;
  browser.verifyReviewer = async () => { calls.push('staff'); if (revoked) throw new Blocked('Synthetic reviewer changed.'); };
  const outcome = await run(args('--read-only'), dependencies(root, {
    authenticate: async () => browser,
    verifyEnvironment: async () => { calls.push('proof'); return environment(); },
    runScenarios: async ({ verify, directory }) => {
      const page = capturePage(); registerScreenshotPage(page, verify);
      calls.length = 0;
      await screenshot(page, directory, 'verified');
      assert.deepEqual(calls, ['staff', 'proof']);
      calls.length = 0; revoked = true;
      await assert.rejects(screenshot(page, directory, 'revoked'), /reviewer changed/);
      assert.deepEqual(calls, ['staff']);
      assert.equal(page.captures.length, 1);
      assert.deepEqual((await fs.readdir(directory)).filter(name => name.endsWith('.png')), ['verified.png']);
    }
  }));
  assert.equal(outcome.report.authentication.status, 'PASS');
});
