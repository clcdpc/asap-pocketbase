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
    context.needs = { 'validation-policy': { result: failed ? 'failure' : 'success', outputs } };
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
  async function currentMain(context, headSha = context.github.sha) {
    const current = step(gate, 'Check automatic main deployment before queuing');
    const outputs = {};
    let summary = '';
    const [owner, repo] = context.github.repository.split('/');
    const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
    await new AsyncFunction('context', 'github', 'core', current.with.script)(
      { sha: context.github.sha, repo: { owner, repo } },
      { rest: { git: { getRef: async (parameters) => {
        assert.deepEqual(parameters, { owner, repo, ref: 'heads/main' });
        if (headSha instanceof Error) { throw headSha; }
        return { data: { object: { sha: headSha } } };
      } } } },
      { setOutput: (key, value) => { outputs[key] = value; }, notice: () => {},
        summary: { addRaw: (value) => { summary += value; return { write: async () => {} }; } } }
    );
    context.needs.validation = { result: 'success', outputs };
    return summary;
  }
  function validateConcurrency(candidate) {
    assert.deepEqual(candidate.jobs['deploy-test-iis'].concurrency,
      { group: 'asap-test-iis', 'cancel-in-progress': false, queue: 'max' });
    assert.equal(candidate.concurrency.queue, undefined, 'Native queue is authorized only for IIS activation');
    for (const [name, job] of Object.entries(candidate.jobs)) {
      if (name !== 'deploy-test-iis') { assert.equal(job.concurrency?.queue, undefined, `Unexpected queue in ${name}`); }
    }
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
  await check('successful current main pushes and test tags retain gated deployment eligibility', async () => {
    const push = nonPr('push', 106);
    const tag = nonPr('push', 107, 'refs/tags/v1.2.3-test.1');
    for (const context of [push, tag]) {
      await routed(context);
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'skipped' };
      context.needs.validation = { result: 'success' };
      if (context === push) { await currentMain(context); }
      assert.equal(enabled(full, context), true);
    }
    assert.equal(enabled(deploy, push), true);
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
    assert.deepEqual(deploy.environment, { name: 'asap-test-iis', deployment: false });
    assert.equal(template(full.env.ASAP_DEPLOY_TEST_IIS, push), 'false', 'Automatic deployment must preserve trusted test reuse rather than force fresh tests');
  });
  await check('automatic authorization rejects arbitrary branches, PRs and unsuccessful or absent main freshness', async () => {
    for (const context of [
      await routed(nonPr('push', 140, 'refs/heads/other')),
      await routed(pr('ready_for_review', false)),
      await routed(nonPr('pull_request_target', 141))
    ]) {
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.inputs.deploy_test_iis = true;
      context.needs['build-test-package'] = { result: 'success' };
      context.needs.validation = { result: 'success', outputs: { automatic_current: 'true' } };
      assert.equal(enabled(deploy, context), false);
    }
    const context = await routed(nonPr('push', 142));
    context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
    context.needs['build-test-package'] = { result: 'success' };
    for (const value of [undefined, '', 'false', 'unknown']) {
      context.needs.validation = { result: 'success', outputs: { automatic_current: value } };
      assert.equal(enabled(deploy, context), false, `main freshness ${value}`);
    }
  });
  await check('actual hosted main-head check skips superseded artifacts before allocating IIS and fails closed on API errors', async () => {
    const context = await routed(nonPr('push', 143));
    context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
    context.needs['build-test-package'] = { result: 'success' };
    const current = step(gate, 'Check automatic main deployment before queuing');
    assert.equal(enabled(current, context), true);
    assert.equal(await currentMain(context), '');
    assert.equal(enabled(deploy, context), true);
    const summary = await currentMain(context, 'b'.repeat(40));
    assert.equal(enabled(deploy, context), false);
    assert.match(summary, /Status: Skipped.*no activation/);
    assert.match(summary, /test-aaaaaaaaaaaa/);
    await assert.rejects(currentMain(context, new Error('API unavailable')), /API unavailable/);
    await assert.rejects(currentMain(context, 'short-sha'), /full commit SHA/);
    for (const other of [nonPr('workflow_dispatch', 144), nonPr('push', 145, 'refs/tags/v1.0.0-test.1'), pr('opened', false)]) {
      other.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      assert.equal(enabled(current, other), false, 'Intentional manual/tag and PR work must not inherit automatic freshness');
    }
    context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'false';
    assert.equal(enabled(current, context), false);
  });
  await check('bounded native deployment queue retains rapid merges and never cancels active activation', () => {
    validateConcurrency(workflow);
    for (const value of ['invalid', true, false, 0, 100, null, undefined]) {
      const changed = structuredClone(workflow);
      changed.jobs['deploy-test-iis'].concurrency.queue = value;
      assert.throws(() => validateConcurrency(changed));
    }
    for (const location of ['workflow', 'full']) {
      const changed = structuredClone(workflow);
      const owner = location === 'workflow' ? changed : changed.jobs['build-test-package'];
      owner.concurrency = { ...(owner.concurrency || {}), queue: 'max' };
      assert.throws(() => validateConcurrency(changed));
    }
    const changed = structuredClone(workflow);
    changed.jobs['deploy-test-iis'].concurrency['cancel-in-progress'] = true;
    assert.throws(() => validateConcurrency(changed));
    const lint = fs.readFileSync(path.join(root, 'scripts/ci/check-workflows.sh'), 'utf8');
    assert.match(lint, /-ignore '\^unexpected key "queue" for "concurrency" section/);
    assert.equal((lint.match(/-ignore /g) || []).length, 1, 'Only the pinned linter missing queue key may be suppressed');
  });
  await check('actual PowerShell identity derives deterministic lowercase main labels and preserves explicit tags', () => {
    const identity = step(full, 'Determine exact artifact identity');
    const temporary = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'asap-workflow-identity-'));
    const commit = '8f3c2a1d92b456789012345678901234567890ab';
    function identify(event, ref, refType, checkedOut = commit, expected = commit) {
      const output = path.join(temporary, `output-${Math.random()}`);
      const run = cp.spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-EncodedCommand', Buffer.from(
        `function git { if (($args -join ' ') -cne 'rev-parse HEAD') { throw 'Unexpected git command' }; $env:FIXTURE_COMMIT }\n${identity.run}\nGet-Content -LiteralPath $env:GITHUB_OUTPUT -Raw`, 'utf16le').toString('base64')], {
        encoding: 'utf8', env: { ...process.env, GITHUB_OUTPUT: output, GITHUB_SHA: expected, FIXTURE_COMMIT: checkedOut,
          GITHUB_EVENT_NAME: event, GITHUB_REF: ref, GITHUB_REF_TYPE: refType, GITHUB_REF_NAME: ref.replace(/^refs\/(heads|tags)\//, '') }
      });
      assert.ifError(run.error);
      return { run, values: Object.fromEntries(run.stdout.trim().split(/\r?\n/).map((line) => line.split('='))) };
    }
    try {
      for (const event of ['push', 'workflow_dispatch']) {
        for (const checkedOut of [commit, commit.toUpperCase()]) {
          const { run, values } = identify(event, 'refs/heads/main', 'branch', checkedOut);
          assert.equal(run.status, 0, run.stderr);
          assert.equal(values.deployment_label, 'test-8f3c2a1d92b4');
          assert.equal(values.commit, commit);
        }
      }
      for (const tag of ['v1.2.0-test.28', 'v1.2.0']) {
        const { run, values } = identify('push', `refs/tags/${tag}`, 'tag');
        assert.equal(run.status, 0, run.stderr);
        assert.equal(values.deployment_label, tag);
        assert.equal(values.commit, commit);
      }
      assert.equal(identify('workflow_dispatch', 'refs/heads/other', 'branch').values.deployment_label, 'manual-other');
      assert.notEqual(identify('push', 'refs/heads/main', 'branch', 'c'.repeat(40)).run.status, 0, 'Mismatched checked-out commit must fail');
      assert.equal(full.outputs.commit, '${{ steps.identity.outputs.commit }}');
      const packaging = step(full, 'Create exact test-IIS deployment ZIP');
      assert.equal(packaging.env.TESTED_COMMIT, '${{ steps.identity.outputs.commit }}');
      assert.match(packaging.run, /commitSha = \$env:TESTED_COMMIT/);
      assert.equal(step(deploy, 'Deploy exact tested artifact').env.EXPECTED_COMMIT_SHA, '${{ needs.build-test-package.outputs.commit }}');
      assert.doesNotMatch(full.steps.map((candidate) => candidate.run || '').join('\n'), /git tag|AssemblyVersion|InformationalVersion|\/p:Version/);
    } finally { fs.rmSync(temporary, { recursive: true, force: true }); }
  });
  await check('actual deployment summary distinguishes new activation, idempotency, skips, diagnostics and failures', () => {
    const summary = step(deploy, 'Summarize test-IIS deployment');
    assert.equal(summary.if, '${{ always() }}');
    assert.deepEqual(deploy.environment, { name: 'asap-test-iis', deployment: false },
      'Environment protections remain while implicit green deployment records cannot misreport a skipped activation');
    const temporary = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'asap-deployment-summary-'));
    try {
      for (const [outcome, status, expected] of [
        ['success', 'deployed', 'Success'],
        ['success', 'already-deployed', 'Already deployed and healthy (no new activation)'],
        ['success', 'skipped', 'Skipped (superseded; no activation)'],
        ['success', 'plan-only', 'SQL plan only (no activation)'],
        ['failure', 'deployed', 'Failure'],
        ['cancelled', '', 'Failure'],
        ['skipped', '', 'Failure']
      ]) {
        const output = path.join(temporary, `${outcome}-${status}.md`);
        const context = nonPr('push', 157);
        context.needs = { 'build-test-package': { outputs: { commit: context.github.sha, deployment_label: 'test-aaaaaaaaaaaa' } } };
        context.steps = { activate: { outcome, outputs: { deployment_status: status, deployment_reason: 'fixture reason', deployment_source: 'automatic' } } };
        const env = Object.fromEntries(Object.entries(summary.env).map(([key, value]) => [key, template(value, context)]));
        const run = cp.spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-EncodedCommand', Buffer.from(summary.run, 'utf16le').toString('base64')], {
          encoding: 'utf8', env: { ...process.env, ...env, GITHUB_STEP_SUMMARY: output, GITHUB_RUN_ID: '157' }
        });
        assert.ifError(run.error);
        assert.equal(run.status, 0, run.stderr);
        const markdown = fs.readFileSync(output, 'utf8');
        for (const text of ['Environment: DEVWEB', 'Deployment: test-aaaaaaaaaaaa', 'Source: Automatic main deployment', `Status: ${expected}`, 'Reason: fixture reason', '/actions/runs/157', `/commit/${context.github.sha}`]) {
          assert.ok(markdown.includes(text), text);
        }
        assert.match(markdown, /Environment: DEVWEB\r?\n\r?\nDeployment:/, 'Summary fields must render as separate paragraphs');
      }
    } finally { fs.rmSync(temporary, { recursive: true, force: true }); }
  });
  await check('persistent DEV policy is host-owned and tag deployment needs no override input', () => {
    assert.equal(workflow.on.workflow_dispatch.inputs.allow_test_sql_data_loss, undefined);
    assert.equal(workflow.on.workflow_dispatch.inputs.reviewed_test_sql_plan_sha256, undefined);
    assert.equal(workflow.on.workflow_dispatch.inputs.test_sql_plan_only.type, 'boolean');
    assert.equal(workflow.on.workflow_dispatch.inputs.test_sql_plan_only.default, false);
    const activation = step(deploy, 'Deploy exact tested artifact');
    assert.equal(activation.env.ALLOW_SQL_DATA_LOSS, undefined);
    assert.equal(activation.env.SQL_DATA_LOSS_TARGET, undefined);
    assert.doesNotMatch(activation.run, /AllowSqlDataLoss|ReviewedSqlPlan|ExpectedSqlDataLossTarget/);
    assert.match(activation.run, /SqlPlanOnly = \$planOnly/);
    assert.match(activation.run, /SqlPlanOutputDirectory = Join-Path/);
    const installer = fs.readFileSync(path.join(root, 'scripts/deployment/Deploy-AsapTest.ps1'), 'utf8');
    assert.match(installer, /Get-DevSqlOverrideSettings -Configuration \$config/);
    assert.match(installer, /\$allowSqlDataLoss = \$hostConfig\.AllowSqlDataLoss/);
    assert.match(installer, /remains enabled across authorized test deployments until the environment setting is disabled/);
    assert.match(installer, /Equals\('DEVWEB'/);
    assert.match(installer, /Equals\('devasap\.clcohio\.org'/);
    for (const job of [full, draft]) {
      assert.doesNotMatch(JSON.stringify(job), /AllowSqlDataLoss|SqlDataLossTarget|reviewed_test_sql_plan/);
    }
    const plans = step(deploy, 'Upload deployment SQL plan');
    assert.equal(plans.if, '$' + '{{ always() }}', 'Tag plans and rejected plans must both be retained');
    assert.equal(plans.uses, 'actions/upload-artifact@v4');
    assert.ok(activation.run.indexOf('Read-RunBoolean') < activation.run.indexOf('Resolve-Path .artifacts'));
  });
  await check('actual PowerShell tag routing is input-free and malformed or non-test plan diagnostics fail before host access', () => {
    const activation = step(deploy, 'Deploy exact tested artifact');
    const guards = activation.run.slice(0, activation.run.indexOf('$zipPath ='));
    function controls(event, ref, changes = {}) {
      const defaults = Object.fromEntries(Object.entries(workflow.on.workflow_dispatch.inputs).map(([key, input]) => [key, input.default]));
      const context = nonPr(event, 201, ref, { ...(event === 'workflow_dispatch' ? defaults : {}), ...changes });
      const env = Object.fromEntries(Object.entries(activation.env).map(([key, value]) => [key, template(value, {
        ...context, needs: { 'build-test-package': { outputs: { zip_sha256: 'b'.repeat(64), deployment_label: 'v1.0.0-test.27' } } }
      })]));
      const run = cp.spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-EncodedCommand', Buffer.from(
        guards + '\n$sqlOptions | ConvertTo-Json -Compress', 'utf16le').toString('base64')], {
        encoding: 'utf8', env: { ...process.env, ...env, GITHUB_EVENT_NAME: event, GITHUB_REF: ref,
          GITHUB_RUN_ID: '201', GITHUB_RUN_ATTEMPT: '1', RUNNER_TEMP: require('node:os').tmpdir() }
      });
      assert.ifError(run.error);
      return run;
    }
    for (const ref of ['refs/tags/v1.0.0-test.27', 'refs/tags/v1.0.0-test.28']) {
      const tag = controls('push', ref);
      assert.equal(tag.status, 0, tag.stderr);
      const options = JSON.parse(tag.stdout);
      assert.equal(options.SqlPlanOnly, false);
      assert.equal(Object.hasOwn(options, 'AllowSqlDataLoss'), false, 'Persistent policy must come only from host deployment.json');
      assert.ok(options.SqlPlanOutputDirectory);
      const candidate = controls('workflow_dispatch', ref, { deploy_test_iis: true, test_sql_plan_only: true });
      assert.equal(candidate.status, 0, candidate.stderr);
      assert.equal(JSON.parse(candidate.stdout).SqlPlanOnly, true);
    }
    for (const bad of ['true', 'false', 'FALSE', '', 0, 1, null, 'false; Write-Output injected']) {
      assert.notEqual(controls('workflow_dispatch', 'refs/tags/v1.0.0-test.27', { test_sql_plan_only: bad }).status, 0, String(bad));
    }
    for (const [event, ref, inputs] of [
      ['push', 'refs/tags/v1.0.0-test.27', { test_sql_plan_only: true }],
      ['workflow_dispatch', 'refs/heads/main', { deploy_test_iis: true, test_sql_plan_only: true }],
      ['workflow_dispatch', 'refs/tags/v1.0.0', { deploy_test_iis: true, test_sql_plan_only: true }],
      ['workflow_dispatch', 'refs/tags/v1.0.0-test.27', { test_sql_plan_only: true }]
    ]) {
      assert.notEqual(controls(event, ref, inputs).status, 0, event + '/' + ref);
    }
  });
  await check('tag and requested manual deployment tolerate skipped draft but fail closed on bad prerequisites', async () => {
    for (const context of [
      nonPr('push', 109),
      nonPr('push', 110, 'refs/tags/v1.0.0-test.23'),
      nonPr('workflow_dispatch', 111, 'refs/heads/codex/wave2-s1-workflow-settings', { deploy_test_iis: true })
    ]) {
      await routed(context);
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'skipped' };
      context.needs.validation = { result: 'success' };
      if (context.github.ref === 'refs/heads/main') { await currentMain(context); }
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
    assert.match(step(full, 'Verify publish artifacts').run, /dotnet publish .* --no-build --no-restore /);
    const safety = step(full, 'Verify current CI safety contracts');
    assert.equal(safety.if, undefined);
    assert.equal(safety.run, 'node tests/run_all.js --ci-safety');
    assert.ok(full.steps.indexOf(safety) < full.steps.indexOf(step(full, 'Find reusable test executions')));
    const verify = step(full, 'Verify required test suites');
    assert.equal(verify.if, '${{ always() }}');
    assert.equal(verify.run, 'python3 -B scripts/ci/reuse.py verify');
    for (const suite of ['dotnet', 'browser', 'frontend']) {
      const test = full.steps.find((candidate) => candidate.id === suite);
      assert.equal(test.if, `\${{ steps.reuse.outputs.${suite} != 'true' }}`);
      assert.equal(test['continue-on-error'], undefined);
      const upload = step(full, `Upload original ${suite} execution evidence`);
      assert.equal(upload.if, `\${{ steps.verify.outcome == 'success' && steps.${suite}.outcome == 'success' }}`);
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
    assert.equal(step(full, 'Run browser accessibility tests').run, 'bash scripts/ci/run-with-performance.sh npm run test:browser');
    assert.ok(full.steps.indexOf(step(full, 'Run frontend unit tests')) < full.steps.indexOf(step(full, 'Start SQL Server 2022')));
    for (const name of ['Verify publish artifacts', 'Determine exact artifact identity', 'Create exact test-IIS deployment ZIP',
      'Validate test-IIS package without host mutation', 'Upload exact test-IIS deployment artifact']) {
      assert.equal(step(full, name).if, undefined, `${name} must never depend on a reuse HIT`);
    }
    assert.equal(step(full, 'Remove SQL Server test container').if, '${{ always() }}');
    assert.ok(!reuse.exclusions.some((rule) => rule.paths.includes('.github/workflows/dotnet.yml')));
  });
  await check('full qualification owns one runner, build, discovery and SQL lifecycle with sequential suites', async () => {
    assert.deepEqual(Object.keys(workflow.jobs), ['validation-policy', 'build-test-package', 'draft-validation', 'validation', 'deploy-test-iis']);
    assert.equal(full['timeout-minutes'], 120);
    assert.equal(reuse.schema, 5);
    assert.equal(reuse.browser_job, undefined);
    assert.equal(step(full, 'Find reusable test executions').env.ASAP_CI_SUITES, undefined);
    assert.equal(step(full, 'Verify required test suites').env.ASAP_CI_SUITES, undefined);
    assert.equal(full.env.ASAP_CI_SUITES, undefined);
    for (const pattern of [/dotnet build/, /selection\.py prepare/, /reuse\.py plan/, /docker run/, /docker rm/]) {
      assert.equal(full.steps.filter((candidate) => pattern.test(candidate.run || '')).length, 1, String(pattern));
    }
    const ordered = ['Run frontend unit tests', 'Generate ephemeral SQL test credentials', 'Start SQL Server 2022',
      'Wait for SQL Server', 'Run .NET and real SQL tests (non-browser partition)', 'Run browser accessibility tests',
      'Verify required test suites', 'Verify publish artifacts', 'Remove SQL Server test container'];
    for (let index = 1; index < ordered.length; index++) {
      assert.ok(full.steps.indexOf(step(full, ordered[index - 1])) < full.steps.indexOf(step(full, ordered[index])), ordered[index]);
    }
    for (const suite of ['frontend', 'dotnet', 'browser']) {
      const test = full.steps.find((candidate) => candidate.id === suite);
      assert.doesNotMatch(test.run, /&(?!&)|\b(nohup|Start-Process)\b/);
      assert.equal(test['continue-on-error'], undefined);
      assert.equal(step(full, 'Verify required test suites').env[`${suite.toUpperCase()}_OUTCOME`], `\${{ steps.${suite}.outcome }}`);
      const upload = step(full, `Upload original ${suite} execution evidence`);
      assert.equal(upload.with.name, `asap-test-evidence-v\${{ steps.reuse.outputs.schema }}-${suite}-\${{ steps.reuse.outputs.${suite}_fingerprint }}`);
      assert.equal(upload.with.overwrite, true, 'Retry replaces only that run\'s same-key artifact');
    }
    const credentials = step(full, 'Generate ephemeral SQL test credentials').run;
    assert.match(credentials, /::add-mask::\$sql_password/);
    assert.match(credentials, /ASAP_CI_SQL_CONTAINER=asap-sql-\$GITHUB_RUN_ID-\$GITHUB_RUN_ATTEMPT-\$\(openssl rand/);
    assert.match(step(full, 'Start SQL Server 2022').run, /--name "\$ASAP_CI_SQL_CONTAINER"/);
    assert.match(step(full, 'Start SQL Server 2022').run, /--publish 127\.0\.0\.1::1433/);
    assert.match(step(full, 'Wait for SQL Server').run, /docker exec --interactive "\$ASAP_CI_SQL_CONTAINER"/);
    assert.match(step(full, 'Remove SQL Server test container').run, /docker rm --force "\$ASAP_CI_SQL_CONTAINER"/);
    assert.equal(step(full, 'Remove SQL Server test container').if, '${{ always() }}');
    assert.equal(step(full, 'Install browser acceptance dependencies').run, 'npx playwright install --with-deps chromium');
    assert.match(credentials, /python3 -B scripts\/ci\/reuse.py sql-image/);
    assert.equal(step(full, 'Start SQL Server 2022').env.SQL_IMAGE, '${{ steps.sql.outputs.image }}');
    assert.ok(!full.steps.some((candidate) => /playwright install-deps/.test(candidate.run || '')));
    for (const context of [await routed(pr('ready_for_review', false)), await routed(pr('converted_to_draft', true))]) {
      const active = Object.values(workflow.jobs).filter((job) => job['runs-on'] === 'clc-linux' && enabled(job, context));
      assert.equal(active.length, 1, 'Policy selects exactly one clc-linux job');
    }
  });
  await check('cold, full reuse and every mixed reuse schedule only necessary suites and one shared SQL instance', () => {
    for (let mask = 0; mask < 8; mask++) {
      const suites = ['frontend', 'dotnet', 'browser'];
      const outputs = Object.fromEntries(suites.map((suite, index) => [suite, String(Boolean(mask & (1 << index)))]));
      const context = { steps: { reuse: { outputs } } };
      for (const suite of suites) {
        assert.equal(enabled(full.steps.find((candidate) => candidate.id === suite), context), outputs[suite] !== 'true');
      }
      for (const name of ['Generate ephemeral SQL test credentials', 'Start SQL Server 2022', 'Wait for SQL Server']) {
        assert.equal(enabled(step(full, name), context), outputs.dotnet !== 'true' || outputs.browser !== 'true');
      }
      assert.equal(enabled(step(full, 'Install browser acceptance dependencies'), context), outputs.browser !== 'true');
    }
  });
  await check('normal ready PR reuse retains final verification, packaging and the required gate', async () => {
    const context = await routed(pr('opened', false));
    context.steps = { reuse: { outputs: { frontend: 'true', dotnet: 'true', browser: 'true' } } };
    assert.equal(template(full.env.ASAP_FORCE_FULL_CI, context), 'false');
    assert.equal(template(full.env.ASAP_DEPLOY_TEST_IIS, context), 'false');
    for (const suite of ['frontend', 'dotnet', 'browser']) {
      assert.equal(enabled(full.steps.find((candidate) => candidate.id === suite), context), false);
    }
    assert.equal(step(full, 'Verify required test suites').if, '${{ always() }}');
    for (const name of ['Verify publish artifacts', 'Create exact test-IIS deployment ZIP',
      'Validate test-IIS package without host mutation']) {
      assert.equal(step(full, name).if, undefined);
    }
    context.needs['build-test-package'] = { result: 'success' };
    context.needs['draft-validation'] = { result: 'skipped' };
    assert.equal(evaluate(gate.name, context), 'Build, test and package');
    assert.equal(report(context), 0);
    assert.equal(enabled(deploy, context), false);
  });
  await check('manual test deployment and test tags retain reuse and every deployment prerequisite', async () => {
    for (const request of [
      nonPr('workflow_dispatch', 130, 'refs/tags/v1.0.0-test.10', { deploy_test_iis: true }),
      nonPr('push', 131, 'refs/tags/v1.0.0-test.11')
    ]) {
      const context = await routed(request);
      context.vars.ASAP_TEST_DEPLOYMENT_ENABLED = 'true';
      context.steps = { reuse: { outputs: { frontend: 'true', dotnet: 'true', browser: 'true' } } };
      context.needs['build-test-package'] = { result: 'success' };
      context.needs['draft-validation'] = { result: 'skipped' };
      context.needs.validation = { result: 'success' };
      assert.equal(template(full.env.ASAP_FORCE_FULL_CI, context), 'false');
      for (const suite of ['frontend', 'dotnet', 'browser']) {
        assert.equal(enabled(full.steps.find((candidate) => candidate.id === suite), context), false);
      }
      assert.equal(step(full, 'Verify required test suites').if, '${{ always() }}');
      assert.equal(report(context), 0);
      assert.equal(enabled(deploy, context), true);
      context.needs.validation.result = 'failure';
      assert.equal(enabled(deploy, context), false);
    }
  });
  await check('suite diagnostics retain separate TRX, fixture timelines and resource files without copying results', () => {
    const dotnet = full.steps.find((candidate) => candidate.id === 'dotnet');
    const browser = full.steps.find((candidate) => candidate.id === 'browser');
    const frontend = full.steps.find((candidate) => candidate.id === 'frontend');
    assert.equal(new Set([dotnet, browser, frontend].map((candidate) => candidate.env.ASAP_CI_PERFORMANCE_OUTPUT)).size, 3);
    assert.equal(browser.env.ASAP_CI_BROWSER_RESULTS, '${{ github.workspace }}/.artifacts/browser-performance/test-results.trx');
    assert.notEqual(dotnet.env.ASAP_CI_FIXTURE_TIMINGS, browser.env.ASAP_CI_FIXTURE_TIMINGS);
    assert.equal(step(full, 'Summarize browser diagnostic evidence').run, 'python3 -B scripts/ci/diagnostic_report.py --output .artifacts/browser-performance');
    assert.equal(step(full, 'Upload browser performance telemetry').with.path, '.artifacts/browser-performance/');
    assert.equal(step(full, 'Upload .NET/SQL performance telemetry').with.path, '.artifacts/ci-performance/');
    assert.doesNotMatch(full.steps.map((candidate) => candidate.run || '').join('\n'), /\bcp\s+.*\.trx/);
    for (const name of ['Summarize .NET/SQL diagnostic evidence', 'Summarize browser diagnostic evidence']) {
      assert.match(step(full, name).if, /always\(\).*outcome != 'skipped'/);
    }
  });
  await check('the reporter always inspects selected execution and runs no PR code', () => {
    assert.equal(gate.if, '${{ always() }}');
    assert.deepEqual(gate.permissions, { contents: 'read' });
    assert.deepEqual(gate.needs, ['validation-policy', 'build-test-package', 'draft-validation']);
    assert.equal(gate['runs-on'], 'ubuntu-latest');
    assert.ok(!gate.steps.some((candidate) => candidate.uses?.startsWith('actions/checkout')));
    assert.equal(gate['continue-on-error'], undefined);
    assert.equal(gate.steps.length, 2);
    assert.equal(step(gate, 'Check automatic main deployment before queuing').uses, 'actions/github-script@v8');
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
