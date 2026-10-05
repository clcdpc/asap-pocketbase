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
    global.DOMParser = dom.window.DOMParser;

    const patron = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'html.js')));
    const shared = await import(pathToFileURL(path.join(temporary, 'shared', 'html.js')));
    const html = '<p>Library collection</p><a href="https://example.org/help">Learn more</a><script>window.patronHtmlScriptRan = true</script><a href="javascript:alert(1)">Unsafe link</a><span onclick="alert(1)">Plain text</span>';
    const sanitized = patron.sanitizeHtml(html);
    assert.equal(sanitized, shared.sanitizeHtml(html), 'patron and staff must share the sanitizer');
    const fragment = shared.sanitizedHtmlFragment(html);
    const container = document.createElement('div');
    container.append(fragment);
    assert.equal(container.querySelector('p').textContent, 'Library collection');
    assert.equal(container.querySelector('a').getAttribute('href'), 'https://example.org/help');
    assert.equal(container.querySelector('script'), null);
    assert.equal(dom.window.patronHtmlScriptRan, undefined);
    assert.equal(container.querySelectorAll('a')[1].hasAttribute('href'), false);
    assert.equal(container.querySelector('span').hasAttribute('onclick'), false);
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Patron and staff configured HTML sanitizer checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
