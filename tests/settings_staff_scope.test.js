const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: 'OK',
    json: async () => body
  };
}

const systemTimeoutValues = {
  outstandingTimeoutEnabled: true,
  outstandingTimeoutDays: 30,
  outstandingTimeoutSendEmail: true,
  outstandingTimeoutRejectionTemplateId: null,
  holdPickupTimeoutEnabled: true,
  holdPickupTimeoutDays: 7,
  pendingHoldTimeoutEnabled: null,
  pendingHoldTimeoutDays: 15,
  additionalCopyTimeoutEnabled: false,
  additionalCopyTimeoutDays: 21
};
const libraryTimeoutOverrides = {
  outstandingTimeoutEnabled: false,
  outstandingTimeoutDays: 45,
  outstandingTimeoutSendEmail: true,
  outstandingTimeoutRejectionTemplateId: '9007199254740993',
  holdPickupTimeoutDays: 9,
  pendingHoldTimeoutEnabled: true,
  pendingHoldTimeoutDays: 18,
  additionalCopyTimeoutDays: 33
};
const timeoutKeys = new Set(Object.keys(systemTimeoutValues));

function settingsData(librarySetsExist = false) {
  const emptySet = { exists: false, values: [] };
  const librarySet = { exists: librarySetsExist, values: [] };
  return {
    orgId: '2',
    version: 'library-2-version',
    organization: { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem: {
        workflow: { ...systemTimeoutValues },
        patron: {},
        email: { fromAddress: 'system@example.org', fromName: 'System' },
        publicationOptions: emptySet,
        commonCreators: emptySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: 'System alt' }
      },
      libraryOverride: {
        workflow: { ...libraryTimeoutOverrides },
        patron: { loginNote: 'Existing library note' },
        email: { hasPostmarkToken: true },
        publicationOptions: librarySet,
        commonCreators: librarySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: null }
      },
      workflow: { ...libraryTimeoutOverrides },
      patron: {},
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: [],
      allowedPatronCodeIds: [],
      providers: [],
      formats: [],
      formatRules: [],
      customFields: [],
      templates: [{ id: '9007199254740993', organizationId: '2', templateKey: 'rejection:used',
        displayName: 'Used rejection', subject: 'Subject', body: 'Body', enabled: true, isCustom: true }],
      autoClaimRules: [],
      branding: { hasLogo: false, altText: null }
    },
    effective: {
      allowedPatronCodeIds: [],
      publicationOptions: [],
      commonCreators: [],
      externalSearchProviders: [],
      formats: [],
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: 'System alt'
    },
    workflow: { ...systemTimeoutValues, ...libraryTimeoutOverrides },
    ui_text: {},
    emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] },
    autoClaimStaff: []
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-staff-scope-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    require('./helpers/confirmation.cjs').answerConfirmations(window, () => true);

    const settingsRequests = [];
    const organizations = [
      { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'system-v1' },
      { id: 2, name: 'Library Two', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'library-v1' }
    ];
    let saveBody;
    const saveBodies = [];
    let librarySetsExist = false;
    let saveCompleted;
    const saveFinished = new Promise(resolve => { saveCompleted = resolve; });
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
        settingsRequests.push(requestUrl);
        return response(200, settingsData(librarySetsExist));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        return response(200, { code: 'ok', data: organizations });
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        saveBody = JSON.parse(options.body);
        saveBodies.push(saveBody);
        librarySetsExist = saveBodies.length === 1;
        for (const [key, value] of Object.entries(saveBody.workflow || {})) {
          if (!timeoutKeys.has(key)) continue;
          if (value === null) delete libraryTimeoutOverrides[key];
          else libraryTimeoutOverrides[key] = value;
        }
        if (saveBodies.length === 1) saveCompleted();
        return response(200, { code: 'saved', data: { orgId: '2', version: 'library-2-saved' } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'admin', organizationId: 2 })
    });
    controller.bind();
    controller.setStaff({
      id: '2',
      tenantId: 'tenant-1',
      objectId: 'object-2',
      role: 'admin',
      organizationId: 2
    });
    await controller.activate();

    assert.deepStrictEqual(settingsRequests, ['/api/asap/staff/settings?orgId=2']);
    const timeout = id => document.getElementById(id);
    const timeoutOverride = key => document.querySelector(
      `[data-setting-section="workflow"][data-setting-key="${key}"] .settings-override-toggle`);
    const change = control => control.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const setChecked = (control, checked) => { control.checked = checked; change(control); };
    const outstandingEnabled = timeout('outstanding-timeout-enabled');
    const outstandingDays = timeout('outstanding-timeout-days');
    const sendRejectionEmail = timeout('outstanding-timeout-send-email');
    const rejectionTemplate = timeout('outstanding-timeout-rejection-template-id');
    assert.equal(outstandingEnabled.checked, false);
    assert.equal(outstandingDays.value, '45');
    assert.equal(outstandingDays.disabled, true);
    assert.equal(sendRejectionEmail.checked, true);
    assert.equal(sendRejectionEmail.disabled, true);
    assert.equal(rejectionTemplate.value, '9007199254740993');
    assert.equal(rejectionTemplate.disabled, true);

    setChecked(timeoutOverride('outstandingTimeoutEnabled'), false);
    assert.equal(outstandingEnabled.checked, true, 'Reset displays the configured System value, not the raw Library row.');
    assert.equal(outstandingEnabled.disabled, true);
    assert.equal(outstandingDays.disabled, false, 'An inherited true parent enables its explicit Library dependent.');
    assert.equal(sendRejectionEmail.disabled, false);
    assert.equal(rejectionTemplate.disabled, false);
    setChecked(sendRejectionEmail, false);
    assert.equal(rejectionTemplate.disabled, true, 'The email checkbox independently gates its template.');
    assert.equal(rejectionTemplate.value, '9007199254740993', 'Email gating retains the selected template.');
    setChecked(sendRejectionEmail, true);
    assert.equal(rejectionTemplate.disabled, false);
    setChecked(timeoutOverride('outstandingTimeoutEnabled'), true);
    assert.equal(outstandingEnabled.checked, false, 'Re-enabling Override restores the local checkbox draft.');
    assert.equal(outstandingDays.value, '45');
    assert.equal(outstandingDays.disabled, true);
    assert.equal(rejectionTemplate.value, '9007199254740993');
    setChecked(timeoutOverride('outstandingTimeoutEnabled'), false);
    assert.equal(outstandingEnabled.checked, true);
    assert.equal(outstandingDays.disabled, false);

    const holdEnabled = timeout('hold-pickup-timeout-enabled');
    const holdDays = timeout('hold-pickup-timeout-days');
    assert.equal(holdEnabled.checked, true);
    assert.equal(holdEnabled.disabled, true, 'The inherited parent remains read-only.');
    assert.equal(holdDays.value, '9');
    assert.equal(holdDays.disabled, false);
    setChecked(timeoutOverride('holdPickupTimeoutDays'), false);
    assert.equal(holdDays.value, '7');
    assert.equal(holdDays.disabled, true, 'An inherited true parent does not unlock an inherited dependent.');
    setChecked(timeoutOverride('holdPickupTimeoutDays'), true);
    assert.equal(holdDays.value, '9', 'Re-enabling the dependent override restores its draft.');
    assert.equal(holdDays.disabled, false);
    setChecked(timeoutOverride('holdPickupTimeoutEnabled'), true);
    setChecked(holdEnabled, false);
    assert.equal(holdDays.disabled, true);
    assert.equal(holdDays.value, '9');
    setChecked(holdEnabled, true);
    assert.equal(holdDays.disabled, false);
    setChecked(timeoutOverride('holdPickupTimeoutEnabled'), false);

    const pendingEnabled = timeout('pending-hold-timeout-enabled');
    const pendingDays = timeout('pending-hold-timeout-days');
    assert.equal(pendingEnabled.checked, true);
    assert.equal(pendingDays.disabled, false);
    setChecked(timeoutOverride('pendingHoldTimeoutEnabled'), false);
    assert.equal(pendingEnabled.checked, false,
      'A null configured System value resets a true Library override to the accepted false presentation.');
    assert.equal(pendingDays.value, '18');
    assert.equal(pendingDays.disabled, true);

    const additionalEnabled = timeout('additional-copy-timeout-enabled');
    const additionalDays = timeout('additional-copy-timeout-days');
    assert.equal(additionalEnabled.checked, false);
    assert.equal(additionalEnabled.disabled, true);
    assert.equal(additionalDays.value, '33');
    assert.equal(additionalDays.disabled, true);
    setChecked(timeoutOverride('additionalCopyTimeoutEnabled'), true);
    setChecked(additionalEnabled, true);
    assert.equal(additionalDays.disabled, false);
    setChecked(additionalEnabled, false);
    assert.equal(additionalDays.disabled, true);
    assert.equal(additionalDays.value, '33');
    setChecked(timeoutOverride('additionalCopyTimeoutEnabled'), false);

    assert.strictEqual(document.getElementById('settings-scope').value, '2');
    assert.strictEqual(document.getElementById('settings-scope-field').hidden, true);
    const currentAddressButton = document.getElementById('use-current-staff-url');
    assert.strictEqual(currentAddressButton.hidden, false, 'A valid local address candidate remains reviewable in Library context.');
    assert.strictEqual(currentAddressButton.matches(':disabled'), true, 'The system-only fieldset disables the suggestion action in Library context.');
    const staffUrl = document.getElementById('system-staff-url');
    const originalStaffUrl = staffUrl.value;
    currentAddressButton.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true }));
    assert.strictEqual(staffUrl.value, originalStaffUrl, 'A library administrator cannot apply the system URL suggestion.');
    assert.match(document.getElementById('settings-library-context-title').textContent, /Editing Library Two settings/);
    assert.match(document.getElementById('settings-library-context-guidance').textContent, /apply to every library/);
    assert.strictEqual(document.getElementById('settings-switch-system').hidden, true);
    assert.strictEqual(document.querySelector('#settings-nav-polaris .settings-scope-badge').hidden, false);
    assert.strictEqual(document.querySelector('#settings-nav-smtp .settings-scope-badge'), null);
    assert.match(document.querySelector('#settings-smtp .settings-scope-explanation').textContent, /Library Two.*managed centrally/);
    assert.strictEqual(document.getElementById('email-postmark-token').disabled, true);
    assert.strictEqual(document.getElementById('email-clear-postmark-token').disabled, true);
    assert.strictEqual(document.getElementById('email-postmark-token').value, '');
    assert.strictEqual(document.getElementById('email-postmark-token').placeholder, '',
      'A library override flag cannot create the system Postmark saved mask.');
    assert.strictEqual(document.getElementById('email-token-status').textContent, 'No system Postmark token is stored.');
    assert.strictEqual(document.getElementById('polaris-api-key').placeholder, '');
    assert.strictEqual(document.getElementById('polaris-admin-pass').placeholder, '');
    assert.strictEqual(document.querySelector('[data-setting-key="postmarkToken"] .settings-override-toggle'), null);
    const usedTemplate = document.querySelector('#email-templates-editor [data-template-id="9007199254740993"]');
    usedTemplate.querySelector('[aria-label="Delete"]').click();
    assert.strictEqual(usedTemplate.isConnected, true);
    assert.match(document.getElementById('settings-message').textContent, /used by auto-rejection/);
    document.getElementById('email-postmark-token').value = 'forged-browser-token';
    document.getElementById('email-clear-postmark-token').checked = true;

    const fromToggle = document.querySelector('[data-setting-key="fromAddress"] .settings-override-toggle');
    fromToggle.checked = true;
    fromToggle.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    document.getElementById('email-from-address').value = 'library@example.org';

    for (const id of ['publication-options-use-system', 'common-creators-use-system']) {
      const toggle = document.getElementById(id);
      toggle.checked = false;
      toggle.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    }

    const note = document.getElementById('patron-login-note');
    note.value = 'Own library edit';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    await saveFinished;
    await flush();

    assert.strictEqual(saveBody.orgId, '2');
    assert.strictEqual(saveBody.patron.loginNote, 'Own library edit');
    assert.strictEqual(saveBody.email.fromAddress, 'library@example.org');
    assert.strictEqual(saveBody.workflow.outstandingTimeoutEnabled, null,
      'Resetting an enabled override submits an explicit inherited reset.');
    assert.strictEqual(saveBody.workflow.outstandingTimeoutDays, 45,
      'Saving with the parent off retains its dependent day override.');
    assert.strictEqual(saveBody.workflow.outstandingTimeoutSendEmail, true);
    assert.strictEqual(saveBody.workflow.outstandingTimeoutRejectionTemplateId, '9007199254740993',
      'Saving with the email parent off retains the selected template override.');
    assert.strictEqual(saveBody.workflow.pendingHoldTimeoutEnabled, null,
      'A Library true override can reset to the null System value.');
    assert.strictEqual(saveBody.workflow.pendingHoldTimeoutDays, 18,
      'Resetting a parent does not transfer or delete its dependent override.');
    assert.strictEqual(saveBody.workflow.holdPickupTimeoutDays, 9);
    assert.strictEqual(saveBody.workflow.additionalCopyTimeoutDays, 33);
    assert.deepStrictEqual(saveBody.patron.publicationOptions, []);
    assert.deepStrictEqual(saveBody.workflow.commonAuthorsList, []);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody.email, 'postmarkToken'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody.email, 'clearPostmarkToken'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'systemSettings'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'polaris'), false);
    assert.ok(settingsRequests.every(url => url === '/api/asap/staff/settings?orgId=2'));

    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(timeoutOverride('outstandingTimeoutEnabled').checked, false);
    assert.strictEqual(outstandingEnabled.checked, true);
    assert.strictEqual(outstandingEnabled.disabled, true);
    assert.strictEqual(outstandingDays.value, '45');
    assert.strictEqual(outstandingDays.disabled, false);
    assert.strictEqual(sendRejectionEmail.checked, true);
    assert.strictEqual(sendRejectionEmail.disabled, false);
    assert.strictEqual(rejectionTemplate.value, '9007199254740993');
    assert.strictEqual(rejectionTemplate.disabled, false);
    assert.strictEqual(timeoutOverride('pendingHoldTimeoutEnabled').checked, false);
    assert.strictEqual(pendingEnabled.checked, false);
    assert.strictEqual(pendingEnabled.disabled, true);
    assert.strictEqual(pendingDays.value, '18');
    assert.strictEqual(pendingDays.disabled, true);
    for (const id of ['publication-options-use-system', 'common-creators-use-system']) {
      const toggle = document.getElementById(id);
      assert.strictEqual(toggle.checked, false);
      toggle.checked = true;
      toggle.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    }
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveBodies.length < 2; attempt++) await flush();
    assert.strictEqual(saveBodies.length, 2);
    assert.strictEqual(saveBodies[1].patron.publicationOptions, null);
    assert.strictEqual(saveBodies[1].workflow.commonAuthorsList, null);

    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    setChecked(timeoutOverride('outstandingTimeoutEnabled'), true);
    setChecked(outstandingEnabled, false);
    setChecked(timeoutOverride('outstandingTimeoutEnabled'), false);
    assert.strictEqual(outstandingEnabled.checked, true);
    controller.setStaff({
      id: '3', tenantId: 'tenant-1', objectId: 'object-3', role: 'admin', organizationId: 2
    });
    await controller.activate();
    assert.strictEqual(timeoutOverride('outstandingTimeoutEnabled').checked, false);
    assert.strictEqual(outstandingEnabled.checked, true,
      'A replacement staff identity reloads the accepted System value rather than restoring a retired local override draft.');
    setChecked(timeoutOverride('outstandingTimeoutEnabled'), true);
    assert.strictEqual(outstandingEnabled.checked, true,
      'Accepted context population clears the previous identity’s timeout draft cache.');

    dom.window.close();
    console.log('Non-super-admin settings scope survives initial option population and save');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
