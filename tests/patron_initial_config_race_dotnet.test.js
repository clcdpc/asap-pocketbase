const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const { patronSession, patronUiText } = require('./helpers/patron-session-fixture');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 401 ? 'Unauthorized' : 'OK',
    json: async () => body
  };
}

function uiText(prefix, overrides = {}) {
  return patronUiText(prefix, overrides);
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
    let suggestionRequests = 0;
    let loginResponseOverride = null;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/config?')) {
        initialConfigRequested();
        return new Promise(resolve => { releaseInitialConfig = () => resolve(response(200, uiText('Old'))); });
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const data = JSON.parse(options.body);
        return response(200, loginResponseOverride || patronSession(data.username, 'new-token', { ui_text: uiText('New') }));
      }
      if (requestUrl.endsWith('/api/asap/patron/logout')) return response(204, null);
      if (requestUrl.endsWith('/api/asap/patron/suggestions')) {
        suggestionRequests++;
        submittedPayload = JSON.parse(options.body);
        return response(201, { id: '1', successTitle: 'Submitted', successMessage: 'B success updated.' });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    const bootstrap = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'bootstrap.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')).href);
    const submit = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'submit.js')).href);
    const configuration = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'config.js')).href);

    // Exercise a new page before bootstrap has loaded any server configuration.
    loginResponseOverride = patronSession('F20000000000901', 'fresh-empty-format-token', {
      effectiveLibraryOrgId: 3,
      ui_text: uiText('Fresh No Formats', { availableFormats: [] })
    });
    document.getElementById('barcode').value = 'F20000000000901';
    document.getElementById('pin').value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.title, 'Fresh No Formats Material Suggestion');
    assert.deepStrictEqual(Array.from(document.getElementById('format').options), [],
      'a fresh page clears its static format options when a complete login snapshot has no enabled formats');
    assert.strictEqual(document.getElementById('format').checkValidity(), false);
    const beforeFreshNoFormatSubmit = suggestionRequests;
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.strictEqual(suggestionRequests, beforeFreshNoFormatSubmit,
      'a fresh page with no enabled formats cannot dispatch a suggestion');
    assert.match(document.getElementById('submit-error').textContent, /format.*available/i);
    await auth.logout();
    loginResponseOverride = null;

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
    assert.strictEqual(document.getElementById('success-title').textContent, 'Submitted',
      'a partial submission success message remains an intentional configuration update');
    assert.strictEqual(document.title, 'New Material Suggestion',
      'a partial success update does not replace the authenticated library configuration');

    await auth.logout();
    loginResponseOverride = patronSession('C20000000000903', 'empty-format-token', {
      effectiveLibraryOrgId: 3,
      ui_text: uiText('No Formats', { availableFormats: [] })
    });
    document.getElementById('barcode').value = 'C20000000000903';
    document.getElementById('pin').value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.title, 'No Formats Material Suggestion');
    assert.deepStrictEqual(Array.from(document.getElementById('format').options), [],
      'a valid complete B snapshot with an empty availableFormats array clears prior options');
    assert.strictEqual(document.getElementById('format').checkValidity(), false,
      'an empty enabled-format list keeps the required format selection invalid');
    const beforeUnavailableFormatSubmit = suggestionRequests;
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.strictEqual(suggestionRequests, beforeUnavailableFormatSubmit,
      'an empty enabled-format list cannot dispatch a suggestion through the direct submit handler');
    assert.match(document.getElementById('submit-error').textContent, /format.*available/i,
      'the patron sees why submission is unavailable when there are no enabled formats');

    await auth.logout();
    async function assertEmptyPublicationSnapshot(mode, suffix) {
      const snapshot = uiText(`Empty Publication ${mode}`, {
        availableFormats: ['book'],
        publicationOptions: []
      });
      snapshot.formatRules.book.fields.publication.mode = mode;
      loginResponseOverride = patronSession(`P2000000000090${suffix}`, `empty-publication-${mode}-token`, {
        effectiveLibraryOrgId: 3,
        ui_text: snapshot
      });
      document.getElementById('barcode').value = `P2000000000090${suffix}`;
      document.getElementById('pin').value = '1234';
      await auth.handleLoginSubmit({ preventDefault() {} });

      const publication = document.getElementById('publication');
      assert.deepStrictEqual(Array.from(publication.options), [],
        `a complete ${mode} session snapshot must keep authoritative empty publication options empty`);
      assert.strictEqual(publication.required, mode === 'required');
      assert.strictEqual(publication.disabled, mode === 'hidden');
      assert.strictEqual(publication.checkValidity(), mode !== 'required',
        `an empty publication set must remain valid for a ${mode} publication field`);
      assert.strictEqual(
        document.getElementById('field-publication').classList.contains('hidden'),
        mode === 'hidden');

      if (mode !== 'required') {
        document.getElementById('format').value = 'book';
        document.getElementById('title').value = `Suggestion for ${mode}`;
        document.getElementById('author').value = 'Test author';
        const beforeSuggestion = suggestionRequests;
        await submit.handleSuggestionSubmit({ preventDefault() {} });
        assert.strictEqual(suggestionRequests, beforeSuggestion + 1,
          `an empty publication set must not block a ${mode} suggestion`);
        assert.ok(submittedPayload.publication === undefined || submittedPayload.publication === '',
          `a ${mode} empty publication field must serialize no selected value`);
      }

      await auth.logout();
    }

    await assertEmptyPublicationSnapshot('required', '4');
    await assertEmptyPublicationSnapshot('optional', '5');
    await assertEmptyPublicationSnapshot('hidden', '6');
    loginResponseOverride = null;
    assert.deepStrictEqual(configuration.normalizePublicationOptions(undefined),
      configuration.defaultUiText.publicationOptions,
      'an absent legacy publication property retains the established defaults');
    assert.deepStrictEqual(configuration.normalizePublicationOptions('Legacy A\r\nLegacy B'), ['Legacy A', 'Legacy B'],
      'the supported legacy newline representation remains readable');
    assert.deepStrictEqual(configuration.normalizePublicationOptions([
      { label: 'Disabled legacy choice', enabled: false },
      '   '
    ]), [], 'a supplied array cleaned to no enabled nonblank choices remains authoritatively empty');

    console.log('Patron initial configuration race regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
