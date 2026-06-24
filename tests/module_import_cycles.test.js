const assert = require('assert');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

function fileText(relativePath) {
  return fs.readFileSync(path.resolve(ROOT, relativePath), 'utf8');
}

function run() {
  console.log('Running module import cycle regression tests...');

  // Cycle 1 (fixed): settings-labels.js → library-context.js → form-population.js → settings-labels.js
  // This cycle existed because form-population.js imported renderDuplicateStatusLabelSettings from
  // settings-labels.js while settings-labels.js imported loadLibrarySettings from library-context.js.
  // Fix: form-population.js now imports from settings/duplicate-labels.js (leaf) instead.
  const formPopulationSource = fileText('pb_public/staff/js/settings/form-population.js');
  assert.ok(
    !formPopulationSource.includes("from '../settings-labels.js'"),
    'form-population.js should not import from settings-labels.js (creates cycle). Should import from settings/duplicate-labels.js'
  );
  assert.ok(
    formPopulationSource.includes("from './duplicate-labels.js'"),
    'form-population.js should import duplicate labels from settings/duplicate-labels.js'
  );

  // Cycle 2 (fixed): settings-polaris.js → serialize-save.js → settings-polaris.js
  // This cycle existed because serialize-save.js imported collectSettingsPolaris and
  // collectEnabledLibraryIds from settings-polaris.js.
  // Fix: serialize-save.js now imports from settings/polaris-fields.js (leaf) instead.
  const serializeSaveSource = fileText('pb_public/staff/js/settings/serialize-save.js');
  assert.ok(
    !serializeSaveSource.includes("from '../settings-polaris.js'"),
    'serialize-save.js should not import from settings-polaris.js (creates cycle). Should import from settings/polaris-fields.js'
  );
  assert.ok(
    serializeSaveSource.includes("from './polaris-fields.js'"),
    'serialize-save.js should import Polaris fields from settings/polaris-fields.js'
  );

  console.log('All settings import cycle regression checks passed.');
}

run();
