const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({ ok: status < 400, status, json: async () => body });
async function settle() {
  for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve));
}

(async () => {
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-forbidden-recovery-'));
  fs.cpSync(path.join(source, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
  let dom;
  try {
    dom = new JSDOM(fs.readFileSync(path.join(source, 'staff', 'index.html'), 'utf8'), { url: 'https://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    const requests = [];
    let signOutStatus = 400;
    global.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: false, antiforgeryToken: 'recovery-af' });
      if (url.endsWith('/sign-out')) return response(signOutStatus, signOutStatus === 200 ? { signedOut: true } : { code: 'antiforgery_invalid' });
      if (url === '/scoped-denied') return response(403, { code: 'staff_scope_forbidden' });
      if (url === '/access-lost') return response(403, { code: 'staff_scope_forbidden', accessAllowed: false });
      throw new Error(`Unexpected request ${url}`);
    };
    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff/js/http.js')).href);
    await workflow.createWorkflowApp().start();
    assert.strictEqual(document.getElementById('workspace').hidden, true);
    assert.strictEqual(document.getElementById('session-actions').hidden, false);
    assert.match(document.getElementById('signed-out-message').textContent, /not currently available/);
    assert.ok(document.querySelector('a[href^="/api/asap/staff/sign-in"]'));
    assert.strictEqual(requests.length, 1, 'Forbidden session must not load business data');
    document.getElementById('sign-out').click();
    await settle();
    assert.strictEqual(requests.at(-1).options.headers['X-ASAP-Antiforgery'], 'recovery-af');
    assert.match(document.getElementById('signed-out-message').textContent, /did not complete/);
    assert.strictEqual(document.getElementById('session-actions').hidden, false);
    signOutStatus = 200;
    document.getElementById('sign-out').click();
    await settle();
    assert.match(document.getElementById('signed-out-message').textContent, /You are signed out/);
    assert.strictEqual(document.getElementById('session-actions').hidden, true);
    await assert.rejects(http.authorizedJson('/scoped-denied'), error => error.status === 403);
    assert.strictEqual(document.getElementById('session-actions').hidden, true, 'Ordinary scoped403 must not signal global access loss');
    await assert.rejects(http.authorizedJson('/access-lost'), error => error.status === 403);
    assert.strictEqual(document.getElementById('workspace').hidden, true);
    assert.strictEqual(document.getElementById('session-actions').hidden, false);
    console.log('Staff forbidden-cookie recovery UI and antiforgery transport checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
