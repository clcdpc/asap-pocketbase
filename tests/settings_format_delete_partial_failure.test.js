const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');
const { projectLegacySettingsFixture } = require('./helpers/legacy-settings-fixture.cjs');

const response = (status, body) => ({
  ok: status >= 200 && status < 300,
  status,
  statusText: 'OK',
  json: async () => body
});

(async () => {
  const frontendRoot = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-format-partial-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Blob = dom.window.Blob;
    global.File = dom.window.File;
    global.FileReader = dom.window.FileReader;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };

    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const settings = await import(pathToFileURL(path.join(temporary, 'staff/js/settings.js')).href);
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-token',
      staff: { id: '27', role: 'admin', libraryOrgId: '2', libraryName: 'Library Two' }
    });
    const canonical = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'settings',
      'canonical-library-settings-response.json'), 'utf8'));
    const projected = projectLegacySettingsFixture(canonical);
    let settingsReads = 0;
    const posts = [];
    global.fetch = async (url, options = {}) => {
      const route = String(url);
      if (route.includes('/api/asap/staff/legacy/settings?orgId=2')) {
        settingsReads += 1;
        return response(200, { ...projected, version: settingsReads === 1 ? 'before-save' : 'after-save' });
      }
      if (route === '/api/asap/staff/legacy/settings' && options.method === 'POST') {
        posts.push(JSON.parse(options.body));
        return response(200, {
          code: 'partial', operationPhase: 'partial',
          data: {
            deletedFormats: ['removable'], failedFormat: 'referenced',
            failureCode: 'format_referenced', failureMessage: 'Referenced format is still in use.'
          }
        });
      }
      if (route === '/api/asap/staff/legacy/patron-codes') return response(200, []);
      if (route.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      throw new Error(`Unexpected request: ${route}`);
    };

    await settings.loadSettings();
    state.addDeletedSettingsFormat({ code: 'removable', version: 'original-first-version' });
    state.addDeletedSettingsFormat({ code: 'referenced', version: 'original-second-version' });
    const saved = await settings.saveSettings({ clearDelay: 0 });
    assert.equal(saved, false, 'a committed partial removal must not report full success');
    assert.equal(posts.length, 1, 'the browser sends one coordinated Settings command');
    assert.deepEqual(posts[0].deletedFormats, [
      { code: 'removable', version: 'original-first-version' },
      { code: 'referenced', version: 'original-second-version' }
    ]);
    assert.equal(settingsReads, 2, 'partial completion reloads the authoritative format list');
    assert.match(document.getElementById('settings-msg').textContent,
      /Settings were saved, but custom format removal did not complete.*Referenced format is still in use.*reloaded/i);
    assert.equal(state.deletedSettingsFormats.length, 0, 'the reloaded list replaces stale deletion intent');
    assert.equal(state.settingsReloadRequired, false);
    dom.window.close();
    console.log('Partial format removal preserves original versions, reloads, and reports the committed partial result');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
