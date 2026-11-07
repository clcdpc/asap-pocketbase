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
const systemSuggestionLimitMessage = 'System suggestion message';
let librarySuggestionLimitMessage = 'Library suggestion message';
let systemCommonAuthorsEnabled = true;
let libraryCommonAuthorsEnabled = false;
let libraryCommonAuthorsEnabledOverridden = true;
const systemCommonCreators = ['System creator'];
let libraryCommonCreators = ['Library creator'];
let libraryCommonCreatorsOverridden = true;
const systemCommonAuthorText = {
  commonAuthorsLabel: 'System creator label',
  commonAuthorsHelp: 'System creator help',
  commonAuthorsMessage: 'System creator message'
};
let libraryCommonAuthorText = {
  commonAuthorsLabel: 'Library creator label',
  commonAuthorsHelp: 'Library creator help',
  commonAuthorsMessage: 'Library creator message'
};

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
        workflow: { ...systemTimeoutValues, suggestionLimitMessage: systemSuggestionLimitMessage,
          commonAuthorsEnabled: systemCommonAuthorsEnabled, ...systemCommonAuthorText },
        patron: {},
        email: { fromAddress: 'system@example.org', fromName: 'System' },
        publicationOptions: emptySet,
        commonCreators: { exists: true, values: systemCommonCreators },
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: 'System alt' }
      },
      libraryOverride: {
        workflow: {
          ...libraryTimeoutOverrides,
          suggestionLimitMessage: librarySuggestionLimitMessage,
          ...(libraryCommonAuthorsEnabledOverridden ? { commonAuthorsEnabled: libraryCommonAuthorsEnabled } : {}),
          ...libraryCommonAuthorText
        },
        patron: { loginNote: 'Existing library note' },
        email: { hasPostmarkToken: true },
        publicationOptions: librarySet,
        commonCreators: { exists: libraryCommonCreatorsOverridden, values: libraryCommonCreators },
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: null }
      },
      workflow: {
        ...libraryTimeoutOverrides,
        suggestionLimitMessage: librarySuggestionLimitMessage,
        commonAuthorsEnabled: libraryCommonAuthorsEnabledOverridden ? libraryCommonAuthorsEnabled : systemCommonAuthorsEnabled,
        ...libraryCommonAuthorText
      },
      patron: {},
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: libraryCommonCreatorsOverridden ? libraryCommonCreators : systemCommonCreators,
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
      commonCreators: libraryCommonCreatorsOverridden ? libraryCommonCreators : systemCommonCreators,
      externalSearchProviders: [],
      formats: [],
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: 'System alt'
    },
    workflow: {
      ...systemTimeoutValues,
      ...libraryTimeoutOverrides,
      suggestionLimitMessage: librarySuggestionLimitMessage ?? systemSuggestionLimitMessage,
      commonAuthorsEnabled: libraryCommonAuthorsEnabledOverridden ? libraryCommonAuthorsEnabled : systemCommonAuthorsEnabled,
      ...libraryCommonAuthorText
    },
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
    document.getElementById('workspace').hidden = false;
    document.getElementById('settings-view').hidden = false;
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
        if (Object.hasOwn(saveBody.workflow || {}, 'suggestionLimitMessage')) {
          librarySuggestionLimitMessage = saveBody.workflow.suggestionLimitMessage;
        }
        if (Object.hasOwn(saveBody.workflow || {}, 'commonAuthorsEnabled')) {
          const value = saveBody.workflow.commonAuthorsEnabled;
          libraryCommonAuthorsEnabledOverridden = value !== null;
          if (libraryCommonAuthorsEnabledOverridden) libraryCommonAuthorsEnabled = value;
        }
        for (const key of Object.keys(systemCommonAuthorText)) {
          if (Object.hasOwn(saveBody.workflow || {}, key)) libraryCommonAuthorText[key] = saveBody.workflow[key];
        }
        if (Object.hasOwn(saveBody.workflow || {}, 'commonAuthorsList')) {
          const values = saveBody.workflow.commonAuthorsList;
          libraryCommonCreatorsOverridden = values !== null;
          if (libraryCommonCreatorsOverridden) libraryCommonCreators = values;
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
    const workflowField = key => document.querySelector(`[data-setting-section="workflow"][data-setting-key="${key}"]`);
    const overridden = key => workflowField(key).querySelector('.settings-inheritance-status')?.textContent.startsWith('Library setting');
    const reset = key => workflowField(key).querySelector('.settings-use-system')?.click();
    const change = control => control.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const setChecked = (control, checked) => { control.checked = checked; change(control); };
    controller.activatePanel('workflow');
    const suggestionMessage = timeout('suggestion-limit-message');
    const suggestionTrigger = timeout('suggestion-limit-placeholder-trigger');
    const suggestionPicker = timeout('suggestion-limit-placeholder-picker');
    const suggestionOption = suggestionPicker.querySelector('[data-suggestion-placeholder="next_available_date"]');
    assert.equal(suggestionMessage.disabled, false);
    let suggestionInputEvents = 0;
    suggestionMessage.addEventListener('input', () => suggestionInputEvents++);
    assert.equal(suggestionMessage.value, librarySuggestionLimitMessage);
    assert.equal(suggestionTrigger.disabled, false, 'An explicitly overridden Library message has an available picker.');
    assert.equal(controller.isDirty(), false);
    suggestionTrigger.click();
    assert.equal(suggestionPicker.hidden, false);
    assert.equal(suggestionPicker.querySelectorAll('[data-suggestion-placeholder]').length, 1);
    assert.equal(document.activeElement, suggestionOption);
    assert.equal(controller.isDirty(), false, 'Opening the picker does not dirty presentation controls.');
    assert.equal(saveBodies.length, 0, 'Opening the picker never submits settings.');
    suggestionOption.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    assert.equal(suggestionPicker.hidden, true);
    assert.equal(document.activeElement, suggestionTrigger, 'Escape returns focus to Insert placeholder.');
    assert.equal(controller.isDirty(), false);

    suggestionTrigger.click();
    assert.equal(suggestionPicker.hidden, false, 'The refresh check starts with an open picker.');
    await controller.load({ silent: true });
    assert.equal(suggestionPicker.hidden, true, 'An accepted refresh retires an open picker context.');
    assert.equal(suggestionTrigger.disabled, false, 'The current Library editor is available after refresh.');
    assert.equal(controller.isDirty(), false);

    suggestionMessage.value = 'Transient selection draft';
    suggestionMessage.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    suggestionMessage.focus();
    suggestionMessage.setSelectionRange(11, 20);
    suggestionTrigger.click();
    assert.equal(document.activeElement, suggestionOption);
    const staleOverrideDraft = systemSuggestionLimitMessage;
    reset('suggestionLimitMessage');
    assert.equal(suggestionTrigger.disabled, false, 'Inherited content remains directly editable.');
    suggestionOption.click();
    assert.equal(suggestionMessage.value, staleOverrideDraft,
      'A retained picker option cannot insert after the Library Override value changes.');
    assert.equal(suggestionInputEvents, 1, 'Rejected stale options do not dispatch editor input.');
    await controller.load({ silent: true });
    suggestionMessage.value = librarySuggestionLimitMessage;
    suggestionMessage.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(controller.isDirty(), false, 'Restoring the accepted message and Override state leaves a clean draft.');

    suggestionMessage.focus();
    suggestionMessage.setSelectionRange(0, 0);
    suggestionTrigger.click();
    const editor = suggestionMessage.closest('.suggestion-message-editor');
    const tools = editor.querySelector('.suggestion-message-placeholder-tools');
    suggestionMessage.remove();
    suggestionOption.focus();
    suggestionOption.click();
    editor.insertBefore(suggestionMessage, tools);
    assert.equal(suggestionMessage.value, librarySuggestionLimitMessage,
      'A detached editor cannot receive a retained placeholder insertion.');
    assert.equal(suggestionInputEvents, 2, 'Detached targets receive no input event.');
    assert.equal(suggestionPicker.hidden, true);

    const messageDraft = 'left selected right';
    suggestionMessage.value = messageDraft;
    suggestionMessage.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    suggestionMessage.focus();
    suggestionMessage.setSelectionRange(5, 13);
    suggestionTrigger.click();
    suggestionOption.click();
    const insertedMessage = 'left {{next_available_date}} right';
    assert.equal(suggestionMessage.value, insertedMessage, 'The token replaces only the remembered selection.');
    assert.equal(document.activeElement, suggestionMessage, 'Insertion returns focus to the message editor.');
    assert.equal(suggestionMessage.selectionStart, 5 + '{{next_available_date}}'.length);
    assert.equal(suggestionInputEvents, 4, 'Insertion uses the normal input path exactly once.');
    assert.equal(controller.isDirty(), true);
    assert.equal(saveBodies.length, 0, 'Editing with the picker does not autosave.');
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

    reset('outstandingTimeoutEnabled');
    assert.equal(outstandingEnabled.checked, true, 'Reset displays the configured System value, not the raw Library row.');
    assert.equal(outstandingEnabled.disabled, false);
    assert.equal(outstandingDays.disabled, false, 'An inherited true parent enables its explicit Library dependent.');
    assert.equal(sendRejectionEmail.disabled, false);
    assert.equal(rejectionTemplate.disabled, false);
    setChecked(sendRejectionEmail, false);
    assert.equal(rejectionTemplate.disabled, true, 'The email checkbox independently gates its template.');
    assert.equal(rejectionTemplate.value, '9007199254740993', 'Email gating retains the selected template.');
    setChecked(sendRejectionEmail, true);
    assert.equal(rejectionTemplate.disabled, false);
    setChecked(outstandingEnabled, false);
    assert.equal(outstandingEnabled.checked, false, 'Direct editing prepares a false Library draft.');
    assert.equal(outstandingDays.value, '45');
    assert.equal(outstandingDays.disabled, true);
    assert.equal(rejectionTemplate.value, '9007199254740993');
    reset('outstandingTimeoutEnabled');
    assert.equal(outstandingEnabled.checked, true);
    assert.equal(outstandingDays.disabled, false);

    const holdEnabled = timeout('hold-pickup-timeout-enabled');
    const holdDays = timeout('hold-pickup-timeout-days');
    assert.equal(holdEnabled.checked, true);
    assert.equal(holdEnabled.disabled, false, 'The inherited parent is directly editable.');
    assert.equal(holdDays.value, '9');
    assert.equal(holdDays.disabled, false);
    reset('holdPickupTimeoutDays');
    assert.equal(holdDays.value, '7');
    assert.equal(holdDays.disabled, false, 'An inherited true parent enables its directly editable dependent.');
    holdDays.value = '9'; change(holdDays);
    assert.equal(holdDays.value, '9', 'Re-enabling the dependent override restores its draft.');
    assert.equal(holdDays.disabled, false);
    setChecked(holdEnabled, false);
    assert.equal(holdDays.disabled, true);
    assert.equal(holdDays.value, '9');
    setChecked(holdEnabled, true);
    assert.equal(holdDays.disabled, false);
    reset('holdPickupTimeoutEnabled');

    const pendingEnabled = timeout('pending-hold-timeout-enabled');
    const pendingDays = timeout('pending-hold-timeout-days');
    assert.equal(pendingEnabled.checked, true);
    assert.equal(pendingDays.disabled, false);
    reset('pendingHoldTimeoutEnabled');
    assert.equal(pendingEnabled.checked, false,
      'A null configured System value resets a true Library override to the accepted false presentation.');
    assert.equal(pendingDays.value, '18');
    assert.equal(pendingDays.disabled, true);

    const additionalEnabled = timeout('additional-copy-timeout-enabled');
    const additionalDays = timeout('additional-copy-timeout-days');
    assert.equal(additionalEnabled.checked, false);
    assert.equal(additionalEnabled.disabled, false);
    assert.equal(additionalDays.value, '33');
    assert.equal(additionalDays.disabled, true);
    setChecked(additionalEnabled, true);
    assert.equal(additionalDays.disabled, false);
    setChecked(additionalEnabled, false);
    assert.equal(additionalDays.disabled, true);
    assert.equal(additionalDays.value, '33');
    reset('additionalCopyTimeoutEnabled');

    const commonAuthorsEnabled = timeout('common-authors-enabled');
    const commonAuthorsOptions = timeout('common-authors-options');
    const commonAuthorsOverride = commonAuthorsEnabled;
    const creatorsInherited = () => timeout('common-creators-inherit-field').textContent.includes('Using system value');
    const commonCreatorsEditor = timeout('common-creators-editor');
    const commonCreatorInput = () => commonCreatorsEditor.querySelector('[data-domain-row] input');
    assert.equal(commonAuthorsEnabled.checked, false);
    assert.equal(overridden('commonAuthorsEnabled'), true);
    assert.equal(commonAuthorsOptions.hidden, true, 'An initially disabled feature keeps its options collapsed.');
    assert.equal(commonAuthorsOptions.disabled, true);
    reset('commonAuthorsEnabled');
    assert.equal(commonAuthorsEnabled.checked, true, 'Reset displays the configured System feature flag.');
    assert.equal(commonAuthorsEnabled.disabled, false);
    assert.equal(commonAuthorsOptions.hidden, false, 'An inherited true flag reveals its read-only options.');
    assert.equal(creatorsInherited(), false);
    assert.equal(commonCreatorInput().value, 'Library creator');
    commonCreatorInput().focus();
    setChecked(commonAuthorsEnabled, false);
    assert.equal(commonAuthorsEnabled.checked, false, 'A direct false edit hides the options.');
    assert.equal(commonAuthorsOptions.hidden, true);
    assert.strictEqual(document.activeElement, commonAuthorsEnabled,
      'Hiding focused options with an editable parent returns focus to its checkbox.');

    setChecked(commonAuthorsEnabled, true);
    assert.equal(commonAuthorsOptions.hidden, false);
    const setText = (control, value) => {
      control.value = value;
      control.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    };
    setText(timeout('common-authors-label'), 'Draft creator label');
    setText(timeout('common-authors-help'), 'Draft creator help');
    setText(timeout('common-authors-message'), 'Draft creator message');
    setText(commonCreatorInput(), 'Draft library creator');
    const retainedCreatorRow = commonCreatorInput().closest('[data-domain-row]');
    const retainedCreatorDelete = retainedCreatorRow.querySelector('.settings-row-actions button[aria-label="Delete"]');
    commonCreatorInput().focus();
    setChecked(commonAuthorsEnabled, false);
    assert.equal(commonAuthorsOptions.hidden, true);
    assert.equal(commonAuthorsOptions.disabled, true);
    assert.strictEqual(document.activeElement, commonAuthorsEnabled,
      'Hiding focused editable options returns focus to the controlling checkbox.');
    timeout('add-common-creator').dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true }));
    retainedCreatorDelete.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true }));
    timeout('common-creators-inherit-field').querySelector('.settings-use-system').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
    assert.equal(creatorsInherited(), false,
      'A hidden Use system list action cannot change creator-list ownership.');
    assert.strictEqual(retainedCreatorRow.isConnected, true, 'A retained hidden Delete handler cannot remove its row.');
    assert.equal(commonCreatorsEditor.querySelectorAll('[data-domain-row]').length, 1,
      'A hidden Add handler cannot create an invisible row.');
    assert.equal(commonCreatorInput().value, 'Draft library creator');

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

    document.getElementById('email-from-address').value = 'library@example.org';
    document.getElementById('email-from-address').dispatchEvent(new window.Event('input', { bubbles: true }));
    document.getElementById('add-publication-option').click();
    document.querySelector('#publication-options-editor [data-row-action="delete"]').click();

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
    assert.strictEqual(saveBody.workflow.outstandingTimeoutDays, undefined,
      'Saving with the parent off retains its dependent day override.');
    assert.strictEqual(saveBody.workflow.outstandingTimeoutSendEmail, true);
    assert.strictEqual(saveBody.workflow.outstandingTimeoutRejectionTemplateId, undefined,
      'Saving with the email parent off retains the selected template override.');
    assert.strictEqual(saveBody.workflow.pendingHoldTimeoutEnabled, null,
      'A Library true override can reset to the null System value.');
    assert.strictEqual(saveBody.workflow.pendingHoldTimeoutDays, undefined,
      'Resetting a parent does not transfer or delete its dependent override.');
    assert.strictEqual(saveBody.workflow.holdPickupTimeoutDays, 9);
    assert.strictEqual(saveBody.workflow.additionalCopyTimeoutDays, undefined);
    assert.strictEqual(saveBody.workflow.commonAuthorsEnabled, false,
      'The feature remains explicitly off when its hidden configuration is saved.');
    assert.strictEqual(saveBody.workflow.commonAuthorsLabel, 'Draft creator label');
    assert.strictEqual(saveBody.workflow.commonAuthorsHelp, 'Draft creator help');
    assert.strictEqual(saveBody.workflow.commonAuthorsMessage, 'Draft creator message');
    assert.strictEqual(saveBody.workflow.suggestionLimitMessage, insertedMessage,
      'The inserted token remains part of the existing scoped Workflow payload.');
    assert.deepStrictEqual(saveBody.patron.publicationOptions, []);
    assert.deepStrictEqual(saveBody.workflow.commonAuthorsList, ['Draft library creator'],
      'Saving while collapsed preserves the edited Library creator list and its ownership.');
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody.email, 'postmarkToken'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody.email, 'clearPostmarkToken'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'systemSettings'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'polaris'), false);
    assert.ok(settingsRequests.every(url => url === '/api/asap/staff/settings?orgId=2'));

    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(overridden('outstandingTimeoutEnabled'), false);
    assert.strictEqual(outstandingEnabled.checked, true);
    assert.strictEqual(outstandingEnabled.disabled, false);
    assert.strictEqual(outstandingDays.value, '45');
    assert.strictEqual(outstandingDays.disabled, false);
    assert.strictEqual(sendRejectionEmail.checked, true);
    assert.strictEqual(sendRejectionEmail.disabled, false);
    assert.strictEqual(rejectionTemplate.value, '9007199254740993');
    assert.strictEqual(rejectionTemplate.disabled, false);
    assert.strictEqual(commonAuthorsEnabled.checked, false);
    assert.strictEqual(commonAuthorsOptions.hidden, true);
    assert.strictEqual(commonAuthorsOptions.disabled, true);
    assert.strictEqual(timeout('common-authors-label').value, 'Draft creator label');
    assert.strictEqual(timeout('common-authors-help').value, 'Draft creator help');
    assert.strictEqual(timeout('common-authors-message').value, 'Draft creator message');
    assert.strictEqual(suggestionMessage.value, insertedMessage,
      'The accepted Library setting reload retains the inserted workflow message.');
    assert.strictEqual(creatorsInherited(), false);
    assert.deepStrictEqual([...commonCreatorsEditor.querySelectorAll('[data-domain-row] input')]
      .map(input => input.value), ['Draft library creator'],
    'Accepted reload retains the hidden creator row and keeps the explicit local list selected.');
    assert.strictEqual(overridden('pendingHoldTimeoutEnabled'), false);
    assert.strictEqual(pendingEnabled.checked, false);
    assert.strictEqual(pendingEnabled.disabled, false);
    assert.strictEqual(pendingDays.value, '18');
    assert.strictEqual(pendingDays.disabled, true);
    setChecked(commonAuthorsEnabled, true);
    for (const id of ['publication-options-inherit-field', 'common-creators-inherit-field']) {
      document.querySelector('#' + id + ' .settings-use-system').click();
      await flush();
    }
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveBodies.length < 2; attempt++) await flush();
    assert.strictEqual(saveBodies.length, 2);
    assert.strictEqual(saveBodies[1].patron.publicationOptions, null);
    assert.strictEqual(saveBodies[1].workflow.commonAuthorsList, null);
    assert.strictEqual(saveBodies[1].workflow.commonAuthorsEnabled, true);

    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(commonAuthorsEnabled.checked, true);
    assert.strictEqual(commonAuthorsOptions.hidden, false);
    assert.strictEqual(creatorsInherited(), true);
    assert.deepStrictEqual([...commonCreatorsEditor.querySelectorAll('[data-domain-row] input')]
      .map(input => input.value), systemCommonCreators,
    'The accepted Use system list selection displays the System creators.');
    const inheritedCreator = commonCreatorInput();
    const inheritedCreatorValue = inheritedCreator.value;
    setText(inheritedCreator, inheritedCreatorValue + ' draft'); setText(inheritedCreator, inheritedCreatorValue);
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveBodies.length < 3; attempt++) await flush();
    assert.strictEqual(saveBodies.length, 3);
    assert.deepStrictEqual(saveBodies[2].workflow.commonAuthorsList, systemCommonCreators,
      'A Library list can be made explicitly editable before testing focused row refresh.');
    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(creatorsInherited(), false);

    systemCommonAuthorsEnabled = null;
    libraryCommonAuthorsEnabledOverridden = false;
    commonCreatorInput().focus();
    document.getElementById('settings-form').inert = true;
    await controller.load({ silent: true });
    assert.strictEqual(commonAuthorsEnabled.checked, false, 'A null System feature value presents as unchecked.');
    assert.strictEqual(commonAuthorsEnabled.disabled, false);
    assert.strictEqual(commonAuthorsOptions.hidden, true);
    assert.strictEqual(commonAuthorsOptions.disabled, true);
    assert.strictEqual(document.activeElement, commonAuthorsOverride,
      `An accepted inert refresh that hides the focused creator row restores focus to the available Override. ` +
      `Active=${document.activeElement.id || document.activeElement.outerHTML}; checkboxDisabled=${commonAuthorsEnabled.disabled}; ` +
      `overrideDisabled=${commonAuthorsOverride.disabled}; formInert=${document.getElementById('settings-form').inert}`);

    systemCommonAuthorsEnabled = false;
    await controller.load({ silent: true });
    assert.strictEqual(commonAuthorsEnabled.checked, false, 'Inherited false remains collapsed after accepted refresh.');
    assert.strictEqual(commonAuthorsOptions.hidden, true);

    assert.strictEqual(commonAuthorsEnabled.checked, false, 'A fresh local override begins at the accepted inherited false value.');
    setChecked(commonAuthorsEnabled, true);
    reset('commonAuthorsEnabled');
    assert.strictEqual(commonAuthorsEnabled.checked, false,
      'Resetting a local true draft shows the latest authoritative System false value.');
    assert.strictEqual(commonAuthorsOptions.hidden, true);

    setChecked(outstandingEnabled, false);
    reset('outstandingTimeoutEnabled');
    assert.strictEqual(outstandingEnabled.checked, true);
    controller.setStaff({
      id: '3', tenantId: 'tenant-1', objectId: 'object-3', role: 'admin', organizationId: 2
    });
    await controller.activate();
    assert.strictEqual(overridden('outstandingTimeoutEnabled'), false);
    assert.strictEqual(outstandingEnabled.checked, true,
      'A replacement staff identity reloads the accepted System value rather than restoring a retired local override draft.');
    assert.strictEqual(overridden('commonAuthorsEnabled'), false);
    assert.strictEqual(commonAuthorsEnabled.checked, false);
    assert.strictEqual(commonAuthorsOptions.hidden, true);
    assert.strictEqual(commonAuthorsEnabled.checked, false,
      'A replacement staff identity cannot restore a retired local common-creators draft.');
    assert.strictEqual(commonAuthorsOptions.hidden, true);
    assert.strictEqual(outstandingEnabled.checked, true,
      'Accepted context population clears the previous identity’s timeout draft cache.');

    controller.activatePanel('workflow');
    const retiredActorDraft = 'Retired actor suggestion message';
    suggestionMessage.value = retiredActorDraft;
    suggestionMessage.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    suggestionMessage.focus();
    suggestionMessage.setSelectionRange(8, 13);
    suggestionTrigger.click();
    assert.equal(document.activeElement, suggestionOption);
    controller.setStaff({
      id: '4', tenantId: 'tenant-1', objectId: 'object-4', role: 'admin', organizationId: 2
    });
    await controller.activate();
    assert.equal(suggestionMessage.value, insertedMessage,
      'The replacement actor sees the last accepted Library message, not the retired draft.');
    const replacementActorValue = suggestionMessage.value;
    const eventsBeforeRetiredClick = suggestionInputEvents;
    assert.notEqual(replacementActorValue, retiredActorDraft, 'The stale picker test uses a different accepted value.');
    suggestionOption.click();
    assert.equal(suggestionMessage.value, replacementActorValue);
    assert.equal(suggestionInputEvents, eventsBeforeRetiredClick,
      'A retained placeholder option cannot mutate a retired account context.');
    assert.equal(controller.isDirty(), false);

    const acceptedReference = rejectionTemplate.value;
    systemTimeoutValues.outstandingTimeoutRejectionTemplateId = '9007199254740997';
    await controller.load({ silent: true });
    controller.activatePanel('workflow');
    reset('outstandingTimeoutRejectionTemplateId');
    assert.equal(rejectionTemplate.value, '9007199254740997',
      'A pending reset preserves the inherited reference even when its template is unavailable.');
    assert.match(rejectionTemplate.selectedOptions[0].textContent, /Unavailable rejection template/);
    assert.equal(controller.isDirty(), true);
    systemTimeoutValues.outstandingTimeoutRejectionTemplateId = null;
    await controller.load({ silent: true });
    assert.equal(rejectionTemplate.value, acceptedReference, 'Discard/reload restores the accepted Library reference.');
    assert.equal(controller.isDirty(), false);
    assert.doesNotMatch(document.getElementById('settings-message').textContent, /using the System value is pending/,
      'Accepted Discard/reload retires the pending-reset announcement.');

    dom.window.close();
    console.log('Non-super-admin settings scope survives initial option population and save');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
