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
    statusText: status === 401 ? 'Unauthorized' : 'OK',
    json: async () => body
  };
}

(async () => {
  const root = path.join(__dirname, '..');
  const source = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-failed-login-config-'));
  try {
    fs.cpSync(path.join(source, 'patron'), path.join(temporary, 'patron'), { recursive: true });
    fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

    const html = fs.readFileSync(path.join(source, 'patron', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/patron/?libraryOrgId=2' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.localStorage = dom.window.localStorage;
    global.sessionStorage = dom.window.sessionStorage;
    global.FormData = dom.window.FormData;
    global.Option = dom.window.Option;
    global.Event = dom.window.Event;

    let releaseInitialConfig;
    let initialConfigRequested;
    const initialConfigStarted = new Promise(resolve => { initialConfigRequested = resolve; });
    global.fetch = async (url) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/config?')) {
        initialConfigRequested();
        return new Promise(resolve => {
          releaseInitialConfig = () => resolve(response(200, {
            ui_text: { pageTitle: 'Expected Library Login Title' }
          }));
        });
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        return response(401, { message: 'Incorrect Login - Please try again' });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    const bootstrap = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'bootstrap.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')).href);
    const initialization = bootstrap.initPatronApp();
    await initialConfigStarted;

    document.getElementById('barcode').value = 'B20000000000902';
    document.getElementById('pin').value = 'wrong';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.getElementById('login-error').textContent, 'Incorrect Login - Please try again');

    releaseInitialConfig();
    await initialization;
    assert.strictEqual(document.title, 'Expected Library Login Title');
    assert.strictEqual(document.getElementById('login-error').textContent, 'Incorrect Login - Please try again');
    assert.strictEqual(document.getElementById('login-error').classList.contains('hidden'), false);

    console.log('Patron failed-login initial configuration regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
