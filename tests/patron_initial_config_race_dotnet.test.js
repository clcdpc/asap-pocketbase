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

function uiText(prefix) {
  return {
    pageTitle: prefix + ' Material Suggestion',
    publicationOptions: [prefix + ' publication'],
    formatRules: {
      book: {
        messageBehavior: 'none',
        fields: {
          title: { mode: 'required', label: prefix + ' Title' },
          author: { mode: 'required', label: 'Author' },
          identifier: { mode: 'optional', label: 'Identifier' },
          publication: { mode: 'required', label: 'Publication Timing' }
        }
      }
    }
  };
}

(async () => {
  const root = path.join(__dirname, '..');
  const source = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-config-race-'));
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
    let submittedPayload;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/config?')) {
        initialConfigRequested();
        return new Promise(resolve => {
          releaseInitialConfig = () => resolve(response(200, { ui_text: uiText('Old') }));
        });
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const data = JSON.parse(options.body);
        return response(200, {
          token: 'new-token',
          barcode: data.username,
          email: 'new@example.org',
          effectiveLibraryOrgId: 2,
          selectedPickupBranchId: 101,
          pickupBranches: [{ id: 101, label: 'New Library' }],
          ui_text: uiText('New')
        });
      }
      if (requestUrl.endsWith('/api/asap/patron/suggestions')) {
        submittedPayload = JSON.parse(options.body);
        return response(201, { ui_text: uiText('New'), successTitle: 'Submitted' });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    const bootstrap = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'bootstrap.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')).href);
    const submit = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'submit.js')).href);
    const initialization = bootstrap.initPatronApp();
    await initialConfigStarted;

    document.getElementById('barcode').value = 'B20000000000902';
    document.getElementById('pin').value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.title, 'New Material Suggestion');
    assert.deepStrictEqual(
      Array.from(document.getElementById('publication').options).map(option => option.value),
      ['New publication']);

    releaseInitialConfig();
    await initialization;
    assert.strictEqual(document.title, 'New Material Suggestion', 'late initial config must not replace the authenticated library config');
    assert.deepStrictEqual(
      Array.from(document.getElementById('publication').options).map(option => option.value),
      ['New publication']);

    document.getElementById('format').value = 'book';
    document.getElementById('title').value = 'New title';
    document.getElementById('author').value = 'New author';
    document.getElementById('publication').value = 'New publication';
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.strictEqual(submittedPayload.publication, 'New publication');
    assert.strictEqual(submittedPayload.format, 'book');

    console.log('Patron initial configuration race regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
