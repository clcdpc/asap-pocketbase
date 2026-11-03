const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return { ok: status >= 200 && status < 300, status, statusText: 'OK', json: async () => body };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
}

(async () => {
  const root = path.join(__dirname, '..');
  const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-email-admin-'));
  fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const { createSettingsController } = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://reviewer:secret@devasap.clcohio.org:8443/staff/?stage=settings&settingsScope=system#settings-start'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    document.getElementById('settings-view').hidden = false;
    document.getElementById('workspace').hidden = false;
    require('./helpers/confirmation.cjs').answerConfirmations(window, () => true);

    let version = 1;
    let fromName = 'System';
    let staffUrl = 'https://staff.example.org';
    let hasToken = true;
    const organizations = [
      { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-system-v1' },
      { id: 2, name: 'Library Two', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-two-v1' },
      { id: 3, name: 'Library Three', organizationCodeId: 2, parentOrganizationId: 1, isActive: false, version: 'org-three-v1' }
    ];
    let organizationResponse = { code: 'ok', data: organizations };
    const saved = [];
    const template = {
      id: '9007199254740993', organizationId: '1', templateKey: 'suggestion_submitted',
      subject: '  Hello {{name}}  ', body: 'Body\n{{title}}  ', enabled: true, isCustom: false
    };
    const emptySet = { exists: false, values: [] };
    const data = () => ({
      orgId: 'system', version: `v${version}`,
      organization: { id: 1, name: 'System', active: true },
      stored: {
        systemSettings: { staffUrl, enabledLibraryOrgIds: [2], libraryOrgIds: [2, 3] }, polaris: {},
        configuredSystem: {
          workflow: {}, patron: {}, email: { fromAddress: 'system@example.org', fromName, hasPostmarkToken: hasToken },
          publicationOptions: emptySet, commonCreators: emptySet, allowedPatronCodeIds: emptySet,
          providers: [], formats: [], templates: [template], branding: {}
        },
        libraryOverride: null, workflow: {}, patron: {}, email: {}, origins: [],
        publicationOptions: [], commonCreators: [], allowedPatronCodeIds: [], providers: [], formats: [],
        customFields: [], formatRules: [],
        templates: [template], autoClaimRules: [], branding: {}
      },
      effective: { allowedPatronCodeIds: [], publicationOptions: [], commonCreators: [],
        externalSearchProviders: [], formats: [], customFields: [] },
      workflow: {}, ui_text: {},
      emails: { fromAddress: 'system@example.org', fromName, templates: [template] },
      patronCodeChoices: [],
      autoClaimStaff: [],
      templatePlaceholders: ['name', 'title']
    });
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=system')) return response(200, data());
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, organizationResponse);
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        const payload = JSON.parse(options.body);
        saved.push(payload);
        fromName = payload.email.fromName ?? fromName;
        if (Object.hasOwn(payload.systemSettings || {}, 'staffUrl')) staffUrl = payload.systemSettings.staffUrl;
        if (payload.email.clearPostmarkToken) hasToken = false;
        version += 1;
        return response(200, { code: 'saved', data: { orgId: 'system', version: `v${version}` } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {}
    });
    controller.bind();
    controller.setStaff({ id: '1', tenantId: 'tenant', role: 'super_admin', organizationId: 1 });
    await controller.activate('templates');
    const staffUrlInput = document.getElementById('system-staff-url');
    const useCurrentStaffUrl = document.getElementById('use-current-staff-url');
    const staffUrlCandidate = document.getElementById('system-staff-url-candidate');
    const expectedCandidate = 'https://devasap.clcohio.org:8443/staff/';
    assert.strictEqual(staffUrlInput.value, 'https://staff.example.org', 'Loading a configured URL does not replace it with the candidate.');
    assert.strictEqual(staffUrlCandidate.textContent, `Current address candidate: ${expectedCandidate}`,
      'The preview uses the current HTTPS origin and port, without credentials, query, or hash.');
    assert.strictEqual(useCurrentStaffUrl.hidden, false);
    assert.strictEqual(controller.isDirty(), false, 'Displaying a candidate does not create a draft.');
    staffUrl = '';
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(staffUrlInput.value, '', 'A blank saved URL remains blank until the action is selected.');
    assert.strictEqual(controller.isDirty(), false);
    staffUrl = 'https://staff.example.org';
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(staffUrlInput.value, staffUrl, 'Reloading a configured URL does not apply the candidate.');
    document.getElementById('email-submit-subject').disabled = true;
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(document.getElementById('email-submit-subject').disabled, false,
      'Repopulating System templates restores controls previously disabled by library inheritance.');
    assert.strictEqual(document.getElementById('email-postmark-token').value, '');
    assert.strictEqual(document.getElementById('email-token-status').textContent.includes('stored'), true);
    assert.deepStrictEqual([...document.querySelectorAll('#template-placeholder-list button')]
      .map(button => button.textContent), ['{{name}}', '{{title}}']);
    document.querySelector('#template-placeholder-help details').open = true;
    assert.strictEqual(controller.isDirty(), false);
    const subject = document.getElementById('email-submit-subject');
    subject.focus();
    subject.setSelectionRange(2, 2);
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    document.querySelector('#template-placeholder-list button').click();
    assert.strictEqual(subject.value, '  {{name}}Hello {{name}}  ');
    assert.strictEqual(document.getElementById('email-submit-body').value, template.body);
    assert.strictEqual(controller.isDirty(), true);
    document.getElementById('settings-discard').click();
    for (let i = 0; i < 10 && controller.isDirty(); i++) await flush();
    assert.strictEqual(subject.value, template.subject);
    document.querySelector('#template-placeholder-list button').click();
    assert.strictEqual(subject.value, template.subject, 'Discard must clear the old insertion target.');
    subject.focus();
    subject.setSelectionRange(0, 0);
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    controller.activatePanel('workflow');
    controller.activatePanel('templates');
    document.querySelector('#template-placeholder-list button').click();
    assert.strictEqual(subject.value, template.subject, 'Panel departure invalidates the previous target.');
    subject.focus();
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    controller.suspend();
    await controller.activate('templates');
    document.querySelector('#template-placeholder-list button').click();
    assert.strictEqual(subject.value, template.subject, 'Suspending Settings invalidates the previous target.');
    assert.strictEqual(controller.isDirty(), false, 'Browsing guidance never creates a settings draft.');

    const name = document.getElementById('email-from-name');
    name.value = 'Changed sender';
    name.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    for (let i = 0; i < 15 && saved.length < 1; i++) await flush();
    assert.strictEqual(saved.length, 1,
      `a duplicate submit must not send a second settings mutation (${document.getElementById('settings-message').textContent})`);
    assert.strictEqual(saved[0].email.postmarkToken, undefined);
    assert.strictEqual(saved[0].email.clearPostmarkToken, undefined);
    assert.deepStrictEqual(saved[0].templates, []);
    assert.strictEqual(Object.hasOwn(saved[0].systemSettings, 'enabledLibraryOrgIds'), false,
      'An unrelated system settings save must omit participation replacement data.');

    for (let i = 0; i < 15 && document.getElementById('settings-version').value !== 'v2'; i++) await flush();
    const clear = document.getElementById('email-clear-postmark-token');
    clear.checked = true;
    clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    for (let i = 0; i < 15 && saved.length < 2; i++) await flush();
    assert.strictEqual(saved[1].email.clearPostmarkToken, true);
    assert.strictEqual(saved[1].email.postmarkToken, undefined);

    organizationResponse = { code: 'ok', data: [] };
    assert.strictEqual(await controller.load({ silent: true }), false, 'A noncomplete organization catalog must not load as empty.');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    staffUrlInput.value = 'https://must-not-save.example.org';
    staffUrlInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await flush();
    assert.strictEqual(saved.length, 2, 'A system settings submission must remain blocked after a failed catalog load.');

    organizationResponse = { code: 'ok', data: organizations.filter(item => item.id !== 3) };
    assert.strictEqual(await controller.load({ silent: true }), false, 'A partial library catalog must not be accepted.');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);

    organizationResponse = { code: 'ok', data: organizations };
    assert.strictEqual(await controller.load({ silent: true }), true, 'A complete organization catalog should restore editing.');
    staffUrlInput.value = 'https://complete-catalog-edit.example.org';
    staffUrlInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    for (let i = 0; i < 15 && saved.length < 3; i++) await flush();
    assert.strictEqual(saved.length, 3);
    assert.strictEqual(saved[2].systemSettings.staffUrl, 'https://complete-catalog-edit.example.org');
    assert.strictEqual(Object.hasOwn(saved[2].systemSettings, 'enabledLibraryOrgIds'), false);
    for (let attempt = 0; attempt < 30 &&
      (document.getElementById('settings-version').value !== 'v4' || controller.isDirty() || controller.hasPendingMutation());
      attempt++) await flush();
    assert.strictEqual(document.getElementById('settings-version').value, 'v4', 'The third save finishes its authoritative reload before the next draft action.');
    assert.strictEqual(controller.isDirty(), false);
    assert.strictEqual(controller.hasPendingMutation(), false);

    controller.activatePanel('start');
    const form = document.getElementById('settings-form');
    const savedStaffUrl = staffUrlInput.value;
    form.inert = true;
    useCurrentStaffUrl.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true }));
    assert.strictEqual(staffUrlInput.value, savedStaffUrl, 'A stale/inert Settings context cannot apply a URL candidate.');
    form.inert = false;
    useCurrentStaffUrl.click();
    assert.strictEqual(staffUrlInput.value, expectedCandidate);
    assert.strictEqual(staffUrlCandidate.textContent,
      `Staff URL draft updated to ${expectedCandidate}. Save settings to persist it.`);
    assert.strictEqual(controller.isDirty(), true, 'Applying the candidate follows the normal draft path.');
    assert.strictEqual(document.getElementById('settings-save').disabled, false);
    await flush(); await flush();
    assert.strictEqual(saved.length, 3, 'Applying the candidate does not autosave.');
    form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    for (let attempt = 0; attempt < 30 && saved.length < 4; attempt++) await flush();
    assert.strictEqual(saved.length, 4);
    assert.strictEqual(saved[3].systemSettings.staffUrl, expectedCandidate, 'The normal Settings payload persists the selected URL.');
    for (let attempt = 0; attempt < 30 && (staffUrlInput.value !== expectedCandidate || controller.isDirty()); attempt++) await flush();
    assert.strictEqual(staffUrlInput.value, expectedCandidate, 'The authoritative reload retains the saved URL.');
    assert.strictEqual(controller.isDirty(), false);

    dom.reconfigure({ url: 'https://devasap.clcohio.org:8443/staff/?stage=settings&settingsScope=system#settings-start' });
    assert.strictEqual(await controller.load({ silent: true }), true);
    controller.activatePanel('start');
    assert.strictEqual(staffUrlCandidate.textContent, `Current address candidate: ${expectedCandidate}`);
    useCurrentStaffUrl.click();
    assert.strictEqual(staffUrlInput.value, expectedCandidate);
    assert.strictEqual(staffUrlCandidate.textContent, `Staff URL already matches the current address: ${expectedCandidate}.`);
    assert.strictEqual(controller.isDirty(), false, 'Applying the already-saved candidate does not create a false draft.');
    assert.strictEqual(document.getElementById('settings-save').disabled, true,
      'An already-saved candidate does not request a redundant save.');
    await flush(); await flush();
    assert.strictEqual(saved.length, 4);

    const staffUrlEvents = [];
    for (const eventName of ['input', 'change']) {
      staffUrlInput.addEventListener(eventName, () => staffUrlEvents.push(eventName));
    }
    const manualStaffUrl = 'https://manual-draft.example.org';
    staffUrlInput.value = manualStaffUrl;
    staffUrlInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(controller.isDirty(), true);
    const urlEventsBeforeIdentityReplacement = staffUrlEvents.length;
    assert.strictEqual(useCurrentStaffUrl.hidden, false);
    assert.notStrictEqual(staffUrlInput.value, expectedCandidate);
    controller.setStaff({ id: '2', tenantId: 'tenant', role: 'super_admin', organizationId: 1 });
    assert.strictEqual(useCurrentStaffUrl.hidden, false, 'The retained action still has its valid prior candidate before the replacement actor loads settings.');
    assert.notStrictEqual(staffUrlInput.value, expectedCandidate, 'The replacement actor inherits a distinct manual draft in the stale form.');
    useCurrentStaffUrl.click();
    await flush(); await flush();
    assert.strictEqual(staffUrlInput.value, manualStaffUrl, 'A retained action cannot apply its candidate after the actor identity changes.');
    assert.strictEqual(staffUrlEvents.length, urlEventsBeforeIdentityReplacement, 'A stale action emits no input or change event.');
    assert.strictEqual(saved.length, 4, 'A stale action sends no settings POST.');

    dom.reconfigure({ url: 'http://[::1]:5012/staff/?stage=settings#settings-start' });
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(staffUrlCandidate.textContent, 'Current address candidate: http://[::1]:5012/staff/',
      'The HTTP loopback exception handles URL.hostname IPv6 brackets and retains its port.');
    assert.strictEqual(useCurrentStaffUrl.hidden, false);
    assert.strictEqual(staffUrlInput.value, expectedCandidate, 'Changing the browser address never overwrites the saved setting.');

    dom.reconfigure({ url: 'http://devasap.clcohio.org:5012/staff/?stage=settings#settings-start' });
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(useCurrentStaffUrl.hidden, true, 'Non-loopback HTTP does not offer a candidate.');
    assert.match(staffUrlCandidate.textContent, /Current address unavailable/);
    dom.reconfigure({ url: 'ftp://devasap.clcohio.org/staff/' });
    assert.strictEqual(await controller.load({ silent: true }), true);
    assert.strictEqual(useCurrentStaffUrl.hidden, true, 'Unsupported schemes do not offer a candidate.');
    assert.strictEqual(staffUrlInput.value, expectedCandidate);

    dom.window.close();
    console.log('System token intents, Staff URL suggestion draft/save/reload, placeholder caret insertion, and unchanged template text passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
