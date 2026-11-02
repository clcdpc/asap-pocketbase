const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');
const YAML = require('yaml');

const root = path.resolve(__dirname, '..');
const workflow = YAML.parse(fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8'), { uniqueKeys: true });
const full = workflow.jobs['build-test-package'];
const draft = workflow.jobs['draft-validation'];
const policy = workflow.jobs['validation-policy'];
const gate = workflow.jobs.validation;
const deploy = workflow.jobs['deploy-test-iis'];
const browserJob = workflow.jobs['browser-qualification'];
const reuse = JSON.parse(fs.readFileSync(path.join(root, 'scripts/ci/test-reuse.json'), 'utf8'));

async function main() {
  // Evaluate the actual YAML expressions with GitHub's expression library.
  // This checks policy, not event delivery, queue ordering or cancellation by GitHub.
  const { Lexer, Parser, Evaluator, data } = await import('@actions/expressions');
  function evaluate(expression, context) {
    const source = expression.trim().match(/^\$\{\{([\s\S]*)\}\}$/)?.[1];
    assert.ok(source, `Expected a single Actions expression: ${expression}`);
    // The standalone evaluator does not know the runner's cancellation state.
    // Substitute only that built-in status result; evaluate the remaining actual expression.
    const evaluatedSource = source.replace(/\bcancelled\(\)/g, context.__cancelled === true ? 'true' : 'false');
    const tokens = new Lexer(evaluatedSource).lex().tokens;
    const ast = new Parser(tokens, Object.keys(context), []).parse();
    return new Evaluator(ast, JSON.parse(JSON.stringify(context), data.reviver)).evaluate().coerceString();
  }
  function template(value, context) {
    return String(value).replace(/\$\{\{([\s\S]*?)\}\}/g, (match) => evaluate(match, context));
  }
  async function routed(context, currentPr = structuredClone(context.github.event.pull_request)) {
    const outputs = {};
    let failed = false;
    const [owner, repo] = context.github.repository.split('/');
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    // Run the actual hosted preflight script against a deterministic API boundary.
    await new AsyncFunction('context', 'github', 'core', policy.steps[0].with.script)(
      { eventName: context.github.event_name, payload: context.github.event, ref: context.github.ref, repo: { owner, repo } },
      { rest: { pulls: { get: async (parameters) => {
        assert.equal(context.github.event_name, 'pull_request', 'Non-PR runs must not query a PR');
        assert.deepEqual(parameters, { owner, repo, pull_number: context.github.event.pull_request.number });
        if (currentPr instanceof Error) { throw currentPr; }
        return { data: currentPr };
      } } } },
      { setOutput: (key, value) => { outputs[key] = value; }, setFailed: () => { failed = true; } }
    );
    context.needs = { 'validation-policy': { result: failed ? 'failure' : 'success', outputs }, 'browser-qualification': { result: outputs.mode === 'full' ? 'success' : 'skipped' } };
    return context;
  }
  function pr(action, isDraft, number = 17) {
    return {
      github: {
        workflow: workflow.name, event_name: 'pull_request', repository: 'clcdpc/asap-pocketbase',
        sha: 'a'.repeat(40), ref: `refs/pull/${number}/merge`, run_id: 100, run_attempt: 1,
        event: { action, pull_request: {
          number, state: 'open', draft: isDraft, author_association: 'COLLABORATOR',
          head: { sha: 'b'.repeat(40), repo: { full_name: 'clcdpc/asap-pocketbase' } }
        } }
      },
      inputs: {}, vars: { ASAP_TEST_DEPLOYMENT_ENABLED: 'false' }
    };
  }
  function nonPr(event, runId, ref = 'refs/heads/main', inputs = {}) {
    return {
      github: { workflow: workflow.name, event_name: event, repository: 'clcdpc/asap-pocketbase',
        sha: 'a'.repeat(40), ref, run_id: runId, run_attempt: 1, event: {} },
      inputs, vars: { ASAP_TEST_DEPLOYMENT_ENABLED: 'false' }
    };
  }
  function enabled(job, context) {
    return evaluate(job.if, context) === 'true';
  }
  function step(job, name) {
    const matches = job.steps.filter((candidate) => candidate.name === name);
    assert.equal(matches.length, 1, name);
    return matches[0];
  }
  const bash = process.platform === 'win32'
    ? path.resolve(cp.execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim(), '../../../bin/bash.exe')
    : 'bash';
  function report(context) {
    const selected = step(gate, 'Require successful selected validation');
    const env = Object.fromEntries(Object.entries(selected.env).map(([key, value]) => [key, template(value, context)]));
    const run = cp.spawnSync(bash, ['-c', selected.run], { encoding: 'utf8', env: { ...process.env, ...env } });
    assert.ifError(run.error);
    assert.ok([0, 1].includes(run.status), `Reporting shell failed unexpectedly: ${run.stderr}`);
    return run.status;
  }
  let checks = 0;
  async function check(name, run) {
    await run();
    checks++;
    console.log(`PASS ${name}`);
  }

  await check('explicit PR events and existing push/manual triggers', () => {
    assert.deepEqual(workflow.on.pull_request.types, ['opened', 'reopened', 'synchronize', 'ready_for_review', 'converted_to_draft']);
    assert.deepEqual(workflow.on.push.branches, ['main', 'codex/csharp-port', 'codex/slice-08-test-deployment']);
    assert.deepEqual(workflow.on.push.tags, ['v*.*.*']);
    assert.equal(workflow.on.workflow_dispatch.inputs.force_full_ci.default, false);
    assert.equal(workflow.on.workflow_dispatch.inputs.deploy_test_iis.default, false);
    assert.equal(workflow.on.pull_request_target, undefined);
  });
  for (const [action, isDraft] of [
    ['opened', true], ['opened', false], ['synchronize', true], ['synchronize', false],
    ['ready_for_review', false], ['converted_to_draft', true], ['reopened', true], ['reopened', false]
  ]) {
    await check(`${action}, draft=${isDraft}: exactly the intended validation depth`, async () => {
      const context = await routed(pr(action, isDraft));
      assert.equal(enabled(draft, context), isDraft);
      assert.equal(enabled(full, context), !isDraft);
      assert.equal(enabled(deploy, context), false);
    });
  }
  await check('same-commit draft/ready transitions select new validation and the same cancellation group', async () => {
    const states = [pr('opened', true), pr('ready_for_review', false), pr('converted_to_draft', true), pr('ready_for_review', false)];
    await Promise.all(states.map((context) => routed(context)));
    assert.equal(new Set(states.map((c) => c.github.sha)).size, 1);
    assert.equal(new Set(states.map((c) => template(workflow.concurrency.group, c))).size, 1);
    assert.deepEqual(states.map((c) => enabled(full, c)), [false, true, false, true]);
    assert.deepEqual(states.map((c) => evaluate(gate.name, c)),
      ['Draft CI result', 'Build, test and package', 'Draft CI result', 'Build, test and package']);
    for (const context of states) {
      context.needs['build-test-package'] = { result: enabled(full, context) ? 'success' : 'skipped' };
      context.needs['draft-validation'] = { result: enabled(draft, context) ? 'success' : 'skipped' };
      assert.equal(report(context), 0);
    }
    states.at(-1).needs['build-test-package'].result = 'skipped';
    states.at(-1).needs['draft-validation'].result = 'success';
    assert.equal(report(states.at(-1)), 1, 'Returning to ready requires full execution in that run, even after draft success');
  });
  await check('out-of-order event snapshots select the current server draft state', async () => {
    for (const [eventDraft, currentDraft] of [[true, false], [false, true]]) {
      const context = pr('synchronize', eventDraft);
      const current = structuredClone(context.github.event.pull_request);
      current.draft = currentDraft;
      await routed(context, current);
      assert.equal(enabled(full, context), !currentDraft);
      assert.equal(enabled(draft, context), currentDraft);
      assert.equal(evaluate(gate.name, context), currentDraft ? 'Draft CI result' : 'Build, test and package');
    }
  });
  await check('repeated draft and ready commits share the PR group; different PRs do not', () => {
    for (const isDraft of [true, false]) {
      const before = pr('synchronize', isDraft);
      const after = structuredClone(before);
      after.github.sha = 'c'.repeat(40);
      after.github.event.pull_request.head.sha = 'd'.repeat(40);
      after.github.run_id++;
      assert.equal(template(workflow.concurrency.group, before), template(workflow.concurrency.group, after));
      assert.equal(evaluate(workflow.concurrency['cancel-in-progress'], after), 'true');
    }
    assert.notEqual(template(workflow.concurrency.group, pr('opened', true, 17)),
      template(workflow.concurrency.group, pr('opened', true, 18)));
  });
  await check('manual, push, tag and rerun attempts cannot cross-cancel, even at the same SHA', async () => {
    const contexts = [
      nonPr('workflow_dispatch', 101), nonPr('workflow_dispatch', 102),
      nonPr('push', 103), nonPr('push', 104, 'refs/tags/v1.2.3-test.1')
    ];
    contexts.push(structuredClone(contexts[0]));
    contexts.at(-1).github.run_attempt = 2;
    const groups = contexts.map((c) => template(workflow.concurrency.group, c));
    groups.push(template(workflow.concurrency.group, pr('opened', false)));
    assert.equal(new Set(groups).size, groups.length);
    for (const context of contexts) {
      await routed(context);
      assert.equal(evaluate(workflow.concurrency['cancel-in-progress'], context), 'false');
      assert.equal(enabled(full, context), true);
      assert.equal(enabled(draft, context), false);
    }
  });
  await check('manual requests preserve full validation, force-full input and deployment controls', async () => {
    const context = await routed(nonPr('workflow_dispatch', 105, 'refs/heads/draft-branch', { force_full_ci: true, deploy_test_iis: true }));
    assert.equal(enabled(full, context), true);
    assert.equal(enabled(draft, context), false);
    assert.equal(template(full.env.ASAP_FORCE_FULL_CI, context), 'true');
    assert.equal(template(full.env.ASAP_DEPLOY_TEST_IIS, context), 'true');
    assert.equal(evaluate(gate.name, context), 'Build, test and package');
    context.needs['build-test-package'] = { result: 'success' };
    context.needs['draft-validation'] = { result: 'skipped' };
    context.needs.validation = { result: 'success' };
    assert.equal(report(context), 0);
    assert.equal(enabled(deploy, context), false);
    context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
    assert.equal(enabled(deploy, context), true);
    context.inputs.deploy_test_iis = false;
    assert.equal(enabled(deploy, context), false);
  });
  await check('main pushes validate; test tags retain gated deployment eligibility', async () => {
    const push = nonPr('push', 106);
    const tag = nonPr('push', 107, 'refs/tags/v1.2.3-test.1');
    for (const context of [push, tag]) {
      await routed(context);
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'skipped' };
      context.needs.validation = { result: 'success' };
      assert.equal(enabled(full, context), true);
    }
    assert.equal(enabled(deploy, push), false);
    assert.equal(enabled(deploy, tag), true);
    const release = await routed(nonPr('push', 112, 'refs/tags/v1.2.3'));
    release.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
    release.needs['build-test-package'] = { result: 'success' };
    release.needs.validation = { result: 'success' };
    assert.equal(enabled(full, release), true);
    assert.equal(enabled(deploy, release), false, 'Production release tags qualify tests without test-IIS activation');
    assert.deepEqual(deploy.needs, ['validation-policy', 'build-test-package', 'validation']);
    // Explicit status check bypasses the implicit success() that treats a skipped
    // draft-validation ancestor as a reason to skip an otherwise eligible deployment.
    assert.match(deploy.if, /!\s*cancelled\(\)/);
    assert.match(deploy.if, /needs\.build-test-package\.result\s*==\s*'success'/);
    assert.match(deploy.if, /needs\.validation\.result\s*==\s*'success'/);
    assert.doesNotMatch(deploy.if, /\balways\(\)/);
    assert.equal(deploy['continue-on-error'], undefined);
    assert.deepEqual(deploy['runs-on'], ['self-hosted', 'windows', 'x64', 'asap-test-iis']);
    assert.equal(deploy.environment, 'asap-test-iis');
  });
  await check('tag and requested manual deployment tolerate skipped draft but fail closed on bad prerequisites', async () => {
    for (const context of [
      nonPr('push', 110, 'refs/tags/v1.0.0-test.23'),
      nonPr('workflow_dispatch', 111, 'refs/heads/codex/wave2-s1-workflow-settings', { deploy_test_iis: true })
    ]) {
      await routed(context);
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'skipped' };
      context.needs.validation = { result: 'success' };
      assert.equal(report(context), 0, 'The reporting gate passes with the expected skipped draft branch');
      assert.equal(enabled(deploy, context), true, 'Full CI can deploy despite the skipped draft ancestor');

      for (const dependency of ['validation-policy', 'build-test-package', 'validation']) {
        for (const result of ['failure', 'cancelled', 'skipped', 'unexpected', undefined]) {
          context.needs[dependency].result = result;
          assert.equal(enabled(deploy, context), false, `${dependency}/${result} must block deployment`);
        }
        context.needs[dependency].result = 'success';
      }
      context.__cancelled = true;
      assert.equal(enabled(deploy, context), false, 'Cancelled workflow must never deploy');
      context.__cancelled = false;
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'false';
      assert.equal(enabled(deploy, context), false, 'Repository activation gate remains mandatory');
    }
  });
  await check('draft work is bounded and has no SQL, browser, publish or evidence setup', () => {
    assert.equal(draft['runs-on'], 'clc-linux');
    assert.ok(draft['timeout-minutes'] <= 15);
    assert.deepEqual(draft.permissions, { contents: 'read' });
    assert.equal(draft.needs, 'validation-policy');
    assert.equal(draft.services, undefined);
    for (const candidate of draft.steps) {
      assert.equal(candidate['continue-on-error'], undefined);
      assert.ok(!/docker|playwright|dotnet publish|test:browser|reuse\.py (plan|verify)/i.test(candidate.run || ''));
      assert.ok(!/upload-artifact/.test(candidate.uses || ''));
    }
    const unit = step(draft, 'Run SQL-independent .NET unit and configuration contracts').run;
    assert.equal(unit, 'python3 -B scripts/ci/selection.py run-draft');
    assert.equal(step(draft, 'Run frontend unit tests').run, 'npm test');
    assert.match(step(draft, 'Check CI contracts and test reuse safety').run, /test_\*\.py/);
    for (const job of [full, draft]) {
      assert.equal(step(job, 'Validate workflow configuration').run, 'bash scripts/ci/check-workflows.sh');
      assert.equal(job.steps[0].with.ref, '${{ github.sha }}');
      assert.equal(step(job, 'Verify exact test source').run, 'python3 -B scripts/ci/reuse.py checkout');
    }
  });
  await check('broader release qualification preserves the exact previous test-tag deployment eligibility', async () => {
    for (const [ref, expected] of [
      ['refs/tags/v1.2.3-test.1', true], ['refs/tags/v1.2.3-test.preview', true],
      ['refs/tags/v1.2.3-prod-test.1', true], ['refs/tags/v1.2.3', false],
      ['refs/tags/v1.2.3-TEST.1', false], ['refs/tags/v1.2.3-test', false],
      ['refs/tags/v1.2.3-test.1/other', false], ['refs/tags/vgroup/1.2.3-test.1', false],
      ['refs/heads/v1.2.3-test.1', false]
    ]) {
      const context = await routed(nonPr('push', 125, ref));
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.needs['build-test-package'] = { result: 'success' };
      context.needs.validation = { result: 'success' };
      assert.equal(context.needs['validation-policy'].outputs.test_tag, String(expected), ref);
      assert.equal(enabled(deploy, context), expected, ref);
    }
  });
  await check('full CI retains original evidence checks and current-commit packaging', () => {
    assert.equal(reuse.job, full.name);
    assert.equal(full['runs-on'], 'clc-linux');
    assert.equal(full.needs, 'validation-policy');
    assert.equal(full['continue-on-error'], undefined);
    assert.equal(step(full, 'Restore and build').run, 'dotnet build Asap.sln --configuration Release');
    const safety = step(full, 'Verify current CI safety contracts');
    assert.equal(safety.if, undefined);
    assert.equal(safety.run, 'node tests/run_all.js --ci-safety');
    assert.ok(full.steps.indexOf(safety) < full.steps.indexOf(step(full, 'Find reusable test executions')));
    const verify = step(full, 'Verify required test suites');
    assert.equal(verify.if, '${{ always() }}');
    assert.equal(verify.run, 'python3 -B scripts/ci/reuse.py verify');
    for (const suite of ['dotnet', 'browser', 'frontend']) {
      const executor = suite === 'browser' ? browserJob : full;
      const test = executor.steps.find((candidate) => candidate.id === suite);
      assert.equal(test.if, `\${{ steps.reuse.outputs.${suite} != 'true' }}`);
      assert.equal(test['continue-on-error'], undefined);
      assert.ok(step(executor, `Upload original ${suite} execution evidence`).if.includes("steps.verify.outcome == 'success'"));
    }
    const tests = step(full, 'Run .NET and real SQL tests (non-browser partition)').run;
    assert.equal(tests, 'bash scripts/ci/run-with-performance.sh python3 -B scripts/ci/selection.py run');
    assert.equal(step(full, 'Resolve required test population').run, 'python3 -B scripts/ci/selection.py prepare');
    assert.ok(full.steps.indexOf(step(full, 'Resolve required test population')) < full.steps.indexOf(step(full, 'Find reusable test executions')));
    assert.equal(full.steps[0].with['fetch-depth'], 0, 'Selection must have complete Git ancestry');
    assert.equal(step(full, 'Run frontend unit tests').run, 'bash scripts/ci/run-with-performance.sh npm test -- --ci-remaining');
    assert.equal(step(full, 'Check test reuse safety').run, "python3 -B -m unittest discover -s scripts/ci -p 'test_*.py' -v");
    assert.equal(full.steps.filter((candidate) => /unittest discover/.test(candidate.run || '')).length, 1);
    assert.ok(full.steps.indexOf(step(full, 'Check test reuse safety')) < full.steps.indexOf(step(full, 'Find reusable test executions')));
    assert.equal(step(browserJob, 'Run browser accessibility tests').run, 'bash scripts/ci/run-with-performance.sh npm run test:browser');
    assert.ok(full.steps.indexOf(step(full, 'Run frontend unit tests')) < full.steps.indexOf(step(full, 'Start SQL Server 2022')));
    for (const name of ['Verify publish artifacts', 'Determine exact artifact identity', 'Create exact test-IIS deployment ZIP',
      'Validate test-IIS package without host mutation', 'Upload exact test-IIS deployment artifact']) {
      assert.equal(step(full, name).if, undefined, `${name} must never depend on a reuse HIT`);
    }
    assert.equal(step(full, 'Remove SQL Server test container').if, '${{ always() }}');
    assert.ok(!reuse.exclusions.some((rule) => rule.paths.includes('.github/workflows/dotnet.yml')));
  });
  await check('browser and SQL qualification use independent ephemeral jobs with exact source and isolated credentials', () => {
    assert.equal(browserJob.needs, 'validation-policy');
    assert.equal(browserJob['runs-on'], 'clc-linux');
    assert.equal(browserJob.name, reuse.browser_job);
    assert.equal(step(full, 'Find reusable test executions').env.ASAP_CI_SUITES, 'dotnet,frontend');
    assert.equal(step(full, 'Verify required test suites').env.ASAP_CI_SUITES, 'dotnet,frontend');
    assert.equal(full.env.ASAP_CI_SUITES, undefined);
    assert.equal(step(browserJob, 'Find reusable test executions').env.ASAP_CI_SUITES, 'browser');
    assert.equal(step(browserJob, 'Verify required test suites').env.ASAP_CI_SUITES, 'browser');
    assert.equal(browserJob.env.ASAP_CI_SUITES, undefined);
    assert.ok(!full.steps.some((candidate) => candidate.id === 'browser'));
    for (const job of [full, browserJob]) {
      assert.equal(job.steps[0].with.ref, '${{ github.sha }}');
      assert.match(step(job, 'Generate ephemeral SQL test credentials').run, /openssl rand/);
      assert.equal(step(job, 'Remove SQL Server test container').if, '${{ always() }}');
      assert.equal(step(job, 'Verify required test suites').if, '${{ always() }}');
    }
    assert.equal(step(browserJob, 'Install browser acceptance dependencies').run, 'npx playwright install chromium');
  });
  await check('missing, failed, skipped or cancelled browser qualification cannot make the required gate green', async () => {
    for (const result of ['failure', 'skipped', 'cancelled', undefined, '']) {
      const context = await routed(pr('synchronize', false));
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['browser-qualification'] = { result };
      assert.equal(report(context), 1);
    }
  });
  await check('the reporter always inspects selected execution and runs no PR code', () => {
    assert.equal(gate.if, '${{ always() }}');
    assert.deepEqual(gate.permissions, {});
    assert.deepEqual(gate.needs, ['validation-policy', 'build-test-package', 'browser-qualification', 'draft-validation']);
    assert.equal(gate['runs-on'], 'ubuntu-latest');
    assert.ok(gate.steps.every((candidate) => !candidate.uses));
    assert.equal(gate['continue-on-error'], undefined);
    assert.equal(gate.steps.length, 1);
    const selected = step(gate, 'Require successful selected validation');
    assert.equal(selected.if, undefined);
    assert.equal(selected['continue-on-error'], undefined);
    assert.equal(selected.shell, 'bash');
  });
  await check('successful draft reporting is green under a distinct non-required check name', async () => {
    const context = await routed(pr('converted_to_draft', true));
    context.needs['draft-validation'] = { result: 'success' };
    context.needs['build-test-package'] = { result: 'skipped' };
    assert.equal(report(context), 0);
    assert.equal(evaluate(gate.name, context), 'Draft CI result');
    assert.notEqual(evaluate(gate.name, context), 'Build, test and package');
    assert.equal(enabled(full, context), false);
  });
  await check('failed, cancelled, skipped, missing or unexpected draft results fail despite a successful full result', async () => {
    for (const result of ['failure', 'cancelled', 'skipped', '', undefined, 'unexpected']) {
      const context = await routed(pr('converted_to_draft', true));
      context.needs['draft-validation'] = { result };
      context.needs['build-test-package'] = { result: 'success' };
      assert.equal(evaluate(gate.name, context), 'Draft CI result');
      assert.equal(report(context), 1, `draft/${result}`);
    }
  });
  await check('the actual full-required gate requires successful full execution for PR, push and manual events', async () => {
    for (const event of ['pull_request', 'push', 'workflow_dispatch']) {
      for (const result of ['success', 'failure', 'cancelled', 'skipped', '', undefined, 'unexpected']) {
        const context = await routed(event === 'pull_request' ? pr('ready_for_review', false) : nonPr(event, 108));
        context.needs['build-test-package'] = { result };
        context.needs['draft-validation'] = { result: 'success' };
        assert.equal(evaluate(gate.name, context), 'Build, test and package');
        assert.equal(report(context), result === 'success' ? 0 : 1, `${event}/${result}`);
      }
    }
  });
  await check('unsuccessful policy resolution fails closed in either mode despite both executors claiming success', async () => {
    for (const isDraft of [true, false]) {
      for (const result of ['failure', 'cancelled', 'skipped', '', undefined, 'unexpected']) {
        const context = await routed(pr('synchronize', isDraft));
        context.needs['validation-policy'].result = result;
        context.needs['build-test-package'] = { result: 'success' };
        context.needs['draft-validation'] = { result: 'success' };
        assert.equal(report(context), 1, `policy/${isDraft}/${result}`);
      }
    }
  });
  await check('missing or malformed validation mode fails closed despite both executors claiming success', async () => {
    for (const mode of [undefined, '', 'unknown', 'DRAFT', 'FULL', ' full', 'full ', 'draft\nfull', 'true']) {
      const context = await routed(pr('ready_for_review', false));
      context.needs['validation-policy'].outputs = mode === undefined ? {} : { mode };
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'success' };
      assert.equal(report(context), 1, `mode/${mode}`);
    }
  });
  await check('forks and untrusted authors allocate no trusted execution runner', async () => {
    for (const isDraft of [true, false]) {
      const fork = pr('opened', isDraft);
      fork.github.event.pull_request.head.repo.full_name = 'outsider/asap-pocketbase';
      const external = pr('opened', isDraft);
      external.github.event.pull_request.author_association = 'CONTRIBUTOR';
      for (const context of [fork, external]) {
        await routed(context);
        assert.equal(enabled(full, context), false);
        assert.equal(enabled(draft, context), false);
      }
    }
    assert.deepEqual(workflow.permissions, { contents: 'read', actions: 'read' });
    assert.deepEqual(full.permissions, { contents: 'read', actions: 'read', 'pull-requests': 'read' });
    assert.ok(!JSON.stringify(workflow).includes('secrets.'));
    assert.ok(!deploy.steps.some((candidate) => candidate.uses?.startsWith('actions/checkout')));
  });
  await check('closed, stale, malformed or unreadable current PR metadata fails policy', async () => {
    for (const change of [
      (current) => { current.state = 'closed'; },
      (current) => { current.head.sha = 'e'.repeat(40); },
      (current) => { current.draft = null; }
    ]) {
      const context = pr('ready_for_review', false);
      const current = structuredClone(context.github.event.pull_request);
      change(current);
      await routed(context, current);
      assert.equal(context.needs['validation-policy'].result, 'failure');
      assert.equal(enabled(full, context), false);
      assert.equal(enabled(draft, context), false);
    }
    await assert.rejects(routed(pr('opened', false), new Error('API unavailable')), /API unavailable/);
    assert.equal(policy['runs-on'], 'ubuntu-latest');
    assert.deepEqual(policy.permissions, { 'pull-requests': 'read' });
    assert.equal(policy.steps.length, 1);
    assert.equal(policy.steps[0].uses, 'actions/github-script@v8');
    assert.equal(policy.outputs.mode, '${{ steps.policy.outputs.mode }}');
    assert.ok(!policy.steps[0].with.script.includes('${{'), 'Never interpolate PR input into JavaScript source');
  });
  console.log(`${checks} CI workflow contract checks passed.`);
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
