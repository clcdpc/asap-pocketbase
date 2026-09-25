const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-duplicate-status-labels-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();

    const moduleAt = name => import(pathToFileURL(path.join(temporary, 'staff', 'js', name)).href);
    const [state, labels, serializer, gridActions] = await Promise.all([
      moduleAt('state.js'),
      moduleAt('settings/duplicate-labels.js'),
      moduleAt('settings/serialize-save.js'),
      moduleAt('grid-actions.js')
    ]);
    state.setCurrentLibraryContextOrgId('system');

    labels.renderDuplicateStatusLabelSettings({
      suggestion: 'Previously configured label',
      duplicate_hold: 'Unsupported custom value'
    });
    assert.strictEqual(document.getElementById('duplicate-status-duplicate_hold'), null,
      'duplicate_hold must not render as an editable Settings control.');

    const suggestionInput = document.getElementById('duplicate-status-suggestion');
    assert.ok(suggestionInput, 'supported status labels remain editable.');
    suggestionInput.value = 'Accepted suggestion';
    const collected = labels.collectDuplicateStatusLabels();
    assert.strictEqual(collected.suggestion, 'Accepted suggestion');
    assert.strictEqual(Object.hasOwn(collected, 'duplicate_hold'), false);

    const serialized = serializer.serializeSettingsState().ui_text.duplicateStatusLabels;
    assert.strictEqual(serialized.suggestion, 'Accepted suggestion');
    assert.strictEqual(Object.hasOwn(serialized, 'duplicate_hold'), false,
      'The Settings serializer must not submit duplicate_hold as mutable state.');

    assert.strictEqual(state.duplicateStatusLabelDefaults.duplicate_hold, 'Duplicate hold / request');
    assert.strictEqual(gridActions.formatCloseReason(
      { status: 'closed', closeReason: 'duplicate_hold' },
      { closeReasonMap: state.closeReasonMap }
    ), 'Duplicate hold / request', 'Runtime duplicate-hold presentation keeps its static fallback.');

    dom.window.close();
    console.log('Duplicate status label editor, serializer, and runtime fallback checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
