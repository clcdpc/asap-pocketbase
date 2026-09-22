const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-templates-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const systemTemplate = {
      id: '9007199254740993',
      organizationId: '1',
      templateKey: 'rejection:system-long',
      displayName: 'System rejection',
      subject: 'System subject',
      body: 'System body',
      enabled: true,
      isCustom: false,
      version: 'system-version'
    };
    const data = {
      orgId: '2',
      stored: {
        configuredSystem: { templates: [systemTemplate] },
        templates: [
          systemTemplate,
          {
            id: '21',
            organizationId: '2',
            sourceTemplateId: systemTemplate.id,
            templateKey: systemTemplate.templateKey,
            displayName: null,
            subject: null,
            body: 'Library body',
            enabled: true,
            isCustom: false,
            version: 'library-version'
          },
          {
            id: '22',
            organizationId: '2',
            templateKey: 'rejection:library-custom',
            displayName: 'Library custom',
            subject: 'Custom subject',
            body: 'Custom body',
            enabled: true,
            isCustom: true,
            version: 'custom-version'
          }
        ],
        libraryOverride: {}
      },
      effective: { formats: [], externalSearchProviders: [], customFields: [] }
    };
    const root = document.getElementById('settings-view');
    const editors = module.createSettingsDomainEditors({ root });
    editors.populate(data, false);

    const rows = [...document.querySelectorAll('#email-templates-editor [data-domain-row]')];
    assert.strictEqual(rows.length, 2);
    const lineage = rows.find(row => row.dataset.templateKind === 'lineage');
    assert.strictEqual(lineage.dataset.templateSourceId, systemTemplate.id);
    assert.strictEqual(lineage.querySelector('.template-subject').value, 'System subject');
    assert.strictEqual(lineage.querySelector('.template-body').value, 'Library body');
    assert.strictEqual(lineage.querySelector('.template-subject').disabled, false);

    lineage.querySelector('.settings-template-override').checked = false;
    lineage.querySelector('.settings-template-override').dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const reset = editors.collect();
    assert.ok(reset.templates.some(item => item.reset && item.sourceTemplateId === systemTemplate.id));
    assert.strictEqual(reset.templates.find(item => item.reset).sourceTemplateId, systemTemplate.id);

    const systemRoot = document.getElementById('settings-view');
    const systemEditors = module.createSettingsDomainEditors({ root: systemRoot });
    systemEditors.populate({
      orgId: 'system',
      stored: { configuredSystem: { templates: [systemTemplate] }, libraryOverride: {} },
      effective: { formats: [], externalSearchProviders: [], customFields: [] }
    }, true);
    assert.strictEqual(document.querySelectorAll('#email-templates-editor [data-domain-row]').length, 1);
    assert.strictEqual(document.getElementById('add-email-template').hidden, false);
    document.getElementById('add-email-template').click();
    assert.ok(systemEditors.collect().templates.some(item => item.templateKey.startsWith('rejection:')));

    dom.window.close();
    console.log('Settings template lineage, hiding/reset, system rejection, and bigint identity controls passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
