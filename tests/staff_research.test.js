const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-research-'));
  const dom = new JSDOM('<section id="links"></section>', { url: 'https://localhost/staff/' });
  try {
    fs.copyFileSync(path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'research.js'),
      path.join(temporary, 'research.js'));
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.document = dom.window.document;
    const { researchUrl, renderResearchLinks } = await import(pathToFileURL(path.join(temporary, 'research.js')).href);
    assert.equal(researchUrl('https://leap.example.test/bib/{{bibid}}', { bibid: '90/01' }, 'bibid'),
      'https://leap.example.test/bib/90%2F01');
    assert.equal(researchUrl('javascript:alert(1)', { bibid: '9001' }), '');
    assert.equal(researchUrl('https://search.example.test/?q={{author}}', { author: 'Someone' }), '');
    assert.equal(researchUrl('https://leap.example.test/bib/', { bibid: '9001' }, 'bibid'), '');

    const links = document.querySelector('#links');
    const request = { title: 'A & B / C', identifier: '978 1', bibid: '9001' };
    renderResearchLinks(links, request, {
      leapBibUrlPattern: 'https://leap.example.test/bib/{{bibid}}',
      leapPatronUrlPattern: 'https://leap.example.test/patron/{{patronId}}',
      patronId: 7001,
      externalSearchProviders: [
        { sortOrder: 30, label: 'Identifier source', urlTemplate: 'https://search.example.test/?id={{identifier}}' },
        { sortOrder: 10, label: 'Title source', urlTemplate: 'https://search.example.test/?q={{title}}' },
        { sortOrder: 20, label: 'Disabled source', isEnabled: false,
          urlTemplate: 'https://search.example.test/?q={{title}}' },
        { sortOrder: 40, label: 'Unsupported source', urlTemplate: 'https://search.example.test/?q={{author}}' }
      ]
    });
    assert.deepEqual([...links.querySelectorAll('a')].map(link => link.textContent),
      ['Open BIB in LEAP', 'Open patron in LEAP', 'Title source', 'Identifier source']);
    assert.equal(links.querySelectorAll('a')[2].href, 'https://search.example.test/?q=A%20%26%20B%20%2F%20C');
    assert.equal(links.querySelectorAll('a')[3].href, 'https://search.example.test/?id=978%201');

    renderResearchLinks(links, { ...request, bibid: 'not-a-bib' }, {
      leapBibUrlPattern: 'https://leap.example.test/bib/{{bibid}}', externalSearchProviders: []
    });
    assert.equal(links.querySelectorAll('a').length, 0);

    renderResearchLinks(links, request, { externalSearchProviders: [] });
    assert.equal(links.hidden, true);
    assert.equal(links.querySelectorAll('a').length, 0);
    console.log('Staff research link contracts passed');
  } finally {
    dom.window.close();
    delete global.document;
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
