'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { Blocked, result, exitCode, safeError } = require('./helpers/reporting.cjs');
const { readState, validateContainers, validateEvidence, requireFixture, verifyEnvironment } = require('./helpers/environment.cjs');
const { parse, run, runScenarios, acquireLease } = require('./run.cjs');
const appSha = 'a'.repeat(40), toolSha = 'b'.repeat(40);
const repository = path.resolve(__dirname, '../..');
const clone = value => JSON.parse(JSON.stringify(value));
function state(root) {
  return { Sha: appSha, ShortSha: appSha.slice(0, 12), ProjectName: 'asap-review-' + appSha.slice(0,12),
    Port: 8080, LifecycleState: 'Ready', StatePath: path.join(root, '.review', appSha.slice(0,12)), ToolingVersion: '0.3.0', ToolingSha: toolSha, ViewerPort: 6080 };
}
function containers(s) {
  return ['web', 'sql', 'db-init'].map((name, i) => {
    const project = s.ProjectName;
    return {
      Id: String(i + 1).repeat(64), Image: 'sha256:' + String(i + 1).repeat(64),
      Path: name === 'web' ? '/review/web-entrypoint.sh' : '/other',
      Config: { WorkingDir: '/app', Hostname: name + '-hostname', Labels: { 'com.docker.compose.service': name,
        'com.docker.compose.project': project, 'org.opencontainers.image.revision': s.Sha,
        'org.opencontainers.image.version': s.ToolingVersion, 'org.asap.review.tooling-revision': toolSha }, Env: [
        'ASPNETCORE_ENVIRONMENT=Testing', 'Testing__UseDeterministicPatronProvider=true',
        'ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Asap.ReviewProviders', 'UNRELATED_SECRET=secret-canary'
      ] },
      State: { Running: name !== 'db-init', Status: name === 'db-init' ? 'exited' : 'running', ExitCode: 0 },
      HostConfig: { NetworkMode: project + '_review', Privileged: false, PortBindings: {} },
      NetworkSettings: { Networks: { [project + '_review']: {} }, Ports: name === 'web' ?
        { '8443/tcp': [{ HostIp: '127.0.0.1', HostPort: String(s.Port) }] } : { '1433/tcp': null } },
      Mounts: [{ Type: 'volume', Destination: name === 'sql' ? '/var/opt/mssql' : '/review/config',
        Name: project + '_' + (name === 'sql' ? 'sql-data' : 'runtime-config'), RW: name !== 'web' }]
    };
  });
}
function proof(s = state(repository)) {
  const c = containers(s), browser = clone(c[0]);
  browser.Config.Labels['com.docker.compose.service'] = 'acceptance';
  browser.Config.Labels['org.opencontainers.image.revision'] = s.ToolingSha;
  browser.NetworkSettings.Ports = { '6080/tcp': [{ HostIp: '127.0.0.1', HostPort: String(s.ViewerPort) }] };
  browser.Mounts = [{ Type: 'bind', Destination: '/run/review/control', RW: false }];
  return { schemaVersion: 1, observedUtc: new Date().toISOString(), dockerEndpoint: 'npipe:////./pipe/dockerDesktopLinuxEngine',
    acceptanceRunning: true, state: { ...s, StatePath: '/tmp/asap-review' }, containers: [...c, browser] };
}
function runtime(s = state(repository)) {
  return { identity: { applicationCommit: s.Sha, toolingCommit: s.ToolingSha, toolingVersion: s.ToolingVersion },
    project: s.ProjectName, webHostname: 'web-hostname', evidence: evidence() };
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
  await fs.mkdir(path.join(root, '.review'));
  await fs.mkdir(state(root).StatePath);
  return root;
}
async function writeState(root, value = state(root)) {
  await fs.writeFile(path.join(root, '.review/current.json'), JSON.stringify(value));
}
function environment(root) {
  const s = state(root);
  return { origin: 'https://localhost:8080', state: s, identity: validateContainers(s, containers(s)), evidence: evidence() };
}
const args = (...extra) => ['--suite', 'pr410', '--expected-commit', appSha, ...extra];
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
test('missing, malformed, legacy and nonobject review states fail closed', async t => {
  const root = await temporary(t);
  await assert.rejects(readState(root, appSha), Blocked);
  for (const text of ['{broken', 'null', '[]', '{}', '{"Sha":"' + appSha + '"}']) {
    await fs.writeFile(path.join(root, '.review/current.json'), text);
    await assert.rejects(readState(root, appSha), Blocked);
  }
});
test('Stopped, Failed and Starting review states cannot run', async t => {
  const root = await temporary(t);
  for (const LifecycleState of ['Stopped', 'Failed', 'Starting']) {
    await writeState(root, { ...state(root), LifecycleState });
    await assert.rejects(readState(root, appSha), /Ready/);
  }
});
test('application SHA, managed paths, ports and tooling contracts are exact', async t => {
  const root = await temporary(t); await writeState(root);
  await assert.rejects(readState(root, toolSha), /SHA/);
  await assert.rejects(readState(root, 'HEAD'), /full lowercase/);
  for (const change of [{ StatePath: root }, { Port: '8080' }, { ShortSha: 'wrong' }, { ToolingVersion: '0.1.7' }]) {
    await writeState(root, { ...state(root), ...change });
    await assert.rejects(readState(root, appSha), Blocked);
  }
});
test('missing explicit mutation authorization blocks before environment or browser calls', async t => {
  const root = await temporary(t);
  const outcome = await run(args(), dependencies(root, { verifyEnvironment: () => { throw new Error('must not be called'); } }));
  assert.equal(outcome.code, 2);
  assert.match(outcome.report.environment.detail, /AllowReviewWrites/);
  assert.ok(outcome.report.scenarios.filter(s => s.category === 'functional').every(s => s.status === 'BLOCKED'));
});
test('non-loopback, additional port bindings and published SQL fail closed', () => {
  const s = state(repository);
  for (const address of ['0.0.0.0', '192.0.2.1', '::']) {
    const c = containers(s); c[0].NetworkSettings.Ports['8443/tcp'][0].HostIp = address;
    assert.throws(() => validateContainers(s, c), /loopback/);
  }
  const c = containers(s); c[1].HostConfig.PortBindings = { '1433/tcp': [{ HostIp: '127.0.0.1', HostPort: '1433' }] };
  assert.throws(() => validateContainers(s, c), /SQL/);
});
test('runtime evidence must correlate container project/image/network/volumes/providers', () => {
  const s = state(repository);
  assert.ok(validateContainers(s, containers(s)).webId);
  for (const mutate of [
    c => { c[0].State.Running = false; },
    c => { c[2].State.ExitCode = 1; },
    c => { c[0].Config.Labels['org.opencontainers.image.revision'] = toolSha; },
    c => { c[0].Config.Labels['com.docker.compose.project'] = 'production'; },
    c => { c[0].NetworkSettings.Networks.remote = {}; },
    c => { c[0].Mounts[0].Name = 'unidentified-db'; },
    c => { c[0].Config.Env.push('ConnectionStrings__AsapDatabase=secret-canary'); },
    c => { c[0].Config.Env = []; },
    c => { c[0].HostConfig.Privileged = true; }
  ]) {
    const c = containers(s); mutate(c);
    assert.throws(() => validateContainers(s, c), Blocked);
  }
  for (const change of [{ database: 'production' }, { sqlMajorVersion: 17 }, { deliveryMode: 'postmark' }, { deterministicProviders: false }]) {
    assert.throws(() => validateEvidence({ ...evidence(), ...change }), Blocked);
  }
});
test('full environment validation correlates fresh host evidence, live runtime and trusted health', async t => {
  const root = await temporary(t); await writeState(root);
  let checkedHealth = false;
  const dependencies = { readProof: async () => proof(state(root)), runtimeEvidence: async () => runtime(state(root)),
    health: async () => { checkedHealth = true; } };
  const actual = await verifyEnvironment(root, appSha, dependencies);
  assert.equal(actual.state.Sha, appSha); assert.equal(checkedHealth, true);
  await assert.rejects(verifyEnvironment(root, appSha, { ...dependencies, readProof: async () => ({ ...proof(state(root)), containers: [] }) }), /containers/);
  await assert.rejects(verifyEnvironment(root, appSha, { ...dependencies, health: async () => { throw new Blocked('Health unavailable.'); } }), /Health/);
  await assert.rejects(verifyEnvironment(root, appSha, { ...dependencies, runtimeEvidence: async () => ({ ...runtime(state(root)), webHostname: 'replaced' }) }), /identity/);
  let reads = 0;
  await assert.rejects(verifyEnvironment(root, appSha, { ...dependencies, readProof: async () => ({ ...proof(state(root)),
    observedUtc: new Date(Date.now() - (reads++ ? 60000 : 0)).toISOString() }) }), /stale/);
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
test('independent application/tooling SHAs and check-only NOT RUN are recorded', async t => {
  const root = await temporary(t);
  const a = await run(args('--check-only'), dependencies(root));
  const b = await run(args('--check-only'), dependencies(root));
  assert.equal(a.code, 0); assert.notEqual(a.directory, b.directory);
  assert.equal(a.report.applicationCommit, appSha);
  assert.equal(a.report.toolingCommit, toolSha);
  assert.equal(a.report.authentication.status, 'NOT RUN');
  assert.equal(a.report.automatedFunctionalAcceptance, 'INCOMPLETE');
  assert.ok(a.report.scenarios.filter(s => s.category === 'functional').every(s => s.status === 'NOT RUN'));
  const failed = await run(args('--check-only'), dependencies(root, {
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
function fakeBrowser() {
  const context = { route: async (_pattern, handler) => { context.handler = handler; },
    unroute: async () => {}, newPage: async () => ({
      on: () => {}, close: async () => {}, isClosed: () => false, url: () => 'about:blank'
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
test('existing CI/browser/lifecycle entry points stay unchanged; artifacts are Git ignored', async () => {
  for (const file of ['package.json', 'tests/browser/run.cjs', '.github/workflows/dotnet.yml']) {
    const baseline = execFileSync('git', ['show', '792b039de3010c832ad6aaa1d65ec09807e3410c:' + file], { cwd: repository, encoding: 'utf8' });
    assert.equal((await fs.readFile(path.join(repository, file), 'utf8')).replace(/\r\n/g, '\n'), baseline.replace(/\r\n/g, '\n'));
  }
  assert.match(execFileSync('git', ['check-ignore', '.artifacts/acceptance/run/results.json'], { cwd: repository, encoding: 'utf8' }), /acceptance/);
});
test('CLI rejects incompatible modes, remote targets, unsafe inputs and incomplete SHAs', () => {
  for (const input of [args('--read-only', '--allow-review-writes'), args('--url', 'https://example.org'),
    args('--auth-timeout-seconds', 'NaN'), ['--suite', 'pr410', '--expected-commit', 'HEAD']]) {
    assert.throws(() => parse(input), Blocked);
  }
});
test('remote Docker proof fails before contacting application', async t => {
  const root = await temporary(t); await writeState(root);
  for (const endpoint of ['ssh://review-host', 'tcp://192.0.2.1:2376', 'npipe:////remote/pipe/docker']) {
    let calls = 0;
    await assert.rejects(verifyEnvironment(root, appSha, { readProof: async () => ({ ...proof(state(root)), dockerEndpoint: endpoint }),
      runtimeEvidence: async () => { calls++; } }), /Remote Docker/);
    assert.equal(calls, 0);
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
test('exclusive lease blocks concurrent acceptance without resetting fixtures', async t => {
  const root = await temporary(t);
  const release = await acquireLease(state(root));
  await assert.rejects(acquireLease(state(root)), /lease/);
  await release();
  const releaseAgain = await acquireLease(state(root));
  await releaseAgain();
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
  verify: async () => { if (checks++) throw new Blocked('Host proof revoked.'); return environment(root); } });
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
});
