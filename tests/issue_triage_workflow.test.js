const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const YAML = require('yaml');

const workflow = YAML.parse(fs.readFileSync(path.join(__dirname, '../.github/workflows/issue-triage.yml'), 'utf8'), { uniqueKeys: true });
const job = Object.values(workflow.jobs)[0];
const step = job.steps.find((candidate) => candidate.uses === 'actions/github-script@v8');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const target = { owner: 'clcdpc', repo: 'asap-pocketbase', issue_number: 17 };

async function main() {
  const { Lexer, Parser, Evaluator, data } = await import('@actions/expressions');
  function evaluate(expression, github) {
    const source = expression.trim().match(/^\$\{\{([\s\S]*)\}\}$/)?.[1];
    assert.ok(source, `Expected an Actions expression: ${expression}`);
    const ast = new Parser(new Lexer(source).lex().tokens, ['github'], []).parse();
    return new Evaluator(ast, JSON.parse(JSON.stringify({ github }), data.reviver)).evaluate().coerceString();
  }
  function event(action, labels = [], changedLabel) {
    return { action, issue: { number: target.issue_number, labels: labels.map((name) => ({ name })) },
      ...(changedLabel ? { label: { name: changedLabel } } : {}) };
  }
  function issue(labels = []) {
    return { state: 'open', labels: labels.map((name) => ({ name })) };
  }
  function labels(current) {
    return current.labels.map((label) => label.name).sort();
  }
  async function dispatch(payload, current, options = {}) {
    const calls = [];
    const githubContext = { workflow: workflow.name, event: payload };
    if (!workflow.on.issues.types.includes(payload.action) ||
        (job.if && evaluate(job.if, githubContext) !== 'true')) {
      return calls;
    }
    // Execute the actual workflow script, replacing only the external GitHub API.
    await new AsyncFunction('context', 'github', step.with.script)(
      { repo: { owner: target.owner, repo: target.repo }, issue: { number: payload.issue.number }, payload },
      { rest: { issues: {
        get: async (parameters) => {
          assert.deepEqual(parameters, target);
          calls.push('get');
          if (options.readError) { throw options.readError; }
          return { data: structuredClone(current) };
        },
        addLabels: async (parameters) => {
          assert.deepEqual(parameters, { ...target, labels: ['needs-triage'] });
          calls.push('add');
          if (options.addError) { throw options.addError; }
          if (!labels(current).includes('needs-triage')) { current.labels.push({ name: 'needs-triage' }); }
        },
        removeLabel: async (parameters) => {
          assert.deepEqual(parameters, { ...target, name: 'needs-triage' });
          calls.push('remove');
          options.beforeRemove?.(current);
          if (options.removeError) { throw options.removeError; }
          if (!labels(current).includes('needs-triage')) { throw Object.assign(new Error('Label absent'), { status: 404 }); }
          current.labels = current.labels.filter((label) => label.name !== 'needs-triage');
        }
      } } }
    );
    return calls;
  }
  let checks = 0;
  async function check(name, body) {
    await body();
    checks++;
    console.log(`PASS ${name}`);
  }

  await check('issues created with meta-issue are exempt from triage', async () => {
    const current = issue(['meta-issue', 'enhancement']);
    await dispatch(event('opened', ['meta-issue', 'enhancement']), current);
    assert.deepEqual(labels(current), ['enhancement', 'meta-issue']);
  });
  await check('applying meta-issue removes only needs-triage', async () => {
    const current = issue(['meta-issue', 'needs-triage', 'bug']);
    await dispatch(event('labeled', ['meta-issue', 'needs-triage', 'bug'], 'meta-issue'), current);
    assert.deepEqual(labels(current), ['bug', 'meta-issue']);
  });
  await check('removing meta-issue restores normal triage eligibility', async () => {
    const current = issue(['enhancement']);
    await dispatch(event('unlabeled', ['enhancement'], 'meta-issue'), current);
    assert.deepEqual(labels(current), ['enhancement', 'needs-triage']);
  });
  await check('ordinary new issues keep the existing triage behavior with or without other labels', async () => {
    for (const existing of [[], ['bug'], ['triaged', 'enhancement']]) {
      const current = issue(existing);
      await dispatch(event('opened', existing), current);
      assert.deepEqual(labels(current), [...existing, 'needs-triage'].sort());
    }
  });
  await check('unrelated label changes and manual triage completion never requeue an issue', async () => {
    for (const action of ['labeled', 'unlabeled']) {
      for (const changed of ['bug', 'needs-triage']) {
        for (const existing of [['bug'], ['meta-issue', 'bug']]) {
          const current = issue(existing);
          assert.deepEqual(await dispatch(event(action, existing, changed), current), []);
          assert.deepEqual(labels(current), [...existing].sort());
        }
      }
    }
  });
  await check('current labels override stale opened and meta-issue event snapshots in both directions', async () => {
    const stale = [event('opened'), event('labeled', [], 'meta-issue'), event('unlabeled', [], 'meta-issue')];
    for (const payload of stale) {
      const current = issue(['meta-issue', 'needs-triage', 'bug']);
      await dispatch(payload, current);
      assert.deepEqual(labels(current), ['bug', 'meta-issue']);
    }
    for (const payload of [event('opened', ['meta-issue']),
      event('labeled', ['meta-issue'], 'meta-issue'), event('unlabeled', ['meta-issue'], 'meta-issue')]) {
      const current = issue(['bug']);
      await dispatch(payload, current);
      assert.deepEqual(labels(current), ['bug', 'needs-triage']);
    }
  });
  await check('repeated eligible events perform no redundant label writes', async () => {
    for (const existing of [['meta-issue'], ['needs-triage']]) {
      const current = issue(existing);
      for (const payload of [event('opened'), event('labeled', [], 'meta-issue'), event('unlabeled', [], 'meta-issue')]) {
        assert.deepEqual(await dispatch(payload, current), ['get']);
      }
      assert.deepEqual(labels(current), existing);
    }
  });
  await check('reordered and repeated queued events converge to the current meta-issue state', async () => {
    const opened = event('opened');
    const added = event('labeled', ['meta-issue', 'needs-triage'], 'meta-issue');
    const removed = event('unlabeled', [], 'meta-issue');
    for (const events of [[opened, added, removed], [removed, added, opened], [added, opened, removed, added]]) {
      for (const existing of [['bug'], ['bug', 'meta-issue', 'needs-triage']]) {
        const current = issue(existing);
        for (const payload of events) { await dispatch(payload, current); }
        assert.deepEqual(labels(current), existing.includes('meta-issue') ? ['bug', 'meta-issue'] : ['bug', 'needs-triage']);
      }
    }
  });
  await check('API read and write failures surface without silently reporting successful triage', async () => {
    for (const failure of [new Error('API unavailable'), Object.assign(new Error('Forbidden'), { status: 403 })]) {
      const ordinary = issue(['bug']);
      await assert.rejects(dispatch(event('opened'), ordinary, { readError: failure }), (error) => error === failure);
      await assert.rejects(dispatch(event('opened'), ordinary, { addError: failure }), (error) => error === failure);
      assert.deepEqual(labels(ordinary), ['bug']);
      const meta = issue(['meta-issue', 'needs-triage']);
      await assert.rejects(dispatch(event('labeled', [], 'meta-issue'), meta, { removeError: failure }), (error) => error === failure);
      assert.deepEqual(labels(meta), ['meta-issue', 'needs-triage']);
    }
  });
  await check('issue runs are serialized without cancellation and unrelated skipped events cannot replace them', () => {
    assert.equal(workflow.concurrency, undefined, 'Only eligible jobs should enter the concurrency group');
    assert.equal(job.concurrency['cancel-in-progress'], false);
    const group = (payload) => job.concurrency.group.replace(/\$\{\{([\s\S]*?)\}\}/g,
      (expression) => evaluate(expression, { workflow: workflow.name, event: payload }));
    for (const action of ['labeled', 'unlabeled']) {
      assert.equal(group(event('opened')), group(event(action, [], 'meta-issue')));
    }
    assert.notEqual(group(event('opened')), group({ issue: { number: 18 } }));
    assert.deepEqual(workflow.permissions, { issues: 'write' });
    assert.ok(!step.with.script.includes('${{'), 'Issue input must never be interpolated into script source');
    assert.ok(!job.steps.some((candidate) => candidate.uses?.startsWith('actions/checkout')));
  });
  await check('a triage label removed between read and delete is already successfully reconciled', async () => {
    const current = issue(['meta-issue', 'needs-triage', 'bug']);
    await dispatch(event('labeled', [], 'meta-issue'), current, {
      beforeRemove: (latest) => { latest.labels = latest.labels.filter((label) => label.name !== 'needs-triage'); }
    });
    assert.deepEqual(labels(current), ['bug', 'meta-issue']);
    assert.deepEqual(await dispatch(event('labeled', [], 'meta-issue'), current), ['get']);
  });
  console.log(`${checks} issue triage workflow checks passed.`);
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
