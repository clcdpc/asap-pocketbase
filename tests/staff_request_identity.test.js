const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const identityModule = path.join(
  __dirname,
  '..',
  'src',
  'Asap.Web',
  'Frontend',
  'staff',
  'js',
  'request-identity.mjs'
);

(async () => {
  const {
    findWorkflowRow,
    requestIdentity,
    requestIdentityKey,
    sameRequestIdentity
  } = await import(pathToFileURL(identityModule).href);

  const id = '90071992547409931234';
  const title = { id, type: 'title_request', title: 'Collision title' };
  const copy = { id, type: 'additional_copy', title: 'Collision copy' };
  const rows = [title, copy];

  assert.deepEqual(requestIdentity(title), { type: 'title_request', id });
  assert.deepEqual(requestIdentity(copy), { type: 'additional_copy', id });
  assert.equal(findWorkflowRow(requestIdentity(title), rows), title);
  assert.equal(findWorkflowRow(requestIdentity(copy), rows), copy);
  assert.equal(sameRequestIdentity(title, copy), false);
  assert.notEqual(requestIdentityKey(title), requestIdentityKey(copy));
  assert.equal(requestIdentity({ id }).type, 'title_request', 'legacy title rows keep their implied type');

  const input = { value: '', dataset: {} };
  const { editRequestIdentity, setEditRequestIdentity } = await import(pathToFileURL(identityModule).href);
  setEditRequestIdentity(input, copy);
  assert.deepEqual(editRequestIdentity(input), { type: 'additional_copy', id });

  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-request-identity-'));
  try {
    const staffJs = path.dirname(identityModule);
    fs.cpSync(staffJs, path.join(temporary, 'js'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.gridjs = { html: value => value };
    const rendering = await import(pathToFileURL(path.join(temporary, 'js', 'grid-rendering.js')).href);
    const utils = await import(pathToFileURL(path.join(temporary, 'js', 'grid-utils.js')).href);
    assert.match(rendering.rowMarker(title), /data-request-type="title_request"/);
    assert.match(rendering.rowMarker(copy), /data-request-type="additional_copy"/);
    assert.match(utils.formatNote({ ...title, notes: 'Title note' }), /data-request-type="title_request"/);
    assert.match(utils.formatNote({ ...copy, notes: 'Copy note' }), /data-request-type="additional_copy"/);
    const searchButton = rendering.renderPolarisRowSearchButton(copy, 'title', {
      polarisSearchValueForRow: () => 'Collision copy',
      renderPolarisSearchButtonMarkup: (_mode, attrs) => JSON.stringify(attrs)
    });
    assert.match(searchButton, /additional_copy/);
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }

  console.log('Staff request identity collision checks passed');
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
