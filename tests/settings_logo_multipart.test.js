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
    statusText: status === 409 ? 'Conflict' : 'OK',
    json: async () => body
  };
}

function settingsData(organizationId) {
  const emptySet = { exists: false, values: [] };
  const configuredSystem = {
    workflow: {},
    patron: {},
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats: [],
    templates: [],
    branding: { hasLogo: false, altText: 'System inherited alt' }
  };
  return {
    orgId: organizationId === 1 ? 'system' : String(organizationId),
    version: `library-${organizationId}-version`,
    organization: { id: organizationId, name: 'Library Two', abbreviation: 'TWO', active: true },
    stored: {
      systemSettings: { enabledLibraryOrgIds: [2, 3], libraryOrgIds: [2, 3] },
      polaris: {},
      configuredSystem,
      libraryOverride: organizationId === 1 ? null : {
        workflow: null,
        patron: null,
        email: null,
        publicationOptions: emptySet,
        commonCreators: emptySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: null }
      },
      workflow: {},
      patron: {},
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: [],
      allowedPatronCodeIds: [],
      providers: [],
      formats: [],
      customFields: [],
      templates: [],
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
      logoAltText: 'System inherited alt'
    },
    workflow: {},
    ui_text: {},
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-logo-'));
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
    global.Blob = dom.window.Blob;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    global.File = dom.window.File;
    window.confirm = () => true;

    const logoRequests = [];
    let settingsReads = 0;
    let actorALogoStarted;
    let releaseActorALogo;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
        settingsReads += 1;
        const requestedScope = new URL(requestUrl, 'http://localhost').searchParams.get('orgId');
        return response(200, settingsData(requestedScope === 'system' ? 1 : Number(requestedScope)));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        return response(200, { code: 'ok', data: [
          { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
          { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' },
          { id: 3, name: 'Library Three', abbreviation: 'THREE', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-3' }
        ] });
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.includes('/api/asap/staff/settings/logo')) {
        logoRequests.push({ url: requestUrl, body: options.body });
        if (logoRequests.length === 1) {
          return response(200, { code: 'branding_saved', data: {
            organizationId: 2, hasLogo: true, version: 'logo-version'
          } });
        }
        if (logoRequests.length === 2) {
          return response(200, { code: 'branding_saved', data: {
            organizationId: 2, hasLogo: true, version: 'clear-version'
          } });
        }
        if (logoRequests.length === 4) {
          return new Promise(resolve => {
            releaseActorALogo = () => resolve(response(200, { code: 'branding_saved', data: {
              organizationId: 2, hasLogo: true, version: 'late-actor-a-logo-version'
            } }));
            actorALogoStarted();
          });
        }
        return response(200, { code: 'saved', data: {
          organizationId: 2, hasLogo: true, version: 'wrong-kind-version'
        } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const commits = [];
    const unconfirmed = [];
    const initialStaff = {
      id: '1', tenantId: 'tenant-1', objectId: 'object-1', role: 'super_admin', organizationId: 1
    };
    let currentStaff = initialStaff;
    const controller = settingsModule.createSettingsController({
      prepareDeparture: () => ({ commit: () => true }),
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => currentStaff,
      onCommitted: (...args) => commits.push(args),
      onUnconfirmed: (...args) => unconfirmed.push(args)
    });
    controller.bind();
    controller.setStaff(initialStaff);
    await controller.activate();

    const scope = document.getElementById('settings-scope');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    for (let attempt = 0; attempt < 30 && scope.value !== '2'; attempt++) await flush();
    assert.strictEqual(scope.value, '2');
    assert.strictEqual(document.getElementById('settings-version').value, 'library-2-version',
      'the initial library scope is accepted only after its settings response settles');

    const alt = document.getElementById('branding-alt');
    const altOverride = alt.closest('[data-setting-section]').querySelector('.settings-override-toggle');
    assert.strictEqual(altOverride.checked, false);
    assert.strictEqual(alt.value, 'System inherited alt');

    const image = new dom.window.File([new Uint8Array([137, 80, 78, 71])], 'logo.png', { type: 'image/png' });
    Object.defineProperty(document.getElementById('branding-logo'), 'files', { value: [image], configurable: true });
    document.getElementById('save-branding-logo').click();
    await flush();
    await flush();

    assert.strictEqual(logoRequests.length, 1);
    assert.strictEqual(logoRequests[0].url, '/api/asap/staff/settings/logo?orgId=2');
    assert.strictEqual(logoRequests[0].body.get('version'), 'library-2-version');
    assert.strictEqual(logoRequests[0].body.get('orgId'), null);
    assert.strictEqual(logoRequests[0].body.get('logoAlt'), null);
    assert.strictEqual(logoRequests[0].body.get('logo').name, 'logo.png');

    document.getElementById('clear-branding-logo').click();
    await flush();
    await flush();

    assert.strictEqual(logoRequests.length, 2);
    assert.strictEqual(logoRequests[1].url, '/api/asap/staff/settings/logo?orgId=2');
    assert.strictEqual(logoRequests[1].body.get('version'), 'library-2-version');
    assert.strictEqual(logoRequests[1].body.get('clearLogo'), 'true');
    assert.strictEqual(logoRequests[1].body.get('logo'), null);
    assert.strictEqual(logoRequests[1].body.get('logoAlt'), null);
    assert.strictEqual(controller.hasUnconfirmedOutcome(), true,
      'a clear response claiming the logo still exists cannot confirm the clear');
    assert.strictEqual(commits.length, 1, 'the contradictory clear response is not a second committed receipt');
    assert.strictEqual(unconfirmed.length, 1, 'the contradictory clear response is reported for review');
    assert.match(document.getElementById('branding-status').textContent, /outcome is uncertain/i);
    document.getElementById('clear-branding-logo').click();
    await flush();
    assert.strictEqual(logoRequests.length, 2, 'an unconfirmed clear cannot be blindly repeated');

    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 && controller.hasUnconfirmedOutcome(); attempt++) await flush();
    assert.strictEqual(controller.hasUnconfirmedOutcome(), false,
      'only authoritative reload releases an unconfirmed logo operation');
    const retryImage = new dom.window.File([new Uint8Array([137, 80, 78, 71])], 'retry.png', { type: 'image/png' });
    Object.defineProperty(document.getElementById('branding-logo'), 'files', { value: [retryImage], configurable: true });
    document.getElementById('save-branding-logo').click();
    for (let attempt = 0; attempt < 20 && logoRequests.length < 3; attempt++) await flush();
    for (let attempt = 0; attempt < 4; attempt++) await flush();
    assert.strictEqual(logoRequests.length, 3);
    assert.strictEqual(controller.hasUnconfirmedOutcome(), true, 'a wrong-kind logo acknowledgement stays uncertain');
    assert.strictEqual(commits.length, 1, 'a wrong-kind acknowledgement is never counted as a saved logo');
    assert.strictEqual(unconfirmed.length, 2);
    assert.strictEqual(document.getElementById('branding-logo').files.length, 1,
      'the selected image remains available for deliberate review after an invalid acknowledgement');
    document.getElementById('save-branding-logo').click();
    await flush();
    assert.strictEqual(logoRequests.length, 3, 'an invalid logo acknowledgement cannot be blindly retried');

    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 && controller.hasUnconfirmedOutcome(); attempt++) await flush();
    assert.strictEqual(controller.hasUnconfirmedOutcome(), false,
      'authoritative reload releases the invalid acknowledgement before changing the actor');

    const actorA = {
      id: '2', tenantId: 'tenant-1', objectId: 'object-2', role: 'admin', organizationId: 2
    };
    currentStaff = actorA;
    controller.setStaff(actorA);
    await controller.activate();
    assert.strictEqual(document.getElementById('settings-version').value, 'library-2-version');
    const actorALogoStartedPromise = new Promise(resolve => { actorALogoStarted = resolve; });
    const actorAImage = new dom.window.File([new Uint8Array([137, 80, 78, 71])], 'actor-a.png', { type: 'image/png' });
    Object.defineProperty(document.getElementById('branding-logo'), 'files', { value: [actorAImage], configurable: true });
    document.getElementById('save-branding-logo').click();
    await actorALogoStartedPromise;
    assert.strictEqual(logoRequests.length, 4, 'actor A has one valid logo mutation pending');

    const actorB = {
      id: '3', tenantId: 'tenant-1', objectId: 'object-3', role: 'admin', organizationId: 3
    };
    currentStaff = actorB;
    controller.setStaff(actorB);
    await controller.activate();
    const actorBSettingsVersion = document.getElementById('settings-version').value;
    assert.strictEqual(actorBSettingsVersion, 'library-3-version');
    const settingsReadsBeforeActorAResponse = settingsReads;
    releaseActorALogo();
    for (let attempt = 0; attempt < 12; attempt++) await flush();
    assert.strictEqual(commits.length, 2, 'a valid actor A response is retained as a committed receipt after the actor changes');
    assert.strictEqual(commits[1][1].id, actorA.id, 'the late receipt remains owned by actor A');
    assert.strictEqual(commits[1][2].context.scope, '2', 'the late receipt retains actor A library scope');
    assert.strictEqual(document.getElementById('settings-scope').value, '3', 'the late response cannot change actor B scope');
    assert.strictEqual(document.getElementById('settings-version').value, actorBSettingsVersion,
      'the late response cannot replace actor B settings');
    assert.strictEqual(settingsReads, settingsReadsBeforeActorAResponse,
      'the late response cannot start an actor A refresh in actor B scope');
    assert.strictEqual(logoRequests.length, 4, 'the accepted late response does not replay the logo mutation');
    assert.strictEqual(unconfirmed.length, 2, 'a valid actor A response is not misreported as uncertain');
    assert.strictEqual(controller.hasUnconfirmedOutcome(), false, 'actor A success does not mark actor B uncertain');

    dom.window.close();
    console.log('Settings logo multipart scope and independent-alt checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
