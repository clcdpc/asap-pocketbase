const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const YAML = require('yaml');

const workflow = YAML.parse(fs.readFileSync(path.join(__dirname, '../.github/workflows/issue-triage.yml'), 'utf8'), { uniqueKeys: true });
const job = workflow.jobs['label-new-issue'];

async function main() {
  assert.deepEqual(workflow.on.issues.types, ['opened']);
  assert.equal(job.steps[0].run, 'gh issue edit "$ISSUE_NUMBER" --add-label "needs-triage"');

  const { Lexer, Parser, Evaluator, data } = await import('@actions/expressions');
  const source = job.if.match(/^\$\{\{([\s\S]*)\}\}$/)?.[1];
  assert.ok(source, 'Expected a single Actions expression');
  const ast = new Parser(new Lexer(source).lex().tokens, ['github'], []).parse();
  function enabled(labels) {
    const context = { github: { event: { issue: { labels: labels.map((name) => ({ name })) } } } };
    return new Evaluator(ast, JSON.parse(JSON.stringify(context), data.reviver)).evaluate().coerceString() === 'true';
  }

  for (const labels of [[], ['bug']]) {
    assert.equal(enabled(labels), true, 'Ordinary new issues receive needs-triage');
  }
  console.log('PASS ordinary new issues receive needs-triage');
  for (const labels of [['meta-issue'], ['bug', 'meta-issue']]) {
    assert.equal(enabled(labels), false, 'New meta issues skip the triage label action');
  }
  console.log('PASS new meta issues skip needs-triage');
  console.log('2 issue triage creation checks passed.');
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
