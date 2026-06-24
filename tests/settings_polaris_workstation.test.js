const assert = require('assert');
const fs = require('fs');
const path = require('path');

const settingsSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings.js'), 'utf8');
const loaderSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/loader.js'), 'utf8');
const serializeSaveSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/serialize-save.js'), 'utf8');
const polarisFieldsSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/polaris-fields.js'), 'utf8');
const polarisSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings-polaris.js'), 'utf8');
const htmlSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/index.html'), 'utf8');

function extractFunction(source, name) {
  const marker = 'function ' + name + '(';
  const start = source.indexOf(marker);
  if (start < 0) throw new Error('Could not find ' + name);
  let depth = 0;
  let opened = false;
  for (let i = start; i < source.length; i++) {
    if (source[i] === '{') { depth++; opened = true; }
    if (source[i] === '}') { depth--; if (opened && depth === 0) return source.slice(start, i + 1); }
  }
  throw new Error('Could not extract ' + name);
}

assert.ok(htmlSource.includes('id="polaris-workstation-id-group"'), 'workstation field group should be present');
assert.ok(loaderSource.includes("setFieldValue('polaris-workstation-id', polaris.workstationId || '1');"), 'settings load paths should populate workstation ID with fallback');
assert.ok(polarisFieldsSource.includes("workstationId: getFieldValue('polaris-workstation-id') || \"1\""), 'collector should read workstation field');
assert.ok(!polarisFieldsSource.includes('workstationId: "1",'), 'collector should not hardcode workstation ID');

let values = {};
const collectSource = polarisFieldsSource
  .replace(/import[^;]+;\n/g, '')
  .replace(/\bexport\s+/g, '');
const collectFnSource = extractFunction(collectSource, 'collectSettingsPolaris');
const collectHarness = new Function('values', `
  function getFieldValue(id) { return values[id] || ''; }
  ${collectFnSource}
  return collectSettingsPolaris;
`)(values);

values['polaris-workstation-id'] = '88';
assert.strictEqual(collectHarness().workstationId, '88');
values['polaris-workstation-id'] = '';
assert.strictEqual(collectHarness().workstationId, '1');

const populateHarness = new Function(`
  const calls = [];
  function setFieldValue(id, value) { calls.push([id, value]); }
  ${extractFunction(loaderSource, 'populatePolarisSettingsForm')}
  populatePolarisSettingsForm({ workstationId: '77' });
  populatePolarisSettingsForm({});
  return calls;
`)();
assert.ok(populateHarness.some(([id, value]) => id === 'polaris-workstation-id' && value === '77'));
assert.ok(populateHarness.some(([id, value]) => id === 'polaris-workstation-id' && value === '1'));

assert.ok(serializeSaveSource.includes('const isSystemSave = currentLibraryContextOrgId === \'system\';'));
assert.ok(serializeSaveSource.includes('if (isSystemSave)') && serializeSaveSource.includes('libraryPayload.polaris = payload.polaris;'), 'library save path should not include global Polaris payload keys');

console.log('Settings Polaris workstation tests passed.');
