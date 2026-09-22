const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');

(async () => {
  const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'settings', 'legacy-form-model.js');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-adapter-'));
  try {
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
    fs.copyFileSync(source, path.join(temporary, 'legacy-form-model.js'));
    const { buildLegacySettingsFormModel } = await import(pathToFileURL(path.join(temporary, 'legacy-form-model.js')).href);

    const providers = [
      { id: '11', key: 'external_search_1', isEnabled: true, label: 'One', urlTemplate: 'https://one/{{title}}', sortOrder: 13 },
      { id: '12', key: 'external_search_2', isEnabled: false, label: 'Two', urlTemplate: 'https://two/{{isbn}}', sortOrder: 27 },
      { id: '13', key: 'external_search_3', isEnabled: true, label: 'Three', urlTemplate: 'https://three/{{author}}', sortOrder: 49 }
    ];
    const formats = [{
      id: '101', code: 'book', ownerOrganizationId: '1', label: 'Configured book', sortOrder: 37,
      isEnabled: false, messageBehavior: 'message', message: 'Configured message',
      titleMode: 'required', titleLabel: 'Configured title', authorMode: 'hidden', authorLabel: 'Configured author',
      identifierMode: 'required', identifierLabel: 'Configured identifier', publicationMode: 'optional', publicationLabel: 'Configured publication'
    }];
    const system = buildLegacySettingsFormModel({
      stored: {
        workflow: { suggestionLimit: 17 },
        commonCreators: ['Creator Z', 'Creator A'],
        allowedPatronCodeIds: ['8', '13'],
        providers,
        formats,
        customFields: [],
        autoClaimRules: []
      },
      workflow: { suggestionLimit: 2 },
      effective: {
        externalSearchProviders: providers,
        formats: formats.map(format => ({
          ...format,
          title: { mode: format.titleMode, label: format.titleLabel },
          author: { mode: format.authorMode, label: format.authorLabel },
          identifier: { mode: format.identifierMode, label: format.identifierLabel },
          publication: { mode: format.publicationMode, label: format.publicationLabel },
          customFields: {}
        }))
      },
      autoClaimStaff: []
    }, 'system');
    assert.strictEqual(system.workflow.suggestionLimit, 17, 'stored system workflow must beat the incomplete alias');
    assert.strictEqual(system.workflow.commonAuthorsList, 'Creator Z\nCreator A');
    assert.strictEqual(system.workflow.allowedPatronCodeIds, '8,13');
    assert.strictEqual(system.providers.length, 3);
    assert.strictEqual(Object.hasOwn(system.workflow, 'externalSearch4Enabled'), false);
    assert.deepStrictEqual(system.uiText.formatOrder, ['book']);
    assert.deepStrictEqual(system.uiText.availableFormats, []);
    assert.strictEqual(system.uiText.formatRules.book.fields.author.mode, 'hidden');
    assert.strictEqual(system.uiText.formatRules.book.message, 'Configured message');

    const library = buildLegacySettingsFormModel({
      stored: {
        configuredSystem: {
          commonCreators: { exists: true, values: [{ value: 'System creator', sortOrder: 10 }] },
          allowedPatronCodeIds: { exists: true, values: ['1'] },
          publicationOptions: { exists: true, values: [{ id: 'system', label: 'System option', enabled: true, sortOrder: 10 }] }
        },
        libraryOverride: {
          commonCreators: { exists: true, values: [] },
          allowedPatronCodeIds: { exists: true, values: ['47'] },
          publicationOptions: { exists: true, values: [{ id: 'local', label: 'Local option', enabled: false, sortOrder: 33 }] }
        },
        workflow: { suggestionLimit: null },
        providers: providers.map(provider => ({ ...provider, overridden: provider.id === '12' })),
        formats,
        customFields: [{
          id: '401', key: 'audience_note', type: 'select', label: 'Audience note', helpText: 'Help', enabled: true,
          sortOrder: 31, options: [{ id: 'adult', label: 'Adult', enabled: false, sortOrder: 19 }]
        }],
        autoClaimRules: [{ materialFormatId: '101', staffUserId: '9007199254740993', active: true }]
      },
      effective: {
        workflow: { suggestionLimit: 23 },
        commonCreators: ['Incorrect fallback creator'],
        allowedPatronCodeIds: ['47'],
        externalSearchProviders: providers,
        formats: formats.map(format => ({ ...format, customFields: { audience_note: { mode: 'required' } } })),
        customFields: []
      },
      autoClaimStaff: [{ id: '9007199254740993', label: 'Large-ID Librarian' }]
    }, '2');
    assert.strictEqual(library.workflow.suggestionLimit, 23);
    assert.strictEqual(library.workflow.commonAuthorsList, 'System creator',
      'an empty library set is a reset marker in the backend contract and must resolve to the configured system set');
    assert.strictEqual(library.workflow.allowedPatronCodeIds, '47');
    assert.deepStrictEqual(library.uiText.publicationOptions,
      [{ id: 'local', label: 'Local option', enabled: false, sortOrder: 33 }]);
    assert.strictEqual(library.customFields[0].key, 'audience_note');
    assert.strictEqual(library.autoClaimRules[0].format, 'book');
    assert.strictEqual(library.autoClaimStaff[0].id, '9007199254740993');
    assert.strictEqual(library.autoClaimStaff[0].displayName, 'Large-ID Librarian');
    assert.strictEqual(library.autoClaimStaff[0].libraryOrgId, '2');

    const unavailable = buildLegacySettingsFormModel({ stored: {}, effective: {} }, '2');
    assert.strictEqual(unavailable.commonCreatorStateTrusted, false);
    assert.strictEqual(unavailable.patronCodeStateTrusted, false);
    assert.strictEqual(unavailable.publicationOptionStateTrusted, false);
    assert.strictEqual(unavailable.providerStateTrusted, false);
    assert.strictEqual(unavailable.formatStateTrusted, false);
    assert.strictEqual(unavailable.customFieldStateTrusted, false);
    assert.strictEqual(unavailable.autoClaimStateTrusted, false);

    console.log('Settings response adapter authoritative-source and partial-load checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
