'use strict';
const path = require('node:path');
const fs = require('node:fs/promises');
const { execFileSync } = require('node:child_process');
const registry = require('./suites/index.cjs');
const { verifyEnvironment, must } = require('./helpers/environment.cjs');
const { openReviewerBrowser } = require('./helpers/authentication.cjs');
const { screenshot, registerScreenshotPage } = require('./helpers/accessibility.cjs');
const { Blocked, safeError, assertionDetails, result, exitCode, createReport, writeReport } = require('./helpers/reporting.cjs');

function parse(argv) {
  const options = { suite: '', expectedCommit: '', auth: 'local', allowWrites: false, readOnly: false };
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i];
    if (['--suite', '--expected-commit'].includes(flag)) {
      if (!argv[i + 1] || argv[i + 1].startsWith('--')) throw new Blocked('Missing value for ' + flag + '.');
      const value = argv[++i];
      if (flag === '--suite') options.suite = value;
      if (flag === '--expected-commit') options.expectedCommit = value;
    } else if (flag === '--allow-review-writes') options.allowWrites = true;
    else if (flag === '--read-only') options.readOnly = true;
    else throw new Blocked('Unknown acceptance option.');
  }
  must(options.suite && /^[a-z0-9-]+$/.test(options.suite), 'Select an acceptance suite, for example --suite pr410.');
  must(/^[a-f0-9]{40}$/.test(options.expectedCommit), 'ExpectedCommit must be a full lowercase application SHA.');
  must(!options.readOnly || !options.allowWrites, 'ReadOnly and AllowReviewWrites are mutually exclusive.');
  return options;
}
function toolingIdentity(root) {
  if (process.env.ASAP_ACCEPTANCE_CONTAINER === '1') {
    const identity = JSON.parse(require('node:fs').readFileSync('/runner/tooling.json', 'utf8'));
    must(/^[a-f0-9]{40}$/.test(identity.commit) && typeof identity.dirty === 'boolean', 'Acceptance image tooling identity is invalid.');
    return identity;
  }
  return {
    commit: execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true }).trim(),
    dirty: !!execFileSync('git', ['status', '--porcelain', '--untracked-files=normal'], { cwd: root, encoding: 'utf8', windowsHide: true }).trim()
  };
}
async function runScenarios({ suite, options, environment, browser, report, directory, verify }) {
  // Reject external requests. A session expiry is a blocker, never a new login trace.
  const guards = { errors: [], expectUnload: false, unloadCount: 0 };
  let active = null;
  const settingsResetPath = '/api/asap/staff/settings/reset';
  const requestGuard = async route => {
    const request = route.request(), url = new URL(request.url());
    if (options.signal?.aborted) return route.abort('blockedbyclient');
    if (url.origin !== environment.origin) {
      guards.errors.push('External request blocked after authentication');
      return route.abort('blockedbyclient');
    }
    const settingsReset = url.pathname === settingsResetPath;
    if (settingsReset || !['GET', 'HEAD', 'OPTIONS'].includes(request.method())) {
      const match = url.pathname.match(/^\/api\/asap\/staff\/(title-requests|additional-copies)\/([1-9][0-9]*)\/(action|close|reopen)$/);
      let resetVersion;
      if (settingsReset) {
        try {
          const body = request.postDataJSON();
          if (body && Object.keys(body).length === 1) resetVersion = body.version;
        } catch { /* A malformed body cannot authorize a reset. */ }
      }
      const allowed = settingsReset
        ? url.search === '?organizationId=2' && active?.settingsReset &&
          !active.settingsReset.submitted && resetVersion === active.settingsReset.version
        : match && active?.allowedFixtures.has(match[1] + '/' + match[2]);
      if (!options.allowWrites || options.readOnly || !active?.writes || request.method() !== 'POST' || !allowed) {
        guards.errors.push('Unexpected mutation blocked');
        return route.abort('blockedbyclient');
      }
      // Reserve the sole submission before awaiting identity/runtime checks so
      // simultaneous requests cannot both consume the same authorization.
      if (settingsReset) active.settingsReset.submitted = true;
      // Validate the local database, providers and authorized staff again at the write boundary.
      // The page request carries the application's own session and antiforgery header.
      try { await verify(); }
      catch {
        guards.blocker = 'Review runtime unavailable before mutation';
        guards.errors.push(guards.blocker);
        return route.abort('blockedbyclient');
      }
      if (settingsReset) active.mutations.push({ path: settingsResetPath, method: 'POST',
        organizationId: 2, version: resetVersion });
    }
    return route.continue();
  };
  await browser.context.route('**/*', requestGuard);
  try {
    // Closing the last headed tab exits Chromium. A context-owned blank tab
    // keeps the authenticated browser alive between isolated scenario pages;
    // context.close() removes it together with the ephemeral profile.
    if (!options.signal?.aborted) await browser.context.newPage();
    await browser.page.close();
    for (const scenario of suite.scenarios) {
      if (options.signal?.aborted) {
        report.scenarios.push(result(scenario, 'BLOCKED', 'Acceptance run was cancelled.'));
        continue;
      }
      if (options.readOnly && scenario.writes) {
        report.scenarios.push(result(scenario, 'NOT RUN', 'Read-only mode; mutation scenario was not executed.'));
        continue;
      }
      const page = await browser.context.newPage();
      const revokeScreenshots = registerScreenshotPage(page, verify);
      const mutations = [], events = [], failuresBefore = guards.errors.length;
      active = { ...scenario, allowedFixtures: new Set(), mutations };
      guards.expectUnload = false; guards.unloadCount = 0;
      guards.blocker = null;
      page.on('pageerror', () => guards.errors.push('Browser page error'));
      page.on('dialog', async dialog => {
        if (dialog.type() === 'beforeunload' && guards.expectUnload) guards.unloadCount++;
        else guards.errors.push('Unexpected native ' + dialog.type() + ' dialog');
        await dialog.dismiss().catch(() => {});
      });
      page.on('request', request => {
        const url = new URL(request.url());
        if (url.origin !== environment.origin || !url.pathname.endsWith('/action') || request.method() !== 'POST') return;
        // Fixed, synthetic action fields only. Never retain request headers or entire bodies.
        let data;
        try { data = request.postDataJSON(); } catch { return; }
        mutations.push({ path: url.pathname, action: data.action, version: data.version,
          emailPurchaseReminder: data.emailPurchaseReminder });
      });
      page.on('response', response => {
        const url = new URL(response.url());
        if (url.origin === environment.origin && /^\/api\/asap\/staff\/(title-requests|additional-copies)\/\d+(\/action|\/close|\/reopen)?$/.test(url.pathname)) {
          events.push({ event: 'application-response', utc: new Date().toISOString(),
            path: url.pathname, method: response.request().method(), status: response.status() });
        }
        if (url.origin === environment.origin && [settingsResetPath, '/api/asap/staff/settings'].includes(url.pathname)) {
          events.push({ event: 'settings-response', utc: new Date().toISOString(),
            path: url.pathname, method: response.request().method(), status: response.status() });
        }
        if (url.origin === environment.origin && response.request().method() === 'POST' &&
            /\/additional-copies\/\d+\/(close|reopen)$/.test(url.pathname)) mutations.push({ path: url.pathname });
      });
      const startedUtc = new Date().toISOString();
      console.log('Running ' + scenario.id);
      events.push({ event: 'scenario-start', utc: startedUtc, id: scenario.id });
      try {
        if (options.signal?.aborted) throw new Blocked('Acceptance run was cancelled.');
        await verify();
        await scenario.run({ id: scenario.id, page, context: browser.context, origin: environment.origin,
          directory, verify, mutations, guards, allowFixture: fixture => active.allowedFixtures.add(
            (fixture.key.endsWith('-copy') ? 'additional-copies/' : 'title-requests/') + fixture.id),
          allowSettingsReset: ({ organizationId, version }) => {
            must(options.allowWrites && !options.readOnly && active.writes,
              'Settings reset requires an explicitly mutating scenario and AllowReviewWrites.');
            must(organizationId === 2 && typeof version === 'string' && /^[A-Za-z0-9+/]{43}=$/.test(version),
              'Settings reset authorization requires Review Library (2) and its authoritative version.');
            must(!active.settingsReset, 'Settings reset may be authorized only once per scenario.');
            active.settingsReset = { version, submitted: false };
          } });
        must(!page.isClosed(), 'Reviewer browser was closed.');
        if (guards.errors.length !== failuresBefore) throw new Error('Browser guard failed');
        report.scenarios.push({ ...result(scenario, 'PASS'), startedUtc, finishedUtc: new Date().toISOString() });
      } catch (error) {
        if (options.signal?.aborted) error = new Blocked('Acceptance run was cancelled.');
        else if (guards.blocker) error = new Blocked(guards.blocker);
        const status = error instanceof Blocked ? 'BLOCKED' : 'FAIL';
        const failure = result(scenario, status, safeError(error));
        failure.startedUtc = startedUtc;
        failure.finishedUtc = new Date().toISOString();
        failure.assertion = assertionDetails(error);
        failure.browserGuards = guards.errors.slice(failuresBefore);
        failure.diagnostics = scenario.id + '-failure-trace.json';
        report.scenarios.push(failure);
        events.push({ event: 'scenario-' + status.toLowerCase(), utc: failure.finishedUtc,
          assertion: failure.assertion, browserGuards: failure.browserGuards });
        // Deliberately network-free step traces: native Playwright archives contain
        // Cookie/Set-Cookie/antiforgery/session responses even after sign-in.
        await fs.writeFile(path.join(directory, failure.diagnostics), JSON.stringify({ schemaVersion: 1,
          kind: 'privacy-safe-application-trace', scenario: scenario.id, events, mutations }, null, 2));
        // Never capture an authentication/redirect page or arbitrary remote page.
        if (!page.isClosed() && new URL(page.url()).origin === environment.origin &&
            new URL(page.url()).pathname.startsWith('/staff/')) {
          await screenshot(page, directory, scenario.id + '-failure').catch(() => {});
        }
      } finally {
        revokeScreenshots();
        active = null;
        await page.close({ runBeforeUnload: false }).catch(() => {});
        await writeReport(directory, report); // Preserve partial results on later browser failure.
      }
    }
  } finally { await browser.context.unroute('**/*', requestGuard).catch(() => {}); }
}
async function run(argv, dependencies = {}) {
  const root = dependencies.root || path.resolve(__dirname, '../..');
  const identity = (dependencies.toolingIdentity || toolingIdentity)(root);
  let options, suite, browser;
  // Allocate results before configuration validation so even early blockers get a report.
  const { directory, report } = await createReport(root, 'unselected', null, identity.commit, identity.dirty);
  try {
    options = parse(argv);
    report.authentication.mode = options.auth;
    options.signal = dependencies.signal;
    report.suite = options.suite; report.expectedApplicationCommit = options.expectedCommit;
    suite = Object.hasOwn(registry, options.suite) ? registry[options.suite] : null;
    must(suite, 'Unknown suite. Registered acceptance suites: ' + Object.keys(registry).join(', ') + '.');
    report.requiredCapabilities = suite.requires;
    report.requiredScenarioIds = suite.scenarios.map(s => s.id);
    report.mode = options.readOnly ? 'read-only' : 'mutating';
    if (suite.mutations && !options.readOnly) {
      must(options.allowWrites, 'Mutating suites require explicit -AllowReviewWrites. Use -ReadOnly for nonmutating checks.');
    }
    let pinned;
    const verify = async () => {
      // The application also rechecks SQL eligibility at the mutation boundary.
      if (browser?.verifyReviewer) await browser.verifyReviewer();
      const current = await (dependencies.verifyEnvironment || verifyEnvironment)(root, options.expectedCommit);
      must(current.identity.toolingCommit === identity.commit, 'Acceptance tooling SHA differs from the running review tooling.');
      must(current.authenticationMode?.toLowerCase() === options.auth, 'Requested authentication mode differs from the review runtime.');
      if (pinned) must(current.origin === pinned.origin &&
        JSON.stringify(current.identity) === JSON.stringify(pinned.identity), 'Review runtime changed during this acceptance run.');
      else pinned = current;
      return current;
    };
    const environment = await verify();
    report.applicationCommit = environment.identity.applicationCommit;
    report.environment = { status: 'PASS', origin: environment.origin, applicationCommit: environment.identity.applicationCommit,
      ...environment.identity,
      detail: 'Built application/tooling revisions, Testing, local authentication, disposable SQL, fixtures and deterministic providers verified.' };

    browser = await (dependencies.authenticate || openReviewerBrowser)(environment.origin, options.signal);
    report.authentication = { mode: options.auth, status: 'PASS', role: browser.role, library: browser.library };
    if (browser.context.browser) report.browser = { name: 'Chromium', version: browser.context.browser()?.version(), platform: process.platform };
    await (dependencies.runScenarios || runScenarios)({ suite, options, environment, browser, report, directory, verify });
  } catch (error) {
    if (report.environment.status !== 'PASS') report.environment = { status: 'BLOCKED', detail: safeError(error) };
    else if (!browser) report.authentication = { mode: options.auth, status: 'BLOCKED', detail: safeError(error) };
    else report.scenarios.push(result({ id: 'browser-execution', requirements: ['runner: browser execution'] }, 'FAIL', safeError(error)));
    if (suite) {
      for (const scenario of suite.scenarios) {
        if (!report.scenarios.some(s => s.id === scenario.id)) report.scenarios.push(result(scenario,
          options.readOnly && scenario.writes ? 'NOT RUN' : 'BLOCKED', safeError(error)));
      }
    }
  } finally {
    if (suite) {
      for (const [id, requirement] of suite.human) report.scenarios.push(result({
        id, requirements: [requirement], category: 'human' }, 'HUMAN REVIEW REQUIRED', 'Actual human observation required; see #398.'));
      for (const [id, requirement, reason] of suite.uncovered) report.scenarios.push(result({
        id, requirements: [requirement], category: 'coverage-gap' }, 'NOT RUN', reason));
    }
    try { await browser?.close(); }
    catch { report.scenarios.push(result({ id: 'browser-cleanup', requirements: ['runner: ephemeral authentication cleanup'] },
      'FAIL', 'Temporary browser profile cleanup failed. Remove the local temporary asap-acceptance directory before sharing this machine.')); }
    await writeReport(directory, report);
  }
  console.log('Application: ' + (report.applicationCommit || '(not validated)'));
  console.log('Tooling:     ' + identity.commit + (identity.dirty ? ' (working tree changes)' : ''));
  console.log('Results:     ' + directory);
  console.log('Automated:   ' + report.automatedFunctionalAcceptance + '; overall acceptance INCOMPLETE');
  if (report.environment.status !== 'PASS') console.log(report.environment.detail);
  return { code: exitCode(report), directory, report };
}
if (require.main === module) {
  const cancellation = new AbortController();
  process.once('SIGINT', () => cancellation.abort());
  process.once('SIGTERM', () => cancellation.abort());
  run(process.argv.slice(2), { signal: cancellation.signal }).then(outcome => { process.exitCode = outcome.code; })
    .catch(() => { console.error('Acceptance runner could not initialize artifacts or Git identity.'); process.exitCode = 2; });
}
module.exports = { parse, run, runScenarios, toolingIdentity };
