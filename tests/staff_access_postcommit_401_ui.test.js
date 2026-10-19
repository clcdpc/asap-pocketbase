const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400, status, statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});

async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(predicate(), message);
}

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-staff-access-401-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/', pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));
    window.confirm = () => true;

    const emptySet = { exists: false, values: [] };
    const configuredSystem = {
      workflow: {}, patron: {}, email: {}, publicationOptions: emptySet,
      commonCreators: emptySet, allowedPatronCodeIds: emptySet,
      providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: null }
    };
    const settings = {
      orgId: 'system', version: 'settings-v1', organization: null,
      stored: {
        systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: {}, configuredSystem, libraryOverride: null,
        workflow: {}, patron: {}, email: {}, origins: [], publicationOptions: [],
        commonCreators: [], allowedPatronCodeIds: [], providers: [], formats: [],
        customFields: [], templates: [], autoClaimRules: [], branding: { hasLogo: false, altText: null }
      },
      effective: {
        allowedPatronCodeIds: [], publicationOptions: [], commonCreators: [],
        externalSearchProviders: [], formats: [], customFields: [], email: {}, logoAltText: null
      },
      workflow: {}, ui_text: {}, emails: { templates: [] }
    };
    const user = (id, name) => ({
      id, userPrincipalName: `${name.toLowerCase()}@example.org`, displayName: name,
      notificationEmail: `${name.toLowerCase()}@example.org`, role: 'super_admin',
      organizationId: 1, active: true, version: `user-${id}`
    });
    let revocation = false;
    let deletePosts = 0;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) {
        if (revocation) return response(401, { code: 'staff_session_invalid' });
        return response(200, {
          authenticated: true, accessAllowed: true, antiforgeryToken: 'staff-af',
          staff: {
            tenantId: 'test-tenant', id: '7', role: 'super_admin', organizationId: 1,
            organizationName: 'System', displayName: 'Ada', version: 'staff-v1',
            authenticationEmail: 'ada@example.org', defaultMineUnclaimedFilter: false
          }
        });
      }
      if (url.includes('/title-requests?')) {
        return response(200, { scope: 'all', organizations: [{ id: 2, name: 'Library Two' }], items: [] });
      }
      if (url.includes('/email-readiness')) return response(200, {});
      if (url.includes('/settings?orgId=')) return response(200, settings);
      if (url.endsWith('/api/asap/staff/organizations')) {
        return response(200, { code: 'ok', data: [
          { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-v1' },
          { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' }
        ] });
      }
      if (url.includes('/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
      if (url === '/api/asap/staff/users' && options.method !== 'POST') {
        return response(200, { canAssignSuperAdmin: true, users: [user('7', 'Ada'), user('8', 'Bea')] });
      }
      if (url.includes('/staff/audit?')) return response(200, { code: 'ok', data: [] });
      if (url === '/api/asap/staff/users/7' && options.method === 'DELETE') {
        deletePosts += 1;
        revocation = true;
        return response(200, {
          user: { ...user('7', 'Ada'), active: false },
          cleanup: { rulesDeactivated: 2, openTitleClaimsCleared: 3,
            openAdditionalCopyClaimsCleared: 4 }
        });
      }
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    document.getElementById('settings-view-tab').click();
    await until(() => !document.getElementById('settings-view').hidden, 'settings view must open');
    document.getElementById('settings-nav-staff').click();
    await until(() => [...document.querySelectorAll('.settings-staff-row')]
      .some(row => row.textContent.includes('Ada')), 'own staff row must load');
    const ownRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Ada'));
    [...ownRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();

    await until(() => document.getElementById('workspace').hidden,
      'post-deactivation 401 must hide the workspace');
    assert.equal(deletePosts, 1);
    const message = document.getElementById('signed-out-message').textContent;
    assert.match(message, /Staff user deactivated/);
    assert.match(message, /2 auto-claim rules deactivated/);
    assert.match(message, /3 open title claims cleared/);
    assert.match(message, /4 open additional-copy claims cleared/);
    assert.match(message, /Sign in again to review the current values/);
    console.log('Committed Staff Access mutation survives immediate refresh 401');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
