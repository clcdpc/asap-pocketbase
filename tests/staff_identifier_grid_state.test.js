const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const policy = await import(pathToFileURL(path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend',
    'staff', 'js', 'grid-policy.mjs')).href);
  const row = {
    bibid: '9002',
    isbnCheckStatus: 'not_found',
    identifierPresentation: 'not_verified_on_bib',
    workflowTags: []
  };
  assert.equal(policy.getIsbnCheckLabel(row), 'Identifier not verified on selected BIB');
  const unprocessed = {
    isbnCheckStatus: 'not_found',
    identifierPresentation: 'not_completed',
    workflowTags: []
  };
  assert.equal(policy.getIsbnCheckLabel(unprocessed), 'Identifier check not completed');
  assert.deepEqual(policy.effectiveWorkflowFlagsForRow({ ...row, workflowTags: ['Identifier number not found'] }),
    ['Identifier number not found in system']);
  assert.deepEqual(policy.effectiveWorkflowFlagsForRow({ ...row, isbnCheckStatus: 'found',
    workflowTags: ['Identifier number found'] }), ['Identifier found']);
  console.log('Staff identifier grid state follows identifier evidence, not BIB presence.');
})().catch(error => { console.error(error); process.exitCode = 1; });
