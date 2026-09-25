const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const { projectLegacySettingsFixture } = require('./helpers/legacy-settings-fixture.cjs');

(async () => {
  const root = path.join(__dirname, '..');
  const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-response-'));
  fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();
    global.fetch = async request => {
      const url = String(request);
      const value = url.includes('/legacy/organizations') ? [
        { id: '1', displayName: 'System', active: true },
        { id: '2', displayName: 'Harbor City Library', active: true }
      ] : url.includes('/legacy/patron-codes') ? [
        { id: '31', description: 'Adult' }, { id: '47', description: 'Young adult' }
      ] : url.includes('/email-status') ? { enabled: false } : { users: [] };
      return { ok: true, status: 200, statusText: 'OK', json: async () => value };
    };

    const moduleAt = name => import(pathToFileURL(path.join(temporary, 'staff', 'js', name)).href);
    const [state, form, serializer] = await Promise.all([
      moduleAt('state.js'), moduleAt('settings/form-population.js'),
      moduleAt('settings/serialize-save.js')
    ]);
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { role: 'super_admin', userPrincipalName: 'admin@example.org' }
    });
    const libraryRaw = JSON.parse(fs.readFileSync(path.join(
      __dirname, 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    const systemRaw = JSON.parse(fs.readFileSync(path.join(
      __dirname, 'fixtures', 'settings', 'canonical-system-settings-response.json'), 'utf8'));
    const library = projectLegacySettingsFixture(libraryRaw);
    const system = projectLegacySettingsFixture(systemRaw);
    assert.strictEqual(Object.hasOwn(library, 'stored'), false);
    assert.strictEqual(Object.hasOwn(library, 'effective'), false);
    assert.strictEqual(library.orgId, '2');
    assert.strictEqual(library.version, libraryRaw.version);
    assert.strictEqual(library.providers.length, 3);
    assert.strictEqual(library.formats.find(row => row.code === 'zine').id, '9007199254741003');
    assert.strictEqual(library.autoClaimRules[0].staffUserId, '9007199254740993');
    assert.strictEqual(library.autoClaimStaff[0].displayName, 'Harbor Librarian');
    assert.strictEqual(library.autoClaimStaff[0].libraryOrgId, '2');
    assert.strictEqual(library.uiText.formatRules.zine.customFields.audience_note.labelOverride, 'Zine audience');
    assert.strictEqual(library.templates.find(row => row.templateKey === 'suggestion_submitted').subject,
      'Harbor request received: {{title}}');

    async function render(model) {
      state.setCurrentLibraryContextOrgId(model.orgId);
      await form.applyLibrarySettingsToForm(structuredClone(model));
      serializer.rememberLastSavedLibrarySettings(model);
      serializer.captureSettingsBaseline();
    }
    await render(library);
    assert.strictEqual(state.currentLegacySettingsForm.orgId, '2');
    assert.strictEqual(document.getElementById('wf-common-authors-list').value, 'N. K. Jemisin\nOctavia E. Butler');
    assert.strictEqual(document.getElementById('wf-external-search-2-label').value, 'Harbor Research Index');
    assert.strictEqual(document.getElementById('email-submit-subject').value, 'Harbor request received: {{title}}');
    assert.strictEqual(document.getElementById('postmark-token').value, '');
    assert.strictEqual(document.getElementById('postmark-token-status').classList.contains('hidden'), false);
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="zine"] .format-claim-staff-select').value,
      '9007199254740993');
    assert.strictEqual(document.querySelector('.format-rule-custom-field-label[data-format="zine"][data-field="audience_note"]').value,
      'Zine audience');
    assert.deepStrictEqual(serializer.buildSettingsPayload(), {},
      'a rendered flat response must establish a no-edit baseline');

    document.getElementById('ui-login-note').value = 'Show this local login note';
    const scalarChange = serializer.buildSettingsPayload();
    assert.deepStrictEqual(scalarChange, { ui_text: { loginNote: 'Show this local login note' } });
    await render(library);

    document.getElementById('email-submit-subject').value = 'Harbor received {{title}}';
    const templateChange = serializer.buildSettingsPayload();
    assert.strictEqual(templateChange.emails.suggestion_submitted.subject, 'Harbor received {{title}}');
    assert.strictEqual(Object.hasOwn(templateChange.emails, 'purchase_approved'), false,
      'editing one template must leave inherited siblings untouched');
    await render(library);

    document.getElementById('wf-external-search-2-label').value = 'Local Research';
    const providerChange = serializer.buildSettingsPayload();
    assert.strictEqual(providerChange.providers.find(row => row.key === 'external_search_2').label, 'Local Research');
    assert.strictEqual(Object.hasOwn(providerChange, 'workflow'), false);
    await render(library);

    const customLabel = document.querySelector('.format-rule-custom-field-label[data-format="zine"][data-field="audience_note"]');
    customLabel.value = 'Community audience';
    const ruleChange = serializer.buildSettingsPayload();
    assert.strictEqual(ruleChange.formatRules.zine.customFields.audience_note.labelOverride, 'Community audience');
    assert.strictEqual(Object.hasOwn(ruleChange, 'formats'), false,
      'a custom field rule edit does not rewrite the format ownership list');

    const nullable = structuredClone(library);
    nullable.workflow.suggestionLimit = null;
    nullable.workflow.outstandingTimeoutDays = null;
    await render(nullable);
    assert.strictEqual(document.getElementById('suggestion-limit').value, '');
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '');
    assert.strictEqual(Object.hasOwn(serializer.buildSettingsPayload().workflow || {}, 'suggestionLimit'), false,
      'a null inherited value must stay absent on a no-edit save');

    await render(system);
    assert.strictEqual(state.currentLegacySettingsForm.orgId, 'system');
    assert.strictEqual(document.getElementById('ui-system-not-enabled-msg').value,
      systemRaw.stored.systemSettings.systemNotEnabledMessage);
    assert.strictEqual(document.getElementById('ui-misconfigured-msg').value,
      systemRaw.stored.systemSettings.misconfiguredMessage);
    assert.strictEqual(document.getElementById('wf-external-search-4-label').disabled, true,
      'the unconfigured fourth provider slot is disabled');

    console.log('Flat settings response and direct form projection checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
