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
const appSha = 'a'.repeat(40), toolSha = 'b'.repeat(40);
const repository = path.resolve(__dirname, '../..');
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
test('missing explicit mutation authorization blocks before environment or browser calls', async t => {
  const root = await temporary(t);
  const outcome = await run(args(), dependencies(root, { verifyEnvironment: () => { throw new Error('must not be called'); } }));
  assert.equal(outcome.code, 2);
  assert.match(outcome.report.environment.detail, /AllowReviewWrites/);
  assert.ok(outcome.report.scenarios.filter(s => s.category === 'functional').every(s => s.status === 'BLOCKED'));
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
  assert.equal(a.code, 0); assert.notEqual(a.directory, b.directory);
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
  assert.equal(exitCode(report), 1);
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
  assert.equal(exitCode(report), 1);
  assert.ok(await fs.stat(path.join(directory, 'fails-failure-trace.json')));
  const trace = JSON.parse(await fs.readFile(path.join(directory, 'fails-failure-trace.json'), 'utf8'));
  assert.equal(trace.events.at(-1).assertion.expected, 2);
  assert.equal(trace.events.at(-1).assertion.actual, 1);
  assert.match(trace.events.at(-1).assertion.label, /Action count/);
  assert.ok(await fs.stat(path.join(directory, 'results.json')));
});
test('mutation guard blocks cross-kind IDs, other scenario fixtures, external requests and read-only writes', async t => {
  const root = await temporary(t), directory = path.join(root, 'guards'); await fs.mkdir(directory);
  for (const pathname of ['/api/asap/staff/additional-copies/27/close', '/api/asap/staff/title-requests/999/action',
    'https://example.org/api/asap/staff/title-requests/27/action']) {
    const browser = fakeBrowser();
    const report = { environment: { status: 'PASS' }, authentication: { status: 'PASS' }, scenarios: [] };
    let aborted = false;
    const suite = { scenarios: [{ id: 'guarded', writes: true, requirements: ['isolation'], run: async t => {
      t.allowFixture(evidence().titles[0]);
      await browser.context.handler({ request: () => ({ url: () => new URL(pathname, t.origin).href, method: () => 'POST' }),
        abort: async () => { aborted = true; }, continue: async () => {} });
    } }] };
    await runScenarios({ suite, options: { allowWrites: true }, environment: environment(root),
      browser, report, directory, verify: async () => environment(root) });
    assert.equal(aborted, true); assert.equal(report.scenarios[0].status, 'FAIL');
  }
});
test('existing frontend/browser entry points and full CI guard remain; artifacts are Git ignored', async () => {
  for (const file of ['package.json', 'tests/browser/run.cjs']) {
    const baseline = execFileSync('git', ['show', '792b039de3010c832ad6aaa1d65ec09807e3410c:' + file], { cwd: repository, encoding: 'utf8' });
    assert.equal((await fs.readFile(path.join(repository, file), 'utf8')).replace(/\r\n/g, '\n'), baseline.replace(/\r\n/g, '\n'));
  }
  const workflow = await fs.readFile(path.join(repository, '.github/workflows/dotnet.yml'), 'utf8');
  assert.match(workflow, /dotnet test --project tests\/Asap.Tests\/Asap.Tests.csproj/);
  assert.match(workflow, /--minimum-expected-tests 1439/);
  assert.match(workflow, /run: npm test/);
  assert.match(workflow, /run: npm run test:browser/);
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
    t.allowFixture(evidence().titles[0]);
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
    t.allowFixture(evidence().titles[0]);
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

test('smoke registers exactly two scenarios with explicit write metadata and preserves pr410', async () => {
  const registry = require('./suites/index.cjs');
  assert.deepEqual(Object.keys(registry), ['pr410', 'smoke']);
  assert.equal(registry.smoke.id, 'smoke');
  assert.equal(registry.smoke.mutations, true);
  assert.deepEqual(registry.smoke.scenarios.map(s => [s.id, s.writes]), [
    ['smoke-authenticated-review', false], ['smoke-synthetic-close', true]
  ]);
  for (const suite of Object.values(registry)) {
    assert.ok(suite.requires.includes('wslc-review-0.4.0'));
    assert.ok(suite.human.length > 0);
    assert.ok(suite.uncovered.length > 0);
  }
  const baseline = execFileSync('git', ['show', '6f928007edf53bb48aeaf9018357aacc2bdaf8dc:tests/acceptance/suites/pr410.cjs'],
    { cwd: repository, encoding: 'utf8' });
  const current = await fs.readFile(path.join(__dirname, 'suites/pr410.cjs'), 'utf8');
  assert.equal(current.replace(/\r\n/g, '\n'), baseline.replace(/\r\n/g, '\n')
    .replace('docker-review-0.2.0', 'wslc-review-0.4.0').replace('entra-super-admin', 'authorized-super-admin'));
  assert.equal(registry.pr410.scenarios.length, 20);
});

const smokeArgs = (...extra) => ['--suite', 'smoke', '--expected-commit', appSha, ...extra];
test('smoke requires explicit mutation permission before environment verification', async t => {
  const root = await temporary(t);
  const outcome = await run(smokeArgs(), dependencies(root, {
    verifyEnvironment: () => { throw new Error('Unauthorized run must not verify or authenticate'); }
  }));
  assert.equal(outcome.code, 2);
  assert.match(outcome.report.environment.detail, /AllowReviewWrites/);
  assert.deepEqual(outcome.report.scenarios.filter(s => s.category === 'functional').map(s => s.status), ['BLOCKED', 'BLOCKED']);
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
  assert.equal(outcome.code, 1);
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
