const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');

const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'patron', 'embed.js'), 'utf8');
const dom = new JSDOM(`
  <div data-asap-suggestions data-src="https://asap.example.org" data-library-org-id="2"></div>
  <div data-asap-suggestions data-src="https://asap.example.org" data-library-org-id="3"></div>
`, { url: 'https://embedding.example.org/page', runScripts: 'outside-only' });

try {
  dom.window.eval(source);
  const frames = [...dom.window.document.querySelectorAll('iframe[data-asap-patron-frame]')];
  assert.equal(frames.length, 2);
  assert.equal(frames[0].src, 'https://asap.example.org/patron/?libraryOrgId=2&embed=1');
  assert.equal(frames[1].src, 'https://asap.example.org/patron/?libraryOrgId=3&embed=1');

  function resize(sourceWindow, origin, height, type = 'asap:patron-resize') {
    dom.window.dispatchEvent(new dom.window.MessageEvent('message', {
      data: { type, height }, source: sourceWindow, origin
    }));
  }

  resize(frames[1].contentWindow, 'https://asap.example.org', 840);
  assert.equal(frames[0].style.height, '');
  assert.equal(frames[1].style.height, '840px');

  resize(frames[0].contentWindow, 'https://asap.example.org', 400);
  assert.equal(frames[0].style.height, '520px', 'The existing minimum-height clamp should still apply');
  assert.equal(frames[1].style.height, '840px');

  resize(frames[1].contentWindow, 'https://wrong.example.org', 1200);
  resize(dom.window, 'https://asap.example.org', 1200);
  resize(frames[1].contentWindow, 'https://asap.example.org', 1200, 'unrelated-message');
  assert.equal(frames[0].style.height, '520px');
  assert.equal(frames[1].style.height, '840px');

  resize(frames[1].contentWindow, 'https://asap.example.org', 5000);
  assert.equal(frames[1].style.height, '1800px', 'The existing maximum-height clamp should still apply');
} finally {
  dom.window.close();
}

console.log('Patron embed loader selects the sending frame and preserves resize bounds');
