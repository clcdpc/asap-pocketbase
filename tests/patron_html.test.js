const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const root = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-html-'));
  const dom = new JSDOM('', { url: 'https://localhost/patron/' });
  try {
    fs.mkdirSync(path.join(temporary, 'patron', 'js'), { recursive: true });
    fs.mkdirSync(path.join(temporary, 'shared'), { recursive: true });
    fs.copyFileSync(path.join(root, 'patron', 'js', 'html.js'), path.join(temporary, 'patron', 'js', 'html.js'));
    fs.copyFileSync(path.join(root, 'shared', 'html.js'), path.join(temporary, 'shared', 'html.js'));
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.document = dom.window.document;

    const patron = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'html.js')));
    const shared = await import(pathToFileURL(path.join(temporary, 'shared', 'html.js')));
    assert.equal(patron.renderPatronText, shared.renderPatronText, 'patron and staff share the live text renderer');
    const container = document.createElement('div');

    const plain = '<img src=x onerror="alert(1)"> & é\nOne line\n\nParagraph\nhttps://help.libbyapp.com/en-us/6260.htm\n(https://example.org/a?x=1&y=2). http://example.org/help! javascript:alert(1)';
    patron.renderPatronText(container, plain);
    assert.equal(container.textContent, plain, 'Every newline, special character and literal tag must survive.');
    assert.equal(container.querySelector('img, script, p, span'), null);
    assert.equal(container.querySelectorAll('a').length, 3);
    assert.equal(container.querySelectorAll('a')[1].getAttribute('href'), 'https://example.org/a?x=1&y=2');
    assert.equal(container.querySelectorAll('a')[2].getAttribute('href'), 'http://example.org/help');
    for (const link of container.querySelectorAll('a')) {
      assert.equal(link.target, '_blank'); assert.equal(link.rel, 'noopener noreferrer');
    }
    assert.equal(container.classList.contains('patron-message'), true);
    patron.renderPatronText(container, patron.applyPatronTextPlaceholders('Use {{barcode_label}}\nand {{pin_label}}.', { barcodeLabel: '<Card &>', pinLabel: 'Secret' }));
    assert.equal(container.textContent, 'Use <Card &>\nand Secret.');
    assert.equal(container.children.length, 0, 'Placeholder values cannot become markup.');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Patron and staff plain-text message rendering checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
