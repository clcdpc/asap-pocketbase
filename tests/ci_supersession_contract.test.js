const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const YAML = require('yaml');

const root = path.resolve(__dirname, '..');
const controller = YAML.parse(fs.readFileSync(path.join(root, '.github/workflows/pr-ci-supersession.yml'), 'utf8'), { uniqueKeys: true });
const baseline = YAML.parse(fs.readFileSync(path.join(root, '.github/workflows/dotnet.yml'), 'utf8'), { uniqueKeys: true });
const job = controller.jobs['cancel-superseded'];
const script = job.steps[0].with.script;
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const repo = { owner: 'clcdpc', repo: 'asap-pocketbase' };
const repository = { id: 123, full_name: 'clcdpc/asap-pocketbase' };
const workflow = { id: 456, name: '.NET baseline', path: '.github/workflows/dotnet.yml', state: 'active' };
const currentSha = 'b'.repeat(40);
const oldSha = 'a'.repeat(40);

function run(id, number, overrides = {}) {
  return {
    id, run_number: number, run_attempt: 1, workflow_id: workflow.id,
    path: workflow.path, event: 'pull_request', repository: structuredClone(repository),
    head_repository: structuredClone(repository), head_branch: 'feature/ci',
    head_sha: currentSha, status: 'in_progress',
    pull_requests: [{ number: 17, head: { repo: { id: repository.id } }, base: { ref: 'main', repo: { id: repository.id } } }],
    ...overrides
  };
}

function currentPr(overrides = {}) {
  return {
    number: 17, state: 'open', draft: false, author_association: 'COLLABORATOR',
    head: { sha: currentSha, ref: 'feature/ci', repo: structuredClone(repository) },
    base: { ref: 'main', repo: structuredClone(repository) }, ...overrides
  };
}

// Execute the shipped inline script; only its GitHub REST boundaries are mocked.
async function exercise(options = {}) {
  const runs = options.runs || [run(101, 1, { head_sha: oldSha }), run(102, 2)];
  const trigger = options.trigger || runs[runs.length - 1];
  const calls = [];
  const cancelled = [];
  const failures = [];
  const reads = new Map();
  let prReads = 0;
  let thrown;
  function record(method, parameters) {
    assert.equal(parameters.owner, repo.owner);
    assert.equal(parameters.repo, repo.repo);
    calls.push({ method, parameters });
    if (options.fail === method) throw Object.assign(new Error(`Failed ${method}`), { status: 503 });
  }
  const actions = {
    getWorkflow: async (parameters) => {
      record('getWorkflow', parameters);
      assert.equal(parameters.workflow_id, 'dotnet.yml');
      return { data: options.workflow || workflow };
    },
    getWorkflowRun: async (parameters) => {
      record('getWorkflowRun', parameters);
      const count = (reads.get(parameters.run_id) || 0) + 1;
      reads.set(parameters.run_id, count);
      const found = runs.find(candidate => candidate.id === parameters.run_id) || (trigger.id === parameters.run_id && trigger);
      assert.ok(found, `Unexpected run lookup: ${parameters.run_id}`);
      const value = options.readRun ? options.readRun(structuredClone(found), count, { calls, cancelled }) : found;
      return { data: structuredClone(value) };
    },
    listWorkflowRuns: async (parameters) => {
      record('listWorkflowRuns', parameters);
      assert.equal(parameters.workflow_id, workflow.id);
      assert.equal(parameters.event, 'pull_request');
      assert.equal(parameters.branch, 'feature/ci');
      assert.equal(parameters.per_page, 100);
      assert.ok(Number.isInteger(parameters.page) && parameters.page >= 1);
      const data = options.page ? options.page(parameters.page) : {
        total_count: runs.length,
        workflow_runs: runs.slice((parameters.page - 1) * 100, parameters.page * 100)
      };
      return { data: structuredClone(data) };
    },
    cancelWorkflowRun: async (parameters) => {
      record('cancelWorkflowRun', parameters);
      cancelled.push(parameters.run_id);
      if (options.cancelError) throw Object.assign(new Error('Cancellation rejected'), { status: options.cancelError });
      return { status: 202 };
    }
  };
  const context = {
    repo, eventName: 'workflow_run', payload: {
      action: options.action || 'requested', repository,
      workflow_run: structuredClone(trigger)
    }
  };
  if (options.context) Object.assign(context, options.context);
  try {
    await new AsyncFunction('context', 'github', 'core', script)(context,
      { rest: { actions, pulls: { get: async (parameters) => {
        record('pulls.get', parameters);
        assert.equal(parameters.pull_number, 17);
        prReads += 1;
        return { data: structuredClone(options.readPr ? options.readPr(prReads) : (options.pr || currentPr())) };
      } } } },
      { info: () => {}, notice: () => {}, warning: () => {}, debug: () => {}, setFailed: message => failures.push(message) });
  } catch (error) {
    thrown = error;
  }
  return { calls, cancelled, failures, thrown, reads, prReads };
}

function successful(result) {
  assert.ifError(result.thrown);
  assert.deepEqual(result.failures, []);
}

