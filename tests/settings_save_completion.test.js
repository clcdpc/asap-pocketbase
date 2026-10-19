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

function settingsData(note, version, formats = []) {
  const emptySet = { exists: false, values: [] };
  const systemNote = 'System login note';
  const libraryOverride = {
    workflow: null,
    patron: note === systemNote ? null : { loginNote: note },
    email: null,
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats,
    templates: [],
    branding: { hasLogo: false, altText: null }
  };
  const configuredSystem = {
    workflow: {},
    patron: { loginNote: systemNote },
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats,
    templates: [],
    branding: { hasLogo: false, altText: 'System alt' }
  };
  return {
    orgId: '2',
    version,
    organization: { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true, version },
    stored: {
      systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] },
      polaris: {},
      configuredSystem,
      libraryOverride,
      workflow: {},
      patron: libraryOverride?.patron || configuredSystem.patron,
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: [],
      allowedPatronCodeIds: [],
      providers: [],
      formats,
      customFields: [],
      formatRules: [],
      templates: [],
      autoClaimRules: [],
      branding: { hasLogo: false, altText: null }
    },
    effective: {
      allowedPatronCodeIds: [],
      publicationOptions: [],
      commonCreators: [],
      externalSearchProviders: [],
      formats,
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: 'System alt',
      loginNote: note
    },
    workflow: {},
    ui_text: { loginNote: note },
    emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] },
    patronCodeChoices: [],
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-save-completion-'));
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

    let saved = false;
    let savedNote = 'Saved library note';
    let refreshFailureStatus = 0;
    let saveCount = 0;
    let committedCount = 0;
    const savingRefreshes = [];
    const committedMessages = [];
    const configurationCommits = [];
    let settingsRequests = 0;
    let formatRows = [];
    let formatDeleteCount = 0;
    const formatDeleteRequests = [];
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) {
        settingsRequests += 1;
        if (refreshFailureStatus) return response(refreshFailureStatus,
          { code: 'settings_unavailable', message: 'Refresh unavailable' });
        return response(200, saved
          ? settingsData(savedNote, saveCount === 1 ? 'after-save' : `after-save-${saveCount}`, formatRows)
          : settingsData('System login note', 'before-save'));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: [
        { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
        { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' }
      ] });
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        const submittedNote = JSON.parse(options.body).patron.loginNote;
        if (saveCount < 3) {
          assert.strictEqual(submittedNote,
            ['Saved library note', 'Saved again', 'Saved after 401'][saveCount]);
        }
        saveCount += 1;
        saved = true;
        if (submittedNote) savedNote = submittedNote;
        return response(200, { code: 'saved', data: {
          version: saveCount === 1 ? 'after-save' : `after-save-${saveCount}`,
          orgId: '2'
        } });
      }
      if (requestUrl.includes('/api/asap/staff/settings/formats/')) {
        assert.strictEqual(options.method, 'DELETE');
        const requestedId = decodeURIComponent(new URL(requestUrl, 'http://localhost').pathname.split('/').at(-1));
        formatDeleteRequests.push(requestedId);
        formatDeleteCount += 1;
        if (formatDeleteCount === 1) {
          return response(200, { code: 'format_deleted', data: { formatId: requestedId } });
        }
        if (formatDeleteCount === 2) {
          return response(200, { code: 'format_deleted', data: { formatId: '9007199254740993' } });
        }
        return response(200, { code: 'format_deleted', data: { formatId: requestedId } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'admin', organizationId: 2 }),
      onCommitted: message => {
        committedCount += 1;
        committedMessages.push(message);
      },
      onConfigurationCommitted: (owner, scope) => configurationCommits.push(scope),
      onRefreshed: () => {
        if (!document.getElementById('settings-form').inert) return;
        savingRefreshes.push({
          dirty: controller.isDirty(),
          title: document.getElementById('settings-save-title').textContent,
          attention: document.querySelector('.settings-save-bar').classList.contains('attention'),
          saveDisabled: document.getElementById('settings-save').disabled,
          discardHidden: document.getElementById('settings-discard').hidden,
          resetDisabled: document.getElementById('settings-reset').disabled
        });
      }
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

    const saveBar = document.querySelector('.settings-save-bar');
    const save = document.getElementById('settings-save');
    const discard = document.getElementById('settings-discard');
    const reset = document.getElementById('settings-reset');
    assert.strictEqual(saveBar.classList.contains('attention'), false);
    assert.strictEqual(save.disabled, true, document.getElementById('settings-message').textContent);
    assert.strictEqual(discard.hidden, true);
    assert.strictEqual(reset.hidden, false);
    assert.strictEqual(reset.disabled, false);
    const override = document.querySelector('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
    override.checked = true;
    override.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const note = document.getElementById('patron-login-note');
    note.value = 'Saved library note';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(saveBar.classList.contains('attention'), true);
    assert.strictEqual(save.disabled, false);
    assert.strictEqual(discard.hidden, false);
    assert.strictEqual(reset.hidden, false);
    assert.strictEqual(reset.disabled, false);
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));

    for (let attempt = 0; attempt < 20 && settingsRequests < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();

    assert.strictEqual(settingsRequests, 2, document.getElementById('settings-message').textContent);
    assert.strictEqual(document.getElementById('settings-version').value, 'after-save');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'No changes');
    assert.strictEqual(controller.isDirty(), false);
    assert.deepStrictEqual(savingRefreshes, [{
      dirty: false, title: 'No changes', attention: true,
      saveDisabled: true, discardHidden: true, resetDisabled: true
    }], 'an active save must retain attention after reload clears dirty and awaiting-reload state');
    assert.strictEqual(saveBar.classList.contains('attention'), false);
    assert.strictEqual(discard.hidden, true);
    assert.strictEqual(reset.hidden, false);
    assert.strictEqual(reset.disabled, false);

    note.value = 'Saved again';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    refreshFailureStatus = 503;
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveCount < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(committedCount, 2);
    assert.deepStrictEqual(configurationCommits, ['2', '2'], 'source commit invalidates configuration even when presentation refresh fails');
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.match(document.getElementById('settings-message').textContent, /Settings saved, but/);
    assert.strictEqual(controller.isDirty(), false);
    assert.strictEqual(saveBar.classList.contains('attention'), true,
      'awaiting reload must retain attention even after the committed save clears dirty state');
    assert.strictEqual(discard.hidden, true);
    assert.strictEqual(reset.hidden, false);
    assert.strictEqual(reset.disabled, true);

    refreshFailureStatus = 0;
    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(document.getElementById('settings-version').value, 'after-save-2');
    assert.strictEqual(saveBar.classList.contains('attention'), false);
    assert.strictEqual(save.disabled, true);
    assert.strictEqual(discard.hidden, true);
    assert.strictEqual(reset.disabled, false);
    note.value = 'Saved after 401';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    refreshFailureStatus = 401;
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveCount < 3; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(committedCount, 3);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.match(document.getElementById('settings-message').textContent, /Settings saved, but/);
    assert.strictEqual(saveBar.classList.contains('attention'), true);

    formatRows = [
      { id: '9007199254740991', version: 'format-v1', code: 'local_one', label: 'Local one', ownerOrganizationId: 2, overridden: false, isEnabled: true, customFields: {} },
      { id: '9007199254740992', version: 'format-v2', code: 'local_two', label: 'Local two', ownerOrganizationId: 2, overridden: false, isEnabled: true, customFields: {} },
      { id: '9007199254740994', version: 'format-v3', code: 'local_three', label: 'Local three', ownerOrganizationId: 2, overridden: false, isEnabled: true, customFields: {} }
    ];
    refreshFailureStatus = 0;
    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 &&
      document.querySelectorAll('.settings-format-row button[aria-label="Delete"]').length < 3; attempt++) await flush();
    assert.strictEqual(document.querySelectorAll('.settings-format-row button[aria-label="Delete"]').length, 3);
    for (const formatId of ['9007199254740991', '9007199254740992', '9007199254740994']) {
      const row = [...document.querySelectorAll('.settings-format-row')]
        .find(candidate => candidate.dataset.formatId === formatId);
      assert.ok(row, `format ${formatId} should remain available until its deletion is selected`);
      const deleteButton = row.querySelector('button[aria-label="Delete"]');
      assert.ok(deleteButton, `format ${formatId} should expose its current Delete control`);
      deleteButton.click();
    }
    assert.strictEqual(document.getElementById('settings-save').disabled, false);
    const settingsReadsBeforeFormatSubmit = settingsRequests;
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && formatDeleteCount < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(formatDeleteCount, 2, 'the malformed second acknowledgement stops the remaining deletion');
    assert.deepStrictEqual(formatDeleteRequests, ['9007199254740991', '9007199254740992'],
      'the format ID remains an exact decimal string above JavaScript safe integer range');
    assert.match(committedMessages.at(-2), /Format deletions confirmed: 0 of 3/);
    assert.match(committedMessages.at(-1), /Format deletions confirmed: 1 of 3/);
    assert.equal(configurationCommits.length, 5, 'Settings and each confirmed format deletion invalidate before a partial follow-up failure');
    assert.match(document.getElementById('settings-message').textContent,
      /Settings saved\. Format deletions confirmed: 1 of 3\. A follow-up action failed/);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    assert.strictEqual(saveBar.classList.contains('attention'), true);
    assert.strictEqual(document.getElementById('settings-form').inert, true,
      'the committed Settings save remains locked until current values are reloaded');
    assert.strictEqual(settingsRequests, settingsReadsBeforeFormatSubmit,
      'an invalid format acknowledgement does not trigger a success reload');
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 4; attempt++) await flush();
    assert.strictEqual(formatDeleteCount, 2, 'a malformed deletion acknowledgement cannot replay remaining deletes');
    dom.window.close();

    for (const kind of ['save', 'reset']) {
      for (const acknowledgementCase of [
        'missing-code', 'missing-data', 'missing-version', 'missing-orgId', 'wrong-orgId', 'numeric-orgId', 'canonical'
      ]) {
        const witnessDom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
          url: 'http://localhost/staff/'
        });
        global.window = witnessDom.window;
        global.document = witnessDom.window.document;
        global.FormData = witnessDom.window.FormData;
        global.Node = witnessDom.window.Node;
        global.Event = witnessDom.window.Event;

        let settingsReads = 0;
        let mutationRequest;
        let committed = 0;
        witnessDom.window.confirm = () => true;
        global.fetch = async (url, options = {}) => {
          const requestUrl = String(url);
          if (requestUrl.endsWith('/api/asap/staff/session')) {
            return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
          }
          if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) {
            settingsReads += 1;
            const loadedNote = kind === 'save' && mutationRequest
              ? mutationRequest.body.patron.loginNote
              : 'System login note';
            return response(200, settingsData(loadedNote, settingsReads === 1 ? 'settings-v1' : 'after-v2'));
          }
          if (requestUrl.endsWith('/api/asap/staff/organizations')) {
            return response(200, { code: 'ok', data: [
              { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
              { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' }
            ] });
          }
          if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
            return response(200, { code: 'ok', data: [] });
          }
          if ((kind === 'save' && requestUrl.endsWith('/api/asap/staff/settings')) ||
              (kind === 'reset' && requestUrl.includes('/api/asap/staff/settings/reset?'))) {
            mutationRequest = { path: requestUrl, options, body: JSON.parse(options.body) };
            const acknowledgement = {
              code: kind === 'save' ? 'saved' : 'reset',
              data: { version: 'after-v2', orgId: '2' }
            };
            if (acknowledgementCase === 'missing-code') delete acknowledgement.code;
            if (acknowledgementCase === 'missing-data') delete acknowledgement.data;
            if (acknowledgementCase === 'missing-version') delete acknowledgement.data.version;
            if (acknowledgementCase === 'missing-orgId') delete acknowledgement.data.orgId;
            if (acknowledgementCase === 'wrong-orgId') acknowledgement.data.orgId = '3';
            if (acknowledgementCase === 'numeric-orgId') acknowledgement.data.orgId = 2;
            return response(200, acknowledgement);
          }
          throw new Error(`Unexpected request: ${requestUrl}`);
        };

        const witnessController = settingsModule.createSettingsController({
          root: document.getElementById('settings-view'),
          tab: document.getElementById('settings-view-tab'),
          announce: () => {},
          getStaff: () => ({ role: 'admin', organizationId: 2 }),
          onCommitted: () => { committed += 1; }
        });
        witnessController.bind();
        witnessController.setStaff({
          id: '2', tenantId: 'tenant-1', objectId: 'object-2', role: 'admin', organizationId: 2
        });
        await witnessController.activate();
        assert.strictEqual(settingsReads, 1, `${kind} ${acknowledgementCase}: fixture loads its initial authoritative settings once`);
        const version = document.getElementById('settings-version');
        assert.strictEqual(version.value, 'settings-v1');

        if (kind === 'save') {
          const overrideToggle = document.querySelector(
            '[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
          overrideToggle.checked = true;
          overrideToggle.dispatchEvent(new witnessDom.window.Event('change', { bubbles: true }));
          const loginNote = document.getElementById('patron-login-note');
          loginNote.value = 'Unconfirmed saved note';
          loginNote.dispatchEvent(new witnessDom.window.Event('input', { bubbles: true }));
          document.getElementById('settings-form').dispatchEvent(new witnessDom.window.Event('submit', {
            bubbles: true, cancelable: true
          }));
        } else {
          document.getElementById('settings-reset').click();
        }

        for (let attempt = 0; attempt < 30; attempt++) await flush();
        assert.ok(mutationRequest, `${kind} ${acknowledgementCase}: mutation was sent`);
        assert.strictEqual(mutationRequest.options.method, 'POST');
        assert.strictEqual(mutationRequest.body.version, 'settings-v1');
        if (kind === 'save') {
          assert.strictEqual(mutationRequest.body.patron.loginNote, 'Unconfirmed saved note');
        } else {
          assert.deepStrictEqual(Object.keys(mutationRequest.body).sort(), ['version']);
        }
        if (acknowledgementCase === 'canonical') {
          for (let attempt = 0; attempt < 30 && settingsReads < 2; attempt++) await flush();
          assert.strictEqual(witnessController.hasUnconfirmedOutcome(), false,
            `${kind}: the exact captured library scope is accepted`);
          assert.strictEqual(witnessController.inspectDeparture().blocked, false);
          assert.strictEqual(committed, 1, `${kind}: the confirmed mutation is reported once`);
          assert.strictEqual(settingsReads, 2, `${kind}: canonical response refreshes authoritative settings`);
          assert.strictEqual(version.value, 'after-v2', `${kind}: canonical response advances after refresh`);
        } else {
          assert.strictEqual(witnessController.hasUnconfirmedOutcome(), true,
            `${kind} ${acknowledgementCase}: response without exact scope evidence remains unconfirmed`);
          assert.strictEqual(witnessController.inspectDeparture().blocked, true);
          assert.strictEqual(committed, 0, `${kind} ${acknowledgementCase}: invalid scope evidence is never reported committed`);
          assert.strictEqual(settingsReads, 1, `${kind} ${acknowledgementCase}: do not refresh as if success were authoritative`);
          assert.strictEqual(version.value, 'settings-v1', `${kind} ${acknowledgementCase}: do not advance the accepted version`);
          if (kind === 'save') {
            assert.strictEqual(document.getElementById('patron-login-note').value, 'Unconfirmed saved note');
            assert.strictEqual(witnessController.isDirty(), true, 'unconfirmed save retains the caller draft');
          }
        }
        witnessController.dispose();
        witnessDom.window.close();
      }
    }

    const retiredDom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = retiredDom.window;
    global.document = retiredDom.window.document;
    global.FormData = retiredDom.window.FormData;
    global.Node = retiredDom.window.Node;
    global.Event = retiredDom.window.Event;
    const retiredActor = {
      id: '2', tenantId: 'tenant-1', objectId: 'object-2', role: 'admin', organizationId: 2
    };
    const replacementActor = { ...retiredActor, id: '3', objectId: 'object-3' };
    const unconfirmed = [];
    let settingsReads = 0;
    let mutationPosts = 0;
    let rejectMutationJson;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) {
        settingsReads += 1;
        return response(200, settingsData('System login note', settingsReads === 1 ? 'actor-a-v1' : 'actor-b-v1'));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        return response(200, { code: 'ok', data: [
          { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
          { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' }
        ] });
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings') && options.method === 'POST') {
        mutationPosts += 1;
        return {
          ok: true,
          status: 200,
          statusText: 'OK',
          json: () => new Promise((resolve, reject) => { rejectMutationJson = reject; })
        };
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    let currentStaff = retiredActor;
    const retiredController = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => currentStaff,
      onCommitted: () => { throw new Error('An unconfirmed response cannot commit settings.'); },
      onUnconfirmed: (message, owner) => unconfirmed.push({ message, owner })
    });
    retiredController.bind();
    retiredController.setStaff(retiredActor);
    await retiredController.activate();
    const retiredOverride = document.querySelector(
      '[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
    retiredOverride.checked = true;
    retiredOverride.dispatchEvent(new retiredDom.window.Event('change', { bubbles: true }));
    const retiredNote = document.getElementById('patron-login-note');
    retiredNote.value = 'Actor A uncertain save';
    retiredNote.dispatchEvent(new retiredDom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new retiredDom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 30 && mutationPosts === 0; attempt++) await flush();
    assert.strictEqual(mutationPosts, 1, 'actor A has one actual settings mutation in flight');

    currentStaff = replacementActor;
    retiredController.setStaff(replacementActor);
    await retiredController.activate();
    assert.strictEqual(settingsReads, 2, 'replacement actor loads its own current settings');
    assert.strictEqual(document.getElementById('settings-version').value, 'actor-b-v1');
    rejectMutationJson(new SyntaxError('Response body was not JSON.'));
    for (let attempt = 0; attempt < 30; attempt++) await flush();

    assert.strictEqual(unconfirmed.length, 1, 'the status-200 malformed response remains an uncertain A-owned result');
    assert.strictEqual(unconfirmed[0].owner.id, retiredActor.id);
    assert.match(unconfirmed[0].message, /uncertain/i);
    assert.strictEqual(retiredController.hasUnconfirmedOutcome(), false,
      'late actor A evidence does not lock actor B settings');
    assert.strictEqual(retiredController.isDirty(), false);
    assert.strictEqual(document.getElementById('settings-version').value, 'actor-b-v1',
      'actor A response cannot replace actor B authoritative settings');
    assert.strictEqual(settingsReads, 2, 'uncertain actor A result is not treated as success and reloaded into actor B');
    assert.strictEqual(mutationPosts, 1, 'the uncertain actor A save is never replayed automatically');
    retiredController.dispose();
    retiredDom.window.close();
    console.log('Settings save completion refreshes the baseline and leaves the form clean');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
