const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-custom-field-clear-'));
const dom = new JSDOM('<div id="fields"></div>');

(async () => {
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    const { collectCustomFields, renderCustomFieldEditor } = await import(
      pathToFileURL(path.join(temporary, 'staff', 'js', 'custom-fields.js')));
    const request = {
      customFields: {
        audience: { label: 'Audience', type: 'text', value: 'Existing value' },
        retired: { label: 'Retired field', type: 'text', value: 'Keep this history' }
      }
    };
    const configuration = {
      additionalFieldDefinitions: [{ key: 'audience', label: 'Audience', type: 'text' }],
      formatRules: { book: { customFields: { audience: { mode: 'optional' } } } }
    };
    const container = document.getElementById('fields');
    const controls = renderCustomFieldEditor(container, request, configuration, 'book');
    controls.get('audience').input.value = '   ';

    const payload = collectCustomFields(request, controls);
    assert.equal(Object.hasOwn(payload, 'audience'), true,
      'an empty optional field needs an explicit payload member');
    assert.equal(payload.audience, null,
      'the request editor must distinguish clearing from omission');
    assert.deepEqual(payload.retired, request.customFields.retired,
      'valid unknown and retired snapshot values remain in the payload');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Staff optional custom-field clears retain explicit null and historical fields');
})().catch(error => { console.error(error); process.exitCode = 1; });