function failed(result) {
  assert.ok(result.thrown || result.failures.length, 'An unexpected API error must fail the controller');
}

async function main() {
  assert.deepEqual(Object.keys(controller.jobs), ['cancel-superseded']);
  const permissions = job.permissions || controller.permissions;
  assert.deepEqual(permissions, { actions: 'write', 'pull-requests': 'read' });
  assert.equal(job.steps.length, 1);
  assert.equal(job.steps[0].uses, 'actions/github-script@v8');
  assert.deepEqual(controller.on.workflow_run.workflows, ['.NET baseline']);
  assert.deepEqual([...controller.on.workflow_run.types].sort(), ['in_progress', 'requested']);
  assert.doesNotMatch(script, /forceCancelWorkflowRun|downloadArtifact|require\(|\bexec\(/);
  assert.equal(baseline.permissions.actions, 'read');
  assert.equal(baseline.permissions.contents, 'read');
  for (const worker of Object.values(baseline.jobs)) {
    assert.notEqual(worker.permissions?.actions, 'write');
    assert.ok(!worker.steps?.some(step => step.with?.script?.includes('cancelWorkflowRun')));
  }
  assert.match(baseline.concurrency.group, /github\.run_id/);
  assert.match(baseline.concurrency.group, /github\.run_attempt/);
  assert.equal(baseline.concurrency['cancel-in-progress'], false);

  let result = await exercise();
  successful(result);
  assert.deepEqual(result.cancelled, [101], 'New head retires older active work');
  assert.ok(result.reads.get(102) >= 2, 'Refresh triggering run, then refresh winner before cancellation');
  assert.ok(result.reads.get(101) >= 1, 'Refresh the cancellation target');
  assert.ok(result.prReads >= 2, 'Recheck live PR before cancellation');

  for (const overrides of [{ id: 999 }, { event: 'push' }, { workflow_id: 999 }, { pull_requests: [] }]) {
    result = await exercise({ readRun: candidate => candidate.id === 102 ? { ...candidate, ...overrides } : candidate });
    successful(result);
    assert.deepEqual(result.cancelled, [], 'Independently refreshed trigger identity must be valid');
    assert.ok(!result.calls.some(call => call.method === 'listWorkflowRuns'));
  }
  result = await exercise({ workflow: { ...workflow, path: '.github/workflows/other.yml' } });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Resolve the exact baseline workflow, not only its display name');
  for (const context of [
    { eventName: 'pull_request' },
    { payload: { action: 'completed', repository, workflow_run: run(102, 2) } },
    { payload: { action: 'requested', workflow_run: run(102, 2) } }
  ]) {
    result = await exercise({ context });
    successful(result);
    assert.deepEqual(result.calls, [], 'Ignore unsupported or incomplete event payloads');
  }

  for (const action of ['requested', 'in_progress']) {
    result = await exercise({ action, trigger: run(101, 1, { head_sha: oldSha, run_attempt: 7 }) });
    successful(result);
    assert.ok(!result.cancelled.includes(102), 'Retrying an old run cannot cancel newer validation');
  }

  // Native reruns retain run_number; timestamps and attempt counts do not establish precedence.
  result = await exercise({ runs: [run(101, 1, { run_attempt: 9, updated_at: '2030-01-01T00:00:00Z' }), run(102, 2)] });
  successful(result);
  assert.deepEqual(result.cancelled, [101], 'Same-SHA events use original run_number');
  result = await exercise({ pr: currentPr({ draft: true }), runs: [run(101, 1), run(102, 2)] });
  successful(result);
  assert.deepEqual(result.cancelled, [101], 'Stable draft state still retires older same-SHA events');

  result = await exercise({ runs: [run(101, 1, { head_sha: oldSha }), run(102, 2, { status: 'completed', conclusion: 'success' })] });
  successful(result);
  assert.deepEqual(result.cancelled, [101], 'Completed current successor still supersedes old active work');
  result = await exercise({ runs: [run(101, 1, { status: 'completed' }), run(102, 2)] });
  successful(result);
  assert.deepEqual(result.cancelled, []);
  result = await exercise({ runs: [run(101, 1, { head_sha: oldSha }), run(102, 2, { head_sha: oldSha })] });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Without a current-head successor, do not cancel work');
  for (const status of ['queued', 'requested', 'waiting', 'pending', 'in_progress']) {
    result = await exercise({ runs: [run(101, 1, { status }), run(102, 2)] });
    successful(result);
    assert.deepEqual(result.cancelled, [101], `Supersede active status ${status}`);
  }

  result = await exercise({ runs: [run(101, 1), run(103, 3), run(102, 2)] });
  successful(result);
  assert.deepEqual([...result.cancelled].sort(), [101, 102], 'Out-of-order list chooses highest run_number');
  result = await exercise({ runs: [run(101, 1), run(103, 3, { head_sha: 'c'.repeat(40) }), run(102, 2)] });
  successful(result);
  assert.ok(!result.cancelled.includes(103), 'A newer run arriving across a head race is never an older target');

  const excluded = [
    { event: 'push' }, { event: 'workflow_dispatch' }, { workflow_id: 999 },
    { path: '.github/workflows/other.yml' }, { head_branch: 'other' },
    { head_repository: { id: 999, full_name: 'fork/asap-pocketbase' } },
    { repository: { id: 999, full_name: 'other/asap-pocketbase' } },
    { pull_requests: [] }, { pull_requests: [{ number: 18 }] },
    { pull_requests: [{ number: 17 }, { number: 18 }] }
  ];
  for (const overrides of excluded) {
    result = await exercise({ runs: [run(101, 1, overrides), run(102, 2)] });
    successful(result);
    assert.deepEqual(result.cancelled, [], `Unsafe target: ${JSON.stringify(overrides)}`);
  }
  for (const overrides of [{ state: 'closed' }, { author_association: 'CONTRIBUTOR' }, { author_association: undefined },
    { head: { sha: currentSha, ref: 'feature/ci', repo: { id: 999 } } },
    { base: { ref: 'release', repo: repository } }]) {
    result = await exercise({ pr: currentPr(overrides) });
    successful(result);
    assert.deepEqual(result.cancelled, [], `Unsafe PR: ${JSON.stringify(overrides)}`);
  }

  for (const changed of [
    currentPr({ head: { sha: 'c'.repeat(40), ref: 'feature/ci', repo: repository } }),
    currentPr({ draft: true }), currentPr({ author_association: 'NONE' }), currentPr({ state: 'closed' })
  ]) {
    result = await exercise({ readPr: count => count === 1 ? currentPr() : changed });
    successful(result);
    assert.deepEqual(result.cancelled, [], 'Changed live PR must stop cancellation');
  }
  result = await exercise({
    runs: [run(100, 1), run(101, 2), run(102, 3)],
    readPr: count => count <= 2 ? currentPr() : currentPr({ draft: true })
  });
  successful(result);
  assert.equal(result.cancelled.length, 1, 'Recheck PR before every cancellation, not once per batch');

  result = await exercise({ readRun: candidate => candidate.id === 101 ? { ...candidate, status: 'completed' } : candidate });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Target completed after listing');
  result = await exercise({ readRun: (candidate, count) => candidate.id === 102 && count > 1 ? { ...candidate, head_sha: oldSha } : candidate });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Winner identity must be revalidated');
  result = await exercise({ readRun: candidate => candidate.id === 101 ? { ...candidate, run_number: 3 } : candidate });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Refreshed target cannot outrank winner');
  result = await exercise({ readRun: candidate => candidate.id === 101 ? { ...candidate, head_repository: { id: 999 } } : candidate });
  successful(result);
  assert.deepEqual(result.cancelled, [], 'Refreshed target must still be trusted and same-repository');

  const manyRuns = Array.from({ length: 100 }, (_, index) => run(1000 + index, 1000 + index, { status: 'completed' }));
  result = await exercise({
    runs: [run(101, 1), run(2000, 2000)],
    page: page => ({ total_count: 102, workflow_runs: page === 1 ? manyRuns : [run(101, 1), run(2000, 2000)] })
  });
  successful(result);
  assert.deepEqual(result.cancelled, [101]);
  assert.deepEqual(result.calls.filter(call => call.method === 'listWorkflowRuns').map(call => call.parameters.page), [1, 2]);
  result = await exercise({ page: () => ({ total_count: 1001, workflow_runs: manyRuns }) });
  failed(result);
  assert.deepEqual(result.cancelled, [], 'Exceeding the bounded scan must not partially cancel');
  assert.ok(result.calls.filter(call => call.method === 'listWorkflowRuns').length <= 10);
  for (const page of [
    { total_count: 2, workflow_runs: null }, { total_count: -1, workflow_runs: [] },
    { total_count: '2', workflow_runs: [] }
  ]) {
    result = await exercise({ page: () => page });
    failed(result);
    assert.deepEqual(result.cancelled, [], 'Malformed list responses fail closed');
  }

  for (const method of ['getWorkflow', 'getWorkflowRun', 'listWorkflowRuns', 'pulls.get']) {
    result = await exercise({ fail: method });
    failed(result);
    assert.deepEqual(result.cancelled, [], `${method} errors fail closed`);
  }
  result = await exercise({ runs: [run(100, 1), run(101, 2), run(102, 3)], cancelError: 403 });
  failed(result);
  assert.equal(result.cancelled.length, 1, 'An error stops subsequent POSTs');
  result = await exercise({ cancelError: 409 });
  failed(result);
  assert.deepEqual(result.cancelled, [101], '409 is not silently treated as success while still active');
  result = await exercise({
    cancelError: 409,
    readRun: (candidate, count, state) => state.cancelled.includes(candidate.id) ? { ...candidate, status: 'completed' } : candidate
  });
  successful(result);
  assert.deepEqual(result.cancelled, [101], '409 is harmless only after confirming completion');
  assert.ok(result.reads.get(101) >= 2);

  console.log('CI supersession controller contracts passed');
}

main().catch(error => { console.error(error); process.exitCode = 1; });
