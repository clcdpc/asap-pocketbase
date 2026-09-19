const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-display-order-'));
  try {
    const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'display-order.js');
    const target = path.join(temporary, 'display-order.js');
    fs.copyFileSync(source, target);
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
    const { organizationDisplayName, sortByDisplayLabel, staffDisplayName } =
      await import(pathToFileURL(target).href);

    const organizations = [
      { id: '5', name: 'alpha library' },
      { id: '4', name: null },
      { id: '3', name: 'Zeta Library' },
      { id: '2', name: 'Alpha Library' }
    ];
    const sorted = sortByDisplayLabel(organizations, organizationDisplayName, item => item.id);

    assert.deepStrictEqual(sorted.map(item => item.id), ['2', '5', '4', '3']);
    assert.deepStrictEqual(organizations.map(item => item.id), ['5', '4', '3', '2']);
    assert.equal(organizationDisplayName(organizations[1]), 'Library 4');
    assert.equal(staffDisplayName({ id: 22, displayName: '  ' }), 'Staff 22');

    console.log('Display-order helper uses locale-aware deterministic sorting without mutating input');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
