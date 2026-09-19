const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-a11y-layout-'));
  fs.cpSync(path.join(frontendRoot, 'staff-next'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff-next', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const panels = [...document.querySelectorAll('[data-settings-panel-content]')];
    assert.strictEqual(panels.filter(panel => !panel.hidden).map(panel => panel.id).join(','), 'settings-start');
    assert.strictEqual(document.getElementById('settings-organizations-list').tagName, 'UL');

    const formats = Array.from({ length: 6 }, (_, index) => ({
      id: String(index + 10),
      code: `format_${index + 1}`,
      ownerOrganizationId: 1,
      label: `Format ${index + 1}`,
      isEnabled: true,
      messageBehavior: 'none',
      fields: {}
    }));
    const root = document.getElementById('settings-view');
    const editors = module.createSettingsDomainEditors({ root });
    editors.populate({
      orgId: 'system',
      stored: {
        configuredSystem: {
          publicationOptions: { exists: false, values: [] },
          commonCreators: { exists: false, values: [] },
          allowedPatronCodeIds: { exists: false, values: [] },
          providers: [],
          formats,
          templates: []
        },
        libraryOverride: {},
        customFields: [],
        autoClaimRules: [],
        templates: []
      },
      effective: {
        externalSearchProviders: [],
        formats,
        customFields: []
      },
      patronCodeChoices: [],
      autoClaimStaff: []
    }, true);

    const labels = [...document.querySelectorAll('input[data-format-label]')];
    const modes = [...document.querySelectorAll('select[data-format-field]')];
    assert.strictEqual(labels.length, 24);
    assert.strictEqual(modes.length, 24);
    assert.ok(labels.every(input => input.getAttribute('aria-label')));
    assert.ok(modes.every(select => select.getAttribute('aria-label')));

    dom.window.close();
    console.log('Settings initial visibility, semantic lists, and dynamic format labels passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
