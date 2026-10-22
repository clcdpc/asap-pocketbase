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
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const panels = [...document.querySelectorAll('[data-settings-panel-content]')];
    assert.strictEqual(panels.filter(panel => !panel.hidden).map(panel => panel.id).join(','), 'settings-start');
    assert.strictEqual(document.getElementById('settings-organizations-list').tagName, 'UL');

    const header = document.querySelector('#settings-view .settings-header');
    assert.ok(header && !header.classList.contains('section-heading'));
    assert.deepStrictEqual([...header.querySelectorAll('[id]')].map(element => element.id), [
      'settings-title', 'settings-context-summary', 'settings-scope-field',
      'settings-scope', 'settings-refresh', 'settings-message'
    ], 'the compact header must preserve its hooks and logical reading order');
    const toolbar = header.querySelector('.settings-toolbar');
    assert.strictEqual(document.getElementById('settings-scope-field').parentElement, toolbar);
    assert.strictEqual(document.getElementById('settings-refresh').parentElement, toolbar);
    assert.strictEqual(document.getElementById('settings-message').getAttribute('role'), 'status');
    assert.strictEqual(document.getElementById('settings-message').getAttribute('aria-live'), 'polite');
    const settingsSource = fs.readFileSync(path.join(frontendRoot, 'staff', 'js', 'settings.js'), 'utf8');
    for (const match of settingsSource.matchAll(/(?:querySelector\(['"]#|getElementById\(['"])([\w-]+)['"]\)/g)) {
      assert.strictEqual(document.querySelectorAll(`#${match[1]}`).length, 1, match[1]);
    }

    const controls = scope => [...scope.querySelectorAll('input, select, textarea')].map(control => control.id);
    const fieldset = id => document.getElementById(id).closest('fieldset');
    const legend = id => fieldset(id).querySelector('legend').textContent;
    const wide = id => assert.ok(document.getElementById(id).closest('label').classList.contains('wide'), id);
    assert.deepStrictEqual(controls(document.getElementById('settings-polaris')), [
      'polaris-host', 'polaris-access-id', 'polaris-api-key', 'polaris-domain',
      'polaris-admin-user', 'polaris-admin-pass', 'polaris-workstation-id', 'polaris-system-user-id'
    ]);
    for (const [ids, name] of [
      [['polaris-host', 'polaris-access-id', 'polaris-api-key'], 'Polaris API'],
      [['polaris-domain', 'polaris-admin-user', 'polaris-admin-pass'], 'System staff account'],
      [['polaris-workstation-id', 'polaris-system-user-id'], 'System identity']
    ]) {
      assert.deepStrictEqual(controls(fieldset(ids[0])), ids);
      assert.strictEqual(legend(ids[0]), name);
    }
    assert.deepStrictEqual(controls(document.getElementById('settings-system-links')), [
      'system-staff-url', 'leap-bib-url-pattern', 'leap-patron-url-pattern',
      'format-icon-url-pattern', 'patron-embed-allowed-origins', 'ui-system-not-enabled-msg', 'ui-misconfigured-msg'
    ]);
    assert.deepStrictEqual(controls(fieldset('email-from-address')), ['email-from-address', 'email-from-name']);
    assert.strictEqual(legend('email-from-address'), 'Sender identity');
    assert.deepStrictEqual(controls(fieldset('email-postmark-token')), ['email-postmark-token', 'email-clear-postmark-token']);
    assert.strictEqual(legend('email-postmark-token'), 'Postmark connection');
    assert.strictEqual(document.getElementById('email-token-status').closest('fieldset'), fieldset('email-postmark-token'));
    assert.deepStrictEqual(controls(fieldset('staff-add-email')), ['staff-add-email', 'staff-add-role', 'staff-add-organization']);
    assert.deepStrictEqual(controls(fieldset('patron-page-title')).slice(0, 3), ['patron-page-title', 'patron-barcode-label', 'patron-pin-label']);
    assert.strictEqual(legend('auto-promote'), 'Suggestion and eligibility');
    assert.strictEqual(legend('outstanding-timeout-enabled'), 'Timeouts');
    assert.deepStrictEqual(controls(fieldset('outstanding-timeout-enabled')), [
      'outstanding-timeout-enabled', 'outstanding-timeout-days',
      'outstanding-timeout-send-email', 'outstanding-timeout-rejection-template-id',
      'hold-pickup-timeout-enabled', 'hold-pickup-timeout-days',
      'pending-hold-timeout-enabled', 'pending-hold-timeout-days',
      'additional-copy-timeout-enabled', 'additional-copy-timeout-days'
    ]);
    ['polaris-host', 'polaris-domain', 'system-staff-url', 'format-icon-url-pattern',
      'email-postmark-token', 'email-clear-postmark-token', 'staff-add-email', 'patron-page-title'].forEach(wide);
    const ids = [...document.querySelectorAll('[id]')].map(element => element.id);
    assert.strictEqual(new Set(ids).size, ids.length, 'control IDs must remain unique');
    for (const control of document.querySelectorAll('#settings-view input:not([type="hidden"]), #settings-view select, #settings-view textarea')) {
      assert.ok(control.closest('label') || document.querySelector(`label[for="${control.id}"]`), control.id);
    }
    const css = fs.readFileSync(path.join(frontendRoot, 'staff', 'styles.css'), 'utf8');
    assert.match(css, /\.settings-save-bar\s*\{\s*position:\s*static;/);
    assert.match(css, /\.settings-save-bar\.attention\s*\{\s*position:\s*sticky;\s*bottom:\s*0;/);
    assert.match(css, /@media[^{}]*max-width:\s*760px[^]*?\.settings-grid\s*\{\s*grid-template-columns:\s*1fr;/);

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
    console.log('Settings header hooks, save-bar styles, field layout, and dynamic labels passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
