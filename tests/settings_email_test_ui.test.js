const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({
  ok: status >= 200 && status < 300,
  status,
  statusText: 'OK',
  json: async () => body
});

function settingsData() {
  const emptySet = { exists: false, values: [] };
  const email = { fromAddress: 'system@example.org', fromName: 'System', hasPostmarkToken: true };
  const configuredSystem = {
    workflow: {}, patron: {}, email,
    publicationOptions: emptySet, commonCreators: emptySet, allowedPatronCodeIds: emptySet,
    providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: null }
  };
  return {
    orgId: '2', version: 'settings-version',
    organization: { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true, version: 'organization-version' },
    stored: {
      systemSettings: {}, polaris: {}, configuredSystem,
      libraryOverride: { workflow: null, patron: null, email: null, publicationOptions: emptySet,
        commonCreators: emptySet, allowedPatronCodeIds: emptySet, providers: [], formats: [],
        templates: [], branding: { hasLogo: false, altText: null } },
      workflow: {}, patron: {}, email, origins: [], publicationOptions: [], commonCreators: [],
      allowedPatronCodeIds: [], providers: [], formats: [], customFields: [], templates: [],
      autoClaimRules: [], branding: { hasLogo: false, altText: null }
    },
    effective: { allowedPatronCodeIds: [], publicationOptions: [], commonCreators: [],
      externalSearchProviders: [], formats: [], customFields: [], email, logoAltText: null },
    workflow: {}, ui_text: {}, emails: { ...email, templates: [] },
    patronCodeChoices: []
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-email-test-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
    url: 'https://localhost/staff/'
  });
  global.window = dom.window;
  global.document = dom.window.document;
  global.FormData = dom.window.FormData;
  global.Node = dom.window.Node;
  global.Event = dom.window.Event;
  global.URLSearchParams = dom.window.URLSearchParams;

  const requests = [];
  try {
    global.fetch = async (url, options = {}) => {
      requests.push({ url: String(url), options });
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) return response(200, settingsData());
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, []);
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.includes('/email-operations/test-context')) return response(200, {
        code: 'ok',
        data: {
          organizationId: 2, organizationName: 'Library Two', fromAddress: 'system@example.org',
          fromName: 'System', recipientAddress: 'admin@example.org', deliveryMode: 'live',
          isConfigured: true, canSend: true, readinessCode: null, blockingReason: null
        }
      });
      if (requestUrl.endsWith('/email-operations/test?organizationId=2')) return response(202, {
        code: 'queued', data: { id: 91, status: 'pending', deliveryMode: 'live' }
      });
      if (requestUrl.includes('/email-operations/91?organizationId=2')) return response(200, {
        code: 'ok', data: {
          id: 91, organizationId: 2, status: 'sent', deliveryClass: 'operational_test',
          attemptCount: 1, lastErrorCode: null, suppressionReason: null,
          createdUtc: '2026-09-18T12:00:00Z', sentUtc: '2026-09-18T12:00:01Z',
          version: 'status-version', deliveryMode: 'live', providerMessageId: 'pm-91'
        }
      });
      throw new Error(`Unexpected request ${requestUrl}`);
    };

    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ id: '7', role: 'admin', organizationId: 2, notificationEmail: 'admin@example.org' })
    });
    controller.bind();
    controller.setStaff({ id: '7', role: 'admin', organizationId: 2, notificationEmail: 'admin@example.org' });
    await controller.activate();
    assert.equal(document.getElementById('email-postmark-token').value, '', 'saved token remains write-only');
    assert.equal(document.getElementById('settings-send-test-email').disabled, false);
    assert.match(document.getElementById('settings-email-test-context').textContent, /Live Postmark/);

    document.getElementById('settings-send-test-email').click();
    for (let attempt = 0; attempt < 20 && !document.getElementById('settings-email-test-status').textContent.includes('accepted'); attempt++) await flush();
    const request = requests.find(item => item.url.endsWith('/email-operations/test?organizationId=2'));
    assert.ok(request, 'settings action should queue the scoped test operation');
    const body = JSON.parse(request.options.body);
    assert.match(body.requestId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
    assert.match(document.getElementById('settings-email-test-status').textContent, /accepted/);

    const from = document.getElementById('email-from-address');
    from.value = 'draft@example.org';
    from.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(document.getElementById('settings-send-test-email').disabled, true, 'dirty settings disable the action');
    console.log('Settings real-email action respects saved scope, write-only credentials, polling, and dirty-state guards');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
