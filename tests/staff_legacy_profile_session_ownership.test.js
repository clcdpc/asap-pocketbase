const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (body) => ({ ok: true, status: 200, statusText: 'OK', json: async () => body });
async function settle() {
  for (let count = 0; count < 8; count += 1) {
    await new Promise(resolve => setImmediate(resolve));
  }
}

(async () => {
  const frontendRoot = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-legacy-profile-owner-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  const originalSetTimeout = global.setTimeout;
  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.FormData = dom.window.FormData;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };

    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'staff/js/app/auth.js')).href);
    await import(pathToFileURL(path.join(temporary, 'staff/js/app/events.js')).href);
    let firstProfileResponse;
    let posts = 0;
    global.fetch = (url, options = {}) => {
      assert.equal(String(url), '/api/asap/staff/legacy/profile');
      assert.equal(options.method, 'POST');
      posts += 1;
      if (posts === 1) {
        return new Promise(resolve => { firstProfileResponse = resolve; });
      }
      return Promise.resolve(response({ staff: { ...state.staffSession.staff, displayName: 'Current profile' } }));
    };
    const staff = id => ({ id, role: 'admin', libraryOrgId: '2', version: `version-${id}` });
    state.setStaffSession({ authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token', staff: staff('27') });
    auth.openProfileDialog();
    document.getElementById('profile-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    await settle();
    assert.equal(posts, 1);
    state.setStaffSession({ authenticated: true, accessAllowed: true, antiforgeryToken: 'new-token', staff: staff('28') });
    auth.openProfileDialog();
    firstProfileResponse(response({ staff: { ...staff('27'), displayName: 'Old profile' } }));
    await settle();
    assert.equal(state.staffSession.staff.id, '28');
    assert.equal(document.getElementById('profile-dialog').open, true);
    assert.equal(document.getElementById('profile-save').disabled, false);
    assert.doesNotMatch(document.getElementById('profile-msg').textContent, /saved/i);

    const delayedClose = [];
    global.setTimeout = (callback, delay, ...args) => {
      if (delay === 700) {
        delayedClose.push(() => callback(...args));
        return 1;
      }
      return originalSetTimeout(callback, delay, ...args);
    };
    document.getElementById('profile-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    await settle();
    assert.equal(posts, 2);
    assert.equal(delayedClose.length, 1);
    assert.match(document.getElementById('profile-msg').textContent, /saved/i);
    auth.openProfileDialog();
    delayedClose[0]();
    assert.equal(document.getElementById('profile-dialog').open, true,
      'an earlier success timer cannot close a newly opened profile dialog');
    dom.window.close();
    console.log('Profile completion belongs to its original session and dialog generation');
  } finally {
    global.setTimeout = originalSetTimeout;
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
