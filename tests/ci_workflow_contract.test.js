const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');
const YAML = require('yaml');

const root = path.resolve(__dirname, '..');
const workflow = YAML.parse(fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8'), { uniqueKeys: true });
const full = workflow.jobs['build-test-package'];
const draft = workflow.jobs['draft-validation'];
const gate = workflow.jobs.validation;
const deploy = workflow.jobs['deploy-test-iis'];
const reuse = JSON.parse(fs.readFileSync(path.join(root, 'scripts/ci/test-reuse.json'), 'utf8'));

async function main() {
  // Evaluate the actual YAML expressions with GitHub's expression library.
  // This checks policy, not event delivery, queue ordering or cancellation by GitHub.
  const { Lexer, Parser, Evaluator, data } = await import('@actions/expressions');
  function evaluate(expression, context) {
    const source = expression.trim().match(/^\$\{\{([\s\S]*)\}\}$/)?.[1];
    assert.ok(source, `Expected a single Actions expression: ${expression}`);
    const tokens = new Lexer(source).lex().tokens;
    const ast = new Parser(tokens, Object.keys(context), []).parse();
    return new Evaluator(ast, JSON.parse(JSON.stringify(context), data.reviver)).evaluate().coerceString();
  }
  function template(value, context) {
    return String(value).replace(/\$\{\{([\s\S]*?)\}\}/g, (match) => evaluate(match, context));
  }
  function pr(action, isDraft, number = 17) {
    return {
      github: {
        workflow: workflow.name, event_name: 'pull_request', repository: 'clcdpc/asap-pocketbase',
        sha: 'a'.repeat(40), ref: `refs/pull/${number}/merge`, run_id: 100, run_attempt: 1,
        event: { action, pull_request: {
          number, draft: isDraft, author_association: 'COLLABORATOR',
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
  let checks = 0;
  function check(name, run) {
    run();
    checks++;
    console.log(`PASS ${name}`);
  }

  check('explicit PR events and existing push/manual triggers', () => {
    assert.deepEqual(workflow.on.pull_request.types, ['opened', 'reopened', 'synchronize', 'ready_for_review', 'converted_to_draft']);
    assert.deepEqual(workflow.on.push.branches, ['main', 'codex/csharp-port', 'codex/slice-08-test-deployment']);
    assert.deepEqual(workflow.on.push.tags, ['v*.*.*-test.*']);
    assert.equal(workflow.on.workflow_dispatch.inputs.force_full_ci.default, false);
    assert.equal(workflow.on.workflow_dispatch.inputs.deploy_test_iis.default, false);
    assert.equal(workflow.on.pull_request_target, undefined);
  });
  for (const [action, isDraft] of [
    ['opened', true], ['opened', false], ['synchronize', true], ['synchronize', false],
    ['ready_for_review', false], ['converted_to_draft', true], ['reopened', true], ['reopened', false]
  ]) {
    check(`${action}, draft=${isDraft}: exactly the intended validation depth`, () => {
      const context = pr(action, isDraft);
      assert.equal(enabled(draft, context), isDraft);
      assert.equal(enabled(full, context), !isDraft);
      assert.equal(enabled(deploy, context), false);
    });
  }
  check('same-commit draft/ready transitions select new validation and the same cancellation group', () => {
    const states = [pr('opened', true), pr('ready_for_review', false), pr('converted_to_draft', true)];
    assert.equal(new Set(states.map((c) => c.github.sha)).size, 1);
    assert.equal(new Set(states.map((c) => template(workflow.concurrency.group, c))).size, 1);
    assert.deepEqual(states.map((c) => enabled(full, c)), [false, true, false]);
  });
  check('current draft state selects depth rather than the activity name alone', () => {
    for (const action of workflow.on.pull_request.types) {
      assert.equal(enabled(full, pr(action, false)), true);
      assert.equal(enabled(full, pr(action, true)), false);
      assert.equal(enabled(draft, pr(action, true)), true);
    }
  });
  check('repeated draft and ready commits share the PR group; different PRs do not', () => {
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
  check('manual, push, tag and rerun attempts cannot cross-cancel, even at the same SHA', () => {
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
      assert.equal(evaluate(workflow.concurrency['cancel-in-progress'], context), 'false');
      assert.equal(enabled(full, context), true);
      assert.equal(enabled(draft, context), false);
    }
  });
  check('manual requests preserve full validation, force-full input and deployment controls', () => {
    const context = nonPr('workflow_dispatch', 105, 'refs/heads/draft-branch', { force_full_ci: true, deploy_test_iis: true });
    assert.equal(enabled(full, context), true);
    assert.equal(enabled(draft, context), false);
    assert.equal(template(full.env.ASAP_FORCE_FULL_CI, context), 'true');
    assert.equal(template(full.env.ASAP_DEPLOY_TEST_IIS, context), 'true');
    assert.equal(enabled(deploy, context), false);
    context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
    assert.equal(enabled(deploy, context), true);
    context.inputs.deploy_test_iis = false;
    assert.equal(enabled(deploy, context), false);
  });
  check('main pushes validate; test tags retain gated deployment eligibility', () => {
    const push = nonPr('push', 106);
    const tag = nonPr('push', 107, 'refs/tags/v1.2.3-test.1');
    for (const context of [push, tag]) {
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      assert.equal(enabled(full, context), true);
    }
    assert.equal(enabled(deploy, push), false);
    assert.equal(enabled(deploy, tag), true);
    assert.deepEqual(deploy.needs, ['build-test-package', 'validation']);
    assert.deepEqual(deploy['runs-on'], ['self-hosted', 'windows', 'x64', 'asap-test-iis']);
    assert.equal(deploy.environment, 'asap-test-iis');
  });
  check('draft work is bounded and has no SQL, browser, publish or evidence setup', () => {
    assert.equal(draft['runs-on'], 'clc-linux');
    assert.ok(draft['timeout-minutes'] <= 15);
    assert.deepEqual(draft.permissions, { contents: 'read' });
    assert.equal(draft.services, undefined);
    for (const candidate of draft.steps) {
      assert.equal(candidate['continue-on-error'], undefined);
      assert.ok(!/docker|playwright|dotnet publish|test:browser|reuse\.py (plan|verify)/i.test(candidate.run || ''));
      assert.ok(!/upload-artifact/.test(candidate.uses || ''));
    }
    const unit = step(draft, 'Run SQL-independent .NET unit and configuration contracts').run;
    assert.match(unit, /--minimum-expected-tests 100/);
    assert.match(unit, /--filter 'FullyQualifiedName~Asap\.Tests\.Unit\.\|FullyQualifiedName~Asap\.Tests\.PolicyContractTests\.\|FullyQualifiedName~Asap\.Tests\.AnalyticsContractTests\.'/);
    assert.equal(step(draft, 'Run legacy/frontend unit tests').run, 'npm test');
    assert.match(step(draft, 'Check CI contracts and test reuse safety').run, /test_reuse\*\.py/);
    for (const job of [full, draft]) {
      assert.equal(step(job, 'Validate workflow configuration').run, 'bash scripts/ci/check-workflows.sh');
      assert.equal(job.steps[0].with.ref, '${{ github.sha }}');
      assert.equal(step(job, 'Verify exact test source').run, 'python3 -B scripts/ci/reuse.py checkout');
    }
  });
  check('full CI retains original evidence checks and current-commit packaging', () => {
    assert.equal(reuse.job, full.name);
    assert.equal(full['runs-on'], 'clc-linux');
    assert.equal(full['continue-on-error'], undefined);
    assert.equal(step(full, 'Restore and build').run, 'dotnet build Asap.sln --configuration Release');
    const verify = step(full, 'Verify required test suites');
    assert.equal(verify.if, '${{ always() }}');
    assert.equal(verify.run, 'python3 -B scripts/ci/reuse.py verify');
    for (const suite of ['dotnet', 'browser', 'frontend']) {
      const test = full.steps.find((candidate) => candidate.id === suite);
      assert.equal(test.if, `\${{ steps.reuse.outputs.${suite} != 'true' }}`);
      assert.equal(test['continue-on-error'], undefined);
      assert.ok(step(full, `Upload original ${suite} execution evidence`).if.includes("steps.verify.outcome == 'success'"));
    }
    const tests = step(full, 'Run .NET and real SQL tests (non-browser partition)').run;
    assert.match(tests, /--minimum-expected-tests 1525/);
    assert.match(tests, /bash scripts\/ci\/run-with-performance\.sh/);
    assert.equal(step(full, 'Run browser accessibility tests').run, 'npm run test:browser');
    assert.ok(full.steps.indexOf(step(full, 'Run legacy/frontend unit tests')) < full.steps.indexOf(step(full, 'Start SQL Server 2022')));
    for (const name of ['Verify publish artifacts', 'Determine exact artifact identity', 'Create exact test-IIS deployment ZIP',
      'Validate test-IIS package without host mutation', 'Upload exact test-IIS deployment artifact']) {
      assert.equal(step(full, name).if, undefined, `${name} must never depend on a reuse HIT`);
    }
    assert.equal(step(full, 'Remove SQL Server test container').if, '${{ always() }}');
    assert.ok(!reuse.exclusions.some((rule) => rule.paths.includes('.github/workflows/dotnet.yml')));
  });
  check('the stable full gate always inspects full execution and runs no PR code', () => {
    assert.equal(gate.name, 'Build, test and package');
    assert.equal(gate.if, '${{ always() }}');
    assert.deepEqual(gate.needs, ['build-test-package', 'draft-validation']);
    assert.equal(gate['runs-on'], 'ubuntu-latest');
    assert.ok(gate.steps.every((candidate) => !candidate.uses));
    assert.equal(gate['continue-on-error'], undefined);
  });
  check('execute the actual required-gate shell against success/failure/cancellation/skip and draft transitions', () => {
    const report = step(gate, 'Require successful full validation');
    const bash = process.platform === 'win32'
      ? path.resolve(cp.execFileSync('git', ['--exec-path'], { encoding: 'utf8' }).trim(), '../../../bin/bash.exe')
      : 'bash';
    for (const event of ['pull_request', 'push', 'workflow_dispatch']) {
      for (const result of ['success', 'failure', 'cancelled', 'skipped', '']) {
        const context = event === 'pull_request' ? pr('ready_for_review', false) : nonPr(event, 108);
        context.needs = { 'build-test-package': { result } };
        const env = Object.fromEntries(Object.entries(report.env).map(([key, value]) => [key, template(value, context)]));
        const run = cp.spawnSync(bash, ['-c', report.run], { encoding: 'utf8', env: { ...process.env, ...env } });
        assert.ifError(run.error);
        assert.equal(run.status, result === 'success' ? 0 : 1, `${event}/${result}: ${run.stderr}`);
      }
    }
    for (const state of [true, null]) {
      const context = pr('converted_to_draft', state);
      context.needs = { 'build-test-package': { result: 'success' } };
      const env = Object.fromEntries(Object.entries(report.env).map(([key, value]) => [key, template(value, context)]));
      const run = cp.spawnSync(bash, ['-c', report.run], { encoding: 'utf8', env: { ...process.env, ...env } });
      assert.ifError(run.error);
      assert.equal(run.status, 1, 'Even an earlier successful full result must not turn draft validation into a full pass');
    }
  });
  check('forks and untrusted authors allocate no trusted execution runner', () => {
    for (const isDraft of [true, false]) {
      const fork = pr('opened', isDraft);
      fork.github.event.pull_request.head.repo.full_name = 'outsider/asap-pocketbase';
      const external = pr('opened', isDraft);
      external.github.event.pull_request.author_association = 'CONTRIBUTOR';
      for (const context of [fork, external]) {
        assert.equal(enabled(full, context), false);
        assert.equal(enabled(draft, context), false);
      }
    }
    assert.deepEqual(workflow.permissions, { contents: 'read', actions: 'read' });
    assert.deepEqual(full.permissions, { contents: 'read', actions: 'read', 'pull-requests': 'read' });
    assert.ok(!JSON.stringify(workflow).includes('secrets.'));
    assert.ok(!deploy.steps.some((candidate) => candidate.uses?.startsWith('actions/checkout')));
  });
  console.log(`${checks} CI workflow contract checks passed.`);
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
